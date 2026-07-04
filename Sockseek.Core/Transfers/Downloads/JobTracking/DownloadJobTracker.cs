using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Transfers.Downloads.JobTracking;

internal sealed class DownloadJobTracker
{
    private readonly DownloadEvents events;
    private readonly ConcurrentDictionary<Guid, Job> jobsById = new();
    private readonly ConcurrentDictionary<int, Job> jobsByDisplayId = new();

    public DownloadJobTracker(DownloadEvents events)
    {
        this.events = events;
    }

    public IEnumerable<Job> Jobs => jobsById.Values;

    public Job? GetJob(Guid id) => jobsById.TryGetValue(id, out var job) ? job : null;

    public Job? GetJob(int displayId) => jobsByDisplayId.TryGetValue(displayId, out var job) ? job : null;

    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => jobsById.Values
        .Where(job => job.WorkflowId == workflowId)
        .OrderBy(job => job.DisplayId)
        .ToList();

    public void Register(Job job, Job? parent)
    {
        job.EnsureDisplayId();
        bool firstRegistration = jobsById.TryAdd(job.Id, job);
        jobsByDisplayId[job.DisplayId] = job;

        if (!firstRegistration)
            return;

        job.StateChanged += (_, transition) =>
        {
            events.RaiseJobStateChanged(job);
            if (transition.ActivityChanged
                && transition.After.LifecycleState == JobLifecycleState.Running
                && transition.After.ActivityPhase != JobActivityPhase.None)
            {
                events.RaiseJobActivityChanged(job, job.ActivityPhase, job.ActivityUntilUtc);
            }
        };

        events.RaiseJobRegistered(job, parent);
    }
}
