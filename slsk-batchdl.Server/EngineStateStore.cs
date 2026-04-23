using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;

namespace Sldl.Server;

public sealed class EngineStateStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
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
    }

    public void DetachEngine(DownloadEngine engine)
    {
        engine.Events.JobRegistered -= OnJobRegistered;
        engine.Events.JobResultCreated -= OnJobResultCreated;
        engine.Events.JobStateChanged -= OnJobStateChanged;
        engine.Events.JobExecutionCompleted -= OnJobExecutionCompleted;
    }

    public JobSummaryDto? GetJobSummary(Guid jobId)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var job))
                return null;

            return BuildJobSummary(job);
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
            if (!jobs.TryGetValue(jobId, out var job))
                return null;

            var children = jobs.Values
                .Where(candidate => parentJobIds.GetValueOrDefault(candidate.Id) == jobId)
                .OrderBy(candidate => candidate.DisplayId)
                .Select(BuildJobSummary)
                .ToList();

            return new JobDetailDto(BuildJobSummary(job), BuildPayload(job), children);
        }
    }

    public IReadOnlyList<JobSummaryDto> GetJobs(JobQuery query)
    {
        lock (gate)
        {
            IEnumerable<Job> filtered = jobs.Values;

            if (query.WorkflowId.HasValue)
                filtered = filtered.Where(job => job.WorkflowId == query.WorkflowId.Value);

            if (!string.IsNullOrWhiteSpace(query.Kind))
                filtered = filtered.Where(job => string.Equals(GetJobKind(job), query.Kind, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(query.State))
                filtered = filtered.Where(job => string.Equals(job.State.ToString(), query.State, StringComparison.OrdinalIgnoreCase));

            if (query.RootOnly)
                filtered = filtered.Where(job => parentJobIds.GetValueOrDefault(job.Id) == null);

            var summaries = filtered
                .OrderBy(job => job.DisplayId)
                .Select(BuildJobSummary)
                .Where(summary => query.IncludeHidden || !summary.Presentation.IsHiddenFromRoot)
                .ToList();

            return summaries;
        }
    }

    public IReadOnlyList<WorkflowSummaryDto> GetWorkflows()
    {
        lock (gate)
        {
            return jobs.Values
                .GroupBy(job => job.WorkflowId)
                .OrderBy(group => group.Min(job => job.DisplayId))
                .Select(BuildWorkflowSummary)
                .ToList();
        }
    }

    public WorkflowDetailDto? GetWorkflow(Guid workflowId)
    {
        lock (gate)
        {
            var workflowJobs = jobs.Values
                .Where(job => job.WorkflowId == workflowId)
                .OrderBy(job => job.DisplayId)
                .ToList();

            if (workflowJobs.Count == 0)
                return null;

            var summary = BuildWorkflowSummary(workflowJobs.GroupBy(job => job.WorkflowId).Single());
            var jobSummaries = workflowJobs
                .Select(BuildJobSummary)
                .ToList();
            return new WorkflowDetailDto(summary, jobSummaries);
        }
    }

    public ServerStatusDto GetStatistics()
    {
        lock (gate)
        {
            int totalJobCount = jobs.Count;
            int activeJobCount = jobs.Values.Count(IsActiveJob);
            int totalWorkflowCount = jobs.Values.Select(job => job.WorkflowId).Distinct().Count();
            int activeWorkflowCount = jobs.Values
                .Where(IsActiveJob)
                .Select(job => job.WorkflowId)
                .Distinct()
                .Count();

            return new ServerStatusDto(false, totalJobCount, activeJobCount, totalWorkflowCount, activeWorkflowCount, 0);
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
            }

            changedJobs = jobs.Values
                .Where(job => infrastructureFailedJobs.Contains(job.Id))
                .OrderBy(job => job.DisplayId)
                .Select(BuildJobSummary)
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
        ExtractJob => "extract",
        SearchJob => "search",
        SongJob => "song",
        AlbumJob => "album",
        JobList => "job-list",
        RetrieveFolderJob => "retrieve-folder",
        AggregateJob => "aggregate",
        AlbumAggregateJob => "album-aggregate",
        _ => job.GetType().Name.Replace("Job", "").ToLowerInvariant(),
    };

    public void SetVisualParent(Guid jobId, Guid visualParentJobId)
    {
        lock (gate)
            visualParentJobIds[jobId] = visualParentJobId;
    }

    private void OnJobRegistered(Job job, Job? parent)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            jobs[job.Id] = job;
            parentJobIds[job.Id] = parent?.Id;
            summary = BuildJobSummary(job);
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
                changedJobs.Add(BuildJobSummary(extractJob));
            if (jobs.TryGetValue(result.Id, out var resultJob))
                changedJobs.Add(BuildJobSummary(resultJob));

            if (jobs.Values.Any(candidate => candidate.WorkflowId == job.WorkflowId))
                workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts(changedJobs, workflowSummary != null ? [workflowSummary] : []);
    }

    private void OnJobStateChanged(Job job, JobState _)
    {
        JobSummaryDto summary;
        WorkflowSummaryDto workflowSummary;
        lock (gate)
        {
            if (!jobs.ContainsKey(job.Id))
                return;

            summary = BuildJobSummary(job);
            workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
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
            summary = BuildJobSummary(job);
            workflowSummary = BuildWorkflowSummary(job.WorkflowId);
        }

        PublishJobAndWorkflowUpserts([summary], [workflowSummary]);
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
        => BuildWorkflowSummary(jobs.Values.Where(job => job.WorkflowId == workflowId).GroupBy(job => job.WorkflowId).Single());

    private WorkflowSummaryDto BuildWorkflowSummary(IGrouping<Guid, Job> workflow)
    {
        var workflowJobs = workflow.OrderBy(job => job.DisplayId).ToList();
        var roots = workflowJobs
            .Where(job => parentJobIds.GetValueOrDefault(job.Id) == null)
            .OrderBy(job => job.DisplayId)
            .Select(job => job.Id)
            .ToList();

        string title = workflowJobs.FirstOrDefault(job => !string.IsNullOrWhiteSpace(job.ItemName))?.ItemName
            ?? workflowJobs.First().ToString(noInfo: true);

        int active = workflowJobs.Count(IsActiveJob);
        int failed = workflowJobs.Count(job => EffectiveState(job) == JobState.Failed);
        int completed = workflowJobs.Count(job => !IsActiveJob(job));

        string state = active > 0 ? "active"
            : failed == workflowJobs.Count ? "failed"
            : "completed";

        return new WorkflowSummaryDto(workflow.Key, title, state, roots, active, failed, completed);
    }

    private JobSummaryDto BuildJobSummary(Job job)
    {
        Guid? resultJobId = resultJobIds.TryGetValue(job.Id, out var resultId) ? resultId : null;
        Guid? visualParentJobId = visualParentJobIds.TryGetValue(job.Id, out var visualParentId)
            ? visualParentId
            : null;
        bool isHiddenFromRoot = visualParentJobId != null
            || job is ExtractJob && job.State == JobState.Done && resultJobId != null;

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
            parentJobIds.GetValueOrDefault(job.Id),
            resultJobId,
            job.Config?.AppliedAutoProfiles?.OrderBy(x => x).ToList() ?? [],
            new PresentationHintsDto(
                isHiddenFromRoot,
                visualParentJobId ?? parentJobIds.GetValueOrDefault(job.Id),
                job.DisplayId,
                resultJobId));
    }

    private static object BuildPayload(Job job)
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
            SongJob songJob => new SongJobPayloadDto(
                ToSongQueryDto(songJob.Query),
                songJob.Candidates?.Count,
                songJob.DownloadPath,
                songJob.ResolvedTarget?.Username,
                songJob.ResolvedTarget?.Filename,
                songJob.ResolvedTarget?.Response.HasFreeUploadSlot,
                songJob.ResolvedTarget?.Response.UploadSpeed,
                songJob.ResolvedTarget?.File.Size,
                songJob.ResolvedTarget?.File.Extension,
                songJob.ResolvedTarget?.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList(),
                songJob.Id,
                songJob.DisplayId,
                songJob.Candidates?.Select(ToFileCandidateDto).ToList(),
                songJob.State.ToString(),
                songJob.FailureReason != FailureReason.None ? songJob.FailureReason.ToString() : null,
                songJob.FailureMessage),
            AlbumJob albumJob => new AlbumJobPayloadDto(
                ToAlbumQueryDto(albumJob.Query),
                albumJob.Results.Count,
                albumJob.DownloadPath,
                albumJob.ResolvedTarget?.Username,
                albumJob.ResolvedTarget?.FolderPath,
                albumJob.Results.Select(folder => ToAlbumFolderDto(folder, includeFiles: true)).ToList()),
            AggregateJob aggregateJob => new AggregateJobPayloadDto(
                ToSongQueryDto(aggregateJob.Query),
                aggregateJob.Songs.Select(song => new SongJobPayloadDto(
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
                    song.FailureMessage)).ToList()),
            AlbumAggregateJob albumAggregateJob => new AlbumAggregateJobPayloadDto(
                ToAlbumQueryDto(albumAggregateJob.Query)),
            JobList jobList => new JobListPayloadDto(
                jobList.Count,
                jobList.Jobs.OfType<SongJob>().Select(song => new SongJobPayloadDto(
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
                    song.FailureMessage)).ToList()),
            RetrieveFolderJob retrieveFolderJob => new RetrieveFolderJobPayloadDto(
                retrieveFolderJob.TargetFolder.FolderPath,
                retrieveFolderJob.TargetFolder.Username,
                retrieveFolderJob.NewFilesFoundCount),
            _ => new GenericJobPayloadDto(job.ToString(noInfo: true))
        };

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
                ? folder.Files.Select(song => new SongJobPayloadDto(
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
                    song.FailureMessage)).ToList()
                : null);

    private JobState EffectiveState(Job job)
        => executionCompletedJobs.Contains(job.Id) && IsActiveJobState(job.State)
            ? JobState.Done
            : job.State;

    private bool IsActiveJob(Job job)
        => IsActiveJobState(EffectiveState(job));

    private static bool IsActiveJobState(JobState state)
        => state is JobState.Pending or JobState.Searching or JobState.Downloading or JobState.Extracting;
}
