namespace Sockseek.Persistence.Write;

public enum PersistenceHealthState
{
    Healthy,
    Degraded,
    Unhealthy,
}

public sealed record PersistenceHealthSnapshot(
    PersistenceHealthState State,
    DateTimeOffset? LastSuccessfulCommitAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? LastFailure,
    int CriticalQueueDepth,
    int CriticalQueueCapacity,
    int OrdinaryQueueDepth,
    int OrdinaryQueueCapacity,
    int ProgressEntityCount,
    int ProgressEntityCapacity,
    int DegradedProjectionCount,
    int DegradedProjectionCapacity,
    int BufferedSearchResultCount,
    int BufferedSearchResultCapacity,
    long BusyRetryCount,
    long DroppedOrdinaryCount,
    long DroppedProgressCount,
    long DroppedSearchResultCount,
    long IncompleteSearchCount,
    long EvictedTerminalProjectionCount,
    long SuccessfulCommitCount,
    long RowsWritten,
    double? LastCommitDurationMilliseconds,
    int LastBatchMutationCount,
    long PermanentlyFailedMutationCount,
    int IncompleteSearchTrackingCount,
    int IncompleteSearchTrackingCapacity,
    bool IncompleteSearchTrackingOverflowed)
{
    /// Buckets: ≤1, ≤5, ≤10, ≤50, ≤100, ≤500, &gt;500 milliseconds.
    public IReadOnlyList<long> CommitLatencyHistogram { get; init; } = [];
    /// Buckets: ≤1, ≤8, ≤32, ≤128, &gt;128 mutations.
    public IReadOnlyList<long> BatchSizeHistogram { get; init; } = [];
}

public sealed class PersistenceHealth
{
    private readonly object gate = new();
    private PersistenceHealthState state = PersistenceHealthState.Healthy;
    private DateTimeOffset? lastSuccessfulCommitAtUtc;
    private DateTimeOffset? lastFailureAtUtc;
    private string? lastFailure;
    private long busyRetryCount;
    private long droppedOrdinaryCount;
    private long droppedProgressCount;
    private long droppedSearchResultCount;
    private long incompleteSearchCount;
    private long evictedTerminalProjectionCount;
    private long successfulCommitCount;
    private long rowsWritten;
    private double? lastCommitDurationMilliseconds;
    private int lastBatchMutationCount;
    private long permanentlyFailedMutationCount;
    private bool stickyUnhealthy;
    private readonly long[] commitLatencyBuckets = new long[7];
    private readonly long[] batchSizeBuckets = new long[5];

    public event Action? CommitCompleted;
    public event Action? FailureRecorded;

    internal void RecordCommit(
        DateTimeOffset now,
        int rows,
        TimeSpan duration,
        int mutationCount,
        bool reconciliationComplete = true)
    {
        lock (gate)
        {
            state = stickyUnhealthy
                ? PersistenceHealthState.Unhealthy
                : reconciliationComplete ? PersistenceHealthState.Healthy : PersistenceHealthState.Degraded;
            lastSuccessfulCommitAtUtc = now;
            successfulCommitCount++;
            rowsWritten += rows;
            lastCommitDurationMilliseconds = duration.TotalMilliseconds;
            lastBatchMutationCount = mutationCount;
            commitLatencyBuckets[LatencyBucket(duration.TotalMilliseconds)]++;
            batchSizeBuckets[BatchBucket(mutationCount)]++;
        }
        InvokeObservers(CommitCompleted);
    }

    internal void RecordFailure(DateTimeOffset now, Exception exception, bool transient)
    {
        lock (gate)
        {
            state = transient ? PersistenceHealthState.Degraded : PersistenceHealthState.Unhealthy;
            if (!transient)
                stickyUnhealthy = true;
            lastFailureAtUtc = now;
            lastFailure = $"{exception.GetType().Name}: {exception.Message}";
        }
        InvokeObservers(FailureRecorded);
    }

    public void RecordOperationalFailure(DateTimeOffset now, Exception exception)
        => RecordFailure(now, exception, transient: false);

    internal void RecordBusyRetry() => Interlocked.Increment(ref busyRetryCount);
    internal void RecordDroppedOrdinary()
    {
        Interlocked.Increment(ref droppedOrdinaryCount);
        MarkDegraded();
    }

    internal void RecordDroppedProgress()
    {
        Interlocked.Increment(ref droppedProgressCount);
        MarkDegraded();
    }

    internal void RecordDroppedSearchResults(int count)
    {
        Interlocked.Add(ref droppedSearchResultCount, count);
        MarkDegraded();
    }

    internal void RecordIncompleteSearch()
    {
        Interlocked.Increment(ref incompleteSearchCount);
        MarkDegraded();
    }

    internal void RecordEvictedTerminalProjection()
    {
        Interlocked.Increment(ref evictedTerminalProjectionCount);
        MarkDegraded();
    }

    internal void RecordPermanentlyFailedMutations(int count)
        => Interlocked.Add(ref permanentlyFailedMutationCount, count);

    private void MarkDegraded()
    {
        lock (gate)
        {
            if (state == PersistenceHealthState.Healthy)
                state = PersistenceHealthState.Degraded;
        }
    }

    private static void InvokeObservers(Action? observers)
    {
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch { }
        }
    }

    public PersistenceHealthSnapshot Snapshot(PersistenceInbox inbox)
    {
        lock (gate)
        {
            return new PersistenceHealthSnapshot(
                state,
                lastSuccessfulCommitAtUtc,
                lastFailureAtUtc,
                lastFailure,
                inbox.CriticalDepth,
                inbox.Options.CriticalQueueCapacity,
                inbox.OrdinaryDepth,
                inbox.Options.OrdinaryQueueCapacity,
                inbox.ProgressCount,
                inbox.Options.ProgressEntityCapacity,
                inbox.DegradedCount,
                inbox.Options.DegradedProjectionCapacity,
                inbox.BufferedSearchResultCount,
                inbox.Options.SearchResultGlobalCapacity,
                Interlocked.Read(ref busyRetryCount),
                Interlocked.Read(ref droppedOrdinaryCount),
                Interlocked.Read(ref droppedProgressCount),
                Interlocked.Read(ref droppedSearchResultCount),
                Interlocked.Read(ref incompleteSearchCount),
                Interlocked.Read(ref evictedTerminalProjectionCount),
                successfulCommitCount,
                rowsWritten,
                lastCommitDurationMilliseconds,
                lastBatchMutationCount,
                Interlocked.Read(ref permanentlyFailedMutationCount),
                inbox.IncompleteSearchTrackingCount,
                inbox.Options.IncompleteSearchTrackingCapacity,
                inbox.IncompleteSearchTrackingOverflowed)
            {
                CommitLatencyHistogram = Array.AsReadOnly(commitLatencyBuckets.ToArray()),
                BatchSizeHistogram = Array.AsReadOnly(batchSizeBuckets.ToArray()),
            };
        }
    }

    private static int LatencyBucket(double milliseconds)
        => milliseconds switch
        {
            <= 1 => 0,
            <= 5 => 1,
            <= 10 => 2,
            <= 50 => 3,
            <= 100 => 4,
            <= 500 => 5,
            _ => 6,
        };

    private static int BatchBucket(int mutations)
        => mutations switch
        {
            <= 1 => 0,
            <= 8 => 1,
            <= 32 => 2,
            <= 128 => 3,
            _ => 4,
        };
}
