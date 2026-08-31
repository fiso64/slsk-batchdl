using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Snapshots;

namespace Sockseek.Server;

/// <summary>Publishes coalesced state and activity batches to scoped SignalR groups.</summary>
public sealed class ServerEventBroadcaster : IDisposable, IAsyncDisposable, IHostedService
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private readonly EngineStateStore stateStore;
    private readonly EngineSupervisor supervisor;
    private readonly StateUpdateCoalescer coalescer;
    private readonly BoundedStateBatchDispatcher dispatcher;
    private readonly ILogger<ServerEventBroadcaster> logger;
    private int disposeState;

    public event Action<StateUpdateBatchDto>? BatchPublished;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal ServerEventBroadcaster(
        EngineStateStore stateStore,
        EngineSupervisor supervisor,
        IHubContext<ServerEventHub> hubContext)
        : this(
            stateStore,
            supervisor,
            hubContext,
            NullLogger<ServerEventBroadcaster>.Instance,
            NullLoggerFactory.Instance)
    {
    }

    public ServerEventBroadcaster(
        EngineStateStore stateStore,
        EngineSupervisor supervisor,
        IHubContext<ServerEventHub> hubContext,
        ILogger<ServerEventBroadcaster> logger,
        ILoggerFactory loggerFactory)
    {
        this.stateStore = stateStore;
        this.supervisor = supervisor;
        this.hubContext = hubContext;
        this.logger = logger;
        dispatcher = new BoundedStateBatchDispatcher(
            SendBatchAsync,
            logger: loggerFactory.CreateLogger<BoundedStateBatchDispatcher>());
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
            if (BatchPublished is { } handlers)
            {
                foreach (Action<StateUpdateBatchDto> handler in handlers.GetInvocationList())
                {
                    try { handler(batch); }
                    catch (Exception ex)
                    {
                        ServerLogMessages.LiveBatchObserverFailed(logger, ex);
                    }
                }
            }
            dispatcher.TryPublish(batch);
        }
    }

    private async Task SendBatchAsync(
        StateUpdateBatchDto batch,
        CancellationToken cancellationToken)
    {
        var clients = batch.Scope.Kind switch
        {
            StateStreamScopeKind.Daemon => hubContext.Clients.Group(ServerEventHub.AllEventsGroup),
            StateStreamScopeKind.Workflow => hubContext.Clients.Group(
                ServerEventHub.WorkflowGroupName(batch.Scope.WorkflowId!.Value)),
            StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom
                => hubContext.Clients.Group(ServerEventHub.ChatGroupName(batch.Scope)),
            StateStreamScopeKind.UserBrowse => hubContext.Clients.Group(
                ServerEventHub.UserBrowseGroupName(batch.Scope.UserBrowseId!.Value)),
            _ => throw new ArgumentOutOfRangeException(),
        };
        await clients.SendAsync(
            "stateUpdateBatch", batch, cancellationToken).ConfigureAwait(false);
    }

    private JobSummaryDto GetSummary(JobSnapshot job)
        => stateStore.GetJobSummary(job.Id) ?? ServerSnapshotMapper.ToJobSummary(job);

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;
        stateStore.StateBatchPublished -= coalescer.Publish;
        supervisor.EngineCreated -= AttachEngine;
        coalescer.Dispose();
        await dispatcher.DisposeAsync().ConfigureAwait(false);
    }
}
