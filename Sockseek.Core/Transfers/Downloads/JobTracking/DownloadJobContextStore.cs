using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.JobTracking;

internal sealed class DownloadJobContextStore
{
    private readonly ConcurrentDictionary<Guid, JobContext> contexts = new();
    private readonly ConcurrentDictionary<Guid, Guid> workflowIds = new();

    public JobContext this[Guid jobId]
    {
        get => contexts[jobId];
        set => contexts[jobId] = value;
    }

    public JobContext Get(Job job) => contexts[job.Id];

    public bool ContainsKey(Guid jobId) => contexts.ContainsKey(jobId);

    public bool TryGetValue(Guid jobId, out JobContext context)
        => contexts.TryGetValue(jobId, out context!);

    public void Set(Job job, JobContext context)
    {
        contexts[job.Id] = context;
        workflowIds[job.Id] = job.WorkflowId;
    }

    public void Set(Guid jobId, Guid workflowId, JobContext context)
    {
        contexts[jobId] = context;
        workflowIds[jobId] = workflowId;
    }

    public IReadOnlyList<Guid> RemoveWorkflow(Guid workflowId)
    {
        var removed = new List<Guid>();
        foreach (var pair in workflowIds.Where(pair => pair.Value == workflowId))
        {
            if (!workflowIds.TryRemove(pair.Key, out _))
                continue;
            contexts.TryRemove(pair.Key, out _);
            removed.Add(pair.Key);
        }
        return removed;
    }

    internal int Count => contexts.Count;
}
