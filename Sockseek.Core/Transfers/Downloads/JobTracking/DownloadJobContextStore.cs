using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.JobTracking;

internal sealed class DownloadJobContextStore
{
    private readonly ConcurrentDictionary<Guid, JobContext> contexts = new();

    public JobContext this[Guid jobId]
    {
        get => contexts[jobId];
        set => contexts[jobId] = value;
    }

    public JobContext Get(Job job) => contexts[job.Id];

    public bool ContainsKey(Guid jobId) => contexts.ContainsKey(jobId);

    public bool TryGetValue(Guid jobId, out JobContext context)
        => contexts.TryGetValue(jobId, out context!);
}
