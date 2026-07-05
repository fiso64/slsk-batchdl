using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Snapshots;

namespace Sockseek.Server;

public sealed class EngineEventDtoAdapter
{
    private readonly Func<JobSnapshot, JobSummaryDto> getSummary;
    private readonly Action<string, object> publish;

    public EngineEventDtoAdapter(Func<JobSnapshot, JobSummaryDto> getSummary, Action<string, object> publish)
    {
        this.getSummary = getSummary;
        this.publish = publish;
    }

    public void Attach(DownloadEvents events, SearchEvents searchEvents)
    {
        events.JobStatus += change => publish("job.status", new JobStatusEventDto(getSummary(change.Job), change.Status));
        events.JobMessage += change => publish("job.message", new JobMessageEventDto(getSummary(change.Job), change.Level.ToString(), change.Source, change.Message));
        events.WorkflowMessage += change => publish("workflow.message", new WorkflowMessageEventDto(change.WorkflowId, change.Level.ToString(), change.Source, change.Message));
        events.JobActivityChanged += change => publish("job.activity-changed", new JobActivityChangedEventDto(getSummary(change.Job)));
        events.JobStateChanged += OnJobStateChanged;
        events.DownloadStarted += change => publish("download.started", new DownloadStartedEventDto(
            change.Song.Id,
            change.Song.DisplayId,
            change.Song.WorkflowId,
            ServerSnapshotMapper.ToSongQueryDto(SongPayload(change.Song).Query),
            ServerSnapshotMapper.ToFileCandidateDto(change.Candidate),
            change.TransferId));
        events.DownloadProgress += change => publish("download.progress", new DownloadProgressEventDto(
            change.Song.Id,
            change.Song.WorkflowId,
            change.BytesTransferred,
            change.TotalBytes,
            change.TransferId));
        events.DownloadStateChanged += change => publish("download.state-changed", new DownloadStateChangedEventDto(
            change.Song.Id,
            change.Song.WorkflowId,
            change.State,
            change.TransferId));
        events.DownloadAttemptFailed += change => publish("download.attempt-failed", new DownloadAttemptFailedEventDto(
            change.Song.Id,
            change.Song.DisplayId,
            change.Song.WorkflowId,
            ServerSnapshotMapper.ToSongQueryDto(SongPayload(change.Song).Query),
            ServerSnapshotMapper.ToFileCandidateDto(change.Candidate),
            change.OutputPath,
            change.Attempt,
            change.MaxAttempts,
            change.Exception.Type,
            change.Exception.Message,
            change.Exception.Detail,
            change.TransferId));
        searchEvents.SearchRateLimited += resetsAt => publish("search.rate-limited", new SearchRateLimitedEventDto(resetsAt));
        searchEvents.SearchResumed += () => publish("search.resumed", new SearchResumedEventDto());
        events.TrackBatchResolved += change => publish("track-batch.resolved", new TrackBatchResolvedEventDto(
            getSummary(change.Owner),
            change.Owner.Kind == JobSnapshotKind.JobList,
            change.Owner.PrintOption,
            change.Pending.Count,
            change.Existing.Count,
            change.NotFound.Count,
            [.. SelectTrackBatchRows(change.Pending, change.Owner.PrintOption, limit: 20)],
            [.. SelectTrackBatchRows(change.Existing, change.Owner.PrintOption, limit: 20)],
            [.. SelectTrackBatchRows(change.NotFound, change.Owner.PrintOption, limit: 20)]));
    }

