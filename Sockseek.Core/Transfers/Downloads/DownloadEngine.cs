using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Transfers.Downloads.Commands;
using Sockseek.Core.Transfers.Downloads.Queueing;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Transfers.Downloads.ManualSelection;
using Sockseek.Core.Transfers.Downloads.Reporting;
using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Transfers.Downloads.Skipping;
using Sockseek.Core.Transfers.Downloads.SourceMutations;
using Sockseek.Core.Transfers.Downloads.State;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Sockseek.Core;

public class DownloadEngine : IDisposable, IAsyncDisposable
{
    private readonly EngineSettings engineSettings;
    private readonly SoulseekClientManager _clientManager;
    private readonly IJobSettingsResolver _jobSettingsResolver;
    private readonly ISongDownloadFallback _songDownloadFallback;
    private readonly StaleDownloadCoordinator _staleDownloadCoordinator;

    internal bool AutomaticStaleChecksEnabled { get; set; } = true;

    public DownloadEvents Events { get; } = new();
    public SearchEvents SearchEvents { get; } = new();

    public JobList Queue { get; } = new();

    private readonly DownloadJobContextStore _contexts = new();
    private readonly SourceMutationCoordinator _sourceMutations = new();
    private readonly ConcurrentDictionary<Guid, byte> _musicDirectoryIndexBuildLoggedByWorkflow = new();
    private readonly DownloadJobTracker _jobs;
    private readonly DownloadCommandTargetResolver _commandTargets;
    private readonly AutoProfileWorkflowReporter _autoProfiles;
    private readonly ManualSelectionCoordinator _manualSelections;
    private readonly SkipEvaluationCoordinator _skipEvaluation;
    private readonly DownloadExecutionContext _executionContext;
    private readonly JobOrchestrator _orchestrator;

    public Job? GetJob(Guid id) => _jobs.GetJob(id);
    public Job? GetJob(int displayId) => _jobs.GetJob(displayId);
    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => _jobs.GetJobsByWorkflow(workflowId);

    public bool TryNextCandidate(Guid jobId)
    {
        var job = _commandTargets.Resolve(jobId);
        if (job == null) return false;

        var activeDownloads = _commandTargets.ActiveDownloadsFor(job);

        if (activeDownloads.Count > 0)
        {
            SockseekLog.Jobs.Info(job, $"trying next candidate; cancelling {activeDownloads.Count} active download{(activeDownloads.Count == 1 ? "" : "s")}: {job}");
            foreach (var ad in activeDownloads)
            {
                ad.IsManuallySkipped = true;
                ad.Cts.Cancel();
            }
            return true;
        }
        return false;
    }

    public bool TryNextCandidateByDisplayId(int displayId, Guid? workflowId = null)
    {
        var job = _commandTargets.ResolveDisplayId(displayId, workflowId);
        return job != null && TryNextCandidate(job.Id);
    }

    // ── public state (read by Searcher / Downloader) ─────────────────────────

    public ISoulseekClient? Client => _clientManager.Client;
    public SoulseekClientStates ClientState => _clientManager.State;
    public bool IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;

    // Session state
    private readonly ActiveDownloadTracker _activeDownloads = new();
    private readonly DownloadedFileCache _downloadedFiles = new();
    private readonly UserSuccessTracker _userSuccesses = new();
    private readonly OutputFinalizer _outputFinalizer;
    public ConcurrentDictionary<string, int> UserSuccessCounts => _userSuccesses.UserSuccessCounts;

    // ── concurrency semaphores ────────────────────────────────────────────────

    // Limits simultaneous extractor runs to avoid API rate limits.
    // Search concurrency is handled inside Searcher (concurrencySemaphore).
    private readonly DownloadRunScope _runtime;

    // ── job channel ──────────────────────────────────────────────────────────

    private readonly DownloadJobQueue _jobQueue = new();

    /// <summary>Enqueues a new root job for processing. Call <see cref="CompleteEnqueue"/> when done adding jobs.</summary>
    public void Enqueue(Job job, DownloadSettings settings)
        => _jobQueue.Enqueue(job, settings);

    /// <summary>Resumes an existing job without re-parenting it or replacing its prepared context.</summary>
    public void Resume(Job job)
        => _jobQueue.Resume(job);

