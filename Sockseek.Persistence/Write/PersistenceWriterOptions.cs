namespace Sockseek.Persistence.Write;

public sealed record PersistenceWriterOptions
{
    public int CriticalQueueCapacity { get; init; } = 512;
    public int OrdinaryQueueCapacity { get; init; } = 2_048;
    public int ProgressEntityCapacity { get; init; } = 512;
    public int DegradedProjectionCapacity { get; init; } = 1_024;
    public int SearchResultCapacityPerSearch { get; init; } = 2_000;
    public int SearchResultGlobalCapacity { get; init; } = 20_000;
    public int IncompleteSearchTrackingCapacity { get; init; } = 1_024;
    public int SearchMutationQueueCapacity { get; init; } = 256;
    public int SearchResultFlushCount { get; init; } = 200;
    public TimeSpan SearchResultFlushInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan TransferProgressFlushInterval { get; init; } = TimeSpan.FromSeconds(3);
    public int MaximumBatchSize { get; init; } = 128;
    public int BusyRetryCount { get; init; } = 3;
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan FailureRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public int MaximumRecoveryAttempts { get; init; } = 60;
    public int MaximumFailureTextLength { get; init; } = 2_048;

    public void Validate()
    {
        if (CriticalQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(CriticalQueueCapacity));
        if (OrdinaryQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(OrdinaryQueueCapacity));
        if (ProgressEntityCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(ProgressEntityCapacity));
        if (DegradedProjectionCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(DegradedProjectionCapacity));
        if (SearchMutationQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(SearchMutationQueueCapacity));
        if (SearchResultFlushCount is < 100 or > 500) throw new ArgumentOutOfRangeException(nameof(SearchResultFlushCount));
        if (SearchResultFlushInterval < TimeSpan.FromMilliseconds(100)
            || SearchResultFlushInterval > TimeSpan.FromMilliseconds(250))
            throw new ArgumentOutOfRangeException(nameof(SearchResultFlushInterval));
        if (TransferProgressFlushInterval < TimeSpan.FromSeconds(2)
            || TransferProgressFlushInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(TransferProgressFlushInterval));
        if (MaximumBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumBatchSize));
        if (BusyRetryCount < 0) throw new ArgumentOutOfRangeException(nameof(BusyRetryCount));
        if (BusyRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BusyRetryDelay));
        if (FailureRetryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FailureRetryDelay));
        if (MaximumRecoveryAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumRecoveryAttempts));
        if (MaximumFailureTextLength <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumFailureTextLength));
    }
}
