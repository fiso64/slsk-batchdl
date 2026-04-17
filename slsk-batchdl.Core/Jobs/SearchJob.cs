using Sldl.Core.Models;
using Sldl.Core.Services;

namespace Sldl.Core.Jobs;

public enum SearchIntent
{
    Track,
    Album,
}

public class SearchJob : Job
{
    private readonly object _projectionCacheLock = new();
    private readonly Dictionary<string, (int Revision, object Value)> _projectionCache = new();

    public SearchIntent Intent { get; }
    public SongQuery Query { get; }
    public AlbumQuery? AlbumQuery { get; }
    public bool IncludeFullResults { get; }

    public SearchSession? Session { get; internal set; }

    public int ResultCount => Session?.Results.Count ?? 0;
    public int Revision => Session?.Revision ?? 0;

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
        => Session?.Snapshot() ?? [];

    public T GetOrCreateProjection<T>(
        string key,
        Func<IReadOnlyCollection<(Soulseek.SearchResponse Response, Soulseek.File File)>, T> factory)
    {
        int revision = Revision;
        lock (_projectionCacheLock)
        {
            if (_projectionCache.TryGetValue(key, out var cached)
                && cached.Revision == revision
                && cached.Value is T value)
            {
                return value;
            }

            var created = factory(Snapshot());
            _projectionCache[key] = (revision, created!);
            return created;
        }
    }

    public override string ToString(bool noInfo)
        => AlbumQuery?.ToString(noInfo) ?? Query.ToString(noInfo);
}
