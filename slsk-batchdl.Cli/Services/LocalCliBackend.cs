using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Settings;
using Sldl.Server;

namespace Sldl.Cli;

internal sealed class LocalCliBackend
    : ICliBackend
{
    private readonly DownloadEngine engine;
    private readonly DownloadSettings? defaultSubmitSettings;
    private readonly EngineStateStore stateStore = new();
    private long nextSequence;

    public event Action<ServerEventEnvelopeDto>? EventReceived;

    public LocalCliBackend(DownloadEngine engine, DownloadSettings? defaultSubmitSettings = null)
    {
        this.engine = engine;
        this.defaultSubmitSettings = defaultSubmitSettings != null
            ? SettingsCloner.Clone(defaultSubmitSettings)
            : null;
        stateStore.AttachEngine(engine);
        stateStore.JobUpserted += summary => Publish("job.upserted", summary);
        stateStore.WorkflowUpserted += summary => Publish("workflow.upserted", summary);
        stateStore.SearchUpdated += update => Publish("search.updated", update);
        engine.Events.ExtractionStarted += job => Publish("extraction.started", new ExtractionStartedEventDto(GetSummary(job), job.Input, job.InputType?.ToString()));
        engine.Events.ExtractionFailed += (job, reason) => Publish("extraction.failed", new ExtractionFailedEventDto(GetSummary(job), reason));
        engine.Events.JobStarted += job => Publish("job.started", new JobStartedEventDto(GetSummary(job)));
        engine.Events.JobCompleted += (job, found, locked) => Publish("job.completed", new JobCompletedEventDto(GetSummary(job), found, locked));
        engine.Events.JobStatus += (job, status) => Publish("job.status", new JobStatusEventDto(GetSummary(job), status));
        engine.Events.JobFolderRetrieving += job => Publish("job.folder-retrieving", new JobFolderRetrievingEventDto(GetSummary(job)));
        engine.Events.SongSearching += song => Publish("song.searching", new SongSearchingEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        engine.Events.SongNotFound += song => Publish("song.not-found", new SongNotFoundEventDto(
            song.Id,
            song.DisplayId,
            song.WorkflowId,
            ToSongQueryDto(song.Query),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null));
        engine.Events.SongFailed += song => Publish("song.failed", new SongFailedEventDto(
            song.Id,
            song.DisplayId,
            song.WorkflowId,
            ToSongQueryDto(song.Query),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null));
        engine.Events.DownloadStarted += (song, candidate) => Publish("download.started", new DownloadStartedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query), ToFileCandidateDto(candidate)));
        engine.Events.DownloadProgress += (song, transferred, total) => Publish("download.progress", new DownloadProgressEventDto(song.Id, transferred, total));
        engine.Events.DownloadStateChanged += (song, state) => Publish("download.state-changed", new DownloadStateChangedEventDto(song.Id, state.ToString()));
        engine.Events.StateChanged += song => Publish("song.state-changed", new SongStateChangedEventDto(
            song.Id,
            song.DisplayId,
            song.WorkflowId,
            ToSongQueryDto(song.Query),
            song.State.ToString(),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null,
            song.DownloadPath,
            song.ChosenCandidate != null ? ToFileCandidateDto(song.ChosenCandidate) : null));
        engine.Events.AlbumDownloadStarted += (job, folder) => Publish("album.download-started", new AlbumDownloadStartedEventDto(GetSummary(job), ToAlbumFolderDto(folder, includeFiles: true)));
        engine.Events.AlbumTrackDownloadStarted += (job, folder) => Publish("album.track-download-started", new AlbumTrackDownloadStartedEventDto(GetSummary(job), ToAlbumFolderDto(folder, includeFiles: true)));
        engine.Events.AlbumDownloadCompleted += job => Publish("album.download-completed", new AlbumDownloadCompletedEventDto(GetSummary(job)));
        engine.Events.OnCompleteStart += song => Publish("on-complete.started", new OnCompleteStartedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        engine.Events.OnCompleteEnd += song => Publish("on-complete.ended", new OnCompleteEndedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        engine.Events.TrackBatchResolved += (job, pending, existing, notFound) => Publish("track-batch.resolved", new TrackBatchResolvedEventDto(
            GetSummary(job),
            job is JobList,
            job.Config.PrintOption,
            pending.Select(ToSongJobPayloadDto).ToList(),
            existing.Select(ToSongJobPayloadDto).ToList(),
            notFound.Select(ToSongJobPayloadDto).ToList()));
    }

    public Task<JobSummaryDto> SubmitJobAsync(SubmitJobRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (defaultSubmitSettings == null)
            throw new NotSupportedException("Local CLI submissions require a default settings baseline.");

        var job = JobRequestMapper.CreateJob(request.Job);
        if (request.Options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;

        var settings = SettingsCloner.Clone(defaultSubmitSettings);
        if (!string.IsNullOrWhiteSpace(request.Options?.OutputParentDir))
            settings.Output.ParentDir = request.Options.OutputParentDir;
        SettingsNormalizer.Normalize(settings);

        engine.Enqueue(job, settings);
        return Task.FromResult(stateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job));
    }

    public Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(stateStore.GetJobs(query));
    }

    public Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(stateStore.GetJobDetail(jobId));
    }

    public Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(stateStore.GetWorkflow(workflowId));
    }

    public Task<SearchProjectionSnapshotDto<FileCandidateDto>?> GetTrackProjectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchProjectionSnapshotDto<FileCandidateDto>?>(null);

        var snapshot = searchJob.GetSortedTrackCandidates(searchJob.Config.Search, engine.UserSuccessCounts);
        return Task.FromResult<SearchProjectionSnapshotDto<FileCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(ToFileCandidateDto).ToList()));
    }

    public Task<SearchProjectionSnapshotDto<AlbumFolderDto>?> GetAlbumProjectionAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchProjectionSnapshotDto<AlbumFolderDto>?>(null);

        var snapshot = searchJob.GetAlbumFolders(searchJob.Config.Search);
        return Task.FromResult<SearchProjectionSnapshotDto<AlbumFolderDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(folder => new AlbumFolderDto(
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
                        new SongQueryDto(
                            song.Query.Artist,
                            song.Query.Title,
                            song.Query.Album,
                            song.Query.URI,
                            song.Query.Length,
                            song.Query.ArtistMaybeWrong,
                            song.Query.IsDirectLink),
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
                        song.Candidates?.Select(ToFileCandidateDto).ToList())).ToList()
                    : null)).ToList()));
    }

    public Task<SearchProjectionSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackProjectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchProjectionSnapshotDto<AggregateTrackCandidateDto>?>(null);

        var snapshot = searchJob.GetAggregateTracks(searchJob.Config.Search, engine.UserSuccessCounts);
        return Task.FromResult<SearchProjectionSnapshotDto<AggregateTrackCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(song => new AggregateTrackCandidateDto(ToSongQueryDto(song.Query), song.ItemName)).ToList()));
    }

    public Task<SearchProjectionSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumProjectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchProjectionSnapshotDto<AggregateAlbumCandidateDto>?>(null);

        var snapshot = searchJob.GetAggregateAlbums(searchJob.Config.Search);
        return Task.FromResult<SearchProjectionSnapshotDto<AggregateAlbumCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(album => new AggregateAlbumCandidateDto(ToAlbumQueryDto(album.Query), album.ItemName)).ToList()));
    }

    public Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = FindAlbumFolder(searchJob, request.Folder);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this search job.");

        var retrieveJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath };
        retrieveJob.WorkflowId = searchJob.WorkflowId;
        engine.Enqueue(retrieveJob, searchJob.Config);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob));
    }

    public async Task<int> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        var summary = await StartRetrieveFolderAsync(searchJobId, request, ct);
        if (summary == null)
            return 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var retrieveJob = stateStore.GetJob<RetrieveFolderJob>(summary.JobId);
            if (retrieveJob == null)
            {
                await Task.Delay(25, ct);
                continue;
            }

            if (retrieveJob.State is not (JobState.Pending or JobState.Extracting or JobState.Searching or JobState.Downloading))
                return retrieveJob.NewFilesFoundCount;

            await Task.Delay(25, ct);
        }
    }

    public Task<JobSummaryDto?> StartSongDownloadAsync(Guid searchJobId, StartSongDownloadRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var candidate = FindTrackCandidate(searchJob, request.Candidate);
        if (candidate == null)
            throw new ArgumentException("Requested candidate was not found in this search job.");

        var songJob = new SongJob(new SongQuery(searchJob.Query))
        {
            ResolvedTarget = candidate,
            ItemName = searchJob.ItemName,
            WorkflowId = searchJob.WorkflowId,
        };

        var settings = SettingsCloner.Clone(searchJob.Config);
        if (!string.IsNullOrWhiteSpace(request.OutputParentDir))
            settings.Output.ParentDir = request.OutputParentDir;
        SettingsNormalizer.Normalize(settings);

        engine.Enqueue(songJob, settings);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(songJob.Id) ?? BuildSubmittedJobSummary(songJob));
    }

    public Task<JobSummaryDto?> StartAlbumDownloadAsync(Guid searchJobId, StartAlbumDownloadRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        if (searchJob.AlbumQuery == null)
            throw new ArgumentException("Album downloads can only be started from album search jobs.");

        var folder = FindAlbumFolder(searchJob, request.Folder);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this search job.");

        var albumJob = new AlbumJob(new AlbumQuery(searchJob.AlbumQuery))
        {
            ResolvedTarget = folder,
            AllowBrowseResolvedTarget = request.AllowBrowseResolvedTarget,
            ItemName = searchJob.ItemName,
            WorkflowId = searchJob.WorkflowId,
        };

        var settings = SettingsCloner.Clone(searchJob.Config);
        if (!string.IsNullOrWhiteSpace(request.OutputParentDir))
            settings.Output.ParentDir = request.OutputParentDir;
        SettingsNormalizer.Normalize(settings);

        engine.Enqueue(albumJob, settings);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(albumJob.Id) ?? BuildSubmittedJobSummary(albumJob));
    }

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var job = engine.GetJob(jobId);
        if (job == null)
            return Task.FromResult(false);

        job.Cancel();
        return Task.FromResult(true);
    }

    public async Task<bool> CancelJobByDisplayIdAsync(int displayId, CancellationToken ct = default)
    {
        var jobs = await GetJobsAsync(new JobQuery(null, null, null, RootOnly: false, IncludeHidden: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await CancelJobAsync(match.JobId, ct);
    }

    public Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.CancelWorkflow(workflowId));
    }

    private void Publish(string type, object payload)
    {
        EventReceived?.Invoke(new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            payload));
    }

    private AlbumFolder? FindAlbumFolder(SearchJob searchJob, AlbumFolderRefDto folderRef)
        => searchJob.Config == null
            ? null
            : searchJob.GetAlbumFolders(searchJob.Config.Search).Items.FirstOrDefault(folder =>
                string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));

    private FileCandidate? FindTrackCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
        => searchJob.Config == null
            ? null
            : searchJob.GetSortedTrackCandidates(searchJob.Config.Search, engine.UserSuccessCounts)
                .Items
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                    && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));

    private static JobSummaryDto BuildSubmittedJobSummary(Job job)
        => new(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            EngineStateStore.GetJobKind(job),
            job.State.ToString(),
            job.ItemName,
            job.ToString(noInfo: true),
            job.FailureReason != FailureReason.None ? job.FailureReason.ToString() : null,
            job.FailureMessage,
            null,
            null,
            job.Config?.AppliedAutoProfiles?.ToList() ?? [],
            new PresentationHintsDto(false, null, job.DisplayId, null));

    private JobSummaryDto GetSummary(Job job)
        => stateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);

    private static SongQueryDto ToSongQueryDto(SongQuery query)
        => new(query.Artist, query.Title, query.Album, query.URI, query.Length, query.ArtistMaybeWrong, query.IsDirectLink);

    private static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query)
        => new(query.Artist, query.Album, query.SearchHint, query.URI, query.ArtistMaybeWrong, query.IsDirectLink, query.MinTrackCount, query.MaxTrackCount);

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate)
        => new(
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
            song.FailureMessage);

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
            includeFiles ? folder.Files.Select(ToSongJobPayloadDto).ToList() : null);
}
