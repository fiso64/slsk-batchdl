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
        new EngineEventDtoAdapter(GetSummary, Publish).Attach(engine.Events);
    }

    public Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateExtractJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateTrackSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateSongJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateJobList(request), request.Options, ct);

    private Task<JobSummaryDto> SubmitJobAsync(Job job, SubmissionOptionsDto? options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (defaultSubmitSettings == null)
            throw new NotSupportedException("Local CLI submissions require a default settings baseline.");

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;

        var settings = SettingsCloner.Clone(defaultSubmitSettings);
        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;
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

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetTrackResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(null);

        var snapshot = searchJob.GetSortedTrackCandidates(searchJob.Config.Search, engine.UserSuccessCounts);
        return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(ToFileCandidateDto).ToList()));
    }

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetAlbumResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(null);

        var snapshot = searchJob.GetAlbumFolders(searchJob.Config.Search);
        return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(new(
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

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(null);

        var snapshot = searchJob.GetAggregateTracks(searchJob.Config.Search, engine.UserSuccessCounts);
        return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(song => new AggregateTrackCandidateDto(ToSongQueryDto(song.Query), song.ItemName)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = stateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(null);

        var snapshot = searchJob.GetAggregateAlbums(searchJob.Config.Search);
        return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(album => new AggregateAlbumCandidateDto(ToAlbumQueryDto(album.Query), album.ItemName)).ToList()));
    }

    public Task<IReadOnlyList<JobSummaryDto>?> StartExtractedResultAsync(Guid extractJobId, StartExtractedResultRequestDto request, CancellationToken ct = default)
        => throw new NotSupportedException("Local interactive extraction result handling is owned by InteractiveCliCoordinator.");

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

    public async Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        var jobs = await GetJobsAsync(new JobQuery(null, null, workflowId, CanonicalRootsOnly: false, IncludeNonDefault: true), ct);
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
        var descriptor = ServerEventCatalog.Describe(type);
        EventReceived?.Invoke(new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            descriptor.Category,
            descriptor.SnapshotInvalidation,
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
            new PresentationHintsDto(ServerProtocol.PresentationDisplayModes.Node, null, job.DisplayId, null),
            []);

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
