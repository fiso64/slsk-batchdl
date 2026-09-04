using System.Threading.Channels;

namespace Sockseek.Persistence.Write;

public interface IPersistenceMutationSink
{
    bool TryEnqueue(PersistenceMutation mutation);
}

public sealed class PersistenceInbox : IPersistenceMutationSink
{
    private readonly Channel<PersistenceMutation> critical;
    private readonly Channel<PersistenceMutation> ordinary;
    private readonly Channel<PersistenceMutation> search;
    private readonly Channel<AwaitablePersistenceCommand> commands;
    private readonly Dictionary<Guid, TransferPersistenceMutation> progress = [];
    private readonly Dictionary<string, PersistenceMutation> degraded = [];
    private readonly object progressGate = new();
    private readonly object degradedGate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly PersistenceHealth health;
    private readonly IPersistenceMutationObserver? mutationObserver;
    private int criticalDepth;
    private int ordinaryDepth;
    private int searchDepth;
    private int bufferedSearchResultCount;
    private int completed;

    public PersistenceInbox(
        PersistenceWriterOptions options,
        PersistenceHealth health,
        IPersistenceMutationObserver? mutationObserver = null)
    {
        options.Validate();
        Options = options;
        this.health = health;
        this.mutationObserver = mutationObserver;
        critical = CreateChannel(options.CriticalQueueCapacity);
        ordinary = CreateChannel(options.OrdinaryQueueCapacity);
        search = CreateChannel(options.SearchMutationQueueCapacity);
        commands = Channel.CreateBounded<AwaitablePersistenceCommand>(
            new BoundedChannelOptions(options.CriticalQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    public PersistenceWriterOptions Options { get; }
    public int CriticalDepth => Volatile.Read(ref criticalDepth) + CommandDepth;
    public int OrdinaryDepth => Volatile.Read(ref ordinaryDepth);
    public int ProgressCount { get { lock (progressGate) return progress.Count; } }
    public int DegradedCount { get { lock (degradedGate) return degraded.Count; } }
    public int BufferedSearchResultCount => Volatile.Read(ref bufferedSearchResultCount);
    internal int SearchDepth => Volatile.Read(ref searchDepth);
    public int IncompleteSearchTrackingCount => 0;
    public bool IncompleteSearchTrackingOverflowed => false;
    internal bool IsCompleted => Volatile.Read(ref completed) != 0;

    public bool TryEnqueue(PersistenceMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (IsCompleted)
        {
            mutationObserver?.PermanentlyFailed(
                [mutation],
                new InvalidOperationException("Persistence stopped before the mutation could be accepted."));
            return false;
        }
        if (mutation is SearchResultsPersistenceMutation or SearchCompletionPersistenceMutation)
            return EnqueueSearchWithBackpressure(mutation);
        if (mutation is TransferTerminalPersistenceMutation terminalTransfer)
        {
            return TryEnqueueCritical(AbsorbBufferedTransfer(terminalTransfer));
        }
        if (mutation is TransferPersistenceMutation { Priority: PersistenceMutationPriority.Progress } transferProgress)
            return TrySetProgress(transferProgress);

        if (mutation.Priority >= PersistenceMutationPriority.Structural)
            return TryEnqueueCritical(mutation);

        if (ordinary.Writer.TryWrite(mutation))
        {
            Interlocked.Increment(ref ordinaryDepth);
            Signal();
            return true;
        }

        health.RecordDroppedOrdinary();

        return false;
    }

    internal async Task EnqueueCommandAsync(
        AwaitablePersistenceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsCompleted)
            throw new InvalidOperationException("Persistence is stopping.");
        try
        {
            await commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            Signal();
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException("Persistence is stopping.", ex);
        }
    }

    internal bool TryDequeueCommand(out AwaitablePersistenceCommand? command)
    {
        if (!commands.Reader.TryRead(out command))
            return false;
        if (CommandDepth > 0)
            Signal();
        return true;
    }

    internal int CommandDepth => commands.Reader.CanCount ? commands.Reader.Count : 0;

    internal async Task WaitForWorkAsync(CancellationToken cancellationToken)
        => await signal.WaitAsync(Options.SearchResultFlushInterval, cancellationToken).ConfigureAwait(false);

    internal IReadOnlyList<PersistenceMutation> DrainBatch(bool includeProgress = true)
    {
        var batch = new List<PersistenceMutation>(Options.MaximumBatchSize);
        while (batch.Count < Options.MaximumBatchSize && critical.Reader.TryRead(out var mutation))
        {
            Interlocked.Decrement(ref criticalDepth);
            batch.Add(mutation);
        }

        lock (degradedGate)
        {
            foreach (var item in degraded.Values
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Sequence)
                .Take(Options.MaximumBatchSize - batch.Count)
                .ToList())
            {
                degraded.Remove(item.CoalescingKey);
                batch.Add(item);
            }
        }

        while (batch.Count < Options.MaximumBatchSize && ordinary.Reader.TryRead(out var mutation))
        {
            Interlocked.Decrement(ref ordinaryDepth);
            batch.Add(mutation);
        }

        while (batch.Count < Options.MaximumBatchSize
            && search.Reader.TryRead(out var searchMutation))
        {
            Interlocked.Decrement(ref searchDepth);
            if (searchMutation is SearchResultsPersistenceMutation results)
                Interlocked.Add(ref bufferedSearchResultCount, -results.Results.Count);
            batch.Add(searchMutation);
        }

        if (includeProgress)
        {
            lock (progressGate)
            {
                foreach (var item in progress.Values
                    .OrderBy(item => item.Sequence)
                    .Take(Options.MaximumBatchSize - batch.Count)
                    .ToList())
                {
                    progress.Remove(item.TransferId);
                    batch.Add(item);
                }
            }
        }

        if (CriticalDepth > 0 || OrdinaryDepth > 0 || DegradedCount > 0 || SearchDepth > 0
            || includeProgress && ProgressCount > 0)
            Signal();
        return batch;
    }

    internal void RequeueAfterFailure(IEnumerable<PersistenceMutation> mutations)
    {
        foreach (var mutation in mutations)
            StoreDegraded(mutation);
        Signal();
    }

    public void Complete()
    {
        Interlocked.Exchange(ref completed, 1);
        commands.Writer.TryComplete();
        critical.Writer.TryComplete();
        ordinary.Writer.TryComplete();
        search.Writer.TryComplete();
        Signal();
    }

    internal void FailPendingCommands(Exception exception)
    {
        while (commands.Reader.TryRead(out var command))
        {
            command.Fail(exception);
        }
    }

    private bool TryEnqueueCritical(PersistenceMutation mutation)
    {
        if (critical.Writer.TryWrite(mutation))
        {
            Interlocked.Increment(ref criticalDepth);
            Signal();
            return true;
        }

        StoreDegraded(mutation);
        Signal();
        return false;
    }

    private bool TrySetProgress(TransferPersistenceMutation mutation)
    {
        lock (progressGate)
        {
            if (progress.TryGetValue(mutation.TransferId, out var current))
            {
                if (mutation.Revision > current.Revision)
                    progress[mutation.TransferId] = MergeTransfer(current, mutation);
                else if (mutation.AccountingObservations is { Count: > 0 })
                    progress[mutation.TransferId] = MergeTransfer(mutation, current);
                Signal();
                return true;
            }

            if (progress.Count >= Options.ProgressEntityCapacity)
            {
                // Accounting-bearing progress is compacted per attempt/time
                // bucket and must survive until a terminal cumulative snapshot.
                // Active transfer concurrency is the representation bound.
                if (mutation.AccountingObservations is { Count: > 0 })
                {
                    progress.Add(mutation.TransferId, mutation);
                    Signal();
                    return true;
                }
                health.RecordDroppedProgress();
                return false;
            }

            progress.Add(mutation.TransferId, mutation);
        }
        Signal();
        return true;
    }

    private TransferTerminalPersistenceMutation AbsorbBufferedTransfer(
        TransferTerminalPersistenceMutation terminal)
    {
        Guid transferId = terminal.Transfer.TransferId;
        TransferPersistenceMutation merged = terminal.Transfer;
        lock (progressGate)
        {
            if (progress.Remove(transferId, out TransferPersistenceMutation? buffered))
                merged = MergeTransfer(buffered, merged);
        }
        lock (degradedGate)
        {
            foreach (var pair in degraded
                .Where(pair => pair.Value.EntityId == transferId)
                .ToArray())
            {
                if (pair.Value is TransferPersistenceMutation transfer)
                    merged = MergeTransfer(transfer, merged);
                degraded.Remove(pair.Key);
            }
        }
        return terminal with { Transfer = merged };
    }

    internal static TransferPersistenceMutation MergeTransfer(
        TransferPersistenceMutation earlier,
        TransferPersistenceMutation later)
    {
        IReadOnlyList<TransferAccountingObservation>? observations = MergeObservations(
            earlier.AccountingObservations,
            later.AccountingObservations);
        return later with { AccountingObservations = observations };
    }

    private static IReadOnlyList<TransferAccountingObservation>? MergeObservations(
        IReadOnlyList<TransferAccountingObservation>? first,
        IReadOnlyList<TransferAccountingObservation>? second)
    {
        if (first is not { Count: > 0 }) return second;
        if (second is not { Count: > 0 }) return first;
        TransferAccountingObservation[] ordered = first.Concat(second)
            .GroupBy(item => (item.AttemptId, item.Revision))
            .Select(group => group.Last())
            .ToArray();
        var compact = new List<TransferAccountingObservation>();
        foreach (IGrouping<Guid, TransferAccountingObservation> attempt in ordered
            .GroupBy(item => item.AttemptId))
        {
            TransferAccountingObservation? pending = null;
            long pendingBucket = 0;
            foreach (TransferAccountingObservation observation in attempt
                .OrderBy(item => item.OccurredAtUtc)
                .ThenBy(item => item.Revision))
            {
                long observedAt = observation.OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
                Math.DivRem(
                    observedAt,
                    PersistenceWriter.AccountingBucketMilliseconds,
                    out long remainder);
                long bucket = observedAt - remainder;
                if (pending is not null
                    && (bucket != pendingBucket
                        || observation.CumulativeBytes < pending.CumulativeBytes))
                {
                    compact.Add(pending);
                }
                pending = observation;
                pendingBucket = bucket;
            }
            if (pending is not null)
                compact.Add(pending);
        }
        return compact
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Revision)
            .ToArray();
    }

