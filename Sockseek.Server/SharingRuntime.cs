using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Api;
using Sockseek.Persistence.Sharing;
using Soulseek;

namespace Sockseek.Server;

/// <summary>
/// Daemon-lifetime owner of the public catalog, scanner, and upload runtime.
/// The neutral daemon Soulseek runtime owns the shared network session.
/// </summary>
public sealed class SharingRuntime : IAsyncDisposable
{
    private readonly EngineSettings settings;
    private readonly ShareCatalogManager catalogs;
    private readonly ShareScanCoordinator scans;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object countSync = new();
    private bool countPublicationRequested;
    private Task? countPublicationWorker;
    private Task? periodicTask;
    private Task? sessionStartupTask;
    private bool started;
    private readonly object stateSync = new();
    private readonly Dictionary<Guid, ShareScanStateDto> recentScans = [];
    private readonly Queue<Guid> recentScanOrder = [];
    private long scanRevision;
    private ShareScanStateDto? activeScan;
    private ShareScanStateDto? lastScan;
    private Task? manuallyStartedScan;
    private readonly DaemonSoulseekRuntime soulseek;
    private readonly IDisposable inboundRegistration;

    public SharingRuntime(
        EngineSettings settings,
        string dataDirectory,
        DaemonSoulseekRuntime soulseek)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.soulseek = soulseek ?? throw new ArgumentNullException(nameof(soulseek));
        string catalogDirectory = Path.Combine(
            Path.GetFullPath(dataDirectory),
            "sharing");
        catalogs = new ShareCatalogManager(catalogDirectory);
        scans = new ShareScanCoordinator(catalogs);

