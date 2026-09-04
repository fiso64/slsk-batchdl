using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Persistence;

public sealed class EnginePersistenceAdapter
{
    private readonly Guid runtimeId;
    private readonly IPersistenceMutationSink sink;
    private readonly PersistenceHandoffTracker? handoffs;
    private readonly ConcurrentDictionary<Guid, JobRelationships> relationships = new();
    private readonly ConcurrentDictionary<Guid, TrackedRevision> terminalJobRevisions = new();
    private readonly ConcurrentDictionary<Guid, TrackedRevision> searchCompletionRevisions = new();
    private readonly ConcurrentDictionary<Guid, TransferAttemptPersistenceMutation> pendingTerminalAttempts = new();
    private readonly ConcurrentDictionary<Guid, Guid> transferWorkflowIds = new();
    private readonly ConcurrentDictionary<Guid, AttemptAccountingState> activeAttempts = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> jobStartedAtUtc = new();

    public EnginePersistenceAdapter(Guid runtimeId, IPersistenceMutationSink sink)
        : this(runtimeId, sink, handoffs: null)
    {
    }

    internal EnginePersistenceAdapter(
        Guid runtimeId,
        IPersistenceMutationSink sink,
        PersistenceHandoffTracker? handoffs)
    {
        if (runtimeId == Guid.Empty)
            throw new ArgumentException("A non-empty runtime ID is required.", nameof(runtimeId));
        this.runtimeId = runtimeId;
        this.sink = sink;
        this.handoffs = handoffs;
    }

    internal static IReadOnlySet<Type> HandledChangeTypes { get; } = new HashSet<Type>
    {
        typeof(JobRegisteredChange),
        typeof(JobStateChangedChange),
        typeof(JobActivityChangedChange),
        typeof(JobDiscoveryChangedChange),
        typeof(JobExecutionCompletedChange),
        typeof(JobResultCreatedChange),
        typeof(EngineCompletedChange),
        typeof(WorkflowRetiredChange),
        typeof(JobStatusChange),
        typeof(JobMessageChange),
        typeof(WorkflowMessageChange),
        typeof(DownloadStartedChange),
        typeof(FallbackTransferStartedChange),
        typeof(DownloadProgressedChange),
        typeof(DownloadStateChangedChange),
        typeof(DownloadAttemptFailedChange),
        typeof(TransferCompletedChange),
        typeof(TransferFailedChange),
        typeof(TransferCancelledChange),
        typeof(TransferAttemptStartedChange),
        typeof(TransferAttemptCompletedChange),
        typeof(TransferAttemptFailedChange),
        typeof(TransferAttemptCancelledChange),
        typeof(SearchResultsAddedChange),
        typeof(SearchCompletedChange),
        typeof(TrackBatchResolvedChange),
        typeof(TrackListReadyChange),
        typeof(ListProgressChange),
        typeof(OverallProgressChange),
    };

    public void Attach(DownloadEvents events)
    {
        // This subscription is deliberately registered before EngineStateStore's
        // retirement handler. It establishes persistent ownership before live state
        // becomes unobservable; all mutation writes remain on ChangePublished.
        events.WorkflowRetired += OnWorkflowRetiring;
        events.ChangePublished += OnChange;
    }

    public void Detach(DownloadEvents events)
    {
        events.WorkflowRetired -= OnWorkflowRetiring;
        events.ChangePublished -= OnChange;
    }

    private void OnWorkflowRetiring(WorkflowRetiredChange retired)
        => RetireWorkflow(retired.WorkflowId);