    private bool EnqueueSearchWithBackpressure(PersistenceMutation mutation)
    {
        try
        {
            search.Writer.WriteAsync(mutation).AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref searchDepth);
            if (mutation is SearchResultsPersistenceMutation results)
                Interlocked.Add(ref bufferedSearchResultCount, results.Results.Count);
            Signal();
            return true;
        }
        catch (ChannelClosedException exception)
        {
            mutationObserver?.PermanentlyFailed([mutation], exception);
            return false;
        }
    }

    private void StoreDegraded(PersistenceMutation mutation)
    {
        lock (degradedGate)
        {
            if (degraded.TryGetValue(mutation.CoalescingKey, out var current))
            {
                if (HasAccounting(current) || HasAccounting(mutation))
                {
                    degraded[mutation.CoalescingKey] = MergeAccountingMutation(current, mutation);
                    return;
                }
                if (mutation.Revision > current.Revision
                    || mutation.Priority > current.Priority)
                    degraded[mutation.CoalescingKey] = mutation;
                return;
            }

            if (degraded.Count >= Options.DegradedProjectionCapacity)
            {
                var victim = degraded.Values
                    .Where(item => !HasAccounting(item))
                    .OrderBy(item => item.Priority == PersistenceMutationPriority.Terminal ? 1 : 0)
                    .ThenBy(item => item.Sequence)
                    .FirstOrDefault();
                if (victim is null)
                {
                    if (HasAccounting(mutation))
                    {
                        degraded.Add(mutation.CoalescingKey, mutation);
                        return;
                    }
                    mutationObserver?.PermanentlyFailed(
                        [mutation],
                        new InvalidOperationException(
                            "Persistence retained exact transfer accounting ahead of a lower-priority degraded projection."));
                    return;
                }
                degraded.Remove(victim.CoalescingKey);
                if (victim.Priority == PersistenceMutationPriority.Terminal)
                    health.RecordEvictedTerminalProjection();
                mutationObserver?.PermanentlyFailed(
                    [victim],
                    new InvalidOperationException(
                        "Persistence evicted a retained mutation while reconciling a degraded writer."));
            }
            degraded.Add(mutation.CoalescingKey, mutation);
        }
    }

    private static bool HasAccounting(PersistenceMutation mutation)
        => mutation switch
        {
            TransferPersistenceMutation transfer =>
                transfer.AccountingObservations is { Count: > 0 },
            TransferTerminalPersistenceMutation terminal =>
                terminal.Transfer.AccountingObservations is { Count: > 0 }
                || terminal.FinalAttempt?.AccountingObservations is { Count: > 0 },
            TransferAttemptPersistenceMutation attempt =>
                attempt.AccountingObservations is { Count: > 0 },
            _ => false,
        };

    private static PersistenceMutation MergeAccountingMutation(
        PersistenceMutation current,
        PersistenceMutation incoming)
    {
        TransferPersistenceMutation? currentTransfer = current switch
        {
            TransferPersistenceMutation transfer => transfer,
            TransferTerminalPersistenceMutation terminal => terminal.Transfer,
            _ => null,
        };
        TransferPersistenceMutation? incomingTransfer = incoming switch
        {
            TransferPersistenceMutation transfer => transfer,
            TransferTerminalPersistenceMutation terminal => terminal.Transfer,
            _ => null,
        };
        if (currentTransfer is null || incomingTransfer is null)
            return incoming.Revision > current.Revision
                || incoming.Priority > current.Priority
                    ? incoming
                    : current;

        bool incomingWins = incomingTransfer.Revision > currentTransfer.Revision
            || incomingTransfer.Revision == currentTransfer.Revision
                && incomingTransfer.Priority >= currentTransfer.Priority;
        TransferPersistenceMutation merged = incomingWins
            ? MergeTransfer(currentTransfer, incomingTransfer)
            : MergeTransfer(incomingTransfer, currentTransfer);
        TransferTerminalPersistenceMutation? terminalMutation = incoming as TransferTerminalPersistenceMutation
            ?? current as TransferTerminalPersistenceMutation;
        return terminalMutation is null
            ? merged
            : terminalMutation with { Transfer = merged };
    }

    private static Channel<PersistenceMutation> CreateChannel(int capacity)
        => Channel.CreateBounded<PersistenceMutation>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private void Signal()
    {
        try
        {
            if (signal.CurrentCount == 0)
                signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

}