        SoulseekClientManager manager = soulseek.ClientManager;
        AccessPolicy = soulseek.AccessPolicy;
        Scheduler = new UploadScheduler(settings.Uploads);
        Uploads = new UploadCoordinator(
            catalogs,
            () => manager.Client,
            AccessPolicy,
            Scheduler);
        Adapter = new SoulseekSharingAdapter(
            catalogs,
            Uploads,
            AccessPolicy,
            settings.Uploads,
            () => manager.Client,
            soulseek.LocalProfile.Description,
            uploadServingEnabled: settings.ListenPort is not null,
            userPicture: soulseek.LocalProfile.Picture?.Bytes);
        ClientManager = manager;
        inboundRegistration = soulseek.InboundRequests.Attach(Adapter);
        ClientManager.StateChanged += OnClientStateChanged;
        scans.StateChanged += OnScanStateChanged;
        Uploads.QueueChanged += OnUploadQueueChanged;
    }

    public SoulseekClientManager ClientManager { get; }
    public ShareCatalogManager Catalogs => catalogs;
    public ShareScanCoordinator Scans => scans;
    public PeerAccessPolicy AccessPolicy { get; }
    public UploadScheduler Scheduler { get; }
    public UploadCoordinator Uploads { get; }
    public SoulseekSharingAdapter Adapter { get; }
    public string SettingsHash { get; private set; } = "";
    public event Action<SharingStateDto, UploadRuntimeStateDto>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started)
            return;
        started = true;
        SettingsHash = SharingSettingsFingerprint.Compute(settings.Sharing);
        bool ready = await catalogs.InitializeAsync(
            SettingsHash,
            cancellationToken).ConfigureAwait(false);

        if (settings.Sharing.Roots.Count > 0)
        {
            // Session startup and a potentially long first scan must not delay
            // the daemon API or download engine. Both have their own bounded,
            // observed lifetime and publish readiness as they complete.
            sessionStartupTask = StartSessionForSharingAsync(lifetime.Token);
            if (!ready || settings.Sharing.ScanOnStart)
                _ = StartScan();
        }

        if (settings.Sharing.RescanInterval is not null)
            periodicTask = RunPeriodicScansAsync(lifetime.Token);
        PublishState();
    }

    private async Task StartSessionForSharingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await soulseek.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await RequestCountPublication().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The manager keeps retrying transient failures. Permanent session
            // failures remain visible through readiness without taking down the
            // daemon or preventing ordinary non-network API work.
            SockseekLog.Daemon.Error(ex, "Initial sharing session startup failed");
            PublishState();
        }
    }

    public async ValueTask<ShareScanResult> ScanNowAsync(
        CancellationToken cancellationToken = default)
    {
        ShareScanResult result = await scans.ScanAsync(
            settings.Sharing,
            SettingsHash,
            cancellationToken).ConfigureAwait(false);
        // Catalog publication is authoritative even if the connected server
        // rejects or delays this derived count update. Reconnect retries it.
        await RequestCountPublication().ConfigureAwait(false);
        return result;
    }

    public (bool Started, ShareScanStateDto Scan)? StartScan()
    {
        if (lifetime.IsCancellationRequested)
            return null;

        lock (stateSync)
        {
            if (activeScan is not null)
                return (false, activeScan);
        }

        Task scanTask;
        try
        {
            scanTask = ScanNowAsync(lifetime.Token).AsTask();
        }
        catch (ShareScanAlreadyRunningException)
        {
            lock (stateSync)
                return activeScan is null ? null : (false, activeScan);
        }

        ShareScanStateDto? scan;
        lock (stateSync)
        {
            manuallyStartedScan = scanTask;
            scan = activeScan;
            if (scan is null
                && scans.State.GenerationId is { } completedId)
            {
                scan = recentScans.GetValueOrDefault(completedId);
            }
        }
        _ = ObserveManualScanAsync(scanTask);
        return scan is null ? null : (true, scan);
    }

    private async Task ObserveManualScanAsync(Task scanTask)
    {
        try
        {
            await scanTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SockseekLog.Daemon.Error(ex, "Manual share scan failed");
        }
        finally
        {
            lock (stateSync)
            {
                if (ReferenceEquals(manuallyStartedScan, scanTask))
                    manuallyStartedScan = null;
            }
        }
    }

    private async Task RunPeriodicScansAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(settings.Sharing.RescanInterval!.Value);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ScanNowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ShareScanAlreadyRunningException)
                {
                    // One elapsed interval coalesces behind the active scan.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SockseekLog.Daemon.Error(ex, "Periodic share scan failed");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnClientStateChanged(SoulseekClientStates state)
    {
        PublishState();
        if (!state.HasFlag(SoulseekClientStates.Connected)
            || !state.HasFlag(SoulseekClientStates.LoggedIn))
            return;
        _ = RequestCountPublication();
    }

    private void OnUploadQueueChanged()
    {
        PublishState();
    }

    private void OnScanStateChanged(ShareScanState state)
    {
        if (state.GenerationId is not { } scanId)
        {
            PublishState();
            return;
        }

        long revision = Interlocked.Increment(ref scanRevision);
        bool terminal = state.Phase is ShareScanPhase.Completed
            or ShareScanPhase.Cancelled
            or ShareScanPhase.Failed;
        bool cancellable = !terminal && state.Phase != ShareScanPhase.Cancelling;
        ShareScanResult? result = state.Result;
        ShareScanProgress? progress = state.Progress;
        var dto = new ShareScanStateDto(
            scanId,
            revision,
            state.Phase,
            state.StartedAtUtc ?? DateTimeOffset.UtcNow,
            state.FinishedAtUtc,
            result?.DirectoriesVisited ?? progress?.DirectoriesDiscovered ?? 0,
            result?.FilesIndexed ?? progress?.FilesDiscovered ?? 0,
            result?.BytesIndexed ?? progress?.BytesDiscovered ?? 0,
            result?.Errors.Count ?? progress?.ErrorCount ?? (state.ErrorCode is null ? 0 : 1),
            result?.Errors.Select(error => new ShareScanErrorSampleDto(
                    error.Code,
                    error.RelativePath,
                    error.Message))
                .ToArray() ?? [],
            cancellable
                ?
                [
                    new ResourceActionDto(
                        ServerResourceActionKind.Cancel,
                        "POST",
                        $"/api/sharing/scans/{scanId:D}/cancel"),
                ]
                : []);

        lock (stateSync)
        {
            if (terminal)
            {
                activeScan = null;
                lastScan = dto;
                recentScans[scanId] = dto;
                recentScanOrder.Enqueue(scanId);
                while (recentScanOrder.Count > 32)
                    recentScans.Remove(recentScanOrder.Dequeue());
            }
            else
            {
                activeScan = dto;
                recentScans[scanId] = dto;
            }
        }
        PublishState();
    }

    public SharingStateDto GetSharingState()
    {
        ShareCatalogMetadata? metadata = catalogs.CurrentMetadata;
        ShareScanStateDto? active;
        ShareScanStateDto? last;
        lock (stateSync)
        {
            active = activeScan;
            last = lastScan;
        }
        bool configured = settings.Sharing.Roots.Count > 0;
        bool catalogReady = metadata is not null;
        bool catalogStale = catalogReady && last?.Phase == ShareScanPhase.Failed;
        DaemonFeatureState state;
        string? reason;
        if (!configured)
        {
            state = DaemonFeatureState.Disabled;
            reason = "NotConfigured";
        }
        else if (!catalogReady)
        {
            state = DaemonFeatureState.Starting;
            reason = "CatalogUnavailable";
        }
        else if (catalogStale)
        {
            state = DaemonFeatureState.Degraded;
            reason = "LastScanFailed";
        }
        else if (metadata!.BrowseStatus != ShareBrowseStatus.Ready)
        {
            state = DaemonFeatureState.Degraded;
            reason = "BrowseUnavailable";
        }
        else if (!ClientManager.IsConnectedAndLoggedIn)
        {
            state = DaemonFeatureState.Degraded;
            reason = "SessionUnavailable";
        }
        else
        {
            state = DaemonFeatureState.Ready;
            reason = null;
        }
        return new SharingStateDto(
            state,
            reason,
            settings.Sharing.Roots
                .Select(root => root.EffectiveAlias)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            settings.PeerAccess.BlockedUsernames.Count,
            settings.PeerAccess.BlockedIpAddresses.Count,
            new ShareCatalogStateDto(
                metadata?.GenerationId,
                metadata?.DirectoryCount ?? 0,
                metadata?.FileCount ?? 0,
                metadata?.TotalBytes ?? 0,
                metadata?.BrowseStatus == ShareBrowseStatus.Ready,
                metadata?.BrowseLengthBytes,
                metadata?.CreatedAtUtc),
            active,
            last);
    }

    public UploadRuntimeStateDto GetUploadRuntimeState()
    {
        bool configured = settings.Sharing.Roots.Count > 0;
        bool listenerReady = ClientManager.IsConnectedAndLoggedIn
                             && settings.ListenPort is not null;
        bool catalogReady = catalogs.CurrentMetadata is not null;
        UploadQueueRuntimeSnapshot queue = Uploads.GetQueueSnapshot();
        bool accepting = configured
                         && listenerReady
                         && catalogReady
                         && queue.AcceptingUploads;
        DaemonFeatureState state = !configured
            ? DaemonFeatureState.Disabled
            : !catalogReady || !ClientManager.IsConnectedAndLoggedIn
                ? DaemonFeatureState.Starting
                : accepting
                    ? DaemonFeatureState.Ready
                    : DaemonFeatureState.Degraded;
        string? reason = accepting
            ? null
            : !configured
                ? "NotConfigured"
                : !catalogReady
                    ? "CatalogUnavailable"
                    : !listenerReady
                        ? "PeerListenerUnavailable"
                        : "QueueCapacity";
        return new UploadRuntimeStateDto(
            state,
            reason,
            accepting,
            queue.TotalSlots,
            queue.ActiveSlots,
            queue.QueuedFiles,
            queue.QueuedBytes,
            queue.QueueRevision,
            settings.Uploads.SpeedLimitKiBPerSecond);
    }

    public ShareScanStateDto? GetScan(Guid scanId)
    {
        lock (stateSync)
            return recentScans.GetValueOrDefault(scanId);
    }

    public bool CancelScan(Guid scanId)
    {
        lock (stateSync)
        {
            if (activeScan?.ScanId != scanId)
                return false;
        }
        return scans.Cancel();
    }

    private void PublishState()
    {
        SharingStateDto sharing = GetSharingState();
        UploadRuntimeStateDto uploads = GetUploadRuntimeState();
        SharingTelemetry.UpdateCatalog(catalogs.CurrentMetadata);
        SharingTelemetry.UpdateQueue(Uploads.GetQueueSnapshot());
        Action<SharingStateDto, UploadRuntimeStateDto>? handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (Action<SharingStateDto, UploadRuntimeStateDto> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(sharing, uploads);
            }
            catch
            {
                // Live-state observers are non-authoritative projections.
            }
        }
    }

    private Task RequestCountPublication()
    {
        lock (countSync)
        {
            countPublicationRequested = true;
            return countPublicationWorker ??= RunCountPublicationWorkerAsync();
        }
    }

    private async Task RunCountPublicationWorkerAsync()
    {
        // Ensure the request method can install the task before this worker
        // takes the same lock, even when no asynchronous client work is needed.
        await Task.Yield();
        while (true)
        {
            lock (countSync)
            {
                if (!countPublicationRequested)
                {
                    countPublicationWorker = null;
                    return;
                }
                countPublicationRequested = false;
            }

            await PublishCountsOnceSafeAsync().ConfigureAwait(false);
        }
    }

    private async Task PublishCountsOnceSafeAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await PublishCountsAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested)
                SockseekLog.Daemon.Warn("Publishing Soulseek share counts timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SockseekLog.Daemon.Warn(
                $"Failed to publish share counts: {SockseekLog.ExceptionSummary(ex)}");
        }
    }

    private Task PublishCountsAsync(CancellationToken cancellationToken)
    {
        ShareCatalogMetadata? metadata = catalogs.CurrentMetadata;
        ISoulseekClient? client = ClientManager.Client;
        if (client is null || !ClientManager.IsConnectedAndLoggedIn)
            return Task.CompletedTask;
        return client.SetSharedCountsAsync(
            checked((int)(metadata?.DirectoryCount ?? 0)),
            checked((int)(metadata?.FileCount ?? 0)),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        scans.Cancel();
        if (periodicTask is not null)
        {
            try
            {
                await periodicTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task? scanTask;
        lock (stateSync)
            scanTask = manuallyStartedScan;
        if (scanTask is not null)
        {
            try
            {
                await scanTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // The observer has already recorded and logged the terminal
                // scan failure; shutdown only needs to join the worker.
            }
        }

        if (sessionStartupTask is not null)
            await sessionStartupTask.ConfigureAwait(false);

        ClientManager.StateChanged -= OnClientStateChanged;
        scans.StateChanged -= OnScanStateChanged;
        Uploads.QueueChanged -= OnUploadQueueChanged;
        Task? countTask;
        lock (countSync)
            countTask = countPublicationWorker;
        if (countTask is not null)
            await countTask.ConfigureAwait(false);
        await Uploads.DisposeAsync().ConfigureAwait(false);
        inboundRegistration.Dispose();
        Adapter.Dispose();
        await catalogs.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
