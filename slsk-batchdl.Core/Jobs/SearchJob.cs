using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    private readonly Lock _projectionCacheLock = new();
    private readonly Dictionary<string, (int Revision, bool IsComplete, object Value)> _projectionCache = [];
    private readonly Dictionary<string, object> _incrementalProjectionStates = [];

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

    // Track searches use Query directly. Album searches keep the original AlbumQuery and expose
    // two explicit SongQuery projections so it is clear which one drives network search terms
    // versus filename-level matching/sorting.
    public SongQuery NetworkQuery
        => Intent == SearchIntent.Album
            ? SearchResultProjector.AlbumNetworkQuery(AlbumQuery!)
            : Query;

    public SongQuery FileMatchQuery
        => Intent == SearchIntent.Album
            ? SearchResultProjector.AlbumFileMatchQuery(AlbumQuery!)
            : Query;

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
        Query = SearchResultProjector.AlbumNetworkQuery(query);
    }

    public IReadOnlyCollection<(Soulseek.SearchResponse Response, Soulseek.File File)> Snapshot()
        => Session.Snapshot();

    public IReadOnlyList<SearchRawResult> RawSnapshot(long afterSequence = 0)
        => Session.RawSnapshot(afterSequence);

    public IAsyncEnumerable<SearchRawResult> ReadRawResultsAsync(
        long afterSequence = 0,
        CancellationToken ct = default)
        => Session.ReadRawResultsAsync(afterSequence, ct);

    public SearchProjectionSnapshot<FileCandidate> GetSortedTrackCandidates(
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("sorted-track", search, userSuccessCounts),
            () => new IncrementalRawProjectionState<IncrementalResultSorter, FileCandidate>(
                new IncrementalResultSorter(
                    Query,
                    search,
                    userSuccessCounts,
                    useInfer: false,
                    useLevenshtein: false),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot()
                    .Select(x => new FileCandidate(x.Response, x.File))
                    .ToList()));

        return state.Snapshot(this);
    }

    public SearchProjectionSnapshot<AlbumFolder> GetAlbumFolders(SearchSettings search)
    {
        if (AlbumQuery == null)
            throw new InvalidOperationException("Album folder projection requires an album search job.");

        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("album-folders", search),
            () => new IncrementalRawProjectionState<IncrementalAlbumFolderProjector, AlbumFolder>(
                new IncrementalAlbumFolderProjector(AlbumQuery, search),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot()));

        return state.Snapshot(this);
    }

    public SearchProjectionSnapshot<SongJob> GetAggregateTracks(
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("aggregate-tracks", search, userSuccessCounts),
            () => new IncrementalRawProjectionState<IncrementalAggregateTrackProjector, SongJob>(
                new IncrementalAggregateTrackProjector(Query, search, userSuccessCounts),
                (projector, results) => projector.AddRange(results),
                projector => projector.Snapshot()));

        return state.Snapshot(this);
    }

    public SearchProjectionSnapshot<AlbumJob> GetAggregateAlbums(SearchSettings search)
    {
        if (AlbumQuery == null)
            throw new InvalidOperationException("Album aggregate projection requires an album search job.");

        var state = GetOrCreateIncrementalProjectionState(
            ProjectionKey("aggregate-albums", search),
            () => new IncrementalAlbumAggregateProjectionState(AlbumQuery, search));

        return state.Snapshot(this);
    }

    private TState GetOrCreateIncrementalProjectionState<TState>(
        string key,
        Func<TState> factory)
        where TState : class
    {
        lock (_projectionCacheLock)
        {
            if (_incrementalProjectionStates.TryGetValue(key, out var cached)
                && cached is TState cachedState)
                return cachedState;

            var created = factory();
            _incrementalProjectionStates[key] = created;
            return created;
        }
    }

    private static List<(Soulseek.SearchResponse Response, Soulseek.File File)> RawPairs(IReadOnlyList<SearchRawResult> rawResults)
        => rawResults
            .Select(x => (x.Response, x.File))
            .ToList();

    private static string ProjectionKey(string name, params object[] dependencies)
        => name + ":" + string.Join(':', dependencies.Select(RuntimeHelpers.GetHashCode));

    private sealed class IncrementalRawProjectionState<TProjector, TItem>
    {
        private readonly Lock gate = new();
        private readonly TProjector projector;
        private readonly Func<TProjector, IEnumerable<(Soulseek.SearchResponse Response, Soulseek.File File)>, int> addRange;
        private readonly Func<TProjector, List<TItem>> snapshot;
        private long lastSequence;
        private SearchProjectionSnapshot<TItem>? cachedSnapshot;

        public IncrementalRawProjectionState(
            TProjector projector,
            Func<TProjector, IEnumerable<(Soulseek.SearchResponse Response, Soulseek.File File)>, int> addRange,
            Func<TProjector, List<TItem>> snapshot)
        {
            this.projector = projector;
            this.addRange = addRange;
            this.snapshot = snapshot;
        }

        public SearchProjectionSnapshot<TItem> Snapshot(SearchJob job)
        {
            lock (gate)
            {
                var newResults = job.RawSnapshot(lastSequence);
                if (newResults.Count > 0)
                {
                    addRange(projector, RawPairs(newResults));
                    lastSequence = newResults[^1].Sequence;
                    cachedSnapshot = null;
                }

                int revision = job.Revision;
                bool isComplete = job.IsComplete;
                if (cachedSnapshot != null
                    && cachedSnapshot.Revision == revision
                    && cachedSnapshot.IsComplete == isComplete)
                {
                    return cachedSnapshot;
                }

                cachedSnapshot = new SearchProjectionSnapshot<TItem>(revision, snapshot(projector), isComplete);
                return cachedSnapshot;
            }
        }
    }

    private sealed class IncrementalAlbumAggregateProjectionState
    {
        private readonly Lock gate = new();
        private readonly IncrementalAlbumFolderProjector albumProjector;
        private readonly IncrementalAlbumAggregateProjector aggregateProjector;
        private long lastSequence;
        private SearchProjectionSnapshot<AlbumJob>? cachedSnapshot;

        public IncrementalAlbumAggregateProjectionState(AlbumQuery query, SearchSettings search)
        {
            albumProjector = new IncrementalAlbumFolderProjector(query, search);
            aggregateProjector = new IncrementalAlbumAggregateProjector(query, search);
        }

        public SearchProjectionSnapshot<AlbumJob> Snapshot(SearchJob job)
        {
            lock (gate)
            {
                var newResults = job.RawSnapshot(lastSequence);
                if (newResults.Count > 0)
                {
                    var changes = albumProjector.AddRangeAndGetChanges(RawPairs(newResults));
                    aggregateProjector.ApplyChanges(changes);
                    lastSequence = newResults[^1].Sequence;
                    cachedSnapshot = null;
                }

                int revision = job.Revision;
                bool isComplete = job.IsComplete;
                if (cachedSnapshot != null
                    && cachedSnapshot.Revision == revision
                    && cachedSnapshot.IsComplete == isComplete)
                {
                    return cachedSnapshot;
                }

                cachedSnapshot = new SearchProjectionSnapshot<AlbumJob>(revision, aggregateProjector.Snapshot(), isComplete);
                return cachedSnapshot;
            }
        }
    }

    public override string ToString(bool noInfo)
        => AlbumQuery?.ToString(noInfo) ?? Query.ToString(noInfo);
}
