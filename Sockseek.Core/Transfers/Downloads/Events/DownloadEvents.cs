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
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<Guid, long> jobRevisions = new();
    private readonly ConcurrentDictionary<Guid, long> transferRevisions = new();
    private readonly ConcurrentDictionary<Guid, long> attemptRevisions = new();
    private readonly ConcurrentDictionary<Guid, object> transferGates = new();
    private readonly ConcurrentDictionary<Guid, byte> terminalTransfers = new();

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
    public event Action<FallbackTransferStartedChange>? FallbackTransferStarted;
    public event Action<DownloadProgressedChange>? DownloadProgress;
    public event Action<DownloadStateChangedChange>? DownloadStateChanged;
    public event Action<DownloadAttemptFailedChange>? DownloadAttemptFailed;
    public event Action<TransferCompletedChange>? TransferCompleted;
    public event Action<TransferFailedChange>? TransferFailed;
    public event Action<TransferCancelledChange>? TransferCancelled;
    public event Action<TransferAttemptStartedChange>? TransferAttemptStarted;
    public event Action<TransferAttemptCompletedChange>? TransferAttemptCompleted;
    public event Action<TransferAttemptFailedChange>? TransferAttemptFailed;
    public event Action<TransferAttemptCancelledChange>? TransferAttemptCancelled;
    public event Action<SearchResultsAddedChange>? SearchResultsAdded;
    public event Action<SearchCompletedChange>? SearchCompleted;

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

    public DownloadEvents(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ── Internal raise methods (same assembly only) ──────────────────────────
    internal void RaiseJobRegistered(Job job, Guid? parentJobId, Guid? sourceJobId)
        => Publish(new JobRegisteredChange(
            NextSequence(),
            UtcNow(),
            Snapshot(job, incrementRevision: true),
            parentJobId,
            sourceJobId));

    internal void RaiseJobStateChanged(Job job)
        => Publish(new JobStateChangedChange(NextSequence(), UtcNow(), Snapshot(job, incrementRevision: true)));

    internal void RaiseJobActivityChanged(Job job, JobActivityPhase phase, DateTimeOffset? untilUtc)
        => Publish(new JobActivityChangedChange(NextSequence(), UtcNow(), Snapshot(job, incrementRevision: true), phase, untilUtc));

    internal void RaiseJobDiscoveryChanged(Job job)
        => Publish(new JobDiscoveryChangedChange(NextSequence(), UtcNow(), Snapshot(job, incrementRevision: true)));

    internal void RaiseJobExecutionCompleted(Job job)
        => Publish(new JobExecutionCompletedChange(NextSequence(), UtcNow(), Snapshot(job, incrementRevision: true)));

    internal void RaiseJobResultCreated(ExtractJob job, Job result)
        => Publish(new JobResultCreatedChange(NextSequence(), UtcNow(), Snapshot(job, incrementRevision: true), Snapshot(result, incrementRevision: true)));

    internal void RaiseEngineCompleted(JobList queue)
        => Publish(new EngineCompletedChange(NextSequence(), UtcNow(), Snapshot(queue, incrementRevision: true)));


    internal void RaiseJobStatus(Job job, string status)
        => Publish(new JobStatusChange(NextSequence(), UtcNow(), Snapshot(job), status));

    internal void RaiseJobMessage(Job job, LogLevel level, string? source, string message)
        => Publish(new JobMessageChange(NextSequence(), UtcNow(), Snapshot(job), level, source, message));

    internal void RaiseWorkflowMessage(Guid workflowId, LogLevel level, string? source, string message)
        => Publish(new WorkflowMessageChange(NextSequence(), UtcNow(), workflowId, level, source, message));

    internal void RaiseDownloadStarted(Guid transferId, FileDownloadJob song, PeerFileTarget c, string outputPath)
        => PublishNonTerminalTransfer(transferId, () => new DownloadStartedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, c, outputPath, state: "Started", bytesTransferred: song.BytesTransferred, totalBytes: c.Size ?? 0, attemptCount: 0, incrementRevision: true)));

    internal void RaiseFallbackTransferStarted(Guid transferId, SongJob song, string sourceReference, string outputPath)
        => PublishNonTerminalTransfer(transferId, () => new FallbackTransferStartedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, outputPath, "Started", 0, 0, 0, incrementRevision: true)));

    internal void RaiseDownloadProgress(Guid transferId, FileDownloadJob song, PeerFileTarget c, string outputPath, long xfer, long total)
        => PublishNonTerminalTransfer(transferId, () => new DownloadProgressedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, c, outputPath, state: "InProgress", bytesTransferred: xfer, totalBytes: total, attemptCount: 0, incrementRevision: true)));

    internal void RaiseDownloadStateChanged(Guid transferId, FileDownloadJob song, PeerFileTarget c, string outputPath, TransferStates s, long bytesTransferred, long totalBytes)
        => PublishNonTerminalTransfer(transferId, () => new DownloadStateChangedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, c, outputPath, s.ToString(), bytesTransferred, totalBytes, attemptCount: 0, incrementRevision: true)));

    internal void RaiseDownloadAttemptFailed(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget c,
        string transferOutputPath,
        string attemptOutputPath,
        int attempt,
        int maxAttempts,
        Exception ex)
        => PublishNonTerminalTransfer(transferId, () => new DownloadAttemptFailedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, c, transferOutputPath, state: "AttemptFailed", bytesTransferred: song.BytesTransferred, totalBytes: c.Size ?? 0, attemptCount: attempt, incrementRevision: true),
            attemptOutputPath,
            attempt,
            maxAttempts,
            CoreSnapshotFactory.CreateException(ex)));

    internal void RaiseTransferCompleted(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string finalLocalPath,
        long totalBytes,
        int attemptCount)
        => PublishTerminalTransfer(transferId, () => new TransferCompletedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, candidate, finalLocalPath, "Completed", totalBytes, totalBytes, attemptCount, incrementRevision: true),
            finalLocalPath));

    internal void RaiseTransferFailed(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string outputPath,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        TransferFailureReason reason,
        Exception exception)
        => PublishTerminalTransfer(transferId, () => new TransferFailedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, candidate, outputPath, "Failed", bytesTransferred, totalBytes, attemptCount, incrementRevision: true),
            reason,
            CoreSnapshotFactory.CreateException(exception)));

    internal void RaiseTransferCancelled(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string outputPath,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        TransferCancellationReason reason)
        => PublishTerminalTransfer(transferId, () => new TransferCancelledChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, candidate, outputPath, "Cancelled", bytesTransferred, totalBytes, attemptCount, incrementRevision: true),
            reason));

    internal void RaiseFallbackTransferCompleted(
        Guid transferId,
        SongJob song,
        string sourceReference,
        string finalLocalPath,
        long totalBytes,
        int attemptCount)
        => PublishTerminalTransfer(transferId, () => new TransferCompletedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, finalLocalPath, "Completed", totalBytes, totalBytes, attemptCount, incrementRevision: true),
            finalLocalPath));

    internal void RaiseFallbackTransferFailed(
        Guid transferId,
        SongJob song,
        string sourceReference,
        string? outputPath,
        int attemptCount,
        TransferFailureReason reason,
        Exception exception)
        => PublishTerminalTransfer(transferId, () => new TransferFailedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, outputPath, "Failed", 0, 0, attemptCount, incrementRevision: true),
            reason,
            CoreSnapshotFactory.CreateException(exception)));

    internal void RaiseFallbackTransferCancelled(
        Guid transferId,
        SongJob song,
        string sourceReference,
        string? outputPath,
        int attemptCount,
        TransferCancellationReason reason)
        => PublishTerminalTransfer(transferId, () => new TransferCancelledChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, outputPath, "Cancelled", 0, 0, attemptCount, incrementRevision: true),
            reason));

    internal void RaiseTransferAttemptStarted(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath,
        string attemptOutputPath)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptStartedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, candidate, transferOutputPath, "AttemptStarted", song.BytesTransferred, candidate.Size ?? 0, attemptNumber, incrementRevision: true),
            attemptId,
            attemptNumber,
            NextAttemptRevision(attemptId),
            TransferAttemptSource.SoulseekPeer,
            attemptOutputPath));

    internal void RaiseTransferAttemptCompleted(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptCompletedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, candidate, transferOutputPath, "AttemptCompleted", song.BytesTransferred, candidate.Size ?? 0, attemptNumber, incrementRevision: true),
            attemptId,
            attemptNumber,
            NextAttemptRevision(attemptId)));

    internal void RaiseTransferAttemptFailed(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath,
        Exception exception)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptFailedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, candidate, transferOutputPath, "AttemptFailed", song.BytesTransferred, candidate.Size ?? 0, attemptNumber, incrementRevision: true),
            attemptId,
            attemptNumber,
            NextAttemptRevision(attemptId),
            CoreSnapshotFactory.CreateException(exception)));

    internal void RaiseTransferAttemptCancelled(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath,
        TransferCancellationReason reason)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptCancelledChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotTransfer(transferId, song, candidate, transferOutputPath, "AttemptCancelled", song.BytesTransferred, candidate.Size ?? 0, attemptNumber, incrementRevision: true),
            attemptId,
            attemptNumber,
            NextAttemptRevision(attemptId),
            reason));

    internal void RaiseFallbackTransferAttemptStarted(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptStartedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, outputPath, "AttemptStarted", 0, 0, 1, incrementRevision: true),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(attemptId),
            Source: TransferAttemptSource.Fallback,
            OutputPath: outputPath));

    internal void RaiseFallbackTransferAttemptCompleted(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptCompletedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, outputPath, "AttemptCompleted", 0, 0, 1, incrementRevision: true),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(attemptId)));

    internal void RaiseFallbackTransferAttemptFailed(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath,
        Exception exception)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptFailedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, outputPath, "AttemptFailed", 0, 0, 1, incrementRevision: true),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(attemptId),
            Exception: CoreSnapshotFactory.CreateException(exception)));

    internal void RaiseFallbackTransferAttemptCancelled(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath,
        TransferCancellationReason reason)
        => PublishNonTerminalTransfer(transferId, () => new TransferAttemptCancelledChange(
            NextSequence(),
            UtcNow(),
            Snapshot(song),
            SnapshotFallbackTransfer(transferId, song, sourceReference, outputPath, "AttemptCancelled", 0, 0, 1, incrementRevision: true),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(attemptId),
            Reason: reason));

    internal void RaiseTrackBatchResolved(Job job, IReadOnlyList<SongJob> pending, IReadOnlyList<SongJob> existing, IReadOnlyList<SongJob> notFound)
        => Publish(new TrackBatchResolvedChange(
            NextSequence(),
            UtcNow(),
            Snapshot(job),
            SnapshotSongs(pending),
            SnapshotSongs(existing),
            SnapshotSongs(notFound)));

    internal void RaiseTrackListReady(IEnumerable<SongJob> songs)
        => Publish(new TrackListReadyChange(NextSequence(), UtcNow(), SnapshotSongs(songs)));

    internal void RaiseListProgress(JobList list, int dl, int fl, int total)
        => Publish(new ListProgressChange(NextSequence(), UtcNow(), Snapshot(list, incrementRevision: true), dl, fl, total));

    internal void RaiseOverallProgress(int dl, int fl, int total)
        => Publish(new OverallProgressChange(NextSequence(), UtcNow(), dl, fl, total));

    internal void RaiseSearchChange(CoreChange change)
    {
        if (change is not (SearchResultsAddedChange or SearchCompletedChange))
            throw new ArgumentException($"Unsupported search change {change.GetType().Name}.", nameof(change));
        Publish(change);
    }

    private long NextSequence() => CoreChangeSequencer.Next();

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow();

    private long NextAttemptRevision(Guid attemptId)
        => attemptRevisions.AddOrUpdate(attemptId, 1, static (_, current) => current + 1);

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
        FileDownloadJob song,
        PeerFileTarget candidate,
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

    private TransferSnapshot SnapshotFallbackTransfer(
        Guid transferId,
        SongJob song,
        string sourceReference,
        string? outputPath,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        bool incrementRevision)
    {
        long revision = incrementRevision
            ? transferRevisions.AddOrUpdate(transferId, 1, static (_, current) => current + 1)
            : transferRevisions.GetOrAdd(transferId, 0);

        return CoreSnapshotFactory.CreateFallbackTransfer(
            transferId,
            song,
            sourceReference,
            outputPath,
            revision,
            state,
            bytesTransferred,
            totalBytes,
            attemptCount);
    }

    private void PublishNonTerminalTransfer(Guid transferId, Func<CoreChange> changeFactory)
    {
        lock (transferGates.GetOrAdd(transferId, static _ => new object()))
        {
            if (terminalTransfers.ContainsKey(transferId))
                return;

            Publish(changeFactory());
        }
    }

    private void PublishTerminalTransfer(Guid transferId, Func<CoreChange> changeFactory)
    {
        lock (transferGates.GetOrAdd(transferId, static _ => new object()))
        {
            if (!terminalTransfers.TryAdd(transferId, 0))
                throw new InvalidOperationException($"Transfer {transferId} already has a terminal outcome.");

            Publish(changeFactory());
        }
    }

    private void Publish(CoreChange change)
    {
        switch (change)
        {
            case JobRegisteredChange specific:
                InvokeObservers(JobRegistered, specific, nameof(JobRegistered));
                break;
            case JobStateChangedChange specific:
                InvokeObservers(JobStateChanged, specific, nameof(JobStateChanged));
                break;
            case JobActivityChangedChange specific:
                InvokeObservers(JobActivityChanged, specific, nameof(JobActivityChanged));
                break;
            case JobDiscoveryChangedChange specific:
                InvokeObservers(JobDiscoveryChanged, specific, nameof(JobDiscoveryChanged));
                break;
            case JobExecutionCompletedChange specific:
                InvokeObservers(JobExecutionCompleted, specific, nameof(JobExecutionCompleted));
                break;
            case JobResultCreatedChange specific:
                InvokeObservers(JobResultCreated, specific, nameof(JobResultCreated));
                break;
            case EngineCompletedChange specific:
                InvokeObservers(EngineCompleted, specific, nameof(EngineCompleted));
                break;
            case JobStatusChange specific:
                InvokeObservers(JobStatus, specific, nameof(JobStatus));
                break;
            case JobMessageChange specific:
                InvokeObservers(JobMessage, specific, nameof(JobMessage));
                break;
            case WorkflowMessageChange specific:
                InvokeObservers(WorkflowMessage, specific, nameof(WorkflowMessage));
                break;
            case DownloadStartedChange specific:
                InvokeObservers(DownloadStarted, specific, nameof(DownloadStarted));
                break;
            case FallbackTransferStartedChange specific:
                InvokeObservers(FallbackTransferStarted, specific, nameof(FallbackTransferStarted));
                break;
            case DownloadProgressedChange specific:
                InvokeObservers(DownloadProgress, specific, nameof(DownloadProgress));
                break;
            case DownloadStateChangedChange specific:
                InvokeObservers(DownloadStateChanged, specific, nameof(DownloadStateChanged));
                break;
            case DownloadAttemptFailedChange specific:
                InvokeObservers(DownloadAttemptFailed, specific, nameof(DownloadAttemptFailed));
                break;
            case TransferCompletedChange specific:
                InvokeObservers(TransferCompleted, specific, nameof(TransferCompleted));
                break;
            case TransferFailedChange specific:
                InvokeObservers(TransferFailed, specific, nameof(TransferFailed));
                break;
            case TransferCancelledChange specific:
                InvokeObservers(TransferCancelled, specific, nameof(TransferCancelled));
                break;
            case TransferAttemptStartedChange specific:
                InvokeObservers(TransferAttemptStarted, specific, nameof(TransferAttemptStarted));
                break;
            case TransferAttemptCompletedChange specific:
                InvokeObservers(TransferAttemptCompleted, specific, nameof(TransferAttemptCompleted));
                break;
            case TransferAttemptFailedChange specific:
                InvokeObservers(TransferAttemptFailed, specific, nameof(TransferAttemptFailed));
                break;
            case TransferAttemptCancelledChange specific:
                InvokeObservers(TransferAttemptCancelled, specific, nameof(TransferAttemptCancelled));
                break;
            case SearchResultsAddedChange specific:
                InvokeObservers(SearchResultsAdded, specific, nameof(SearchResultsAdded));
                break;
            case SearchCompletedChange specific:
                InvokeObservers(SearchCompleted, specific, nameof(SearchCompleted));
                break;
            case TrackBatchResolvedChange specific:
                InvokeObservers(TrackBatchResolved, specific, nameof(TrackBatchResolved));
                break;
            case TrackListReadyChange specific:
                InvokeObservers(TrackListReady, specific, nameof(TrackListReady));
                break;
            case ListProgressChange specific:
                InvokeObservers(ListProgress, specific, nameof(ListProgress));
                break;
            case OverallProgressChange specific:
                InvokeObservers(OverallProgress, specific, nameof(OverallProgress));
                break;
        }

        InvokeObservers(ChangePublished, change, nameof(ChangePublished));
    }

    private static void InvokeObservers<T>(Action<T>? observers, T value, string eventName)
    {
        if (observers == null)
            return;

        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try
            {
                observer(value);
            }
            catch (Exception ex)
            {
                try
                {
                    SockseekLog.Core.Error(ex, $"Observer for {eventName} failed");
                }
                catch
                {
                    // Observational failures, including logging failures, never affect domain work.
                }
            }
        }
    }
}
