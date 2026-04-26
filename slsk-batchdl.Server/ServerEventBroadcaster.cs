using Microsoft.AspNetCore.SignalR;
using Sldl.Core;
using Sldl.Core.Jobs;

namespace Sldl.Server;

public sealed class ServerEventBroadcaster : IDisposable
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private readonly EngineStateStore stateStore;
    private readonly ServerEventCoalescer coalescer;
    private long nextSequence;

    public ServerEventBroadcaster(EngineStateStore stateStore, EngineSupervisor supervisor, IHubContext<ServerEventHub> hubContext)
    {
        this.stateStore = stateStore;
        this.hubContext = hubContext;
        coalescer = new ServerEventCoalescer(PublishImmediate);
        stateStore.JobUpserted += summary => coalescer.Publish("job.upserted", summary);
        stateStore.WorkflowUpserted += summary => coalescer.Publish("workflow.upserted", summary);
        stateStore.SearchUpdated += update => coalescer.Publish("search.updated", update);
        supervisor.EngineCreated += AttachEngine;
    }

    private void AttachEngine(DownloadEngine engine)
    {
        new EngineEventDtoAdapter(GetSummary, coalescer.Publish).Attach(engine.Events);
    }

    private void PublishImmediate(string type, object payload)
    {
        var descriptor = ServerEventCatalog.Describe(type);
        var envelope = new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            descriptor.Category,
            descriptor.SnapshotInvalidation,
            payload);

        _ = hubContext.Clients.All.SendAsync("serverEvent", envelope);
    }

    public void Dispose()
    {
        coalescer.Dispose();
    }

    private JobSummaryDto GetSummary(Job job)
        => stateStore.GetJobSummary(job.Id) ?? new JobSummaryDto(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            EngineStateStore.GetJobKind(job),
            job.State.ToString(),
            job.ItemName,
            job.ToString(noInfo: true),
            job.FailureReason != FailureReason.None ? job.FailureReason.ToString() : null,
            job.FailureMessage,
            null,
            null,
            job.Config?.AppliedAutoProfiles?.ToList() ?? [],
            new PresentationHintsDto(ServerProtocol.PresentationDisplayModes.Node, null, job.DisplayId, null),
            []);

}
