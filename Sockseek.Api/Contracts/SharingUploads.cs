using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<StartShareScanResult>))]
public enum StartShareScanResult
{
    Started,
    AlreadyRunning,
}

public sealed record StartShareScanResponseDto(
    StartShareScanResult Result,
    ShareScanStateDto Scan);

public sealed record TransferQueueEstimateDto(
    int? AheadCount,
    long QueueRevision);

public sealed record LiveTransferFilter(
    string? Direction = null,
    string? State = null,
    string? Username = null);

public sealed record LiveTransferPageDto(
    IReadOnlyList<TransferStateDto> Items,
    string? NextCursor,
    long ObservedQueueRevision,
    bool QueueChanged);

[JsonConverter(typeof(JsonStringEnumConverter<TransferDetailSource>))]
public enum TransferDetailSource
{
    Live,
    Historical,
    Merged,
}

/// <summary>
/// Point-in-time transfer detail. Live is authoritative when present; History
/// supplies retained metadata and is the fallback after live removal. Complete
/// attempt history is available from the paged transfer-attempts resource.
/// </summary>
public sealed record TransferDetailDto(
    TransferDetailSource Source,
    TransferStateDto? Live,
    TransferQueueEstimateDto? QueueEstimate,
    TransferHistoryDto? History,
    int AttemptCount,
    TransferAttemptHistoryDto? LatestAttempt);