    /// <summary>
    /// Applies a manual album-folder selection to an existing selection job and resumes it
    /// without creating a follow-up job or rebuilding its prepared context.
    /// </summary>
    public bool TryStartManualAlbumSelection(
        Guid sourceJobId,
        AlbumFolder selectedFolder,
        AlbumQuery? albumQuery,
        Action<AlbumJob>? configureSelection,
        out AlbumJob? selectedJob)
        => _manualSelections.TryStart(sourceJobId, selectedFolder, albumQuery, configureSelection, out selectedJob);

    /// <summary>Completes an AwaitingSelection job through the engine so terminal side effects stay centralized.</summary>
    public async Task<bool> CompleteManualSelectionAsync(Guid jobId)
        => await _manualSelections.CompleteAsync(jobId);

    /// <summary>Marks an AwaitingSelection job as explicitly skipped by the user.</summary>
    public async Task<bool> SkipManualSelectionAsync(Guid jobId)
        => await _manualSelections.SkipAsync(jobId);

    public async Task<RetrieveFolderJob> ProcessFolderRetrieval(
        AlbumFolder folder,
        Job parentJob,
        string? statusMessage = null,
        bool consumeJobSlot = true)
        => await _orchestrator.ProcessFolderRetrieval(folder, parentJob, statusMessage, consumeJobSlot);

    /// <summary>Signals that no more jobs will be enqueued. <see cref="RunAsync"/> will drain and exit.</summary>
    public void CompleteEnqueue() => _jobQueue.Complete();

    // ── cancellation ─────────────────────────────────────────────────────────

    public void Cancel()
    {
        foreach (var job in _jobs.Jobs)
            if (!job.IsTerminal)
                job.MarkCancellationSource(JobCancellationSource.UserRequestedAllJobs);

        _runtime.Cancel();
    }

    public int CancelWorkflow(Guid workflowId)
    {
        var jobs = _commandTargets.ResolveWorkflow(workflowId);
        int cancelled = 0;

        foreach (var job in jobs)
        {
            var cts = job.Cts;
            if (cts == null || cts.IsCancellationRequested)
                continue;

            job.Cancel(JobCancellationSource.UserRequestedWorkflow);
            cancelled++;
        }

        return cancelled;
    }

    public bool CancelJob(Guid jobId)
        => CancelCommandTarget(_commandTargets.Resolve(jobId), JobCancellationSource.UserRequestedJob);

    public bool CancelJobByDisplayId(int displayId, Guid? workflowId = null)
        => CancelCommandTarget(_commandTargets.ResolveDisplayId(displayId, workflowId), JobCancellationSource.UserRequestedJob);

    private static bool CancelCommandTarget(Job? job, JobCancellationSource source)
    {
        if (job == null)
            return false;

        job.Cancel(source);
        return true;
    }

    // ── construction ─────────────────────────────────────────────────────────

