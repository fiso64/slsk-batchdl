using System.Collections.Concurrent;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Sldl.Core.Jobs;

public enum SearchIntent
{
    Track,
    Album,
}

public class SearchJob : Job
{
    private readonly object _projectionCacheLock = new();
    private readonly Dictionary<string, (int Revision, bool IsComplete, object Value)> _projectionCache = new();

    public SearchIntent Intent { get; }
    public SongQuery Query { get; }
    public AlbumQuery? AlbumQuery { get; }
    public bool IncludeFullResults { get; }

    public SearchSession Session { get; } = new();

    public int ResultCount => Session.Results.Count;
    public int Revision => Session.Revision;
    public bool IsComplete => Session.IsComplete;

    public override SongQuery QueryTrack => Query;
    protected override bool DefaultCanBeSkipped => false;

    public SearchJob(SongQuery query, bool includeFullResults = false)
    {
        Intent = SearchIntent.Track;
        Query = query;
        IncludeFullResults = includeFullResults;
    }

    public SearchJob(AlbumQuery query)
    {
        Intent = SearchIntent.Album;
        AlbumQuery = query;
        Query = SearchResultProjector.AlbumBridgeQuery(query);
    }

    public IReadOnlyCollection<(Soulseek.SearchResponse Response, Soulseek.File File)> Snapshot()
        => Session.Snapshot();

    public IReadOnlyList<SearchRawResult> RawSnapshot(long afterSequence = 0)
        => Session.RawSnapshot(afterSequence);

    public IAsyncEnumerable<SearchRawResult> ReadRawResultsAsync(
        long afterSequence = 0,
        CancellationToken ct = default)
        => Session.ReadRawResultsAsync(afterSequence, ct);

    public T GetOrCreateProjection<T>(
        string key,
        Func<IReadOnlyCollection<(Soulseek.SearchResponse Response, Soulseek.File File)>, T> factory)
    {
        int revision = Revision;
        bool isComplete = IsComplete;
        lock (_projectionCacheLock)
        {
            if (_projectionCache.TryGetValue(key, out var cached)
                && cached.Revision == revision
                && cached.IsComplete == isComplete
                && cached.Value is T value)
            {
                return value;
            }

            var created = factory(Snapshot());
            _projectionCache[key] = (revision, isComplete, created!);
            return created;
        }
    }

    public SearchProjectionSnapshot<FileCandidate> GetSortedTrackCandidates(
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = true,
        bool useLevenshtein = true)
        => GetProjection(
            $"sorted-track:{useInfer}:{useLevenshtein}",
            snapshot => SearchResultProjector.SortedTrackCandidates(
                snapshot.Select(x => (x.Response, x.File)),
                Query,
                search,
                userSuccessCounts,
                useInfer,
                useLevenshtein));

    public SearchProjectionSnapshot<AlbumFolder> GetAlbumFolders(SearchSettings search)
    {
        if (AlbumQuery == null)
            throw new InvalidOperationException("Album folder projection requires an album search job.");

        return GetProjection(
            "album-folders",
            snapshot => SearchResultProjector.AlbumFolders(
                snapshot.Select(x => (x.Response, x.File)),
                AlbumQuery,
                search));
    }

    public SearchProjectionSnapshot<SongJob> GetAggregateTracks(
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
        => GetProjection(
            "aggregate-tracks",
            snapshot => SearchResultProjector.AggregateTracks(
                snapshot.Select(x => (x.Response, x.File)),
                Query,
                search,
                userSuccessCounts));

    public SearchProjectionSnapshot<AlbumJob> GetAggregateAlbums(SearchSettings search)
    {
        if (AlbumQuery == null)
            throw new InvalidOperationException("Album aggregate projection requires an album search job.");

        return GetProjection(
            "aggregate-albums",
            snapshot =>
            {
                var folders = SearchResultProjector.AlbumFolders(
                    snapshot.Select(x => (x.Response, x.File)),
                    AlbumQuery,
                    search);
                return SearchResultProjector.AggregateAlbums(folders, AlbumQuery, search);
            });
    }

    private SearchProjectionSnapshot<T> GetProjection<T>(
        string key,
        Func<IReadOnlyCollection<(Soulseek.SearchResponse Response, Soulseek.File File)>, List<T>> factory)
    {
        int revision = Revision;
        bool isComplete = IsComplete;
        lock (_projectionCacheLock)
        {
            if (_projectionCache.TryGetValue(key, out var cached)
                && cached.Revision == revision
                && cached.IsComplete == isComplete
                && cached.Value is SearchProjectionSnapshot<T> value)
            {
                return value;
            }

            var created = new SearchProjectionSnapshot<T>(revision, factory(Snapshot()), isComplete);
            _projectionCache[key] = (revision, isComplete, created);
            return created;
        }
    }

    public override string ToString(bool noInfo)
        => AlbumQuery?.ToString(noInfo) ?? Query.ToString(noInfo);
}
