using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.State;

internal sealed class DownloadedFileCache
{
    internal sealed record CachedDownload(
        string OutputPath,
        PeerFileTarget Target);

    private readonly object gate = new();
    private readonly ConcurrentDictionary<PeerFileIdentity, CachedDownload> downloadedFiles = new();

    public T WithExclusiveAccess<T>(Func<T> action)
    {
        lock (gate)
            return action();
    }

    public bool TryGetReusable(
        PeerFileTarget target,
        [NotNullWhen(true)] out CachedDownload? result)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (gate)
        {
            if (!downloadedFiles.TryGetValue(target.Identity, out var cached))
            {
                result = null;
                return false;
            }

            var existingFileInfo = new FileInfo(cached.OutputPath);
            var expectedSize = target.Size ?? cached.Target.Size;
            if (existingFileInfo.Exists
                && (expectedSize is null || existingFileInfo.Length == expectedSize.Value))
            {
                result = cached;
                return true;
            }

            downloadedFiles.TryRemove(target.Identity, out _);
            result = null;
            return false;
        }
    }

    public void Publish(string outputPath, PeerFileTarget target)
    {
        lock (gate)
            downloadedFiles[target.Identity] = new CachedDownload(outputPath, target);
    }
}
