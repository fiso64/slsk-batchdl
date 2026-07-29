using Microsoft.AspNetCore.SignalR;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Snapshots;

namespace Sockseek.Server;

/// <summary>Publishes coalesced v4 state and activity batches to scoped SignalR groups.</summary>
public sealed class ServerEventBroadcaster : IDisposable
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private readonly EngineStateStore stateStore;
    private readonly StateUpdateCoalescer coalescer;

    public event Action<StateUpdateBatchDto>? BatchPublished;

    public ServerEventBroadcaster(
        EngineStateStore stateStore,
        EngineSupervisor supervisor,
        IHubContext<ServerEventHub> hubContext)
    {
        this.stateStore = stateStore;
        this.hubContext = hubContext;
        coalescer = new StateUpdateCoalescer(PublishBatches);
        stateStore.StateBatchPublished += coalescer.Publish;
        supervisor.EngineCreated += AttachEngine;
    }

    private void AttachEngine(DownloadEngine engine)
        => new EngineActivityDtoAdapter(stateStore, GetSummary)
            .Attach(engine.Events, engine.SearchEvents);

    private void PublishBatches(IReadOnlyList<StateUpdateBatchDto> batches)
    {
        foreach (var batch in batches)
        {
            BatchPublished?.Invoke(batch);
            var clients = batch.Scope.Kind == StateStreamScopeKind.Daemon
                ? hubContext.Clients.Group(ServerEventHub.AllEventsGroup)
                : hubContext.Clients.Group(
                    ServerEventHub.WorkflowGroupName(batch.Scope.WorkflowId!.Value));
            _ = clients.SendAsync("stateUpdateBatch", batch);
        }
    }

    private JobSummaryDto GetSummary(JobSnapshot job)
        => stateStore.GetJobSummary(job.Id) ?? ServerSnapshotMapper.ToJobSummary(job);

    public void Dispose()
    {
        stateStore.StateBatchPublished -= coalescer.Publish;
        coalescer.Dispose();
    }
}
