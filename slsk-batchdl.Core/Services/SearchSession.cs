using System.Collections.Concurrent;
using Soulseek;

namespace Sldl.Core.Services;

public sealed class SearchSession
{
    private int _revision;
    private int _lockedFileCount;

    public ConcurrentDictionary<string, (SearchResponse Response, Soulseek.File File)> Results { get; } = new();

    public int Revision => Volatile.Read(ref _revision);
    public int LockedFileCount => Volatile.Read(ref _lockedFileCount);

    public event Action<SearchSession, SearchResponse, Soulseek.File>? RawResultReceived;

    public IReadOnlyCollection<(SearchResponse Response, Soulseek.File File)> Snapshot()
        => Results.Values.ToList();

    public void AddResponse(SearchResponse response)
    {
        Interlocked.Add(ref _lockedFileCount, response.LockedFileCount);

        if (response.Files.Count == 0)
            return;

        foreach (var file in response.Files)
        {
            if (Results.TryAdd(response.Username + '\\' + file.Filename, (response, file)))
            {
                Interlocked.Increment(ref _revision);
                RawResultReceived?.Invoke(this, response, file);
            }
        }
    }
}
