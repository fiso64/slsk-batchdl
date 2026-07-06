using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core;

internal sealed class DownloadExecutorCoordinator
{
    private readonly DownloadExecutionContext context;
    private readonly JobOrchestrator jobs;
    private readonly AggregateDownloadExecutor aggregateDownloads;
    private readonly SongDownloadExecutor songDownloads;
    private readonly AlbumDownloadExecutor albumDownloads;

    public DownloadExecutorCoordinator(DownloadExecutionContext context, JobOrchestrator jobs)
    {
        this.context = context;
        this.jobs = jobs;
        aggregateDownloads = new AggregateDownloadExecutor(context, jobs);
        songDownloads = new SongDownloadExecutor(context, jobs);
        albumDownloads = new AlbumDownloadExecutor(context, jobs, songDownloads);
    }

    public async Task<JobOutcome> ProcessLeafDownload(Job job, JobContext ctx, CancellationToken parentToken, Job? parentJob)
    {
        var config = job.Config;

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await context.ClientManager.WaitUntilReadyAsync(job.Cts!.Token);

        try
        {
            JobOutcome outcome = JobOutcome.NoChange();
            switch (job)
            {
                case SongJob sj:
                    var songParent = parentJob ?? sj;
                    var songOrganizer = new FileManager(sj, config.Output, config.Extraction, ctx.OutputScope);
                    outcome = await songDownloads.ProcessSongDownload(sj, songParent, songOrganizer, parentToken);
                    outcome = await songDownloads.CommitAndFinalizeSong(sj, songParent, outcome, ctx, songOrganizer, organize: true, updateIndexes: true);
                    break;

                case AlbumJob aj:
                    outcome = await albumDownloads.ProcessAlbumDownload(aj, ctx);
                    break;

                case AggregateJob ag:
                    ag.UpdateActivity(JobActivityPhase.RunningChildren);
                    outcome = await aggregateDownloads.ProcessAggregateDownload(ag, ctx);
                    JobOutcomeCommitter.Commit(ag, outcome);
                    break;
            }

            SockseekLog.Jobs.Trace($"ProcessLeafJob: finished for job {job.DisplayId} ({job.GetType().Name})");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            var outcome = JobOutcome.Cancelled(jobs.CancellationSourceFor(job, parentToken));
            JobOutcomeCommitter.Commit(job, outcome);
            return outcome;
        }
    }

    // ── per-job-type handlers ─────────────────────────────────────────────────

    public async Task<JobOutcome> CommitAndFinalizeSong(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        JobContext jobCtx,
        FileManager organizer,
        bool organize,
        bool updateIndexes)
        => await songDownloads.CommitAndFinalizeSong(song, parentJob, outcome, jobCtx, organizer, organize, updateIndexes);

    public async Task<JobOutcome> RunOnCompleteIfApplicable(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
        => await songDownloads.RunOnCompleteIfApplicable(job, song, ctx, outcome);

    public static void ApplyPreCommitOutcomeMetadata(Job job, JobOutcome outcome)
    {
        if (job is SongJob song)
        {
            if (outcome.ChosenCandidate != null)
                song.ChosenCandidate = outcome.ChosenCandidate;
            if (outcome.ShouldUpdateDownloadPath)
                song.DownloadPath = outcome.DownloadPath;
            if (outcome.DownloadSource != SongDownloadSource.None)
                song.DownloadSource = outcome.DownloadSource;
        }
        else if (job is AlbumJob album && outcome.ShouldUpdateDownloadPath)
        {
            album.DownloadPath = outcome.DownloadPath;
        }
    }

    public static JobOutcome OutcomeWithCurrentMetadata(Job job, JobOutcome outcome)
    {
        if (job is SongJob song)
        {
            var downloadPath = song.DownloadPath ?? outcome.DownloadPath;
            var chosenCandidate = song.ChosenCandidate ?? outcome.ChosenCandidate;
            var downloadSource = song.DownloadSource != SongDownloadSource.None
                ? song.DownloadSource
                : outcome.DownloadSource;

            return outcome.TerminalOutcome switch
            {
                JobTerminalOutcome.Succeeded => JobOutcome.Done(downloadPath, chosenCandidate, downloadSource),
                JobTerminalOutcome.Skipped when outcome.SkipReason == JobSkipReason.AlreadyExists => JobOutcome.AlreadyExists(downloadPath),
                JobTerminalOutcome.Skipped => JobOutcome.Skipped(outcome.SkipReason, outcome.FailureReason, downloadPath),
                _ => outcome,
            };
        }

        if (job is AlbumJob album)
        {
            var downloadPath = album.DownloadPath ?? outcome.DownloadPath;

            return outcome.TerminalOutcome switch
            {
                JobTerminalOutcome.Succeeded => JobOutcome.Done(downloadPath),
                JobTerminalOutcome.Skipped when outcome.SkipReason == JobSkipReason.AlreadyExists => JobOutcome.AlreadyExists(downloadPath),
                JobTerminalOutcome.Skipped => JobOutcome.Skipped(outcome.SkipReason, outcome.FailureReason, downloadPath),
                _ => outcome,
            };
        }

        return outcome;
    }

    public static string JobLogKind(Job job) => SockseekLog.JobTypeName(job);
}
