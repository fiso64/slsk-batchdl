using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Sldl.Server;

namespace Sldl.Cli;

internal sealed class RemoteCliBackend : ICliBackend, IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly HubConnection connection;
    private readonly JsonSerializerOptions jsonOptions;

    public event Action<ServerEventEnvelopeDto>? EventReceived;

    public RemoteCliBackend(string serverUrl)
    {
        var baseUri = NormalizeServerUrl(serverUrl);
        http = new HttpClient { BaseAddress = baseUri };
        jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "api/events"))
            .WithAutomaticReconnect()
            .Build();

        connection.On<ServerEventEnvelopeDto>("serverEvent", envelope =>
        {
            EventReceived?.Invoke(RehydrateEnvelope(envelope));
        });
    }

    internal static Uri NormalizeServerUrl(string serverUrl)
    {
        var value = serverUrl.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;

        var builder = new UriBuilder(value);
        if (builder.Uri.IsDefaultPort)
            builder.Port = 5030;

        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";

        return builder.Uri;
    }

    public Task StartAsync(CancellationToken ct = default)
        => connection.StartAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        http.Dispose();
    }

    public async Task<JobSummaryDto> SubmitJobAsync(SubmitJobRequestDto request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/jobs", request, jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobSummaryDto>(response, ct);
    }

    public async Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        var url = "api/jobs"
            + $"?canonicalRootsOnly={query.CanonicalRootsOnly.ToString().ToLowerInvariant()}"
            + $"&includeNonDefault={query.IncludeNonDefault.ToString().ToLowerInvariant()}"
            + QueryPart("state", query.State)
            + QueryPart("kind", query.Kind)
            + QueryPart("workflowId", query.WorkflowId?.ToString());

        return await http.GetFromJsonAsync<IReadOnlyList<JobSummaryDto>>(url, jsonOptions, ct) ?? [];
    }

    public async Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobDetailDto>(response, ct);
    }

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/workflows/{workflowId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<WorkflowDetailDto>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<FileCandidateDto>?> GetTrackResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/tracks", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<FileCandidateDto>>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetAlbumResultsAsync(Guid jobId, bool includeFiles, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/albums?includeFiles={includeFiles.ToString().ToLowerInvariant()}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AlbumFolderDto>>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/aggregate-tracks", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AggregateTrackCandidateDto>>(response, ct);
    }

    public async Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/jobs/{jobId}/results/aggregate-albums", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<SearchResultSnapshotDto<AggregateAlbumCandidateDto>>(response, ct);
    }

    public async Task<IReadOnlyList<JobSummaryDto>?> StartExtractedResultAsync(
        Guid extractJobId,
        StartExtractedResultRequestDto request,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/jobs/{extractJobId}/extracted-result/start", request, jsonOptions, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<IReadOnlyList<JobSummaryDto>>(response, ct);
    }

    public async Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
        => await PostOptionalSummaryAsync($"api/jobs/{searchJobId}/retrieve-folder", request, ct);

    public async Task<int> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default)
    {
        var summary = await StartRetrieveFolderAsync(searchJobId, request, ct);
        if (summary == null)
            return 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var detail = await GetJobDetailAsync(summary.JobId, ct);
            if (detail == null || IsActiveState(detail.Summary.State))
            {
                await Task.Delay(100, ct);
                continue;
            }

            return detail.Payload is RetrieveFolderJobPayloadDto payload
                ? payload.NewFilesFoundCount
                : 0;
        }
    }

    public async Task<JobSummaryDto?> StartSongDownloadAsync(Guid searchJobId, StartSongDownloadRequestDto request, CancellationToken ct = default)
        => await PostOptionalSummaryAsync($"api/jobs/{searchJobId}/downloads/song", request, ct);

    public async Task<JobSummaryDto?> StartAlbumDownloadAsync(Guid searchJobId, StartAlbumDownloadRequestDto request, CancellationToken ct = default)
        => await PostOptionalSummaryAsync($"api/jobs/{searchJobId}/downloads/album", request, ct);

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/jobs/{jobId}/cancel", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    public async Task<bool> CancelJobByDisplayIdAsync(int displayId, Guid? workflowId = null, CancellationToken ct = default)
    {
        var jobs = await GetJobsAsync(new JobQuery(null, null, workflowId, CanonicalRootsOnly: false, IncludeNonDefault: true), ct);
        var match = jobs.FirstOrDefault(job => job.DisplayId == displayId);
        return match != null && await CancelJobAsync(match.JobId, ct);
    }

    public async Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/workflows/{workflowId}/cancel", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return 0;

        await EnsureSuccessAsync(response, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("cancelled", out var cancelled) ? cancelled.GetInt32() : 0;
    }

    private async Task<JobSummaryDto?> PostOptionalSummaryAsync<T>(string url, T request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, jsonOptions, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredAsync<JobSummaryDto>(response, ct);
    }

    private ServerEventEnvelopeDto RehydrateEnvelope(ServerEventEnvelopeDto envelope)
    {
        if (envelope.Payload is not JsonElement payload)
            return envelope;

        object typedPayload = envelope.Type switch
        {
            "job.upserted" => Deserialize<JobSummaryDto>(payload),
            "workflow.upserted" => Deserialize<WorkflowSummaryDto>(payload),
            "search.updated" => Deserialize<SearchUpdatedDto>(payload),
            "extraction.started" => Deserialize<ExtractionStartedEventDto>(payload),
            "extraction.failed" => Deserialize<ExtractionFailedEventDto>(payload),
            "job.started" => Deserialize<JobStartedEventDto>(payload),
            "job.completed" => Deserialize<JobCompletedEventDto>(payload),
            "job.status" => Deserialize<JobStatusEventDto>(payload),
            "job.folder-retrieving" => Deserialize<JobFolderRetrievingEventDto>(payload),
            "song.searching" => Deserialize<SongSearchingEventDto>(payload),
            "song.not-found" => Deserialize<SongNotFoundEventDto>(payload),
            "song.failed" => Deserialize<SongFailedEventDto>(payload),
            "download.started" => Deserialize<DownloadStartedEventDto>(payload),
            "download.progress" => Deserialize<DownloadProgressEventDto>(payload),
            "download.state-changed" => Deserialize<DownloadStateChangedEventDto>(payload),
            "song.state-changed" => Deserialize<SongStateChangedEventDto>(payload),
            "album.download-started" => Deserialize<AlbumDownloadStartedEventDto>(payload),
            "album.track-download-started" => Deserialize<AlbumTrackDownloadStartedEventDto>(payload),
            "album.download-completed" => Deserialize<AlbumDownloadCompletedEventDto>(payload),
            "on-complete.started" => Deserialize<OnCompleteStartedEventDto>(payload),
            "on-complete.ended" => Deserialize<OnCompleteEndedEventDto>(payload),
            "track-batch.resolved" => Deserialize<TrackBatchResolvedEventDto>(payload),
            _ => payload,
        };

        return envelope with { Payload = typedPayload };
    }

    private T Deserialize<T>(JsonElement payload)
        => payload.Deserialize<T>(jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize server event payload as {typeof(T).Name}.");

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken ct)
        => await response.Content.ReadFromJsonAsync<T>(jsonOptions, ct)
            ?? throw new InvalidOperationException($"Server returned an empty {typeof(T).Name} response.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"Server request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private static string QueryPart(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : $"&{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private static bool IsActiveState(string state)
        => state is nameof(Sldl.Core.JobState.Pending)
            or nameof(Sldl.Core.JobState.Searching)
            or nameof(Sldl.Core.JobState.Downloading)
            or nameof(Sldl.Core.JobState.Extracting);
}
