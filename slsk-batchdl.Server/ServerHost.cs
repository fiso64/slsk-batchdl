using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sldl.Server;

public static class ServerHost
{
    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(consoleOptions =>
        {
            consoleOptions.SingleLine = true;
            consoleOptions.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        if (!string.IsNullOrWhiteSpace(url))
            builder.WebHost.UseUrls(url);

        if (options != null)
            builder.Services.AddSingleton<IOptions<ServerOptions>>(Options.Create(options));
        else
            builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("SldlServer"));

        builder.Services.Configure<JsonOptions>(jsonOptions =>
        {
            jsonOptions.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddSignalR();
        builder.Services.AddSingleton<EngineSupervisor>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().StateStore);
        builder.Services.AddSingleton<ServerEventBroadcaster>();
        builder.Services.AddHostedService<EngineRuntimeHostedService>();

        var app = builder.Build();
        CoreLoggerBridge.Configure(app.Services, (options ?? app.Services.GetRequiredService<IOptions<ServerOptions>>().Value).Engine.LogLevel);
        _ = app.Services.GetRequiredService<ServerEventBroadcaster>();

        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/api/server/info"));

        app.MapGet("/api/server/info", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetInfo()));
        app.MapGet("/api/server/status", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetStatus()));
        app.MapGet("/api/profiles", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetProfiles()));
        app.MapGet("/api/events/catalog", () => Results.Ok(ServerEventCatalog.All));

        app.MapGet("/api/jobs", (
            EngineStateStore stateStore,
            string? state,
            string? kind,
            Guid? workflowId,
            bool canonicalRootsOnly = false,
            bool includeNonDefault = false) =>
        {
            var jobs = stateStore.GetJobs(new JobQuery(state, kind, workflowId, canonicalRootsOnly, includeNonDefault));
            return Results.Ok(jobs);
        });

        app.MapGet("/api/jobs/{jobId:guid}", (Guid jobId, EngineStateStore stateStore) =>
        {
            var detail = stateStore.GetJobDetail(jobId);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        });

        app.MapGet("/api/jobs/{jobId:guid}/raw", (Guid jobId, long afterSequence, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetSearchRawResults(jobId, afterSequence);
            return results != null ? Results.Ok(results) : Results.NotFound();
        });

        app.MapGet("/api/jobs/{jobId:guid}/results/tracks", (Guid jobId, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetTrackResults(jobId);
            return results != null ? Results.Ok(results) : Results.NotFound();
        });

        app.MapGet("/api/jobs/{jobId:guid}/results/albums", (Guid jobId, bool includeFiles, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetAlbumResults(jobId, includeFiles);
            return results != null ? Results.Ok(results) : Results.NotFound();
        });

        app.MapGet("/api/jobs/{jobId:guid}/results/aggregate-tracks", (Guid jobId, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetAggregateTrackResults(jobId);
            return results != null ? Results.Ok(results) : Results.NotFound();
        });

        app.MapGet("/api/jobs/{jobId:guid}/results/aggregate-albums", (Guid jobId, EngineSupervisor supervisor) =>
        {
            var results = supervisor.GetAggregateAlbumResults(jobId);
            return results != null ? Results.Ok(results) : Results.NotFound();
        });

        app.MapPost("/api/jobs/{jobId:guid}/retrieve-folder", async (
            Guid jobId,
            RetrieveFolderRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartRetrieveFolderAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/jobs/{jobId:guid}/extracted-result/start", async (
            Guid jobId,
            StartExtractedResultRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summaries = await supervisor.StartExtractedResultAsync(jobId, request, ct);
                return summaries != null
                    ? Results.Accepted($"/api/jobs/{jobId}", summaries)
                    : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/jobs/{jobId:guid}/downloads/song", async (
            Guid jobId,
            StartSongDownloadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartSongDownloadAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/jobs/{jobId:guid}/downloads/album", async (
            Guid jobId,
            StartAlbumDownloadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartAlbumDownloadAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/jobs/{jobId:guid}/cancel", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.CancelJob(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        });

        app.MapPost("/api/jobs/extract", async (SubmitExtractJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitExtractJobAsync(request, ct)));

        app.MapPost("/api/jobs/search/tracks", async (SubmitTrackSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitTrackSearchJobAsync(request, ct)));

        app.MapPost("/api/jobs/search/albums", async (SubmitAlbumSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumSearchJobAsync(request, ct)));

        app.MapPost("/api/jobs/downloads/song", async (SubmitSongJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitSongJobAsync(request, ct)));

        app.MapPost("/api/jobs/downloads/album", async (SubmitAlbumJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumJobAsync(request, ct)));

        app.MapPost("/api/jobs/aggregate/tracks", async (SubmitAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAggregateJobAsync(request, ct)));

        app.MapPost("/api/jobs/aggregate/albums", async (SubmitAlbumAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumAggregateJobAsync(request, ct)));

        app.MapPost("/api/jobs/lists", async (SubmitJobListRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitJobListAsync(request, ct)));

        app.MapGet("/api/workflows", (EngineStateStore stateStore) => Results.Ok(stateStore.GetWorkflows()));

        app.MapGet("/api/workflows/{workflowId:guid}", (Guid workflowId, EngineStateStore stateStore) =>
        {
            var workflow = stateStore.GetWorkflow(workflowId);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        });

        app.MapGet("/api/workflows/{workflowId:guid}/presentation", (Guid workflowId, EngineStateStore stateStore) =>
        {
            var workflow = stateStore.GetPresentedWorkflow(workflowId);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        });

        app.MapPost("/api/workflows/{workflowId:guid}/cancel", (Guid workflowId, EngineSupervisor supervisor) =>
        {
            int cancelled = supervisor.CancelWorkflow(workflowId);
            return cancelled > 0
                ? Results.Accepted($"/api/workflows/{workflowId}", new { cancelled })
                : Results.NotFound();
        });

        app.MapHub<ServerEventHub>("/api/events");
    }

    private static async Task<IResult> SubmitJobAsync(Func<Task<JobSummaryDto>> submit)
    {
        try
        {
            var summary = await submit();
            return Results.Accepted($"/api/jobs/{summary.JobId}", summary);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
