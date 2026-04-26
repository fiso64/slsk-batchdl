using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Sldl.Server;

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

    private DownloadEngine? currentEngine;
    private int restartCount;

    public event Action<DownloadEngine>? EngineCreated;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public EngineStateStore StateStore { get; }

    public EngineSupervisor(IOptions<ServerOptions> options)
    {
        this.options = options.Value;

        engineSettings = SettingsCloner.Clone(this.options.Engine);
        defaultDownloadSettings = SettingsCloner.Clone(this.options.DefaultDownload);
        ServerJobSettingsResolver.NormalizeForServer(defaultDownloadSettings);
        profileCatalog = this.options.Profiles ?? ProfileCatalog.Empty;
        jobSettingsResolver = new ServerJobSettingsResolver(defaultDownloadSettings, profileCatalog, this.options.LaunchDownloadSettings);

        StateStore = new EngineStateStore();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var engine = CreateEngine();
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
                        engine.Enqueue(submission.Job, submission.Settings);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref restartCount);
                Logger.Error($"Engine instance failed, restarting supervisor loop: {ex.Message}");
                StateStore.MarkActiveJobsInfrastructureFailed(ex.Message);
                StateStore.DetachEngine(engine);
                lock (engineGate)
                {
                    if (ReferenceEquals(currentEngine, engine))
                        currentEngine = null;
                }
                continue;
            }
        }
    }

    public ServerInfoDto GetInfo()
    {
        string version = typeof(EngineSupervisor).Assembly.GetName().Version?.ToString() ?? "dev";
        return new ServerInfoDto(options.Name, version, StartedAtUtc);
    }

    public ServerStatusDto GetStatus()
    {
        bool isConnectedAndLoggedIn;
        lock (engineGate)
            isConnectedAndLoggedIn = currentEngine?.IsConnectedAndLoggedIn == true;

        var stats = StateStore.GetStatistics();
        return new ServerStatusDto(
            isConnectedAndLoggedIn,
            stats.TotalJobCount,
            stats.ActiveJobCount,
            stats.TotalWorkflowCount,
            stats.ActiveWorkflowCount,
            restartCount);
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

    public async Task<JobSummaryDto> SubmitJobAsync(SubmitJobRequestDto request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var job = JobRequestMapper.CreateJob(request.Job);
        if (request.Options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        jobSettingsResolver.SetWorkflowOptions(job.WorkflowId, request.Options);

        var settings = jobSettingsResolver.Resolve(defaultDownloadSettings, job);

        if (settings.NeedLogin && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");

        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(job, settings), ct);

        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    public bool CancelJob(Guid jobId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        var job = engine?.GetJob(jobId);
        if (job == null)
            return false;

        job.Cancel();
        return true;
    }

    public int CancelWorkflow(Guid workflowId)
    {
        DownloadEngine? engine;
        lock (engineGate)
            engine = currentEngine;

        return engine?.CancelWorkflow(workflowId) ?? 0;
    }

    public IReadOnlyList<SearchRawResultDto>? GetSearchRawResults(Guid jobId, long afterSequence)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob == null)
            return null;

        return searchJob.RawSnapshot(afterSequence)
            .Select(ToSearchRawResultDto)
            .ToList();
    }

    public SearchResultSnapshotDto<FileCandidateDto>? GetTrackResults(Guid jobId)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return null;

        var snapshot = searchJob.GetSortedTrackCandidates(searchJob.Config.Search, GetCurrentEngineUserSuccessCounts());
        return new SearchResultSnapshotDto<FileCandidateDto>(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(ToFileCandidateDto).ToList());
    }

    public SearchResultSnapshotDto<AlbumFolderDto>? GetAlbumResults(Guid jobId, bool includeFiles)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return null;

        var snapshot = searchJob.GetAlbumFolders(searchJob.Config.Search);
        return new SearchResultSnapshotDto<AlbumFolderDto>(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(folder => ToAlbumFolderDto(folder, includeFiles)).ToList());
    }

    public SearchResultSnapshotDto<AggregateTrackCandidateDto>? GetAggregateTrackResults(Guid jobId)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return null;

        var snapshot = searchJob.GetAggregateTracks(searchJob.Config.Search, GetCurrentEngineUserSuccessCounts());
        return new SearchResultSnapshotDto<AggregateTrackCandidateDto>(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(song => new AggregateTrackCandidateDto(ToSongQuery(song.Query), song.ItemName)).ToList());
    }

    public SearchResultSnapshotDto<AggregateAlbumCandidateDto>? GetAggregateAlbumResults(Guid jobId)
    {
        var searchJob = StateStore.GetJob<SearchJob>(jobId);
        if (searchJob?.Config == null)
            return null;

        var snapshot = searchJob.GetAggregateAlbums(searchJob.Config.Search);
        return new SearchResultSnapshotDto<AggregateAlbumCandidateDto>(
            snapshot.Revision,
            snapshot.IsComplete,
            snapshot.Items.Select(album => new AggregateAlbumCandidateDto(ToAlbumQuery(album.Query), album.ItemName)).ToList());
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct)
    {
        var searchJob = StateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return null;

        var folder = FindAlbumFolder(searchJob, request.Folder);
        if (folder == null)
            throw new ArgumentException("Requested folder was not found in this search job.");

        var retrieveJob = new RetrieveFolderJob(folder) { ItemName = folder.FolderPath };
        return await SubmitFollowUpJobAsync(searchJobId, searchJob, retrieveJob, searchJob.Config, null, ct);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartExtractedResultAsync(
        Guid extractJobId,
        StartExtractedResultRequestDto request,
        CancellationToken ct)
    {
        var extractJob = StateStore.GetJob<ExtractJob>(extractJobId);
        if (extractJob?.Config == null)
            return null;

        if (extractJob.Result == null)
            return [];

        var resultJob = request.Mode switch
        {
            ServerProtocol.ExtractedResultStartModes.Normal => extractJob.Result,
            ServerProtocol.ExtractedResultStartModes.AlbumSearch => ToInteractiveJob(extractJob.Result),
            _ => throw new ArgumentException($"Unsupported extracted result start mode '{request.Mode}'."),
        };
        AssignWorkflowId(resultJob, extractJob.WorkflowId);
        PrepareDetachedExtractions(resultJob);

        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(resultJob, extractJob.Config), ct);

        return [StateStore.GetJobSummary(resultJob.Id) ?? BuildSubmittedJobSummary(resultJob)];
    }

    public async Task<JobSummaryDto?> StartSongDownloadAsync(Guid searchJobId, StartSongDownloadRequestDto request, CancellationToken ct)
    {
        var searchJob = StateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return null;

        var candidate = FindTrackCandidate(searchJob, request.Candidate);
        if (candidate == null)
            throw new ArgumentException("Requested candidate was not found in this search job.");

        var songJob = new SongJob(new SongQuery(searchJob.Query))
        {
            ResolvedTarget = candidate,
            ItemName = searchJob.ItemName,
        };

        var settings = SettingsCloner.Clone(searchJob.Config);
        if (!string.IsNullOrWhiteSpace(request.OutputParentDir))
            settings.Output.ParentDir = request.OutputParentDir;
        ServerJobSettingsResolver.NormalizeForServer(settings);

        return await SubmitFollowUpJobAsync(searchJobId, searchJob, songJob, settings, request.OutputParentDir, ct);
    }

    public async Task<JobSummaryDto?> StartAlbumDownloadAsync(Guid searchJobId, StartAlbumDownloadRequestDto request, CancellationToken ct)
    {
        var searchJob = StateStore.GetJob<SearchJob>(searchJobId);
        if (searchJob?.Config == null)
            return null;

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
        };

        var settings = SettingsCloner.Clone(searchJob.Config);
        if (!string.IsNullOrWhiteSpace(request.OutputParentDir))
            settings.Output.ParentDir = request.OutputParentDir;
        ServerJobSettingsResolver.NormalizeForServer(settings);

        return await SubmitFollowUpJobAsync(searchJobId, searchJob, albumJob, settings, request.OutputParentDir, ct);
    }

    private DownloadEngine CreateEngine()
    {
        var clientManager = new SoulseekClientManager(engineSettings);
        var engine = new DownloadEngine(engineSettings, clientManager, jobSettingsResolver);
        StateStore.AttachEngine(engine);
        lock (engineGate)
            currentEngine = engine;
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

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? visualParentJobId = null)
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
            new PresentationHintsDto(ServerProtocol.PresentationDisplayModes.Node, visualParentJobId, job.DisplayId, null),
            []);

    private static Job ToInteractiveJob(Job job)
    {
        switch (job)
        {
            case AlbumJob albumJob when !albumJob.Query.IsDirectLink:
                var searchJob = new SearchJob(albumJob.Query);
                searchJob.CopySharedFieldsFrom(albumJob);
                return searchJob;

            case JobList jobList:
                var transformed = new JobList(jobList.ItemName, jobList.Jobs.Select(ToInteractiveJob));
                transformed.CopySharedFieldsFrom(jobList);
                return transformed;

            default:
                return job;
        }
    }

    private static void PrepareDetachedExtractions(Job job)
    {
        switch (job)
        {
            case ExtractJob extractJob:
                extractJob.AutoProcessResult = false;
                break;

            case JobList jobList:
                foreach (var child in jobList.Jobs)
                    PrepareDetachedExtractions(child);
                break;
        }
    }

    private static void AssignWorkflowId(Job job, Guid workflowId)
    {
        job.WorkflowId = workflowId;
        if (job is JobList jobList)
        {
            foreach (var child in jobList.Jobs)
                AssignWorkflowId(child, workflowId);
        }
    }

    private static SearchRawResultDto ToSearchRawResultDto(SearchRawResult result)
        => new(
            result.Sequence,
            result.Revision,
            result.Username,
            result.Filename,
            result.File.Size,
            result.File.BitRate,
            result.File.Length);

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
                    ToSongQuery(song.Query),
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

    private static SongQueryDto ToSongQuery(SongQuery query)
        => new(query.Artist, query.Title, query.Album, query.URI, query.Length, query.ArtistMaybeWrong, query.IsDirectLink);

    private static AlbumQueryDto ToAlbumQuery(AlbumQuery query)
        => new(query.Artist, query.Album, query.SearchHint, query.URI, query.ArtistMaybeWrong, query.IsDirectLink, query.MinTrackCount, query.MaxTrackCount);

    private bool CanAcceptLoginRequiredJobs()
        => !string.IsNullOrWhiteSpace(engineSettings.MockFilesDir)
        || engineSettings.UseRandomLogin
        || (!string.IsNullOrWhiteSpace(engineSettings.Username)
            && !string.IsNullOrWhiteSpace(engineSettings.Password));

    private AlbumFolder? FindAlbumFolder(SearchJob searchJob, AlbumFolderRefDto folderRef)
    {
        if (searchJob.Config == null || searchJob.AlbumQuery == null)
            return null;

        return searchJob.GetAlbumFolders(searchJob.Config.Search).Items.FirstOrDefault(folder =>
            string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
            && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));
    }

    private FileCandidate? FindTrackCandidate(SearchJob searchJob, FileCandidateRefDto candidateRef)
    {
        if (searchJob.Config == null)
            return null;

        return searchJob.GetSortedTrackCandidates(searchJob.Config.Search, GetCurrentEngineUserSuccessCounts())
            .Items
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal));
    }

    private async Task<JobSummaryDto> SubmitFollowUpJobAsync(
        Guid sourceJobId,
        SearchJob sourceJob,
        Job followUpJob,
        DownloadSettings settings,
        string? outputParentDir,
        CancellationToken ct)
    {
        followUpJob.WorkflowId = sourceJob.WorkflowId;
        StateStore.SetVisualParent(followUpJob.Id, sourceJobId);
        jobSettingsResolver.SetJobOutputParentDir(followUpJob.Id, outputParentDir);
        await submissionChannel.Writer.WriteAsync(new QueuedSubmission(followUpJob, settings), ct);
        return StateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId);
    }

    private sealed record QueuedSubmission(Job Job, DownloadSettings Settings);
}
