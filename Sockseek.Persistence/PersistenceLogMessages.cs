using Microsoft.Extensions.Logging;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence;

internal static partial class PersistenceLogMessages
{
    [LoggerMessage(4050, LogLevel.Warning, "Peer browse artifact byte target was exceeded; an old artifact was evicted")]
    internal static partial void BrowseArtifactEvicted(ILogger logger);

    [LoggerMessage(4051, LogLevel.Error, "Peer browse artifact cleanup failed; startup cleanup will retry orphan removal")]
    internal static partial void BrowseCleanupFailed(ILogger logger);

    [LoggerMessage(4052, LogLevel.Error, "Peer browse artifact cleanup failed; a later cleanup will retry")]
    internal static partial void BrowseCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(4053, LogLevel.Warning, "Persistence writer health changed to {State} ({FailureKind}; suppressed failures: {SuppressedCount})")]
    internal static partial void WriterDegraded(ILogger logger, PersistenceHealthState state, string failureKind, int suppressedCount);

    [LoggerMessage(4054, LogLevel.Error, "Persistence writer health changed to {State} ({FailureKind}; suppressed failures: {SuppressedCount})")]
    internal static partial void WriterUnhealthy(ILogger logger, PersistenceHealthState state, string failureKind, int suppressedCount);

    [LoggerMessage(4055, LogLevel.Information, "Persistence writer recovered after reconciling retained mutations")]
    internal static partial void WriterRecovered(ILogger logger);

    [LoggerMessage(4056, LogLevel.Warning, "Input artifact blob storage is unavailable; browser file inputs are disabled")]
    internal static partial void InputArtifactStorageUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(4057, LogLevel.Warning, "Search View storage is unavailable; search execution and raw history remain available")]
    internal static partial void SearchViewStorageUnavailable(ILogger logger, Exception exception);
}
