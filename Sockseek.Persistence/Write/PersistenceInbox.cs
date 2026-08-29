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
    private readonly Channel<AwaitablePersistenceCommand> commands;
    private readonly Dictionary<Guid, TransferPersistenceMutation> progress = [];
    private readonly Dictionary<string, PersistenceMutation> degraded = [];
    private readonly Dictionary<Guid, SearchResultBuffer> searchBuffers = [];
    private readonly HashSet<Guid> incompleteSearches = [];
    private readonly object progressGate = new();
    private readonly object degradedGate = new();
    private readonly object searchGate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly PersistenceHealth health;
    private readonly IPersistenceMutationObserver? mutationObserver;
    private int criticalDepth;
    private int ordinaryDepth;
    private int bufferedSearchResultCount;
    private int incompleteTrackingOverflowed;
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
    public int IncompleteSearchTrackingCount { get { lock (searchGate) return incompleteSearches.Count; } }
    public bool IncompleteSearchTrackingOverflowed => Volatile.Read(ref incompleteTrackingOverflowed) != 0;
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
        if (mutation is SearchResultsPersistenceMutation searchResults)
            return TryBufferSearchResults(searchResults);
        if (mutation is SearchCompletionPersistenceMutation searchCompletion)
            return TryEnqueueSearchCompletion(searchCompletion);
        if (mutation is TransferTerminalPersistenceMutation terminalTransfer)
        {
            RemoveBufferedTransfer(terminalTransfer.Transfer.TransferId);
            return TryEnqueueCritical(terminalTransfer);
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

        lock (searchGate)
        {
            while (batch.Count < Options.MaximumBatchSize && searchBuffers.Count > 0)
            {
                var next = searchBuffers
                    .Select(pair => (pair.Key, Mutation: pair.Value.Batches.Peek()))
                    .OrderBy(item => item.Mutation.Sequence)
                    .First();
                SearchResultBuffer buffer = searchBuffers[next.Key];
                buffer.Batches.Dequeue();
                buffer.ResultCount -= next.Mutation.Results.Count;
                if (buffer.Batches.Count == 0)
                    searchBuffers.Remove(next.Key);
                Interlocked.Add(ref bufferedSearchResultCount, -next.Mutation.Results.Count);
                batch.Add(next.Mutation);
            }
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

        if (CriticalDepth > 0 || OrdinaryDepth > 0 || DegradedCount > 0 || BufferedSearchResultCount > 0
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
                    progress[mutation.TransferId] = mutation;
                Signal();
                return true;
            }

            if (progress.Count >= Options.ProgressEntityCapacity)
            {
                health.RecordDroppedProgress();
                return false;
            }

            progress.Add(mutation.TransferId, mutation);
        }
        Signal();
        return true;
    }

    private void RemoveBufferedTransfer(Guid transferId)
    {
        lock (progressGate)
            progress.Remove(transferId);
        lock (degradedGate)
        {
            foreach (string key in degraded
                .Where(pair => pair.Value.EntityId == transferId)
                .Select(pair => pair.Key)
                .ToArray())
            {
                degraded.Remove(key);
            }
        }
    }

    private bool TryBufferSearchResults(SearchResultsPersistenceMutation mutation)
    {
        bool flushThresholdReached;
        lock (searchGate)
        {
            int perSearchCount = searchBuffers.TryGetValue(mutation.SearchJobId, out var existing)
                ? existing.ResultCount
                : 0;
            if (perSearchCount + mutation.Results.Count > Options.SearchResultCapacityPerSearch
                || bufferedSearchResultCount + mutation.Results.Count > Options.SearchResultGlobalCapacity)
            {
                incompleteSearches.Add(mutation.SearchJobId);
                if (incompleteSearches.Count > Options.IncompleteSearchTrackingCapacity)
                    Volatile.Write(ref incompleteTrackingOverflowed, 1);
                health.RecordDroppedSearchResults(mutation.Results.Count);
                health.RecordIncompleteSearch();
                return false;
            }

            if (existing == null)
            {
                existing = new SearchResultBuffer();
                searchBuffers.Add(mutation.SearchJobId, existing);
            }
            existing.Batches.Enqueue(mutation);
            existing.ResultCount += mutation.Results.Count;
            Interlocked.Add(ref bufferedSearchResultCount, mutation.Results.Count);
            flushThresholdReached = perSearchCount + mutation.Results.Count >= Options.SearchResultFlushCount;
        }
        if (flushThresholdReached)
            Signal();
        return true;
    }

    private bool TryEnqueueSearchCompletion(SearchCompletionPersistenceMutation completion)
    {
        IReadOnlyList<SearchResultsPersistenceMutation> pending;
        bool incomplete;
        lock (searchGate)
        {
            if (searchBuffers.Remove(completion.SearchJobId, out var buffered))
            {
                pending = buffered.Batches.ToArray();
                Interlocked.Add(ref bufferedSearchResultCount, -buffered.ResultCount);
            }
            else
            {
                pending = [];
            }
            incomplete = incompleteSearches.Remove(completion.SearchJobId);
        }

        if (incomplete)
            completion = completion with { ResultPersistenceState = "Incomplete" };
        return TryEnqueueCritical(new SearchTerminalPersistenceMutation(completion, pending));
    }

    private void StoreDegraded(PersistenceMutation mutation)
    {
        lock (degradedGate)
        {
            if (degraded.TryGetValue(mutation.CoalescingKey, out var current))
            {
                if (mutation.Revision > current.Revision
                    || mutation.Priority > current.Priority)
                    degraded[mutation.CoalescingKey] = mutation;
                return;
            }

            if (degraded.Count >= Options.DegradedProjectionCapacity)
            {
                var victim = degraded.Values
                    .OrderBy(item => item.Priority == PersistenceMutationPriority.Terminal ? 1 : 0)
                    .ThenBy(item => item.Sequence)
                    .First();
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

    private sealed class SearchResultBuffer
    {
        public Queue<SearchResultsPersistenceMutation> Batches { get; } = new();
        public int ResultCount { get; set; }
    }
}
