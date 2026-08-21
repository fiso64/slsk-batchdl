using Microsoft.Extensions.Logging;

namespace Sockseek.Server;

internal static partial class ServerLogMessages
{
    [LoggerMessage(4000, LogLevel.Warning, "Live batch observer failed")]
    internal static partial void LiveBatchObserverFailed(ILogger logger, Exception exception);

    [LoggerMessage(4001, LogLevel.Warning, "Live state transport send failed (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void LiveStateSendFailed(ILogger logger, Exception exception, long suppressedCount);

    [LoggerMessage(4002, LogLevel.Warning, "Live state transport send timed out (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void LiveStateSendTimedOut(ILogger logger, long suppressedCount);

    [LoggerMessage(4003, LogLevel.Warning, "Disabled chat callback failed (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void DisabledChatCallbackFailed(ILogger logger, Exception exception, long suppressedCount);

    [LoggerMessage(4004, LogLevel.Warning, "Disabled chat discard queue is full; the message remains replayable (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void DisabledChatQueueFull(ILogger logger, long suppressedCount);

    [LoggerMessage(4005, LogLevel.Warning, "Discarded private-message acknowledgement failed (suppressed since previous warning: {SuppressedCount})")]
    internal static partial void DisabledChatAcknowledgementFailed(ILogger logger, Exception exception, long suppressedCount);

    [LoggerMessage(4006, LogLevel.Error, "Download engine instance failed; restarting supervisor loop (restart {RestartCount})")]
    internal static partial void EngineRestarting(ILogger logger, Exception exception, int restartCount);

    [LoggerMessage(4007, LogLevel.Warning, "Chat retention projection failed")]
    internal static partial void ChatRetentionProjectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(4008, LogLevel.Error, "Persistence drain timed out; runtime {RuntimeId} remains unfinished for startup reconciliation")]
    internal static partial void PersistenceDrainTimedOut(ILogger logger, Guid? runtimeId);

    [LoggerMessage(4009, LogLevel.Information, "Persistence retention pruned {PrunedJobs} jobs, {PrunedSearchResults} raw search results, and {PrunedChatMessages} chat messages in {DurationMilliseconds} ms")]
    internal static partial void PersistenceRetentionCompleted(ILogger logger, int prunedJobs, int prunedSearchResults, int prunedChatMessages, long durationMilliseconds);

    [LoggerMessage(4010, LogLevel.Error, "Scheduled persistence retention failed")]
    internal static partial void PersistenceRetentionFailed(ILogger logger, Exception exception);

    [LoggerMessage(4011, LogLevel.Critical, "Unhandled server startup or runtime error")]
    internal static partial void UnhandledServerError(ILogger logger, Exception exception);

    [LoggerMessage(4012, LogLevel.Warning, "Request for {Feature} could not start because the feature is unavailable")]
    internal static partial void FeatureRequestUnavailable(ILogger logger, string feature);
}