    private void OnChange(CoreChange change)
    {
        switch (change)
        {
            case JobRegisteredChange registered:
                relationships[registered.Job.Id] = new JobRelationships(
                    registered.Job.WorkflowId,
                    registered.ParentJobId,
                    registered.SourceJobId,
                    null);
                EnqueueJobMutation(
                    registered.Job,
                    registered,
                    PersistenceMutationPriority.Structural);
                break;

            case JobStateChangedChange state:
                EnqueueJobMutation(
                    state.Job,
                    state,
                    state.IsTerminal ? PersistenceMutationPriority.Terminal : PersistenceMutationPriority.Ordinary);
                break;

            case JobActivityChangedChange activity:
                EnqueueJobMutation(activity.Job, activity, PersistenceMutationPriority.Ordinary);
                break;

            case JobDiscoveryChangedChange discovery:
                EnqueueJobMutation(discovery.Job, discovery, PersistenceMutationPriority.Ordinary);
                break;

            case JobResultCreatedChange result:
                relationships.AddOrUpdate(
                    result.ExtractJob.Id,
                    _ => new JobRelationships(result.ExtractJob.WorkflowId, null, null, result.ResultJob.Id),
                    (_, current) => current with { ResultJobId = result.ResultJob.Id });
                EnqueueJobMutation(result.ExtractJob, result, PersistenceMutationPriority.Structural);
                EnqueueJobMutation(result.ResultJob, result, PersistenceMutationPriority.Structural);
                break;

            case DownloadStartedChange started:
                sink.TryEnqueue(TransferMutation(started.Transfer, started, PersistenceMutationPriority.Structural, "Started", "None", "None", null));
                break;

            case FallbackTransferStartedChange started:
                sink.TryEnqueue(TransferMutation(started.Transfer, started, PersistenceMutationPriority.Structural, "Started", "None", "None", null));
                break;

            case DownloadProgressedChange progress:
                sink.TryEnqueue(TransferMutation(progress.Transfer, progress, PersistenceMutationPriority.Progress, "InProgress", "None", "None", null));
                break;

            case DownloadStateChangedChange state:
                sink.TryEnqueue(TransferMutation(state.Transfer, state, PersistenceMutationPriority.Ordinary, DurableTransferState(state.State), "None", "None", null));
                break;

            case TransferAttemptStartedChange attempt:
                FlushPendingAttempt(attempt.Transfer.Id);
                activeAttempts[attempt.Transfer.Id] = new AttemptAccountingState(
                    attempt.AttemptId,
                    attempt.Transfer.BytesTransferred,
                    attempt.OccurredAtUtc);
                sink.TryEnqueue(AttemptMutation(attempt, "Started", "None", null));
                break;

            case TransferAttemptCompletedChange attempt:
                pendingTerminalAttempts[attempt.Transfer.Id] = AttemptMutation(attempt, "Completed", "None", null);
                activeAttempts.TryRemove(attempt.Transfer.Id, out _);
                break;

            case TransferAttemptFailedChange attempt:
                pendingTerminalAttempts[attempt.Transfer.Id] = AttemptMutation(
                    attempt,
                    "Failed",
                    TransferFailureReason.PeerFailure.ToString(),
                    attempt.Exception.Message);
                activeAttempts.TryRemove(attempt.Transfer.Id, out _);
                break;

            case TransferAttemptCancelledChange attempt:
                pendingTerminalAttempts[attempt.Transfer.Id] = AttemptMutation(
                    attempt,
                    "Cancelled",
                    attempt.Reason.ToString(),
                    null);
                activeAttempts.TryRemove(attempt.Transfer.Id, out _);
                break;

            case TransferCompletedChange completed:
                EnqueueTerminalTransfer(completed.Transfer, completed, "Completed", "Succeeded", "None", null);
                break;

            case TransferFailedChange failed:
                EnqueueTerminalTransfer(failed.Transfer, failed, "Failed", "Failed", failed.Reason.ToString(), failed.Exception.Message);
                break;

            case TransferCancelledChange cancelled:
                EnqueueTerminalTransfer(
                    cancelled.Transfer,
                    cancelled,
                    "Cancelled",
                    "Cancelled",
                    cancelled.Reason.ToString(),
                    null,
                    cancelled.Reason.ToString());
                break;

            case SearchResultsAddedChange results:
                sink.TryEnqueue(SearchResultsMutation(results));
                break;

            case SearchCompletedChange completed:
                if (relationships.GetValueOrDefault(completed.JobId) is { } searchRelationship)
                {
                    handoffs?.RegisterJob(searchRelationship.WorkflowId, completed.JobId);
                    searchCompletionRevisions[completed.JobId] = new TrackedRevision(
                        searchRelationship.WorkflowId,
                        completed.Revision);
                }
                sink.TryEnqueue(new SearchCompletionPersistenceMutation(
                    runtimeId,
                    completed.Sequence,
                    completed.OccurredAtUtc,
                    completed.JobId,
                    completed.Revision,
                    completed.QueryText,
                    completed.ResultCount,
                    completed.LockedFileCount,
                    "Complete",
                    completed.ObservedPeerCount));
                break;

            case WorkflowRetiredChange:
                // The handoff marker and adapter cleanup run on the earlier typed
                // event so no query can fall between live removal and ownership.
                break;

            case JobExecutionCompletedChange:
            case EngineCompletedChange:
            case JobStatusChange:
            case JobMessageChange:
            case WorkflowMessageChange:
            case DownloadAttemptFailedChange:
            case TrackBatchResolvedChange:
            case TrackListReadyChange:
            case ListProgressChange:
            case OverallProgressChange:
                break;

            default:
                throw new InvalidOperationException($"Core change {change.GetType().FullName} is not classified by persistence.");
        }
    }

