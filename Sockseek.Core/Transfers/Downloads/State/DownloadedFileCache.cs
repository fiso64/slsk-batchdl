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
    private readonly Dictionary<string, HashSet<PeerFileIdentity>> identitiesByPath = new(PathComparer);
    private readonly Dictionary<string, OutputPathClaim> outputPathClaims = new(PathComparer);

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

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

            RemoveIdentity(target.Identity);
            result = null;
            return false;
        }
    }

    public void Publish(string outputPath, PeerFileTarget target)
    {
        lock (gate)
        {
            string path = NormalizePath(outputPath);
            InvalidatePathCore(path);
            RemoveIdentity(target.Identity);
            downloadedFiles[target.Identity] = new CachedDownload(path, target);
            if (!identitiesByPath.TryGetValue(path, out var identities))
            {
                identities = [];
                identitiesByPath.Add(path, identities);
            }
            identities.Add(target.Identity);
        }
    }

    public void InvalidatePath(string outputPath)
    {
        lock (gate)
            InvalidatePathCore(NormalizePath(outputPath));
    }

    public bool IsPathOwnedByAnotherTarget(string outputPath, PeerFileTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (gate)
        {
            return identitiesByPath.TryGetValue(NormalizePath(outputPath), out var identities)
                && identities.Any(identity => identity != target.Identity);
        }
    }

    public bool HasPublishedPathOwner(string outputPath)
    {
        lock (gate)
            return identitiesByPath.ContainsKey(NormalizePath(outputPath));
    }

    public async ValueTask<IDisposable> ClaimOutputPathAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        string path = NormalizePath(outputPath);
        OutputPathClaim claim;
        lock (gate)
        {
            if (!outputPathClaims.TryGetValue(path, out claim!))
            {
                claim = new OutputPathClaim();
                outputPathClaims.Add(path, claim);
            }
            claim.ReferenceCount++;
        }

        try
        {
            // Taking an uncontended in-process claim is not an I/O boundary and
            // must not introduce a new cancellation checkpoint before the
            // transfer owner publishes its normal start/attempt lifecycle.
            // Cancellation still aborts an actual wait behind another owner.
            if (!claim.Semaphore.Wait(0))
                await claim.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new OutputPathLease(this, path, claim);
        }
        catch
        {
            ReleaseClaimReference(path, claim, releaseSemaphore: false);
            throw;
        }
    }

    private void InvalidatePathCore(string path)
    {
        if (!identitiesByPath.Remove(path, out var identities))
            return;
        foreach (PeerFileIdentity identity in identities)
            downloadedFiles.TryRemove(identity, out _);
    }

    private void RemoveIdentity(PeerFileIdentity identity)
    {
        if (!downloadedFiles.TryRemove(identity, out CachedDownload? previous))
            return;
        string path = NormalizePath(previous.OutputPath);
        if (!identitiesByPath.TryGetValue(path, out var identities))
            return;
        identities.Remove(identity);
        if (identities.Count == 0)
            identitiesByPath.Remove(path);
    }

    private void ReleaseClaimReference(
        string path,
        OutputPathClaim claim,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
            claim.Semaphore.Release();
        lock (gate)
        {
            claim.ReferenceCount--;
            if (claim.ReferenceCount == 0
                && outputPathClaims.GetValueOrDefault(path) == claim)
            {
                outputPathClaims.Remove(path);
                claim.Semaphore.Dispose();
            }
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path);

    private sealed class OutputPathClaim
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class OutputPathLease(
        DownloadedFileCache owner,
        string path,
        OutputPathClaim claim) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.ReleaseClaimReference(path, claim, releaseSemaphore: true);
        }
    }
}
