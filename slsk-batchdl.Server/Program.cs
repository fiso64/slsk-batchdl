using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Sldl.Server;

Sldl.Core.Logger.SetupExceptionHandling();
Sldl.Core.Logger.AddConsole();

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("SldlServer"));
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<EngineSupervisor>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().StateStore);
builder.Services.AddSingleton<ServerEventBroadcaster>();
builder.Services.AddHostedService<EngineRuntimeHostedService>();

var app = builder.Build();
_ = app.Services.GetRequiredService<ServerEventBroadcaster>();

app.MapGet("/", () => Results.Redirect("/api/server/info"));

app.MapGet("/api/server/info", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetInfo()));
app.MapGet("/api/server/status", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetStatus()));
app.MapGet("/api/profiles", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetProfiles()));

app.MapGet("/api/jobs", (
    EngineStateStore stateStore,
    string? state,
    string? kind,
    Guid? workflowId,
    bool rootOnly = false,
    bool includeHidden = false) =>
{
    var jobs = stateStore.GetJobs(new JobQuery(state, kind, workflowId, rootOnly, includeHidden));
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

app.MapGet("/api/jobs/{jobId:guid}/projections/tracks", (Guid jobId, EngineSupervisor supervisor) =>
{
    var projection = supervisor.GetTrackProjection(jobId);
    return projection != null ? Results.Ok(projection) : Results.NotFound();
});

app.MapGet("/api/jobs/{jobId:guid}/projections/albums", (Guid jobId, bool includeFiles, EngineSupervisor supervisor) =>
{
    var projection = supervisor.GetAlbumProjection(jobId, includeFiles);
    return projection != null ? Results.Ok(projection) : Results.NotFound();
});

app.MapGet("/api/jobs/{jobId:guid}/projections/aggregate-tracks", (Guid jobId, EngineSupervisor supervisor) =>
{
    var projection = supervisor.GetAggregateTrackProjection(jobId);
    return projection != null ? Results.Ok(projection) : Results.NotFound();
});

app.MapGet("/api/jobs/{jobId:guid}/projections/aggregate-albums", (Guid jobId, EngineSupervisor supervisor) =>
{
    var projection = supervisor.GetAggregateAlbumProjection(jobId);
    return projection != null ? Results.Ok(projection) : Results.NotFound();
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

app.MapPost("/api/jobs", async (SubmitJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
{
    try
    {
        var summary = await supervisor.SubmitJobAsync(request, ct);
        return Results.Accepted($"/api/jobs/{summary.JobId}", summary);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/workflows", (EngineStateStore stateStore) => Results.Ok(stateStore.GetWorkflows()));

app.MapGet("/api/workflows/{workflowId:guid}", (Guid workflowId, EngineStateStore stateStore) =>
{
    var workflow = stateStore.GetWorkflow(workflowId);
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

app.Run();
