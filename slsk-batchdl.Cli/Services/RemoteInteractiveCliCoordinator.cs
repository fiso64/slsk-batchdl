using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Server;

namespace Sldl.Cli;

internal sealed class RemoteInteractiveCliCoordinator
{
    private readonly ICliBackend backend;
    private readonly CliSettings cliSettings;
    private readonly CancellationToken appToken;
    private readonly Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride;
    private readonly SemaphoreSlim promptSemaphore = new(1, 1);
    private readonly HashSet<Guid> startedExtractResults = [];
    private readonly HashSet<Guid> handledAlbumSearches = [];
    private readonly Dictionary<Guid, InteractiveAlbumSession> interactiveAlbumSessions = [];
    private bool interactiveEnabled;

    public RemoteInteractiveCliCoordinator(
        ICliBackend backend,
        CliSettings cliSettings,
        CancellationToken appToken,
        Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride = null)
    {
        this.backend = backend;
        this.cliSettings = cliSettings;
        this.appToken = appToken;
        this.promptOverride = promptOverride;
        interactiveEnabled = cliSettings.InteractiveMode;
    }

    public async Task<JobSummaryDto> StartAsync(SubmitJobRequestDto request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var job = request.Job with { AutoStartExtractedResult = false };
        return await backend.SubmitJobAsync(request with { Job = job }, ct);
    }