    public DownloadEngine(
        EngineSettings settings,
        SoulseekClientManager clientManager,
        IJobSettingsResolver? jobSettingsResolver = null,
        ISongDownloadFallback? songDownloadFallback = null,
        TimeProvider? timeProvider = null)
    {
        engineSettings = settings;
        _clientManager = clientManager;
        _jobSettingsResolver = jobSettingsResolver ?? DefaultJobSettingsResolver.Instance;
        _songDownloadFallback = songDownloadFallback ?? SongDownloadFallback.Default;
        _staleDownloadCoordinator = new StaleDownloadCoordinator(_activeDownloads, timeProvider);
        _outputFinalizer = new OutputFinalizer(_downloadedFiles);
        _jobs = new DownloadJobTracker(Events);
        _commandTargets = new DownloadCommandTargetResolver(_jobs, _activeDownloads);
        _autoProfiles = new AutoProfileWorkflowReporter(Events);
        // These collaborators call each other only after construction; the delegates break the cycle explicitly.
        _skipEvaluation = new SkipEvaluationCoordinator(
            job => _executionContext!.Ctx(job),
            job => _executionContext!.RaiseBuildingMusicDirectoryIndex(job));
        if (settings.ConcurrentJobs <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings.ConcurrentJobs), "ConcurrentJobs must be greater than zero.");
        _runtime = new DownloadRunScope(settings, _clientManager, _activeDownloads, _downloadedFiles, _userSuccesses, Events, SearchEvents, _staleDownloadCoordinator);
        _executionContext = new DownloadExecutionContext(
            engineSettings,
            _clientManager,
            _jobSettingsResolver,
            _songDownloadFallback,
            _staleDownloadCoordinator,
            Events,
            Queue,
            _userSuccesses,
            _outputFinalizer,
            _contexts,
            _sourceMutations,
            _jobs,
            _autoProfiles,
            _skipEvaluation,
            _runtime);
        _orchestrator = new JobOrchestrator(
            _executionContext,
            album => _manualSelections!.TryFinalizeClosedAggregateSelectionForAlbumAsync(album));
        _manualSelections = new ManualSelectionCoordinator(
            GetJob,
            _contexts,
            _executionContext.RegisterJob,
            _executionContext.ObservePreparedAutoProfiles,
            Resume,
            _orchestrator.FlushManualSelectionTerminalEffectsAsync,
            JobOrchestrator.IsSuccessfulTerminal);
    }

    public void Dispose()
    {
        _runtime.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _runtime.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    internal int RunStaleDownloadCheckForTesting() => _staleDownloadCoordinator.CancelStaleDownloads();

    // ── top-level entry point ─────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        var rootTasks = new List<Task>();

        SockseekLog.Jobs.Trace("RunAsync: Starting to read from job channel.");
        await foreach (var queuedJob in _jobQueue.ReadAllAsync(ct))
        {
            var rootJob = queuedJob.Job;
            var settings = queuedJob.Settings;
            SockseekLog.Jobs.Trace($"RunAsync: Read {(queuedJob.IsResume ? "resume" : "root")} job {rootJob.DisplayId} from channel.");

            if (!queuedJob.IsResume)
            {
                Queue.Jobs.Add(rootJob);

                foreach (var (id, ctx) in JobPreparer.PrepareSubtree(rootJob, settings!, _jobSettingsResolver))
                    _contexts[id] = ctx;

                _executionContext.ObservePreparedAutoProfiles(rootJob);
            }
            else if (!_contexts.ContainsKey(rootJob.Id))
            {
                throw new InvalidOperationException($"Cannot resume job {rootJob.DisplayId}: no prepared job context exists.");
            }

            if (ContainsLoginRequiredJob(rootJob))
            {
                await _runtime.EnsureServicesInitializedAsync(ct, AutomaticStaleChecksEnabled);
            }

            rootTasks.Add(_orchestrator.ProcessRootJob(rootJob));
        }

        SockseekLog.Jobs.Trace("RunAsync: Channel fully drained. Waiting for rootTasks to complete.");
        await Task.WhenAll(rootTasks);
        SockseekLog.Jobs.Trace("RunAsync: All rootTasks completed.");

        CleanupEmptyStagingDirectories();

        if (Queue.Jobs.Any(ContainsDownloadableJob))
            Events.RaiseEngineCompleted(Queue);

        SockseekLog.Jobs.Debug("Exiting RunAsync");
        await _runtime.CancelAsync();
    }

    private void CleanupEmptyStagingDirectories()
    {
        var outputParents = Queue.AllJobs()
            .Select(job => job.Config?.Output.ParentDir)
            .Where(parent => !string.IsNullOrWhiteSpace(parent))
            .Select(parent => Path.GetFullPath(parent!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var parentDir in outputParents)
        {
            var stagingRoot = Path.Join(parentDir, ".sockseek-staging");
            if (!Directory.Exists(stagingRoot) || Utils.FileCountRecursive(stagingRoot) > 0)
                continue;

            try
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
            catch (Exception ex)
            {
                SockseekLog.Jobs.Debug($"Failed to remove empty staging directory '{stagingRoot}': {SockseekLog.ExceptionSummary(ex)}");
            }
        }
    }

    private static bool ContainsDownloadableJob(Job job)
        => job switch
        {
            ExtractJob { Result: { } result } => ContainsDownloadableJob(result),
            ExtractJob => false,
            JobList list => list.Jobs.Any(ContainsDownloadableJob),
            RetrieveFolderJob => false,
            _ => job.Config?.DoNotDownload == false,
        };

    private static bool ContainsLoginRequiredJob(Job job)
        => job switch
        {
            ExtractJob => job.Config?.NeedLogin == true,
            JobList list => list.Jobs.Any(ContainsLoginRequiredJob),
            _ => job.Config?.NeedLogin == true,
        };

}
