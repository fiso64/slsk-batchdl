using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Transfers.Downloads.SourceMutations;

internal sealed class SourceMutationCoordinator
{
    private readonly ConcurrentDictionary<string, byte> appliedSourceMutations = new(StringComparer.Ordinal);
    private readonly SourceMutationExecutor executor = new();
    private readonly DownloadEvents events;
    private readonly ILogger<SourceMutationCoordinator> logger;

    public SourceMutationCoordinator(
        DownloadEvents events,
        ILogger<SourceMutationCoordinator> logger)
    {
        this.events = events;
        this.logger = logger;
    }

    public async Task ApplyIfNeededAsync(Job job, DownloadSettings config)
    {
        if (!config.Extraction.RemoveTracksFromSource) return;
        if (job is SearchJob or RetrieveFolderJob) return;
        if (job.SourceMutation == null) return;
        if (!appliedSourceMutations.TryAdd(job.SourceMutation.Key, 0)) return;

        DownloadLogMessages.JobDecision(logger, job.Id, "source-mutation", null);
        try { await executor.ApplyAsync(job.SourceMutation, config); }
        catch (Exception ex)
        {
            DownloadLogMessages.ComponentFailed(
                logger,
                ex,
                "source-mutation",
                job.Id);
            events.RaiseJobMessage(
                job,
                LogLevel.Warning,
                null,
                "download succeeded, but removing it from the source failed");
        }
    }
}