    public async Task RunUntilCompleteAsync(Guid workflowId, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool startedFollowUp = await ProcessWorkflowAsync(workflowId, ct);

            var workflow = await backend.GetWorkflowAsync(workflowId, ct);
            if (!startedFollowUp && (workflow?.Summary.State is "completed" or "failed"))
            {
                startedFollowUp = await ProcessWorkflowAsync(workflowId, ct);
                workflow = await backend.GetWorkflowAsync(workflowId, ct);
                if (!startedFollowUp && (workflow?.Summary.State is "completed" or "failed"))
                    return;
            }

            await Task.Delay(150, ct);
        }
    }

    private async Task<bool> ProcessWorkflowAsync(Guid workflowId, CancellationToken ct)
    {
        bool startedFollowUp = false;
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return false;

        foreach (var summary in workflow.Jobs.OrderBy(job => job.DisplayId))
        {
            ct.ThrowIfCancellationRequested();
            if (summary.Kind == "extract"
                && IsCompleted(summary.State)
                && summary.ResultJobId != null
                && startedExtractResults.Add(summary.JobId))
            {
                await backend.StartExtractedResultAsync(
                    summary.JobId,
                    new StartExtractedResultRequestDto(Interactive: true),
                    ct);
                startedFollowUp = true;
            }

            if (summary.Kind == "search"
                && IsCompleted(summary.State)
                && handledAlbumSearches.Add(summary.JobId))
            {
                await HandleCompletedSearchAsync(summary.JobId, ct);
                startedFollowUp = true;
            }

            if (summary.Kind == "album"
                && !IsActive(summary.State)
                && interactiveAlbumSessions.TryGetValue(summary.JobId, out var session))
            {
                interactiveAlbumSessions.Remove(summary.JobId);
                await HandleCompletedInteractiveAlbumAsync(summary.JobId, session, ct);
                startedFollowUp = true;
            }
        }

        return startedFollowUp;
    }

    private async Task HandleCompletedSearchAsync(Guid searchJobId, CancellationToken ct)
    {
        if (!interactiveEnabled)
            return;

        var detail = await backend.GetJobDetailAsync(searchJobId, ct);
        if (detail?.Payload is not SearchJobPayloadDto search || search.AlbumQuery == null)
            return;

        var projection = await backend.GetAlbumProjectionAsync(searchJobId, includeFiles: true, ct);
        var folders = projection?.Items.Select(InteractiveCliCoordinator.ToAlbumFolder).ToList() ?? [];
        if (folders.Count == 0)
            return;

        var promptJob = ToSearchJob(search);
        var session = new InteractiveAlbumSession(searchJobId, promptJob, search.AlbumQuery, folders);
        var selected = await PromptForAlbumSelectionAsync(session);
        if (selected == null)
            return;

        await EnqueueInteractiveAlbumJobAsync(session, selected, ct);
    }

    private async Task HandleCompletedInteractiveAlbumAsync(
        Guid albumJobId,
        InteractiveAlbumSession session,
        CancellationToken ct)
    {
        if (appToken.IsCancellationRequested)
            return;

        var detail = await backend.GetJobDetailAsync(albumJobId, ct);
        if (detail?.Summary.State == nameof(JobState.Done))
            return;

        if (detail?.Payload is AlbumJobPayloadDto album
            && !string.IsNullOrWhiteSpace(album.ResolvedFolderUsername)
            && !string.IsNullOrWhiteSpace(album.ResolvedFolderPath))
        {
            session.ExcludedFolderKeys.Add(album.ResolvedFolderUsername + "\\" + album.ResolvedFolderPath);
        }

        var selected = await PromptForAlbumSelectionAsync(session);
        if (selected == null)
            return;

        await EnqueueInteractiveAlbumJobAsync(session, selected, ct);
    }

    private async Task EnqueueInteractiveAlbumJobAsync(
        InteractiveAlbumSession session,
        AlbumFolder selected,
        CancellationToken ct)
    {
        var summary = await backend.StartAlbumDownloadAsync(
            session.SourceSearchJobId,
            new StartAlbumDownloadRequestDto(
                new AlbumFolderRefDto(selected.Username, selected.FolderPath),
                AllowBrowseResolvedTarget: true),
            ct);

        if (summary == null)
            throw new InvalidOperationException("Failed to start interactive album download.");

        interactiveAlbumSessions[summary.JobId] = session;
    }

    private async Task<AlbumFolder?> PromptForAlbumSelectionAsync(InteractiveAlbumSession session)
    {
        var availableFolders = session.Folders
            .Where(folder => !session.ExcludedFolderKeys.Contains(InteractiveCliCoordinator.FolderKey(folder)))
            .ToList();

        if (availableFolders.Count == 0)
            return null;

        await promptSemaphore.WaitAsync(appToken);
        try
        {
            InteractiveModeManager.RunResult result;
            if (promptOverride != null)
            {
                result = await promptOverride(new InteractiveAlbumPromptRequest(
                    session.PromptJob,
                    availableFolders,
                    session.RetrievedFolders,
                    session.FilterStr));
            }
            else
            {
                if (ConsoleInputManager.Reporter != null)
                    ConsoleInputManager.Reporter.IsPaused = true;

                Printing.SetBuffering(true);
                try
                {
                    var interactive = new InteractiveModeManager(
                        session.PromptJob,
                        new JobList(),
                        availableFolders,
                        canRetrieve: true,
                        retrievedFolders: session.RetrievedFolders,
                        retrieveFolderCallback: async folder => await backend.RetrieveFolderAndWaitAsync(
                            session.SourceSearchJobId,
                            new RetrieveFolderRequestDto(new AlbumFolderRefDto(folder.Username, folder.FolderPath)),
                            appToken),
                        filterStr: session.FilterStr);

                    result = await interactive.Run();
                }
                finally
                {
                    Printing.SetBuffering(false);
                    Printing.Flush();
                    if (ConsoleInputManager.Reporter != null)
                        ConsoleInputManager.Reporter.IsPaused = false;
                }
            }

            session.FilterStr = result.FilterStr;
            if (result.ExitInteractiveMode)
            {
                interactiveEnabled = false;
                cliSettings.InteractiveMode = false;
            }

            if (result.Index < 0 || result.Folder == null)
                return null;

            return result.Folder;
        }
        finally
        {
            promptSemaphore.Release();
        }
    }

    private static SearchJob ToSearchJob(SearchJobPayloadDto payload)
    {
        var job = payload.AlbumQuery != null
            ? new SearchJob(new AlbumQuery
            {
                Artist = payload.AlbumQuery.Artist,
                Album = payload.AlbumQuery.Album,
                SearchHint = payload.AlbumQuery.SearchHint,
                URI = payload.AlbumQuery.Uri,
                ArtistMaybeWrong = payload.AlbumQuery.ArtistMaybeWrong,
                IsDirectLink = payload.AlbumQuery.IsDirectLink,
                MinTrackCount = payload.AlbumQuery.MinTrackCount,
                MaxTrackCount = payload.AlbumQuery.MaxTrackCount,
            })
            : new SearchJob(new SongQuery
            {
                Artist = payload.Query.Artist,
                Title = payload.Query.Title,
                Album = payload.Query.Album,
                URI = payload.Query.Uri,
                Length = payload.Query.Length,
                ArtistMaybeWrong = payload.Query.ArtistMaybeWrong,
                IsDirectLink = payload.Query.IsDirectLink,
            });

        return job;
    }

    private static bool IsActive(string state)
        => state is nameof(JobState.Pending)
            or nameof(JobState.Extracting)
            or nameof(JobState.Searching)
            or nameof(JobState.Downloading);

    private static bool IsCompleted(string state)
        => state is nameof(JobState.Done)
            or nameof(JobState.AlreadyExists);

    private sealed class InteractiveAlbumSession
    {
        public Guid SourceSearchJobId { get; }
        public Job PromptJob { get; }
        public AlbumQueryDto Query { get; }
        public List<AlbumFolder> Folders { get; }
        public HashSet<string> ExcludedFolderKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RetrievedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FilterStr { get; set; }

        public InteractiveAlbumSession(Guid sourceSearchJobId, Job promptJob, AlbumQueryDto query, List<AlbumFolder> folders)
        {
            SourceSearchJobId = sourceSearchJobId;
            PromptJob = promptJob;
            Query = query;
            Folders = folders;
        }
    }
}