    private void OnJobStateChanged(JobStateChangedChange change)
    {
        var job = change.Job;
        switch (job.Payload)
        {
            case SongJobSnapshotPayload song:
                if (job.ActivityPhase == JobActivityPhase.Searching)
                {
                    publish("song.searching", new SongSearchingEventDto(job.Id, job.DisplayId, job.WorkflowId, ServerSnapshotMapper.ToSongQueryDto(song.Query)));
                }
                else if (job.LifecycleState == JobLifecycleState.Terminal && ShouldPublishSongStateChanged(job))
                {
                    publish("song.state-changed", new SongStateChangedEventDto(
                        job.Id,
                        job.DisplayId,
                        job.WorkflowId,
                        ServerSnapshotMapper.ToSongQueryDto(song.Query),
                        EngineStateStore.ToServerJobLifecycleState(job.LifecycleState),
                        EngineStateStore.ToServerJobActivityPhase(job.ActivityPhase),
                        job.ActivityUntilUtc,
                        EngineStateStore.ToServerJobTerminalOutcome(job.TerminalOutcome),
                        EngineStateStore.ToServerJobSkipReason(job.SkipReason),
                        EngineStateStore.ToServerFailureReason(job.FailureReason),
                        song.DownloadPath,
                        song.ResolvedTarget != null ? ServerSnapshotMapper.ToFileCandidateDto(song.ResolvedTarget) : null,
                        job.Discovery?.RawResultCount,
                        job.Discovery?.LockedFileCount,
                        job.FailureMessage,
                        EngineStateStore.ToServerJobCancellationSource(job.CancellationSource),
                        EngineStateStore.ToServerSongDownloadSource(song.DownloadSource)));
                }
                break;

            case AlbumJobSnapshotPayload album:
                if (job.ActivityPhase == JobActivityPhase.Searching)
                {
                    publish("job.started", new JobStartedEventDto(getSummary(job)));
                }
                else if (job.ActivityPhase == JobActivityPhase.Downloading && album.ResolvedTarget != null)
                {
                    var folder = album.ResolvedTarget;
                    var tracks = album.TrackJobs.Select(track => ServerSnapshotMapper.ToSongJobPayloadDto(track)).ToList();
                    publish("album.download-started", new AlbumDownloadStartedEventDto(
                        getSummary(job),
                        ServerSnapshotMapper.ToAlbumFolderDto(folder, includeFiles: false),
                        tracks));
                    publish("album.track-download-started", new AlbumTrackDownloadStartedEventDto(
                        getSummary(job),
                        ServerSnapshotMapper.ToAlbumFolderDto(folder, includeFiles: false),
                        tracks));
                }
                else if (job.LifecycleState == JobLifecycleState.Terminal)
                {
                    publish("album.state-changed", new AlbumStateChangedEventDto(getSummary(job), album.DownloadPath));
                }
                break;

            case ExtractJobSnapshotPayload extract:
                if (job.ActivityPhase == JobActivityPhase.Extracting)
                {
                    publish("extraction.started", new ExtractionStartedEventDto(
                        getSummary(job),
                        extract.Input,
                        extract.InputType,
                        ExtractionSource(extract)));
                }
                else if (IsUnsuccessfulTerminal(job))
                {
                    publish("extraction.failed", new ExtractionFailedEventDto(
                        getSummary(job),
                        job.FailureMessage ?? "Extraction failed",
                        ExtractionSource(extract)));
                }
                break;

            case AggregateJobSnapshotPayload aggregate when job.ActivityPhase == JobActivityPhase.RunningChildren:
                publish("job.status", new JobStatusEventDto(getSummary(job), "running"));
                var pending = aggregate.Songs.Where(song => song.LifecycleState == JobLifecycleState.Pending).ToList();
                var existing = aggregate.Songs
                    .Where(song => song.TerminalOutcome == JobTerminalOutcome.Skipped && song.SkipReason == JobSkipReason.AlreadyExists)
                    .ToList();
                var notFound = aggregate.Songs.Where(song => IsNotFoundFailure(song.FailureReason)).ToList();
                publish("track-batch.resolved", new TrackBatchResolvedEventDto(
                    getSummary(job),
                    false,
                    job.PrintOption,
                    pending.Count,
                    existing.Count,
                    notFound.Count,
                    [.. SelectTrackBatchRows(pending, job.PrintOption, limit: 20)],
                    [.. SelectTrackBatchRows(existing, job.PrintOption, limit: 20)],
                    [.. SelectTrackBatchRows(notFound, job.PrintOption, limit: 20)]));
                break;

            case AggregateJobSnapshotPayload when job.TerminalOutcome == JobTerminalOutcome.Succeeded:
                publish("job.status", new JobStatusEventDto(getSummary(job), "done"));
                break;

            default:
                if (job.ActivityPhase == JobActivityPhase.Searching)
                    publish("job.started", new JobStartedEventDto(getSummary(job)));
                break;
        }

        PublishDiagnosticErrorIfNeeded(job);
    }

    private static bool ShouldPublishSongStateChanged(JobSnapshot song)
        => song.TerminalOutcome != JobTerminalOutcome.Cancelled
            || song.CancellationSource is JobCancellationSource.UserRequestedJob
                or JobCancellationSource.InternalEngine
                or JobCancellationSource.None;

    private void PublishDiagnosticErrorIfNeeded(JobSnapshot job)
    {
        if (job.TerminalOutcome != JobTerminalOutcome.Failed || string.IsNullOrWhiteSpace(job.FailureDetail))
            return;

        var summary = getSummary(job);
        publish("diagnostic.error", new DiagnosticErrorEventDto(
            "job",
            job.FailureMessage ?? "Job failed",
            ExceptionType(job.FailureDetail),
            job.FailureDetail,
            summary,
            job.WorkflowId,
            job.Payload is ExtractJobSnapshotPayload extract ? ExtractionSource(extract) : null));
    }

    private static string? ExtractionSource(ExtractJobSnapshotPayload job)
        => job.InputType;

    private static string ExceptionType(string exceptionDetail)
    {
        var firstLine = exceptionDetail
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', 2)[0];
        var separatorIndex = firstLine.IndexOf(':');
        return separatorIndex > 0 ? firstLine[..separatorIndex] : firstLine;
    }

    private static IEnumerable<SongJobPayloadDto> SelectTrackBatchRows(
        IReadOnlyList<JobSnapshot> songs, PrintOption printOption, int limit = int.MaxValue)
    {
        bool needsFullRows = printOption.HasFlag(PrintOption.Jobs)
            || (printOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
        int effectiveLimit = needsFullRows ? int.MaxValue : limit;
        return songs.Take(effectiveLimit)
            .Where(song => song.Payload is SongJobSnapshotPayload)
            .Select(song => ServerSnapshotMapper.ToSongJobPayloadDto(song));
    }

    private static bool IsNotFoundFailure(JobFailureReason reason)
        => reason is JobFailureReason.NoSearchResults or JobFailureReason.NoMatchingResults;

    private static bool IsUnsuccessfulTerminal(JobSnapshot job)
        => job.LifecycleState == JobLifecycleState.Terminal && !ServerSnapshotMapper.IsSuccessfulJob(job);

    private static SongJobSnapshotPayload SongPayload(JobSnapshot job)
        => job.Payload as SongJobSnapshotPayload
            ?? throw new InvalidOperationException($"Expected song snapshot for job {job.Id}.");
}
