using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public sealed class IncrementalAggregateTrackProjector
{
    private readonly SongQuery query;
    private readonly SearchSettings search;
    private readonly ConcurrentDictionary<string, int> userSuccessCounts;
    private readonly SongQueryComparer comparer;
    private readonly Dictionary<SongQuery, AggregateTrackBucket> buckets;
    private readonly List<AggregateTrackBucket> bucketOrder = [];
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

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
            string seenKey = input.Username + '\\' + input.Filename;
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
                ignoreStringSortConditions: true);
        }

        public void Add(SearchProjectionInput input)
        {
            candidates.Add(input);
            users.Add(input.Username);
            sorter.AddRange([input]);
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
    }
}
