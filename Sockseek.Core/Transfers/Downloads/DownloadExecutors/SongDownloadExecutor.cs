using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core;

internal sealed class SongDownloadExecutor
{
    private readonly DownloadExecutionContext context;
    private readonly JobOrchestrator jobs;
    private readonly SongDownloadFallbackRunner fallbackRunner;

    public SongDownloadExecutor(DownloadExecutionContext context, JobOrchestrator jobs)
    {
        this.context = context;
        this.jobs = jobs;
        fallbackRunner = new SongDownloadFallbackRunner(context);
    }

    public async Task<JobOutcome> ProcessSongDownload(
        SongJob job,
        Job downloadOwner,
        FileManager organizer,
        CancellationToken parentToken)
    {
        var config = job.Config;

        // If ResolvedTarget is set, pre-populate Candidates so search is skipped.
        if (job.ResolvedTarget != null && job.Candidates == null)
            job.Candidates = new List<FileCandidate> { job.ResolvedTarget };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        return await DownloadSong(job, downloadOwner, config, organizer, cts, () => jobs.CancellationSourceFor(job, parentToken));
    }

    public async Task<JobOutcome> CommitAndFinalizeSong(
        SongJob song,
        Job parentJob,
        JobOutcome outcome,
        JobContext jobCtx,
        FileManager organizer,
        bool organize,
        bool updateIndexes)
    {
        DownloadExecutorCoordinator.ApplyPreCommitOutcomeMetadata(song, outcome);
        if (outcome.FailureReason != JobFailureReason.Cancelled)
            outcome = await CompleteSongBeforeCommit(song, parentJob, outcome, jobCtx, organizer, organize);

        outcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(song, outcome);
        JobOutcomeCommitter.Commit(song, outcome);

        if (updateIndexes)
        {
            SockseekLog.Jobs.Trace($"ProcessSongJob finished for {song.DisplayId}. Calling IndexEditor Update ({(jobCtx.IndexEditor != null ? "Yes" : "No")}) and PlaylistEditor Update ({(jobCtx.PlaylistEditor != null ? "Yes" : "No")})");
            jobCtx.IndexEditor?.Update();
            jobCtx.PlaylistEditor?.Update();
        }

        return outcome;
    }

    public async Task<JobOutcome> RunOnCompleteIfApplicable(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
    {
        if (!OnCompleteExecutor.HasApplicableCommand(job, song, outcome))
            return outcome;

        var activityJob = song ?? job;
        activityJob.UpdateActivity(JobActivityPhase.RunningOnComplete);
        return await OnCompleteExecutor.ExecuteAsync(job, song, ctx, outcome);
    }

    public async Task<JobOutcome> DownloadEmbeddedSong(
        SongJob song,
        Job parentJob,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource groupCts,
        bool cancelGroupOnFail,
        bool organize)
    {
        if (song.LifecycleState != JobLifecycleState.Pending) return JobOutcome.NoChange();

        song.WorkflowId = parentJob.WorkflowId;
        song.Config = config;
        song.Cts = CancellationTokenSource.CreateLinkedTokenSource(context.Runtime.Token, groupCts.Token);
        context.RegisterJob(song, parentJob);

        JobOutcome outcome = JobOutcome.NoChange();
        try
        {
            outcome = await DownloadSong(
                song,
                parentJob,
                config,
                organizer,
                song.Cts,
                () => CancellationSourceForEmbeddedSong(song, parentJob, groupCts));

            if (outcome.FailureReason == JobFailureReason.Cancelled && !groupCts.IsCancellationRequested)
            {
                JobOutcomeCommitter.Commit(song, outcome);
                return outcome;
            }

            if (cancelGroupOnFail && ShouldCancelGroupOnEmbeddedOutcome(outcome))
            {
                JobOutcomeCommitter.Commit(song, outcome);
                await groupCts.CancelAsync();
                throw new OperationCanceledException();
            }

            var finalOutcome = await CommitAndFinalizeSong(
                song,
                parentJob,
                outcome,
                context.Ctx(parentJob),
                organizer,
                organize,
                updateIndexes: false);

            if (cancelGroupOnFail && ShouldCancelGroupOnEmbeddedOutcome(finalOutcome))
            {
                await groupCts.CancelAsync();
                throw new OperationCanceledException();
            }

            return finalOutcome;
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested
            && song.Cts.IsCancellationRequested
            && song.FailureReason == JobFailureReason.Cancelled)
        {
            // User cancelled only this embedded song; keep the album/aggregate parent running.
            return JobOutcome.Cancelled(JobOrchestrator.CancellationSourceForDerivedCancellation(song));
        }
        catch (OperationCanceledException) when (!groupCts.IsCancellationRequested && cancelGroupOnFail)
        {
            await groupCts.CancelAsync();
            throw;
        }
        finally
        {
            context.Events.RaiseJobExecutionCompleted(song);
        }
    }

    private async Task<JobOutcome> CompleteSongBeforeCommit(SongJob song, Job parentJob, JobOutcome outcome, JobContext jobCtx, FileManager organizer, bool organize)
    {
        var finalization = context.OutputFinalizer.FinalizeSongPlacement(song, parentJob, outcome, organizer, organize);
        if (finalization.OrganizationException != null)
            return finalization.Outcome;

        var postProcessOutcome = DownloadExecutorCoordinator.OutcomeWithCurrentMetadata(song, finalization.Outcome);
        postProcessOutcome = await RunOnCompleteIfApplicable(parentJob, song, jobCtx, postProcessOutcome);
        DownloadExecutorCoordinator.ApplyPreCommitOutcomeMetadata(song, postProcessOutcome);

        context.OutputFinalizer.PublishDownloadedFileCache(song, postProcessOutcome);
        return postProcessOutcome;
    }

    private async Task<JobOutcome> DownloadSong(
        SongJob song,
        Job job,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource cts,
        Func<JobCancellationSource> cancellationSource)
    {
        if (song.LifecycleState != JobLifecycleState.Pending) return JobOutcome.NoChange();

        int tries = config.Transfer.UnknownErrorRetries;
        JobOutcome? finalOutcome = null;
        string? lastFailureMessage = null;
        string? lastFailureDetail = null;

        while (tries > 0)
        {
            if (song.LifecycleState == JobLifecycleState.Terminal)
                break;

            await context.ClientManager.WaitUntilReadyAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                var outcome = await SearchAndDownloadSong(song, job, config, organizer, cts);
                if (outcome.TerminalOutcome == JobTerminalOutcome.Succeeded)
                {
                    finalOutcome = outcome;
                }
                else
                {
                    lastFailureMessage = outcome.FailureMessage;
                    lastFailureDetail = outcome.FailureDetail;
                    finalOutcome = outcome;
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    SockseekLog.Jobs.Debug($"{ex}");
                else
                    SockseekLog.Jobs.Debug($"Cancelled: {song}");

                if (!context.ClientManager.IsConnectedAndLoggedIn)
                {
                    continue;
                }
                else if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    return JobOutcome.Cancelled(cancellationSource());
                }
                else
                {
                    lastFailureMessage = DownloadFailureMessage(ex);
                    lastFailureDetail = SockseekLog.ExceptionDetail(ex);
                    tries--;
                    continue;
                }
            }

            break;
        }

        if (tries == 0)
        {
            return JobOutcome.Failed(JobFailureReason.AllDownloadsFailed, lastFailureMessage, lastFailureDetail);
        }

        return finalOutcome ?? JobOutcome.NoChange();
    }

