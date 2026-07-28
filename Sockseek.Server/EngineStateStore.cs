using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Snapshots;
using Soulseek;

namespace Sockseek.Server;

public sealed class EngineStateStore
{
    private readonly Lock gate = new();
    // Keep records and workflow aggregate indexes in sync only through UpdateJobRecord.
    private readonly Dictionary<Guid, JobSnapshot> jobs = [];
    private readonly Dictionary<Guid, JobRecord> records = [];
    private readonly Dictionary<Guid, WorkflowStateRecord> workflows = [];
    private readonly Dictionary<Guid, Guid?> parentJobIds = [];
    private readonly Dictionary<Guid, Guid> resultJobIds = [];
    private readonly Dictionary<Guid, Guid> sourceJobIds = [];
    private readonly HashSet<Guid> executionCompletedJobs = [];
    private readonly Dictionary<Guid, string> songTransferStates = [];
    private readonly Dictionary<Guid, TransferStateDto> activeTransfers = [];
    private readonly Dictionary<Guid, Guid> transferWorkflowIds = [];
    private readonly Dictionary<Guid, SearchStateDto> searchStates = [];
    private readonly Dictionary<Guid, JobStateDto> projectedJobs = [];
    private readonly Dictionary<Guid, WorkflowStateDto> projectedWorkflows = [];
    private readonly Dictionary<Guid, long> workflowStreamSequences = [];
    private readonly HashSet<Guid> daemonLiveWorkflowIds = [];
    private readonly Guid streamEpoch = Guid.NewGuid();
    private long daemonStreamSequence;
    private DaemonStateDto daemonState = new(
        0,
        new SoulseekClientStatusDto("None", [], false),
        0,
        null);

    public event Action<JobSummaryDto>? JobUpserted;
    public event Action<WorkflowSummaryDto>? WorkflowUpserted;
    public event Action<SearchStateDto>? SearchUpdated;
    public event Action<StateUpdateBatchDto>? StateBatchPublished;

    public void AttachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered += OnJobRegistered;
        engine.Events.JobResultCreated += OnJobResultCreated;
        engine.Events.JobStateChanged += OnJobStateChanged;
        engine.Events.JobDiscoveryChanged += OnJobDiscoveryChanged;
        engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
        engine.Events.DownloadStarted += OnNestedSongDownloadStarted;
        engine.Events.FallbackTransferStarted += OnFallbackTransferStarted;
        engine.Events.DownloadProgress += OnDownloadProgress;
        engine.Events.DownloadStateChanged += OnDownloadStateChanged;
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
        engine.Events.DownloadStarted -= OnNestedSongDownloadStarted;
        engine.Events.FallbackTransferStarted -= OnFallbackTransferStarted;
        engine.Events.DownloadProgress -= OnDownloadProgress;
        engine.Events.DownloadStateChanged -= OnDownloadStateChanged;
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

            var children = records.Values
                .Where(candidate => candidate.ParentJobId == jobId)
                .OrderBy(candidate => candidate.Summary.DisplayId)
                .ToList();

            JobPayloadDto payload = record.Payload;
            if (payload is AlbumJobPayloadDto albumPayload)
            {
                var tracks = children
                    .Select(child => jobs.TryGetValue(child.Id, out var childJob) ? childJob : null)
                    .OfType<JobSnapshot>()
                    .Where(childJob => childJob.Kind == JobSnapshotKind.Song)
                    .OrderBy(childJob => childJob.DisplayId)
                    .Select(song => ServerSnapshotMapper.ToSongJobPayloadDto(song, GetTransferState(song.Id)))
                    .ToList();
                if (tracks.Count > 0)
                    payload = albumPayload with { Tracks = tracks };
            }

