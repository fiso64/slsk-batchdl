namespace Sockseek.Persistence.Entities;

internal sealed class RuntimeSessionEntity
{
    public Guid Id { get; set; }
    public long StartedAtUtc { get; set; }
    public long? StoppedAtUtc { get; set; }
    public string? ShutdownKind { get; set; }
    public string Version { get; set; } = "";
}

internal sealed class JobEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid? ParentJobId { get; set; }
    public Guid? SourceJobId { get; set; }
    public Guid? ResultJobId { get; set; }
    public Guid LastRuntimeId { get; set; }
    public long LastSequence { get; set; }
    public long DisplayId { get; set; }
    public string Kind { get; set; } = "";
    public string LifecycleState { get; set; } = "";
    public string ActivityPhase { get; set; } = "";
    public long? ActivityUntilUtc { get; set; }
    public string TerminalOutcome { get; set; } = "";
    public string SkipReason { get; set; } = "";
    public string CancellationSource { get; set; } = "";
    public string FailureReason { get; set; } = "";
    public string? FailureMessage { get; set; }
    public string? FailureDetail { get; set; }
    public string? ItemName { get; set; }
    public string? QueryText { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public long Revision { get; set; }
    public int PayloadSchemaVersion { get; set; }
    public string? PayloadJson { get; set; }
}

internal sealed class SearchJobEntity
{
    public Guid JobId { get; set; }
    public string Query { get; set; } = "";
    public long Revision { get; set; }
    public long ResultCount { get; set; }
    public long LockedFileCount { get; set; }
    public bool IsComplete { get; set; }
    public long? CompletedAtUtc { get; set; }
    public string ResultPersistenceState { get; set; } = "NotPersisted";
    public long? ResultsPrunedAtUtc { get; set; }
}

internal sealed class SearchResultEntity
{
    public Guid Id { get; set; }
    public Guid SearchJobId { get; set; }
    public long Sequence { get; set; }
    public long Revision { get; set; }
    public string Username { get; set; } = "";
    public string RemoteFilename { get; set; } = "";
    public long SizeBytes { get; set; }
    public int? BitRate { get; set; }
    public int? BitDepth { get; set; }
    public int ResponseFileCount { get; set; }
    public int? SampleRate { get; set; }
    public int? DurationSeconds { get; set; }
    public string Extension { get; set; } = "";
    public int? UploadSpeed { get; set; }
    public bool? HasFreeUploadSlot { get; set; }
    public string? AttributesJson { get; set; }
    public long ObservedAtUtc { get; set; }
}

internal sealed class TransferEntity
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public Guid? WorkflowId { get; set; }
    public Guid LastRuntimeId { get; set; }
    public long LastSequence { get; set; }
    public string Direction { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Username { get; set; }
    public string? RemotePath { get; set; }
    public string? LocalPath { get; set; }
    public string State { get; set; } = "";
    public string TerminalOutcome { get; set; } = "";
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public int AttemptCount { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? LastProgressAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public string FailureReason { get; set; } = "";
    public string? FailureMessage { get; set; }
    public long Revision { get; set; }
}

internal sealed class TransferAttemptEntity
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public Guid LastRuntimeId { get; set; }
    public long LastSequence { get; set; }
    public int AttemptNumber { get; set; }
    public string Source { get; set; } = "";
    public string State { get; set; } = "";
    public string? SourceUsername { get; set; }
    public string? SourcePath { get; set; }
    public string? OutputPath { get; set; }
    public long StartedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public string FailureReason { get; set; } = "";
    public string? FailureMessage { get; set; }
    public long Revision { get; set; }
}
