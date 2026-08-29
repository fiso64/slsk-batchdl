using System.Text.Json;
using System.Collections.Concurrent;
using Sockseek.Api;

namespace Sockseek.Cli;

internal sealed class RemoteCliBackend : ICliBackend, IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly SockseekApiClient api;
    private readonly SockseekLiveClient live;
    private readonly ConcurrentDictionary<Guid, byte> workflowSubscriptions = [];

    public event Action<DaemonClientUpdate>? StateUpdated;
    public event Action<ActivityEventDto>? ActivityReceived;
    public event Action<StateSnapshotDto>? LiveSnapshotApplied;

    public DaemonClientStore ClientStore => live.Store;

    internal static JsonSerializerOptions CreateJsonOptions()
        => SockseekApiJson.CreateSerializerOptions();

    public RemoteCliBackend(string serverUrl)
    {
        var baseUri = SockseekApiClient.NormalizeServerUrl(serverUrl);
        http = new HttpClient { BaseAddress = baseUri };
        var jsonOptions = CreateJsonOptions();
        api = new SockseekApiClient(http, jsonOptions);
        live = new SockseekLiveClient(http, ownsHttp: false, jsonOptions);
        live.Updated += HandleStateUpdate;
        live.ActivityReceived += HandleActivity;
        live.SnapshotApplied += snapshot => LiveSnapshotApplied?.Invoke(snapshot);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await live.DisposeAsync();
        http.Dispose();
    }

    public async Task<JobSummaryDto> SubmitExtractJobAsync(SubmitExtractJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitExtractJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitSearchJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitTrackSearchJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumSearchJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitSongJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAggregateJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitAlbumAggregateJobAsync(request with { Options = options }, ct), ct);

    public async Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct = default)
        => await SubmitAndSubscribeAsync(request.Options, options => api.SubmitJobListAsync(request with { Options = options }, ct), ct);

    public Task SubscribeWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => SubscribeWorkflowCoreAsync(workflowId, ct);

    public Task SubscribeAllAsync(CancellationToken ct = default)
        => SubscribeAllCoreAsync(ct);

    private async Task<JobSummaryDto> SubmitAndSubscribeAsync(
        SubmissionOptionsDto? options,
        Func<SubmissionOptionsDto, Task<JobSummaryDto>> submit,
        CancellationToken ct)
    {
        var workflowId = options?.WorkflowId ?? Guid.NewGuid();
        var scopedOptions = (options ?? new SubmissionOptionsDto()) with { WorkflowId = workflowId };

        if (live.Mode != LiveSubscriptionMode.Daemon)
        {
            bool reusingSubscription = workflowSubscriptions.ContainsKey(workflowId);
            await SubscribeWorkflowAsync(workflowId, ct);
            if (reusingSubscription)
                await live.RefreshWorkflowAsync(workflowId, ct);
        }
        var summary = await submit(scopedOptions);
        if (live.Mode != LiveSubscriptionMode.Daemon && summary.WorkflowId != workflowId)
            await SubscribeWorkflowAsync(summary.WorkflowId, ct);
        return summary;
    }

    private async Task SubscribeWorkflowCoreAsync(Guid workflowId, CancellationToken ct = default)
    {
        await live.StartWorkflowAsync(workflowId, ct);
        workflowSubscriptions.TryAdd(workflowId, 0);
    }

    private async Task SubscribeAllCoreAsync(CancellationToken ct = default)
    {
        await live.StartDaemonAsync(ct);
    }

    public Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
        => api.GetJobsAsync(query, ct);

    public Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
        => api.GetJobDetailAsync(jobId, ct);

    public Task<JobDetailDto?> GetJobDetailByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.GetJobDetailByDisplayIdAsync(displayId, workflowId, ct);

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        return await api.GetWorkflowAsync(workflowId, ct);
    }

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetFileResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(Guid jobId, FileSearchProjectionRequestDto request, CancellationToken ct = default)
        => api.GetFileResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
        => api.GetFolderResultsAsync(jobId, includeFiles, ct);

    public Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(Guid jobId, FolderSearchProjectionRequestDto request, CancellationToken ct = default)
        => api.GetFolderResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetAggregateTrackResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, AggregateTrackProjectionRequestDto request, CancellationToken ct = default)
        => api.GetAggregateTrackResultsAsync(jobId, request, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
        => api.GetAggregateAlbumResultsAsync(jobId, ct);

    public Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, AggregateAlbumProjectionRequestDto request, CancellationToken ct = default)
        => api.GetAggregateAlbumResultsAsync(jobId, request, ct);

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        await PrepareFollowUpSubscriptionAsync(searchJobId, ct);
        return await api.StartRetrieveFolderAsync(searchJobId, request, ct);
    }

    public async Task<RetrieveFolderJobPayloadDto?> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        await PrepareFollowUpSubscriptionAsync(searchJobId, ct);
        return await api.RetrieveFolderAndWaitAsync(searchJobId, request, ct);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartFileDownloadsAsync(Guid searchJobId, StartFileDownloadsRequestDto request, CancellationToken ct = default)
    {
        await PrepareFollowUpSubscriptionAsync(searchJobId, ct);
        return await api.StartFileDownloadsAsync(searchJobId, request, ct);
    }

    public async Task<JobSummaryDto?> StartFolderDownloadAsync(Guid searchJobId, StartFolderDownloadRequestDto request, CancellationToken ct = default)
    {
        await PrepareFollowUpSubscriptionAsync(searchJobId, ct);
        return await api.StartFolderDownloadAsync(searchJobId, request, ct);
    }

    private async Task PrepareFollowUpSubscriptionAsync(Guid sourceJobId, CancellationToken ct)
    {
        if (live.Mode != LiveSubscriptionMode.Workflow)
            return;

        var source = await api.GetJobDetailAsync(sourceJobId, ct);
        if (source != null)
            await live.RefreshWorkflowAsync(source.Summary.WorkflowId, ct);
    }

    public Task<bool> CompleteManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => api.CompleteManualSelectionAsync(jobId, ct);

    public Task<bool> SkipManualSelectionAsync(Guid jobId, CancellationToken ct = default)
        => api.SkipManualSelectionAsync(jobId, ct);

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
        => api.CancelJobAsync(jobId, ct);

    public Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.CancelJobByDisplayIdAsync(displayId, workflowId, ct);

    public Task<int> CancelAllJobsAsync(CancellationToken ct = default)
        => api.CancelAllJobsAsync(ct);

    public Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => api.CancelWorkflowAsync(workflowId, ct);

    public Task<bool> TryNextCandidateAsync(Guid jobId, CancellationToken ct = default)
        => api.TryNextCandidateAsync(jobId, ct);

    public Task<bool> TryNextCandidateByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
        => api.TryNextCandidateByDisplayIdAsync(displayId, workflowId, ct);

    private void HandleStateUpdate(DaemonClientUpdate update)
        => StateUpdated?.Invoke(update);

    private void HandleActivity(ActivityEventDto activity)
        => ActivityReceived?.Invoke(activity);
}