            return new JobDetailDto(record.Summary, payload, children.Select(c => c.Summary).ToList());
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        lock (gate)
        {
            IEnumerable<JobRecord> filtered = records.Values;

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

            return filtered
                .OrderBy(record => record.Summary.DisplayId)
                .Where(record => query.IncludeAll || IsDefaultRoot(record))
                .Select(record => record.Summary)
                .ToList();
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

    public WorkflowSummaryDto? GetWorkflowSummary(Guid workflowId)
    {
        lock (gate)
        {
            return workflows.TryGetValue(workflowId, out var workflow)
                ? workflow.ToSummary(records)
                : null;
        }
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId, bool includeAll = false)
    {
        lock (gate)
        {
            if (!workflows.TryGetValue(workflowId, out var workflow))
                return null;

            var workflowJobs = records.Values
                .Where(record => record.WorkflowId == workflowId)
                .OrderBy(record => record.Summary.DisplayId)
                .ToList();

            if (workflowJobs.Count == 0)
                return null;

            var summary = workflow.ToSummary(records);
            var jobSummaries = workflowJobs
                .Where(record => includeAll || IsDefaultRoot(record))
                .Select(record => record.Summary)
                .ToList();
            return new WorkflowDetailDto(summary, jobSummaries);
        }
    }

    public WorkflowTreeDto? GetWorkflowTree(Guid workflowId)
    {
        lock (gate)
        {
            if (!workflows.TryGetValue(workflowId, out var workflow))
                return null;

            var workflowJobs = records.Values
                .Where(record => record.WorkflowId == workflowId)
                .ToList();

            if (workflowJobs.Count == 0)
                return null;

            var summary = workflow.ToSummary(records);
            return new WorkflowTreeDto(summary, BuildWorkflowJobTree(workflowJobs));
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
                    .Where(transfer => workflowIds.Contains(transfer.Identity.WorkflowId))
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
                    streamEpoch,
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
                searchRateLimitResetsAtUtc);
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
                jobs[failedJob.Id] = failedJob;
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
            jobs[change.Job.Id] = change.Job;
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
            jobs[change.ExtractJob.Id] = change.ExtractJob;
            resultJobIds[change.ExtractJob.Id] = change.ResultJob.Id;

            changedJobs.Add(UpdateJobRecord(change.ExtractJob).Summary);

            if (jobs.ContainsKey(change.ResultJob.Id))
            {
                jobs[change.ResultJob.Id] = change.ResultJob;
                changedJobs.Add(UpdateJobRecord(change.ResultJob).Summary);
            }
            else if (change.ExtractJob.Payload is ExtractJobSnapshotPayload { AutoProcessResult: true })
            {
                jobs[change.ResultJob.Id] = change.ResultJob;
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

            jobs[change.Job.Id] = change.Job;
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
            jobs[change.Job.Id] = change.Job;
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
            jobs[change.Job.Id] = change.Job;
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

    private void OnDownloadStateChanged(DownloadStateChangedChange change)
    {
        IReadOnlyList<StateUpdateBatchDto> batches;
        lock (gate)
        {
            songTransferStates[change.Song.Id] = change.State;
            jobs[change.Song.Id] = change.Song;
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
            jobs[change.Song.Id] = change.Song;
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
            .Where(transfer => terminalWorkflowIds.Contains(transfer.Identity.WorkflowId))
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
        transferWorkflowIds[transfer.Id] = transfer.WorkflowId;
        var current = ToTransferState(transfer, isTerminal);
        if (!activeTransfers.TryGetValue(transfer.Id, out var previous))
        {
            if (!isTerminal)
                activeTransfers[transfer.Id] = current;
            return new TransferDeltaDto(transfer.Id, current.Revision, Added: current);
        }

        var status = previous.Status == current.Status ? null : current.Status;
        var progress = previous.Progress == current.Progress ? null : current.Progress;
        if (!isTerminal)
            activeTransfers[transfer.Id] = current;
        return new TransferDeltaDto(
            transfer.Id,
            Math.Max(current.Revision, previous.Revision + 1),
            Status: status,
            Progress: progress);
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
                streamEpoch,
                previous,
                sequence,
                occurredAtUtc,
                pair.Value,
                []));
        }

        return batches;
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
                streamEpoch,
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
        foreach (var batch in batches)
            StateBatchPublished?.Invoke(batch);
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
        => new(
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
                isTerminal),
            new TransferProgressFieldsDto(
                transfer.BytesTransferred,
                transfer.TotalBytes));

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

    private static bool WorkflowSummaryEquals(WorkflowSummaryDto left, WorkflowSummaryDto right)
        => left.WorkflowId == right.WorkflowId
            && left.Title == right.Title
            && left.State == right.State
            && left.ActiveJobCount == right.ActiveJobCount
            && left.FailedJobCount == right.FailedJobCount
            && left.CompletedJobCount == right.CompletedJobCount
            && left.RootJobIds.SequenceEqual(right.RootJobIds);

    private SearchStateDto? ToSearchState(JobSnapshot job)
        => job.Payload is SearchJobSnapshotPayload search
            ? new SearchStateDto(job.Id, job.WorkflowId, search.Revision, search.ResultCount, search.IsComplete)
            : null;

    private WorkflowSummaryDto BuildWorkflowSummary(Guid workflowId)
        => workflows.TryGetValue(workflowId, out var workflow)
            ? workflow.ToSummary(records)
            : throw new InvalidOperationException($"Workflow {workflowId} is not registered.");

    private static List<WorkflowJobNodeDto> BuildWorkflowJobTree(IReadOnlyList<JobRecord> sourceRecords)
    {
        var visibleRecords = sourceRecords
            .OrderBy(record => record.Summary.DisplayId)
            .ToList();

        var visibleIds = visibleRecords.Select(record => record.Id).ToHashSet();
        var childrenByParentId = new Dictionary<Guid, List<JobRecord>>();
        var roots = new List<JobRecord>();

        foreach (var record in visibleRecords)
        {
            if (record.ParentJobId is Guid parentId && visibleIds.Contains(parentId))
            {
                if (!childrenByParentId.TryGetValue(parentId, out var children))
                {
                    children = [];
                    childrenByParentId[parentId] = children;
                }

                children.Add(record);
            }
            else
            {
                roots.Add(record);
            }
        }

        return roots
            .Select(root => BuildWorkflowJobNode(root, childrenByParentId, []))
            .ToList();
    }

    private static WorkflowJobNodeDto BuildWorkflowJobNode(
        JobRecord record,
        IReadOnlyDictionary<Guid, List<JobRecord>> childrenByParentId,
        HashSet<Guid> visited)
    {
        if (!visited.Add(record.Id))
            return new WorkflowJobNodeDto(record.Summary, []);

        var children = childrenByParentId.TryGetValue(record.Id, out var childRecords)
            ? childRecords
                .Select(child => BuildWorkflowJobNode(child, childrenByParentId, visited))
                .ToList()
            : [];

        visited.Remove(record.Id);
        return new WorkflowJobNodeDto(record.Summary, children);
    }

    private static bool IsDefaultRoot(JobRecord record)
        => record.ParentJobId == null;

    private JobRecord UpdateJobRecord(JobSnapshot job)
    {
        var current = RefreshNestedSnapshots(jobs.GetValueOrDefault(job.Id) ?? job);
        var parentJobId = parentJobIds.GetValueOrDefault(current.Id);
        if (records.TryGetValue(current.Id, out var oldRecord))
            RemoveWorkflowRecord(oldRecord);

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
        => jobs.Values
            .Where(job => job.Id != jobId && ServerSnapshotMapper.ContainsNestedJob(job, jobId))
            .Select(UpdateJobRecord)
            .ToList();

    private JobSnapshot RefreshNestedSnapshots(JobSnapshot job)
    {
        if (jobs.TryGetValue(job.Id, out var current) && !ReferenceEquals(current, job))
            job = current;

        return job.Payload switch
        {
            AlbumJobSnapshotPayload album => job with
            {
                Payload = album with
                {
                    TrackJobs = album.TrackJobs.Select(RefreshNestedSnapshots).ToList(),
                },
            },
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
        var children = records.Values
            .Where(record => record.ParentJobId == parentId)
            .ToList();

        int count = children.Count(record => kind == null || record.Summary.Kind == kind);
        foreach (var child in children)
            count += CountDescendants(child.Id, kind);

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
                rootJobs.Select(root => root.JobId).ToList(),
                ActiveJobCount,
                FailedJobCount,
                CompletedJobCount);
        }

        private static WorkflowRecordRef ToRef(JobRecord record)
            => new(record.Summary.DisplayId, record.Id);
    }
}
