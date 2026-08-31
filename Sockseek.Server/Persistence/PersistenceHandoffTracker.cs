using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Persistence;

/// <summary>
/// Bridges the short interval between engine-owned live state retirement and
/// visibility of its terminal job and search-projection state in SQLite.
/// Tracking is per workflow generation so reusing a workflow ID does not mix
/// independent retirements.
/// </summary>
internal sealed class PersistenceHandoffTracker : IPersistenceMutationObserver
{
    private readonly Lock gate = new();
    private readonly ILogger<PersistenceHandoffTracker> logger;
    private readonly Dictionary<Guid, Generation> activeByWorkflow = [];
    private readonly Dictionary<Guid, Generation> generationByJob = [];
    private readonly Dictionary<Guid, List<Generation>> generationsByWorkflow = [];

    public PersistenceHandoffTracker(ILogger<PersistenceHandoffTracker>? logger = null)
    {
        this.logger = logger ?? NullLogger<PersistenceHandoffTracker>.Instance;
    }

    public void RegisterJob(Guid workflowId, Guid jobId)
    {
        lock (gate)
        {
            if (generationByJob.ContainsKey(jobId))
                return;

            if (!activeByWorkflow.TryGetValue(workflowId, out var generation))
            {
                generation = CreateGenerationLocked(workflowId);
                activeByWorkflow.Add(workflowId, generation);
            }

            generation.JobIds.Add(jobId);
            generationByJob.Add(jobId, generation);
        }
    }

    public void BeginRetirement(
        Guid workflowId,
        IReadOnlyDictionary<Guid, long> terminalJobRevisions,
        IReadOnlyDictionary<Guid, long> searchCompletionRevisions)
    {
        lock (gate)
        {
            if (!activeByWorkflow.Remove(workflowId, out var generation))
            {
                if (terminalJobRevisions.Count == 0 && searchCompletionRevisions.Count == 0)
                    return;
                generation = CreateGenerationLocked(workflowId);
                generation.IsRetiring = true;
                foreach (Guid jobId in terminalJobRevisions.Keys.Concat(searchCompletionRevisions.Keys))
                {
                    generation.JobIds.Add(jobId);
                    generationByJob.TryAdd(jobId, generation);
                }
                FailGenerationLocked(generation, new InvalidOperationException(
                    $"Persistence handoff for workflow {workflowId:N} had no registered generation."));
                return;
            }

            generation.IsRetiring = true;
            foreach (var (jobId, revision) in terminalJobRevisions)
            {
                if (!ReferenceEquals(generationByJob.GetValueOrDefault(jobId), generation))
                {
                    FailGenerationLocked(generation, new InvalidOperationException(
                        $"Persistence handoff for workflow {workflowId:N} did not own terminal job {jobId:N}."));
                    return;
                }
                generation.RequiredJobRevisions[jobId] = revision;
            }
            foreach (var (jobId, revision) in searchCompletionRevisions)
            {
                if (!ReferenceEquals(generationByJob.GetValueOrDefault(jobId), generation))
                {
                    FailGenerationLocked(generation, new InvalidOperationException(
                        $"Persistence handoff for workflow {workflowId:N} did not own completed search {jobId:N}."));
                    return;
                }
                generation.RequiredSearchRevisions[jobId] = revision;
            }

            if (generation.JobIds.Count != generation.RequiredJobRevisions.Count)
            {
                FailGenerationLocked(generation, new InvalidOperationException(
                    $"Persistence handoff for workflow {workflowId:N} captured " +
                    $"{generation.RequiredJobRevisions.Count} terminal revisions for {generation.JobIds.Count} jobs."));
                return;
            }

            TryFailForLostRequiredMutationLocked(generation);
            TryCompleteLocked(generation);
        }
    }

