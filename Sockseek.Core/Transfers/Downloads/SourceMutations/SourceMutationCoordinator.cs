using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Transfers.Downloads.SourceMutations;

internal sealed class SourceMutationCoordinator
{
    private readonly ConcurrentDictionary<string, byte> appliedSourceMutations = new(StringComparer.Ordinal);
    private readonly SourceMutationExecutor executor = new();

    public async Task ApplyIfNeededAsync(Job job, DownloadSettings config)
    {
        if (!config.Extraction.RemoveTracksFromSource) return;
        if (job is SearchJob or RetrieveFolderJob) return;
        if (job.SourceMutation == null) return;
        if (!appliedSourceMutations.TryAdd(job.SourceMutation.Key, 0)) return;

        SockseekLog.Jobs.Debug($"RemoveFromSource: '{job}' ({job.SourceMutation.Kind}, source='{job.SourceMutation.Source}', line={job.SourceMutation.LineNumber})");
        try { await executor.ApplyAsync(job.SourceMutation, config); }
        catch (Exception ex) { SockseekLog.Jobs.Error(ex, "Error removing from source"); }
    }
}
