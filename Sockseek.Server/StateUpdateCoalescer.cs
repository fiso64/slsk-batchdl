using Sockseek.Api;

namespace Sockseek.Server;

/// <summary>
/// Bounded transport scheduler for typed state batches. The server projection remains
/// the owner of complete state; this class only merges unflushed network changes.
/// </summary>
public sealed class StateUpdateCoalescer : IDisposable
{
    private readonly Lock gate = new();
    private readonly Action<IReadOnlyList<StateUpdateBatchDto>> publish;
    private readonly Dictionary<StateStreamScopeDto, PendingBatch> pending = [];
    private readonly Timer timer;

    public StateUpdateCoalescer(
        Action<IReadOnlyList<StateUpdateBatchDto>> publish,
        TimeSpan? flushInterval = null)
    {
        this.publish = publish;
        var interval = flushInterval ?? TimeSpan.FromMilliseconds(200);
        timer = new Timer(_ => Flush(), null, interval, interval);
    }

    public void Publish(StateUpdateBatchDto batch)
    {
        lock (gate)
        {
            if (pending.TryGetValue(batch.Scope, out var current))
                pending[batch.Scope] = current.Merge(batch);
            else
                pending[batch.Scope] = PendingBatch.From(batch);

            if (RequiresPromptFlush(batch.State))
                FlushCore();
        }
    }

    public void Flush()
    {
        lock (gate)
            FlushCore();
    }

    private void FlushCore()
    {
        if (pending.Count == 0)
            return;

        var batches = pending.Values
            .OrderBy(batch => batch.Scope.Kind)
            .ThenBy(batch => batch.Scope.WorkflowId)
            .Select(batch => batch.ToDto())
            .ToList();
        pending.Clear();
        publish(batches);
    }

    private static bool RequiresPromptFlush(StateDeltaDto state)
        => state.RemovedWorkflowIds.Count > 0
            || state.RemovedJobIds.Count > 0
            || state.RemovedTransferIds.Count > 0
            || state.Workflows.Any(workflow => workflow.Summary.State != ServerWorkflowState.Active)
            || state.Jobs.Any(job =>
                (job.Added?.Lifecycle ?? job.Lifecycle)?.LifecycleState == ServerJobLifecycleState.Terminal)
            || state.Transfers.Any(transfer =>
                (transfer.Added?.Status ?? transfer.Status)?.IsTerminal == true);

    public void Dispose()
    {
        timer.Dispose();
        Flush();
    }

    private sealed record PendingBatch(
        StateStreamScopeDto Scope,
        Guid Epoch,
        long PreviousSequence,
        long Sequence,
        DateTimeOffset OccurredAtUtc,
        StateDeltaDto State,
        IReadOnlyList<ActivityEventDto> Activity)
    {
        public static PendingBatch From(StateUpdateBatchDto batch)
            => new(
                batch.Scope,
                batch.Epoch,
                batch.PreviousSequence,
                batch.Sequence,
                batch.OccurredAtUtc,
                batch.State,
                batch.Activity);

        public PendingBatch Merge(StateUpdateBatchDto next)
        {
            if (next.Epoch != Epoch || next.PreviousSequence != Sequence)
                throw new InvalidOperationException(
                    $"Cannot coalesce discontinuous {Scope.Kind} stream batches "
                    + $"{PreviousSequence}->{Sequence} and {next.PreviousSequence}->{next.Sequence}.");

            return this with
            {
                Sequence = next.Sequence,
                OccurredAtUtc = next.OccurredAtUtc,
                State = MergeState(State, next.State),
                Activity = Activity.Concat(next.Activity).OrderBy(activity => activity.Sequence).ToList(),
            };
        }

        public StateUpdateBatchDto ToDto()
            => new(
                Scope,
                Epoch,
                PreviousSequence,
                Sequence,
                OccurredAtUtc,
                State,
                Activity);
    }

