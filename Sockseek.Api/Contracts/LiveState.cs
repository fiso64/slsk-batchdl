using System.Text.Json.Serialization;
using Sockseek.Core;
using Sockseek.Core.Sharing;

namespace Sockseek.Api;

/// <summary>The live replication protocol implemented by this API.</summary>
public static class LiveProtocol
{
    public const int Version = 4;
}

[JsonConverter(typeof(JsonStringEnumConverter<StateStreamScopeKind>))]
public enum StateStreamScopeKind
{
    [JsonStringEnumMemberName("daemon")]
    Daemon,
    [JsonStringEnumMemberName("workflow")]
    Workflow,
}

/// <summary>A recoverable live stream. WorkflowId is set only for workflow streams.</summary>
public sealed record StateStreamScopeDto(
    StateStreamScopeKind Kind,
    Guid? WorkflowId = null)
{
    public static StateStreamScopeDto Daemon { get; } = new(StateStreamScopeKind.Daemon);

    public static StateStreamScopeDto Workflow(Guid workflowId)
        => new(StateStreamScopeKind.Workflow, workflowId);

    public void Validate()
    {
        if (Kind == StateStreamScopeKind.Daemon && WorkflowId != null)
            throw new ArgumentException("A daemon stream scope cannot contain a workflow id.");
        if (Kind == StateStreamScopeKind.Workflow && WorkflowId == null)
            throw new ArgumentException("A workflow stream scope requires a workflow id.");
    }
}

/// <summary>
/// A stream cursor. Epoch changes on every daemon process start; Sequence is scoped
/// independently to the daemon stream or to one workflow stream.
/// </summary>
public sealed record StateStreamPositionDto(Guid Epoch, long Sequence);

/// <summary>Small daemon state replicated to daemon-wide monitors.</summary>
public sealed record DaemonStateDto(
    long Revision,
    SoulseekClientStatusDto SoulseekClient,
    int RestartCount,
    DateTimeOffset? SearchRateLimitResetsAtUtc,
    SharingStateDto Sharing,
    UploadRuntimeStateDto Uploads);

[JsonConverter(typeof(JsonStringEnumConverter<SharingHealthState>))]
public enum SharingHealthState
{
    Disabled,
    Starting,
    Ready,
    Degraded,
}

public sealed record ShareCatalogStateDto(
    Guid? GenerationId,
    long DirectoryCount,
    long FileCount,
    long TotalBytes,
    bool BrowseAvailable,
    long? BrowseArtifactBytes,
    DateTimeOffset? PublishedAtUtc);

public sealed record ShareScanErrorSampleDto(
    string Code,
    string RelativePath,
    string Message);

public sealed record ShareScanStateDto(
    Guid ScanId,
    long Revision,
    ShareScanPhase Phase,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long DirectoriesDiscovered,
    long FilesDiscovered,
    long BytesDiscovered,
    int ErrorCount,
    IReadOnlyList<ShareScanErrorSampleDto> ErrorSamples,
    IReadOnlyList<ResourceActionDto> AvailableActions);

public sealed record SharingStateDto(
    SharingHealthState State,
    string? Reason,
    IReadOnlyList<string> Aliases,
    int BlockedUsernameCount,
    int BlockedIpAddressCount,
    ShareCatalogStateDto Catalog,
    ShareScanStateDto? ActiveScan,
    ShareScanStateDto? LastScan);

public sealed record UploadRuntimeStateDto(
    SharingHealthState State,
    string? Reason,
    bool AcceptingUploads,
    int Slots,
    int ActiveSlots,
    int QueuedFiles,
    long QueuedBytes,
    long QueueRevision,
    int? SpeedLimitKiBPerSecond);

/// <summary>Fields that identify and label a job and normally remain stable.</summary>
public sealed record JobDisplayFieldsDto(
    int DisplayId,
    Guid WorkflowId,
    ServerJobKind Kind,
    string? ItemName,
    string? QueryText,
    IReadOnlyList<string> AppliedAutoProfiles,
    PrintOption PrintOption);

/// <summary>
/// A cohesive replacement for job lifecycle state. Nullable failure and timing fields
/// are cleared by sending this component with those fields set to null.
/// </summary>
public sealed record JobLifecycleFieldsDto(
    ServerJobLifecycleState LifecycleState,
    ServerJobActivityPhase ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    ServerJobTerminalOutcome TerminalOutcome,
    ServerJobSkipReason SkipReason,
    ServerJobFailureReason? FailureReason,
    string? FailureMessage,
    string? FailureDetail,
    ServerJobCancellationSource CancellationSource,
    IReadOnlyList<ResourceActionDto> AvailableActions);

/// <summary>A cohesive replacement for current discovery counters.</summary>
public sealed record JobDiscoveryFieldsDto(
    int? RawResultCount,
    int? LockedFileCount);

/// <summary>A cohesive replacement for job graph and provenance relationships.</summary>
public sealed record JobRelationshipFieldsDto(
    Guid? ParentJobId,
    Guid? ResultJobId,
    Guid? SourceJobId);

