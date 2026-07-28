using Microsoft.AspNetCore.SignalR;
using Sockseek.Api;

namespace Sockseek.Server;

/// <summary>
/// SignalR hub for typed live state batches. A connection operates either in daemon
/// mode or workflow mode so one change is never delivered twice to that connection.
/// </summary>
public sealed class ServerEventHub : Hub
{
    private const string AllEventsGroupName = "events:all";
    private const string ModeKey = "sockseek:stream-mode";
    private const string DaemonMode = "daemon";
    private const string WorkflowMode = "workflow";

    public override async Task OnConnectedAsync()
    {
        var requested = Context.GetHttpContext()?.Request.Query["liveProtocol"].ToString();
        if (!int.TryParse(requested, out var version) || version != LiveProtocol.Version)
        {
            Context.Abort();
            throw new HubException(
                $"Incompatible live protocol. Server requires version {LiveProtocol.Version}.");
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribes this connection to the daemon-scoped stream containing every live
    /// workflow change and daemon-global activity edge.
    /// </summary>
    public Task SubscribeAll()
    {
        if (Context.Items.TryGetValue(ModeKey, out var mode) && Equals(mode, WorkflowMode))
            throw new HubException("Cannot mix daemon and workflow subscriptions on one connection.");

        Context.Items[ModeKey] = DaemonMode;
        return Groups.AddToGroupAsync(Context.ConnectionId, AllEventsGroupName);
    }

    /// <summary>
    /// Removes this connection from the all-events subscription.
    /// </summary>
    public async Task UnsubscribeAll()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AllEventsGroupName);
        Context.Items.Remove(ModeKey);
    }

    /// <summary>
    /// Subscribes this connection to events for one workflow.
    /// This is mainly useful for narrow clients such as a remote CLI tracking one submitted workflow.
    /// </summary>
    public Task SubscribeWorkflow(Guid workflowId)
    {
        if (Context.Items.TryGetValue(ModeKey, out var mode) && Equals(mode, DaemonMode))
            throw new HubException("Cannot mix daemon and workflow subscriptions on one connection.");

        Context.Items[ModeKey] = WorkflowMode;
        return Groups.AddToGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));
    }

    /// <summary>
    /// Removes this connection from the workflow-specific event subscription.
    /// </summary>
    public Task UnsubscribeWorkflow(Guid workflowId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));

    internal static string WorkflowGroupName(Guid workflowId)
        => $"workflow:{workflowId:N}";

    internal static string AllEventsGroup => AllEventsGroupName;
}
