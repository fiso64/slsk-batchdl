using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Snapshots;
using Soulseek;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Core.Chat;

namespace Sockseek.Server;

public sealed record LiveWorkflowPageItem(long FirstDisplayId, WorkflowSummaryDto Summary);
public sealed record LiveTransferDetail(TransferStateDto Transfer, TransferAttemptHistoryDto? LatestAttempt);

public sealed class EngineStateStore
{
    private readonly Lock gate = new();
    // Sequence allocation is protected by gate, while event callbacks run outside it.
    // Buffer per scope so concurrent handlers cannot publish a later sequence first.
    private readonly Lock stateBatchPublicationGate = new();
    private readonly Dictionary<StateStreamScopeDto, Dictionary<long, StateUpdateBatchDto>> pendingStateBatches = [];
    private readonly Dictionary<StateStreamScopeDto, long> publishedStateBatchSequences = [];
    private bool publishingStateBatches;
    // Keep records and workflow aggregate indexes in sync only through UpdateJobRecord.
    private readonly Dictionary<Guid, JobSnapshot> jobs = [];
    private readonly Dictionary<Guid, HashSet<Guid>> nestedJobIdsByContainer = [];
    private readonly Dictionary<Guid, HashSet<Guid>> containerIdsByNestedJob = [];
    private readonly Dictionary<Guid, JobRecord> records = [];
    private readonly Dictionary<Guid, WorkflowStateRecord> workflows = [];
    private readonly Dictionary<Guid, Guid?> parentJobIds = [];
    private readonly Dictionary<Guid, HashSet<Guid>> childJobIdsByParent = [];
    private readonly Dictionary<Guid, Guid> resultJobIds = [];
    private readonly Dictionary<Guid, Guid> sourceJobIds = [];
    private readonly HashSet<Guid> archivedSubmissionIds = [];
    private readonly HashSet<Guid> executionCompletedJobs = [];
    private readonly Dictionary<Guid, string> songTransferStates = [];
    private readonly Dictionary<Guid, TransferStateDto> activeTransfers = [];
    private readonly Dictionary<Guid, TransferStateDto> liveUploadTransfers = [];
    private readonly Dictionary<Guid, TransferAttemptHistoryDto> latestTransferAttempts = [];
    private readonly Dictionary<Guid, Guid> transferWorkflowIds = [];
    private readonly Dictionary<Guid, SearchStateDto> searchStates = [];
    private readonly Dictionary<Guid, JobStateDto> projectedJobs = [];
    private readonly Dictionary<Guid, WorkflowStateDto> projectedWorkflows = [];
    private readonly Dictionary<Guid, long> workflowStreamSequences = [];
    private readonly Dictionary<Guid, Guid> workflowStreamEpochs = [];
    private readonly Dictionary<Guid, int> workflowStreamReservations = [];
    private readonly Dictionary<StateStreamScopeDto, long> chatStreamSequences = [];
    private readonly Dictionary<Guid, long> userBrowseStreamSequences = [];
    private readonly Dictionary<Guid, UserBrowseDto> userBrowses = [];
    private readonly HashSet<Guid> daemonLiveWorkflowIds = [];
    private readonly Guid streamEpoch = Guid.NewGuid();
    private long daemonStreamSequence;
    private DaemonStateDto daemonState = new(
        0,
        new SoulseekClientStatusDto("None", [], false),
        0,
        null,
        new SharingStateDto(
            DaemonFeatureState.Disabled,
            "NotConfigured",
            [],
            0,
            0,
            new ShareCatalogStateDto(
                null,
                0,
                0,
                0,
                false,
                null,
                null),
            null,
            null),
        new UploadRuntimeStateDto(
            DaemonFeatureState.Disabled,
            "NotConfigured",
            false,
            0,
            0,
            0,
            0,
            0,
            null),
        new ChatRuntimeStateDto(
            DaemonFeatureState.Disabled,
            "PersistenceUnavailable",
            0,
            0,
            0,
            0,
            0),
        new NotificationSummaryDto(0, 0));

    public event Action<JobSummaryDto>? JobUpserted;
    public event Action<WorkflowSummaryDto>? WorkflowUpserted;
    public event Action<SearchStateDto>? SearchUpdated;
    public event Action<StateUpdateBatchDto>? StateBatchPublished;

    public void SetSubmissionArchived(Guid submissionId, bool archived)
    {
        lock (gate)
        {
            if (archived)
                archivedSubmissionIds.Add(submissionId);
            else
                archivedSubmissionIds.Remove(submissionId);
        }
    }

    internal EngineStateStoreRetainedWorkflowCounts RetainedWorkflowStateCounts
    {
        get
        {
            lock (gate)
            {
                return new EngineStateStoreRetainedWorkflowCounts(
                    jobs.Count,
                    records.Count,
                    workflows.Count,
                    parentJobIds.Count,
                    childJobIdsByParent.Count,
                    nestedJobIdsByContainer.Count,
                    containerIdsByNestedJob.Count,
                    resultJobIds.Count,
                    sourceJobIds.Count,
                    executionCompletedJobs.Count,
                    songTransferStates.Count,
                    activeTransfers.Count,
                    latestTransferAttempts.Count,
                    transferWorkflowIds.Count,
                    searchStates.Count,
                    projectedJobs.Count,
                    projectedWorkflows.Count,
                    workflowStreamSequences.Count,
                    workflowStreamEpochs.Count,
                    workflowStreamReservations.Count,
                    daemonLiveWorkflowIds.Count);
            }
        }
    }

    public TransferStateDto? GetLiveTransfer(Guid transferId)
    {
        lock (gate)
            return activeTransfers.GetValueOrDefault(transferId)
                   ?? liveUploadTransfers.GetValueOrDefault(transferId);
    }

    public IReadOnlyList<TransferStateDto> GetActiveTransferSnapshot()
    {
        lock (gate)
            return activeTransfers.Values.ToArray();
    }