    private void EnqueueTerminalTransfer(
        TransferSnapshot transfer,
        CoreChange change,
        string state,
        string outcome,
        string failureReason,
        string? failureMessage,
        string cancellationSource = "None")
    {
        pendingTerminalAttempts.TryRemove(transfer.Id, out var finalAttempt);
        var transferMutation = TransferMutation(
            transfer,
            change,
            PersistenceMutationPriority.Terminal,
            state,
            outcome,
            failureReason,
            failureMessage,
            cancellationSource);
        handoffs?.BeginTransferTerminal(transfer.Id, transfer.Revision);
        bool accepted = sink.TryEnqueue(new TransferTerminalPersistenceMutation(
            transferMutation,
            finalAttempt,
            OwningJob: null));
        if (!accepted)
            handoffs?.FailTransferTerminalAdmission(transfer.Id, transfer.Revision);
    }

    private void FlushPendingAttempt(Guid transferId)
    {
        if (pendingTerminalAttempts.TryRemove(transferId, out var previous))
            sink.TryEnqueue(previous);
    }

    private JobPersistenceMutation JobMutation(
        JobSnapshot job,
        CoreChange change,
        PersistenceMutationPriority priority)
    {
        var relation = relationships.GetValueOrDefault(job.Id)
            ?? new JobRelationships(job.WorkflowId, null, null, null);
        return new JobPersistenceMutation(
            runtimeId,
            change.Sequence,
            change.OccurredAtUtc,
            job.Id,
            job.Revision,
            priority,
            job.WorkflowId,
            relation.ParentJobId,
            relation.SourceJobId,
            relation.ResultJobId,
            job.DisplayId,
            job.Kind.ToString(),
            job.LifecycleState.ToString(),
            job.ActivityPhase.ToString(),
            job.ActivityUntilUtc,
            job.TerminalOutcome.ToString(),
            job.SkipReason.ToString(),
            job.CancellationSource.ToString(),
            job.FailureReason.ToString(),
            job.FailureMessage,
            job.FailureDetail,
            job.ItemName,
            job.QueryText,
            PayloadSchemaVersion: 1,
            PayloadJson(job.Payload),
            job.SubmissionId,
            job.SemanticRole.ToString(),
            job.CreatedAtUtc,
            job.SubmissionSpecificationJson,
            job.RerunOfSubmissionId,
            job.PreviewId,
            job.ArtifactId,
            jobStartedAtUtc.GetValueOrDefault(job.Id));
    }

    private void EnqueueJobMutation(
        JobSnapshot job,
        CoreChange change,
        PersistenceMutationPriority priority)
    {
        handoffs?.RegisterJob(job.WorkflowId, job.Id);
        if (job.LifecycleState != JobLifecycleState.Pending)
            jobStartedAtUtc.TryAdd(job.Id, change.OccurredAtUtc);
        var mutation = JobMutation(job, change, priority);
        if (priority == PersistenceMutationPriority.Terminal)
            terminalJobRevisions[job.Id] = new TrackedRevision(job.WorkflowId, job.Revision);
        sink.TryEnqueue(mutation);
    }

    private static string? PayloadJson(JobSnapshotPayload payload)
    {
        object compact = payload switch
        {
            ExtractJobSnapshotPayload extract => new { extract.Input, extract.InputType },
            SearchJobSnapshotPayload search => new
            {
                search.QueryText,
                search.DefaultFileProjection,
                search.DefaultFolderProjection,
                search.ResultCount,
                search.Revision,
                search.IsComplete,
                search.Definition,
            },
            SongJobSnapshotPayload song => new
            {
                song.Query,
                song.File,
                song.DownloadSource,
                song.ExactTarget,
                song.Definition,
            },
            AlbumJobSnapshotPayload album => new
            {
                album.Query,
                album.ResultCount,
                album.Directory,
                album.Definition,
            },
            RemoteFileJobSnapshotPayload remoteFile => new
            {
                remoteFile.Target,
                remoteFile.OutputPath,
                remoteFile.File,
            },
            RemoteDirectoryJobSnapshotPayload remoteDirectory => new
            {
                remoteDirectory.SourceKind,
                remoteDirectory.DirectorySource,
                remoteDirectory.Directory,
            },
            AggregateJobSnapshotPayload aggregate => new
            {
                aggregate.Query,
                SongCount = aggregate.Songs.Count,
                aggregate.Definition,
            },
            AlbumAggregateJobSnapshotPayload aggregate => new
            {
                aggregate.Query,
                aggregate.AlbumCount,
                aggregate.Definition,
            },
            JobListSnapshotPayload list => new { list.Count },
            RetrieveFolderJobSnapshotPayload retrieve => new
            {
                retrieve.Directory.Username,
                retrieve.Directory.FolderPath,
                retrieve.NewFilesFoundCount,
                retrieve.RetrievalOutcome,
                retrieve.RetrievalCancelled,
            },
            GenericJobSnapshotPayload generic => new { generic.Text },
            _ => throw new InvalidOperationException($"Unsupported job payload {payload.GetType().FullName}."),
        };
        return JsonSerializer.Serialize(compact);
    }

