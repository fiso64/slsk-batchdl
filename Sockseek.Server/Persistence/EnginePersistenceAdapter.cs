using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Persistence;

public sealed class EnginePersistenceAdapter
{
    private readonly Guid runtimeId;
    private readonly IPersistenceMutationSink sink;
    private readonly ConcurrentDictionary<Guid, JobRelationships> relationships = new();
    private readonly ConcurrentDictionary<Guid, TransferAttemptPersistenceMutation> pendingTerminalAttempts = new();

    public EnginePersistenceAdapter(Guid runtimeId, IPersistenceMutationSink sink)
    {
        if (runtimeId == Guid.Empty)
            throw new ArgumentException("A non-empty runtime ID is required.", nameof(runtimeId));
        this.runtimeId = runtimeId;
        this.sink = sink;
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

    public void Attach(DownloadEvents events) => events.ChangePublished += OnChange;

    public void Detach(DownloadEvents events) => events.ChangePublished -= OnChange;

    private void OnChange(CoreChange change)
    {
        switch (change)
        {
            case JobRegisteredChange registered:
                relationships[registered.Job.Id] = new JobRelationships(registered.ParentJobId, registered.SourceJobId, null);
                sink.TryEnqueue(JobMutation(registered.Job, registered, PersistenceMutationPriority.Structural));
                break;

            case JobStateChangedChange state:
                sink.TryEnqueue(JobMutation(
                    state.Job,
                    state,
                    state.IsTerminal ? PersistenceMutationPriority.Terminal : PersistenceMutationPriority.Ordinary));
                break;

            case JobActivityChangedChange activity:
                sink.TryEnqueue(JobMutation(activity.Job, activity, PersistenceMutationPriority.Ordinary));
                break;

            case JobDiscoveryChangedChange discovery:
                sink.TryEnqueue(JobMutation(discovery.Job, discovery, PersistenceMutationPriority.Ordinary));
                break;

            case JobResultCreatedChange result:
                relationships.AddOrUpdate(
                    result.ExtractJob.Id,
                    _ => new JobRelationships(null, null, result.ResultJob.Id),
                    (_, current) => current with { ResultJobId = result.ResultJob.Id });
                sink.TryEnqueue(JobMutation(result.ExtractJob, result, PersistenceMutationPriority.Structural));
                sink.TryEnqueue(JobMutation(result.ResultJob, result, PersistenceMutationPriority.Structural));
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
                sink.TryEnqueue(AttemptMutation(attempt, "Started", "None", null));
                break;

            case TransferAttemptCompletedChange attempt:
                pendingTerminalAttempts[attempt.Transfer.Id] = AttemptMutation(attempt, "Completed", "None", null);
                break;

            case TransferAttemptFailedChange attempt:
                pendingTerminalAttempts[attempt.Transfer.Id] = AttemptMutation(
                    attempt,
                    "Failed",
                    "AttemptFailed",
                    attempt.Exception.Message);
                break;

            case TransferAttemptCancelledChange attempt:
                pendingTerminalAttempts[attempt.Transfer.Id] = AttemptMutation(
                    attempt,
                    "Cancelled",
                    attempt.Reason.ToString(),
                    null);
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
                sink.TryEnqueue(new SearchCompletionPersistenceMutation(
                    runtimeId,
                    completed.Sequence,
                    completed.OccurredAtUtc,
                    completed.JobId,
                    completed.Revision,
                    completed.QueryText,
                    completed.ResultCount,
                    completed.LockedFileCount,
                    "Complete"));
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
        sink.TryEnqueue(new TransferTerminalPersistenceMutation(transferMutation, finalAttempt, OwningJob: null));
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
        var relation = relationships.GetValueOrDefault(job.Id) ?? new JobRelationships(null, null, null);
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
            PayloadJson(job.Payload));
    }

    private static string? PayloadJson(JobSnapshotPayload payload)
    {
        object compact = payload switch
        {
            ExtractJobSnapshotPayload extract => new { extract.Input, extract.InputType, extract.AutoProcessResult },
            SearchJobSnapshotPayload search => new
            {
                search.QueryText,
                search.DefaultFileProjection,
                search.DefaultFolderProjection,
                search.ResultCount,
                search.Revision,
                search.IsComplete,
            },
            SongJobSnapshotPayload song => new
            {
                song.Query,
                song.File,
                song.DownloadSource,
                song.ExactTarget,
            },
            AlbumJobSnapshotPayload album => new { album.Query, album.ResultCount, album.Directory },
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
                remoteDirectory.ResolvedPlanSource,
                remoteDirectory.ResolvedDirectory,
                remoteDirectory.ActivePlan,
                remoteDirectory.Directory,
            },
            AggregateJobSnapshotPayload aggregate => new { aggregate.Query, SongCount = aggregate.Songs.Count },
            AlbumAggregateJobSnapshotPayload aggregate => new { aggregate.Query, aggregate.AlbumCount },
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
        => new(
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
            cancellationSource);

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
            failureMessage);
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
                StableResultId(change.JobId, result.Username, result.Filename),
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
                result.ObservedAtUtc)).ToArray());

    private static Guid StableResultId(Guid jobId, string username, string filename)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{jobId:N}\0{username}\0{filename}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string DurableTransferState(string state)
    {
        if (state.Contains("InProgress", StringComparison.OrdinalIgnoreCase)) return "InProgress";
        if (state.Contains("Initializing", StringComparison.OrdinalIgnoreCase)) return "Initializing";
        if (state.Contains("Queued", StringComparison.OrdinalIgnoreCase)) return "Queued";
        return "Requested";
    }

    private sealed record JobRelationships(Guid? ParentJobId, Guid? SourceJobId, Guid? ResultJobId);
}
