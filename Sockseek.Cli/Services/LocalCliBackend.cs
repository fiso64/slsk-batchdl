using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Api;
using Sockseek.Server;

namespace Sockseek.Cli;

internal sealed class LocalCliBackend
    : ICliBackend
{
    private readonly DownloadEngine engine;
    private readonly DownloadSettings? defaultSubmitSettings;
    private readonly SubmissionOptionsJobSettingsResolver? submissionOptionsResolver;
    private readonly DownloadSettingsPatchDto? explicitCliDownloadSettings;
    private readonly EngineStateStore stateStore = new();
    private readonly DaemonClientStore daemonStore = new();
    private readonly ConcurrentDictionary<Guid, byte> liveWorkflowSubscriptions = [];
    private volatile bool liveDaemonSubscription;

    public event Action<DaemonClientUpdate>? StateUpdated;
    public event Action<ActivityEventDto>? ActivityReceived;
    public event Action<StateSnapshotDto>? LiveSnapshotApplied;

    public DaemonClientStore ClientStore => daemonStore;

    public LocalCliBackend(
        DownloadEngine engine,
        DownloadSettings? defaultSubmitSettings = null,
        SubmissionOptionsJobSettingsResolver? submissionOptionsResolver = null,
        DownloadSettingsPatchDto? explicitCliDownloadSettings = null)
    {
        this.engine = engine;
        this.submissionOptionsResolver = submissionOptionsResolver;
        this.explicitCliDownloadSettings = explicitCliDownloadSettings;
        this.defaultSubmitSettings = defaultSubmitSettings != null
            ? SettingsCloner.Clone(defaultSubmitSettings)
            : null;
        stateStore.AttachEngine(engine);
        stateStore.StateBatchPublished += HandleStateBatch;
        new EngineActivityDtoAdapter(stateStore, GetSummary).Attach(engine.Events, engine.SearchEvents);
    }

    public Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateExtractJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => SubmitJobAsync(JobRequestMapper.CreateSearchJob(request), request.Options, ct);

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
    {
        var job = JobRequestMapper.CreateJobList(request);
        ApplyDraftJobOptions(job, request.Jobs);
        return SubmitJobAsync(job, request.Options, ct, request.Jobs);
    }

    public Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (liveDaemonSubscription)
            throw new InvalidOperationException("Cannot mix daemon and workflow subscriptions in one local backend.");
        if (liveWorkflowSubscriptions.TryAdd(workflowId, 0))
        {
            stateStore.ReserveWorkflowStream(workflowId);
            var snapshot = stateStore.GetWorkflowSnapshot(workflowId);
            LiveSnapshotApplied?.Invoke(snapshot);
            var update = daemonStore.ApplySnapshot(snapshot);
            StateUpdated?.Invoke(update);
        }
        return Task.CompletedTask;
    }

    public Task SubscribeAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!liveWorkflowSubscriptions.IsEmpty)
            throw new InvalidOperationException("Cannot mix daemon and workflow subscriptions in one local backend.");
        if (!liveDaemonSubscription)
        {
            liveDaemonSubscription = true;
            var snapshot = stateStore.GetDaemonSnapshot();
            LiveSnapshotApplied?.Invoke(snapshot);
            var update = daemonStore.ApplySnapshot(snapshot);
            StateUpdated?.Invoke(update);
        }
        return Task.CompletedTask;
    }

    private Task<JobSummaryDto> SubmitJobAsync(
        Job job,
        SubmissionOptionsDto? options,
        CancellationToken ct,
        IReadOnlyList<JobDraftDto>? childDrafts = null)
    {
        ct.ThrowIfCancellationRequested();
        if (defaultSubmitSettings == null)
            throw new NotSupportedException("Local CLI submissions require a default settings baseline.");

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);

        if (!liveDaemonSubscription)
            SubscribeWorkflowAsync(job.WorkflowId, ct).GetAwaiter().GetResult();

        submissionOptionsResolver?.SetJobOptions(job.Id, options);

        var settings = submissionOptionsResolver?.Resolve(defaultSubmitSettings, job)
            ?? SettingsCloner.Clone(defaultSubmitSettings);
        ApplySubmissionOptionsToInheritedSettings(settings, options);
        if (ContainsRemoteTransfer(job, settings))
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(explicitCliDownloadSettings);
        if (ContainsRemoteTransfer(job, settings)
            && options?.DownloadSettings is { } explicitPatch)
        {
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        }
        if (job is JobList jobList && childDrafts != null)
            ValidateDraftRemoteTransferOverrides(jobList, childDrafts, settings);
        NormalizeLocalSettings(settings);
        ValidateSubmissionSettings(job, settings);

        job.EnsureDisplayId();
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

    public Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var job = engine.GetJob(displayId);
        if (job == null || (workflowId.HasValue && job.WorkflowId != workflowId.Value))
            return Task.FromResult<JobDetailDto?>(null);
        return Task.FromResult(stateStore.GetJobDetail(job.Id));
    }

    public Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(stateStore.GetWorkflow(workflowId));
    }

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetFileResultsAsync(jobId, new FileSearchProjectionRequestDto(), ct);

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request.SongQuery != null
                ? new FileSearchProjection(
                    JobRequestMapper.ToSongQuery(request.SongQuery),
                    request.IncludeFullResults)
                : searchJob.DefaultFileProjection
                    ?? new FileSearchProjection(new SongQuery { Title = searchJob.QueryText });
            var snapshot = searchJob.GetSortedTrackCandidates(projection, searchJob.Config.Search, engine.UserSuccessCounts);
            return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(ToFileCandidateDto).ToList()));
        }

        var songJob = GetRuntimeJob<SongJob>(jobId);
        if (songJob == null)
            return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(null);

        return Task.FromResult<SearchResultSnapshotDto<FileCandidateDto>?>(new(
            Revision: 0,
            IsComplete: songJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: songJob.Candidates?.Select(ToFileCandidateDto).ToList() ?? []));
    }

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
        => GetFolderResultsAsync(jobId, null, includeFiles, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => GetFolderResultsAsync(jobId, request.AlbumQuery, request.IncludeFiles, ct);

    private Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, AlbumQueryDto? albumQuery, bool includeFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = albumQuery != null
                ? new FolderSearchProjection(JobRequestMapper.ToAlbumQuery(albumQuery), includeFiles)
                : searchJob.DefaultFolderProjection is { } defaultProjection
                    ? defaultProjection with { IncludeFiles = includeFiles }
                    : null;
            if (projection == null)
                throw new ArgumentException("Album folder projection requires an album query.");

            var snapshot = searchJob.GetAlbumFolders(projection, searchJob.Config.Search);
            return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(folder => new AlbumFolderDto(
                    new AlbumFolderRefDto(folder.Username, folder.FolderPath),
                    folder.Username,
                    folder.FolderPath,
                    new PeerInfoDto(
                        folder.Username,
                        folder.Files.FirstOrDefault()?.Candidate.HasFreeUploadSlot,
                        folder.Files.FirstOrDefault()?.Candidate.UploadSpeed),
                    folder.SearchFileCount,
                    folder.SearchAudioFileCount,
                    includeFiles
                        ? folder.Files
                            .Select(file => ToFileCandidateDto(file.Candidate))
                            .ToList()
                        : null,
                    folder.IsFullyRetrieved)).ToList()));
        }

        var albumJob = GetRuntimeJob<AlbumJob>(jobId);
        if (albumJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(null);

        var folders = JobRequestMapper.ProjectAlbumJobFolders(albumJob, engine.UserSuccessCounts);
        return Task.FromResult<SearchResultSnapshotDto<AlbumFolderDto>?>(new(
            Revision: 0,
            IsComplete: albumJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: folders.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetAggregateTrackResultsAsync(jobId, new AggregateTrackProjectionRequestDto(), ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request.SongQuery != null
                ? new AggregateTrackProjection(JobRequestMapper.ToSongQuery(request.SongQuery))
                : searchJob.DefaultAggregateTrackProjection
                    ?? (searchJob.DefaultFileProjection is { } fileProjection
                        ? new AggregateTrackProjection(fileProjection.Query)
                        : new AggregateTrackProjection(new SongQuery { Title = searchJob.QueryText }));
            bool includeCandidates = request.IncludeCandidates;
            var snapshot = searchJob.GetAggregateTracks(projection, searchJob.Config.Search, engine.UserSuccessCounts);
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(song => new AggregateTrackCandidateDto(
                    ToSongQueryDto(song.Query),
                    song.ItemName,
                    includeCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList()));
        }

        var aggregateJob = GetRuntimeJob<AggregateJob>(jobId);
        if (aggregateJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(null);

        bool includeAggregateCandidates = request.IncludeCandidates;
        return Task.FromResult<SearchResultSnapshotDto<AggregateTrackCandidateDto>?>(new(
            Revision: 0,
            IsComplete: aggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: aggregateJob.Songs.Select(song => new AggregateTrackCandidateDto(
                ToSongQueryDto(song.Query),
                song.ItemName,
                includeAggregateCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList()));
    }

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, null, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => GetAggregateAlbumResultsCoreAsync(jobId, request, ct);

    private Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsCoreAsync(Guid jobId, AggregateAlbumProjectionRequestDto? request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var projection = request?.AlbumQuery != null
                ? new AggregateAlbumProjection(JobRequestMapper.ToAlbumQuery(request.AlbumQuery))
                : searchJob.DefaultAggregateAlbumProjection
                    ?? (searchJob.DefaultFolderProjection is { } folderProjection
                        ? new AggregateAlbumProjection(folderProjection.Query)
                        : null);
            if (projection == null)
                throw new ArgumentException("Aggregate album projection requires an album query.");

            bool includeFolders = request?.IncludeFolders ?? false;
            var snapshot = searchJob.GetAggregateAlbums(projection, searchJob.Config.Search);
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(album => new AggregateAlbumCandidateDto(
                    ToAlbumQueryDto(album.Query),
                    album.ItemName,
                    includeFolders ? [..album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true))] : null)).ToList()));
        }

        var albumAggregateJob = GetRuntimeJob<AlbumAggregateJob>(jobId);
        if (albumAggregateJob == null)
            return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(null);

        bool includeAggregateFolders = request?.IncludeFolders ?? false;
        return Task.FromResult<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?>(new(
            Revision: 0,
            IsComplete: albumAggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: albumAggregateJob.Albums.Select(album => new AggregateAlbumCandidateDto(
                ToAlbumQueryDto(album.Query),
                album.ItemName,
                includeAggregateFolders ? [..album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true))] : null)).ToList()));
    }

    public Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = FindAlbumFolderForRetrieval(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = new RetrieveFolderJob(folder.DirectoryIdentity) { ItemName = folder.FolderPath, WorkflowId = sourceJob.WorkflowId };
        stateStore.SetSourceJob(retrieveJob.Id, sourceJobId);
        retrieveJob.EnsureDisplayId();
        engine.Enqueue(retrieveJob, sourceJob.Config);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId));
    }

    public async Task<RetrieveFolderJobPayloadDto?> RetrieveFolderAndWaitAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return null;

        var folder = FindAlbumFolderForRetrieval(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = await engine.ProcessFolderRetrieval(folder, sourceJob);
        return new RetrieveFolderJobPayloadDto(
            retrieveJob.Directory.FolderPath,
            retrieveJob.Directory.Username,
            retrieveJob.NewFilesFoundCount,
            EngineStateStore.ToServerFolderRetrievalOutcome(retrieveJob.RetrievalOutcome),
            retrieveJob.RetrievalCancelled);
    }

    public Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(null);

        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");
        if (request.RequestedMode == ExtractionMode.Album)
            throw new ArgumentException("A file selection cannot be interpreted as an album.");

        var summaries = new List<JobSummaryDto>();
        if (request.RequestedMode == ExtractionMode.General
            && request.Options?.DownloadSettings is { } explicitPatch)
        {
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        }

        if ((request.RequestedMode is null or ExtractionMode.Song)
            && sourceJob is SongJob manualSong && manualSong.IsAwaitingSelection)
        {
            if (request.Files.Count != 1)
                throw new ArgumentException("Manual song jobs require exactly one selected file.");

            var candidate = FindFileCandidate(sourceJob, request.Files[0]);
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            manualSong.ResolvedTarget = candidate;
            manualSong.Candidates ??= [candidate];
            if (!manualSong.Candidates.Contains(candidate))
                manualSong.Candidates.Insert(0, candidate);
            manualSong.ResetToPending();
            manualSong.EnsureDisplayId();
            engine.Resume(manualSong);
            summaries.Add(stateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong));
            return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(summaries);
        }

        foreach (var file in request.Files)
        {
            var candidate = FindFileCandidate(sourceJob, file);
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

            Job followUpJob;
            if (request.RequestedMode == ExtractionMode.General)
            {
                followUpJob = new RemoteFileJob(candidate.Target);
            }
            else
            {
                var songQuery = sourceJob switch
                {
                    SearchJob searchJob => searchJob.DefaultFileProjection?.Query
                        ?? Searcher.InferSongQuery(candidate.Filename, new SongQuery { Title = searchJob.QueryText }),
                    SongJob existingSongJob => existingSongJob.Query,
                    AggregateJob aggregateJob => aggregateJob.Songs
                        .FirstOrDefault(song => song.Candidates?.Contains(candidate) == true)?.Query
                        ?? Searcher.InferSongQuery(candidate.Filename, aggregateJob.Query),
                    _ => Searcher.InferSongQuery(candidate.Filename, sourceJob.QueryTrack ?? new SongQuery()),
                };
                followUpJob = new SongJob(new SongQuery(songQuery))
                {
                    ResolvedTarget = candidate,
                };
            }
            followUpJob.ItemName = sourceJob.ItemName;
            followUpJob.WorkflowId = sourceJob.WorkflowId;
            var settings = BuildFollowUpSettings(sourceJob, followUpJob, request.Options);
            if (followUpJob is RemoteFileJob)
            {
                RemoteTransferSettingsValidator.ValidateExplicitNameFormat(explicitCliDownloadSettings);
                ValidateSubmissionSettings(followUpJob, settings);
            }

            if (ShouldPropagateSourceMutationToFollowUp(sourceJob))
                followUpJob.CopySourceMutationFrom(sourceJob);
            stateStore.SetSourceJob(followUpJob.Id, sourceJobId);
            followUpJob.EnsureDisplayId();
            engine.Enqueue(followUpJob, settings);
            summaries.Add(stateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId));
        }

        return Task.FromResult<IReadOnlyList<JobSummaryDto>?>(summaries);
    }

    public Task<JobSummaryDto?> StartFolderDownloadAsync(Guid sourceJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.RequestedMode == ExtractionMode.Song)
            throw new ArgumentException("A directory selection cannot be interpreted as one song.");

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return Task.FromResult<JobSummaryDto?>(null);

        var folder = FindAlbumFolder(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        folder = JobRequestMapper.ApplySelectedFolderSnapshot(folder, request);
        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        if (request.RequestedMode == ExtractionMode.General
            && request.Options?.DownloadSettings is { } explicitPatch)
        {
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        }

        if (request.RequestedMode == ExtractionMode.General)
        {
            var directoryJob = JobRequestMapper.CreateRemoteDirectoryDownload(folder, request.Selection);
            directoryJob.ItemName = sourceJob.ItemName;
            directoryJob.WorkflowId = sourceJob.WorkflowId;
            var settings = BuildFollowUpSettings(sourceJob, directoryJob, request.Options);
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(explicitCliDownloadSettings);
            ValidateSubmissionSettings(directoryJob, settings);
            if (ShouldPropagateSourceMutationToFollowUp(sourceJob))
                directoryJob.CopySourceMutationFrom(sourceJob);
            stateStore.SetSourceJob(directoryJob.Id, sourceJobId);
            directoryJob.EnsureDisplayId();
            engine.Enqueue(directoryJob, settings);
            return Task.FromResult<JobSummaryDto?>(
                stateStore.GetJobSummary(directoryJob.Id)
                ?? BuildSubmittedJobSummary(directoryJob, sourceJobId));
        }

        var albumQuery = request.AlbumQuery != null
            ? JobRequestMapper.ToAlbumQuery(request.AlbumQuery)
            : sourceJob switch
            {
                SearchJob searchJob => searchJob.DefaultFolderProjection?.Query,
                AlbumJob existingAlbumJob => existingAlbumJob.Query,
                AlbumAggregateJob aggregate => aggregate.Query,
                _ => null,
            };

        if (engine.TryStartManualAlbumSelection(
            sourceJobId,
            folder,
            albumQuery,
            album => JobRequestMapper.ApplyFolderDownloadSelection(album, request.Selection),
            out var selectedAlbum))
        {
            if (sourceJob is AlbumAggregateJob)
                stateStore.SetSourceJob(selectedAlbum!.Id, sourceJobId);

            return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(selectedAlbum!.Id) ?? BuildSubmittedJobSummary(selectedAlbum!, sourceJobId));
        }
        if (albumQuery == null)
            throw new ArgumentException("Album downloads from this job require an album query.");

        string? itemName = sourceJob.ItemName;
        if (sourceJob is SearchJob { DefaultAggregateAlbumProjection: not null } && !string.IsNullOrWhiteSpace(folder.FolderPath))
            itemName = Utils.GetBaseNameSlsk(folder.FolderPath);

        var followUpAlbumJob = new AlbumJob(new AlbumQuery(albumQuery))
        {
            ResolvedTarget = folder,
            ItemName = itemName,
            WorkflowId = sourceJob.WorkflowId,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
        };
        JobRequestMapper.ApplyFolderDownloadSelection(followUpAlbumJob, request.Selection);
        var albumSettings = BuildFollowUpSettings(sourceJob, followUpAlbumJob, request.Options);
        if (ShouldPropagateSourceMutationToFollowUp(sourceJob))
            followUpAlbumJob.CopySourceMutationFrom(sourceJob);

        stateStore.SetSourceJob(followUpAlbumJob.Id, sourceJobId);
        followUpAlbumJob.EnsureDisplayId();
        engine.Enqueue(followUpAlbumJob, albumSettings);
        return Task.FromResult<JobSummaryDto?>(stateStore.GetJobSummary(followUpAlbumJob.Id) ?? BuildSubmittedJobSummary(followUpAlbumJob, sourceJobId));
    }

    public async Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await engine.CompleteManualSelectionAsync(jobId);
    }

    public async Task<bool> SkipManualSelectionAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await engine.SkipManualSelectionAsync(jobId);
    }

    private static bool ShouldPropagateSourceMutationToFollowUp(Job sourceJob)
        => sourceJob is not AlbumAggregateJob;

    private void ApplyDraftJobOptions(JobList list, IReadOnlyList<JobDraftDto> drafts)
    {
        for (int index = 0; index < list.Jobs.Count && index < drafts.Count; index++)
            ApplyDraftJobOptions(list.Jobs[index], drafts[index]);
    }

    private void ApplyDraftJobOptions(Job job, JobDraftDto draft)
    {
        if (DraftDownloadSettings(draft) is { } patch)
        {
            submissionOptionsResolver?.SetJobOptions(
                job.Id,
                new SubmissionOptionsDto(DownloadSettings: patch));
        }

        if (job is JobList childList && draft is JobListJobDraftDto childDraft)
            ApplyDraftJobOptions(childList, childDraft.Jobs);
    }

    private static DownloadSettingsPatchDto? DraftDownloadSettings(JobDraftDto draft)
        => draft switch
        {
            ExtractJobDraftDto typed => typed.DownloadSettings,
            TrackSearchJobDraftDto typed => typed.DownloadSettings,
            AlbumSearchJobDraftDto typed => typed.DownloadSettings,
            SongJobDraftDto typed => typed.DownloadSettings,
            AlbumJobDraftDto typed => typed.DownloadSettings,
            AggregateJobDraftDto typed => typed.DownloadSettings,
            AlbumAggregateJobDraftDto typed => typed.DownloadSettings,
            JobListJobDraftDto typed => typed.DownloadSettings,
            RemoteFileJobDraftDto typed => typed.DownloadSettings,
            RemoteDirectoryJobDraftDto typed => typed.DownloadSettings,
            _ => null,
        };

    private void ValidateSubmissionSettings(Job job, DownloadSettings settings)
    {
        if (IsOrdinaryRemoteTransfer(job, settings))
        {
            RemoteTransferNameFormatPolicy.ApplyInherited(settings.Output);
        }

        if (job is not JobList list)
            return;

        foreach (Job child in list.Jobs)
        {
            var childSettings = submissionOptionsResolver?.Resolve(settings, child)
                ?? SettingsCloner.Clone(settings);
            ValidateSubmissionSettings(child, childSettings);
        }
    }

    private static bool IsOrdinaryRemoteTransfer(Job job, DownloadSettings settings)
        => job is RemoteFileJob or RemoteDirectoryJob
            || (job is ExtractJob extract
                && Sockseek.Core.Extractors.SoulseekExtractor.InputMatches(extract.Input)
                && settings.Extraction.RequestedMode is null or ExtractionMode.General);

    private bool ContainsRemoteTransfer(Job job, DownloadSettings settings)
    {
        if (IsOrdinaryRemoteTransfer(job, settings))
            return true;
        if (job is not JobList list)
            return false;

        return list.Jobs.Any(child => ContainsRemoteTransfer(child, ResolveChildSettings(settings, child)));
    }

    private void ValidateDraftRemoteTransferOverrides(
        JobList list,
        IReadOnlyList<JobDraftDto> drafts,
        DownloadSettings parentSettings)
    {
        for (int index = 0; index < list.Jobs.Count && index < drafts.Count; index++)
        {
            Job child = list.Jobs[index];
            JobDraftDto draft = drafts[index];
            DownloadSettingsPatchDto? patch = DraftDownloadSettings(draft);
            DownloadSettings childSettings = ResolveChildSettings(parentSettings, child, patch);
            if (patch != null
                && ContainsRemoteTransfer(child, childSettings))
            {
                RemoteTransferSettingsValidator.ValidateExplicitPatch(patch);
            }

            if (child is JobList childList && draft is JobListJobDraftDto childDraft)
                ValidateDraftRemoteTransferOverrides(childList, childDraft.Jobs, childSettings);
        }
    }

    private DownloadSettings ResolveChildSettings(
        DownloadSettings parentSettings,
        Job child,
        DownloadSettingsPatchDto? fallbackPatch = null)
    {
        if (submissionOptionsResolver != null)
            return submissionOptionsResolver.Resolve(parentSettings, child);

        var settings = SettingsCloner.Clone(parentSettings);
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, fallbackPatch);
        return settings;
    }

    private DownloadSettings BuildFollowUpSettings(
        Job sourceJob,
        Job followUpJob,
        SubmissionOptionsDto? options)
    {
        if (submissionOptionsResolver != null)
        {
            submissionOptionsResolver.SetJobOptions(followUpJob.Id, options);
            return submissionOptionsResolver.Resolve(
                defaultSubmitSettings ?? sourceJob.Config!,
                followUpJob);
        }

        var settings = SettingsCloner.Clone(defaultSubmitSettings ?? sourceJob.Config);
        ApplySubmissionOptionsToInheritedSettings(settings, options);
        NormalizeLocalSettings(settings);
        return settings;
    }

    private void ApplySubmissionOptionsToInheritedSettings(DownloadSettings settings, SubmissionOptionsDto? options)
    {
        if (submissionOptionsResolver != null)
            return;

        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);
        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;
    }

    private static void NormalizeLocalSettings(DownloadSettings settings)
    {
        SettingsNormalizer.NormalizeDownloadPaths(settings, settings.RuntimePathContext);
    }

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(engine.CancelJob(jobId));
    }

    public async Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await Task.FromResult(engine.CancelJobByDisplayId(displayId, workflowId));
    }

    public Task<int> CancelAllJobsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.CancelAllJobs());
    }

    public Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.CancelWorkflow(workflowId));
    }

    public Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.TryNextCandidate(jobId));
    }

    public Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(engine.TryNextCandidateByDisplayId(displayId, workflowId));
    }

    private void HandleStateBatch(StateUpdateBatchDto batch)
    {
        bool subscribed = batch.Scope.Kind == StateStreamScopeKind.Daemon
            ? liveDaemonSubscription
            : liveWorkflowSubscriptions.ContainsKey(batch.Scope.WorkflowId!.Value);
        if (!subscribed)
            return;

        var update = daemonStore.Apply(batch);
        StateUpdated?.Invoke(update);
        foreach (var activity in update.Activity)
            ActivityReceived?.Invoke(activity);
    }

    private AlbumFolder? FindAlbumFolderForRetrieval(Job sourceJob, AlbumFolderRefDto folderRef, AlbumQueryDto? albumQuery = null)
    {
        static bool Matches(AlbumFolder folder, AlbumFolderRefDto folderRef)
            => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal);

        if (sourceJob is AlbumJob albumJob)
            return albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? FindAlbumFolder(sourceJob, folderRef, albumQuery);

        return FindAlbumFolder(sourceJob, folderRef, albumQuery);
    }

    private AlbumFolder? FindAlbumFolder(Job sourceJob, AlbumFolderRefDto folderRef, AlbumQueryDto? albumQuery = null)
    {
        static bool Matches(AlbumFolder folder, AlbumFolderRefDto folderRef)
            => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal);

        if (sourceJob is SearchJob searchJob)
        {
            if (searchJob.Config == null)
                return null;

            var projection = albumQuery != null
                ? new FolderSearchProjection(JobRequestMapper.ToAlbumQuery(albumQuery))
                : searchJob.DefaultFolderProjection;
            if (projection == null)
                return null;

            var folders = searchJob.GetAlbumFolders(projection, searchJob.Config.Search).Items;
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? JobRequestMapper.BuildRelatedFolder(folderRef, folders);
        }

        if (sourceJob is AlbumJob albumJob)
            return JobRequestMapper.FindProjectedAlbumFolder(albumJob, folderRef, engine.UserSuccessCounts)
                ?? albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? JobRequestMapper.BuildRelatedFolder(folderRef, albumJob.Results);

        if (sourceJob is AlbumAggregateJob aggregateJob)
        {
            var folders = aggregateJob.Albums
                .Where(album => albumQuery == null || AlbumQueriesEqual(album.Query, JobRequestMapper.ToAlbumQuery(albumQuery)))
                .SelectMany(album => album.Results)
                .ToList();
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? JobRequestMapper.BuildRelatedFolder(folderRef, folders);
        }

        return null;
    }

    private FileCandidate? FindFileCandidate(Job sourceJob, FileCandidateRefDto candidateRef)
    {
        static bool Matches(FileCandidate candidate, FileCandidateRefDto candidateRef)
            => string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal);

        if (sourceJob is SearchJob searchJob)
            return FindTrackCandidate(searchJob, candidateRef);

        if (sourceJob is SongJob songJob)
            return songJob.Candidates?.FirstOrDefault(candidate => Matches(candidate, candidateRef));

        if (sourceJob is AggregateJob aggregateJob)
            return aggregateJob.Songs
                .SelectMany(song => song.Candidates ?? Enumerable.Empty<FileCandidate>())
                .FirstOrDefault(candidate => Matches(candidate, candidateRef));

        if (sourceJob is AlbumJob albumJob)
            return albumJob.Results
                .SelectMany(folder => folder.Files)
                .Select(file => file.Candidate)
                .FirstOrDefault(candidate => Matches(candidate, candidateRef));

        if (sourceJob is AlbumAggregateJob aggregateAlbumJob)
            return aggregateAlbumJob.Albums
                .SelectMany(album => album.Results)
                .SelectMany(folder => folder.Files)
                .Select(file => file.Candidate)
                .FirstOrDefault(candidate => Matches(candidate, candidateRef));

        return null;
    }

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase);

    private FileCandidate? FindTrackCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
    {
        if (searchJob.Config == null)
            return null;

        var trackCandidate = searchJob.GetSortedTrackCandidates(searchJob.Config.Search, engine.UserSuccessCounts)
            .Items
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));

        if (trackCandidate != null || searchJob.DefaultFolderProjection == null)
            return trackCandidate ?? FindRawFileCandidate(searchJob, candidateRef);

        return searchJob.GetAlbumFolders(searchJob.Config.Search)
            .Items
            .SelectMany(folder => folder.Files)
            .Select(file => file.Candidate)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));
    }

    private static FileCandidate? FindRawFileCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
        => searchJob.RawSnapshot()
            .Select(result => result.ProjectionInput.ToFileCandidate())
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => ServerSnapshotMapper.ToSubmittedJobSummary(job, sourceJobId);

    private JobSummaryDto GetSummary(JobSnapshot job)
        => stateStore.GetJobSummary(job.Id) ?? ServerSnapshotMapper.ToJobSummary(job);

    private TJob? GetRuntimeJob<TJob>(Guid jobId)
        where TJob : Job
        => engine.GetJob(jobId) as TJob;

    private static SongQueryDto ToSongQueryDto(SongQuery query)
        => new(Optional(query.Artist), Optional(query.Title), Optional(query.Album), Optional(query.URI), Optional(query.Length), query.ArtistMaybeWrong);

    private static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query)
        => new(Optional(query.Artist), Optional(query.Album), Optional(query.SearchHint), Optional(query.URI), query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate)
        => new(
            new FileCandidateRefDto(candidate.Username, candidate.Filename),
            candidate.Username,
            candidate.Filename,
            new PeerInfoDto(candidate.Username, candidate.HasFreeUploadSlot, candidate.UploadSpeed),
            new FileMetadataDto(
                Utils.GetFileNameSlsk(candidate.Filename),
                candidate.Size,
                candidate.Extension,
                candidate.BitRate,
                candidate.BitDepth,
                candidate.SampleRate,
                candidate.Length,
                candidate.Attributes?.Select(x => new FileAttributeDto(x.Type, x.Value)).ToList()));

    private static SongJobPayloadDto ToSongJobPayloadDto(SongJob song)
        => new(
            ToSongQueryDto(song.Query),
            song.Candidates?.Count,
            new FileDownloadStateDto(
                song.DownloadPath,
                song.BytesTransferred,
                song.FileSize ?? song.ResolvedPeerTarget?.Size,
                (song.FileSize ?? song.ResolvedPeerTarget?.Size) is > 0 and var size
                    ? Math.Round((double)song.BytesTransferred / size * 100, 2)
                    : null),
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.HasFreeUploadSlot,
            song.ResolvedTarget?.UploadSpeed,
            song.ResolvedTarget?.Size,
            song.ResolvedTarget?.SampleRate,
            song.ResolvedTarget?.Extension,
            song.ResolvedTarget?.Attributes?.Select(x => new FileAttributeDto(x.Type, x.Value)).ToList(),
            song.Id,
            song.DisplayId,
            EngineStateStore.ToServerJobLifecycleState(song.LifecycleState),
            EngineStateStore.ToServerJobActivityPhase(song.ActivityPhase),
            song.ActivityUntilUtc,
            EngineStateStore.ToServerJobTerminalOutcome(song.TerminalOutcome),
            EngineStateStore.ToServerJobSkipReason(song.SkipReason),
            EngineStateStore.ToServerFailureReason(song.FailureReason),
            song.FailureMessage,
            CancellationSource: EngineStateStore.ToServerJobCancellationSource(song.CancellationSource),
            DownloadSource: EngineStateStore.ToServerSongDownloadSource(song.DownloadSource),
            ExactTarget: song.ExactTarget == null ? null : ToPeerFileTargetDto(song.ExactTarget));

    private static PeerFileTargetDto ToPeerFileTargetDto(PeerFileTarget target)
        => new(
            target.Username,
            target.Filename,
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.Select(x => new FileAttributeDto(x.Type, x.Value)).ToList());

    private static AlbumFolderDto ToAlbumFolderDto(AlbumFolder folder, bool includeFiles)
        => new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            new PeerInfoDto(
                folder.Username,
                folder.Files.FirstOrDefault()?.Candidate.HasFreeUploadSlot,
                folder.Files.FirstOrDefault()?.Candidate.UploadSpeed),
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            includeFiles
                ? folder.Files
                    .Select(file => ToFileCandidateDto(file.Candidate))
                    .ToList()
                : null,
            folder.IsFullyRetrieved);
}