    private TransferPersistenceMutation TransferMutation(
        TransferSnapshot transfer,
        CoreChange change,
        PersistenceMutationPriority priority,
        string state,
        string terminalOutcome,
        string failureReason,
        string? failureMessage,
        string cancellationSource = "None")
    {
        transferWorkflowIds[transfer.Id] = transfer.WorkflowId ?? Guid.Empty;
        return new(
            runtimeId,
            change.Sequence,
            change.OccurredAtUtc,
            transfer.Id,
            transfer.Revision,
            priority,
            transfer.JobId,
            transfer.WorkflowId,
            transfer.Direction.ToString(),
            transfer.Source.ToString(),
            transfer.Username,
            transfer.RemotePath,
            transfer.LocalPath,
            state,
            terminalOutcome,
            transfer.TotalBytes,
            transfer.BytesTransferred,
            transfer.AttemptCount,
            failureReason,
            failureMessage,
            cancellationSource,
            transfer.RequestedAtUtc,
            transfer.StartedAtUtc,
            transfer.LastProgressAtUtc,
            transfer.BytesPerSecond,
            transfer.File,
            AccountingObservations: AccountingObservations(transfer, change));
    }

    private TransferAttemptPersistenceMutation AttemptMutation(
        TransferAttemptStartedChange attempt,
        string state,
        string failureReason,
        string? failureMessage)
        => AttemptMutation(attempt, attempt.AttemptId, attempt.AttemptNumber, attempt.AttemptRevision, attempt.Source.ToString(), attempt.OutputPath, state, failureReason, failureMessage);

    private TransferAttemptPersistenceMutation AttemptMutation(
        TransferAttemptCompletedChange attempt,
        string state,
        string failureReason,
        string? failureMessage)
        => AttemptMutation(attempt, attempt.AttemptId, attempt.AttemptNumber, attempt.AttemptRevision, attempt.Transfer.Source.ToString(), attempt.Transfer.LocalPath, state, failureReason, failureMessage);

    private TransferAttemptPersistenceMutation AttemptMutation(
        TransferAttemptFailedChange attempt,
        string state,
        string failureReason,
        string? failureMessage)
        => AttemptMutation(attempt, attempt.AttemptId, attempt.AttemptNumber, attempt.AttemptRevision, attempt.Transfer.Source.ToString(), attempt.Transfer.LocalPath, state, failureReason, failureMessage);

    private TransferAttemptPersistenceMutation AttemptMutation(
        TransferAttemptCancelledChange attempt,
        string state,
        string failureReason,
        string? failureMessage)
        => AttemptMutation(attempt, attempt.AttemptId, attempt.AttemptNumber, attempt.AttemptRevision, attempt.Transfer.Source.ToString(), attempt.Transfer.LocalPath, state, failureReason, failureMessage);

    private TransferAttemptPersistenceMutation AttemptMutation(
        CoreChange change,
        Guid attemptId,
        int attemptNumber,
        long attemptRevision,
        string source,
        string? outputPath,
        string state,
        string failureReason,
        string? failureMessage)
    {
        var transfer = AttemptTransfer(change);
        return new(
            runtimeId,
            change.Sequence,
            change.OccurredAtUtc,
            attemptId,
            attemptRevision,
            state == "Started" ? PersistenceMutationPriority.Structural : PersistenceMutationPriority.Terminal,
            TransferId(change),
            attemptNumber,
            source,
            state,
            transfer.Username,
            transfer.RemotePath,
            outputPath,
            failureReason,
            failureMessage,
            transfer.Direction.ToString(),
            GroupRef: null,
            GroupDisplayPath: null,
            AccountingObservations: AccountingObservations(transfer, change),
            StartedAtUtc: activeAttempts.GetValueOrDefault(transfer.Id)?.StartedAtUtc);
    }

