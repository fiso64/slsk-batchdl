using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Soulseek;
using Sockseek.Api;
using Sockseek.Server.Persistence;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Core.Chat;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Events;
using Sockseek.Persistence.PeerBrowsing;
using Sockseek.Server.PeerBrowsing;
using Sockseek.Core.UserProfiles;
using Sockseek.Server.UserProfiles;
using Sockseek.Core.Planning;
using Sockseek.Server.Planning;
using Sockseek.Server.PeerRestrictions;

namespace Sockseek.Server;

public sealed class EngineSupervisor
{
    private readonly ServerOptions options;
    private readonly EngineSettings engineSettings;
    private readonly DownloadSettings defaultDownloadSettings;
    private readonly ProfileCatalog profileCatalog;
    private readonly SubmissionOptionsJobSettingsResolver jobSettingsResolver;
    private readonly Channel<QueuedSubmission> submissionChannel = Channel.CreateUnbounded<QueuedSubmission>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, Job> acceptedRootsAwaitingRegistration = [];
    private readonly Lock engineGate = new();
    private readonly PersistenceCoordinator? persistence;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<EngineSupervisor> logger;
    private readonly bool retireTerminalWorkflows;
    private readonly InputArtifactCoordinator? inputArtifacts;
    private PeerRestrictionPolicy? peerRestrictions;

    private DownloadEngine? currentEngine;
    private int restartCount;

    public event Action<DownloadEngine>? EngineCreated;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public EngineStateStore StateStore { get; }
    public SharingRuntime? Sharing { get; private set; }
    public ChatRuntime? Chat { get; private set; }
    public DaemonSoulseekRuntime? SoulseekRuntime { get; private set; }
    public PeerBrowseService? PeerBrowses { get; private set; }
    public UserProfileService? UserProfiles { get; private set; }
    /// <summary>
    /// Neutral daemon-session seam for future chat and remote-user services.
    /// This is the same manager used by sharing and every download engine.
    /// </summary>
    public SoulseekClientManager? SoulseekSession { get; private set; }
    internal DownloadSettings DefaultDownloadSettings => defaultDownloadSettings;