    private static StateDeltaDto MergeState(StateDeltaDto first, StateDeltaDto second)
    {
        var workflows = first.Workflows.ToDictionary(workflow => workflow.Summary.WorkflowId);
        foreach (var workflow in second.Workflows)
        {
            if (!workflows.TryGetValue(workflow.Summary.WorkflowId, out var current)
                || workflow.Revision > current.Revision)
            {
                workflows[workflow.Summary.WorkflowId] = workflow;
            }
        }

        var jobs = first.Jobs.ToDictionary(job => job.JobId);
        foreach (var job in second.Jobs)
        {
            if (!jobs.TryGetValue(job.JobId, out var current))
                jobs[job.JobId] = job;
            else if (job.Revision > current.Revision)
                jobs[job.JobId] = MergeJob(current, job);
        }

        var searches = first.Searches.ToDictionary(search => search.JobId);
        foreach (var search in second.Searches)
        {
            if (!searches.TryGetValue(search.JobId, out var current)
                || search.Revision > current.Revision)
            {
                searches[search.JobId] = search;
            }
        }

        var transfers = first.Transfers.ToDictionary(transfer => transfer.TransferId);
        foreach (var transfer in second.Transfers)
        {
            if (!transfers.TryGetValue(transfer.TransferId, out var current))
                transfers[transfer.TransferId] = transfer;
            else if (transfer.Revision > current.Revision)
                transfers[transfer.TransferId] = MergeTransfer(current, transfer);
        }

        return new StateDeltaDto(
            Latest(first.Daemon, second.Daemon),
            workflows.Values.ToList(),
            jobs.Values.ToList(),
            searches.Values.ToList(),
            transfers.Values.ToList(),
            Union(first.RemovedWorkflowIds, second.RemovedWorkflowIds),
            Union(first.RemovedJobIds, second.RemovedJobIds),
            Union(first.RemovedSearchJobIds, second.RemovedSearchJobIds),
            Union(first.RemovedTransferIds, second.RemovedTransferIds));
    }

    private static JobDeltaDto MergeJob(JobDeltaDto first, JobDeltaDto second)
    {
        if (second.Added != null)
            return second;

        if (first.Added is { } added)
        {
            var mergedAdded = added with
            {
                Revision = second.Revision,
                Display = second.Display ?? added.Display,
                Lifecycle = second.Lifecycle ?? added.Lifecycle,
                Discovery = second.Discovery ?? added.Discovery,
                Relationships = second.Relationships ?? added.Relationships,
            };
            return new JobDeltaDto(first.JobId, second.Revision, Added: mergedAdded);
        }

        return new JobDeltaDto(
            first.JobId,
            second.Revision,
            Display: second.Display ?? first.Display,
            Lifecycle: second.Lifecycle ?? first.Lifecycle,
            Discovery: second.Discovery ?? first.Discovery,
            Relationships: second.Relationships ?? first.Relationships);
    }

    private static TransferDeltaDto MergeTransfer(TransferDeltaDto first, TransferDeltaDto second)
    {
        if (second.Added != null)
            return second;

        if (first.Added is { } added)
        {
            var mergedAdded = added with
            {
                Revision = second.Revision,
                Status = second.Status ?? added.Status,
                Progress = second.Progress ?? added.Progress,
            };
            return new TransferDeltaDto(first.TransferId, second.Revision, Added: mergedAdded);
        }

        return new TransferDeltaDto(
            first.TransferId,
            second.Revision,
            Status: second.Status ?? first.Status,
            Progress: second.Progress ?? first.Progress);
    }

    private static DaemonStateDto? Latest(DaemonStateDto? first, DaemonStateDto? second)
        => second == null || first?.Revision > second.Revision ? first : second;

    private static IReadOnlyList<Guid> Union(
        IReadOnlyList<Guid> first,
        IReadOnlyList<Guid> second)
        => first.Concat(second).Distinct().ToList();
}
