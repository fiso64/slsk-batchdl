using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.State;

internal sealed class DownloadedFileCache
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, FileDownloadResult> downloadedFiles = new();

    public T WithExclusiveAccess<T>(Func<T> action)
    {
        lock (gate)
            return action();
    }

    public bool TryGetReusable(FileCandidate candidate, [NotNullWhen(true)] out FileDownloadResult? result)
    {
        var fileKey = Key(candidate);

        lock (gate)
        {
            if (!downloadedFiles.TryGetValue(fileKey, out var cached))
            {
                result = null;
                return false;
            }

            var existingFileInfo = new FileInfo(cached.OutputPath);
            if (existingFileInfo.Exists && existingFileInfo.Length == candidate.File.Size)
            {
                result = cached;
                return true;
            }

            downloadedFiles.TryRemove(fileKey, out _);
            result = null;
            return false;
        }
    }

    public void Publish(FileDownloadResult result)
        => Publish(result.OutputPath, result.Candidate);

    public void Publish(string outputPath, FileCandidate candidate)
    {
        lock (gate)
            downloadedFiles[Key(candidate)] = new FileDownloadResult(outputPath, candidate);
    }

    private static string Key(FileCandidate candidate)
        => candidate.Username + '\\' + candidate.Filename;
}
