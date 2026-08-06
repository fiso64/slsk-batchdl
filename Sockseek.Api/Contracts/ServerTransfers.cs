namespace Sockseek.Api;

public sealed record TransferHistoryDto(
    Guid TransferId,
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string State,
    string TerminalOutcome,
    long? TotalBytes,
    long TransferredBytes,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string FailureReason,
    string? FailureMessage,
    TransferCancellationSource CancellationSource,
    long Revision);

public sealed record TransferAttemptHistoryDto(
    Guid AttemptId,
    Guid TransferId,
    int AttemptNumber,
    string Source,
    string State,
    string? SourceUsername,
    string? SourcePath,
    string? OutputPath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string FailureReason,
    string? FailureMessage,
    long Revision);

public sealed record TransferHistoryDetailDto(
    TransferHistoryDto Transfer,
    IReadOnlyList<TransferAttemptHistoryDto> Attempts);
