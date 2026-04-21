using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Settings;

namespace Sldl.Cli;

internal sealed class InteractiveCliCoordinator
{
    private readonly DownloadEngine _engine;
    private readonly CliSettings _cliSettings;
    private readonly CancellationToken _appToken;
    private readonly Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? _promptOverride;
    private readonly SemaphoreSlim _promptSemaphore = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _rootJobIds = [];
    private readonly Dictionary<Guid, InteractiveAlbumSession> _interactiveAlbumSessions = [];

    private int _pendingRootJobs;
    private bool _enqueueCompleted;
    private bool _interactiveEnabled;

    public InteractiveCliCoordinator(
        DownloadEngine engine,
        CliSettings cliSettings,
        CancellationToken appToken,
        Func<InteractiveAlbumPromptRequest, Task<InteractiveModeManager.RunResult>>? promptOverride = null)
    {
        _engine = engine;
        _cliSettings = cliSettings;
        _appToken = appToken;
        _promptOverride = promptOverride;
        _interactiveEnabled = cliSettings.InteractiveMode;

        _engine.Events.JobResultCreated += OnJobResultCreated;
        _engine.Events.JobExecutionCompleted += OnJobExecutionCompleted;
    }

    public void Start(ExtractJob rootJob, DownloadSettings settings)
    {
        rootJob.AutoProcessResult = false;
        EnqueueRoot(rootJob, settings);
    }

    private void OnJobResultCreated(ExtractJob extractJob, Job result)
    {
        if (extractJob.AutoProcessResult)
            return;

        if (_appToken.IsCancellationRequested)
            return;

        PrepareDetachedExtractions(result);
        var inheritedSettings = extractJob.Config
            ?? throw new InvalidOperationException("Detached extracted jobs require the extract job config.");

        if (_interactiveEnabled
            && result is AlbumJob albumJob
            && !albumJob.Query.IsDirectLink)
        {
            var searchJob = new SearchJob(albumJob.Query);
            searchJob.CopySharedFieldsFrom(albumJob);
            EnqueueRoot(searchJob, inheritedSettings);
            return;
        }

        EnqueueRoot(result, inheritedSettings);
    }

    private void OnJobExecutionCompleted(Job job)
    {
        if (!IsRootJob(job.Id))
            return;

        try
        {
            if (_appToken.IsCancellationRequested)
                return;

            if (_interactiveEnabled && job is SearchJob searchJob && searchJob.Intent == SearchIntent.Album)
            {
                HandleCompletedAlbumSearch(searchJob);
            }
            else if (_interactiveAlbumSessions.TryGetValue(job.Id, out var session))
            {
                _interactiveAlbumSessions.Remove(job.Id);
                HandleCompletedInteractiveAlbum((AlbumJob)job, session);
            }
        }
        finally
        {
            CompleteRoot(job.Id);
        }
    }

    private void HandleCompletedAlbumSearch(SearchJob searchJob)
    {
        if (searchJob.State != JobState.Done || searchJob.AlbumQuery == null)
            return;

        var folders = searchJob.GetAlbumFolders(searchJob.Config.Search).Items.ToList();
        if (folders.Count == 0)
            return;

        var session = new InteractiveAlbumSession(searchJob, searchJob.AlbumQuery, folders);
        var selected = PromptForAlbumSelectionAsync(session).GetAwaiter().GetResult();
        if (selected == null)
            return;

        EnqueueInteractiveAlbumJob(session, selected, searchJob.Config);
    }

    private void HandleCompletedInteractiveAlbum(AlbumJob albumJob, InteractiveAlbumSession session)
    {
        if (_appToken.IsCancellationRequested)
            return;

        if (albumJob.State == JobState.Done)
            return;

        if (albumJob.ResolvedTarget != null)
            session.ExcludedFolderKeys.Add(FolderKey(albumJob.ResolvedTarget));

        var selected = PromptForAlbumSelectionAsync(session).GetAwaiter().GetResult();
        if (selected == null)
            return;

        EnqueueInteractiveAlbumJob(session, selected, albumJob.Config);
    }

    private void EnqueueInteractiveAlbumJob(InteractiveAlbumSession session, AlbumFolder selected, DownloadSettings settings)
    {
        var albumJob = new AlbumJob(session.Query)
        {
            Results = session.Folders,
            ResolvedTarget = selected,
            AllowBrowseResolvedTarget = true,
        };
        albumJob.CopySharedFieldsFrom(session.PromptJob);

        _interactiveAlbumSessions[albumJob.Id] = session;
        EnqueueRoot(albumJob, settings);
    }

    private async Task<AlbumFolder?> PromptForAlbumSelectionAsync(InteractiveAlbumSession session)
    {
        var availableFolders = session.Folders
            .Where(folder => !session.ExcludedFolderKeys.Contains(FolderKey(folder)))
            .ToList();

        if (availableFolders.Count == 0)
            return null;

        await _promptSemaphore.WaitAsync(_appToken);
        try
        {
            InteractiveModeManager.RunResult result;
            if (_promptOverride != null)
            {
                result = await _promptOverride(new InteractiveAlbumPromptRequest(
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
                        _engine.Queue,
                        availableFolders,
                        canRetrieve: true,
                        retrievedFolders: session.RetrievedFolders,
                        retrieveFolderCallback: async folder => await _engine.ProcessFolderRetrieval(folder, session.PromptJob),
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
                _interactiveEnabled = false;
                _cliSettings.InteractiveMode = false;
            }

            if (result.Index < 0 || result.Folder == null)
                return null;

            return result.Folder;
        }
        finally
        {
            _promptSemaphore.Release();
        }
    }

    private void EnqueueRoot(Job job, DownloadSettings settings)
    {
        lock (_gate)
        {
            if (_enqueueCompleted)
                throw new InvalidOperationException("Cannot enqueue more jobs after completion was signaled.");

            _rootJobIds.Add(job.Id);
            _pendingRootJobs++;
        }

        _engine.Enqueue(job, settings);
    }

    private bool IsRootJob(Guid jobId)
    {
        lock (_gate)
            return _rootJobIds.Contains(jobId);
    }

    private void CompleteRoot(Guid jobId)
    {
        bool shouldComplete = false;

        lock (_gate)
        {
            if (!_rootJobIds.Remove(jobId))
                return;

            _pendingRootJobs--;
            if (_pendingRootJobs == 0 && !_enqueueCompleted)
            {
                _enqueueCompleted = true;
                shouldComplete = true;
            }
        }

        if (shouldComplete)
            _engine.CompleteEnqueue();
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

    private static string FolderKey(AlbumFolder folder)
        => folder.Username + "\\" + folder.FolderPath;

    private sealed class InteractiveAlbumSession
    {
        public Job PromptJob { get; }
        public AlbumQuery Query { get; }
        public List<AlbumFolder> Folders { get; }
        public HashSet<string> ExcludedFolderKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RetrievedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FilterStr { get; set; }

        public InteractiveAlbumSession(Job promptJob, AlbumQuery query, List<AlbumFolder> folders)
        {
            PromptJob = promptJob;
            Query = query;
            Folders = folders;
        }
    }
}

internal sealed record InteractiveAlbumPromptRequest(
    Job PromptJob,
    List<AlbumFolder> Folders,
    HashSet<string> RetrievedFolders,
    string? FilterStr);