/// <summary>A complete replicated job row.</summary>
public sealed record JobStateDto(
    Guid JobId,
    long Revision,
    JobDisplayFieldsDto Display,
    JobLifecycleFieldsDto Lifecycle,
    JobDiscoveryFieldsDto Discovery,
    JobRelationshipFieldsDto Relationships)
{
    public static JobStateDto FromSummary(JobSummaryDto summary, long revision)
        => new(
            summary.JobId,
            revision,
            new JobDisplayFieldsDto(
                summary.DisplayId,
                summary.WorkflowId,
                summary.Kind,
                summary.ItemName,
                summary.QueryText,
                summary.AppliedAutoProfiles,
                summary.PrintOption),
            new JobLifecycleFieldsDto(
                summary.LifecycleState,
                summary.ActivityPhase,
                summary.ActivityUntilUtc,
                summary.TerminalOutcome,
                summary.SkipReason,
                summary.FailureReason,
                summary.FailureMessage,
                summary.FailureDetail,
                summary.CancellationSource,
                summary.AvailableActions),
            new JobDiscoveryFieldsDto(
                summary.DiscoveryRawResultCount,
                summary.DiscoveryLockedFileCount),
            new JobRelationshipFieldsDto(
                summary.ParentJobId,
                summary.ResultJobId,
                summary.SourceJobId));

    public JobSummaryDto ToSummary()
        => new(
            JobId,
            Display.DisplayId,
            Display.WorkflowId,
            Display.Kind,
            Lifecycle.LifecycleState,
            Lifecycle.ActivityPhase,
            Lifecycle.ActivityUntilUtc,
            Lifecycle.TerminalOutcome,
            Lifecycle.SkipReason,
            Display.ItemName,
            Display.QueryText,
            Lifecycle.FailureReason,
            Lifecycle.FailureMessage,
            Relationships.ParentJobId,
            Relationships.ResultJobId,
            Relationships.SourceJobId,
            Discovery.RawResultCount,
            Discovery.LockedFileCount,
            Display.AppliedAutoProfiles,
            Lifecycle.AvailableActions,
            Lifecycle.FailureDetail,
            Lifecycle.CancellationSource,
            Display.PrintOption);
}

/// <summary>
/// A new job uses Added. Existing jobs replace only the supplied cohesive components.
/// Revision is the resulting entity revision.
/// </summary>
public sealed record JobDeltaDto(
    Guid JobId,
    long Revision,
    JobStateDto? Added = null,
    JobDisplayFieldsDto? Display = null,
    JobLifecycleFieldsDto? Lifecycle = null,
    JobDiscoveryFieldsDto? Discovery = null,
    JobRelationshipFieldsDto? Relationships = null);

/// <summary>A complete small workflow row with an API-owned aggregate revision.</summary>
public sealed record WorkflowStateDto(
    long Revision,
    WorkflowSummaryDto Summary);

/// <summary>Latest search projection metadata for one job.</summary>
public sealed record SearchStateDto(
    Guid JobId,
    Guid WorkflowId,
    long Revision,
    int ResultCount,
    bool IsComplete);

/// <summary>Stable source and ownership metadata for a transfer.</summary>
public sealed record TransferIdentityFieldsDto(
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? CandidateKey);

[JsonConverter(typeof(JsonStringEnumConverter<TransferTerminalOutcome>))]
public enum TransferTerminalOutcome
{
    None,
    Succeeded,
    Cancelled,
    Failed,
    Interrupted,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferFailureReason>))]
public enum TransferFailureReason
{
    None,
    FileUnavailable,
    FileNoLongerShared,
    FileChanged,
    InvalidOffset,
    Denied,
    PeerDisconnected,
    ConnectionFailed,
    TransferTimedOut,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferCancellationSource>))]
public enum TransferCancellationSource
{
    None,
    User,
    Peer,
    DaemonShutdown,
    CatalogInvalidation,
}

/// <summary>A cohesive replacement for the current transfer state.</summary>
public sealed record TransferStatusFieldsDto(
    string State,
    string? LocalPath,
    int AttemptCount,
    bool IsTerminal,
    TransferTerminalOutcome TerminalOutcome = TransferTerminalOutcome.None,
    TransferFailureReason FailureReason = TransferFailureReason.None,
    TransferCancellationSource CancellationSource = TransferCancellationSource.None,
    IReadOnlyList<ResourceActionDto>? AvailableActions = null)
{
    public IReadOnlyList<ResourceActionDto> AvailableActions { get; init; } =
        AvailableActions ?? [];
}

public sealed record TransferSchedulingFieldsDto(
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc);

/// <summary>A cohesive replacement for transfer byte progress.</summary>
public sealed record TransferProgressFieldsDto(
    long BytesTransferred,
    long TotalBytes,
    long? BytesPerSecond = null,
    DateTimeOffset? LastProgressAtUtc = null);

