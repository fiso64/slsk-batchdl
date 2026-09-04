using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.Write;

public enum PersistenceMutationPriority
{
    Progress,
    Ordinary,
    Structural,
    Terminal,
}

public abstract record PersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid EntityId,
    long Revision,
    PersistenceMutationPriority Priority)
{
    public long OccurredAtUnixMilliseconds => OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
    public virtual string CoalescingKey => $"{GetType().Name}:{EntityId}";
}

public sealed record JobPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid JobId,
    long Revision,
    PersistenceMutationPriority Priority,
    Guid WorkflowId,
    Guid? ParentJobId,
    Guid? SourceJobId,
    Guid? ResultJobId,
    long DisplayId,
    string Kind,
    string LifecycleState,
    string ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    string TerminalOutcome,
    string SkipReason,
    string CancellationSource,
    string FailureReason,
    string? FailureMessage,
    string? FailureDetail,
    string? ItemName,
    string? QueryText,
    int PayloadSchemaVersion,
    string? PayloadJson,
    Guid? SubmissionId = null,
    string SemanticRole = "Legacy",
    DateTimeOffset? RegisteredAtUtc = null,
    string? SubmissionSpecificationJson = null,
    Guid? RerunOfSubmissionId = null,
    Guid? PreviewId = null,
    string? ArtifactId = null)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, JobId, Revision, Priority);

public sealed record TransferPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid TransferId,
    long Revision,
    PersistenceMutationPriority Priority,
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string State,
    string TerminalOutcome,
    long TotalBytes,
    long TransferredBytes,
    int AttemptCount,
    string FailureReason,
    string? FailureMessage,
    string CancellationSource = "None",
    DateTimeOffset? RequestedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? LastProgressAtUtc = null,
    long? BytesPerSecond = null,
    TransferFileMetadataSnapshot? File = null,
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    IReadOnlyList<TransferAccountingObservation>? AccountingObservations = null)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, TransferId, Revision, Priority);

/// <summary>
/// One cumulative transport-byte observation for an attempt. Revision is the
/// owning transfer revision, which makes replay idempotent independently of the
/// transfer/attempt history projection revisions.
/// </summary>
public sealed record TransferAccountingObservation(
    Guid AttemptId,
    long Revision,
    DateTimeOffset OccurredAtUtc,
    long CumulativeBytes);

public sealed record TransferAttemptPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid AttemptId,
    long Revision,
    PersistenceMutationPriority Priority,
    Guid TransferId,
    int AttemptNumber,
    string Source,
    string State,
    string? SourceUsername,
    string? SourcePath,
    string? OutputPath,
    string FailureReason,
    string? FailureMessage,
    string Direction = "Download",
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    IReadOnlyList<TransferAccountingObservation>? AccountingObservations = null)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, AttemptId, Revision, Priority);

public sealed record SearchResultPersistenceRecord(
    Guid Id,
    long Sequence,
    long Revision,
    string Username,
    string RemoteFilename,
    long SizeBytes,
    int? BitRate,
    int? BitDepth,
    int ResponseFileCount,
    int? SampleRate,
    int? DurationSeconds,
    string Extension,
    int? UploadSpeed,
    bool? HasFreeUploadSlot,
    string? AttributesJson,
    DateTimeOffset ObservedAtUtc,
    int? QueueLength = null,
    string Visibility = "Public");

public sealed record SearchResultsPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid SearchJobId,
    long Revision,
    IReadOnlyList<SearchResultPersistenceRecord> Results)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, SearchJobId, Revision, PersistenceMutationPriority.Ordinary)
{
    public override string CoalescingKey => $"search-results:{SearchJobId}:{Sequence}";
}

public sealed record SearchCompletionPersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid SearchJobId,
    long Revision,
    string Query,
    long ResultCount,
    long LockedFileCount,
    string ResultPersistenceState,
    long ObservedPeerCount = 0)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, SearchJobId, Revision, PersistenceMutationPriority.Terminal);

public sealed record SearchIncompletePersistenceMutation(
    Guid RuntimeId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid SearchJobId,
    long Revision,
    string Reason)
    : PersistenceMutation(RuntimeId, Sequence, OccurredAtUtc, SearchJobId, Revision, PersistenceMutationPriority.Terminal);

public sealed record SearchTerminalPersistenceMutation(
    SearchCompletionPersistenceMutation Completion,
    IReadOnlyList<SearchResultsPersistenceMutation> PendingResultBatches)
    : PersistenceMutation(
        Completion.RuntimeId,
        Completion.Sequence,
        Completion.OccurredAtUtc,
        Completion.SearchJobId,
        Completion.Revision,
        PersistenceMutationPriority.Terminal);

public sealed record TransferTerminalPersistenceMutation(
    TransferPersistenceMutation Transfer,
    TransferAttemptPersistenceMutation? FinalAttempt,
    JobPersistenceMutation? OwningJob)
    : PersistenceMutation(
        Transfer.RuntimeId,
        Transfer.Sequence,
        Transfer.OccurredAtUtc,
        Transfer.TransferId,
        Transfer.Revision,
        PersistenceMutationPriority.Terminal);
