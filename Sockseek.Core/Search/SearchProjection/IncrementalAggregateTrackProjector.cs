using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public sealed record SearchViewProjectedAggregateTrackGroup(
    int Index,
    SongQuery Query,
    int ShareCount,
    long SelectableOptionCount,
    ProjectedFileCandidate Representative,
    IReadOnlyList<ProjectedFileCandidate> NewOptions);

public sealed record AggregateTrackSearchViewChanges(
    IReadOnlyList<SearchViewProjectedAggregateTrackGroup> ChangedGroups,
    IReadOnlyList<ProjectedFileCandidate> AdmittedFiles);

public sealed class IncrementalAggregateTrackProjector
{
    private readonly SongQuery query;
    private readonly SearchSettings search;
    private readonly ConcurrentDictionary<string, int> userSuccessCounts;
    private readonly SongQueryComparer comparer;
    private readonly Dictionary<SongQuery, AggregateTrackBucket> buckets;
    private readonly List<AggregateTrackBucket> bucketOrder = [];
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)> seen = [];

    public IncrementalAggregateTrackProjector(
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null)
    {
        this.query = query;
        this.search = search;
        this.userSuccessCounts = userSuccessCounts ?? new ConcurrentDictionary<string, int>();
        comparer = new SongQueryComparer(ignoreCase: true, search.AggregateLengthTol);
        buckets = new Dictionary<SongQuery, AggregateTrackBucket>(comparer);
    }

    public int Count => seen.Count;

    public void Clear()
    {
        buckets.Clear();
        bucketOrder.Clear();
        seen.Clear();
    }

    internal int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
        => AddRange(results.Select((result, index) => SearchProjectionInput.FromLive(
            index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)));

    public int AddRange(IEnumerable<SearchProjectionInput> results)
    {
        int added = 0;
        foreach (var input in results)
        {
            var seenKey = (
                new PeerPathKey(input.Username, input.Filename),
                input.Visibility);
            if (!seen.Add(seenKey))
                continue;

            if (!SearchResultProjector.AggregateTrackProjectionIncludes(input, query, search))
                continue;

            var inferred = Searcher.InferSongQuery(input.Filename, query);
            var bucketKey = new SongQuery(inferred) { Length = input.Length ?? -1 };

            if (!buckets.TryGetValue(bucketKey, out var bucket))
            {
                bucket = new AggregateTrackBucket(
                    bucketOrder.Count,
                    bucketKey,
                    search,
                    userSuccessCounts);
                buckets.Add(bucketKey, bucket);
                bucketOrder.Add(bucket);
            }

            bucket.Add(input);
            added++;
        }

        return added;
    }

    public AggregateTrackSearchViewChanges AddRangeForSearchView(
        IEnumerable<SearchProjectionInput> results)
    {
        var admitted = new List<ProjectedFileCandidate>();
        var touched = new Dictionary<int, (
            AggregateTrackBucket Bucket,
            bool WasVisible,
            List<ProjectedFileCandidate> Added)>();
        foreach (SearchProjectionInput input in results)
        {
            var seenKey = (
                new PeerPathKey(input.Username, input.Filename),
                input.Visibility);
            if (!seen.Add(seenKey)
                || !SearchResultProjector.AggregateTrackProjectionIncludes(
                    input,
                    query,
                    search))
            {
                continue;
            }

            SongQuery inferred = Searcher.InferSongQuery(input.Filename, query);
            var bucketKey = new SongQuery(inferred) { Length = input.Length ?? -1 };
            if (!buckets.TryGetValue(bucketKey, out AggregateTrackBucket? bucket))
            {
                bucket = new AggregateTrackBucket(
                    bucketOrder.Count,
                    bucketKey,
                    search,
                    userSuccessCounts);
                buckets.Add(bucketKey, bucket);
                bucketOrder.Add(bucket);
            }
            bool wasVisible = IsVisible(bucket);
            ProjectedFileCandidate projected = bucket.Add(input);
            admitted.Add(projected);
            if (!touched.TryGetValue(bucket.Index, out var change))
            {
                change = (bucket, wasVisible, []);
                touched.Add(bucket.Index, change);
            }
            change.Added.Add(projected);
        }

        var changed = new List<SearchViewProjectedAggregateTrackGroup>();
        foreach (var change in touched.Values.OrderBy(value => value.Bucket.Index))
        {
            AggregateTrackBucket bucket = change.Bucket;
            if (!IsVisible(bucket))
                continue;
            IReadOnlyList<ProjectedFileCandidate> ordered = bucket.SortedProjected();
            changed.Add(new(
                bucket.Index,
                bucket.QueryWithKnownLength(),
                bucket.ShareCount,
                ordered.LongCount(option =>
                    option.Input.Visibility == SearchResultVisibility.Public),
                ordered[0],
                change.WasVisible ? change.Added : ordered));
        }
        return new(changed, admitted);
    }

    public List<SongJob> Snapshot()
        => bucketOrder
            .Where(x => x.ShareCount >= search.MinSharesAggregate)
            .Where(PassesStrictFilter)
            .OrderByDescending(x => x.ShareCount)
            .ThenBy(x => x.Index)
            .Select(x =>
            {
                var song = new SongJob(x.QueryWithKnownLength());
                song.Candidates = x.SortedCandidates();
                return song;
            })
            .ToList();

    public List<SearchViewProjectedAggregateTrackGroup> SnapshotForSearchView()
        => bucketOrder
            .Where(IsVisible)
            .Select(bucket =>
            {
                IReadOnlyList<ProjectedFileCandidate> ordered = bucket.SortedProjected();
                return new SearchViewProjectedAggregateTrackGroup(
                    bucket.Index,
                    bucket.QueryWithKnownLength(),
                    bucket.ShareCount,
                    ordered.LongCount(option =>
                        option.Input.Visibility == SearchResultVisibility.Public),
                    ordered[0],
                    ordered);
            })
            .ToList();

    private bool PassesStrictFilter(AggregateTrackBucket bucket)
    {
        if (search.Relax)
            return true;

        var bucketQuery = bucket.Query;
        return FileConditions.StrictString(bucketQuery.Title, query.Title, ignoreCase: true)
            && (FileConditions.StrictString(bucketQuery.Artist, query.Artist, ignoreCase: true, boundarySkipWs: false)
                || FileConditions.StrictString(bucketQuery.Title, query.Artist, ignoreCase: true, boundarySkipWs: false)
                    && bucketQuery.Title.ContainsInBrackets(query.Artist, ignoreCase: true));
    }

    private bool IsVisible(AggregateTrackBucket bucket)
        => bucket.ShareCount >= search.MinSharesAggregate
            && PassesStrictFilter(bucket);

    private sealed class AggregateTrackBucket
    {
        private readonly IncrementalResultSorter sorter;
        private readonly List<SearchProjectionInput> candidates = [];
        private readonly HashSet<string> users = new(StringComparer.Ordinal);

        public int Index { get; }
        public SongQuery Query { get; }
        public int ShareCount => users.Count;

        public AggregateTrackBucket(
            int index,
            SongQuery query,
            SearchSettings search,
            ConcurrentDictionary<string, int> userSuccessCounts)
        {
            Index = index;
            Query = query;
            sorter = new IncrementalResultSorter(
                query,
                search,
                userSuccessCounts,
                albumMode: false,
                ignoreStringSortConditions: true,
                necessaryConditionEvaluator: static _ => true);
        }

        public ProjectedFileCandidate Add(SearchProjectionInput input)
        {
            candidates.Add(input);
            users.Add(input.Username);
            return sorter.AddRangeAndGetProjected([input]).Single();
        }

        public SongQuery QueryWithKnownLength()
        {
            if (Query.Length != -1)
                return Query;

            int length = candidates.FirstOrDefault(x => x.Length != null)?.Length ?? -1;
            return new SongQuery(Query) { Length = length };
        }

        public List<FileCandidate> SortedCandidates()
            => sorter.SnapshotInputs()
                .Select(input => input.ToFileCandidate())
                .ToList();

        public List<ProjectedFileCandidate> SortedProjected()
            => sorter.SnapshotProjectedFiles();
    }
}
