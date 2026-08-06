using Microsoft.Extensions.Options;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;
using System.Diagnostics;
using Sockseek.Api;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Persistence.Chat;

namespace Sockseek.Server.Persistence;

public sealed class PersistenceCoordinator(IOptions<ServerOptions> serverOptions)
{
    private readonly ServerOptions options = serverOptions.Value;
    private readonly Dictionary<DownloadEngine, EnginePersistenceAdapter> adapters = [];
    private readonly object gate = new();
    private PersistenceRuntimeHost? host;
    private UploadPersistenceAdapter? uploadAdapter;
    private string? databasePath;

    public bool IsEnabled => options.Persistence.Enabled;
    public bool IsStarted => host?.IsStarted == true;
    public SqliteInitializationResult? Initialization => host?.Initialization;
    public PersistenceRuntimeInfo? Runtime => host?.Runtime;
    public StartupReconciliationResult? Reconciliation => host?.Reconciliation;
    public PersistenceHealth? Health => host?.Health;
    public PersistenceHealthSnapshot? HealthSnapshot => host?.HealthSnapshot;
    public IPersistenceMutationSink? MutationSink => host?.MutationSink;
    public PersistenceQueueSnapshot Queue => host?.Queue ?? new(0, 0, 0, 0, 0);
    public IJobHistoryReader? JobHistory => host?.JobHistory;
    public ISearchHistoryReader? SearchHistory => host?.SearchHistory;
    public ITransferHistoryReader? TransferHistory => host?.TransferHistory;
    public ChatPersistenceStore? Chat => host?.Chat;
    public long? DatabaseSizeBytes => host?.DatabaseSizeBytes;
    public long? WalSizeBytes => host?.WalSizeBytes;
    public DateTimeOffset? LastRetentionAtUtc { get; private set; }
    public RetentionResult? LastRetentionResult { get; private set; }
    public event Action? HistoryHealthChanged;
    public event Func<ChatRetentionResult, CancellationToken, Task>? ChatRetentionCompleted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled || IsStarted)
            return;
        if (options.Persistence.DrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.Persistence.DrainTimeout));

        databasePath = SockseekDataPaths.ResolveDatabasePath(options.Persistence.DataDirectory);
        var retentionOptions = new PersistenceRetentionOptions
        {
            CompletedJobHistoryAge = options.Persistence.CompletedJobHistoryAge,
            UnsuccessfulJobHistoryAge = options.Persistence.UnsuccessfulJobHistoryAge,
            MaximumRetainedJobs = options.Persistence.MaximumRetainedJobs,
            SearchResultAge = options.Persistence.SearchResultAge,
            TransferHistoryAge = options.Persistence.TransferHistoryAge,
            BatchSize = options.Persistence.RetentionBatchSize,
        };
        retentionOptions.Validate();
        if (options.Persistence.PrivateMessageHistoryAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.Persistence.PrivateMessageHistoryAge));
        if (options.Persistence.RoomMessageHistoryAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.Persistence.RoomMessageHistoryAge));
        if (options.Persistence.RetentionEnabled && options.Persistence.RetentionInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.Persistence.RetentionInterval));

        var writerOptions = new PersistenceWriterOptions
        {
            CriticalQueueCapacity = options.Persistence.CriticalQueueCapacity,
            OrdinaryQueueCapacity = options.Persistence.OrdinaryQueueCapacity,
            ProgressEntityCapacity = options.Persistence.ProgressEntityCapacity,
            DegradedProjectionCapacity = options.Persistence.DegradedProjectionCapacity,
            SearchResultCapacityPerSearch = options.Persistence.SearchResultCapacityPerSearch,
            SearchResultGlobalCapacity = options.Persistence.SearchResultGlobalCapacity,
            IncompleteSearchTrackingCapacity = options.Persistence.IncompleteSearchTrackingCapacity,
            SearchResultFlushCount = options.Persistence.SearchResultFlushCount,
            SearchResultFlushInterval = options.Persistence.SearchResultFlushInterval,
            TransferProgressFlushInterval = options.Persistence.TransferProgressFlushInterval,
        };
        writerOptions.Validate();
        string version = typeof(PersistenceCoordinator).Assembly.GetName().Version?.ToString() ?? "dev";
        // SQLite can otherwise remain in its own five-second lock wait after the
        // configured persistence drain deadline has elapsed.
        int busyTimeoutMilliseconds = (int)Math.Clamp(
            options.Persistence.DrainTimeout.TotalMilliseconds,
            1,
            5_000);
        int defaultTimeoutSeconds = (int)Math.Clamp(
            Math.Ceiling(options.Persistence.DrainTimeout.TotalSeconds),
            1,
            5);
        host = new PersistenceRuntimeHost(
            new SockseekSqliteOptions(
                databasePath,
                DefaultTimeoutSeconds: defaultTimeoutSeconds,
                BusyTimeoutMilliseconds: busyTimeoutMilliseconds),
            writerOptions,
            retentionOptions,
            version);
        var startup = await host.StartAsync(cancellationToken).ConfigureAwait(false);
        host.Health.CommitCompleted += OnHistoryHealthChanged;
        host.Health.FailureRecorded += OnHistoryHealthChanged;
        JobDisplayIds.ContinueAfter(startup.MaximumRetainedDisplayId);
    }

    public async Task<PersistenceIntegrityResultDto> CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        EnsureHostAvailable();
        var result = await host!.CheckIntegrityAsync(cancellationToken).ConfigureAwait(false);
        return new PersistenceIntegrityResultDto(result.IsHealthy, result.Result);
    }

    public async Task<PersistenceBackupResultDto> BackupAsync(string? requestedPath, CancellationToken cancellationToken)
    {
        EnsureHostAvailable();
        string backupPath = string.IsNullOrWhiteSpace(requestedPath)
            ? Path.Combine(Path.GetDirectoryName(databasePath!)!, "backups", $"sockseek-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db")
            : Path.GetFullPath(requestedPath);
        var result = await host!.BackupAsync(backupPath, cancellationToken).ConfigureAwait(false);
        return new PersistenceBackupResultDto(
            result.BackupPath, result.SizeBytes, result.Integrity.IsHealthy, result.Integrity.Result);
    }

    public async Task<PersistenceCheckpointResultDto> CheckpointAsync(CancellationToken cancellationToken)
    {
        EnsureHostAvailable();
        var result = await host!.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        return new PersistenceCheckpointResultDto(result.Busy, result.LogFrames, result.CheckpointedFrames);
    }

    public async Task<PersistenceRetentionResultDto> RunRetentionAsync(CancellationToken cancellationToken)
    {
        EnsureHostAvailable();
        var stopwatch = Stopwatch.StartNew();
        var result = await host!.RunRetentionAsync(cancellationToken).ConfigureAwait(false);
        ChatRetentionResult chatResult = new(0, []);
        if (host.Chat is not null)
        {
            chatResult = await host.Chat.ApplyRetentionAsync(
                options.Persistence.PrivateMessageHistoryAge,
                options.Persistence.RoomMessageHistoryAge,
                options.Persistence.RetentionBatchSize,
                cancellationToken).ConfigureAwait(false);
            await NotifyChatRetentionCompletedAsync(
                chatResult, CancellationToken.None).ConfigureAwait(false);
        }
        LastRetentionAtUtc = DateTimeOffset.UtcNow;
        LastRetentionResult = result;
        return new PersistenceRetentionResultDto(
            result.PrunedJobs, result.PrunedSearchResults, result.SearchesMarkedPruned,
            stopwatch.ElapsedMilliseconds, result.PrunedTransfers, result.PrunedTransferAttempts,
            chatResult.PrunedMessages);
    }

    private async Task NotifyChatRetentionCompletedAsync(
        ChatRetentionResult result,
        CancellationToken cancellationToken)
    {
        if (result.PrunedMessages == 0 || ChatRetentionCompleted is not { } handlers)
            return;
        foreach (Func<ChatRetentionResult, CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(result, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                SockseekLog.Daemon.Warn(
                    $"Chat retention projection failed: {SockseekLog.ExceptionSummary(ex)}");
            }
        }
    }

    public void AttachEngine(DownloadEngine engine)
    {
        if (!IsStarted || Runtime == null || host?.MutationSink == null)
            return;

        lock (gate)
        {
            var adapter = new EnginePersistenceAdapter(Runtime.RuntimeId, host.MutationSink);
            adapter.Attach(engine.Events);
            adapters.Add(engine, adapter);
        }
    }

    public void DetachEngine(DownloadEngine engine)
    {
        lock (gate)
        {
            if (!adapters.Remove(engine, out var adapter))
                return;
            adapter.Detach(engine.Events);
        }
    }

    public void AttachUploads(UploadCoordinator uploads)
    {
        if (!IsStarted || Runtime == null || host?.MutationSink == null)
            return;
        lock (gate)
        {
            if (uploadAdapter is not null)
                throw new InvalidOperationException("An upload runtime is already attached.");
            uploadAdapter = new UploadPersistenceAdapter(
                Runtime.RuntimeId,
                host.MutationSink);
            uploadAdapter.Attach(uploads);
        }
    }

    public void DetachUploads(UploadCoordinator uploads)
    {
        lock (gate)
        {
            uploadAdapter?.Detach(uploads);
            uploadAdapter = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsStarted)
            return;

        host!.Health.CommitCompleted -= OnHistoryHealthChanged;
        host.Health.FailureRecorded -= OnHistoryHealthChanged;
        var stop = await host.StopAsync(options.Persistence.DrainTimeout, cancellationToken).ConfigureAwait(false);
        if (!stop.Drained)
        {
            SockseekLog.Daemon.Error(
                $"Persistence drain timed out; leaving runtime {stop.RuntimeId} unfinished for startup reconciliation.");
        }
    }

    private void EnsureHostAvailable()
    {
        if (!IsStarted || host == null)
            throw new InvalidOperationException("Persistence maintenance is unavailable because persistence is disabled or not started.");
    }

    private void OnHistoryHealthChanged()
        => HistoryHealthChanged?.Invoke();

}