    /// <summary>
    /// Captures one command-resolution population, including queued uploads
    /// which are intentionally absent from the compact active-state snapshot.
    /// </summary>
    public IReadOnlyList<TransferStateDto> GetCancellableTransferSnapshot()
    {
        lock (gate)
        {
            var snapshot = activeTransfers.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var pair in liveUploadTransfers)
                snapshot[pair.Key] = pair.Value;
            return snapshot.Values.ToArray();
        }
    }

    public LiveTransferDetail? GetLiveTransferDetail(Guid transferId)
    {
        lock (gate)
        {
            var transfer = activeTransfers.GetValueOrDefault(transferId)
                ?? liveUploadTransfers.GetValueOrDefault(transferId);
            return transfer == null
                ? null
                : new LiveTransferDetail(
                    transfer,
                    latestTransferAttempts.GetValueOrDefault(transferId));
        }
    }

    public void UpdateUploadTransfer(UploadTransferSnapshot upload)
    {
        IReadOnlyList<StateUpdateBatchDto> batches = [];
        lock (gate)
        {
            TransferStateDto current = ToUploadTransferState(upload);
            liveUploadTransfers[upload.TransferId] = current;
            if (ToUploadAttempt(upload) is { } attempt)
                latestTransferAttempts[upload.TransferId] = attempt;

            bool replicated = upload.State is not UploadTransferState.Queued
                              && (upload.Attempt is not null
                                  || activeTransfers.ContainsKey(upload.TransferId));
            if (!replicated)
                return;

            TransferDeltaDto delta;
            if (!activeTransfers.TryGetValue(upload.TransferId, out var previous))
            {
                activeTransfers[upload.TransferId] = current;
                delta = new TransferDeltaDto(
                    upload.TransferId,
                    current.Revision,
                    Added: current);
            }
            else
            {
                var status = TransferStatusEquals(previous.Status, current.Status)
                    ? null
                    : current.Status;
                var progress = previous.Progress == current.Progress ? null : current.Progress;
                var scheduling = previous.Scheduling == current.Scheduling
                    ? null
                    : current.Scheduling;
                activeTransfers[upload.TransferId] = current;
                if (status == null && progress == null && scheduling == null)
                    return;
                delta = new TransferDeltaDto(
                    upload.TransferId,
                    Math.Max(current.Revision, previous.Revision + 1),
                    Status: status,
                    Progress: progress,
                    Scheduling: scheduling);
            }

            var daemonDelta = StateDeltaDto.Empty with
            {
                Transfers = [delta],
            };
            batches = CreateStateBatches(
                daemonDelta,
                new Dictionary<Guid, StateDeltaDto>(),
                DateTimeOffset.UtcNow);
        }
        PublishStateBatches(batches);
    }

    public void RemoveUploadTransfer(Guid transferId)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            liveUploadTransfers.Remove(transferId);
            latestTransferAttempts.Remove(transferId);
            if (!activeTransfers.Remove(transferId))
                return;
            batches = CreateStateBatches(
                StateDeltaDto.Empty with { RemovedTransferIds = [transferId] },
                new Dictionary<Guid, StateDeltaDto>(),
                DateTimeOffset.UtcNow);
        }
        PublishStateBatches(batches);
    }

    public void AttachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered += OnJobRegistered;
        engine.Events.JobResultCreated += OnJobResultCreated;
        engine.Events.JobStateChanged += OnJobStateChanged;
        engine.Events.JobDiscoveryChanged += OnJobDiscoveryChanged;
        engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
        engine.Events.WorkflowRetired += OnWorkflowRetired;
        engine.Events.DownloadStarted += OnNestedSongDownloadStarted;
        engine.Events.FallbackTransferStarted += OnFallbackTransferStarted;
        engine.Events.DownloadProgress += OnDownloadProgress;
        engine.Events.DownloadStateChanged += OnDownloadStateChanged;
        engine.Events.TransferAttemptStarted += OnTransferAttemptStarted;
        engine.Events.TransferAttemptCompleted += OnTransferAttemptCompleted;
        engine.Events.TransferAttemptFailed += OnTransferAttemptFailed;
        engine.Events.TransferAttemptCancelled += OnTransferAttemptCancelled;
        engine.Events.TransferCompleted += OnTransferCompleted;
        engine.Events.TransferFailed += OnTransferFailed;
        engine.Events.TransferCancelled += OnTransferCancelled;
    }

    public void DetachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered -= OnJobRegistered;
        engine.Events.JobResultCreated -= OnJobResultCreated;
        engine.Events.JobStateChanged -= OnJobStateChanged;
        engine.Events.JobDiscoveryChanged -= OnJobDiscoveryChanged;
        engine.Events.JobExecutionCompleted -= OnJobExecutionCompleted;
        engine.Events.WorkflowRetired -= OnWorkflowRetired;
        engine.Events.DownloadStarted -= OnNestedSongDownloadStarted;
        engine.Events.FallbackTransferStarted -= OnFallbackTransferStarted;
        engine.Events.DownloadProgress -= OnDownloadProgress;
        engine.Events.DownloadStateChanged -= OnDownloadStateChanged;
        engine.Events.TransferAttemptStarted -= OnTransferAttemptStarted;
        engine.Events.TransferAttemptCompleted -= OnTransferAttemptCompleted;
        engine.Events.TransferAttemptFailed -= OnTransferAttemptFailed;
        engine.Events.TransferAttemptCancelled -= OnTransferAttemptCancelled;
        engine.Events.TransferCompleted -= OnTransferCompleted;
        engine.Events.TransferFailed -= OnTransferFailed;
        engine.Events.TransferCancelled -= OnTransferCancelled;
    }

    public JobSummaryDto? GetJobSummary(Guid jobId)
    {
        lock (gate)
        {
            return records.TryGetValue(jobId, out var record)
                ? record.Summary
                : null;
        }
    }

    public JobDetailDto? GetJobDetail(Guid jobId)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(jobId, out var job))
                UpdateJobRecord(job);

            if (!records.TryGetValue(jobId, out var record))
                return null;

            int childCount = childJobIdsByParent.TryGetValue(jobId, out var childIds)
                ? childIds.Count
                : 0;
            return new JobDetailDto(record.Summary, record.Payload, childCount);
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        lock (gate)
        {
            return FilterJobs(records.Values, query)
                .OrderBy(record => record.Summary.DisplayId)
                .Select(record => record.Summary)
                .ToList();
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobPageCandidates(
        JobQuery query,
        long? afterDisplayId,
        Guid? afterJobId,
        int take)
    {
        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take));

        lock (gate)
        {
            IEnumerable<JobRecord> filtered = FilterJobs(records.Values, query);
            if (afterDisplayId is long displayId && afterJobId is Guid jobId)
            {
                filtered = filtered.Where(record =>
                    record.Summary.DisplayId > displayId
                    || record.Summary.DisplayId == displayId && record.Id.CompareTo(jobId) > 0);
            }

            return filtered
                .OrderBy(record => record.Summary.DisplayId)
                .ThenBy(record => record.Id)
                .Take(take)
                .Select(record => record.Summary)
                .ToArray();
        }
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        lock (gate)
        {
            return workflows.Values
                .OrderBy(workflow => workflow.FirstDisplayId)
                .Select(workflow => workflow.ToSummary(records))
                .ToList();
        }
    }

    public IReadOnlyList<LiveWorkflowPageItem> GetWorkflowPageCandidates(
        long? afterFirstDisplayId,
        Guid? afterWorkflowId,
        int take)
    {
        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take));

        lock (gate)
        {
            IEnumerable<WorkflowStateRecord> filtered = workflows.Values;
            if (afterFirstDisplayId is long displayId && afterWorkflowId is Guid workflowId)
            {
                filtered = filtered.Where(workflow =>
                    workflow.FirstDisplayId > displayId
                    || workflow.FirstDisplayId == displayId
                        && workflow.WorkflowId.CompareTo(workflowId) > 0);
            }

            return filtered
                .OrderBy(workflow => workflow.FirstDisplayId)
                .ThenBy(workflow => workflow.WorkflowId)
                .Take(take)
                .Select(workflow => new LiveWorkflowPageItem(
                    workflow.FirstDisplayId,
                    workflow.ToSummary(records)))
                .ToArray();
        }
    }

    public WorkflowSummaryDto? GetWorkflowSummary(Guid workflowId)
    {
        lock (gate)
        {
            return workflows.TryGetValue(workflowId, out var workflow)
                ? workflow.ToSummary(records)
                : null;
        }
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId)
    {
        lock (gate)
        {
            if (!workflows.TryGetValue(workflowId, out var workflow))
                return null;

            return new WorkflowDetailDto(workflow.ToSummary(records));
        }
    }

    /// <summary>
    /// Captures bounded daemon live state and its cursor under the same ordering lock
    /// used to allocate stream sequences.
    /// </summary>
    public StateSnapshotDto GetDaemonSnapshot()
    {
        lock (gate)
        {
            var workflowIds = daemonLiveWorkflowIds.ToHashSet();
            return new StateSnapshotDto(
                StateStreamScopeDto.Daemon,
                new StateStreamPositionDto(streamEpoch, daemonStreamSequence),
                DateTimeOffset.UtcNow,
                daemonState,
                projectedWorkflows.Values
                    .Where(workflow => workflowIds.Contains(workflow.Summary.WorkflowId))
                    .OrderBy(workflow => workflow.Summary.Title)
                    .ToList(),
                projectedJobs.Values
                    .Where(job => workflowIds.Contains(job.Display.WorkflowId))
                    .OrderBy(job => job.Display.DisplayId)
                    .ToList(),
                searchStates.Values
                    .Where(search => workflowIds.Contains(search.WorkflowId))
                    .OrderBy(search => search.JobId)
                    .ToList(),
                activeTransfers.Values
                    .Where(transfer =>
                        transfer.Identity.WorkflowId is null
                        || workflowIds.Contains(transfer.Identity.WorkflowId.Value))
                    .OrderBy(transfer => transfer.TransferId)
                    .ToList());
        }
    }

    /// <summary>
    /// Captures the complete requested workflow, including terminal jobs, and its
    /// workflow-local cursor under one ordering lock.
    /// </summary>
    public StateSnapshotDto GetWorkflowSnapshot(Guid workflowId)
    {
        lock (gate)
        {
            var workflowRows = projectedWorkflows.TryGetValue(workflowId, out var workflow)
                ? new[] { workflow }
                : [];

            return new StateSnapshotDto(
                StateStreamScopeDto.Workflow(workflowId),
                new StateStreamPositionDto(
                    GetWorkflowSnapshotEpoch(workflowId),
                    workflowStreamSequences.GetValueOrDefault(workflowId)),
                DateTimeOffset.UtcNow,
                null,
                workflowRows,
                projectedJobs.Values
                    .Where(job => job.Display.WorkflowId == workflowId)
                    .OrderBy(job => job.Display.DisplayId)
                    .ToList(),
                searchStates.Values
                    .Where(search => search.WorkflowId == workflowId)
                    .OrderBy(search => search.JobId)
                    .ToList(),
                activeTransfers.Values
                    .Where(transfer => transfer.Identity.WorkflowId == workflowId)
                    .OrderBy(transfer => transfer.TransferId)
                    .ToList());
        }
    }

    /// <summary>
    /// Reserves the stream generation before a live client fetches its initial snapshot.
    /// This keeps the snapshot and the first delta on one epoch without retaining state
    /// for arbitrary snapshot requests that never establish a subscription.
    /// </summary>
    public void ReserveWorkflowStream(Guid workflowId)
    {
        if (workflowId == Guid.Empty)
            throw new ArgumentException("A workflow stream requires a workflow ID.", nameof(workflowId));

        lock (gate)
        {
            workflowStreamReservations[workflowId] =
                workflowStreamReservations.GetValueOrDefault(workflowId) + 1;
            GetOrCreateWorkflowStreamEpoch(workflowId);
        }
    }

    public void ReleaseWorkflowStreamReservation(Guid workflowId)
    {
        lock (gate)
        {
            if (!workflowStreamReservations.TryGetValue(workflowId, out int count))
                return;

            if (count > 1)
            {
                workflowStreamReservations[workflowId] = count - 1;
                return;
            }

            workflowStreamReservations.Remove(workflowId);
            if (!workflows.ContainsKey(workflowId))
            {
                workflowStreamSequences.Remove(workflowId);
                workflowStreamEpochs.Remove(workflowId);
            }
        }
    }

    public void UpdateDaemonState(
        SoulseekClientStatusDto soulseekClient,
        int restartCount,
        DateTimeOffset? searchRateLimitResetsAtUtc)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var next = new DaemonStateDto(
                daemonState.Revision + 1,
                soulseekClient,
                restartCount,
                searchRateLimitResetsAtUtc,
                daemonState.Sharing,
                daemonState.Uploads,
                daemonState.Chat,
                daemonState.Notifications);
            if (daemonState with { Revision = 0 } == next with { Revision = 0 })
                return;

            daemonState = next;
            batches = CreateStateBatches(
                StateDeltaDto.Empty with { Daemon = daemonState },
                new Dictionary<Guid, StateDeltaDto>(),
                DateTimeOffset.UtcNow);
        }

        PublishStateBatches(batches);
    }

    public void UpdateSearchRateLimit(DateTimeOffset? resetsAtUtc)
    {
        SoulseekClientStatusDto soulseekClient;
        int restartCount;
        lock (gate)
        {
            soulseekClient = daemonState.SoulseekClient;
            restartCount = daemonState.RestartCount;
        }

        UpdateDaemonState(soulseekClient, restartCount, resetsAtUtc);
    }

    public void UpdateDaemonRuntime(SoulseekClientStatusDto soulseekClient, int restartCount)
    {
        DateTimeOffset? searchRateLimitResetsAtUtc;
        lock (gate)
            searchRateLimitResetsAtUtc = daemonState.SearchRateLimitResetsAtUtc;

        UpdateDaemonState(soulseekClient, restartCount, searchRateLimitResetsAtUtc);
    }

    public void UpdateSharingRuntime(
        SharingStateDto sharing,
        UploadRuntimeStateDto uploads)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var next = daemonState with
            {
                Revision = daemonState.Revision + 1,
                Sharing = sharing,
                Uploads = uploads,
            };
            if (daemonState with { Revision = 0 } == next with { Revision = 0 })
                return;
            daemonState = next;
            batches = CreateStateBatches(
                StateDeltaDto.Empty with { Daemon = daemonState },
                new Dictionary<Guid, StateDeltaDto>(),
                DateTimeOffset.UtcNow);
        }
        PublishStateBatches(batches);
    }

    public void UpdateChatRuntime(
        ChatRuntimeStateDto chat,
        NotificationSummaryDto notifications)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var next = daemonState with
            {
                Revision = daemonState.Revision + 1,
                Chat = chat,
                Notifications = notifications,
            };
            if (daemonState with { Revision = 0 } == next with { Revision = 0 })
                return;
            daemonState = next;
            batches = CreateStateBatches(
                StateDeltaDto.Empty with { Daemon = daemonState },
                new Dictionary<Guid, StateDeltaDto>(),
                DateTimeOffset.UtcNow);
        }
        PublishStateBatches(batches);
    }

    public void PublishNotification(UserNotificationRecord notification)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            batches = CreateStateBatches(
                StateDeltaDto.Empty with
                {
                    Notifications = [ChatDtoMapper.ToDto(notification)],
                },
                new Dictionary<Guid, StateDeltaDto>(),
                notification.CreatedAtUtc);
        }
        PublishStateBatches(batches);
    }

    public StateStreamPositionDto GetChatPosition(StateStreamScopeDto scope)
    {
        scope.Validate();
        if (scope.Kind is not (StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
            throw new ArgumentException("A chat position requires a chat scope.", nameof(scope));
        lock (gate)
            return new StateStreamPositionDto(streamEpoch, chatStreamSequences.GetValueOrDefault(scope));
    }

    public void PublishChatTarget(ChatTargetDeltaDto delta)
    {
        StateStreamScopeDto scope = delta.Kind == ChatTargetKind.Direct
            ? StateStreamScopeDto.ChatConversation(delta.TargetId)
            : StateStreamScopeDto.ChatRoom(delta.TargetId);
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            long previous = chatStreamSequences.GetValueOrDefault(scope);
            long sequence = previous + 1;
            chatStreamSequences[scope] = sequence;
            batches =
            [
                new StateUpdateBatchDto(
                    scope,
                    streamEpoch,
                    previous,
                    sequence,
                    DateTimeOffset.UtcNow,
                    StateDeltaDto.Empty with { ChatTargets = [delta] },
                    [])
            ];
        }
        PublishStateBatches(batches);
    }

    public StateSnapshotDto GetUserBrowseSnapshot(UserBrowseDto resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (gate)
        {
            if (!userBrowses.TryGetValue(resource.BrowseId, out UserBrowseDto? current)
                || resource.Revision >= current.Revision)
            {
                userBrowses[resource.BrowseId] = resource;
            }
            else
            {
                resource = current;
            }
            return new StateSnapshotDto(
                StateStreamScopeDto.UserBrowse(resource.BrowseId),
                new StateStreamPositionDto(
                    streamEpoch,
                    userBrowseStreamSequences.GetValueOrDefault(resource.BrowseId)),
                DateTimeOffset.UtcNow,
                null,
                [],
                [],
                [],
                [],
                UserBrowse: resource);
        }
    }

    public void UpdateUserBrowse(UserBrowseDto resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        StateUpdateBatchDto batch;
        lock (gate)
        {
            if (userBrowses.TryGetValue(resource.BrowseId, out UserBrowseDto? current)
                && resource.Revision <= current.Revision)
            {
                return;
            }
            userBrowses[resource.BrowseId] = resource;
            long previous = userBrowseStreamSequences.GetValueOrDefault(resource.BrowseId);
            long sequence = previous + 1;
            userBrowseStreamSequences[resource.BrowseId] = sequence;
            batch = new StateUpdateBatchDto(
                StateStreamScopeDto.UserBrowse(resource.BrowseId),
                streamEpoch,
                previous,
                sequence,
                resource.UpdatedAt,
                StateDeltaDto.Empty with { UserBrowse = resource },
                []);
        }
        PublishStateBatches([batch]);
    }

    /// <summary>
    /// Drops the live projection after its backing ephemeral resource has been
    /// removed. There is no removal delta because future snapshots already return
    /// 410 before entering the live-state store.
    /// </summary>
    public void RemoveUserBrowse(Guid browseId)
    {
        lock (gate)
        {
            userBrowses.Remove(browseId);
            userBrowseStreamSequences.Remove(browseId);
        }
    }

    /// <summary>
    /// Allocates activity positions under the same ordering boundary as snapshots and
    /// state changes. Activity is emitted to the daemon stream and, when scoped, to the
    /// matching workflow stream.
    /// </summary>
    public void PublishActivity(
        string type,
        ActivityPayloadDto payload,
        Guid? workflowId = null,
        Guid? jobId = null,
        Guid? transferId = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var occurredAt = occurredAtUtc ?? DateTimeOffset.UtcNow;
            batches = CreateActivityBatches(
                type,
                payload,
                workflowId,
                jobId,
                transferId,
                occurredAt);
        }

        PublishStateBatches(batches);
    }

    public ServerStatusDto GetStatistics()
    {
        lock (gate)
        {
            int totalJobCount = records.Count;
            int activeJobCount = workflows.Values.Sum(workflow => workflow.ActiveJobCount);
            int totalWorkflowCount = workflows.Count;
            int activeWorkflowCount = workflows.Values.Count(workflow => workflow.ActiveJobCount > 0);

            return new ServerStatusDto(
                new SoulseekClientStatusDto("None", [], false),
                totalJobCount,
                activeJobCount,
                totalWorkflowCount,
                activeWorkflowCount,
                0);
        }
    }

    public void MarkActiveJobsInfrastructureFailed(string reason, string? detail = null)
    {
        List<JobSummaryDto> changedJobs;
        List<WorkflowSummaryDto> changedWorkflows;
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var failedJobs = jobs.Values
                .Where(job => EffectiveLifecycleState(job) != JobLifecycleState.Terminal)
                .Select(job => job with
                {
                    LifecycleState = JobLifecycleState.Terminal,
                    ActivityPhase = JobActivityPhase.None,
                    ActivityUntilUtc = null,
                    TerminalOutcome = JobTerminalOutcome.Failed,
                    FailureReason = JobFailureReason.Other,
                    FailureMessage = "Infrastructure failure: " + reason,
                    FailureDetail = detail,
                    CanCancel = false,
                    Revision = job.Revision + 1,
                })
                .ToList();

            foreach (var failedJob in failedJobs)
            {
                StoreJob(failedJob);
                UpdateJobRecord(failedJob);
            }

            changedJobs = failedJobs
                .Select(job => records[job.Id].Summary)
                .OrderBy(summary => summary.DisplayId)
                .ToList();
            changedWorkflows = changedJobs
                .Select(job => job.WorkflowId)
                .Distinct()
                .Select(BuildWorkflowSummary)
                .ToList();
            batches = ProjectStateChanges(
                changedJobs,
                changedWorkflows,
                [],
                [],
                [],
                DateTimeOffset.UtcNow);
        }

        PublishJobAndWorkflowUpserts(changedJobs, changedWorkflows);
        PublishStateBatches(batches);
    }

    public static ServerJobKind GetJobKind(Job job)
        => ServerSnapshotMapper.ToServerJobKind(job);

    public void SetSourceJob(Guid jobId, Guid sourceJobId)
    {
        JobSummaryDto? summary = null;
        IReadOnlyList<StateUpdateBatchDto> batches = [];
        lock (gate)
        {
            sourceJobIds[jobId] = sourceJobId;
            if (jobs.TryGetValue(jobId, out var job))
            {
                summary = UpdateJobRecord(job).Summary;
                batches = ProjectStateChanges(
                    [summary],
                    [],
                    [],
                    [],
                    [],
                    DateTimeOffset.UtcNow);
            }
        }

        if (summary != null)
            JobUpserted?.Invoke(summary);
        PublishStateBatches(batches);
    }

    private void OnJobRegistered(JobRegisteredChange change)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            StoreJob(change.Job);
            parentJobIds[change.Job.Id] = change.ParentJobId;
            if (change.SourceJobId is Guid sourceJobId)
                sourceJobIds[change.Job.Id] = sourceJobId;
            summary = UpdateJobRecord(change.Job).Summary;
            workflowSummary = BuildWorkflowSummary(change.Job.WorkflowId);
            batches = ProjectStateChanges(
                [summary],
                [workflowSummary],
                [],
                [],
                [],
                change.OccurredAtUtc);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
        PublishStateBatches(batches);
    }

    private void OnJobResultCreated(JobResultCreatedChange change)
    {
        List<JobSummaryDto> changedJobs = [];
        WorkflowSummaryDto? workflowSummary = null;
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            StoreJob(change.ExtractJob);
            resultJobIds[change.ExtractJob.Id] = change.ResultJob.Id;

            changedJobs.Add(UpdateJobRecord(change.ExtractJob).Summary);

            if (jobs.ContainsKey(change.ResultJob.Id))
            {
                StoreJob(change.ResultJob);
                changedJobs.Add(UpdateJobRecord(change.ResultJob).Summary);
            }
            else if (change.ExtractJob.Payload is ExtractJobSnapshotPayload { AutoProcessResult: true })
            {
                StoreJob(change.ResultJob);
                parentJobIds[change.ResultJob.Id] = parentJobIds.GetValueOrDefault(change.ExtractJob.Id);
                changedJobs.Add(UpdateJobRecord(change.ResultJob).Summary);
            }

            if (workflows.ContainsKey(change.ExtractJob.WorkflowId))
                workflowSummary = BuildWorkflowSummary(change.ExtractJob.WorkflowId);

            changedJobs = changedJobs.DistinctBy(summary => summary.JobId).ToList();
            batches = ProjectStateChanges(
                changedJobs,
                workflowSummary != null ? [workflowSummary] : [],
                [],
                [],
                [],
                change.OccurredAtUtc);
        }

        PublishJobAndWorkflowUpserts(
            changedJobs,
            workflowSummary != null ? [workflowSummary] : []);
        PublishStateBatches(batches);
    }

    private void OnJobStateChanged(JobStateChangedChange change)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        SearchStateDto? searchUpdate;
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            if (ServerSnapshotMapper.IsRunningOrPending(change.Job))
                executionCompletedJobs.Remove(change.Job.Id);

            StoreJob(change.Job);
            var changedRecords = UpdateRecordsContainingJob(change.Job.Id);
            changedRecords.Add(UpdateJobRecord(change.Job));
            summaries = changedRecords
                .DistinctBy(record => record.Id)
                .Select(record => record.Summary)
                .ToList();
            workflowSummaries = [BuildWorkflowSummary(change.Job.WorkflowId)];
            searchUpdate = ToSearchState(change.Job);
            batches = ProjectStateChanges(
                summaries,
                workflowSummaries,
                searchUpdate != null ? [searchUpdate] : [],
                [],
                [],
                change.OccurredAtUtc);
        }

        if (summaries.Count > 0 || workflowSummaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, workflowSummaries);

        if (searchUpdate != null)
            SearchUpdated?.Invoke(searchUpdate);
        PublishStateBatches(batches);
    }

    private void OnJobDiscoveryChanged(JobDiscoveryChangedChange change)
    {
        List<JobSummaryDto> summaries = [];
        SearchStateDto? searchUpdate;
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            StoreJob(change.Job);
            var changedRecords = UpdateRecordsContainingJob(change.Job.Id);
            changedRecords.Add(UpdateJobRecord(change.Job));
            summaries.AddRange(changedRecords
                .DistinctBy(record => record.Id)
                .Select(record => record.Summary));
            searchUpdate = ToSearchState(change.Job);
            batches = ProjectStateChanges(
                summaries,
                [],
                searchUpdate != null ? [searchUpdate] : [],
                [],
                [],
                change.OccurredAtUtc);
        }

        if (summaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, []);

        if (searchUpdate != null)
            SearchUpdated?.Invoke(searchUpdate);
        PublishStateBatches(batches);
    }

    private void OnJobExecutionCompleted(JobExecutionCompletedChange change)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            StoreJob(change.Job);
            executionCompletedJobs.Add(change.Job.Id);
            summary = UpdateJobRecord(change.Job).Summary;
            workflowSummary = BuildWorkflowSummary(change.Job.WorkflowId);
            batches = ProjectStateChanges(
                [summary],
                [workflowSummary],
                [],
                [],
                [],
                change.OccurredAtUtc);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
        PublishStateBatches(batches);
    }

    private void OnWorkflowRetired(WorkflowRetiredChange change)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            Guid workflowId = change.WorkflowId;
            var jobIds = records.Values
                .Where(record => record.WorkflowId == workflowId)
                .Select(record => record.Id)
                .ToHashSet();
            var searchJobIds = searchStates.Values
                .Where(search => search.WorkflowId == workflowId)
                .Select(search => search.JobId)
                .ToHashSet();
            var transferIds = transferWorkflowIds
                .Where(pair => pair.Value == workflowId)
                .Select(pair => pair.Key)
                .ToHashSet();

            var removal = StateDeltaDto.Empty with
            {
                RemovedWorkflowIds = [workflowId],
                RemovedJobIds = jobIds.Order().ToArray(),
                RemovedSearchJobIds = searchJobIds.Order().ToArray(),
                RemovedTransferIds = transferIds.Order().ToArray(),
            };
            batches = CreateStateBatches(
                removal,
                new Dictionary<Guid, StateDeltaDto> { [workflowId] = removal },
                change.OccurredAtUtc);

            foreach (Guid jobId in jobIds)
                RemoveJobState(jobId);
            foreach (Guid searchJobId in searchJobIds)
                searchStates.Remove(searchJobId);
            foreach (Guid transferId in transferIds)
            {
                activeTransfers.Remove(transferId);
                transferWorkflowIds.Remove(transferId);
                latestTransferAttempts.Remove(transferId);
            }

            workflows.Remove(workflowId);
            projectedWorkflows.Remove(workflowId);
            daemonLiveWorkflowIds.Remove(workflowId);
        }

        PublishStateBatches(batches);
        ReleaseWorkflowStream(change.WorkflowId);
    }

    private void RemoveJobState(Guid jobId)
    {
        jobs.Remove(jobId);
        records.Remove(jobId);
        executionCompletedJobs.Remove(jobId);
        songTransferStates.Remove(jobId);
        searchStates.Remove(jobId);
        projectedJobs.Remove(jobId);
        parentJobIds.Remove(jobId);
        resultJobIds.Remove(jobId);
        sourceJobIds.Remove(jobId);

        if (childJobIdsByParent.Remove(jobId, out var directChildren))
        {
            foreach (Guid childId in directChildren)
                parentJobIds.Remove(childId);
        }
        RemoveIndexValue(childJobIdsByParent, jobId);

        if (nestedJobIdsByContainer.Remove(jobId, out var nestedIds))
        {
            foreach (Guid nestedId in nestedIds)
            {
                if (containerIdsByNestedJob.TryGetValue(nestedId, out var containers))
                {
                    containers.Remove(jobId);
                    if (containers.Count == 0)
                        containerIdsByNestedJob.Remove(nestedId);
                }
            }
        }
        if (containerIdsByNestedJob.Remove(jobId, out var containerIds))
        {
            foreach (Guid containerId in containerIds)
            {
                if (nestedJobIdsByContainer.TryGetValue(containerId, out var nested))
                {
                    nested.Remove(jobId);
                    if (nested.Count == 0)
                        nestedJobIdsByContainer.Remove(containerId);
                }
            }
        }
    }

    private static void RemoveIndexValue(
        Dictionary<Guid, HashSet<Guid>> index,
        Guid value)
    {
        foreach (Guid key in index.Keys.ToArray())
        {
            index[key].Remove(value);
            if (index[key].Count == 0)
                index.Remove(key);
        }
    }

    private void ReleaseWorkflowStream(Guid workflowId)
    {
        var scope = StateStreamScopeDto.Workflow(workflowId);
        lock (stateBatchPublicationGate)
        {
            pendingStateBatches.Remove(scope);
            publishedStateBatchSequences.Remove(scope);
        }
        lock (gate)
        {
            workflowStreamSequences.Remove(workflowId);
            workflowStreamEpochs.Remove(workflowId);
        }
    }

    private void OnDownloadStateChanged(DownloadStateChangedChange change)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            songTransferStates[change.Song.Id] = change.State;
            StoreJob(change.Song);
            UpdateJobRecord(change.Song);
            UpdateRecordsContainingJob(change.Song.Id);
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            batches = ProjectStateChanges(
                [],
                [],
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        PublishStateBatches(batches);
    }

    private void OnNestedSongDownloadStarted(DownloadStartedChange change)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            StoreJob(change.Song);
            var containingRecords = UpdateRecordsContainingJob(change.Song.Id);
            summaries = containingRecords.Select(record => record.Summary).ToList();
            workflowSummaries = containingRecords
                .Select(record => record.WorkflowId)
                .Distinct()
                .Select(BuildWorkflowSummary)
                .ToList();
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            batches = ProjectStateChanges(
                summaries,
                workflowSummaries,
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        if (summaries.Count > 0 || workflowSummaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, workflowSummaries);
        PublishStateBatches(batches);
    }

    private void OnFallbackTransferStarted(FallbackTransferStartedChange change)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            batches = ProjectStateChanges(
                [],
                [],
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        PublishStateBatches(batches);
    }

    private void OnDownloadProgress(DownloadProgressedChange change)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var transferDelta = UpsertTransfer(change.Transfer, isTerminal: false);
            batches = ProjectStateChanges(
                [],
                [],
                [],
                [transferDelta],
                [],
                change.OccurredAtUtc);
        }

        PublishStateBatches(batches);
    }

    private void OnTransferAttemptStarted(TransferAttemptStartedChange change)
    {
        lock (gate)
        {
            latestTransferAttempts[change.Transfer.Id] = new TransferAttemptHistoryDto(
                change.AttemptId,
                change.Transfer.Id,
                change.AttemptNumber,
                change.Source.ToString(),
                "Started",
                change.Transfer.Username,
                change.Transfer.RemotePath,
                change.OutputPath,
                change.OccurredAtUtc,
                null,
                "None",
                null,
                change.AttemptRevision);
        }
    }

    private void OnTransferAttemptCompleted(TransferAttemptCompletedChange change)
        => UpdateTerminalAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Completed",
            "None",
            null);

    private void OnTransferAttemptFailed(TransferAttemptFailedChange change)
        => UpdateTerminalAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Failed",
            "AttemptFailed",
            change.Exception.Message);

    private void OnTransferAttemptCancelled(TransferAttemptCancelledChange change)
        => UpdateTerminalAttempt(
            change.Transfer,
            change.AttemptId,
            change.AttemptNumber,
            change.AttemptRevision,
            change.OccurredAtUtc,
            "Cancelled",
            change.Reason.ToString(),
            null);

    private void UpdateTerminalAttempt(
        TransferSnapshot transfer,
        Guid attemptId,
        int attemptNumber,
        long attemptRevision,
        DateTimeOffset occurredAtUtc,
        string state,
        string failureReason,
        string? failureMessage)
    {
        lock (gate)
        {
            var previous = latestTransferAttempts.GetValueOrDefault(transfer.Id);
            latestTransferAttempts[transfer.Id] = new TransferAttemptHistoryDto(
                attemptId,
                transfer.Id,
                attemptNumber,
                transfer.Source.ToString(),
                state,
                transfer.Username,
                transfer.RemotePath,
                transfer.LocalPath ?? previous?.OutputPath,
                previous is { AttemptId: var previousId } && previousId == attemptId
                    ? previous.StartedAtUtc
                    : occurredAtUtc,
                occurredAtUtc,
                failureReason,
                failureMessage,
                attemptRevision);
        }
    }

    private void OnTransferCompleted(TransferCompletedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc);

    private void OnTransferFailed(TransferFailedChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc);

    private void OnTransferCancelled(TransferCancelledChange change)
        => OnTerminalTransfer(change.Transfer, change.OccurredAtUtc);

    private void OnTerminalTransfer(TransferSnapshot transfer, DateTimeOffset occurredAtUtc)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            var transferDelta = UpsertTransfer(transfer, isTerminal: true);
            activeTransfers.Remove(transfer.Id);
            latestTransferAttempts.Remove(transfer.Id);
            batches = ProjectStateChanges(
                [],
                [],
                [],
                [transferDelta],
                [transfer.Id],
                occurredAtUtc);
        }

        PublishStateBatches(batches);
    }

    private void PublishJobAndWorkflowUpserts(
        IReadOnlyList<JobSummaryDto> jobSummaries,
        IReadOnlyList<WorkflowSummaryDto> workflowSummaries)
    {
        foreach (var summary in jobSummaries)
            JobUpserted?.Invoke(summary);

        foreach (var workflow in workflowSummaries)
            WorkflowUpserted?.Invoke(workflow);
    }

    private IReadOnlyList<StateUpdateBatchDto> ProjectStateChanges(
        IReadOnlyList<JobSummaryDto> jobSummaries,
        IReadOnlyList<WorkflowSummaryDto> workflowSummaries,
        IReadOnlyList<SearchStateDto> searches,
        IReadOnlyList<TransferDeltaDto> transfers,
        IReadOnlyList<Guid> removedTransferIds,
        DateTimeOffset occurredAtUtc)
    {
        var jobDeltas = new List<JobDeltaDto>();
        foreach (var summary in jobSummaries.DistinctBy(summary => summary.JobId))
        {
            long coreRevision = jobs.GetValueOrDefault(summary.JobId)?.Revision ?? 0;
            var delta = ProjectJob(summary, coreRevision);
            if (delta != null)
                jobDeltas.Add(delta);
        }

        var workflowStates = new List<WorkflowStateDto>();
        foreach (var summary in workflowSummaries.DistinctBy(summary => summary.WorkflowId))
        {
            var projected = ProjectWorkflow(summary);
            if (projected != null)
                workflowStates.Add(projected);
        }

        var searchUpdates = new List<SearchStateDto>();
        var removedSearchJobIds = new List<Guid>();
        foreach (var search in searches)
        {
            var current = searchStates.GetValueOrDefault(search.JobId);
            if (current == null
                || search.Revision > current.Revision
                || search.ResultCount != current.ResultCount
                || search.IsComplete != current.IsComplete)
            {
                searchUpdates.Add(search);
            }

            if (search.IsComplete)
            {
                searchStates.Remove(search.JobId);
                removedSearchJobIds.Add(search.JobId);
            }
            else
            {
                searchStates[search.JobId] = search;
            }
        }

        var affectedWorkflowIds = workflowSummaries.Select(workflow => workflow.WorkflowId)
            .Concat(jobDeltas.Select(JobWorkflowId))
            .Concat(searchUpdates.Select(search => search.WorkflowId))
            .Concat(transfers.Select(TransferWorkflowId))
            .Concat(removedTransferIds.Select(id => transferWorkflowIds.GetValueOrDefault(id)))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var wasDaemonLive = affectedWorkflowIds.ToDictionary(
            workflowId => workflowId,
            workflowId => daemonLiveWorkflowIds.Contains(workflowId));
        var isDaemonLive = affectedWorkflowIds.ToDictionary(
            workflowId => workflowId,
            workflowId => projectedWorkflows.TryGetValue(workflowId, out var workflow)
                && workflow.Summary.State == ServerWorkflowState.Active);

        var workflowDeltas = new Dictionary<Guid, StateDeltaDto>();
        foreach (var workflowId in affectedWorkflowIds)
        {
            workflowDeltas[workflowId] = new StateDeltaDto(
                null,
                workflowStates.Where(workflow => workflow.Summary.WorkflowId == workflowId).ToList(),
                jobDeltas.Where(job => JobWorkflowId(job) == workflowId).ToList(),
                searchUpdates.Where(search => search.WorkflowId == workflowId).ToList(),
                transfers.Where(transfer => TransferWorkflowId(transfer) == workflowId).ToList(),
                [],
                [],
                removedSearchJobIds.Where(id => projectedJobs.GetValueOrDefault(id)?.Display.WorkflowId == workflowId).ToList(),
                removedTransferIds.Where(id => transferWorkflowIds.GetValueOrDefault(id) == workflowId).ToList());
        }

        var daemonWorkflowIds = affectedWorkflowIds
            .Where(workflowId => wasDaemonLive[workflowId] || isDaemonLive[workflowId])
            .ToHashSet();
        var terminalWorkflowIds = daemonWorkflowIds
            .Where(workflowId => wasDaemonLive[workflowId] && !isDaemonLive[workflowId])
            .ToHashSet();
        var removedDaemonJobIds = projectedJobs.Values
            .Where(job => terminalWorkflowIds.Contains(job.Display.WorkflowId))
            .Select(job => job.JobId)
            .ToList();
        var removedDaemonSearchIds = searchStates.Values
            .Where(search => terminalWorkflowIds.Contains(search.WorkflowId))
            .Select(search => search.JobId)
            .Concat(removedSearchJobIds.Where(id =>
                terminalWorkflowIds.Contains(projectedJobs.GetValueOrDefault(id)?.Display.WorkflowId ?? Guid.Empty)))
            .Distinct()
            .ToList();
        var removedDaemonTransferIds = activeTransfers.Values
            .Where(transfer =>
                transfer.Identity.WorkflowId is { } workflowId
                && terminalWorkflowIds.Contains(workflowId))
            .Select(transfer => transfer.TransferId)
            .Concat(removedTransferIds.Where(id => terminalWorkflowIds.Contains(transferWorkflowIds.GetValueOrDefault(id))))
            .Distinct()
            .ToList();

        var daemonDelta = new StateDeltaDto(
            null,
            workflowStates.Where(workflow => daemonWorkflowIds.Contains(workflow.Summary.WorkflowId)).ToList(),
            jobDeltas.Where(job => daemonWorkflowIds.Contains(JobWorkflowId(job))).ToList(),
            searchUpdates.Where(search => daemonWorkflowIds.Contains(search.WorkflowId)).ToList(),
            transfers.Where(transfer => daemonWorkflowIds.Contains(TransferWorkflowId(transfer))).ToList(),
            terminalWorkflowIds.ToList(),
            removedDaemonJobIds,
            removedSearchJobIds
                .Where(id => daemonWorkflowIds.Contains(projectedJobs.GetValueOrDefault(id)?.Display.WorkflowId ?? Guid.Empty))
                .Concat(removedDaemonSearchIds)
                .Distinct()
                .ToList(),
            removedTransferIds
                .Where(id => daemonWorkflowIds.Contains(transferWorkflowIds.GetValueOrDefault(id)))
                .Concat(removedDaemonTransferIds)
                .Distinct()
                .ToList());

        foreach (var workflowId in affectedWorkflowIds)
        {
            if (isDaemonLive[workflowId])
                daemonLiveWorkflowIds.Add(workflowId);
            else
                daemonLiveWorkflowIds.Remove(workflowId);
        }

        return CreateStateBatches(daemonDelta, workflowDeltas, occurredAtUtc);
    }

    private JobDeltaDto? ProjectJob(JobSummaryDto summary, long coreRevision)
    {
        if (!projectedJobs.TryGetValue(summary.JobId, out var previous))
        {
            var added = JobStateDto.FromSummary(summary, Math.Max(1, coreRevision));
            projectedJobs[summary.JobId] = added;
            return new JobDeltaDto(summary.JobId, added.Revision, Added: added);
        }

        var candidate = JobStateDto.FromSummary(summary, previous.Revision);
        var display = JobDisplayEquals(previous.Display, candidate.Display) ? null : candidate.Display;
        var lifecycle = JobLifecycleEquals(previous.Lifecycle, candidate.Lifecycle) ? null : candidate.Lifecycle;
        var discovery = previous.Discovery == candidate.Discovery ? null : candidate.Discovery;
        var relationships = previous.Relationships == candidate.Relationships ? null : candidate.Relationships;
        if (display == null && lifecycle == null && discovery == null && relationships == null)
            return null;

        long revision = Math.Max(coreRevision, previous.Revision + 1);
        var current = candidate with { Revision = revision };
        projectedJobs[summary.JobId] = current;
        return new JobDeltaDto(
            summary.JobId,
            revision,
            Display: display,
            Lifecycle: lifecycle,
            Discovery: discovery,
            Relationships: relationships);
    }

    private WorkflowStateDto? ProjectWorkflow(WorkflowSummaryDto summary)
    {
        if (!projectedWorkflows.TryGetValue(summary.WorkflowId, out var previous))
        {
            var added = new WorkflowStateDto(1, summary);
            projectedWorkflows[summary.WorkflowId] = added;
            return added;
        }

        if (WorkflowSummaryEquals(previous.Summary, summary))
            return null;

        var current = new WorkflowStateDto(previous.Revision + 1, summary);
        projectedWorkflows[summary.WorkflowId] = current;
        return current;
    }

    private TransferDeltaDto UpsertTransfer(TransferSnapshot transfer, bool isTerminal)
    {
        transferWorkflowIds[transfer.Id] = transfer.WorkflowId ?? Guid.Empty;
        var current = ToTransferState(transfer, isTerminal);
        if (!activeTransfers.TryGetValue(transfer.Id, out var previous))
        {
            if (!isTerminal)
                activeTransfers[transfer.Id] = current;
            return new TransferDeltaDto(transfer.Id, current.Revision, Added: current);
        }

        var status = TransferStatusEquals(previous.Status, current.Status)
            ? null
            : current.Status;
        var progress = previous.Progress == current.Progress ? null : current.Progress;
        var scheduling = previous.Scheduling == current.Scheduling
            ? null
            : current.Scheduling;
        if (!isTerminal)
            activeTransfers[transfer.Id] = current;
        return new TransferDeltaDto(
            transfer.Id,
            Math.Max(current.Revision, previous.Revision + 1),
            Status: status,
            Progress: progress,
            Scheduling: scheduling);
    }

    private IReadOnlyList<StateUpdateBatchDto> CreateStateBatches(
        StateDeltaDto daemonDelta,
        IReadOnlyDictionary<Guid, StateDeltaDto> workflowDeltas,
        DateTimeOffset occurredAtUtc)
    {
        var batches = new List<StateUpdateBatchDto>();
        if (!daemonDelta.IsEmpty)
        {
            long previous = daemonStreamSequence;
            long sequence = ++daemonStreamSequence;
            batches.Add(new StateUpdateBatchDto(
                StateStreamScopeDto.Daemon,
                streamEpoch,
                previous,
                sequence,
                occurredAtUtc,
                daemonDelta,
                []));
        }

        foreach (var pair in workflowDeltas.OrderBy(pair => pair.Key))
        {
            if (pair.Value.IsEmpty)
                continue;

            long previous = workflowStreamSequences.GetValueOrDefault(pair.Key);
            long sequence = previous + 1;
            workflowStreamSequences[pair.Key] = sequence;
            batches.Add(new StateUpdateBatchDto(
                StateStreamScopeDto.Workflow(pair.Key),
                GetOrCreateWorkflowStreamEpoch(pair.Key),
                previous,
                sequence,
                occurredAtUtc,
                pair.Value,
                []));
        }

        return batches;
    }

    private Guid GetOrCreateWorkflowStreamEpoch(Guid workflowId)
    {
        if (!workflowStreamEpochs.TryGetValue(workflowId, out Guid epoch))
        {
            epoch = Guid.NewGuid();
            workflowStreamEpochs[workflowId] = epoch;
        }
        return epoch;
    }

    private Guid GetWorkflowSnapshotEpoch(Guid workflowId)
    {
        if (workflowStreamEpochs.TryGetValue(workflowId, out Guid epoch))
            return epoch;

        return workflows.ContainsKey(workflowId)
               || workflowStreamReservations.ContainsKey(workflowId)
            ? GetOrCreateWorkflowStreamEpoch(workflowId)
            : streamEpoch;
    }

    private IReadOnlyList<StateUpdateBatchDto> CreateActivityBatches(
        string type,
        ActivityPayloadDto payload,
        Guid? workflowId,
        Guid? jobId,
        Guid? transferId,
        DateTimeOffset occurredAtUtc)
    {
        var batches = new List<StateUpdateBatchDto>();
        long daemonPrevious = daemonStreamSequence;
        long daemonSequence = ++daemonStreamSequence;
        batches.Add(new StateUpdateBatchDto(
            StateStreamScopeDto.Daemon,
            streamEpoch,
            daemonPrevious,
            daemonSequence,
            occurredAtUtc,
            StateDeltaDto.Empty,
            [new ActivityEventDto(
                daemonSequence,
                occurredAtUtc,
                type,
                workflowId,
                jobId,
                transferId,
                payload)]));

        if (workflowId is Guid id)
        {
            long workflowPrevious = workflowStreamSequences.GetValueOrDefault(id);
            long workflowSequence = workflowPrevious + 1;
            workflowStreamSequences[id] = workflowSequence;
            batches.Add(new StateUpdateBatchDto(
                StateStreamScopeDto.Workflow(id),
                GetOrCreateWorkflowStreamEpoch(id),
                workflowPrevious,
                workflowSequence,
                occurredAtUtc,
                StateDeltaDto.Empty,
                [new ActivityEventDto(
                    workflowSequence,
                    occurredAtUtc,
                    type,
                    id,
                    jobId,
                    transferId,
                    payload)]));
        }

        return batches;
    }

    private void PublishStateBatches(IReadOnlyList<StateUpdateBatchDto> batches)
    {
        if (batches.Count == 0)
            return;

        lock (stateBatchPublicationGate)
        {
            foreach (var batch in batches)
            {
                long publishedSequence = publishedStateBatchSequences.GetValueOrDefault(batch.Scope);
                if (batch.Sequence <= publishedSequence)
                    continue;

                if (!pendingStateBatches.TryGetValue(batch.Scope, out var pending))
                {
                    pending = [];
                    pendingStateBatches[batch.Scope] = pending;
                }

                pending[batch.Sequence] = batch;
            }

            if (publishingStateBatches)
                return;

            publishingStateBatches = true;
        }

        try
        {
            while (true)
            {
                StateUpdateBatchDto? batch;
                lock (stateBatchPublicationGate)
                {
                    batch = DequeueNextStateBatch();
                    if (batch == null)
                    {
                        publishingStateBatches = false;
                        return;
                    }
                }

                StateBatchPublished?.Invoke(batch);
            }
        }
        catch
        {
            lock (stateBatchPublicationGate)
                publishingStateBatches = false;
            throw;
        }
    }

    private StateUpdateBatchDto? DequeueNextStateBatch()
    {
        StateStreamScopeDto? readyScope = null;
        StateUpdateBatchDto? readyBatch = null;

        foreach (var (scope, pending) in pendingStateBatches)
        {
            long nextSequence = publishedStateBatchSequences.GetValueOrDefault(scope) + 1;
            if (!pending.TryGetValue(nextSequence, out var batch))
                continue;

            readyScope = scope;
            readyBatch = batch;
            break;
        }

        if (readyScope == null || readyBatch == null)
            return null;

        var readyPending = pendingStateBatches[readyScope];
        readyPending.Remove(readyBatch.Sequence);
        if (readyPending.Count == 0)
            pendingStateBatches.Remove(readyScope);

        publishedStateBatchSequences[readyScope] = readyBatch.Sequence;
        return readyBatch;
    }

    private Guid JobWorkflowId(JobDeltaDto delta)
        => delta.Added?.Display.WorkflowId
            ?? delta.Display?.WorkflowId
            ?? projectedJobs.GetValueOrDefault(delta.JobId)?.Display.WorkflowId
            ?? Guid.Empty;

    private Guid TransferWorkflowId(TransferDeltaDto delta)
        => delta.Added?.Identity.WorkflowId
            ?? transferWorkflowIds.GetValueOrDefault(delta.TransferId);

    private static TransferStateDto ToTransferState(TransferSnapshot transfer, bool isTerminal)
    {
        bool terminal = isTerminal
            || transfer.TerminalOutcome != TransferSnapshotTerminalOutcome.None;
        return new(
            transfer.Id,
            transfer.Revision,
            new TransferIdentityFieldsDto(
                transfer.JobId,
                transfer.WorkflowId,
                transfer.Direction.ToString(),
                transfer.Source.ToString(),
                transfer.Username,
                transfer.RemotePath,
                transfer.CandidateKey),
            new TransferStatusFieldsDto(
                transfer.State ?? "",
                transfer.LocalPath,
                transfer.AttemptCount,
                terminal,
                transfer.TerminalOutcome switch
                {
                    TransferSnapshotTerminalOutcome.Succeeded => TransferTerminalOutcome.Succeeded,
                    TransferSnapshotTerminalOutcome.Cancelled => TransferTerminalOutcome.Cancelled,
                    TransferSnapshotTerminalOutcome.Failed => TransferTerminalOutcome.Failed,
                    TransferSnapshotTerminalOutcome.Interrupted => TransferTerminalOutcome.Interrupted,
                    _ => TransferTerminalOutcome.None,
                },
                transfer.FailureReason switch
                {
                    Sockseek.Core.Events.TransferFailureReason.PeerFailure => Sockseek.Api.TransferFailureReason.ConnectionFailed,
                    Sockseek.Core.Events.TransferFailureReason.Stale => Sockseek.Api.TransferFailureReason.TransferTimedOut,
                    Sockseek.Core.Events.TransferFailureReason.Finalization => Sockseek.Api.TransferFailureReason.Unknown,
                    Sockseek.Core.Events.TransferFailureReason.Unknown => Sockseek.Api.TransferFailureReason.Unknown,
                    _ => Sockseek.Api.TransferFailureReason.None,
                },
                transfer.CancellationReason switch
                {
                    Sockseek.Core.Events.TransferCancellationReason.Requested => TransferCancellationSource.User,
                    Sockseek.Core.Events.TransferCancellationReason.ManualSkip => TransferCancellationSource.User,
                    _ => TransferCancellationSource.None,
                },
                terminal
                    ? []
                    :
                    [
                        new ResourceActionDto(
                            ServerResourceActionKind.Cancel,
                            "POST",
                            $"/api/transfers/{transfer.Id:D}/cancel"),
                    ]),
            new TransferProgressFieldsDto(
                transfer.BytesTransferred,
                transfer.TotalBytes,
                transfer.BytesPerSecond,
                transfer.LastProgressAtUtc),
            transfer.RequestedAtUtc is { } requested
                ? new TransferSchedulingFieldsDto(requested, transfer.StartedAtUtc)
                : null,
            ToFileMetadata(transfer.File));
    }

    private static TransferStateDto ToUploadTransferState(
        UploadTransferSnapshot transfer)
    {
        bool terminal = transfer.State is UploadTransferState.Completed
            or UploadTransferState.Cancelled
            or UploadTransferState.Failed
            or UploadTransferState.Interrupted;
        TransferTerminalOutcome terminalOutcome = transfer.State switch
        {
            UploadTransferState.Completed => TransferTerminalOutcome.Succeeded,
            UploadTransferState.Cancelled => TransferTerminalOutcome.Cancelled,
            UploadTransferState.Failed => TransferTerminalOutcome.Failed,
            UploadTransferState.Interrupted => TransferTerminalOutcome.Interrupted,
            _ => TransferTerminalOutcome.None,
        };
        return new TransferStateDto(
            transfer.TransferId,
            transfer.Revision,
            new TransferIdentityFieldsDto(
                null,
                null,
                "Upload",
                "SoulseekPeer",
                transfer.Username,
                transfer.RemotePath,
                null,
                transfer.GroupRef,
                transfer.GroupDisplayPath),
            new TransferStatusFieldsDto(
                transfer.State.ToString(),
                null,
                transfer.Attempt?.Number ?? 0,
                terminal,
                terminalOutcome,
                transfer.FailureReason switch
                {
                    UploadFailureReason.NotShared => Sockseek.Api.TransferFailureReason.FileNoLongerShared,
                    UploadFailureReason.Unavailable => Sockseek.Api.TransferFailureReason.FileUnavailable,
                    UploadFailureReason.InvalidOffset => Sockseek.Api.TransferFailureReason.InvalidOffset,
                    UploadFailureReason.Denied => Sockseek.Api.TransferFailureReason.Denied,
                    UploadFailureReason.InternalFailure => Sockseek.Api.TransferFailureReason.Unknown,
                    _ => Sockseek.Api.TransferFailureReason.None,
                },
                transfer.CancellationSource switch
                {
                    UploadCancellationSource.User => TransferCancellationSource.User,
                    UploadCancellationSource.Peer => TransferCancellationSource.Peer,
                    UploadCancellationSource.DaemonShutdown => TransferCancellationSource.DaemonShutdown,
                    UploadCancellationSource.CatalogInvalidation => TransferCancellationSource.CatalogInvalidation,
                    _ => TransferCancellationSource.None,
                },
                terminal
                    ? []
                    :
                    [
                        new ResourceActionDto(
                            ServerResourceActionKind.Cancel,
                            "POST",
                            $"/api/transfers/{transfer.TransferId:D}/cancel"),
                    ]),
            new TransferProgressFieldsDto(
                transfer.BytesTransferred,
                transfer.SizeBytes,
                checked((long)Math.Max(0, transfer.BytesPerSecond)),
                transfer.LastProgressAtUtc),
            new TransferSchedulingFieldsDto(
                transfer.RequestedAtUtc,
                transfer.Attempt?.StartedAtUtc),
            ToFileMetadata(transfer.File));
    }

    private static FileMetadataDto? ToFileMetadata(
        TransferFileMetadataSnapshot? file)
        => file is null
            ? null
            : new FileMetadataDto(
                file.Name,
                file.Size,
                file.Extension,
                file.BitRate,
                file.BitDepth,
                file.SampleRate,
                file.Length,
                file.Attributes?.Select(attribute => new FileAttributeDto(
                    attribute.Type,
                    attribute.Value)).ToArray());

    private static TransferAttemptHistoryDto? ToUploadAttempt(UploadTransferSnapshot transfer)
    {
        if (transfer.Attempt is not { } attempt)
            return null;

        bool terminal = transfer.State is UploadTransferState.Completed
            or UploadTransferState.Cancelled
            or UploadTransferState.Failed
            or UploadTransferState.Interrupted;
        string state = terminal
            ? transfer.State switch
            {
                UploadTransferState.Completed => "Completed",
                UploadTransferState.Cancelled => "Cancelled",
                UploadTransferState.Interrupted => "Interrupted",
                _ => "Failed",
            }
            : "Started";
        return new TransferAttemptHistoryDto(
            attempt.AttemptId,
            transfer.TransferId,
            attempt.Number,
            "SoulseekPeer",
            state,
            transfer.Username,
            transfer.RemotePath,
            null,
            attempt.StartedAtUtc,
            terminal
                ? attempt.FinishedAtUtc ?? transfer.FinishedAtUtc
                : null,
            transfer.FailureReason.ToString(),
            null,
            transfer.Revision);
    }

    private static bool JobDisplayEquals(JobDisplayFieldsDto left, JobDisplayFieldsDto right)
        => left.DisplayId == right.DisplayId
            && left.WorkflowId == right.WorkflowId
            && left.Kind == right.Kind
            && left.ItemName == right.ItemName
            && left.QueryText == right.QueryText
            && left.PrintOption == right.PrintOption
            && left.AppliedAutoProfiles.SequenceEqual(right.AppliedAutoProfiles);

    private static bool JobLifecycleEquals(JobLifecycleFieldsDto left, JobLifecycleFieldsDto right)
        => left.LifecycleState == right.LifecycleState
            && left.ActivityPhase == right.ActivityPhase
            && left.ActivityUntilUtc == right.ActivityUntilUtc
            && left.TerminalOutcome == right.TerminalOutcome
            && left.SkipReason == right.SkipReason
            && left.FailureReason == right.FailureReason
            && left.FailureMessage == right.FailureMessage
            && left.FailureDetail == right.FailureDetail
            && left.CancellationSource == right.CancellationSource
            && left.AvailableActions.SequenceEqual(right.AvailableActions);

    private static bool TransferStatusEquals(
        TransferStatusFieldsDto left,
        TransferStatusFieldsDto right)
        => left.State == right.State
            && left.LocalPath == right.LocalPath
            && left.AttemptCount == right.AttemptCount
            && left.IsTerminal == right.IsTerminal
            && left.TerminalOutcome == right.TerminalOutcome
            && left.FailureReason == right.FailureReason
            && left.CancellationSource == right.CancellationSource
            && left.AvailableActions.SequenceEqual(right.AvailableActions);

    private static bool WorkflowSummaryEquals(WorkflowSummaryDto left, WorkflowSummaryDto right)
        => left.WorkflowId == right.WorkflowId
            && left.Title == right.Title
            && left.State == right.State
            && left.ActiveJobCount == right.ActiveJobCount
            && left.FailedJobCount == right.FailedJobCount
            && left.CompletedJobCount == right.CompletedJobCount
            && left.RootJobCount == right.RootJobCount;

    private SearchStateDto? ToSearchState(JobSnapshot job)
        => job.Payload is SearchJobSnapshotPayload search
            ? new SearchStateDto(job.Id, job.WorkflowId, search.Revision, search.ResultCount, search.IsComplete)
            : null;

    private WorkflowSummaryDto BuildWorkflowSummary(Guid workflowId)
        => workflows.TryGetValue(workflowId, out var workflow)
            ? workflow.ToSummary(records)
            : throw new InvalidOperationException($"Workflow {workflowId} is not registered.");

    private static bool IsDefaultRoot(JobRecord record)
        => record.ParentJobId == null;

    private IEnumerable<JobRecord> FilterJobs(
        IEnumerable<JobRecord> source,
        JobQuery query)
    {
        IEnumerable<JobRecord> filtered;
        if (query.ParentJobId is Guid parentJobId)
        {
            filtered = childJobIdsByParent.TryGetValue(parentJobId, out var childIds)
                ? childIds.Select(childId => records[childId])
                : [];
        }
        else
        {
            filtered = source;
        }
        if (query.WorkflowId.HasValue)
            filtered = filtered.Where(record => record.WorkflowId == query.WorkflowId.Value);
        if (query.Kind.HasValue)
            filtered = filtered.Where(record => record.Summary.Kind == query.Kind.Value);
        if (query.LifecycleState.HasValue)
            filtered = filtered.Where(record => record.Summary.LifecycleState == query.LifecycleState.Value);
        if (query.TerminalOutcome.HasValue)
            filtered = filtered.Where(record => record.Summary.TerminalOutcome == query.TerminalOutcome.Value);
        if (query.SkipReason.HasValue)
            filtered = filtered.Where(record => record.Summary.SkipReason == query.SkipReason.Value);
        if (query.SubmissionId.HasValue)
            filtered = filtered.Where(record => record.Summary.SubmissionId == query.SubmissionId.Value);
        if (query.Role.HasValue)
            filtered = filtered.Where(record => record.Summary.Role == query.Role.Value);
        filtered = query.Archived
            ? filtered.Where(record => record.Summary.SubmissionId is Guid id
                && archivedSubmissionIds.Contains(id))
            : filtered.Where(record => record.Summary.SubmissionId is not Guid id
                || !archivedSubmissionIds.Contains(id));
        return filtered.Where(record => query.ParentJobId.HasValue || query.IncludeAll || IsDefaultRoot(record));
    }

    private JobRecord UpdateJobRecord(JobSnapshot job)
    {
        var current = RefreshNestedSnapshots(jobs.GetValueOrDefault(job.Id) ?? job);
        var parentJobId = parentJobIds.GetValueOrDefault(current.Id);
        if (records.TryGetValue(current.Id, out var oldRecord))
        {
            RemoveWorkflowRecord(oldRecord);
            if (oldRecord.ParentJobId != parentJobId)
                RemoveChildIndex(oldRecord.ParentJobId, current.Id);
        }

        if (oldRecord == null || oldRecord.ParentJobId != parentJobId)
            AddChildIndex(parentJobId, current.Id);

        var record = new JobRecord(
            current.Id,
            current.WorkflowId,
            parentJobId,
            BuildJobSummary(current),
            ServerSnapshotMapper.ToJobPayload(current, GetTransferState, CountDescendants));
        records[current.Id] = record;
        AddWorkflowRecord(record);
        return record;
    }

    private void AddChildIndex(Guid? parentJobId, Guid jobId)
    {
        if (parentJobId is not Guid parentId)
            return;
        if (!childJobIdsByParent.TryGetValue(parentId, out var childIds))
            childJobIdsByParent[parentId] = childIds = [];
        childIds.Add(jobId);
    }

    private void RemoveChildIndex(Guid? parentJobId, Guid jobId)
    {
        if (parentJobId is not Guid parentId
            || !childJobIdsByParent.TryGetValue(parentId, out var childIds))
            return;
        childIds.Remove(jobId);
        if (childIds.Count == 0)
            childJobIdsByParent.Remove(parentId);
    }

    private void AddWorkflowRecord(JobRecord record)
    {
        if (!workflows.TryGetValue(record.WorkflowId, out var workflow))
        {
            workflow = new WorkflowStateRecord(record.WorkflowId);
            workflows[record.WorkflowId] = workflow;
        }

        workflow.Add(record);
    }

    private void RemoveWorkflowRecord(JobRecord record)
    {
        if (!workflows.TryGetValue(record.WorkflowId, out var workflow))
            return;

        workflow.Remove(record);
        if (workflow.Count == 0)
            workflows.Remove(record.WorkflowId);
    }

    private List<JobRecord> UpdateRecordsContainingJob(Guid jobId)
        => containerIdsByNestedJob.TryGetValue(jobId, out HashSet<Guid>? containerIds)
            ? containerIds.Select(id => UpdateJobRecord(jobs[id])).ToList()
            : [];

    private void StoreJob(JobSnapshot job)
    {
        if (nestedJobIdsByContainer.Remove(job.Id, out HashSet<Guid>? previousIds))
        {
            foreach (Guid nestedId in previousIds)
            {
                HashSet<Guid> containers = containerIdsByNestedJob[nestedId];
                containers.Remove(job.Id);
                if (containers.Count == 0)
                    containerIdsByNestedJob.Remove(nestedId);
            }
        }

        HashSet<Guid> nestedIds = ServerSnapshotMapper.NestedJobIds(job)
            .Where(id => id != job.Id)
            .ToHashSet();
        if (nestedIds.Count > 0)
        {
            nestedJobIdsByContainer[job.Id] = nestedIds;
            foreach (Guid nestedId in nestedIds)
            {
                if (!containerIdsByNestedJob.TryGetValue(nestedId, out HashSet<Guid>? containers))
                    containerIdsByNestedJob[nestedId] = containers = [];
                containers.Add(job.Id);
            }
        }

        jobs[job.Id] = job;
    }

    private JobSnapshot RefreshNestedSnapshots(JobSnapshot job)
    {
        if (jobs.TryGetValue(job.Id, out var current) && !ReferenceEquals(current, job))
            job = current;

        return job.Payload switch
        {
            AlbumJobSnapshotPayload album => RefreshAlbumPayload(job, album),
            RemoteDirectoryJobSnapshotPayload directory => RefreshRemoteDirectoryPayload(job, directory),
            AggregateJobSnapshotPayload aggregate => job with
            {
                Payload = aggregate with
                {
                    Songs = aggregate.Songs.Select(RefreshNestedSnapshots).ToList(),
                },
            },
            JobListSnapshotPayload list => job with
            {
                Payload = list with
                {
                    Jobs = list.Jobs.Select(RefreshNestedSnapshots).ToList(),
                },
            },
            _ => job,
        };
    }

    private JobSnapshot RefreshAlbumPayload(JobSnapshot job, AlbumJobSnapshotPayload album)
    {
        var children = album.TrackJobs.Select(RefreshNestedSnapshots).ToList();
        return job with
        {
            Payload = album with
            {
                TrackJobs = children,
                Directory = RefreshDirectoryState(album.Directory, children),
            },
        };
    }

    private JobSnapshot RefreshRemoteDirectoryPayload(
        JobSnapshot job,
        RemoteDirectoryJobSnapshotPayload directory)
    {
        var children = directory.FileJobs.Select(RefreshNestedSnapshots).ToList();
        return job with
        {
            Payload = directory with
            {
                FileJobs = children,
                Directory = RefreshDirectoryState(directory.Directory, children),
            },
        };
    }

    private static DirectoryDownloadStateSnapshot RefreshDirectoryState(
        DirectoryDownloadStateSnapshot state,
        IReadOnlyList<JobSnapshot> children)
    {
        long bytes = children.Sum(FileBytesTransferred);
        return state with
        {
            FileCount = children.Count,
            TerminalFileCount = children.Count(child => child.LifecycleState == JobLifecycleState.Terminal),
            SuccessfulFileCount = children.Count(child =>
                child.TerminalOutcome == JobTerminalOutcome.Succeeded
                || child.SkipReason == JobSkipReason.AlreadyExists),
            FailedFileCount = children.Count(child =>
                child.LifecycleState == JobLifecycleState.Terminal
                && child.TerminalOutcome != JobTerminalOutcome.Succeeded
                && child.SkipReason != JobSkipReason.AlreadyExists),
            BytesTransferred = bytes,
        };
    }

    private static long FileBytesTransferred(JobSnapshot child)
        => child.Payload switch
        {
            SongJobSnapshotPayload song => song.File.BytesTransferred,
            RemoteFileJobSnapshotPayload remote => remote.File.BytesTransferred,
            _ => 0,
        };

    private JobSummaryDto BuildJobSummary(JobSnapshot job)
    {
        var parentJobId = parentJobIds.GetValueOrDefault(job.Id);
        Guid? resultJobId = resultJobIds.TryGetValue(job.Id, out var resultId) ? resultId : null;
        Guid? sourceJobId = sourceJobIds.TryGetValue(job.Id, out var sourceId) ? sourceId : null;

        return ServerSnapshotMapper.ToJobSummary(
            job,
            parentJobId,
            resultJobId,
            sourceJobId,
            EffectiveLifecycleState(job),
            EffectiveActivityPhase(job),
            EffectiveActivityUntilUtc(job),
            EffectiveTerminalOutcome(job));
    }

    private JobLifecycleState EffectiveLifecycleState(JobSnapshot job)
        => executionCompletedJobs.Contains(job.Id)
            && ServerSnapshotMapper.IsRunningOrPending(job)
                ? JobLifecycleState.Terminal
                : job.LifecycleState;

    private JobActivityPhase EffectiveActivityPhase(JobSnapshot job)
        => EffectiveLifecycleState(job) == JobLifecycleState.Terminal
            ? JobActivityPhase.None
            : job.ActivityPhase;

    private DateTimeOffset? EffectiveActivityUntilUtc(JobSnapshot job)
        => EffectiveActivityPhase(job) == JobActivityPhase.None
            ? null
            : job.ActivityUntilUtc;

    private JobTerminalOutcome EffectiveTerminalOutcome(JobSnapshot job)
        => executionCompletedJobs.Contains(job.Id)
            && ServerSnapshotMapper.IsRunningOrPending(job)
                ? JobTerminalOutcome.Succeeded
                : job.TerminalOutcome;

    private string? GetTransferState(Guid jobId)
        => songTransferStates.TryGetValue(jobId, out var state) ? state : null;

    private int CountDescendants(Guid parentId, ServerJobKind? kind = null)
    {
        if (!childJobIdsByParent.TryGetValue(parentId, out var childIds))
            return 0;

        int count = 0;
        foreach (Guid childId in childIds)
        {
            if (!records.TryGetValue(childId, out var child))
                continue;
            if (kind == null || child.Summary.Kind == kind)
                count++;
            count += CountDescendants(childId, kind);
        }
        return count;
    }

    private static bool IsActiveRecord(JobRecord record)
        => record.Summary.LifecycleState != ServerJobLifecycleState.Terminal;

    private static bool IsFailedRecord(JobRecord record)
        => record.Summary.TerminalOutcome is ServerJobTerminalOutcome.Failed
            or ServerJobTerminalOutcome.Cancelled
            or ServerJobTerminalOutcome.PartialSuccess
            || (record.Summary.TerminalOutcome == ServerJobTerminalOutcome.Skipped
                && record.Summary.SkipReason != ServerJobSkipReason.AlreadyExists);

    public static ServerJobLifecycleState ToServerJobLifecycleState(JobLifecycleState state)
        => ServerSnapshotMapper.ToServerJobLifecycleState(state);

    public static ServerJobActivityPhase ToServerJobActivityPhase(JobActivityPhase phase)
        => ServerSnapshotMapper.ToServerJobActivityPhase(phase);

    public static ServerJobTerminalOutcome ToServerJobTerminalOutcome(JobTerminalOutcome outcome)
        => ServerSnapshotMapper.ToServerJobTerminalOutcome(outcome);

    public static ServerSongDownloadSource ToServerSongDownloadSource(SongDownloadSource source)
        => ServerSnapshotMapper.ToServerSongDownloadSource(source);

    public static ServerJobSkipReason ToServerJobSkipReason(JobSkipReason reason)
        => ServerSnapshotMapper.ToServerJobSkipReason(reason);

    public static ServerJobFailureReason? ToServerFailureReason(JobFailureReason reason)
        => ServerSnapshotMapper.ToServerFailureReason(reason);

    public static ServerJobCancellationSource ToServerJobCancellationSource(JobCancellationSource source)
        => ServerSnapshotMapper.ToServerJobCancellationSource(source);

    public static ServerFolderRetrievalOutcome ToServerFolderRetrievalOutcome(FolderRetrievalOutcome outcome)
        => ServerSnapshotMapper.ToServerFolderRetrievalOutcome(outcome);

    private sealed record JobRecord(
        Guid Id,
        Guid WorkflowId,
        Guid? ParentJobId,
        JobSummaryDto Summary,
        JobPayloadDto Payload);

    private readonly record struct WorkflowRecordRef(int DisplayId, Guid JobId);

    private sealed class WorkflowStateRecord(Guid workflowId)
    {
        private static readonly IComparer<WorkflowRecordRef> RecordRefComparer =
            Comparer<WorkflowRecordRef>.Create((x, y) =>
            {
                int displayIdComparison = x.DisplayId.CompareTo(y.DisplayId);
                return displayIdComparison != 0
                    ? displayIdComparison
                    : x.JobId.CompareTo(y.JobId);
            });

        private readonly SortedSet<WorkflowRecordRef> allJobs = new(RecordRefComparer);
        private readonly SortedSet<WorkflowRecordRef> rootJobs = new(RecordRefComparer);
        private readonly SortedSet<WorkflowRecordRef> itemNameJobs = new(RecordRefComparer);

        public Guid WorkflowId => workflowId;
        public int Count => allJobs.Count;
        public int ActiveJobCount { get; private set; }
        public int FailedJobCount { get; private set; }
        public int CompletedJobCount { get; private set; }
        public int FirstDisplayId => allJobs.Count == 0 ? int.MaxValue : allJobs.Min.DisplayId;

        public void Add(JobRecord record)
        {
            var key = ToRef(record);
            if (!allJobs.Add(key))
                return;

            if (IsDefaultRoot(record))
                rootJobs.Add(key);

            if (!string.IsNullOrWhiteSpace(record.Summary.ItemName))
                itemNameJobs.Add(key);

            if (IsActiveRecord(record))
                ActiveJobCount++;
            else
                CompletedJobCount++;

            if (IsFailedRecord(record))
                FailedJobCount++;
        }

        public void Remove(JobRecord record)
        {
            var key = ToRef(record);
            if (!allJobs.Remove(key))
                return;

            if (IsDefaultRoot(record))
                rootJobs.Remove(key);

            if (!string.IsNullOrWhiteSpace(record.Summary.ItemName))
                itemNameJobs.Remove(key);

            if (IsActiveRecord(record))
                ActiveJobCount--;
            else
                CompletedJobCount--;

            if (IsFailedRecord(record))
                FailedJobCount--;
        }

        public WorkflowSummaryDto ToSummary(IReadOnlyDictionary<Guid, JobRecord> records)
        {
            var firstRecord = records[allJobs.Min.JobId];
            string? itemNameTitle = itemNameJobs.Count > 0
                ? records[itemNameJobs.Min.JobId].Summary.ItemName
                : null;

            string title = itemNameTitle
                ?? firstRecord.Summary.QueryText
                ?? firstRecord.Summary.Kind.ToWireString();

            var state = ActiveJobCount > 0 ? ServerWorkflowState.Active
                : FailedJobCount > 0 ? ServerWorkflowState.Failed
                : ServerWorkflowState.Completed;

            return new WorkflowSummaryDto(
                workflowId,
                title,
                state,
                rootJobs.Count,
                ActiveJobCount,
                FailedJobCount,
                CompletedJobCount);
        }

        private static WorkflowRecordRef ToRef(JobRecord record)
            => new(record.Summary.DisplayId, record.Id);
    }
}

internal sealed record EngineStateStoreRetainedWorkflowCounts(
    int Jobs,
    int Records,
    int Workflows,
    int ParentLinks,
    int ChildIndexes,
    int NestedIndexes,
    int ContainerIndexes,
    int ResultLinks,
    int SourceLinks,
    int ExecutionMarkers,
    int SongTransferStates,
    int Transfers,
    int Attempts,
    int TransferWorkflowLinks,
    int Searches,
    int ProjectedJobs,
    int ProjectedWorkflows,
    int WorkflowStreamSequences,
    int WorkflowStreamEpochs,
    int WorkflowStreamReservations,
    int DaemonWorkflowLinks);