    public Task WaitForJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        Task completion;
        lock (gate)
        {
            if (!generationByJob.TryGetValue(jobId, out var generation) || !generation.IsRetiring)
                return Task.CompletedTask;
            else
                completion = generation.Completion.Task;
        }
        return completion.WaitAsync(cancellationToken);
    }

    public Task WaitForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        Task completion;
        lock (gate)
        {
            if (!generationsByWorkflow.TryGetValue(workflowId, out var generations))
                return Task.CompletedTask;
            else
            {
                Task[] pending = generations
                    .Where(generation => generation.IsRetiring)
                    .Select(generation => generation.Completion.Task)
                    .ToArray();
                if (pending.Length == 0)
                    return Task.CompletedTask;
                completion = Task.WhenAll(pending);
            }
        }
        return completion.WaitAsync(cancellationToken);
    }

    public Task WaitForAllAsync(CancellationToken cancellationToken)
    {
        Task completion;
        lock (gate)
        {
            Task[] pending = generationsByWorkflow.Values
                .SelectMany(generations => generations)
                .Where(generation => generation.IsRetiring)
                .Select(generation => generation.Completion.Task)
                .ToArray();
            if (pending.Length == 0)
                return Task.CompletedTask;
            completion = Task.WhenAll(pending);
        }
        return completion.WaitAsync(cancellationToken);
    }

    public void Committed(IReadOnlyList<PersistenceMutation> mutations)
    {
        lock (gate)
        {
            var touched = new HashSet<Generation>();
            foreach (var mutation in mutations)
            {
                switch (mutation)
                {
                    case JobPersistenceMutation job
                        when generationByJob.TryGetValue(job.JobId, out var generation):
                        SetMaximum(generation.CommittedJobRevisions, job.JobId, job.Revision);
                        touched.Add(generation);
                        break;
                    case SearchTerminalPersistenceMutation search
                        when generationByJob.TryGetValue(search.Completion.SearchJobId, out var generation):
                        SetMaximum(
                            generation.CommittedSearchRevisions,
                            search.Completion.SearchJobId,
                            search.Completion.Revision);
                        touched.Add(generation);
                        break;
                    case SearchCompletionPersistenceMutation search
                        when generationByJob.TryGetValue(search.SearchJobId, out var generation):
                        SetMaximum(
                            generation.CommittedSearchRevisions,
                            search.SearchJobId,
                            search.Revision);
                        touched.Add(generation);
                        break;
                }
            }

            foreach (var generation in touched)
                TryCompleteLocked(generation);
        }
    }

    public void PermanentlyFailed(
        IReadOnlyList<PersistenceMutation> mutations,
        Exception exception)
    {
        lock (gate)
        {
            var affected = new HashSet<Generation>();
            foreach (PersistenceMutation mutation in mutations)
            {
                switch (mutation)
                {
                    case JobPersistenceMutation { Priority: PersistenceMutationPriority.Terminal } job
                        when generationByJob.TryGetValue(job.JobId, out var generation):
                        SetLatestFailure(
                            generation.FailedJobMutations,
                            job.JobId,
                            new FailedMutation(job.Revision, Describe(job), exception));
                        affected.Add(generation);
                        break;
                    case SearchTerminalPersistenceMutation search
                        when generationByJob.TryGetValue(search.Completion.SearchJobId, out var generation):
                        SetLatestFailure(
                            generation.FailedSearchMutations,
                            search.Completion.SearchJobId,
                            new FailedMutation(search.Completion.Revision, Describe(search), exception));
                        affected.Add(generation);
                        break;
                    case SearchCompletionPersistenceMutation search
                        when generationByJob.TryGetValue(search.SearchJobId, out var generation):
                        SetLatestFailure(
                            generation.FailedSearchMutations,
                            search.SearchJobId,
                            new FailedMutation(search.Revision, Describe(search), exception));
                        affected.Add(generation);
                        break;
                }
            }

            foreach (Generation generation in affected)
                TryFailForLostRequiredMutationLocked(generation);
        }
    }

    private void TryFailForLostRequiredMutationLocked(Generation generation)
    {
        if (!generation.IsRetiring || generation.Completion.Task.IsCompleted)
            return;

        var requiredLosses = new List<FailedMutation>();
        foreach (var (jobId, requiredRevision) in generation.RequiredJobRevisions)
        {
            if (generation.CommittedJobRevisions.GetValueOrDefault(jobId) < requiredRevision
                && generation.FailedJobMutations.GetValueOrDefault(jobId) is { } failed
                && failed.Revision >= requiredRevision)
                requiredLosses.Add(failed);
        }
        foreach (var (jobId, requiredRevision) in generation.RequiredSearchRevisions)
        {
            if (generation.CommittedSearchRevisions.GetValueOrDefault(jobId) < requiredRevision
                && generation.FailedSearchMutations.GetValueOrDefault(jobId) is { } failed
                && failed.Revision >= requiredRevision)
                requiredLosses.Add(failed);
        }
        if (requiredLosses.Count == 0)
            return;

        string items = string.Join(", ", requiredLosses.Take(3).Select(failure => failure.Description));
        if (requiredLosses.Count > 3)
            items += $", +{requiredLosses.Count - 3} more";
        FailGenerationLocked(generation, new PersistenceHandoffException(
            $"Historical state for workflow {generation.WorkflowId:N} is unavailable because " +
            $"persistence permanently lost required terminal mutation batch [{items}].",
            requiredLosses[0].Exception));
    }

    private void TryCompleteLocked(Generation generation)
    {
        if (generation.Completion.Task.IsCompleted
            || !generation.IsRetiring
            || generation.RequiredJobRevisions.Any(required =>
                generation.CommittedJobRevisions.GetValueOrDefault(required.Key) < required.Value)
            || generation.RequiredSearchRevisions.Any(required =>
                generation.CommittedSearchRevisions.GetValueOrDefault(required.Key) < required.Value))
            return;

        generation.Completion.TrySetResult();
        foreach (Guid jobId in generation.JobIds)
            generationByJob.Remove(jobId);
        if (generationsByWorkflow.TryGetValue(generation.WorkflowId, out var generations))
        {
            generations.Remove(generation);
            if (generations.Count == 0)
                generationsByWorkflow.Remove(generation.WorkflowId);
        }
    }

    private Generation CreateGenerationLocked(Guid workflowId)
    {
        var generation = new Generation(workflowId);
        if (!generationsByWorkflow.TryGetValue(workflowId, out var generations))
        {
            generations = [];
            generationsByWorkflow.Add(workflowId, generations);
        }
        generations.Add(generation);
        return generation;
    }

    private void FailGenerationLocked(Generation generation, Exception exception)
    {
        if (generation.Completion.Task.IsCompleted)
            return;
        Exception failure = exception is PersistenceHandoffException
            ? exception
            : new PersistenceHandoffException(
                $"Historical state for workflow {generation.WorkflowId:N} is unavailable because " +
                "its live-to-persistent handoff invariant failed.",
                exception);
        ServerLogMessages.PersistenceHandoffFailed(logger, generation.WorkflowId, failure);
        generation.Completion.TrySetException(failure);
    }

    private static void SetMaximum(Dictionary<Guid, long> revisions, Guid id, long revision)
    {
        if (revision > revisions.GetValueOrDefault(id))
            revisions[id] = revision;
    }

    private static void SetLatestFailure(
        Dictionary<Guid, FailedMutation> failures,
        Guid id,
        FailedMutation failure)
    {
        if (failures.GetValueOrDefault(id) is not { } current || failure.Revision > current.Revision)
            failures[id] = failure;
    }

    private static string Describe(PersistenceMutation mutation)
        => $"{mutation.GetType().Name}:{mutation.EntityId:N}@r{mutation.Revision}";

    private sealed class Generation(Guid workflowId)
    {
        public Guid WorkflowId { get; } = workflowId;
        public HashSet<Guid> JobIds { get; } = [];
        public Dictionary<Guid, long> RequiredJobRevisions { get; } = [];
        public Dictionary<Guid, long> RequiredSearchRevisions { get; } = [];
        public Dictionary<Guid, long> CommittedJobRevisions { get; } = [];
        public Dictionary<Guid, long> CommittedSearchRevisions { get; } = [];
        public Dictionary<Guid, FailedMutation> FailedJobMutations { get; } = [];
        public Dictionary<Guid, FailedMutation> FailedSearchMutations { get; } = [];
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRetiring { get; set; }
    }

    private sealed record FailedMutation(long Revision, string Description, Exception Exception);
}

internal sealed class PersistenceHandoffException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