    public EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence = null,
        ILoggerFactory? loggerFactory = null)
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true)
    {
    }

    public EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        InputArtifactCoordinator inputArtifacts)
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true)
    {
        this.inputArtifacts = inputArtifacts;
    }

    public EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        InputArtifactCoordinator inputArtifacts,
        PeerRestrictionCoordinator peerRestrictions)
        : this(options, persistence, loggerFactory, retireTerminalWorkflows: true)
    {
        this.inputArtifacts = inputArtifacts;
        this.peerRestrictions = peerRestrictions.Policy;
    }

    internal EngineSupervisor(
        IOptions<ServerOptions> options,
        PersistenceCoordinator? persistence,
        ILoggerFactory? loggerFactory,
        bool retireTerminalWorkflows)
    {
        this.options = options.Value;
        this.persistence = persistence;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.retireTerminalWorkflows = retireTerminalWorkflows;
        logger = this.loggerFactory.CreateLogger<EngineSupervisor>();

        engineSettings = SettingsCloner.Clone(this.options.Engine);
        engineSettings.AutoReconnectAfterKickedFromServer = true;
        defaultDownloadSettings = this.options.DefaultDownload == null
            ? SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic)
            : SettingsCloner.Clone(this.options.DefaultDownload);
        DownloadSettingsPatchDtoMapper.ApplyTo(
            defaultDownloadSettings,
            this.options.OperatorDownloadSettings);
        var pathContext = new PathVariableContext(ConfigDir: this.options.ConfigDir);
        SharingSettingsValidator.NormalizeAndValidate(engineSettings, pathContext);
        ChatSettingsValidator.NormalizeAndValidate(engineSettings);
        SettingsNormalizer.NormalizeDownloadPaths(defaultDownloadSettings, pathContext);
        profileCatalog = this.options.Profiles ?? ProfileCatalog.Empty;
        jobSettingsResolver = CreateJobSettingsResolver(this.options, profileCatalog, pathContext);

        StateStore = new EngineStateStore();
        StateStore.JobUpserted += summary =>
        {
            if (summary.SubmissionId is Guid submissionId
                && summary.Role == ServerJobRole.UserRoot)
                acceptedRootsAwaitingRegistration.TryRemove(submissionId, out _);
        };
    }

    private static SubmissionOptionsJobSettingsResolver CreateJobSettingsResolver(
        ServerOptions options,
        ProfileCatalog profiles,
        PathVariableContext pathContext)
    {
        var launchPatch = new DownloadSettingsPatch();
        if (options.LaunchDownloadSettings != null)
        {
            launchPatch.Add(
                settings => DownloadSettingsPatchDtoMapper.ApplyTo(
                    settings,
                    options.LaunchDownloadSettings),
                DownloadSettingsPatchDtoMapper.ExplicitFields(
                    options.LaunchDownloadSettings));
        }
        var operatorPatch = new DownloadSettingsPatch();
        if (options.OperatorDownloadSettings != null)
        {
            operatorPatch.Add(
                settings => DownloadSettingsPatchDtoMapper.ApplyTo(
                    settings,
                    options.OperatorDownloadSettings),
                DownloadSettingsPatchDtoMapper.ExplicitFields(
                    options.OperatorDownloadSettings));
        }
        var profilesResolver = new ProfileJobSettingsResolver(
            options.DefaultDownload,
            profiles,
            profiles.ResolveNamedProfiles(options.LaunchProfileNames),
            new SettingsProfile { Name = "<daemon-launch>", Download = launchPatch },
            normalize: settings => SettingsNormalizer.NormalizeDownloadPaths(
                settings,
                pathContext),
            operatorDefault: operatorPatch);
        return new SubmissionOptionsJobSettingsResolver(profilesResolver);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string dataDirectory = SockseekDataPaths.ResolveDataDirectory(
            options.Persistence.DataDirectory);
        LocalUserProfile localProfile = await LocalUserProfile.LoadAsync(
            engineSettings,
            ct,
            loggerFactory.CreateLogger<LocalUserProfile>()).ConfigureAwait(false);
        await using var soulseek = new DaemonSoulseekRuntime(
            engineSettings,
            options.ClientFactory,
            localProfile,
            loggerFactory.CreateLogger<SoulseekClientManager>(),
            peerRestrictions);
        await using var sharing = new SharingRuntime(
            engineSettings,
            dataDirectory,
            soulseek,
            loggerFactory.CreateLogger<SharingRuntime>(),
            loggerFactory.CreateLogger<UploadCoordinator>());
        await using ChatRuntime? chat = persistence?.Chat is { } chatStore
            ? new ChatRuntime(
                engineSettings,
                soulseek,
                chatStore,
                loggerFactory.CreateLogger<ChatRuntime>())
            : null;
        await using DisabledChatIngress? disabledChat = chat is null
            ? new DisabledChatIngress(
                soulseek,
                loggerFactory.CreateLogger<DisabledChatIngress>())
            : null;
        Sharing = sharing;
        Chat = chat;
        SoulseekRuntime = soulseek;
        SoulseekSession = soulseek.ClientManager;
        PeerBrowseService? peerBrowses = null;
        UserProfileService? userProfiles = null;
        void OnClientStateChanged(SoulseekClientStates state)
        {
            StateStore.UpdateDaemonRuntime(
                ToSoulseekClientStatusDto(state),
                restartCount);
            userProfiles?.OnSoulseekStateChanged(state);
            peerBrowses?.OnSoulseekStateChanged(state);
        }
        void OnSharingStateChanged(
            SharingStateDto sharingState,
            UploadRuntimeStateDto uploadState)
            => StateStore.UpdateSharingRuntime(
                sharingState,
                WithHistoryHealth(uploadState));
        void OnHistoryHealthChanged()
            => StateStore.UpdateSharingRuntime(
                sharing.GetSharingState(),
                WithHistoryHealth(sharing.GetUploadRuntimeState()));
        void OnChatStateChanged(
            ChatRuntimeStateDto chatState,
            NotificationSummaryDto notifications)
            => StateStore.UpdateChatRuntime(chatState, notifications);
        void OnNotificationCommitted(Sockseek.Core.Chat.UserNotificationRecord notification)
            => StateStore.PublishNotification(notification);
        void OnChatTargetChanged(ChatTargetDeltaDto delta)
            => StateStore.PublishChatTarget(delta);
        Task OnChatRetentionCompleted(
            Sockseek.Persistence.Chat.ChatRetentionResult result,
            CancellationToken cancellationToken)
            => chat?.PublishRetentionAsync(result, cancellationToken) ?? Task.CompletedTask;
        void OnUploadChanged(UploadTransferSnapshot transfer)
        {
            StateStore.UpdateUploadTransfer(transfer);
            if (transfer.State is UploadTransferState.Completed
                or UploadTransferState.Cancelled
                or UploadTransferState.Failed
                or UploadTransferState.Interrupted)
            {
                _ = RetireTerminalUploadAsync(transfer);
            }
        }
        void OnPeerBrowseChanged(PeerBrowseResource resource)
            => StateStore.UpdateUserBrowse(UserBrowseDtoMapper.ToDto(resource));
        void OnPeerBrowseRemoved(Guid browseId)
            => StateStore.RemoveUserBrowse(browseId);
        sharing.ClientManager.StateChanged += OnClientStateChanged;
        sharing.StateChanged += OnSharingStateChanged;
        if (chat is not null)
        {
            chat.StateChanged += OnChatStateChanged;
            chat.NotificationCommitted += OnNotificationCommitted;
            chat.TargetChanged += OnChatTargetChanged;
        }
        persistence?.AttachUploads(sharing.Uploads);
        sharing.Uploads.TransferChanged += OnUploadChanged;
        foreach (UploadTransferSnapshot transfer in sharing.Uploads.Snapshot())
            OnUploadChanged(transfer);
        if (persistence is not null)
        {
            persistence.HistoryHealthChanged += OnHistoryHealthChanged;
            persistence.ChatRetentionCompleted += OnChatRetentionCompleted;
        }
        try
        {
            if (chat is not null)
                await chat.StartAsync(ct);
            // Chat attaches protocol callbacks before any sharing-triggered
            // login can release queued private messages from the server.
            await sharing.StartAsync(ct);
            userProfiles = new UserProfileService(
                new SoulseekUserProfileTransport(() => soulseek.ClientManager.Client),
                soulseek.EnsureStartedAsync,
                () => soulseek.ClientManager.LoggedInUsername
                      ?? (!string.IsNullOrWhiteSpace(engineSettings.MockFilesDir) ? "local" : null)
                      ?? (!engineSettings.UseRandomLogin ? engineSettings.Username : null),
                logger: loggerFactory.CreateLogger<UserProfileService>());
            userProfiles.OnSoulseekStateChanged(sharing.ClientManager.State);
            UserProfiles = userProfiles;
            var peerBrowseStore = new PeerBrowseArtifactStore(
                dataDirectory,
                logger: loggerFactory.CreateLogger<PeerBrowseArtifactStore>());
            await peerBrowseStore.InitializeAsync(ct);
            peerBrowses = new PeerBrowseService(
                peerBrowseStore,
                new SoulseekPeerBrowseTransport(
                    soulseek.ClientManager,
                    ensureSessionStarted: soulseek.EnsureStartedAsync),
                () => soulseek.ClientManager.LoggedInUsername
                      ?? (!string.IsNullOrWhiteSpace(engineSettings.MockFilesDir) ? "local" : null)
                      ?? (!engineSettings.UseRandomLogin ? engineSettings.Username : null),
                logger: loggerFactory.CreateLogger<PeerBrowseService>());
            peerBrowses.OnSoulseekStateChanged(sharing.ClientManager.State);
            peerBrowses.Changed += OnPeerBrowseChanged;
            peerBrowses.Removed += OnPeerBrowseRemoved;
            PeerBrowses = peerBrowses;
            while (!ct.IsCancellationRequested)
            {
                var engine = CreateEngine(sharing.ClientManager);
                void OnDownloadCompleted(TransferCompletedChange change)
                    => _ = RetireTerminalDownloadAsync(change.Transfer);
                void OnDownloadFailed(TransferFailedChange change)
                    => _ = RetireTerminalDownloadAsync(change.Transfer);
                void OnDownloadCancelled(TransferCancelledChange change)
                    => _ = RetireTerminalDownloadAsync(change.Transfer);
                engine.Events.TransferCompleted += OnDownloadCompleted;
                engine.Events.TransferFailed += OnDownloadFailed;
                engine.Events.TransferCancelled += OnDownloadCancelled;
                var runTask = engine.RunAsync(ct);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var waitToReadTask = submissionChannel.Reader.WaitToReadAsync(ct).AsTask();
                        var completedTask = await Task.WhenAny(runTask, waitToReadTask);

                        if (completedTask == runTask)
                        {
                            await runTask;
                            return;
                        }

                        if (!await waitToReadTask)
                            continue;

                        while (submissionChannel.Reader.TryRead(out var submission))
                        {
                            if (submission.IsResume)
                                engine.Resume(submission.Job);
                            else
                                engine.Enqueue(
                                    submission.Job,
                                    submission.Settings!,
                                    submission.SourceJobId,
                                    submission.SettingsAreFinal);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref restartCount);
                    ServerLogMessages.EngineRestarting(logger, ex, restartCount);
                    StateStore.MarkActiveJobsInfrastructureFailed(
                        ExceptionText.Summary(ex),
                        ExceptionText.Detail(ex));
                    continue;
                }
                finally
                {
                    engine.Events.TransferCompleted -= OnDownloadCompleted;
                    engine.Events.TransferFailed -= OnDownloadFailed;
                    engine.Events.TransferCancelled -= OnDownloadCancelled;
                    StateStore.DetachEngine(engine);
                    persistence?.DetachEngine(engine);
                    lock (engineGate)
                    {
                        if (ReferenceEquals(currentEngine, engine))
                            currentEngine = null;
                    }
                    await engine.DisposeAsync();
                }
            }
        }
        finally
        {
            UserProfiles = null;
            if (userProfiles is not null)
                await userProfiles.DisposeAsync();
            userProfiles = null;
            PeerBrowses = null;
            if (peerBrowses is not null)
            {
                peerBrowses.Changed -= OnPeerBrowseChanged;
                peerBrowses.Removed -= OnPeerBrowseRemoved;
                await peerBrowses.DisposeAsync();
            }
            sharing.ClientManager.StateChanged -= OnClientStateChanged;
            sharing.StateChanged -= OnSharingStateChanged;
            if (chat is not null)
            {
                chat.StateChanged -= OnChatStateChanged;
                chat.NotificationCommitted -= OnNotificationCommitted;
                chat.TargetChanged -= OnChatTargetChanged;
            }
            sharing.Uploads.TransferChanged -= OnUploadChanged;
            if (persistence is not null)
            {
                persistence.HistoryHealthChanged -= OnHistoryHealthChanged;
                persistence.ChatRetentionCompleted -= OnChatRetentionCompleted;
            }
            persistence?.DetachUploads(sharing.Uploads);
            Sharing = null;
            Chat = null;
            SoulseekRuntime = null;
            SoulseekSession = null;
        }

        UploadRuntimeStateDto WithHistoryHealth(UploadRuntimeStateDto upload)
        {
            if (!upload.AcceptingUploads
                || persistence?.IsEnabled != true
                || persistence.HealthSnapshot?.State
                    is null or Sockseek.Persistence.Write.PersistenceHealthState.Healthy)
            {
                return upload;
            }
            return upload with
            {
                State = DaemonFeatureState.Degraded,
                Reason = "HistoryPersistenceDegraded",
            };
        }

        async Task RetireTerminalUploadAsync(UploadTransferSnapshot transfer)
        {
            try
            {
                // The terminal delta is published synchronously before this
                // continuation. When history is enabled, retain presentation
                // state until the same terminal revision is durably committed.
                if (persistence?.IsStarted == true)
                {
                    await persistence.WaitForTransferHandoffAsync(
                        transfer.TransferId,
                        transfer.Revision,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ServerLogMessages.TerminalUploadHandoffFailed(
                    logger,
                    ex,
                    transfer.TransferId);
            }
            finally
            {
                StateStore.RemoveTerminalTransfer(transfer.TransferId);
                sharing.Uploads.Forget(transfer.TransferId);
            }
        }

        async Task RetireTerminalDownloadAsync(TransferSnapshot transfer)
        {
            try
            {
                if (persistence?.IsStarted == true)
                {
                    await persistence.WaitForTransferHandoffAsync(
                        transfer.Id,
                        transfer.Revision,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ServerLogMessages.TerminalDownloadHandoffFailed(
                    logger,
                    ex,
                    transfer.Id);
            }
            finally
            {
                StateStore.RemoveTerminalTransfer(transfer.Id);
            }
        }
    }

    public ServerInfoDto GetInfo()
    {
        string version = typeof(EngineSupervisor).Assembly.GetName().Version?.ToString() ?? "dev";
        return new ServerInfoDto(options.Name, version, StartedAtUtc, LiveProtocol.Version);
    }

    public bool TryCancelDownloadTransfer(Guid transferId)
    {
        lock (engineGate)
            return currentEngine?.TryCancelTransfer(transferId) == true;
    }

    public TransferCommandReceiptDto CancelTransfers(
        BulkCancelTransfersRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        TransferStateDto[] targets = StateStore.GetCancellableTransferSnapshot()
            .Where(transfer =>
                !transfer.Status.IsTerminal
                && string.Equals(
                    transfer.Identity.Direction,
                    request.Direction.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                && request.Scope switch
                {
                    TransferCancellationScope.Queued => string.Equals(
                        transfer.Status.State, "Queued", StringComparison.OrdinalIgnoreCase),
                    TransferCancellationScope.InProgress => !string.Equals(
                        transfer.Status.State, "Queued", StringComparison.OrdinalIgnoreCase),
                    _ => true,
                })
            .ToArray();

        return CancelTransferSnapshot(
            request.Direction,
            targets,
            StateStore.GetLiveTransfer,
            TryCancelDownloadTransfer,
            transferId => Sharing?.Uploads.Cancel(transferId) == true,
            (transferId, exception) => ServerLogMessages.BulkTransferCancellationFailed(
                logger,
                exception,
                transferId),
            uploadRuntimeAvailable: Sharing is not null);
    }

    internal static TransferCommandReceiptDto CancelTransferSnapshot(
        TransferCommandDirection direction,
        IReadOnlyList<TransferStateDto> targets,
        Func<Guid, TransferStateDto?> getCurrent,
        Func<Guid, bool> cancelDownload,
        Func<Guid, bool> cancelUpload,
        Action<Guid, Exception> logFailure,
        bool uploadRuntimeAvailable = true)
    {
        int succeeded = 0;
        int noOp = 0;
        int rejected = 0;
        int failed = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        void Reason(string reason)
            => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

        foreach (TransferStateDto target in targets)
        {
            try
            {
                TransferStateDto? current = getCurrent(target.TransferId);
                if (current is null || current.Status.IsTerminal)
                {
                    noOp++;
                    Reason("already-terminal");
                    continue;
                }

                bool cancelled = direction switch
                {
                    TransferCommandDirection.Download => cancelDownload(target.TransferId),
                    TransferCommandDirection.Upload => cancelUpload(target.TransferId),
                    _ => false,
                };
                if (cancelled)
                {
                    succeeded++;
                    continue;
                }

                current = getCurrent(target.TransferId);
                if (current is null || current.Status.IsTerminal)
                {
                    noOp++;
                    Reason("already-terminal");
                }
                else
                {
                    rejected++;
                    Reason(direction == TransferCommandDirection.Upload
                           && !uploadRuntimeAvailable
                        ? "runtime-unavailable"
                        : "not-cancellable");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Reason("internal-failure");
                logFailure(target.TransferId, ex);
            }
        }

        return new TransferCommandReceiptDto(
            targets.Count,
            succeeded,
            noOp,
            rejected,
            failed,
            reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TransferCommandReasonCountDto(pair.Key, pair.Value))
                .ToArray());
    }

    public ServerStatusDto GetStatus()
    {
        SoulseekClientStates clientState;
        lock (engineGate)
            clientState = SoulseekSession?.State
                          ?? currentEngine?.ClientState
                          ?? SoulseekClientStates.None;

        var stats = StateStore.GetStatistics();
        var persistenceStatus = GetPersistenceStatus();
        return new ServerStatusDto(
            ToSoulseekClientStatusDto(clientState),
            stats.TotalJobCount,
            stats.ActiveJobCount,
            stats.TotalWorkflowCount,
            stats.ActiveWorkflowCount,
            restartCount,
            persistenceStatus);
    }

    private PersistenceStatusDto GetPersistenceStatus()
    {
        if (persistence?.IsEnabled != true)
            return new PersistenceStatusDto(
                false, false, "Disabled", null, null, null, null, null, null,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0);

        var runtime = persistence.Runtime;
        var snapshot = persistence.HealthSnapshot;
        var reconciliation = persistence.Reconciliation;
        var lastRetention = persistence.LastRetentionResult;
        if (snapshot == null)
            return new PersistenceStatusDto(
                true, false, "Starting", persistence.Initialization?.SchemaVersion,
                runtime?.RuntimeId, runtime?.StartedAtUtc, null, null, null,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0);

        return new PersistenceStatusDto(
            true,
            persistence.Initialization != null,
            snapshot.State.ToString(),
            persistence.Initialization?.SchemaVersion,
            runtime?.RuntimeId,
            runtime?.StartedAtUtc,
            snapshot.LastSuccessfulCommitAtUtc,
            snapshot.LastFailureAtUtc,
            snapshot.LastFailure,
            snapshot.CriticalQueueDepth,
            snapshot.CriticalQueueCapacity,
            snapshot.OrdinaryQueueDepth,
            snapshot.OrdinaryQueueCapacity,
            snapshot.ProgressEntityCount,
            snapshot.ProgressEntityCapacity,
            snapshot.BufferedSearchResultCount,
            snapshot.BufferedSearchResultCapacity,
            snapshot.DegradedProjectionCount,
            snapshot.DegradedProjectionCapacity,
            snapshot.BusyRetryCount,
            snapshot.DroppedOrdinaryCount,
            snapshot.DroppedProgressCount,
            snapshot.DroppedSearchResultCount,
            snapshot.IncompleteSearchCount,
            snapshot.EvictedTerminalProjectionCount,
            snapshot.SuccessfulCommitCount,
            snapshot.RowsWritten,
            persistence.DatabaseSizeBytes,
            persistence.WalSizeBytes,
            snapshot.LastCommitDurationMilliseconds,
            snapshot.LastBatchMutationCount,
            snapshot.PermanentlyFailedMutationCount,
            snapshot.IncompleteSearchTrackingCount,
            snapshot.IncompleteSearchTrackingCapacity,
            snapshot.IncompleteSearchTrackingOverflowed,
            snapshot.CommitLatencyHistogram,
            snapshot.BatchSizeHistogram,
            reconciliation?.UnfinishedRuntimeCount ?? 0,
            reconciliation?.InterruptedJobCount ?? 0,
            reconciliation?.InterruptedTransferCount ?? 0,
            reconciliation?.InterruptedAttemptCount ?? 0,
            reconciliation?.InterruptedSearchCount ?? 0,
            persistence.LastRetentionAtUtc,
            lastRetention?.PrunedJobs ?? 0,
            lastRetention?.PrunedSearchResults ?? 0,
            lastRetention?.PrunedTransfers ?? 0,
            lastRetention?.PrunedTransferAttempts ?? 0);
    }

    public IReadOnlyList<ProfileSummaryDto> GetProfiles()
        => profileCatalog.NamedProfiles
            .Select(profile => new ProfileSummaryDto(
                profile.Name,
                profile.Condition,
                profile.Condition != null,
                profile.HasEngineSettings,
                profile.HasDownloadSettings))
            .OrderBy(profile => profile.Name)
            .ToList();

    private static SoulseekClientStatusDto ToSoulseekClientStatusDto(SoulseekClientStates state)
    {
        var flags = Enum.GetValues<SoulseekClientStates>()
            .Where(flag => flag != SoulseekClientStates.None && state.HasFlag(flag))
            .Select(flag => flag.ToString())
            .ToList();

        bool isConnected = state.HasFlag(SoulseekClientStates.Connected);
        bool isLoggedIn = state.HasFlag(SoulseekClientStates.LoggedIn);

        return new SoulseekClientStatusDto(
            state.ToString(),
            flags,
            isConnected && isLoggedIn);
    }

    public async Task<JobSummaryDto> SubmitExtractJobAsync(
        SubmitExtractJobRequestDto request,
        CancellationToken ct)
    {
        if (request.ArtifactId == null)
            return await SubmitJobAsync(
                JobRequestMapper.CreateExtractJob(request),
                request.Options,
                ct).ConfigureAwait(false);
        if (inputArtifacts == null)
            throw new InputArtifactUnavailableException(
                "Input artifacts are unavailable; ordinary inputs remain available.");

        Sockseek.Persistence.Planning.InputArtifactLease lease =
            await inputArtifacts.ResolveAsync(request.ArtifactId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException(
                $"Input artifact '{request.ArtifactId}' was not found or has expired.");
        string inputType = request.InputType
            ?? (string.Equals(
                    Path.GetExtension(lease.Artifact.OriginalName),
                    ".csv",
                    StringComparison.OrdinalIgnoreCase)
                ? InputType.CSV.ToString()
                : InputType.List.ToString());
        var job = JobRequestMapper.CreateExtractJob(request with
        {
            Input = lease.Path,
            InputType = inputType,
        });
        job.PlannedSourceRevision = InputArtifactCoordinator.Revision(lease);
        var immutablePatch = new DownloadSettingsPatchDto(
            Extraction: new ExtractionSettingsPatchDto(RemoveTracksFromSource: false));
        SubmissionOptionsDto options = (request.Options ?? new SubmissionOptionsDto()) with
        {
            DownloadSettings = DownloadSettingsPatchDtoMapper.Combine(
                request.Options?.DownloadSettings,
                immutablePatch),
        };
        Guid submissionId = Guid.NewGuid();
        if (!await inputArtifacts.PinAsync(
                request.ArtifactId,
                "submission",
                submissionId,
                ct).ConfigureAwait(false))
        {
            throw new ArgumentException(
                $"Input artifact '{request.ArtifactId}' expired before submission.");
        }
        try
        {
            JobSummaryDto summary = await SubmitJobAsync(
                job,
                options,
                ct,
                submissionId: submissionId,
                sourceRevision: job.PlannedSourceRevision,
                artifactId: request.ArtifactId).ConfigureAwait(false);
            // The accepted submission contains the immutable planned result and
            // source revision, so execution and rerun no longer read the upload.
            // Release the temporary admission pin once that record is durable.
            await ReleaseSubmissionArtifactPinAsync(
                request.ArtifactId,
                submissionId).ConfigureAwait(false);
            return summary;
        }
        catch
        {
            await ReleaseSubmissionArtifactPinAsync(
                request.ArtifactId,
                submissionId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReleaseSubmissionArtifactPinAsync(
        string artifactId,
        Guid submissionId)
    {
        try
        {
            await inputArtifacts!.UnpinAsync(
                artifactId,
                "submission",
                submissionId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ServerLogMessages.SubmissionArtifactPinReleaseFailed(
                logger,
                exception,
                submissionId);
        }
    }

    public Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateTrackSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSongJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct)
    {
        var job = JobRequestMapper.CreateJobList(request);
        JobRequestMapper.ApplyDraftDownloadSettings(
            job,
            request.Jobs,
            (item, patch) => jobSettingsResolver.SetJobOptions(
                item.Id,
                new SubmissionOptionsDto(DownloadSettings: patch)));
        return SubmitJobAsync(job, request.Options, ct, request.Jobs);
    }

    internal string ResolveUserShareOutputParent(
        string username,
        PeerBrowseDownloadResolution resolution,
        SubmissionOptionsDto? options)
    {
        EnsureShareSubmissionConnection();
        JobList workflow = CreateUserShareWorkflow(username, resolution);
        JobRequestMapper.AssignWorkflowId(workflow, workflow.WorkflowId);
        jobSettingsResolver.SetWorkflowOptions(workflow.WorkflowId, options);
        try
        {
            DownloadSettings settings = jobSettingsResolver.Resolve(defaultDownloadSettings, workflow);
            ValidateExplicitRemoteTransferOverrides(
                workflow,
                options?.DownloadSettings,
                settings);
            RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
                workflow,
                settings,
                ResolveChildSettings);
            return settings.Output.ParentDir
                ?? throw new InvalidOperationException("The output parent was not resolved.");
        }
        finally
        {
            jobSettingsResolver.RemoveWorkflowOptions(workflow.WorkflowId);
        }
    }

    public Task<JobSummaryDto> SubmitUserShareDownloadsAsync(
        string username,
        PeerBrowseDownloadResolution resolution,
        SubmissionOptionsDto? options,
        CancellationToken cancellationToken)
    {
        EnsureShareSubmissionConnection();
        JobList workflow = CreateUserShareWorkflow(username, resolution);
        return SubmitJobAsync(workflow, options, cancellationToken);
    }

    private static JobList CreateUserShareWorkflow(
        string username,
        PeerBrowseDownloadResolution resolution)
    {
        username = PeerUsername.Validate(username);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Plans.Count == 0)
            throw new PeerBrowseSelectionException("The selection contains no downloadable public files.");
        return new JobList(
            $"Shares from {username}",
            resolution.Plans.Select(plan =>
                (Job)new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan))));
    }

    private void EnsureShareSubmissionConnection()
    {
        if (SoulseekSession?.IsConnectedAndLoggedIn != true)
            throw new InvalidOperationException("Soulseek is not connected.");
    }

    public ResolveEffectiveSettingsResponseDto ResolveEffectiveSettings(
        ResolveEffectiveSettingsRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Job);
        Job job = JobRequestMapper.CreateJob(request.Job);
        DownloadSettingsPatchDto? combinedPatch = DownloadSettingsPatchDtoMapper.Combine(
            request.Options?.DownloadSettings,
            JobRequestMapper.DraftDownloadSettings(request.Job));
        SubmissionOptionsDto requestOptions = (request.Options ?? new SubmissionOptionsDto()) with
        {
            DownloadSettings = combinedPatch,
        };
        JobSettingsCompositionResult result = jobSettingsResolver.ResolveDetailed(job, requestOptions);
        bool containsRemoteTransfer = ValidateDraftSubmissionSettings(
            job,
            request.Job,
            result.Settings,
            requestOptions,
            combinedPatch);
        if (containsRemoteTransfer)
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
        return EffectiveSettingsMapper.ToDto(result, requestOptions.ProfileNames);
    }

    private bool ValidateDraftSubmissionSettings(
        Job job,
        JobDraftDto draft,
        DownloadSettings effectiveSettings,
        SubmissionOptionsDto submissionOptions,
        DownloadSettingsPatchDto? explicitPatch)
    {
        bool containsRemoteTransfer = RemoteTransferSubmissionPolicy
            .IsOrdinaryRemoteTransfer(job, effectiveSettings);
        if (containsRemoteTransfer)
            RemoteTransferNameFormatPolicy.ApplyInherited(effectiveSettings.Output);

        if (job is JobList list && draft is JobListJobDraftDto listDraft)
        {
            for (int index = 0; index < list.Jobs.Count && index < listDraft.Jobs.Count; index++)
            {
                Job child = list.Jobs[index];
                JobDraftDto childDraft = listDraft.Jobs[index];
                DownloadSettingsPatchDto? childPatch = JobRequestMapper.DraftDownloadSettings(childDraft);
                SubmissionOptionsDto? childOptions = SubmissionOptionsStore.Merge(
                    submissionOptions,
                    childPatch == null
                        ? null
                        : new SubmissionOptionsDto(DownloadSettings: childPatch));
                JobSettingsCompositionResult childResult = jobSettingsResolver.ResolveDetailed(
                    effectiveSettings,
                    child,
                    childOptions,
                    JobSettingsInheritance.SearchConstraints);
                containsRemoteTransfer |= ValidateDraftSubmissionSettings(
                    child,
                    childDraft,
                    childResult.Settings,
                    submissionOptions,
                    childPatch);
            }
        }

        if (containsRemoteTransfer && explicitPatch != null)
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        return containsRemoteTransfer;
    }

    private async Task<JobSummaryDto> SubmitJobAsync(
        Job job,
        SubmissionOptionsDto? options,
        CancellationToken ct,
        IReadOnlyList<JobDraftDto>? childDrafts = null,
        Guid? submissionId = null,
        SubmissionSourceRevision? sourceRevision = null,
        string? artifactId = null)
    {
        ct.ThrowIfCancellationRequested();

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        jobSettingsResolver.SetWorkflowOptions(job.WorkflowId, options);

        var settings = jobSettingsResolver.Resolve(defaultDownloadSettings, job);

        ValidateExplicitRemoteTransferOverrides(job, options?.DownloadSettings, settings);
        if (job is JobList jobList && childDrafts != null)
            RemoteTransferSubmissionPolicy.ValidateChildOverrides(
                jobList,
                childDrafts,
                settings,
                ResolveChildSettings);
        RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
            job,
            settings,
            ResolveChildSettings);

        if (ContainsLoginRequiredJob(job, defaultDownloadSettings, settings) && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");

        await PlanForExecutionAsync(job, ct).ConfigureAwait(false);
        settings = job.PlannedEffectiveSettings ?? settings;
        if (submissionId != null)
        {
            SubmissionIdentity.AssignAccepted(
                job,
                settings,
                sourceRevision,
                submissionId,
                artifactId: artifactId);
        }
        await QueueAcceptedSubmissionAsync(
            job,
            settings,
            sourceJobId: null,
            ct,
            settingsAreFinal: true).ConfigureAwait(false);

        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    private async Task PlanForExecutionAsync(Job job, CancellationToken cancellationToken)
    {
        var planner = new JobPlanner(jobSettingsResolver);
        await foreach (PlannedJobNode _ in planner.PlanAsync(
            job,
            defaultDownloadSettings,
            cancellationToken).ConfigureAwait(false))
        {
            // Planning mutates only the supplied runtime tree with captured
            // extraction results and per-node effective settings. Consumers
            // that need records (Review/local print) enumerate the same stream.
        }
    }

    internal async IAsyncEnumerable<PlannedJobNode> PlanDraftAsync(
        JobDraftDto draft,
        SubmissionOptionsDto? submissionOptions,
        IReadOnlyDictionary<string, SubmissionSourceRevision>? artifactRevisions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Job root = JobRequestMapper.CreateJob(draft);
        if (submissionOptions?.WorkflowId is Guid workflowId)
            root.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);
        AttachArtifactRevisions(root, artifactRevisions);
        jobSettingsResolver.SetWorkflowOptions(root.WorkflowId, submissionOptions);
        JobRequestMapper.ApplyDraftDownloadSettings(
            root,
            draft,
            (item, patch) => jobSettingsResolver.SetJobOptions(
                item.Id,
                new SubmissionOptionsDto(DownloadSettings: patch)));
        long optionsVersion = jobSettingsResolver.CaptureWorkflowVersion(root.WorkflowId);
        var plannedJobIds = new HashSet<Guid>();
        try
        {
            DownloadSettings rootSettings = jobSettingsResolver.Resolve(
                defaultDownloadSettings,
                root);
            ValidateExplicitRemoteTransferOverrides(
                root,
                SubmissionOptionsStore.Merge(
                    submissionOptions,
                    JobRequestMapper.DraftDownloadSettings(draft) is { } rootPatch
                        ? new SubmissionOptionsDto(DownloadSettings: rootPatch)
                        : null)?.DownloadSettings,
                rootSettings);
            RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
                root,
                rootSettings,
                ResolveChildSettings);
            if (ContainsLoginRequiredJob(root, defaultDownloadSettings, rootSettings)
                && !CanAcceptLoginRequiredJobs())
            {
                throw new ArgumentException(
                    "This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");
            }

            var planner = new JobPlanner(jobSettingsResolver);
            await foreach (PlannedJobNode node in planner.PlanAsync(
                root,
                defaultDownloadSettings,
                cancellationToken).ConfigureAwait(false))
            {
                plannedJobIds.Add(node.RuntimeJob.Id);
                yield return node;
            }
        }
        finally
        {
            jobSettingsResolver.RetireWorkflow(
                root.WorkflowId,
                plannedJobIds,
                optionsVersion);
        }
    }

    private static void AttachArtifactRevisions(
        Job job,
        IReadOnlyDictionary<string, SubmissionSourceRevision>? revisions)
    {
        if (job.ArtifactId is { } artifactId
            && revisions?.TryGetValue(artifactId, out SubmissionSourceRevision? revision) == true)
        {
            job.PlannedSourceRevision = revision;
        }
        if (job is JobList list)
        {
            foreach (Job child in list.Jobs)
                AttachArtifactRevisions(child, revisions);
        }
    }

    internal async Task<JobSummaryDto> QueuePlannedSubmissionAsync(
        Job root,
        DownloadSettings rootSettings,
        SubmissionSpecification specification,
        Guid submissionId,
        Guid previewId,
        string commitFingerprint,
        CancellationToken cancellationToken)
    {
        SubmissionIdentity.AssignAccepted(
            root,
            specification,
            submissionId: submissionId,
            previewId: previewId);
        await QueueAcceptedSubmissionAsync(
            root,
            rootSettings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true,
            commitFingerprint: commitFingerprint).ConfigureAwait(false);
        return StateStore.GetJobSummary(root.Id) ?? BuildSubmittedJobSummary(root);
    }

    internal DownloadSettings PrepareSearchViewSelectionJob(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        DownloadSettings settings = jobSettingsResolver.ResolveFollowUp(job, options: null);
        RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
            job,
            settings,
            ResolveChildSettings);
        job.PlannedEffectiveSettings = SettingsCloner.Clone(settings);
        return settings;
    }

    internal async Task<JobSummaryDto> QueueSearchViewSelectionAsync(
        JobList root,
        Guid sourceJobId,
        Guid submissionId,
        string commitFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Count == 0)
            throw new ArgumentException("A search-view submission requires at least one resolved job.");
        DownloadSettings rootSettings = SearchSettingsBaselines.Create(
            SearchSettingsBaselineKind.Generic);
        root.PlannedEffectiveSettings = SettingsCloner.Clone(rootSettings);
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);
        SubmissionSpecification specification = SubmissionSpecification.Create(
            root,
            rootSettings);
        SubmissionIdentity.AssignAccepted(
            root,
            specification,
            submissionId: submissionId);
        await QueueAcceptedSubmissionAsync(
            root,
            rootSettings,
            sourceJobId,
            cancellationToken,
            settingsAreFinal: true,
            commitFingerprint: commitFingerprint).ConfigureAwait(false);
        return StateStore.GetJobSummary(root.Id)
            ?? BuildSubmittedJobSummary(root, sourceJobId);
    }

    private void ValidateExplicitRemoteTransferOverrides(
        Job job,
        DownloadSettingsPatchDto? patch,
        DownloadSettings effectiveSettings)
    {
        if (!RemoteTransferSubmissionPolicy.ContainsOrdinaryRemoteTransfer(
                job,
                effectiveSettings,
                ResolveChildSettings))
            return;

        RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
        if (patch != null)
            RemoteTransferSettingsValidator.ValidateExplicitPatch(patch);
    }

    private DownloadSettings ResolveChildSettings(
        DownloadSettings parentSettings,
        Job child,
        DownloadSettingsPatchDto? _)
        => jobSettingsResolver.Resolve(
            parentSettings,
            child,
            JobSettingsInheritance.SearchConstraints);

    private bool ContainsLoginRequiredJob(Job job, DownloadSettings inheritedSettings, DownloadSettings? resolvedSettings = null)
    {
        var effectiveSettings = resolvedSettings ?? jobSettingsResolver.Resolve(inheritedSettings, job);

        return job switch
        {
            JobList list => list.Jobs.Any(child => ContainsLoginRequiredJob(
                child,
                effectiveSettings,
                jobSettingsResolver.Resolve(
                    effectiveSettings,
                    child,
                    JobSettingsInheritance.SearchConstraints))),
            _ => effectiveSettings.NeedLogin,
        };
    }

    public bool CancelJob(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelJob(jobId) ?? false;
    }

    public bool CancelJobByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelJobByDisplayId(displayId, workflowId) ?? false;
    }

    public int CancelWorkflow(Guid workflowId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelWorkflow(workflowId) ?? 0;
    }

    public int CancelAllJobs()
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelAllJobs() ?? 0;
    }

    public async Task<SubmissionArchiveResponseDto> SetSubmissionArchivedAsync(
        Guid submissionId,
        bool archived,
        CancellationToken cancellationToken)
    {
        if (persistence?.Submissions == null)
            throw new NotSupportedException("Submission archive is unavailable because persistence is disabled or not started.");
        if (archived)
        {
            bool pending = acceptedRootsAwaitingRegistration.TryGetValue(
                submissionId,
                out Job? acceptedRoot)
                && acceptedRoot.LifecycleState != JobLifecycleState.Terminal;
            bool liveActive = StateStore.GetJobs(new JobQuery(
                    null,
                    null,
                    null,
                    null,
                    IncludeAll: true,
                    SubmissionId: submissionId))
                .Any(job => job.LifecycleState != ServerJobLifecycleState.Terminal);
            if (pending || liveActive)
            {
                return new SubmissionArchiveResponseDto(
                    submissionId,
                    true,
                    0,
                    0,
                    1,
                    [new SubmissionReasonCountDto("nonterminal-jobs", 1)]);
            }
        }
        await persistence.WaitForAllHandoffsAsync(cancellationToken).ConfigureAwait(false);
        var result = await persistence.Submissions
            .SetArchivedAsync(submissionId, archived, cancellationToken)
            .ConfigureAwait(false);
        if (result.RejectedSubmissionCount == 0)
            StateStore.SetSubmissionArchived(submissionId, archived);
        return SubmissionDtoMapper.ToArchiveResponse(result);
    }

    public async Task<JobSummaryDto?> RerunSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        if (persistence?.Submissions == null)
            throw new NotSupportedException("Submission rerun is unavailable because persistence is disabled or not started.");
        await persistence.WaitForAllHandoffsAsync(cancellationToken).ConfigureAwait(false);
        var retained = await persistence.Submissions
            .GetSubmissionAsync(submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (retained == null)
            return null;

        SubmissionSpecification specification =
            SubmissionSpecificationCodec.Deserialize(retained.SpecificationJson);
        Job job = specification.MaterializeJob(defaultDownloadSettings);
        DownloadSettings settings = specification.MaterializeSettings(defaultDownloadSettings);
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
            job,
            settings,
            ResolveChildSettings);
        if (settings.NeedLogin && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This retained submission requires Soulseek login, but the server is not configured for it.");

        SubmissionIdentity.AssignAccepted(
            job,
            specification,
            rerunOfSubmissionId: submissionId);
        await QueueAcceptedSubmissionAsync(
            job,
            settings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true).ConfigureAwait(false);
        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    public bool TryNextCandidate(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.TryNextCandidate(jobId) ?? false;
    }

    public bool TryNextCandidateByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.TryNextCandidateByDisplayId(displayId, workflowId) ?? false;
    }

    public JobDetailDto? GetJobDetailByDisplayId(Guid workflowId, int displayId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        var job = engine?.GetJob(displayId);
        if (job == null || job.WorkflowId != workflowId)
            return null;

        return StateStore.GetJobDetail(job.Id);
    }

    public IReadOnlyList<SearchRawResultDto>? GetSearchRawResults(Guid jobId, long afterSequence)
    {
        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob == null)
            return null;

        return searchJob.RawSnapshot(afterSequence)
            .Select(ToSearchRawResultDto)
            .ToList();
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct)
    {
        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalRetrieveFolderAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var folder = JobRequestMapper.FindAlbumFolderForRetrieval(
            sourceJob,
            request.Folder,
            GetCurrentEngineUserSuccessCounts(),
            request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = new RetrieveFolderJob(folder.DirectoryIdentity)
        {
            ItemName = folder.FolderPath,
            ResultObserver = snapshot => Searcher.ApplyDirectorySnapshot(folder, snapshot),
        };
        retrieveJob.WorkflowId = sourceJob.WorkflowId;
        await QueueAcceptedSubmissionAsync(retrieveJob, sourceJob.Config, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    internal async Task<JobSummaryDto?> StartSearchViewDirectoryRetrievalAsync(
        Guid sourceJobId,
        PeerDirectoryIdentity directory,
        Func<PeerDirectorySnapshot, CancellationToken, Task<int>> resultObserver,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(resultObserver);
        Job? sourceJob = GetRuntimeJob<Job>(sourceJobId);
        var retrieveJob = new RetrieveFolderJob(directory)
        {
            ItemName = directory.FolderPath,
            AsyncResultObserver = resultObserver,
        };
        DownloadSettings settings;
        if (sourceJob?.Config != null)
        {
            retrieveJob.WorkflowId = sourceJob.WorkflowId;
            settings = sourceJob.Config;
        }
        else
        {
            if (persistence?.JobHistory == null)
                return null;
            await persistence.WaitForJobHandoffAsync(sourceJobId, ct).ConfigureAwait(false);
            PersistedJob? retained = await persistence.JobHistory.GetJobAsync(
                sourceJobId,
                ct).ConfigureAwait(false);
            if (retained == null)
                return null;
            retrieveJob.WorkflowId = retained.WorkflowId;
            settings = jobSettingsResolver.ResolveFollowUp(retrieveJob, options: null);
        }
        await QueueAcceptedSubmissionAsync(
            retrieveJob,
            settings,
            sourceJobId,
            ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(retrieveJob.Id)
            ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    private async Task<JobSummaryDto?> StartHistoricalRetrieveFolderAsync(
        Guid sourceJobId,
        RetrieveFolderRequestDto request,
        CancellationToken ct)
    {
        var historical = await ResolveHistoricalFolderAsync(sourceJobId, request.Folder, request.AlbumQuery, ct).ConfigureAwait(false);
        if (historical == null)
            return null;
        var retrieveJob = new RetrieveFolderJob(historical.Value.Folder.DirectoryIdentity)
        {
            ItemName = historical.Value.Folder.FolderPath,
            WorkflowId = historical.Value.Job.WorkflowId,
        };
        var settings = jobSettingsResolver.ResolveFollowUp(retrieveJob, options: null);
        await QueueAcceptedSubmissionAsync(retrieveJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct)
    {
        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");
        if (request.RequestedMode == ExtractionMode.Album)
            throw new ArgumentException("A file selection cannot be interpreted as an album.");

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalFileDownloadsAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var summaries = new List<JobSummaryDto>();

        if ((request.RequestedMode is null or ExtractionMode.Song)
            && sourceJob is SongJob manualSong && manualSong.IsAwaitingSelection)
        {
            if (request.Files.Count != 1)
                throw new ArgumentException("Manual song jobs require exactly one selected file.");

            var candidate = JobRequestMapper.FindFileCandidate(
                sourceJob,
                request.Files[0],
                GetCurrentEngineUserSuccessCounts());
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            JobRequestMapper.ApplyManualSongSelection(manualSong, candidate);
            await submissionChannel.Writer.WriteAsync(QueuedSubmission.Resume(manualSong), ct);
            return new List<JobSummaryDto> { StateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong, sourceJobId) };
        }

        foreach (var file in request.Files)
        {
            var candidate = JobRequestMapper.FindFileCandidate(
                sourceJob,
                file,
                GetCurrentEngineUserSuccessCounts());
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            Job followUpJob = JobRequestMapper.CreateFileSelectionFollowUp(
                sourceJob,
                candidate,
                request.RequestedMode);

            var followUpSettings = jobSettingsResolver.ResolveFollowUp(followUpJob, request.Options);
            summaries.Add(await SubmitFollowUpJobAsync(sourceJobId, sourceJob, followUpJob, followUpSettings, request.Options, isolateOptions: true, ct));
        }

        return summaries;
    }

    private async Task<IReadOnlyList<JobSummaryDto>?> StartHistoricalFileDownloadsAsync(
        Guid sourceJobId,
        StartFileDownloadsRequestDto request,
        CancellationToken ct)
    {
        if (persistence?.JobHistory == null || persistence.SearchHistory == null)
            return null;

        await persistence.WaitForJobHandoffAsync(sourceJobId, ct).ConfigureAwait(false);

        var persistedJob = await persistence.JobHistory.GetJobAsync(sourceJobId, ct).ConfigureAwait(false);
        if (persistedJob == null)
            return null;

        var summaries = new List<JobSummaryDto>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var lookup = await persistence.SearchHistory
                .GetResultAsync(sourceJobId, file.Username, file.Filename, ct)
                .ConfigureAwait(false);
            if (lookup == null)
                throw new ArgumentException("The historical job has no retained search-result history.");
            if (lookup.Result == null)
            {
                string detail = lookup.Metadata.ResultPersistenceState switch
                {
                    "Pruned" => "Its raw results were pruned by retention.",
                    "Incomplete" => "Its persisted raw results are incomplete.",
                    "Interrupted" => "The search was interrupted and the requested result was not committed.",
                    "NotPersisted" => "Its raw results were not persisted.",
                    _ => "The requested result was not found.",
                };
                throw new ArgumentException($"Cannot start a download from this historical search. {detail}");
            }

            var result = lookup.Result;
            var candidate = new FileCandidate(
                new PeerFileTarget(
                    new PeerFileIdentity(result.Username, result.RemoteFilename),
                    result.SizeBytes < 0 ? null : result.SizeBytes,
                    result.Extension,
                    result.BitRate,
                    result.BitDepth,
                    result.SampleRate,
                    result.DurationSeconds,
                    DeserializeFileAttributes(result.AttributesJson)),
                new SearchPeerSnapshot(
                    result.Username,
                    result.ResponseFileCount,
                    result.UploadSpeed,
                    result.HasFreeUploadSlot));
            Job followUp = request.RequestedMode == ExtractionMode.General
                ? new RemoteFileJob(candidate.Target)
                : new SongJob(Searcher.InferSongQuery(
                    candidate.Filename,
                    new SongQuery { Title = lookup.Metadata.Query }))
                {
                    ResolvedTarget = candidate,
                };
            followUp.ItemName = persistedJob.ItemName;
            followUp.WorkflowId = persistedJob.WorkflowId;
            var settings = jobSettingsResolver.ResolveFollowUp(followUp, request.Options);
            ValidateExplicitRemoteTransferOverrides(
                followUp,
                request.Options?.DownloadSettings ?? new DownloadSettingsPatchDto(),
                settings);
            RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
                followUp,
                settings,
                ResolveChildSettings);
            jobSettingsResolver.SetIsolatedJobOptions(followUp.Id, request.Options);
            await QueueAcceptedSubmissionAsync(followUp, settings, sourceJobId, ct).ConfigureAwait(false);
            summaries.Add(StateStore.GetJobSummary(followUp.Id) ?? BuildSubmittedJobSummary(followUp, sourceJobId));
        }

        return summaries;
    }

    private static IReadOnlyList<FileAttributeSnapshot>? DeserializeFileAttributes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PersistedFileAttribute[]>(json)?
                .Select(attribute => new FileAttributeSnapshot(attribute.Name, attribute.Value, attribute.Code))
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The retained result has invalid file-attribute data and cannot be downloaded safely.", ex);
        }
    }

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid sourceJobId, StartFolderDownloadRequestDto request, CancellationToken ct)
    {
        if (request.RequestedMode == ExtractionMode.Song)
            throw new ArgumentException("A directory selection cannot be interpreted as one song.");
        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalFolderDownloadAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var folder = JobRequestMapper.FindAlbumFolder(
            sourceJob,
            request.Folder,
            GetCurrentEngineUserSuccessCounts(),
            request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        if (request.RequestedMode == ExtractionMode.General)
        {
            var directoryJob = JobRequestMapper.CreateRemoteDirectoryDownload(folder, request.Selection);
            directoryJob.ItemName = sourceJob.ItemName;
            var directorySettings = jobSettingsResolver.ResolveFollowUp(directoryJob, request.Options);
            return await SubmitFollowUpJobAsync(
                sourceJobId, sourceJob, directoryJob, directorySettings,
                request.Options, isolateOptions: true, ct);
        }

        AlbumQuery? albumQuery = JobRequestMapper.ResolveFolderSelectionQuery(
            sourceJob,
            request.AlbumQuery);

        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        if (engine?.TryStartManualAlbumSelection(
            sourceJobId,
            folder,
            albumQuery,
            album => JobRequestMapper.ApplyFolderDownloadSelection(album, request.Selection),
            out var selectedAlbum) == true)
        {
            return StateStore.GetJobSummary(selectedAlbum!.Id) ?? BuildSubmittedJobSummary(selectedAlbum!, sourceJobId);
        }
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from this job require an album query.");

        AlbumJob albumJob = JobRequestMapper.CreateAlbumSelectionFollowUp(
            sourceJob,
            folder,
            albumQuery,
            request.Selection);

        var followUpSettings = jobSettingsResolver.ResolveFollowUp(albumJob, request.Options);

        return await SubmitFollowUpJobAsync(sourceJobId, sourceJob, albumJob, followUpSettings, request.Options, isolateOptions: true, ct);
    }

    private async Task<JobSummaryDto?> StartHistoricalFolderDownloadAsync(
        Guid sourceJobId,
        StartFolderDownloadRequestDto request,
        CancellationToken ct)
    {
        var historical = await ResolveHistoricalFolderAsync(sourceJobId, request.Folder, request.AlbumQuery, ct).ConfigureAwait(false);
        if (historical == null)
            return null;
        var folder = historical.Value.Folder;
        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);
        if (request.RequestedMode == ExtractionMode.General)
        {
            var directoryJob = JobRequestMapper.CreateRemoteDirectoryDownload(folder, request.Selection);
            directoryJob.ItemName = historical.Value.Job.ItemName;
            directoryJob.WorkflowId = historical.Value.Job.WorkflowId;
            var directorySettings = jobSettingsResolver.ResolveFollowUp(directoryJob, request.Options);
            ValidateExplicitRemoteTransferOverrides(
                directoryJob,
                request.Options?.DownloadSettings ?? new DownloadSettingsPatchDto(),
                directorySettings);
            RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
                directoryJob,
                directorySettings,
                ResolveChildSettings);
            jobSettingsResolver.SetIsolatedJobOptions(directoryJob.Id, request.Options);
            await QueueAcceptedSubmissionAsync(directoryJob, directorySettings, sourceJobId, ct).ConfigureAwait(false);
            return StateStore.GetJobSummary(directoryJob.Id) ?? BuildSubmittedJobSummary(directoryJob, sourceJobId);
        }
        var albumJob = new AlbumJob(new AlbumQuery(historical.Value.Query))
        {
            ResolvedTarget = folder,
            ItemName = historical.Value.Job.ItemName,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
            WorkflowId = historical.Value.Job.WorkflowId,
        };
        JobRequestMapper.ApplyFolderDownloadSelection(albumJob, request.Selection);
        var settings = jobSettingsResolver.ResolveFollowUp(albumJob, request.Options);
        jobSettingsResolver.SetIsolatedJobOptions(albumJob.Id, request.Options);
        await QueueAcceptedSubmissionAsync(albumJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(albumJob.Id) ?? BuildSubmittedJobSummary(albumJob, sourceJobId);
    }

    private async Task<(PersistedJob Job, AlbumFolder Folder, AlbumQuery Query)?> ResolveHistoricalFolderAsync(
        Guid sourceJobId,
        AlbumFolderRefDto folderRef,
        AlbumQueryDto? requestedQuery,
        CancellationToken ct)
    {
        if (persistence?.JobHistory == null || persistence.SearchHistory == null)
            return null;
        await persistence.WaitForJobHandoffAsync(sourceJobId, ct).ConfigureAwait(false);
        var job = await persistence.JobHistory.GetJobAsync(sourceJobId, ct).ConfigureAwait(false);
        var metadata = await persistence.SearchHistory.GetMetadataAsync(sourceJobId, ct).ConfigureAwait(false);
        if (job == null || metadata == null)
            return null;
        if (metadata.ResultPersistenceState is "Pruned" or "NotPersisted")
            throw new ArgumentException($"Cannot use this historical folder because its result data is {metadata.ResultPersistenceState.ToLowerInvariant()}.");
        var defaultProjection = HistoricalJobDtoMapper.DefaultFolderProjection(job);
        var query = requestedQuery != null
            ? JobRequestMapper.ToAlbumQuery(requestedQuery)
            : defaultProjection?.Query
                ?? throw new ArgumentException("Historical folder operations require an album query.");
        var inputs = new List<SearchProjectionInput>();
        await foreach (var input in persistence.SearchHistory
            .ReadProjectionInputsAsync(sourceJobId, ct)
            .ConfigureAwait(false))
            inputs.Add(input);
        SearchDefinition definition = await HistoricalJobDtoMapper.SearchDefinitionAsync(
            persistence.Submissions,
            job,
            ct).ConfigureAwait(false);
        var folder = SearchResultProjector.AlbumFolders(
                inputs,
                query,
                definition.ProjectionSettings.ToSettings(),
                GetCurrentEngineUserSuccessCounts())
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));
        if (folder == null)
        {
            string detail = metadata.ResultPersistenceState is "Incomplete" or "Interrupted"
                ? $" Persisted results are {metadata.ResultPersistenceState.ToLowerInvariant()}."
                : "";
            throw new ArgumentException("Requested folder was not found in retained search results." + detail);
        }
        return (job, folder, query);
    }

    public async Task<bool> CompleteManualSelectionAsync(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine != null && await engine.CompleteManualSelectionAsync(jobId);
    }

    public async Task<bool> SkipManualSelectionAsync(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine != null && await engine.SkipManualSelectionAsync(jobId);
    }

    private DownloadEngine CreateEngine(SoulseekClientManager clientManager)
    {
        var engine = new DownloadEngine(
            engineSettings,
            clientManager,
            jobSettingsResolver,
            directorySource: PeerBrowses,
            loggerFactory: loggerFactory,
            retireTerminalWorkflows: retireTerminalWorkflows);
        persistence?.AttachEngine(engine);
        StateStore.AttachEngine(engine);
        lock (engineGate)
            currentEngine = engine;
        StateStore.UpdateDaemonRuntime(ToSoulseekClientStatusDto(clientManager.State), restartCount);
        EngineCreated?.Invoke(engine);
        return engine;
    }

    private ConcurrentDictionary<string, int> GetCurrentEngineUserSuccessCounts()
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.UserSuccessCounts ?? new ConcurrentDictionary<string, int>();
    }

    internal IReadOnlyDictionary<string, int> GetUserSuccessCountSnapshot()
        => new Dictionary<string, int>(
            GetCurrentEngineUserSuccessCounts(),
            StringComparer.Ordinal);

    internal TJob? GetRuntimeJob<TJob>(Guid jobId)
        where TJob : Job
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.GetJob(jobId) as TJob;
    }

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => ServerSnapshotMapper.ToSubmittedJobSummary(job, sourceJobId);

    private static SearchRawResultDto ToSearchRawResultDto(SearchRawResult result)
        => new(
            result.Sequence,
            result.Revision,
            result.Username,
            result.Filename,
            result.Size,
            result.BitRate,
            result.SampleRate,
            result.Length,
            result.Visibility,
            result.QueueLength,
            result.ObservedAtUtc);

    private bool CanAcceptLoginRequiredJobs()
        => !string.IsNullOrWhiteSpace(engineSettings.MockFilesDir)
        || engineSettings.UseRandomLogin
        || (!string.IsNullOrWhiteSpace(engineSettings.Username)
            && !string.IsNullOrWhiteSpace(engineSettings.Password));

    private async Task<JobSummaryDto> SubmitFollowUpJobAsync(
        Guid sourceJobId,
        Job sourceJob,
        Job followUpJob,
        DownloadSettings settings,
        SubmissionOptionsDto? options,
        bool isolateOptions,
        CancellationToken ct)
    {
        followUpJob.WorkflowId = sourceJob.WorkflowId;
        JobRequestMapper.PropagateSourceMutationToFollowUp(sourceJob, followUpJob);
        if (isolateOptions)
            jobSettingsResolver.SetIsolatedJobOptions(followUpJob.Id, options);
        ValidateExplicitRemoteTransferOverrides(
            followUpJob,
            options?.DownloadSettings,
            settings);
        RemoteTransferSubmissionPolicy.NormalizeInheritedSettings(
            followUpJob,
            settings,
            ResolveChildSettings);
        await QueueAcceptedSubmissionAsync(followUpJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId);
    }

    private async Task QueueAcceptedSubmissionAsync(
        Job job,
        DownloadSettings settings,
        Guid? sourceJobId,
        CancellationToken cancellationToken,
        bool settingsAreFinal = false,
        string? commitFingerprint = null)
    {
        if (job.SubmissionId == null)
            SubmissionIdentity.AssignAccepted(job, settings);
        if (persistence?.Submissions != null)
        {
            await persistence.Submissions.CreateAsync(
                new SubmissionRegistration(
                    job.SubmissionId!.Value,
                    job.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                    SubmissionSpecification.CurrentSchemaVersion,
                    job.SubmissionSpecificationJson
                        ?? throw new InvalidOperationException("An accepted submission has no retained specification."),
                    job.RerunOfSubmissionId,
                    job.PreviewId,
                    job.ArtifactId,
                    commitFingerprint),
                cancellationToken).ConfigureAwait(false);
        }
        acceptedRootsAwaitingRegistration[job.SubmissionId!.Value] = job;
        job.EnsureDisplayId();
        await submissionChannel.Writer.WriteAsync(
            new QueuedSubmission(
                job,
                settings,
                SourceJobId: sourceJobId,
                SettingsAreFinal: settingsAreFinal),
            CancellationToken.None).ConfigureAwait(false);
    }

    private sealed record PersistedFileAttribute(int Code, string Name, int Value);

    private sealed record QueuedSubmission(
        Job Job,
        DownloadSettings? Settings,
        bool IsResume = false,
        Guid? SourceJobId = null,
        bool SettingsAreFinal = false)
    {
        public static QueuedSubmission Resume(Job job) => new(job, null, true);
    }
}
