using System.Text.Json.Serialization;

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
    long Revision,
    DateTimeOffset? RequestedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? LastProgressAtUtc = null,
    long? BytesPerSecond = null,
    FileMetadataDto? File = null,
    IReadOnlyList<ResourceActionDto>? AvailableActions = null,
    DateTimeOffset? ArchivedAtUtc = null,
    string? GroupRef = null,
    string? GroupDisplayPath = null)
{
    public IReadOnlyList<ResourceActionDto> AvailableActions { get; init; } =
        AvailableActions ?? [];
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferRetainedCoverageState>))]
public enum TransferRetainedCoverageState
{
    Available,
    Degraded,
    Unavailable,
}

public sealed record TransferRetainedCoverageDto(
    TransferRetainedCoverageState State,
    string? Reason = null);

[JsonConverter(typeof(JsonStringEnumConverter<TransferCommandDirection>))]
public enum TransferCommandDirection
{
    Download,
    Upload,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferCancellationScope>))]
public enum TransferCancellationScope
{
    All,
    Queued,
    InProgress,
}

public sealed record BulkCancelTransfersRequestDto(
    TransferCommandDirection Direction,
    TransferCancellationScope Scope = TransferCancellationScope.All);

public sealed record TransferCommandReasonCountDto(string Reason, int Count);

/// <summary>
/// Fixed-size result for a command resolved against a target snapshot. A no-op
/// means the requested state was already true when that target was processed.
/// </summary>
public sealed record TransferCommandReceiptDto(
    int ResolvedCount,
    int SucceededCount,
    int NoOpCount,
    int RejectedCount,
    int FailedCount,
    IReadOnlyList<TransferCommandReasonCountDto> Reasons);

public sealed record SetTransferArchivedRequestDto(bool Archived = true);

public sealed record ArchiveTransfersRequestDto(
    bool Archived = true,
    string? Direction = null,
    string? TerminalOutcome = null,
    string? Username = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

/// <summary>
/// Moving newest-first keyset page. New transfers can appear above a cursor;
/// status changes do not move an existing transfer in the traversal.
/// </summary>
public sealed record TransferTimelinePageDto(
    IReadOnlyList<TransferHistoryDto> Items,
    string? NextCursor,
    TransferRetainedCoverageDto RetainedCoverage);

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
