using Soulseek;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;
using System.Collections.Concurrent;

namespace Sockseek.Core;

/// <summary>
/// Multicast event bus for download workflows. Public subscribers receive immutable
/// Sockseek-owned change records captured synchronously by the publisher.
/// </summary>
public class DownloadEvents
{
    private readonly ConcurrentDictionary<Guid, long> jobRevisions = new();
    private readonly ConcurrentDictionary<Guid, long> transferRevisions = new();

    // ── Graph / lifecycle ───────────────────────────────────────────────────
    public event Action<JobRegisteredChange>? JobRegistered;
    public event Action<JobStateChangedChange>? JobStateChanged;
    public event Action<JobActivityChangedChange>? JobActivityChanged;
    /// <summary>
    /// Fired when a download job's discovery snapshot changes, such as raw search result
    /// or locked-file counts. Generic search-service events live in <see cref="SearchEvents"/>.
    /// </summary>
    public event Action<JobDiscoveryChangedChange>? JobDiscoveryChanged;
    // Fired when a job's own execution path is finished.
    // For ExtractJob this is raised immediately after the result job has been produced,
    // not after any optional automatic processing of that result.
    public event Action<JobExecutionCompletedChange>? JobExecutionCompleted;
    // Fired when an ExtractJob produces its semantic output job (possibly after upgrade
    // transforms). This happens at the same moment the ExtractJob itself completes.
    public event Action<JobResultCreatedChange>? JobResultCreated;
    public event Action<EngineCompletedChange>? EngineCompleted;

    // Fired for transient, human-readable status updates that don't warrant a formal state change
    // (e.g. "deleting files", "moving").
    public event Action<JobStatusChange>? JobStatus;

    // Fired for job-scoped log messages that should be rendered with the same prefix/color policy
    // as other job activity.
    public event Action<JobMessageChange>? JobMessage;
    // Fired for workflow-scoped messages in the jobs category that should not be attributed
    // to the first job that happened to discover the condition.
    public event Action<WorkflowMessageChange>? WorkflowMessage;

    // ── Download ─────────────────────────────────────────────────────────────
    public event Action<DownloadStartedChange>? DownloadStarted;
    public event Action<DownloadProgressedChange>? DownloadProgress;
    public event Action<DownloadStateChangedChange>? DownloadStateChanged;
    public event Action<DownloadAttemptFailedChange>? DownloadAttemptFailed;

    // ── List / overall ───────────────────────────────────────────────────────
    // Fired when a batch of songs has been resolved into:
    // - tracks still pending download
    // - tracks already satisfied by skip/existing logic
    // - tracks skipped because they were not found in a prior run
    // The owner job carries any rendering context (for example PrintOption).
    public event Action<TrackBatchResolvedChange>? TrackBatchResolved;
    public event Action<TrackListReadyChange>? TrackListReady;
    public event Action<ListProgressChange>? ListProgress;
    public event Action<OverallProgressChange>? OverallProgress;

    public event Action<CoreChange>? ChangePublished;

