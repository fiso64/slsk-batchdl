using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Sockseek.Api;

public enum LiveSubscriptionMode
{
    None,
    Daemon,
    Workflow,
    Chat,
    DaemonAndChat,
}

/// <summary>
/// Reusable SignalR live client. It owns subscriptions, buffering, HTTP snapshot
/// handoff, gap recovery, reconnect recovery, and a shared <see cref="DaemonClientStore"/>.
/// </summary>
public sealed class SockseekLiveClient : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SockseekApiClient api;
    private readonly HubConnection connection;
    private readonly ConcurrentDictionary<StateStreamScopeDto, ScopeSession> sessions = [];
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool protocolChecked;
    private bool disposed;

    public DaemonClientStore Store { get; } = new();
    public LiveSubscriptionMode Mode { get; private set; }

    public event Action<DaemonClientUpdate>? Updated;
    public event Action<StateSnapshotDto>? SnapshotApplied;
    public event Action<ActivityEventDto>? ActivityReceived;
    public event Action<StateStreamScopeDto, Exception>? SynchronizationFailed;

    public SockseekLiveClient(string serverUrl, JsonSerializerOptions? jsonOptions = null)
        : this(SockseekApiClient.CreateHttpClient(serverUrl), ownsHttp: true, jsonOptions)
    {
    }

    public SockseekLiveClient(
        HttpClient http,
        bool ownsHttp = false,
        JsonSerializerOptions? jsonOptions = null)
    {
        this.http = http;
        this.ownsHttp = ownsHttp;
        var options = jsonOptions ?? SockseekApiJson.CreateSerializerOptions();
        api = new SockseekApiClient(http, options);
        var baseUri = http.BaseAddress
            ?? throw new ArgumentException("The HTTP client requires a daemon BaseAddress.", nameof(http));

        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, $"api/events?liveProtocol={LiveProtocol.Version}"))
            .AddJsonProtocol(json =>
            {
                json.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                SockseekApiJson.ConfigureSerializerOptions(json.PayloadSerializerOptions);
            })
            .WithAutomaticReconnect()
            .Build();
        connection.On<StateUpdateBatchDto>("stateUpdateBatch", HandleBatch);
        connection.Reconnected += _ => RecoverAfterReconnectAsync();
    }

    public async Task StartDaemonAsync(CancellationToken ct = default)
    {
        await lifecycleGate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (Mode == LiveSubscriptionMode.Workflow)
                throw new InvalidOperationException("Cannot mix daemon and workflow subscriptions on one live client.");
            if (Mode is LiveSubscriptionMode.Daemon or LiveSubscriptionMode.DaemonAndChat)
                return;

            await EnsureConnectedAsync(ct);
            Mode = Mode == LiveSubscriptionMode.Chat
                ? LiveSubscriptionMode.DaemonAndChat
                : LiveSubscriptionMode.Daemon;
            var scope = StateStreamScopeDto.Daemon;
            var session = sessions.GetOrAdd(scope, static _ => new ScopeSession());
            bool subscribed = false;
            try
            {
                session.BeginBuffering();
                await connection.InvokeAsync("SubscribeAll", ct);
                subscribed = true;
                await RecoverScopeAsync(scope, ct);
            }
            catch
            {
                await RollBackInitialSubscriptionAsync(scope, session, subscribed);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StartWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        await lifecycleGate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (Mode is not (LiveSubscriptionMode.None or LiveSubscriptionMode.Workflow))
                throw new InvalidOperationException("Cannot mix workflow and daemon/chat subscriptions on one live client.");

            await EnsureConnectedAsync(ct);
            Mode = LiveSubscriptionMode.Workflow;
            var scope = StateStreamScopeDto.Workflow(workflowId);
            if (sessions.ContainsKey(scope))
                return;

            var session = sessions.GetOrAdd(scope, static _ => new ScopeSession());
            bool subscribed = false;
            try
            {
                session.BeginBuffering();
                await connection.InvokeAsync("SubscribeWorkflow", workflowId, ct);
                subscribed = true;
                await RecoverScopeAsync(scope, ct);
            }
            catch
            {
                await RollBackInitialSubscriptionAsync(scope, session, subscribed);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public Task StartConversationAsync(Guid conversationId, CancellationToken ct = default)
        => StartChatAsync(StateStreamScopeDto.ChatConversation(conversationId), ct);

    public Task StartRoomAsync(Guid roomId, CancellationToken ct = default)
        => StartChatAsync(StateStreamScopeDto.ChatRoom(roomId), ct);

    private async Task StartChatAsync(StateStreamScopeDto scope, CancellationToken ct)
    {
        await lifecycleGate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (Mode == LiveSubscriptionMode.Workflow)
                throw new InvalidOperationException("Cannot mix chat and workflow subscriptions on one live client.");
            await EnsureConnectedAsync(ct);
            Mode = Mode == LiveSubscriptionMode.Daemon
                ? LiveSubscriptionMode.DaemonAndChat
                : Mode == LiveSubscriptionMode.None
                    ? LiveSubscriptionMode.Chat
                    : Mode;
            if (sessions.ContainsKey(scope))
                return;
            var session = sessions.GetOrAdd(scope, static _ => new ScopeSession());
            bool subscribed = false;
            try
            {
                session.BeginBuffering();
                await connection.InvokeAsync("SubscribeChat", scope, ct);
                subscribed = true;
                await RecoverScopeAsync(scope, ct);
            }
            catch
            {
                await RollBackInitialSubscriptionAsync(scope, session, subscribed);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopChatAsync(StateStreamScopeDto scope, CancellationToken ct = default)
    {
        if (scope.Kind is not (StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
            throw new ArgumentException("A chat scope is required.", nameof(scope));
        await lifecycleGate.WaitAsync(ct);
        try
        {
            if (sessions.TryRemove(scope, out var session))
            {
                if (connection.State == HubConnectionState.Connected)
                    await connection.InvokeAsync("UnsubscribeChat", scope, ct);
                session.Dispose();
                Store.RemoveChatTarget(scope);
            }
            bool hasDaemon = sessions.ContainsKey(StateStreamScopeDto.Daemon);
            bool hasChat = sessions.Keys.Any(item => item.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom);
            Mode = (hasDaemon, hasChat) switch
            {
                (true, true) => LiveSubscriptionMode.DaemonAndChat,
                (true, false) => LiveSubscriptionMode.Daemon,
                (false, true) => LiveSubscriptionMode.Chat,
                _ => LiveSubscriptionMode.None,
            };
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<CursorPage<WorkflowSummaryDto>> LoadWorkflowHistoryPageAsync(
        string? cursor = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var page = await api.GetWorkflowsPageAsync(cursor, limit, ct);
        Store.MergeWorkflowHistory(page.Items);
        return page;
    }

    public async Task<CursorPage<JobSummaryDto>> LoadJobHistoryPageAsync(
        JobQuery query,
        string? cursor = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var page = await api.GetJobsPageAsync(query, cursor, limit, ct);
        Store.MergeJobHistory(page.Items);
        return page;
    }

    public SharingStateDto? GetSharing()
        => Store.GetSharing();

    public UploadRuntimeStateDto? GetUploadRuntime()
        => Store.GetUploadRuntime();

    public IReadOnlyList<TransferStateDto> GetActiveTransfers()
        => Store.GetActiveTransfers();

    public Task<SharingStateDto> GetSharingAsync(CancellationToken ct = default)
        => api.GetSharingAsync(ct);

    public Task<StartShareScanResponseDto> StartShareScanAsync(
        CancellationToken ct = default)
        => api.StartShareScanAsync(ct);

    public Task<ShareScanStateDto?> GetShareScanAsync(
        Guid scanId,
        CancellationToken ct = default)
        => api.GetShareScanAsync(scanId, ct);

    public async Task CancelShareScanAsync(
        Guid scanId,
        CancellationToken ct = default)
        => _ = await api.CancelShareScanAsync(scanId, ct);

    public async Task CancelTransferAsync(
        Guid transferId,
        CancellationToken ct = default)
        => _ = await api.CancelTransferAsync(transferId, ct);

    public Task<LiveTransferPageDto> LoadLiveTransferPageAsync(
        LiveTransferFilter? filter = null,
        string? cursor = null,
        int limit = 100,
        CancellationToken ct = default)
        => api.LoadLiveTransferPageAsync(filter, cursor, limit, ct);

    public Task<CursorPage<TransferHistoryDto>> LoadTransferHistoryPageAsync(
        TransferHistoryFilter? filter = null,
        string? cursor = null,
        int limit = 100,
        CancellationToken ct = default)
        => api.GetTransfersPageAsync(filter, cursor, limit, ct);

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!protocolChecked)
        {
            var info = await api.GetServerInfoAsync(ct);
            if (info.LiveProtocolVersion != LiveProtocol.Version)
            {
                throw new InvalidOperationException(
                    $"Incompatible live protocol: client requires {LiveProtocol.Version}, "
                    + $"server exposes {info.LiveProtocolVersion}.");
            }
            protocolChecked = true;
        }

        if (connection.State == HubConnectionState.Disconnected)
            await connection.StartAsync(ct);
    }

    private void HandleBatch(StateUpdateBatchDto batch)
    {
        if (!sessions.TryGetValue(batch.Scope, out var session))
            return;

        if (session.TryBuffer(batch))
            return;
        if (session.IsDisposed)
            return;

        var update = Store.Apply(batch);
        Publish(update);
        if (update.Status == DaemonClientApplyStatus.RecoveryRequired)
        {
            session.BeginBuffering(batch);
            ScheduleRecovery(batch.Scope);
        }
    }

    private async Task RecoverAfterReconnectAsync()
    {
        var scopes = sessions.Keys.ToList();
        foreach (var scope in scopes)
            if (sessions.TryGetValue(scope, out ScopeSession? session))
                session.BeginBuffering();

        if (Mode is LiveSubscriptionMode.Daemon or LiveSubscriptionMode.DaemonAndChat)
            await connection.InvokeAsync("SubscribeAll");
        if (Mode == LiveSubscriptionMode.Workflow)
        {
            foreach (var scope in scopes.Where(scope => scope.Kind == StateStreamScopeKind.Workflow))
                await connection.InvokeAsync("SubscribeWorkflow", scope.WorkflowId!.Value);
        }
        if (Mode is LiveSubscriptionMode.Chat or LiveSubscriptionMode.DaemonAndChat)
        {
            foreach (var scope in scopes.Where(scope => scope.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
                await connection.InvokeAsync("SubscribeChat", scope);
        }

        await Task.WhenAll(scopes.Select(RecoverScopeSafelyAsync));
    }

    private void ScheduleRecovery(StateStreamScopeDto scope)
    {
        if (!sessions.TryGetValue(scope, out ScopeSession? session))
            return;
        if (!session.TryScheduleRecovery())
            return;
        _ = RecoverScopeSafelyAsync(scope);
    }

    private async Task RecoverScopeSafelyAsync(StateStreamScopeDto scope)
    {
        if (!sessions.TryGetValue(scope, out ScopeSession? session))
            return;
        try
        {
            int attempt = 0;
            while (!lifetime.IsCancellationRequested && !session.IsDisposed)
            {
                try
                {
                    await RecoverScopeAsync(scope, lifetime.Token);
                    return;
                }
                catch (OperationCanceledException) when (
                    lifetime.IsCancellationRequested || session.IsDisposed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SynchronizationFailed?.Invoke(scope, ex);
                    int delayMs = Math.Min(5_000, 100 * (1 << Math.Min(attempt++, 5)));
                    try
                    {
                        await Task.Delay(delayMs, lifetime.Token);
                    }
                    catch (OperationCanceledException) when (
                        lifetime.IsCancellationRequested || session.IsDisposed)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            session.CompleteScheduledRecovery();
        }
    }

    private async Task RecoverScopeAsync(StateStreamScopeDto scope, CancellationToken ct)
    {
        if (!sessions.TryGetValue(scope, out ScopeSession? session))
            return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, session.CancellationToken);
        CancellationToken recoveryToken = linked.Token;
        await session.RecoveryGate.WaitAsync(recoveryToken);
        try
        {
            while (true)
            {
                var snapshot = scope.Kind switch
                {
                    StateStreamScopeKind.Daemon => await api.GetDaemonSnapshotAsync(recoveryToken),
                    StateStreamScopeKind.Workflow => await api.GetWorkflowSnapshotAsync(scope.WorkflowId!.Value, recoveryToken),
                    StateStreamScopeKind.ChatConversation => await api.GetConversationSnapshotAsync(scope.ChatTargetId!.Value, recoveryToken),
                    StateStreamScopeKind.ChatRoom => await api.GetRoomSnapshotAsync(scope.ChatTargetId!.Value, recoveryToken),
                    _ => throw new ArgumentOutOfRangeException(),
                };

                var snapshotUpdate = Store.ApplySnapshot(snapshot);
                SnapshotApplied?.Invoke(snapshot);
                Publish(snapshotUpdate);

                bool restartRecovery = false;
                while (true)
                {
                    BufferedDrain drain = session.Drain();
                    if (drain.Overflowed)
                    {
                        restartRecovery = true;
                        break;
                    }
                    if (drain.Batches.Count == 0)
                    {
                        if (session.EndBufferingIfEmpty())
                            return;
                        continue;
                    }

                    foreach (var batch in drain.Batches
                        .OrderBy(batch => batch.Sequence)
                        .ThenBy(batch => batch.PreviousSequence))
                    {
                        var update = Store.Apply(batch);
                        Publish(update);
                        if (update.Status == DaemonClientApplyStatus.RecoveryRequired)
                        {
                            restartRecovery = true;
                            break;
                        }
                    }

                    if (restartRecovery)
                        break;
                }
            }
        }
        finally
        {
            session.RecoveryGate.Release();
        }
    }

    private async Task RollBackInitialSubscriptionAsync(
        StateStreamScopeDto scope,
        ScopeSession session,
        bool subscribed)
    {
        if (subscribed && connection.State == HubConnectionState.Connected)
        {
            try
            {
                if (scope.Kind == StateStreamScopeKind.Daemon)
                    await connection.InvokeAsync("UnsubscribeAll");
                else if (scope.Kind == StateStreamScopeKind.Workflow)
                    await connection.InvokeAsync("UnsubscribeWorkflow", scope.WorkflowId!.Value);
                else
                    await connection.InvokeAsync("UnsubscribeChat", scope);
            }
            catch
            {
                // Stopping the connection below clears any server-side group membership.
            }
        }

        if (sessions.TryRemove(scope, out var removed))
            removed.Dispose();
        else
            session.Dispose();
        if (scope.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom)
            Store.RemoveChatTarget(scope);

        RecomputeMode();
        if (!sessions.IsEmpty)
            return;

        if (connection.State != HubConnectionState.Disconnected)
        {
            try
            {
                await connection.StopAsync();
            }
            catch
            {
                // Preserve the original subscription/snapshot exception.
            }
        }
    }

    private void Publish(DaemonClientUpdate update)
    {
        Updated?.Invoke(update);
        foreach (var activity in update.Activity)
            ActivityReceived?.Invoke(activity);
    }

    private void RecomputeMode()
    {
        bool hasDaemon = sessions.ContainsKey(StateStreamScopeDto.Daemon);
        bool hasWorkflow = sessions.Keys.Any(item => item.Kind == StateStreamScopeKind.Workflow);
        bool hasChat = sessions.Keys.Any(item => item.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom);
        Mode = hasWorkflow
            ? LiveSubscriptionMode.Workflow
            : (hasDaemon, hasChat) switch
            {
                (true, true) => LiveSubscriptionMode.DaemonAndChat,
                (true, false) => LiveSubscriptionMode.Daemon,
                (false, true) => LiveSubscriptionMode.Chat,
                _ => LiveSubscriptionMode.None,
            };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await lifetime.CancelAsync();
        await connection.DisposeAsync();
        lifecycleGate.Dispose();
        foreach (var session in sessions.Values)
            session.Dispose();
        lifetime.Dispose();
        if (ownsHttp)
            http.Dispose();
    }

    private sealed class ScopeSession : IDisposable
    {
        private const int MaximumBufferedBatches = 2_048;
        private readonly object gate = new();
        private readonly List<StateUpdateBatchDto> buffered = [];
        private readonly CancellationTokenSource lifetime = new();
        private bool buffering = true;
        private bool recoveryScheduled;
        private bool overflowed;

        public SemaphoreSlim RecoveryGate { get; } = new(1, 1);
        public CancellationToken CancellationToken => lifetime.Token;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        private int disposed;

        public void BeginBuffering(StateUpdateBatchDto? first = null)
        {
            lock (gate)
            {
                buffering = true;
                if (first != null)
                    AddBounded(first);
            }
        }

        public bool TryBuffer(StateUpdateBatchDto batch)
        {
            lock (gate)
            {
                if (IsDisposed)
                    return false;
                if (!buffering)
                    return false;
                AddBounded(batch);
                return true;
            }
        }

        public BufferedDrain Drain()
        {
            lock (gate)
            {
                var result = buffered.ToList();
                buffered.Clear();
                bool hadOverflow = overflowed;
                overflowed = false;
                return new BufferedDrain(result, hadOverflow);
            }
        }

        public bool EndBufferingIfEmpty()
        {
            lock (gate)
            {
                if (buffered.Count != 0)
                    return false;
                buffering = false;
                return true;
            }
        }

        public bool TryScheduleRecovery()
        {
            lock (gate)
            {
                if (recoveryScheduled)
                    return false;
                recoveryScheduled = true;
                return true;
            }
        }

        public void CompleteScheduledRecovery()
        {
            lock (gate)
                recoveryScheduled = false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                lifetime.Cancel();
        }

        private void AddBounded(StateUpdateBatchDto batch)
        {
            if (buffered.Count == MaximumBufferedBatches)
            {
                buffered.Clear();
                overflowed = true;
            }
            buffered.Add(batch);
        }
    }

    private sealed record BufferedDrain(
        IReadOnlyList<StateUpdateBatchDto> Batches,
        bool Overflowed);
}
