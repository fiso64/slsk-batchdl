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

    public event Action<JobSummaryDto>? JobUpserted;
    public event Action<WorkflowSummaryDto>? WorkflowUpserted;
    public event Action<SearchUpdatedDto>? SearchUpdated;

    public void AttachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered += OnJobRegistered;
        engine.Events.JobResultCreated += OnJobResultCreated;
        engine.Events.JobStateChanged += OnJobStateChanged;
        engine.Events.JobDiscoveryChanged += OnJobDiscoveryChanged;
        engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
        engine.Events.DownloadStarted += OnNestedSongDownloadStarted;
        engine.Events.DownloadStateChanged += OnDownloadStateChanged;
    }

    public void DetachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered -= OnJobRegistered;
        engine.Events.JobResultCreated -= OnJobResultCreated;
        engine.Events.JobStateChanged -= OnJobStateChanged;
        engine.Events.JobDiscoveryChanged -= OnJobDiscoveryChanged;
        engine.Events.JobExecutionCompleted -= OnJobExecutionCompleted;
        engine.Events.DownloadStarted -= OnNestedSongDownloadStarted;
        engine.Events.DownloadStateChanged -= OnDownloadStateChanged;
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
        }

        PublishJobAndWorkflowUpserts(changedJobs, changedWorkflows);
    }

    public static ServerJobKind GetJobKind(Job job)
        => ServerSnapshotMapper.ToServerJobKind(job);

    public void SetSourceJob(Guid jobId, Guid sourceJobId)
    {
        lock (gate)
        {
            sourceJobIds[jobId] = sourceJobId;
            if (jobs.TryGetValue(jobId, out var job))
                UpdateJobRecord(job);
        }
    }

    private void OnJobRegistered(JobRegisteredChange change)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            jobs[change.Job.Id] = change.Job;
            parentJobIds[change.Job.Id] = change.ParentJobId;
            if (change.SourceJobId is Guid sourceJobId)
                sourceJobIds[change.Job.Id] = sourceJobId;
            summary = UpdateJobRecord(change.Job).Summary;
            workflowSummary = BuildWorkflowSummary(change.Job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
    }

    private void OnJobResultCreated(JobResultCreatedChange change)
    {
        List<JobSummaryDto> changedJobs = [];
        WorkflowSummaryDto? workflowSummary = null;
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
        }

        PublishJobAndWorkflowUpserts(
            changedJobs.DistinctBy(summary => summary.JobId).ToList(),
            workflowSummary != null ? [workflowSummary] : []);
    }

    private void OnJobStateChanged(JobStateChangedChange change)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        SearchUpdatedDto? searchUpdate;
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
            searchUpdate = ToSearchUpdated(change.Job);
        }

        if (summaries.Count > 0 || workflowSummaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, workflowSummaries);

        if (searchUpdate != null)
            SearchUpdated?.Invoke(searchUpdate);
    }

    private void OnJobDiscoveryChanged(JobDiscoveryChangedChange change)
    {
        List<JobSummaryDto> summaries = [];
        SearchUpdatedDto? searchUpdate;
        lock (gate)
        {
            jobs[change.Job.Id] = change.Job;
            var changedRecords = UpdateRecordsContainingJob(change.Job.Id);
            changedRecords.Add(UpdateJobRecord(change.Job));
            summaries.AddRange(changedRecords
                .DistinctBy(record => record.Id)
                .Select(record => record.Summary));
            searchUpdate = ToSearchUpdated(change.Job);
        }

        if (summaries.Count > 0)
            PublishJobAndWorkflowUpserts(summaries, []);

        if (searchUpdate != null)
            SearchUpdated?.Invoke(searchUpdate);
    }

    private void OnJobExecutionCompleted(JobExecutionCompletedChange change)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            jobs[change.Job.Id] = change.Job;
            executionCompletedJobs.Add(change.Job.Id);
            summary = UpdateJobRecord(change.Job).Summary;
            workflowSummary = BuildWorkflowSummary(change.Job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
    }

    private void OnDownloadStateChanged(DownloadStateChangedChange change)
    {
        lock (gate)
        {
            songTransferStates[change.Song.Id] = change.State;
            jobs[change.Song.Id] = change.Song;
            UpdateJobRecord(change.Song);
            UpdateRecordsContainingJob(change.Song.Id);
        }
    }

    private void OnNestedSongDownloadStarted(DownloadStartedChange change)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
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
        }

        if (summaries.Count == 0 && workflowSummaries.Count == 0)
            return;

        PublishJobAndWorkflowUpserts(summaries, workflowSummaries);
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

    private SearchUpdatedDto? ToSearchUpdated(JobSnapshot job)
        => job.Payload is SearchJobSnapshotPayload search
            ? new SearchUpdatedDto(job.Id, job.WorkflowId, search.Revision, search.ResultCount, search.IsComplete)
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
