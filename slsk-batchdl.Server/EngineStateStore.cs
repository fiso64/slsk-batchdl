using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;

namespace Sldl.Server;

public sealed class EngineStateStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private readonly Dictionary<Guid, JobRecord> records = [];
    private readonly Dictionary<Guid, Guid?> parentJobIds = [];
    private readonly Dictionary<Guid, Guid> resultJobIds = [];
    private readonly Dictionary<Guid, Guid> visualParentJobIds = [];
    private readonly HashSet<Guid> infrastructureFailedJobs = [];
    private readonly HashSet<Guid> executionCompletedJobs = [];

    public event Action<JobSummaryDto>? JobUpserted;
    public event Action<WorkflowSummaryDto>? WorkflowUpserted;
    public event Action<SearchUpdatedDto>? SearchUpdated;

    public void AttachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered += OnJobRegistered;
        engine.Events.JobResultCreated += OnJobResultCreated;
        engine.Events.JobStateChanged += OnJobStateChanged;
        engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
        engine.Events.SongSearching += OnNestedSongChanged;
        engine.Events.SearchCompleted += OnNestedSongSearchCompleted;
        engine.Events.SongNotFound += OnNestedSongChanged;
        engine.Events.SongFailed += OnNestedSongChanged;
        engine.Events.StateChanged += OnNestedSongChanged;
        engine.Events.DownloadStarted += OnNestedSongDownloadStarted;
    }

    public void DetachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered -= OnJobRegistered;
        engine.Events.JobResultCreated -= OnJobResultCreated;
        engine.Events.JobStateChanged -= OnJobStateChanged;
        engine.Events.JobExecutionCompleted -= OnJobExecutionCompleted;
        engine.Events.SongSearching -= OnNestedSongChanged;
        engine.Events.SearchCompleted -= OnNestedSongSearchCompleted;
        engine.Events.SongNotFound -= OnNestedSongChanged;
        engine.Events.SongFailed -= OnNestedSongChanged;
        engine.Events.StateChanged -= OnNestedSongChanged;
        engine.Events.DownloadStarted -= OnNestedSongDownloadStarted;
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

    public TJob? GetJob<TJob>(Guid jobId)
        where TJob : Job
    {
        lock (gate)
            return jobs.TryGetValue(jobId, out var job) ? job as TJob : null;
    }

    public JobDetailDto? GetJobDetail(Guid jobId)
    {
        lock (gate)
        {
            if (!records.TryGetValue(jobId, out var record))
                return null;

            var children = records.Values
                .Where(candidate => candidate.ParentJobId == jobId)
                .OrderBy(candidate => candidate.Summary.DisplayId)
                .Select(candidate => candidate.Summary)
                .ToList();

            return new JobDetailDto(record.Summary, record.Payload, children);
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        lock (gate)
        {
            IEnumerable<JobRecord> filtered = records.Values;

            if (query.WorkflowId.HasValue)
                filtered = filtered.Where(record => record.WorkflowId == query.WorkflowId.Value);

            if (!string.IsNullOrWhiteSpace(query.Kind))
                filtered = filtered.Where(record => string.Equals(record.Summary.Kind, query.Kind, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(query.State))
                filtered = filtered.Where(record => string.Equals(record.Summary.State, query.State, StringComparison.OrdinalIgnoreCase));

            if (query.CanonicalRootsOnly)
                filtered = filtered.Where(record => record.ParentJobId == null);

            var summaries = filtered
                .OrderBy(record => record.Summary.DisplayId)
                .Select(record => record.Summary)
                .Where(summary => query.IncludeNonDefault || IsListedByDefault(summary))
                .ToList();

            return summaries;
        }
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        lock (gate)
        {
            return records.Values
                .GroupBy(record => record.WorkflowId)
                .OrderBy(group => group.Min(record => record.Summary.DisplayId))
                .Select(BuildWorkflowSummary)
                .ToList();
        }
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId)
    {
        lock (gate)
        {
            var workflowJobs = records.Values
                .Where(record => record.WorkflowId == workflowId)
                .OrderBy(record => record.Summary.DisplayId)
                .ToList();

            if (workflowJobs.Count == 0)
                return null;

            var summary = BuildWorkflowSummary(workflowJobs.GroupBy(record => record.WorkflowId).Single());
            var jobSummaries = workflowJobs
                .Select(record => record.Summary)
                .ToList();
            return new WorkflowDetailDto(summary, jobSummaries);
        }
    }

    public PresentedWorkflowDto? GetPresentedWorkflow(Guid workflowId)
    {
        lock (gate)
        {
            var workflowJobs = records.Values
                .Where(record => record.WorkflowId == workflowId)
                .ToList();

            if (workflowJobs.Count == 0)
                return null;

            var summary = BuildWorkflowSummary(workflowJobs.GroupBy(record => record.WorkflowId).Single());
            return new PresentedWorkflowDto(summary, BuildPresentedJobTree(workflowJobs));
        }
    }

    public ServerStatusDto GetStatistics()
    {
        lock (gate)
        {
            int totalJobCount = records.Count;
            int activeJobCount = records.Values.Count(IsActiveRecord);
            int totalWorkflowCount = records.Values.Select(record => record.WorkflowId).Distinct().Count();
            int activeWorkflowCount = records.Values
                .Where(IsActiveRecord)
                .Select(record => record.WorkflowId)
                .Distinct()
                .Count();

            return new ServerStatusDto(
                new SoulseekClientStatusDto("None", [], false),
                totalJobCount,
                activeJobCount,
                totalWorkflowCount,
                activeWorkflowCount,
                0);
        }
    }

    public void MarkActiveJobsInfrastructureFailed(string reason)
    {
        List<JobSummaryDto> changedJobs;
        List<WorkflowSummaryDto> changedWorkflows;
        lock (gate)
        {
            foreach (var job in jobs.Values.Where(IsActiveJob))
            {
                job.State = JobState.Failed;
                job.FailureReason = FailureReason.Other;
                job.FailureMessage = "Infrastructure failure: " + reason;
                infrastructureFailedJobs.Add(job.Id);
                UpdateJobRecord(job);
            }

            changedJobs = records.Values
                .Where(record => infrastructureFailedJobs.Contains(record.Id))
                .OrderBy(record => record.Summary.DisplayId)
                .Select(record => record.Summary)
                .ToList();
            changedWorkflows = changedJobs
                .Select(job => job.WorkflowId)
                .Distinct()
                .Select(BuildWorkflowSummary)
                .ToList();
        }

        PublishJobAndWorkflowUpserts(changedJobs, changedWorkflows);
    }

    public static string GetJobKind(Job job) => job switch
    {
        ExtractJob => ServerProtocol.JobKinds.Extract,
        SearchJob => ServerProtocol.JobKinds.Search,
        SongJob => ServerProtocol.JobKinds.Song,
        AlbumJob => ServerProtocol.JobKinds.Album,
        JobList => ServerProtocol.JobKinds.JobList,
        RetrieveFolderJob => ServerProtocol.JobKinds.RetrieveFolder,
        AggregateJob => ServerProtocol.JobKinds.Aggregate,
        AlbumAggregateJob => ServerProtocol.JobKinds.AlbumAggregate,
        _ => ServerProtocol.JobKinds.Generic,
    };

    public void SetVisualParent(Guid jobId, Guid visualParentJobId)
    {
        lock (gate)
        {
            visualParentJobIds[jobId] = visualParentJobId;
            if (jobs.TryGetValue(jobId, out var job))
                UpdateJobRecord(job);
        }
    }

    private void OnJobRegistered(Job job, Job? parent)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            jobs[job.Id] = job;
            parentJobIds[job.Id] = parent?.Id;
            summary = UpdateJobRecord(job).Summary;
            workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        if (job is SearchJob searchJob)
            SubscribeToSearchJob(searchJob);

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
    }

    private void OnJobResultCreated(ExtractJob job, Job result)
    {
        List<JobSummaryDto> changedJobs = [];
        WorkflowSummaryDto? workflowSummary = null;
        lock (gate)
        {
            resultJobIds[job.Id] = result.Id;

            if (jobs.TryGetValue(job.Id, out var extractJob))
                changedJobs.Add(UpdateJobRecord(extractJob).Summary);
            if (jobs.TryGetValue(result.Id, out var resultJob))
                changedJobs.Add(UpdateJobRecord(resultJob).Summary);
            else if (job.AutoProcessResult)
            {
                jobs[result.Id] = result;
                parentJobIds[result.Id] = parentJobIds.GetValueOrDefault(job.Id);
                changedJobs.Add(UpdateJobRecord(result).Summary);
            }

            if (records.Values.Any(candidate => candidate.WorkflowId == job.WorkflowId))
                workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts(changedJobs, workflowSummary != null ? [workflowSummary] : []);
    }

    private void OnJobStateChanged(Job job, JobState _)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        lock (gate)
        {
            if (!jobs.ContainsKey(job.Id))
            {
                var containingRecords = UpdateRecordsContainingJob(job.Id);
                summaries = containingRecords.Select(record => record.Summary).ToList();
                workflowSummaries = containingRecords
                    .Select(record => record.WorkflowId)
                    .Distinct()
                    .Select(BuildWorkflowSummary)
                    .ToList();
            }
            else
            {
                var record = UpdateJobRecord(job);
                summaries = [record.Summary];
                workflowSummaries = [BuildWorkflowSummary(job.WorkflowId)];
            }
        }

        if (summaries.Count == 0 && workflowSummaries.Count == 0)
            return;

        PublishJobAndWorkflowUpserts(summaries, workflowSummaries);
    }

    private void OnJobExecutionCompleted(Job job)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            if (!jobs.ContainsKey(job.Id))
                return;

            executionCompletedJobs.Add(job.Id);
            summary = UpdateJobRecord(job).Summary;
            workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
    }

    private void OnNestedSongSearchCompleted(SongJob song, int _) => OnNestedSongChanged(song);

    private void OnNestedSongDownloadStarted(SongJob song, FileCandidate _) => OnNestedSongChanged(song);

    private void OnNestedSongChanged(SongJob song)
    {
        List<JobSummaryDto> summaries;
        List<WorkflowSummaryDto> workflowSummaries;
        lock (gate)
        {
            var containingRecords = UpdateRecordsContainingJob(song.Id);
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

    private void SubscribeToSearchJob(SearchJob searchJob)
    {
        searchJob.Session.RawResultAdded += OnSearchRawResultAdded;
        searchJob.Session.Completed += OnSearchCompleted;
    }

    private void OnSearchRawResultAdded(SearchSession session, SearchRawResult rawResult)
    {
        SearchJob? searchJob;
        lock (gate)
        {
            searchJob = jobs.Values
                .OfType<SearchJob>()
                .FirstOrDefault(job => ReferenceEquals(job.Session, session));

            if (searchJob != null)
                UpdateJobRecord(searchJob);
        }

        if (searchJob == null)
            return;

        SearchUpdated?.Invoke(new SearchUpdatedDto(
            searchJob.Id,
            rawResult.Revision,
            searchJob.ResultCount,
            false));
    }

    private void OnSearchCompleted(SearchSession session)
    {
        SearchJob? searchJob;
        lock (gate)
        {
            searchJob = jobs.Values
                .OfType<SearchJob>()
                .FirstOrDefault(job => ReferenceEquals(job.Session, session));

            if (searchJob != null)
                UpdateJobRecord(searchJob);
        }

        if (searchJob == null)
            return;

        SearchUpdated?.Invoke(new SearchUpdatedDto(
            searchJob.Id,
            searchJob.Revision,
            searchJob.ResultCount,
            searchJob.IsComplete));
    }

    private WorkflowSummaryDto BuildWorkflowSummary(Guid workflowId)
        => BuildWorkflowSummary(records.Values.Where(record => record.WorkflowId == workflowId).GroupBy(record => record.WorkflowId).Single());

    private WorkflowSummaryDto BuildWorkflowSummary(IGrouping<Guid, JobRecord> workflow)
    {
        var workflowJobs = workflow.OrderBy(record => record.Summary.DisplayId).ToList();
        var roots = workflowJobs
            .Where(record => record.ParentJobId == null)
            .OrderBy(record => record.Summary.DisplayId)
            .Select(record => record.Id)
            .ToList();

        string title = workflowJobs.FirstOrDefault(record => !string.IsNullOrWhiteSpace(record.Summary.ItemName))?.Summary.ItemName
            ?? workflowJobs.First().Summary.QueryText
            ?? workflowJobs.First().Summary.Kind;

        int active = workflowJobs.Count(IsActiveRecord);
        int failed = workflowJobs.Count(record => IsState(record, JobState.Failed));
        int completed = workflowJobs.Count(record => !IsActiveRecord(record));

        string state = active > 0 ? "active"
            : failed == workflowJobs.Count ? "failed"
            : "completed";

        return new WorkflowSummaryDto(workflow.Key, title, state, roots, active, failed, completed);
    }

    private static IReadOnlyList<PresentedJobNodeDto> BuildPresentedJobTree(IReadOnlyList<JobRecord> sourceRecords)
    {
        var visibleRecords = sourceRecords
            .Where(record => record.Summary.Presentation.DisplayMode == ServerProtocol.PresentationDisplayModes.Node)
            .OrderBy(record => record.Summary.Presentation.DisplayOrder)
            .ThenBy(record => record.Summary.DisplayId)
            .ToList();

        var visibleIds = visibleRecords.Select(record => record.Id).ToHashSet();
        var childrenByParentId = new Dictionary<Guid, List<JobRecord>>();
        var roots = new List<JobRecord>();

        foreach (var record in visibleRecords)
        {
            var presentationParentId = record.Summary.Presentation.DisplayParentJobId ?? record.ParentJobId;
            if (presentationParentId is Guid parentId && visibleIds.Contains(parentId))
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
            .Select(root => BuildPresentedNode(root, childrenByParentId, []))
            .ToList();
    }

    private static PresentedJobNodeDto BuildPresentedNode(
        JobRecord record,
        IReadOnlyDictionary<Guid, List<JobRecord>> childrenByParentId,
        HashSet<Guid> visited)
    {
        if (!visited.Add(record.Id))
            return new PresentedJobNodeDto(record.Summary, []);

        var children = childrenByParentId.TryGetValue(record.Id, out var childRecords)
            ? childRecords
                .Select(child => BuildPresentedNode(child, childrenByParentId, visited))
                .ToList()
            : [];

        visited.Remove(record.Id);
        return new PresentedJobNodeDto(record.Summary, children);
    }

    private static bool IsListedByDefault(JobSummaryDto summary)
        => summary.Presentation.DisplayMode == ServerProtocol.PresentationDisplayModes.Node
            && (summary.Presentation.DisplayParentJobId == null
                || summary.Presentation.DisplayParentJobId == summary.ParentJobId);

    private JobRecord UpdateJobRecord(Job job)
    {
        var parentJobId = parentJobIds.GetValueOrDefault(job.Id);
        var record = new JobRecord(
            job.Id,
            job.WorkflowId,
            parentJobId,
            BuildJobSummary(job),
            BuildPayload(job));
        records[job.Id] = record;
        return record;
    }

    private IReadOnlyList<JobRecord> UpdateRecordsContainingJob(Guid jobId)
    {
        return jobs.Values
            .Where(job => ContainsNestedJob(job, jobId))
            .Select(UpdateJobRecord)
            .ToList();
    }

    private static bool ContainsNestedJob(Job container, Guid jobId)
        => container switch
        {
            AlbumJob albumJob => albumJob.Results
                .SelectMany(folder => folder.Files)
                .Any(song => song.Id == jobId),
            AggregateJob aggregateJob => aggregateJob.Songs.Any(song => song.Id == jobId),
            JobList jobList => jobList.Jobs.Any(job => job.Id == jobId || ContainsNestedJob(job, jobId)),
            _ => false,
        };

    private JobSummaryDto BuildJobSummary(Job job)
    {
        var parentJobId = parentJobIds.GetValueOrDefault(job.Id);
        Guid? resultJobId = resultJobIds.TryGetValue(job.Id, out var resultId) ? resultId : null;
        Guid? visualParentJobId = visualParentJobIds.TryGetValue(job.Id, out var visualParentId)
            ? visualParentId
            : null;
        var displayParentJobId = visualParentJobId ?? parentJobId;
        string displayMode = job switch
        {
            ExtractJob when job.State == JobState.Done && resultJobId != null => ServerProtocol.PresentationDisplayModes.Replaced,
            SongJob when parentJobId is Guid parentId && IsEmbeddedSongParent(parentId) => ServerProtocol.PresentationDisplayModes.Embedded,
            _ => ServerProtocol.PresentationDisplayModes.Node,
        };

        return new JobSummaryDto(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            GetJobKind(job),
            EffectiveState(job).ToString(),
            job.ItemName,
            job.ToString(noInfo: true),
            job.FailureReason != FailureReason.None ? job.FailureReason.ToString() : null,
            job.FailureMessage,
            parentJobId,
            resultJobId,
            job.Config?.AppliedAutoProfiles?.OrderBy(x => x).ToList() ?? [],
            new PresentationHintsDto(
                displayMode,
                displayParentJobId,
                job.DisplayId,
                resultJobId),
            BuildActions(job));
    }

    private static JobPayloadDto BuildPayload(Job job)
        => job switch
        {
            ExtractJob extractJob => new ExtractJobPayloadDto(
                extractJob.Input,
                extractJob.InputType?.ToString(),
                extractJob.Result?.Id),
            SearchJob searchJob => new SearchJobPayloadDto(
                searchJob.Intent.ToString(),
                ToSongQueryDto(searchJob.Query),
                searchJob.AlbumQuery != null ? ToAlbumQueryDto(searchJob.AlbumQuery) : null,
                searchJob.ResultCount,
                searchJob.Revision,
                searchJob.IsComplete),
            SongJob songJob => ToSongJobPayloadDto(songJob),
            AlbumJob albumJob => new AlbumJobPayloadDto(
                ToAlbumQueryDto(albumJob.Query),
                albumJob.Results.Count,
                albumJob.DownloadPath,
                albumJob.ResolvedTarget?.Username,
                albumJob.ResolvedTarget?.FolderPath,
                albumJob.Results.Select(folder => ToAlbumFolderDto(folder, includeFiles: true)).ToList()),
            AggregateJob aggregateJob => new AggregateJobPayloadDto(
                ToSongQueryDto(aggregateJob.Query),
                aggregateJob.Songs.Select(ToSongJobPayloadDto).ToList()),
            AlbumAggregateJob albumAggregateJob => new AlbumAggregateJobPayloadDto(
                ToAlbumQueryDto(albumAggregateJob.Query)),
            JobList jobList => new JobListPayloadDto(
                jobList.Count,
                jobList.Jobs.OfType<SongJob>().Select(ToSongJobPayloadDto).ToList()),
            RetrieveFolderJob retrieveFolderJob => new RetrieveFolderJobPayloadDto(
                retrieveFolderJob.TargetFolder.FolderPath,
                retrieveFolderJob.TargetFolder.Username,
                retrieveFolderJob.NewFilesFoundCount),
            _ => new GenericJobPayloadDto(job.ToString(noInfo: true))
        };

    private bool IsEmbeddedSongParent(Guid parentJobId)
        => jobs.TryGetValue(parentJobId, out var parent)
            && parent is AlbumJob or AggregateJob;

    private static IReadOnlyList<ResourceActionDto> BuildActions(Job job)
        => IsActiveJobState(job.State) && job.Cts != null && !job.Cts.IsCancellationRequested
            ? [CancelAction(job.Id)]
            : [];

    private static ResourceActionDto CancelAction(Guid jobId)
        => new(ServerProtocol.ResourceActionKinds.Cancel, "POST", $"/api/jobs/{jobId}/cancel");

    private static SongJobPayloadDto ToSongJobPayloadDto(SongJob song)
        => new(
            ToSongQueryDto(song.Query),
            song.Candidates?.Count,
            song.DownloadPath,
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.Response.HasFreeUploadSlot,
            song.ResolvedTarget?.Response.UploadSpeed,
            song.ResolvedTarget?.File.Size,
            song.ResolvedTarget?.File.Extension,
            song.ResolvedTarget?.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList(),
            song.Id,
            song.DisplayId,
            song.Candidates?.Select(ToFileCandidateDto).ToList(),
            song.State.ToString(),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null,
            song.FailureMessage,
            BuildActions(song));

    private static SongQueryDto ToSongQueryDto(SongQuery query) => new(
        query.Artist,
        query.Title,
        query.Album,
        query.URI,
        query.Length,
        query.ArtistMaybeWrong,
        query.IsDirectLink);

    private static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query) => new(
        query.Artist,
        query.Album,
        query.SearchHint,
        query.URI,
        query.ArtistMaybeWrong,
        query.IsDirectLink,
        query.MinTrackCount,
        query.MaxTrackCount);

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate) => new(
        new FileCandidateRefDto(candidate.Username, candidate.Filename),
        candidate.Username,
        candidate.Filename,
        candidate.File.Size,
        candidate.File.BitRate,
        candidate.File.Length,
        candidate.Response.HasFreeUploadSlot,
        candidate.Response.UploadSpeed,
        candidate.File.Extension,
        candidate.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList());

    private static AlbumFolderDto ToAlbumFolderDto(AlbumFolder folder, bool includeFiles)
        => new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            folder.SearchSortedAudioLengths.ToList(),
            folder.SearchRepresentativeAudioFilename,
            folder.HasSearchMetadata,
            includeFiles
                ? folder.Files.Select(ToSongJobPayloadDto).ToList()
                : null);

    private JobState EffectiveState(Job job)
        => executionCompletedJobs.Contains(job.Id) && IsActiveJobState(job.State)
            ? JobState.Done
            : job.State;

    private bool IsActiveJob(Job job)
        => IsActiveJobState(EffectiveState(job));

    private static bool IsActiveRecord(JobRecord record)
        => Enum.TryParse<JobState>(record.Summary.State, out var state) && IsActiveJobState(state);

    private static bool IsState(JobRecord record, JobState state)
        => string.Equals(record.Summary.State, state.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveJobState(JobState state)
        => state is JobState.Pending or JobState.Searching or JobState.Downloading or JobState.Extracting;

    private sealed record JobRecord(
        Guid Id,
        Guid WorkflowId,
        Guid? ParentJobId,
        JobSummaryDto Summary,
        JobPayloadDto Payload);
}
