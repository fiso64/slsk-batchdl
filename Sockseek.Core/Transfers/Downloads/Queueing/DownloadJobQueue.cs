using System.Threading.Channels;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Transfers.Downloads.Queueing;

internal sealed class DownloadJobQueue
{
    private readonly Channel<QueuedDownloadJob> channel =
        Channel.CreateUnbounded<QueuedDownloadJob>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Job job, DownloadSettings settings)
    {
        job.EnsureDisplayId();
        channel.Writer.TryWrite(QueuedDownloadJob.Root(job, settings));
    }

    public void Resume(Job job)
    {
        job.EnsureDisplayId();
        channel.Writer.TryWrite(QueuedDownloadJob.Resume(job));
    }

    public void Complete() => channel.Writer.Complete();

    public IAsyncEnumerable<QueuedDownloadJob> ReadAllAsync(CancellationToken ct)
        => channel.Reader.ReadAllAsync(ct);
}

internal sealed record QueuedDownloadJob(Job Job, DownloadSettings? Settings, bool IsResume)
{
    public static QueuedDownloadJob Root(Job job, DownloadSettings settings) => new(job, settings, false);
    public static QueuedDownloadJob Resume(Job job) => new(job, null, true);
}
