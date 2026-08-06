using System.Threading.Channels;
using Sockseek.Core;
using Sockseek.Core.Chat;
using Soulseek;

namespace Sockseek.Server;

/// <summary>
/// Minimal bounded protocol adapter used when durable chat is unavailable. It
/// acknowledges only intentional blocked/invalid discards; valid DMs remain
/// unacknowledged so the server can replay them after persistence is restored.
/// </summary>
internal sealed class DisabledChatIngress : IAsyncDisposable
{
    private readonly DaemonSoulseekRuntime soulseek;
    private readonly Channel<(ISoulseekClient Client, int Id)> acknowledgements;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<ISoulseekClient> attached = new(ReferenceEqualityComparer.Instance);
    private readonly object gate = new();
    private readonly Task worker;

    public DisabledChatIngress(DaemonSoulseekRuntime soulseek)
    {
        this.soulseek = soulseek;
        acknowledgements = Channel.CreateBounded<(ISoulseekClient, int)>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        soulseek.ClientManager.ClientCreated += Attach;
        if (soulseek.ClientManager.Client is { } client)
            Attach(client);
        worker = Task.Run(() => RunAsync(lifetime.Token));
    }

    private void Attach(ISoulseekClient client)
    {
        lock (gate)
        {
            if (!attached.Add(client))
                return;
        }
        client.PrivateMessageReceived += OnPrivateMessage;
    }

    private void OnPrivateMessage(object? sender, PrivateMessageReceivedEventArgs args)
    {
        try
        {
            bool discard = soulseek.AccessPolicy.IsUsernameBlocked(args.Username);
            if (!discard)
            {
                ChatIdentity.NormalizeUsername(args.Username);
                ChatIdentity.ValidateMessage(args.Message);
                return;
            }
        }
        catch (ArgumentException)
        {
            // Invalid input is an intentional discard.
        }
        catch (Exception ex)
        {
            SockseekLog.Daemon.Warn($"Disabled chat callback failed: {SockseekLog.ExceptionSummary(ex)}");
            return;
        }

        if (sender is ISoulseekClient client
            && !acknowledgements.Writer.TryWrite((client, args.Id)))
        {
            ChatTelemetry.RecordDropped("private", "disabled_capacity");
            SockseekLog.Daemon.Warn("Disabled chat discard queue is full; the message remains replayable.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in acknowledgements.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await item.Client.AcknowledgePrivateMessageAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    ChatTelemetry.RecordAcknowledged("disabled_discard");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    SockseekLog.Daemon.Warn($"Discarded private-message ACK failed: {SockseekLog.ExceptionSummary(ex)}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        soulseek.ClientManager.ClientCreated -= Attach;
        ISoulseekClient[] clients;
        lock (gate)
            clients = attached.ToArray();
        foreach (ISoulseekClient client in clients)
            client.PrivateMessageReceived -= OnPrivateMessage;
        acknowledgements.Writer.TryComplete();
        Task completed = await Task.WhenAny(
            worker,
            Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (!ReferenceEquals(completed, worker))
            lifetime.Cancel();
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
