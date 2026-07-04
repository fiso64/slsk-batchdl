using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Sockseek.Core.Transfers.Downloads.State;

internal sealed class ActiveDownloadTracker
{
    private readonly ConcurrentDictionary<string, ActiveDownload> downloads = new();

    public IEnumerable<ActiveDownload> ActiveDownloads => downloads.Values;

    public bool TryAdd(ActiveDownload download)
        => downloads.TryAdd(download.Candidate.Filename, download);

    public bool TryGet(string filename, [NotNullWhen(true)] out ActiveDownload? download)
        => downloads.TryGetValue(filename, out download);

    public bool TryRemove(string filename, [NotNullWhen(true)] out ActiveDownload? download)
        => downloads.TryRemove(filename, out download);

    public bool Contains(string filename)
        => downloads.ContainsKey(filename);
}