/// <summary>A complete active transfer row, always keyed by TransferId.</summary>
public sealed record TransferStateDto(
    Guid TransferId,
    long Revision,
    TransferIdentityFieldsDto Identity,
    TransferStatusFieldsDto Status,
    TransferProgressFieldsDto Progress,
    TransferSchedulingFieldsDto? Scheduling = null);

/// <summary>
/// A new transfer uses Added. Existing transfers replace only the supplied state and
/// progress components. Revision is the resulting entity revision.
/// </summary>
public sealed record TransferDeltaDto(
    Guid TransferId,
    long Revision,
    TransferStateDto? Added = null,
    TransferStatusFieldsDto? Status = null,
    TransferProgressFieldsDto? Progress = null,
    TransferSchedulingFieldsDto? Scheduling = null);

/// <summary>Typed compact state changes carried by one stream batch.</summary>
public sealed record StateDeltaDto(
    DaemonStateDto? Daemon,
    IReadOnlyList<WorkflowStateDto> Workflows,
    IReadOnlyList<JobDeltaDto> Jobs,
    IReadOnlyList<SearchStateDto> Searches,
    IReadOnlyList<TransferDeltaDto> Transfers,
    IReadOnlyList<Guid> RemovedWorkflowIds,
    IReadOnlyList<Guid> RemovedJobIds,
    IReadOnlyList<Guid> RemovedSearchJobIds,
    IReadOnlyList<Guid> RemovedTransferIds)
{
    public static StateDeltaDto Empty { get; } = new(null, [], [], [], [], [], [], [], []);

    [JsonIgnore]
    public bool IsEmpty =>
        Daemon == null
        && Workflows.Count == 0
        && Jobs.Count == 0
        && Searches.Count == 0
        && Transfers.Count == 0
        && RemovedWorkflowIds.Count == 0
        && RemovedJobIds.Count == 0
        && RemovedSearchJobIds.Count == 0
        && RemovedTransferIds.Count == 0;
}

/// <summary>
/// A complete bounded replication snapshot. Daemon snapshots contain active workflows
/// only; retained terminal history is loaded through paginated history endpoints.
/// </summary>
public sealed record StateSnapshotDto(
    StateStreamScopeDto Scope,
    StateStreamPositionDto Position,
    DateTimeOffset CapturedAtUtc,
    DaemonStateDto? Daemon,
    IReadOnlyList<WorkflowStateDto> Workflows,
    IReadOnlyList<JobStateDto> Jobs,
    IReadOnlyList<SearchStateDto> Searches,
    IReadOnlyList<TransferStateDto> Transfers);

/// <summary>
/// An ordered stream batch. State is applied before Activity. PreviousSequence permits
/// safe overlap with a concurrently captured HTTP snapshot.
/// </summary>
public sealed record StateUpdateBatchDto(
    StateStreamScopeDto Scope,
    Guid Epoch,
    long PreviousSequence,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    StateDeltaDto State,
    IReadOnlyList<ActivityEventDto> Activity);

/// <summary>An ephemeral, best-effort activity edge. It is never required for state.</summary>
public sealed record ActivityEventDto(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Type,
    Guid? WorkflowId,
    Guid? JobId,
    Guid? TransferId,
    ActivityPayloadDto Payload);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JobStatusActivityDto), "jobStatus")]
[JsonDerivedType(typeof(JobMessageActivityDto), "jobMessage")]
[JsonDerivedType(typeof(WorkflowMessageActivityDto), "workflowMessage")]
[JsonDerivedType(typeof(DiagnosticActivityDto), "diagnostic")]
[JsonDerivedType(typeof(ExtractionStartedActivityDto), "extractionStarted")]
[JsonDerivedType(typeof(DownloadAttemptFailedActivityDto), "downloadAttemptFailed")]
[JsonDerivedType(typeof(TrackBatchResolvedActivityDto), "trackBatchResolved")]
public abstract record ActivityPayloadDto;

public sealed record JobStatusActivityDto(
    int DisplayId,
    string Status) : ActivityPayloadDto;

public sealed record JobMessageActivityDto(
    int DisplayId,
    string Level,
    string? Source,
    string Message) : ActivityPayloadDto;

public sealed record WorkflowMessageActivityDto(
    string Level,
    string? Source,
    string Message) : ActivityPayloadDto;

public sealed record DiagnosticActivityDto(
    int? DisplayId,
    string Scope,
    string Message,
    string ExceptionType,
    string Exception,
    string? Source) : ActivityPayloadDto;

public sealed record ExtractionStartedActivityDto(
    int DisplayId,
    string Input,
    string? InputType,
    string? Source) : ActivityPayloadDto;

public sealed record DownloadAttemptFailedActivityDto(
    int DisplayId,
    string? Username,
    string? RemotePath,
    string OutputPath,
    int Attempt,
    int MaxAttempts,
    string ExceptionType,
    string ExceptionMessage,
    string Exception) : ActivityPayloadDto;

public sealed record TrackBatchResolvedActivityDto(
    int DisplayId,
    bool IsNormal,
    PrintOption PrintOption,
    int PendingCount,
    int ExistingCount,
    int NotFoundCount) : ActivityPayloadDto;
