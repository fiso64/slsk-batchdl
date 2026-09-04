using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
using Sockseek.Core.PeerBrowsing;

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
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<DownloadEngine> logger;
    private readonly string engineId = Guid.NewGuid().ToString("N")[..12];
    private readonly bool retireTerminalWorkflows;

    internal bool AutomaticStaleChecksEnabled { get; set; } = true;

    public DownloadEvents Events { get; }
    public SearchEvents SearchEvents { get; }

    public JobList Queue { get; } = new();

    private readonly DownloadJobContextStore _contexts = new();
    private readonly SourceMutationCoordinator _sourceMutations;
    private readonly DownloadJobTracker _jobs;
    private readonly DownloadCommandTargetResolver _commandTargets;
    private readonly AutoProfileWorkflowReporter _autoProfiles;
    private readonly ManualSelectionCoordinator _manualSelections;
    private readonly SkipEvaluationCoordinator _skipEvaluation;
    private readonly DownloadExecutionContext _executionContext;
    private readonly JobOrchestrator _orchestrator;
    private readonly WorkflowLifetimeCoordinator? _workflowLifetime;

    public Job? GetJob(Guid id) => _jobs.GetJob(id);
    public Job? GetJob(int displayId) => _jobs.GetJob(displayId);
    public IReadOnlyList<Job> GetJobsByWorkflow(Guid workflowId) => _jobs.GetJobsByWorkflow(workflowId);

    public bool TryCancelTransfer(Guid transferId)
    {
        if (!_activeDownloads.TryGet(transferId, out var download))
            return false;
        download.Cts.Cancel();
        return true;
    }

    public bool TryNextCandidate(Guid jobId)
    {
        var job = _commandTargets.Resolve(jobId);
        if (job == null) return false;

        var activeDownloads = _commandTargets.ActiveDownloadsFor(job);

        if (activeDownloads.Count > 0)
        {
            Events.RaiseJobMessage(
                job,
                LogLevel.Information,
                null,
                $"trying next candidate; cancelling {activeDownloads.Count} active download{(activeDownloads.Count == 1 ? "" : "s")}");
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

    // ── public state (read by search and transfer services) ──────────────────

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
    public void Enqueue(
        Job job,
        DownloadSettings settings,
        Guid? sourceJobId = null,
        bool settingsAreFinal = false)
    {
        if (sourceJobId is Guid sourceId)
            _jobs.AssociateSource(job.Id, sourceId);

        _jobQueue.Enqueue(job, settings, _workflowLifetime?.QueueRoot(job), settingsAreFinal);
    }

    /// <summary>Resumes an existing job without re-parenting it or replacing its prepared context.</summary>
    public void Resume(Job job)
        => _jobQueue.Resume(job, _workflowLifetime?.QueueRoot(job));

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
    {
        var job = GetJob(jobId);
        bool completed = await _manualSelections.CompleteAsync(jobId);
        if (completed && job != null)
            _workflowLifetime?.Reevaluate(job.WorkflowId);
        return completed;
    }

    /// <summary>Marks an AwaitingSelection job as explicitly skipped by the user.</summary>
    public async Task<bool> SkipManualSelectionAsync(Guid jobId)
    {
        var job = GetJob(jobId);
        bool skipped = await _manualSelections.SkipAsync(jobId);
        if (skipped && job != null)
            _workflowLifetime?.Reevaluate(job.WorkflowId);
        return skipped;
    }

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

    /// <summary>
    /// Cancels every currently cancellable job without stopping the engine runtime.
    /// Daemon callers use this instead of <see cref="Cancel"/> so the engine remains
    /// available for later submissions.
    /// </summary>
    public int CancelAllJobs()
    {
        int cancelled = 0;
        foreach (var job in _jobs.Jobs)
        {
            var cts = job.Cts;
            if (job.IsTerminal || cts == null || cts.IsCancellationRequested)
                continue;

            job.Cancel(JobCancellationSource.UserRequestedAllJobs);
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
        TimeProvider? timeProvider = null,
        IPeerDirectorySource? directorySource = null,
        ILoggerFactory? loggerFactory = null,
        ISensitiveOutput? sensitiveOutput = null,
        bool retireTerminalWorkflows = false)
    {
        engineSettings = settings;
        this.retireTerminalWorkflows = retireTerminalWorkflows;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<DownloadEngine>();
        Events = new DownloadEvents(
            timeProvider,
            this.loggerFactory.CreateLogger<DownloadEvents>());
        SearchEvents = new SearchEvents();
        _sourceMutations = new SourceMutationCoordinator(
            Events,
            this.loggerFactory.CreateLogger<SourceMutationCoordinator>());
        _clientManager = clientManager;
        _jobSettingsResolver = jobSettingsResolver ?? DefaultJobSettingsResolver.Instance;
        _songDownloadFallback = songDownloadFallback ?? SongDownloadFallback.Default;
        _staleDownloadCoordinator = new StaleDownloadCoordinator(
            _activeDownloads,
            timeProvider,
            this.loggerFactory.CreateLogger<StaleDownloadCoordinator>());
        _outputFinalizer = new OutputFinalizer(
            _downloadedFiles,
            Events,
            this.loggerFactory.CreateLogger<OutputFinalizer>());
        _jobs = new DownloadJobTracker(Events);
        _commandTargets = new DownloadCommandTargetResolver(_jobs, _activeDownloads);
        _autoProfiles = new AutoProfileWorkflowReporter(Events);
        // These collaborators call each other only after construction; the delegates break the cycle explicitly.
        _skipEvaluation = new SkipEvaluationCoordinator(
            job => _executionContext!.Ctx(job),
            job => _executionContext!.RaiseBuildingMusicDirectoryIndex(job),
            this.loggerFactory.CreateLogger<SkipEvaluationCoordinator>());
        if (settings.ConcurrentJobs <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings.ConcurrentJobs), "ConcurrentJobs must be greater than zero.");
        _runtime = new DownloadRunScope(
            settings,
            _clientManager,
            _activeDownloads,
            _downloadedFiles,
            _userSuccesses,
            Events,
            SearchEvents,
            _staleDownloadCoordinator,
            timeProvider,
            directorySource,
            this.loggerFactory);
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
            _runtime,
            this.loggerFactory,
            sensitiveOutput ?? NullSensitiveOutput.Instance);
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
        if (retireTerminalWorkflows)
        {
            _workflowLifetime = new WorkflowLifetimeCoordinator(
                _jobs.GetJobsByWorkflow,
                _manualSelections.HasResumableState,
                workflowId => (_jobSettingsResolver as IWorkflowSettingsLifetime)
                    ?.CaptureWorkflowVersion(workflowId) ?? 0,
                RetireWorkflow);
        }
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

    internal DownloadEngineRetainedStateCounts RetainedStateCounts
    {
        get
        {
            var execution = _executionContext.RetainedStateCounts;
            var events = Events.RetainedStateCounts;
            return new DownloadEngineRetainedStateCounts(
                Queue.Count,
                _jobs.Count,
                _contexts.Count,
                _activeDownloads.Count,
                _manualSelections.RetainedStateCount,
                _workflowLifetime?.RetainedGenerationCount ?? 0,
                execution.WorkflowDiagnostics,
                execution.PendingTerminalTransfers,
                execution.AutoProfileWorkflows,
                events.Jobs,
                events.Transfers,
                events.Attempts,
                events.Gates,
                events.TerminalTransfers);
        }
    }

    // ── top-level entry point ─────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        DownloadLogMessages.EngineStage(logger, engineId, "reading-job-channel");
        await BoundedAsync.ForEachAsync(
            ReadPreparedRootJobs(ct),
            _runtime.ConcurrentSchedulingLimit,
            ProcessPreparedRootJob,
            ct);
        DownloadLogMessages.EngineStage(logger, engineId, "root-jobs-completed");

        CleanupEmptyStagingDirectories();

        if (Queue.Jobs.Any(ContainsDownloadableJob))
            Events.RaiseEngineCompleted(Queue);

        DownloadLogMessages.EngineStage(logger, engineId, "stopped");
        await _runtime.CancelAsync();
    }

    private async Task ProcessPreparedRootJob(PreparedRootJob prepared)
    {
        try
        {
            if (prepared.PreparationFailure != null)
            {
                _executionContext.RegisterJob(prepared.Job, parent: null);
                FailActiveWorkflowJobs(prepared.Job, prepared.PreparationFailure);
                Events.RaiseJobExecutionCompleted(prepared.Job);
            }
            else
            {
                try
                {
                    await _orchestrator.ProcessRootJob(
                        prepared.Job,
                        emitAutoProfileFinalSummary: !retireTerminalWorkflows);
                }
                catch (Exception ex) when (retireTerminalWorkflows)
                {
                    FailActiveWorkflowJobs(prepared.Job, ex);
                }
            }
        }
        finally
        {
            if (prepared.Lifetime != null)
                _workflowLifetime!.RootCompleted(prepared.Lifetime);
        }
    }

    private async IAsyncEnumerable<PreparedRootJob> ReadPreparedRootJobs(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var queuedJob in _jobQueue.ReadAllAsync(cancellationToken))
        {
            var rootJob = queuedJob.Job;
            var settings = queuedJob.Settings;
            if (queuedJob.Lifetime != null)
                await _workflowLifetime!.WaitUntilReadyAsync(queuedJob.Lifetime, cancellationToken);
            DownloadLogMessages.JobDecision(
                logger,
                rootJob.Id,
                queuedJob.IsResume ? "resume-dequeued" : "root-dequeued",
                null);

            Exception? preparationFailure = null;
            try
            {
                if (!queuedJob.IsResume)
                {
                    Queue.Add(rootJob);

                    foreach (var (id, ctx) in JobPreparer.PrepareSubtree(
                        rootJob,
                        settings!,
                        _jobSettingsResolver,
                        rootSettingsAreFinal: queuedJob.SettingsAreFinal))
                        _contexts.Set(id, rootJob.WorkflowId, ctx);

                    _executionContext.ObservePreparedAutoProfiles(rootJob);
                }
                else if (!_contexts.ContainsKey(rootJob.Id))
                {
                    throw new InvalidOperationException($"Cannot resume job {rootJob.DisplayId}: no prepared job context exists.");
                }

                if (ContainsLoginRequiredJob(rootJob))
                {
                    await _runtime.EnsureServicesInitializedAsync(cancellationToken, AutomaticStaleChecksEnabled);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (retireTerminalWorkflows)
            {
                preparationFailure = ex;
            }

            yield return new PreparedRootJob(rootJob, queuedJob.Lifetime, preparationFailure);
        }

        DownloadLogMessages.EngineStage(logger, engineId, "waiting-for-root-jobs");
    }

    private void RetireWorkflow(
        Guid workflowId,
        IReadOnlyList<Job> registeredJobs,
        long settingsVersion)
    {
        _executionContext.EmitAutoProfileFinalSummary(workflowId);
        Events.RaiseWorkflowRetired(workflowId, registeredJobs.Count);

        var registeredIds = registeredJobs.Select(job => job.Id).ToHashSet();
        Queue.RemoveJobs(registeredIds);
        var preparedIds = _contexts.RemoveWorkflow(workflowId);
        var allJobIds = registeredIds.Concat(preparedIds).ToHashSet();
        _executionContext.RetireWorkflow(workflowId, allJobIds);
        _manualSelections.Retire(registeredIds);
        _jobs.Retire(registeredIds);
        if (_jobSettingsResolver is IWorkflowSettingsLifetime settingsLifetime)
            settingsLifetime.RetireWorkflow(workflowId, allJobIds, settingsVersion);
    }

    private void FailActiveWorkflowJobs(Job rootJob, Exception exception)
    {
        DownloadLogMessages.ComponentFailed(logger, exception, "root-execution", rootJob.Id);
        string message = Diagnostics.ExceptionText.Summary(exception);
        string detail = Diagnostics.ExceptionText.Detail(exception);
        foreach (var job in _jobs.GetJobsByWorkflow(rootJob.WorkflowId))
        {
            if (!job.IsTerminal)
                JobOutcomeCommitter.Commit(
                    job,
                    JobOutcome.Failed(JobFailureReason.Other, message, detail));
        }
    }

    private sealed record PreparedRootJob(
        Job Job,
        WorkflowLifetimeCoordinator.WorkflowRootLease? Lifetime,
        Exception? PreparationFailure);

    private void CleanupEmptyStagingDirectories()
    {
        var outputParents = Queue.AllJobs()
            .Select(job => job.Config?.Output.ParentDir)
            .Where(parent => !string.IsNullOrWhiteSpace(parent))
            .Select(parent => Path.GetFullPath(parent!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var parentDir in outputParents)
        {
            var stagingRoot = Path.Join(parentDir, OutputStaging.DirectoryName);
            if (!Directory.Exists(stagingRoot) || Utils.FileCountRecursive(stagingRoot) > 0)
                continue;

            try
            {
                Directory.Delete(stagingRoot, recursive: false);
            }
            catch (Exception ex)
            {
                DownloadLogMessages.CleanupFailed(
                    logger,
                    "staging-directory",
                    ex.GetType().Name);
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

internal sealed record DownloadEngineRetainedStateCounts(
    int QueueRoots,
    int RegisteredJobs,
    int Contexts,
    int ActiveDownloads,
    int ManualSelections,
    int WorkflowGenerations,
    int WorkflowDiagnostics,
    int PendingTerminalTransfers,
    int AutoProfileWorkflows,
    int EventJobs,
    int EventTransfers,
    int EventAttempts,
    int EventGates,
    int EventTerminalTransfers);
