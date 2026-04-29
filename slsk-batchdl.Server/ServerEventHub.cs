using Microsoft.AspNetCore.SignalR;

namespace Sldl.Server;

public sealed class ServerEventHub : Hub
{
    private const string AllEventsGroupName = "events:all";

    public Task SubscribeAll()
        => Groups.AddToGroupAsync(Context.ConnectionId, AllEventsGroupName);

    public Task UnsubscribeAll()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AllEventsGroupName);

    public Task SubscribeWorkflow(Guid workflowId)
        => Groups.AddToGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));

    public Task UnsubscribeWorkflow(Guid workflowId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkflowGroupName(workflowId));

    internal static string WorkflowGroupName(Guid workflowId)
        => $"workflow:{workflowId:N}";

    internal static string AllEventsGroup => AllEventsGroupName;
}
