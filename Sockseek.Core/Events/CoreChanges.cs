using Microsoft.Extensions.Logging;
using Sockseek.Core;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Events;

public interface ICoreChange
{
    long Sequence { get; }
    DateTimeOffset OccurredAtUtc { get; }
}

public interface ICoalescibleCoreChange : ICoreChange
{
    string CoalescingKey { get; }
    long Revision { get; }
}

/// <summary>
/// Marks a change whose relative publication order must be preserved. This does not
/// imply that the change is persisted; persistence selects an explicit set of changes.
/// </summary>
public interface IOrderedCoreChange : ICoreChange;

public abstract record CoreChange(long Sequence, DateTimeOffset OccurredAtUtc) : ICoreChange;

public sealed record JobRegisteredChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Job,
    Guid? ParentJobId,
    Guid? SourceJobId)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record JobStateChangedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Job)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange, ICoalescibleCoreChange
{
    public string CoalescingKey => $"job:{Job.Id}:state";
    public long Revision => Job.Revision;
    public Guid Id => Job.Id;
    public JobLifecycleState LifecycleState => Job.LifecycleState;
    public JobActivityPhase ActivityPhase => Job.ActivityPhase;
    public DateTimeOffset? ActivityUntilUtc => Job.ActivityUntilUtc;
    public JobTerminalOutcome TerminalOutcome => Job.TerminalOutcome;
    public JobSkipReason SkipReason => Job.SkipReason;
    public JobCancellationSource CancellationSource => Job.CancellationSource;
    public JobFailureReason FailureReason => Job.FailureReason;
    public string? FailureMessage => Job.FailureMessage;
    public string? FailureDetail => Job.FailureDetail;
    public DiscoverySnapshot? Discovery => Job.Discovery;
    public bool IsTerminal => Job.LifecycleState == JobLifecycleState.Terminal;
    public bool IsSuccessfulTerminal =>
        Job.TerminalOutcome == JobTerminalOutcome.Succeeded
        || (Job.TerminalOutcome == JobTerminalOutcome.Skipped && Job.SkipReason == JobSkipReason.AlreadyExists);
    public bool IsUnsuccessfulTerminal => IsTerminal && !IsSuccessfulTerminal;
}

public sealed record JobActivityChangedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Job,
    JobActivityPhase Phase,
    DateTimeOffset? UntilUtc)
    : CoreChange(Sequence, OccurredAtUtc), ICoalescibleCoreChange
{
    public string CoalescingKey => $"job:{Job.Id}:activity";
    public long Revision => Job.Revision;
}

public sealed record JobDiscoveryChangedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Job)
    : CoreChange(Sequence, OccurredAtUtc), ICoalescibleCoreChange
{
    public string CoalescingKey => $"job:{Job.Id}:discovery";
    public long Revision => Job.Revision;
    public DiscoverySnapshot? Discovery => Job.Discovery;
}

public sealed record JobExecutionCompletedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Job)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record JobResultCreatedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot ExtractJob,
    JobSnapshot ResultJob)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record EngineCompletedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Queue)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record JobStatusChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Job,
    string Status)
    : CoreChange(Sequence, OccurredAtUtc);

public sealed record JobMessageChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Job,
    LogLevel Level,
    string? Source,
    string Message)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record WorkflowMessageChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid WorkflowId,
    LogLevel Level,
    string? Source,
    string Message)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record DownloadStartedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange
{
    public Guid TransferId => Transfer.Id;
    public PeerFileTargetSnapshot Target => Transfer.Target!;
    public FileCandidateSnapshot? Candidate => Transfer.Candidate;
}

public sealed record DownloadProgressedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer)
    : CoreChange(Sequence, OccurredAtUtc), ICoalescibleCoreChange
{
    public string CoalescingKey => $"transfer:{Transfer.Id}:progress";
    public long Revision => Transfer.Revision;
    public Guid TransferId => Transfer.Id;
    public long BytesTransferred => Transfer.BytesTransferred;
    public long TotalBytes => Transfer.TotalBytes;
}

public sealed record DownloadStateChangedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer)
    : CoreChange(Sequence, OccurredAtUtc), ICoalescibleCoreChange
{
    public string CoalescingKey => $"transfer:{Transfer.Id}:state";
    public long Revision => Transfer.Revision;
    public Guid TransferId => Transfer.Id;
    public string State => Transfer.State ?? "";
}

public sealed record ExceptionSnapshot(string Type, string Message, string Detail);

public sealed record DownloadAttemptFailedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    string OutputPath,
    int Attempt,
    int MaxAttempts,
    ExceptionSnapshot Exception)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange
{
    public Guid TransferId => Transfer.Id;
    public PeerFileTargetSnapshot Target => Transfer.Target!;
    public FileCandidateSnapshot? Candidate => Transfer.Candidate;
}

public sealed record FallbackTransferStartedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public enum TransferFailureReason
{
    Unknown,
    PeerFailure,
    Stale,
    Finalization,
}

public enum TransferCancellationReason
{
    Requested,
    ManualSkip,
    Stale,
}

public enum TransferAttemptSource
{
    SoulseekPeer,
    Fallback,
}

public sealed record TransferAttemptStartedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    Guid AttemptId,
    int AttemptNumber,
    long AttemptRevision,
    TransferAttemptSource Source,
    string? OutputPath)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TransferAttemptCompletedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    Guid AttemptId,
    int AttemptNumber,
    long AttemptRevision)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TransferAttemptFailedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    Guid AttemptId,
    int AttemptNumber,
    long AttemptRevision,
    ExceptionSnapshot Exception)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TransferAttemptCancelledChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    Guid AttemptId,
    int AttemptNumber,
    long AttemptRevision,
    TransferCancellationReason Reason)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TransferCompletedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    string FinalLocalPath)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TransferFailedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    TransferFailureReason Reason,
    ExceptionSnapshot Exception)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TransferCancelledChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Song,
    TransferSnapshot Transfer,
    TransferCancellationReason Reason)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record SearchResultsAddedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid JobId,
    int Revision,
    IReadOnlyList<SearchResultSnapshot> Results)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record SearchCompletedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    Guid JobId,
    int Revision,
    string QueryText,
    int ResultCount,
    int LockedFileCount)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TrackBatchResolvedChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot Owner,
    IReadOnlyList<JobSnapshot> Pending,
    IReadOnlyList<JobSnapshot> Existing,
    IReadOnlyList<JobSnapshot> NotFound)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record TrackListReadyChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<JobSnapshot> Songs)
    : CoreChange(Sequence, OccurredAtUtc), IOrderedCoreChange;

public sealed record ListProgressChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    JobSnapshot List,
    int Done,
    int Failed,
    int Total)
    : CoreChange(Sequence, OccurredAtUtc), ICoalescibleCoreChange
{
    public string CoalescingKey => $"list:{List.Id}:progress";
    public long Revision => List.Revision;
}

public sealed record OverallProgressChange(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    int Done,
    int Failed,
    int Total)
    : CoreChange(Sequence, OccurredAtUtc), ICoalescibleCoreChange
{
    public string CoalescingKey => "download:overall-progress";
    public long Revision => Sequence;
}