    // ── Internal raise methods (same assembly only) ──────────────────────────
    internal void RaiseJobRegistered(Job job, Job? parent)
        => Publish(new JobRegisteredChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job, incrementRevision: true), parent == null ? null : Snapshot(parent)));

    internal void RaiseJobStateChanged(Job job)
        => Publish(new JobStateChangedChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job, incrementRevision: true)));

    internal void RaiseJobActivityChanged(Job job, JobActivityPhase phase, DateTimeOffset? untilUtc)
        => Publish(new JobActivityChangedChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job, incrementRevision: true), phase, untilUtc));

    internal void RaiseJobDiscoveryChanged(Job job)
        => Publish(new JobDiscoveryChangedChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job, incrementRevision: true)));

    internal void RaiseJobExecutionCompleted(Job job)
        => Publish(new JobExecutionCompletedChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job, incrementRevision: true)));

    internal void RaiseJobResultCreated(ExtractJob job, Job result)
        => Publish(new JobResultCreatedChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job, incrementRevision: true), Snapshot(result, incrementRevision: true)));

    internal void RaiseEngineCompleted(JobList queue)
        => Publish(new EngineCompletedChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(queue, incrementRevision: true)));


    internal void RaiseJobStatus(Job job, string status)
        => Publish(new JobStatusChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job), status));

    internal void RaiseJobMessage(Job job, LogLevel level, string? source, string message)
        => Publish(new JobMessageChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(job), level, source, message));

    internal void RaiseWorkflowMessage(Guid workflowId, LogLevel level, string? source, string message)
        => Publish(new WorkflowMessageChange(NextSequence(), DateTimeOffset.UtcNow, workflowId, level, source, message));

    internal void RaiseDownloadStarted(Guid transferId, SongJob song, FileCandidate c, string outputPath)
        => Publish(new DownloadStartedChange(
            NextSequence(),
            DateTimeOffset.UtcNow,
            Snapshot(song, incrementRevision: true),
            SnapshotTransfer(transferId, song, c, outputPath, state: "Started", bytesTransferred: song.BytesTransferred, totalBytes: c.File.Size > 0 ? c.File.Size : 0, attemptCount: 0, incrementRevision: true)));

    internal void RaiseDownloadProgress(Guid transferId, SongJob song, FileCandidate c, string outputPath, long xfer, long total)
        => Publish(new DownloadProgressedChange(
            NextSequence(),
            DateTimeOffset.UtcNow,
            Snapshot(song, incrementRevision: true),
            SnapshotTransfer(transferId, song, c, outputPath, state: "InProgress", bytesTransferred: xfer, totalBytes: total, attemptCount: 0, incrementRevision: true)));

    internal void RaiseDownloadStateChanged(Guid transferId, SongJob song, FileCandidate c, string outputPath, TransferStates s, long bytesTransferred, long totalBytes)
        => Publish(new DownloadStateChangedChange(
            NextSequence(),
            DateTimeOffset.UtcNow,
            Snapshot(song, incrementRevision: true),
            SnapshotTransfer(transferId, song, c, outputPath, s.ToString(), bytesTransferred, totalBytes, attemptCount: 0, incrementRevision: true)));

    internal void RaiseDownloadAttemptFailed(
        Guid transferId,
        SongJob song,
        FileCandidate c,
        string transferOutputPath,
        string attemptOutputPath,
        int attempt,
        int maxAttempts,
        Exception ex)
        => Publish(new DownloadAttemptFailedChange(
            NextSequence(),
            DateTimeOffset.UtcNow,
            Snapshot(song, incrementRevision: true),
            SnapshotTransfer(transferId, song, c, transferOutputPath, state: "AttemptFailed", bytesTransferred: song.BytesTransferred, totalBytes: c.File.Size > 0 ? c.File.Size : 0, attemptCount: attempt, incrementRevision: true),
            attemptOutputPath,
            attempt,
            maxAttempts,
            CoreSnapshotFactory.CreateException(ex)));

    internal void RaiseTrackBatchResolved(Job job, IReadOnlyList<SongJob> pending, IReadOnlyList<SongJob> existing, IReadOnlyList<SongJob> notFound)
        => Publish(new TrackBatchResolvedChange(
            NextSequence(),
            DateTimeOffset.UtcNow,
            Snapshot(job),
            SnapshotSongs(pending),
            SnapshotSongs(existing),
            SnapshotSongs(notFound)));

    internal void RaiseTrackListReady(IEnumerable<SongJob> songs)
        => Publish(new TrackListReadyChange(NextSequence(), DateTimeOffset.UtcNow, SnapshotSongs(songs)));

    internal void RaiseListProgress(JobList list, int dl, int fl, int total)
        => Publish(new ListProgressChange(NextSequence(), DateTimeOffset.UtcNow, Snapshot(list, incrementRevision: true), dl, fl, total));

    internal void RaiseOverallProgress(int dl, int fl, int total)
        => Publish(new OverallProgressChange(NextSequence(), DateTimeOffset.UtcNow, dl, fl, total));

    private long NextSequence() => CoreChangeSequencer.Next();

    private JobSnapshot Snapshot(Job job, bool incrementRevision = false)
    {
        long revision = incrementRevision
            ? jobRevisions.AddOrUpdate(job.Id, 1, static (_, current) => current + 1)
            : jobRevisions.GetOrAdd(job.Id, 0);

        return CoreSnapshotFactory.CreateJob(job, revision);
    }

    private IReadOnlyList<JobSnapshot> SnapshotSongs(IEnumerable<SongJob> songs)
        => SnapshotCollections.Freeze(songs.Select(song => Snapshot(song)));

    private TransferSnapshot SnapshotTransfer(
        Guid transferId,
        SongJob song,
        FileCandidate candidate,
        string outputPath,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        bool incrementRevision)
    {
        long revision = incrementRevision
            ? transferRevisions.AddOrUpdate(transferId, 1, static (_, current) => current + 1)
            : transferRevisions.GetOrAdd(transferId, 0);

        return CoreSnapshotFactory.CreateDownloadTransfer(
            transferId,
            song,
            candidate,
            outputPath,
            revision,
            state,
            bytesTransferred,
            totalBytes,
            attemptCount);
    }

    private void Publish(CoreChange change)
    {
        switch (change)
        {
            case JobRegisteredChange specific:
                JobRegistered?.Invoke(specific);
                break;
            case JobStateChangedChange specific:
                JobStateChanged?.Invoke(specific);
                break;
            case JobActivityChangedChange specific:
                JobActivityChanged?.Invoke(specific);
                break;
            case JobDiscoveryChangedChange specific:
                JobDiscoveryChanged?.Invoke(specific);
                break;
            case JobExecutionCompletedChange specific:
                JobExecutionCompleted?.Invoke(specific);
                break;
            case JobResultCreatedChange specific:
                JobResultCreated?.Invoke(specific);
                break;
            case EngineCompletedChange specific:
                EngineCompleted?.Invoke(specific);
                break;
            case JobStatusChange specific:
                JobStatus?.Invoke(specific);
                break;
            case JobMessageChange specific:
                JobMessage?.Invoke(specific);
                break;
            case WorkflowMessageChange specific:
                WorkflowMessage?.Invoke(specific);
                break;
            case DownloadStartedChange specific:
                DownloadStarted?.Invoke(specific);
                break;
            case DownloadProgressedChange specific:
                DownloadProgress?.Invoke(specific);
                break;
            case DownloadStateChangedChange specific:
                DownloadStateChanged?.Invoke(specific);
                break;
            case DownloadAttemptFailedChange specific:
                DownloadAttemptFailed?.Invoke(specific);
                break;
            case TrackBatchResolvedChange specific:
                TrackBatchResolved?.Invoke(specific);
                break;
            case TrackListReadyChange specific:
                TrackListReady?.Invoke(specific);
                break;
            case ListProgressChange specific:
                ListProgress?.Invoke(specific);
                break;
            case OverallProgressChange specific:
                OverallProgress?.Invoke(specific);
                break;
        }

        ChangePublished?.Invoke(change);
    }
}