    /// <summary>
    /// Searches for candidates for <paramref name="song"/> then downloads the best one.
    /// Returns an explicit outcome for expected domain failures; unexpected infrastructure
    /// exceptions still bubble to the retry policy in <see cref="DownloadSong"/>.
    /// </summary>
    private async Task<JobOutcome> SearchAndDownloadSong(
        SongJob song,
        Job job,
        DownloadSettings config,
        FileManager organizer,
        CancellationTokenSource cts)
    {
        var responseData = new ResponseData();
        bool searched = false;

        // Skip search if candidates are pre-set (ResolvedTarget / direct download).
        if (song.Candidates == null)
        {

            if (!config.Search.FastSearch)
            {
                searched = true;
                await context.Runtime.Searcher.SearchSong(song, config.Search, responseData, cts.Token);
            }
            else
            {
                // Fast-search: start the search as a background task and race it against a
                // provisional download of the first qualifying candidate.
                // The search concurrency slot is held by SearchSong internally; cancelling
                // searchCts causes SearchSong to return and release it naturally.
                using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                Task<FileDownloadOutcome?>? fastDownloadTask = null;

                searched = true;
                var searchTask = context.Runtime.Searcher.SearchSong(song, config.Search, responseData, searchCts.Token,
                    onFastSearchCandidate: fc =>
                    {
                        if (fastDownloadTask == null)
                        {
                            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search starting provisional download from {fc.Username}\\{fc.Filename}: {song}");
                            var target = context.OutputFinalizer.GetInitialDownloadTarget(config, song, organizer, fc);

                            // Use the main job CTS for the download so cancelling the search doesn't kill the download.
                            fastDownloadTask = context.Runtime.Downloader
                                .DownloadFile(
                                    fc,
                                    target.Path,
                                    song,
                                    config.Transfer,
                                    config.Output.ParentDir,
                                    cts.Token,
                                    target.PublishToDuplicateCache,
                                    DownloadParentFor(song, job))
                                .ContinueWith(t =>
                                {
                                    if (t.IsCompletedSuccessfully)
                                        return (FileDownloadOutcome?)t.Result;
                                    return null;
                                }, TaskScheduler.Default);
                        }
                    });

                while (!searchTask.IsCompleted)
                {
                    if (fastDownloadTask != null && fastDownloadTask.IsCompleted)
                        break;
                    await Task.WhenAny(fastDownloadTask ?? searchTask, searchTask);
                }

                if (fastDownloadTask != null)
                {
                    var fastDownload = await fastDownloadTask;
                    if (fastDownload?.Status == FileDownloadStatus.Completed && fastDownload.Result != null)
                    {
                        // Fast download won - cancel the search.
                        await searchCts.CancelAsync();
                        try { await searchTask; } catch (OperationCanceledException) { }

                        var result = fastDownload.Result;
                        context.UserSuccesses.RecordSuccess(result.Candidate.Username);
                        SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search provisional download succeeded from {result.Candidate.Username}\\{result.Candidate.Filename}: {song}");
                        return JobOutcome.Done(result.OutputPath, result.Candidate);
                    }

                    if (fastDownload?.Status == FileDownloadStatus.ManuallySkipped)
                    {
                        SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search provisional download was manually skipped, waiting for full search to complete: {song}");
                    }
                    else
                    {
                        SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: fast-search provisional download failed, waiting for full search to complete: {song}");
                    }

                    await searchTask;
                }
                else
                {
                    await searchTask;
                }
            }
        }

        var candidates = song.Candidates;

        if (candidates == null || candidates.Count == 0)
        {
            var fallbackOutcome = await fallbackRunner.TryRunAsync(song, config, organizer, cts.Token);
            if (fallbackOutcome != null)
                return fallbackOutcome;

            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: no suitable candidates after search: {song}");
            return searched
                ? DownloadOutcomes.NoMatchingDiscovery(responseData, "file result", "file results", "song candidates")
                : DownloadOutcomes.NoMatchingCandidates();
        }

        // Try candidates in order until one succeeds.
        int tried = 0;
        Exception? lastDownloadException = null;
        string? lastDownloadFailureMessage = null;
        foreach (var candidate in candidates)
        {
            tried++;
            var target = context.OutputFinalizer.GetInitialDownloadTarget(config, song, organizer, candidate);

            FileDownloadOutcome download;
            try
            {
                song.UpdateActivity(JobActivityPhase.Downloading);
                // ReportDownloadStart is called inside DownloadFile (via Downloader).
                download = await context.Runtime.Downloader.DownloadFile(
                    candidate,
                    target.Path,
                    song,
                    config.Transfer,
                    config.Output.ParentDir,
                    cts.Token,
                    target.PublishToDuplicateCache,
                    DownloadParentFor(song, job));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!context.ClientManager.IsConnectedAndLoggedIn)
                    throw;

                lastDownloadException = ex;
                lastDownloadFailureMessage = DownloadFailureMessage(ex);
                SockseekLog.Jobs.Debug(
                    $"Download attempt {tried} failed for '{candidate.Username}\\{candidate.Filename}' " +
                    $"to '{target.Path}': {SockseekLog.ExceptionSummary(ex)}");
                if (tried >= candidates.Count || tried >= config.Transfer.MaxDownloadRetries)
                {
                    return JobOutcome.Failed(
                        JobFailureReason.AllDownloadsFailed,
                        lastDownloadFailureMessage);
                }

                continue;
            }

            if (download.Status == FileDownloadStatus.ManuallySkipped)
            {
                SockseekLog.Jobs.Debug($"Manually skipped candidate: {candidate.Username}\\{candidate.Filename}");
                tried--;
                continue;
            }

            var result = download.Result
                ?? throw new InvalidOperationException($"Completed download outcome missing result for '{candidate.Username}\\{candidate.Filename}'.");
            context.UserSuccesses.RecordSuccess(result.Candidate.Username);
            SockseekLog.Jobs.Debug($"[{song.DisplayId}] SongJob: download succeeded from {result.Candidate.Username}\\{result.Candidate.Filename} to '{result.OutputPath}': {song}");
            return JobOutcome.Done(result.OutputPath, result.Candidate);
        }