    private IReadOnlyList<TransferAccountingObservation>? AccountingObservations(
        TransferSnapshot transfer,
        CoreChange change)
    {
        if (!activeAttempts.TryGetValue(transfer.Id, out AttemptAccountingState? attempt))
            return null;
        long cumulative = Math.Max(0, transfer.BytesTransferred - attempt.BaselineBytes);
        return
        [
            new TransferAccountingObservation(
                attempt.AttemptId,
                transfer.Revision,
                change.OccurredAtUtc,
                cumulative),
        ];
    }

    private static Guid TransferId(CoreChange change)
        => change switch
        {
            TransferAttemptStartedChange attempt => attempt.Transfer.Id,
            TransferAttemptCompletedChange attempt => attempt.Transfer.Id,
            TransferAttemptFailedChange attempt => attempt.Transfer.Id,
            TransferAttemptCancelledChange attempt => attempt.Transfer.Id,
            _ => throw new InvalidOperationException("Expected a transfer-attempt change."),
        };

    private static TransferSnapshot AttemptTransfer(CoreChange change)
        => change switch
        {
            TransferAttemptStartedChange attempt => attempt.Transfer,
            TransferAttemptCompletedChange attempt => attempt.Transfer,
            TransferAttemptFailedChange attempt => attempt.Transfer,
            TransferAttemptCancelledChange attempt => attempt.Transfer,
            _ => throw new InvalidOperationException("Expected a transfer-attempt change."),
        };

    private SearchResultsPersistenceMutation SearchResultsMutation(SearchResultsAddedChange change)
        => new(
            runtimeId,
            change.Sequence,
            change.OccurredAtUtc,
            change.JobId,
            change.Revision,
            change.Results.Select(result => new SearchResultPersistenceRecord(
                StableResultId(
                    change.JobId,
                    result.Username,
                    result.Filename,
                    result.Visibility),
                result.Sequence,
                result.Revision,
                result.Username,
                result.Filename,
                result.Size,
                result.BitRate,
                result.BitDepth,
                result.ResponseFileCount,
                result.SampleRate,
                result.Length,
                result.Extension,
                result.UploadSpeed,
                result.HasFreeUploadSlot,
                result.Attributes == null ? null : JsonSerializer.Serialize(result.Attributes.Select(attribute => new
                {
                    Code = attribute.StableCode,
                    Name = attribute.Type,
                    attribute.Value,
                })),
                result.ObservedAtUtc,
                result.QueueLength,
                result.Visibility.ToString())).ToArray());

    private static Guid StableResultId(
        Guid jobId,
        string username,
        string filename,
        SearchResultVisibility visibility = SearchResultVisibility.Public)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{jobId:N}\0{username}\0{filename}\0{visibility}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private void RetireWorkflow(Guid workflowId)
    {
        var jobs = terminalJobRevisions
            .Where(pair => pair.Value.WorkflowId == workflowId)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Revision);
        var searches = searchCompletionRevisions
            .Where(pair => pair.Value.WorkflowId == workflowId)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Revision);
        handoffs?.BeginRetirement(workflowId, jobs, searches);

        foreach (var pair in relationships.Where(pair => pair.Value.WorkflowId == workflowId))
            relationships.TryRemove(pair.Key, out _);

        foreach (Guid jobId in jobs.Keys)
        {
            terminalJobRevisions.TryRemove(jobId, out _);
            jobStartedAtUtc.TryRemove(jobId, out _);
        }
        foreach (Guid jobId in searches.Keys)
            searchCompletionRevisions.TryRemove(jobId, out _);

        foreach (var pair in transferWorkflowIds.Where(pair => pair.Value == workflowId))
        {
            transferWorkflowIds.TryRemove(pair.Key, out _);
            pendingTerminalAttempts.TryRemove(pair.Key, out _);
            activeAttempts.TryRemove(pair.Key, out _);
        }
    }

    internal (int Relationships, int PendingAttempts, int Transfers) RetainedStateCounts
        => (relationships.Count, pendingTerminalAttempts.Count, transferWorkflowIds.Count);

    private static string DurableTransferState(string state)
    {
        if (state.Contains("InProgress", StringComparison.OrdinalIgnoreCase)) return "InProgress";
        if (state.Contains("Initializing", StringComparison.OrdinalIgnoreCase)) return "Initializing";
        if (state.Contains("Queued", StringComparison.OrdinalIgnoreCase)) return "Queued";
        return "Requested";
    }

    private sealed record JobRelationships(
        Guid WorkflowId,
        Guid? ParentJobId,
        Guid? SourceJobId,
        Guid? ResultJobId);

    private sealed record TrackedRevision(Guid WorkflowId, long Revision);

    private sealed record AttemptAccountingState(
        Guid AttemptId,
        long BaselineBytes,
        DateTimeOffset StartedAtUtc);
}
