using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.Runtime;

namespace Sockseek.Core;

internal sealed class SongDownloadFallbackRunner
{
    private readonly DownloadExecutionContext context;

    public SongDownloadFallbackRunner(DownloadExecutionContext context)
    {
        this.context = context;
    }

    public async Task<JobOutcome?> TryRunAsync(
        SongJob song,
        DownloadSettings config,
        FileManager organizer,
        CancellationToken ct)
    {
        if (!context.SongDownloadFallback.CanRun(song, config))
            return null;

        song.UpdateActivity(JobActivityPhase.RunningFallback);
        SockseekLog.Jobs.Info(song, $"running fallback: {song}");
        var fallbackLog = ExtractorContext.ForJob(song, context.Events).Log;
        var outcome = await context.SongDownloadFallback.TryDownloadAsync(song, config, organizer, fallbackLog, ct);
        if (outcome == null || !outcome.ShouldCommit)
            return null;

        SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fallback produced {outcome.TerminalOutcome}: {song}");
        return outcome;
    }
}