        if (lastDownloadException != null)
            return JobOutcome.Failed(
                JobFailureReason.AllDownloadsFailed,
                lastDownloadFailureMessage ?? DownloadFailureMessage(lastDownloadException));

        return DownloadOutcomes.NoMatchingCandidates();
    }

    private static bool ShouldCancelGroupOnEmbeddedOutcome(JobOutcome outcome)
        => outcome.TerminalOutcome is JobTerminalOutcome.Failed
            or JobTerminalOutcome.Skipped
            or JobTerminalOutcome.PartialSuccess;

    private JobCancellationSource CancellationSourceForEmbeddedSong(
        SongJob song,
        Job parentJob,
        CancellationTokenSource groupCts)
    {
        if (song.CancellationSource != JobCancellationSource.None)
            return song.CancellationSource;
        if (context.Runtime.IsCancellationRequested)
            return JobCancellationSource.UserRequestedAllJobs;
        if (parentJob.Cts?.IsCancellationRequested == true || groupCts.IsCancellationRequested)
            return JobCancellationSource.ParentJob;

        return JobCancellationSource.InternalEngine;
    }

    private static string? DownloadFailureMessage(Exception ex)
        => SockseekLog.ExceptionSummary(ex);

    private static Job? DownloadParentFor(SongJob song, Job job)
        => ReferenceEquals(song, job) ? null : job;
}
