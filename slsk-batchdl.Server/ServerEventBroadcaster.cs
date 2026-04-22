using Microsoft.AspNetCore.SignalR;

namespace Sldl.Server;

public sealed class ServerEventBroadcaster
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private long nextSequence;

    public ServerEventBroadcaster(EngineStateStore stateStore, IHubContext<ServerEventHub> hubContext)
    {
        this.hubContext = hubContext;
        stateStore.JobUpserted += summary => Publish("job.upserted", summary);
        stateStore.WorkflowUpserted += summary => Publish("workflow.upserted", summary);
        stateStore.SearchUpdated += update => Publish("search.updated", update);
    }

    private void Publish(string type, object payload)
    {
        var envelope = new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            payload);

        _ = hubContext.Clients.All.SendAsync("serverEvent", envelope);
    }
}
