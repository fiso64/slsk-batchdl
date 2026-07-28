using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
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

namespace Sockseek.Server;

public sealed class EngineSupervisor
{
    private readonly ServerOptions options;
    private readonly EngineSettings engineSettings;
    private readonly DownloadSettings defaultDownloadSettings;
    private readonly ProfileCatalog profileCatalog;
    private readonly ServerJobSettingsResolver jobSettingsResolver;
    private readonly Channel<QueuedSubmission> submissionChannel = Channel.CreateUnbounded<QueuedSubmission>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock engineGate = new();
    private readonly PersistenceCoordinator? persistence;

    private DownloadEngine? currentEngine;
    private int restartCount;

    public event Action<DownloadEngine>? EngineCreated;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public EngineStateStore StateStore { get; }

    public EngineSupervisor(IOptions<ServerOptions> options, PersistenceCoordinator? persistence = null)
    {
        this.options = options.Value;
        this.persistence = persistence;

        engineSettings = SettingsCloner.Clone(this.options.Engine);
        engineSettings.AutoReconnectAfterKickedFromServer = true;
        defaultDownloadSettings = SettingsCloner.Clone(this.options.DefaultDownload);
        var pathContext = new PathVariableContext(ConfigDir: this.options.ConfigDir);
        ServerJobSettingsResolver.NormalizeForServer(defaultDownloadSettings, pathContext);
        profileCatalog = this.options.Profiles ?? ProfileCatalog.Empty;
        jobSettingsResolver = new ServerJobSettingsResolver(defaultDownloadSettings, profileCatalog, this.options.LaunchDownloadSettings, pathContext);

        StateStore = new EngineStateStore();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (engine, clientManager) = CreateEngine();
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
                            engine.Enqueue(submission.Job, submission.Settings!, submission.SourceJobId);
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
                SockseekLog.Daemon.Error(ex, "Engine instance failed, restarting supervisor loop");
                StateStore.MarkActiveJobsInfrastructureFailed(
                    SockseekLog.ExceptionSummary(ex),
                    SockseekLog.ExceptionDetail(ex));
                continue;
            }
            finally
            {
                StateStore.DetachEngine(engine);
                persistence?.DetachEngine(engine);
                lock (engineGate)
                {
                    if (ReferenceEquals(currentEngine, engine))
                        currentEngine = null;
                }
                await engine.DisposeAsync();
                clientManager.Dispose();
            }
        }
    }

    public ServerInfoDto GetInfo()
    {
        string version = typeof(EngineSupervisor).Assembly.GetName().Version?.ToString() ?? "dev";
        return new ServerInfoDto(options.Name, version, StartedAtUtc, LiveProtocol.Version);
    }

    public ServerStatusDto GetStatus()
    {
        SoulseekClientStates clientState;
        lock (engineGate)
            clientState = currentEngine?.ClientState ?? SoulseekClientStates.None;

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

    public Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateExtractJob(request), request.Options, ct);

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
        ApplyDraftJobOptions(job, request.Jobs);
        return SubmitJobAsync(job, request.Options, ct);
    }

    private void ApplyDraftJobOptions(JobList jobList, IReadOnlyList<JobDraftDto> drafts)
    {
        for (int i = 0; i < jobList.Jobs.Count && i < drafts.Count; i++)
            ApplyDraftJobOptions(jobList.Jobs[i], drafts[i]);
    }

    private void ApplyDraftJobOptions(Job job, JobDraftDto draft)
    {
        if (DraftDownloadSettings(draft) is { } patch)
            jobSettingsResolver.SetJobOptions(job.Id, new SubmissionOptionsDto(DownloadSettings: patch));

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
            _ => null,
        };

    private async Task<JobSummaryDto> SubmitJobAsync(Job job, SubmissionOptionsDto? options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        jobSettingsResolver.SetWorkflowOptions(job.WorkflowId, options);

        var settings = jobSettingsResolver.Resolve(defaultDownloadSettings, job);

        if (ContainsLoginRequiredJob(job, defaultDownloadSettings, settings) && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");

        job.EnsureDisplayId();
        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(job, settings), ct);

        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    private bool ContainsLoginRequiredJob(Job job, DownloadSettings inheritedSettings, DownloadSettings? resolvedSettings = null)
    {
        var effectiveSettings = resolvedSettings ?? jobSettingsResolver.Resolve(inheritedSettings, job);

        return job switch
        {
            JobList list => list.Jobs.Any(child => ContainsLoginRequiredJob(child, effectiveSettings)),
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

    public SearchResultSnapshotDto<FileCandidateDto>? GetFileResults(Guid jobId)
        => GetFileResults(jobId, null);

    public SearchResultSnapshotDto<FileCandidateDto>? GetFileResults(Guid jobId, FileSearchProjectionRequestDto? projection)
    {
        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var fileProjection = projection?.SongQuery != null
                ? new FileSearchProjection(
                    JobRequestMapper.ToSongQuery(projection.SongQuery),
                    projection.IncludeFullResults)
                : searchJob.DefaultFileProjection
                    ?? new FileSearchProjection(new SongQuery { Title = searchJob.QueryText });
            var snapshot = searchJob.GetSortedTrackCandidates(fileProjection, searchJob.Config.Search, GetCurrentEngineUserSuccessCounts());
            return new SearchResultSnapshotDto<FileCandidateDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(ToFileCandidateDto).ToList());
        }

        var songJob = GetRuntimeJob<SongJob>(jobId);
        if (songJob == null)
            return null;

        return new SearchResultSnapshotDto<FileCandidateDto>(
            Revision: 0,
            IsComplete: songJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: songJob.Candidates?.Select(ToFileCandidateDto).ToList() ?? []);
    }

    internal SearchResultSnapshotDto<FileCandidateDto> ProjectHistoricalFileResults(
        IReadOnlyList<SearchProjectionInput> inputs,
        PersistedSearchMetadata metadata,
        FileSearchProjectionRequestDto projection)
    {
        var query = projection.SongQuery != null
            ? JobRequestMapper.ToSongQuery(projection.SongQuery)
            : new SongQuery { Title = metadata.Query };
        var items = SearchResultProjector.SortedTrackCandidates(
            inputs,
            query,
            defaultDownloadSettings.Search,
            GetCurrentEngineUserSuccessCounts(),
            useInfer: false,
            includeFullResults: projection.IncludeFullResults);
        return new SearchResultSnapshotDto<FileCandidateDto>(
            checked((int)metadata.Revision),
            metadata.IsComplete,
            items.Select(ToFileCandidateDto).ToArray(),
            metadata.ResultPersistenceState,
            metadata.ResultsPrunedAtUtc);
    }

    internal SearchResultSnapshotDto<AlbumFolderDto> ProjectHistoricalFolderResults(
        IReadOnlyList<SearchProjectionInput> inputs,
        PersistedSearchMetadata metadata,
        AlbumQueryDto query,
        bool includeFiles)
    {
        var folders = SearchResultProjector.AlbumFolders(
            inputs,
            JobRequestMapper.ToAlbumQuery(query),
            defaultDownloadSettings.Search,
            GetCurrentEngineUserSuccessCounts());
        return new SearchResultSnapshotDto<AlbumFolderDto>(
            checked((int)metadata.Revision), metadata.IsComplete,
            folders.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToArray(),
            metadata.ResultPersistenceState, metadata.ResultsPrunedAtUtc);
    }

    internal SearchResultSnapshotDto<AggregateTrackCandidateDto> ProjectHistoricalAggregateTracks(
        IReadOnlyList<SearchProjectionInput> inputs,
        PersistedSearchMetadata metadata,
        SongQueryDto query,
        bool includeCandidates)
    {
        var tracks = SearchResultProjector.AggregateTracks(
            inputs,
            JobRequestMapper.ToSongQuery(query),
            defaultDownloadSettings.Search,
            GetCurrentEngineUserSuccessCounts());
        return new SearchResultSnapshotDto<AggregateTrackCandidateDto>(
            checked((int)metadata.Revision), metadata.IsComplete,
            tracks.Select(song => new AggregateTrackCandidateDto(
                ToSongQuery(song.Query), song.ItemName,
                includeCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToArray(),
            metadata.ResultPersistenceState, metadata.ResultsPrunedAtUtc);
    }

    internal SearchResultSnapshotDto<AggregateAlbumCandidateDto> ProjectHistoricalAggregateAlbums(
        IReadOnlyList<SearchProjectionInput> inputs,
        PersistedSearchMetadata metadata,
        AlbumQueryDto query,
        bool includeFolders)
    {
        var albumQuery = JobRequestMapper.ToAlbumQuery(query);
        var folders = SearchResultProjector.AlbumFolders(
            inputs, albumQuery, defaultDownloadSettings.Search, GetCurrentEngineUserSuccessCounts(),
            ignoreStringSortConditions: true,
            sortMode: FolderSortMode.DeterministicUnranked);
        var albums = SearchResultProjector.AggregateAlbums(folders, albumQuery, defaultDownloadSettings.Search);
        return new SearchResultSnapshotDto<AggregateAlbumCandidateDto>(
            checked((int)metadata.Revision), metadata.IsComplete,
            albums.Select(album => new AggregateAlbumCandidateDto(
                ToAlbumQuery(album.Query), album.ItemName,
                includeFolders ? album.Results.Select(folder => ToAlbumFolderDto(folder, includeFiles: true)).ToList() : null)).ToArray(),
            metadata.ResultPersistenceState, metadata.ResultsPrunedAtUtc);
    }

    public SearchResultSnapshotDto<AlbumFolderDto>? GetFolderResults(Guid jobId, bool includeFiles)
        => GetFolderResults(jobId, null, includeFiles);

    public SearchResultSnapshotDto<AlbumFolderDto>? GetFolderResults(Guid jobId, FolderSearchProjectionRequestDto request)
        => GetFolderResults(jobId, request.AlbumQuery, request.IncludeFiles);

    private SearchResultSnapshotDto<AlbumFolderDto>? GetFolderResults(Guid jobId, AlbumQueryDto? albumQuery, bool includeFiles)
    {
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
            return new SearchResultSnapshotDto<AlbumFolderDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToList());
        }

        var albumJob = GetRuntimeJob<AlbumJob>(jobId);
        if (albumJob == null)
            return null;

        var folders = JobRequestMapper.ProjectAlbumJobFolders(albumJob, GetCurrentEngineUserSuccessCounts());
        return new SearchResultSnapshotDto<AlbumFolderDto>(
            Revision: 0,
            IsComplete: albumJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: folders.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToList());
    }

    public SearchResultSnapshotDto<AggregateTrackCandidateDto>? GetAggregateTrackResults(Guid jobId)
        => GetAggregateTrackResults(jobId, null);

    public SearchResultSnapshotDto<AggregateTrackCandidateDto>? GetAggregateTrackResults(Guid jobId, AggregateTrackProjectionRequestDto? projection)
    {
        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var aggregateProjection = projection?.SongQuery != null
                ? new AggregateTrackProjection(JobRequestMapper.ToSongQuery(projection.SongQuery))
                : searchJob.DefaultAggregateTrackProjection
                    ?? (searchJob.DefaultFileProjection is { } fileProjection
                        ? new AggregateTrackProjection(fileProjection.Query)
                        : new AggregateTrackProjection(new SongQuery { Title = searchJob.QueryText }));
            bool includeCandidates = projection?.IncludeCandidates ?? false;
            var snapshot = searchJob.GetAggregateTracks(aggregateProjection, searchJob.Config.Search, GetCurrentEngineUserSuccessCounts());
            return new SearchResultSnapshotDto<AggregateTrackCandidateDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(song => new AggregateTrackCandidateDto(
                    ToSongQuery(song.Query),
                    song.ItemName,
                    includeCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList());
        }

        var aggregateJob = GetRuntimeJob<AggregateJob>(jobId);
        if (aggregateJob == null)
            return null;

        bool includeAggregateCandidates = projection?.IncludeCandidates ?? false;
        return new SearchResultSnapshotDto<AggregateTrackCandidateDto>(
            Revision: 0,
            IsComplete: aggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: aggregateJob.Songs.Select(song => new AggregateTrackCandidateDto(
                ToSongQuery(song.Query),
                song.ItemName,
                includeAggregateCandidates ? song.Candidates?.Select(ToFileCandidateDto).ToList() : null)).ToList());
    }

    public SearchResultSnapshotDto<AggregateAlbumCandidateDto>? GetAggregateAlbumResults(Guid jobId)
        => GetAggregateAlbumResults(jobId, null);

    public SearchResultSnapshotDto<AggregateAlbumCandidateDto>? GetAggregateAlbumResults(Guid jobId, AggregateAlbumProjectionRequestDto? projection)
    {
        var searchJob = GetRuntimeJob<SearchJob>(jobId);
        if (searchJob?.Config != null)
        {
            var aggregateProjection = projection?.AlbumQuery != null
                ? new AggregateAlbumProjection(JobRequestMapper.ToAlbumQuery(projection.AlbumQuery))
                : searchJob.DefaultAggregateAlbumProjection
                    ?? (searchJob.DefaultFolderProjection is { } folderProjection
                        ? new AggregateAlbumProjection(folderProjection.Query)
                        : null);
            if (aggregateProjection == null)
                throw new ArgumentException("Aggregate album projection requires an album query.");

            bool includeFolders = projection?.IncludeFolders ?? false;
            var snapshot = searchJob.GetAggregateAlbums(aggregateProjection, searchJob.Config.Search);
            return new SearchResultSnapshotDto<AggregateAlbumCandidateDto>(
                snapshot.Revision,
                snapshot.IsComplete,
                snapshot.Items.Select(album => new AggregateAlbumCandidateDto(
                    ToAlbumQuery(album.Query),
                    album.ItemName,
                    includeFolders ? album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true)).ToList() : null)).ToList());
        }

        var albumAggregateJob = GetRuntimeJob<AlbumAggregateJob>(jobId);
        if (albumAggregateJob == null)
            return null;

        bool includeAggregateFolders = projection?.IncludeFolders ?? false;
        return new SearchResultSnapshotDto<AggregateAlbumCandidateDto>(
            Revision: 0,
            IsComplete: albumAggregateJob.LifecycleState is not (JobLifecycleState.Pending or JobLifecycleState.Running),
            Items: albumAggregateJob.Albums.Select(album => new AggregateAlbumCandidateDto(
                ToAlbumQuery(album.Query),
                album.ItemName,
                includeAggregateFolders ? album.Results.Select(f => ToAlbumFolderDto(f, includeFiles: true)).ToList() : null)).ToList());
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid sourceJobId, RetrieveFolderRequestDto request, CancellationToken ct)
    {
        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalRetrieveFolderAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var folder = FindAlbumFolderForRetrieval(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        var retrieveJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath };
        retrieveJob.WorkflowId = sourceJob.WorkflowId;
        retrieveJob.EnsureDisplayId();
        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(retrieveJob, sourceJob.Config, SourceJobId: sourceJobId), ct);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    private async Task<JobSummaryDto?> StartHistoricalRetrieveFolderAsync(
        Guid sourceJobId,
        RetrieveFolderRequestDto request,
        CancellationToken ct)
    {
        var historical = await ResolveHistoricalFolderAsync(sourceJobId, request.Folder, request.AlbumQuery, ct).ConfigureAwait(false);
        if (historical == null)
            return null;
        var retrieveJob = new RetrieveFolderJob(historical.Value.Folder)
        {
            ItemName = historical.Value.Folder.FolderPath,
            WorkflowId = historical.Value.Job.WorkflowId,
        };
        var settings = jobSettingsResolver.ResolveFollowUp(retrieveJob, options: null);
        retrieveJob.EnsureDisplayId();
        await submissionChannel.Writer.WriteAsync(
            new QueuedSubmission(retrieveJob, settings, SourceJobId: sourceJobId), ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(retrieveJob.Id) ?? BuildSubmittedJobSummary(retrieveJob, sourceJobId);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid sourceJobId, StartFileDownloadsRequestDto request, CancellationToken ct)
    {
        if (request.Files.Count == 0)
            throw new ArgumentException("At least one file is required.");

        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalFileDownloadsAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var summaries = new List<JobSummaryDto>();

        if (sourceJob is SongJob manualSong && manualSong.IsAwaitingSelection)
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
            await submissionChannel.Writer.WriteAsync(QueuedSubmission.Resume(manualSong), ct);
            return new List<JobSummaryDto> { StateStore.GetJobSummary(manualSong.Id) ?? BuildSubmittedJobSummary(manualSong, sourceJobId) };
        }

        foreach (var file in request.Files)
        {
            var candidate = FindFileCandidate(sourceJob, file);
            if (candidate == null)
                throw new ArgumentException("Requested file was not found in this job's file candidates.");

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

            var followUpSongJob = new SongJob(new SongQuery(songQuery))
            {
                ResolvedTarget = candidate,
                ItemName = sourceJob.ItemName,
            };

            var followUpSettings = jobSettingsResolver.ResolveFollowUp(followUpSongJob, request.Options);
            summaries.Add(await SubmitFollowUpJobAsync(sourceJobId, sourceJob, followUpSongJob, followUpSettings, request.Options, isolateOptions: true, ct));
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
                result.Username,
                result.RemoteFilename,
                result.SizeBytes,
                result.BitRate,
                result.BitDepth,
                result.ResponseFileCount,
                result.SampleRate,
                result.DurationSeconds,
                result.Extension,
                result.UploadSpeed,
                result.HasFreeUploadSlot,
                DeserializeFileAttributes(result.AttributesJson));
            var query = Searcher.InferSongQuery(
                candidate.Filename,
                new SongQuery { Title = lookup.Metadata.Query });
            var followUp = new SongJob(query)
            {
                ResolvedTarget = candidate,
                ItemName = persistedJob.ItemName,
                WorkflowId = persistedJob.WorkflowId,
            };
            var settings = jobSettingsResolver.ResolveFollowUp(followUp, request.Options);
            jobSettingsResolver.SetJobOptions(followUp.Id, request.Options);
            followUp.EnsureDisplayId();
            await submissionChannel.Writer.WriteAsync(
                new QueuedSubmission(followUp, settings, SourceJobId: sourceJobId), ct).ConfigureAwait(false);
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
        var sourceJob = GetRuntimeJob<Job>(sourceJobId);
        if (sourceJob?.Config == null)
            return await StartHistoricalFolderDownloadAsync(sourceJobId, request, ct).ConfigureAwait(false);

        var folder = FindAlbumFolder(sourceJob, request.Folder, request.AlbumQuery);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this job's album candidates.");

        folder = JobRequestMapper.ApplySelectedFolderSnapshot(folder, request);
        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);

        var albumQuery = request.AlbumQuery != null
            ? JobRequestMapper.ToAlbumQuery(request.AlbumQuery)
            : sourceJob switch
            {
                SearchJob searchJob => searchJob.DefaultFolderProjection?.Query,
                AlbumJob album => album.Query,
                AlbumAggregateJob aggregate => aggregate.Query,
                _ => null,
            };

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

        string? itemName = sourceJob.ItemName;
        if (sourceJob is SearchJob { DefaultAggregateAlbumProjection: not null } && !string.IsNullOrWhiteSpace(folder.FolderPath))
            itemName = Utils.GetBaseNameSlsk(folder.FolderPath);

        var albumJob = new AlbumJob(new AlbumQuery(albumQuery))
        {
            ResolvedTarget = folder,
            ItemName = itemName,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
        };
        JobRequestMapper.ApplyFolderDownloadSelection(albumJob, request.Selection);

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
        var folder = JobRequestMapper.ApplySelectedFolderSnapshot(historical.Value.Folder, request);
        folder = JobRequestMapper.ApplyFolderDownloadSelection(folder, request.Selection);
        var albumJob = new AlbumJob(new AlbumQuery(historical.Value.Query))
        {
            ResolvedTarget = folder,
            ItemName = historical.Value.Job.ItemName,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
            WorkflowId = historical.Value.Job.WorkflowId,
        };
        JobRequestMapper.ApplyFolderDownloadSelection(albumJob, request.Selection);
        var settings = jobSettingsResolver.ResolveFollowUp(albumJob, request.Options);
        jobSettingsResolver.SetJobOptions(albumJob.Id, request.Options);
        albumJob.EnsureDisplayId();
        await submissionChannel.Writer.WriteAsync(
            new QueuedSubmission(albumJob, settings, SourceJobId: sourceJobId), ct).ConfigureAwait(false);
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
        var job = await persistence.JobHistory.GetJobAsync(sourceJobId, ct).ConfigureAwait(false);
        var metadata = await persistence.SearchHistory.GetMetadataAsync(sourceJobId, ct).ConfigureAwait(false);
        if (job == null || metadata == null)
            return null;
        if (metadata.ResultPersistenceState is "Pruned" or "NotPersisted")
            throw new ArgumentException($"Cannot use this historical folder because its result data is {metadata.ResultPersistenceState.ToLowerInvariant()}.");
        var defaultProjection = HistoricalJobDtoMapper.DefaultFolderProjection(job);
        var queryDto = requestedQuery ?? defaultProjection?.AlbumQuery
            ?? throw new ArgumentException("Historical folder operations require an album query.");
        var inputs = new List<SearchProjectionInput>();
        await foreach (var input in persistence.SearchHistory
            .ReadProjectionInputsAsync(sourceJobId, ct)
            .ConfigureAwait(false))
            inputs.Add(input);
        var query = JobRequestMapper.ToAlbumQuery(queryDto);
        var folder = SearchResultProjector.AlbumFolders(
                inputs, query, defaultDownloadSettings.Search, GetCurrentEngineUserSuccessCounts())
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

    private (DownloadEngine Engine, SoulseekClientManager ClientManager) CreateEngine()
    {
        var clientManager = new SoulseekClientManager(engineSettings, options.ClientFactory?.Invoke(engineSettings));
        clientManager.StateChanged += state =>
            StateStore.UpdateDaemonRuntime(ToSoulseekClientStatusDto(state), restartCount);
        var engine = new DownloadEngine(engineSettings, clientManager, jobSettingsResolver);
        persistence?.AttachEngine(engine);
        StateStore.AttachEngine(engine);
        lock (engineGate)
            currentEngine = engine;
        StateStore.UpdateDaemonRuntime(ToSoulseekClientStatusDto(clientManager.State), restartCount);
        EngineCreated?.Invoke(engine);
        return (engine, clientManager);
    }

    private ConcurrentDictionary<string, int> GetCurrentEngineUserSuccessCounts()
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.UserSuccessCounts ?? new ConcurrentDictionary<string, int>();
    }

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
            result.Length);

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate)
        => new(
            new FileCandidateRefDto(candidate.Username, candidate.Filename),
            candidate.Username,
            candidate.Filename,
            new PeerInfoDto(candidate.Username, candidate.HasFreeUploadSlot, candidate.UploadSpeed),
            candidate.Size,
            candidate.BitRate,
            candidate.SampleRate,
            candidate.Length,
            candidate.Extension,
            candidate.Attributes?.Select(x => new FileAttributeDto(x.Type, x.Value)).ToList());

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

    private static SongQueryDto ToSongQuery(SongQuery query)
        => new(Optional(query.Artist), Optional(query.Title), Optional(query.Album), Optional(query.URI), Optional(query.Length), query.ArtistMaybeWrong);

    private static AlbumQueryDto ToAlbumQuery(AlbumQuery query)
        => new(Optional(query.Artist), Optional(query.Album), Optional(query.SearchHint), Optional(query.URI), query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    private bool CanAcceptLoginRequiredJobs()
        => !string.IsNullOrWhiteSpace(engineSettings.MockFilesDir)
        || engineSettings.UseRandomLogin
        || (!string.IsNullOrWhiteSpace(engineSettings.Username)
            && !string.IsNullOrWhiteSpace(engineSettings.Password));

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
            return JobRequestMapper.FindProjectedAlbumFolder(albumJob, folderRef, GetCurrentEngineUserSuccessCounts())
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

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase);

    private FileCandidate? FindFileCandidate(Job sourceJob, FileCandidateRefDto candidateRef)
    {
        static bool Matches(FileCandidate candidate, FileCandidateRefDto candidateRef)
            => string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal);

        if (sourceJob is SearchJob searchJob)
            return FindSearchFileCandidate(searchJob, candidateRef);

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

    private FileCandidate? FindSearchFileCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
    {
        if (searchJob.Config == null)
            return null;

        var trackCandidate = searchJob.GetSortedTrackCandidates(searchJob.Config.Search, GetCurrentEngineUserSuccessCounts())
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
        if (ShouldPropagateSourceMutationToFollowUp(sourceJob))
            followUpJob.CopySourceMutationFrom(sourceJob);
        if (isolateOptions)
            jobSettingsResolver.SetJobOptions(followUpJob.Id, options);
        followUpJob.EnsureDisplayId();
        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(followUpJob, settings, SourceJobId: sourceJobId), ct);
        return StateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId);
    }

    private static bool ShouldPropagateSourceMutationToFollowUp(Job sourceJob)
        => sourceJob is not AlbumAggregateJob;

    private sealed record PersistedFileAttribute(int Code, string Name, int Value);

    private sealed record QueuedSubmission(Job Job, DownloadSettings? Settings, bool IsResume = false, Guid? SourceJobId = null)
    {
        public static QueuedSubmission Resume(Job job) => new(job, null, true);
    }
}
