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
            if (Mode == LiveSubscriptionMode.Daemon)
                return;

            await EnsureConnectedAsync(ct);
            Mode = LiveSubscriptionMode.Daemon;
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
            if (Mode == LiveSubscriptionMode.Daemon)
                throw new InvalidOperationException("Cannot mix daemon and workflow subscriptions on one live client.");

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
            sessions[scope].BeginBuffering();

        if (Mode == LiveSubscriptionMode.Daemon)
            await connection.InvokeAsync("SubscribeAll");
        else
        {
            foreach (var scope in scopes.Where(scope => scope.Kind == StateStreamScopeKind.Workflow))
                await connection.InvokeAsync("SubscribeWorkflow", scope.WorkflowId!.Value);
        }

        await Task.WhenAll(scopes.Select(RecoverScopeSafelyAsync));
    }

    private void ScheduleRecovery(StateStreamScopeDto scope)
    {
        var session = sessions[scope];
        if (!session.TryScheduleRecovery())
            return;
        _ = RecoverScopeSafelyAsync(scope);
    }

    private async Task RecoverScopeSafelyAsync(StateStreamScopeDto scope)
    {
        var session = sessions[scope];
        try
        {
            int attempt = 0;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await RecoverScopeAsync(scope, lifetime.Token);
                    return;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
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
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
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
        var session = sessions[scope];
        await session.RecoveryGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var snapshot = scope.Kind == StateStreamScopeKind.Daemon
                    ? await api.GetDaemonSnapshotAsync(ct)
                    : await api.GetWorkflowSnapshotAsync(scope.WorkflowId!.Value, ct);

                var snapshotUpdate = Store.ApplySnapshot(snapshot);
                SnapshotApplied?.Invoke(snapshot);
                Publish(snapshotUpdate);

                bool restartRecovery = false;
                while (true)
                {
                    var buffered = session.Drain();
                    if (buffered.Count == 0)
                    {
                        if (session.EndBufferingIfEmpty())
                            return;
                        continue;
                    }

                    foreach (var batch in buffered
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
                else
                    await connection.InvokeAsync("UnsubscribeWorkflow", scope.WorkflowId!.Value);
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

        if (!sessions.IsEmpty)
            return;

        Mode = LiveSubscriptionMode.None;
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
        private readonly object gate = new();
        private readonly List<StateUpdateBatchDto> buffered = [];
        private bool buffering = true;
        private bool recoveryScheduled;

        public SemaphoreSlim RecoveryGate { get; } = new(1, 1);

        public void BeginBuffering(StateUpdateBatchDto? first = null)
        {
            lock (gate)
            {
                buffering = true;
                if (first != null)
                    buffered.Add(first);
            }
        }

        public bool TryBuffer(StateUpdateBatchDto batch)
        {
            lock (gate)
            {
                if (!buffering)
                    return false;
                buffered.Add(batch);
                return true;
            }
        }

        public IReadOnlyList<StateUpdateBatchDto> Drain()
        {
            lock (gate)
            {
                var result = buffered.ToList();
                buffered.Clear();
                return result;
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
            => RecoveryGate.Dispose();
    }
}
