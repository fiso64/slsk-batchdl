using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Soulseek;

namespace Sockseek.Server;

/// <summary>Daemon-lifetime owner of the one shared Soulseek session.</summary>
public sealed class DaemonSoulseekRuntime : IAsyncDisposable
{
    private readonly EngineSettings settings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object startupGate = new();
    private Task? startupTask;

    public DaemonSoulseekRuntime(
        EngineSettings settings,
        Func<EngineSettings, ISoulseekClient>? clientFactory = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InboundRequests = new SoulseekInboundRequestRouter();
        AccessPolicy = new PeerAccessPolicy(settings.PeerAccess);
        ClientManager = new SoulseekClientManager(
            settings,
            clientFactory?.Invoke(settings),
            InboundRequests);
    }

    public SoulseekClientManager ClientManager { get; }
    public SoulseekInboundRequestRouter InboundRequests { get; }
    public PeerAccessPolicy AccessPolicy { get; }

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        lock (startupGate)
        {
            if (startupTask is { IsFaulted: true } or { IsCanceled: true })
                startupTask = null;
            return startupTask ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        await ClientManager.EnsureConnectedAndLoggedInAsync(
            settings,
            linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        Task? pending;
        lock (startupGate)
            pending = startupTask;
        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* The observed startup failure was already published/logged. */ }
        }
        await ClientManager.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
