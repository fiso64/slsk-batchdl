using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Transfers.Downloads.Reporting;
using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Transfers.Downloads.Skipping;
using Sockseek.Core.Transfers.Downloads.SourceMutations;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Transfers.Downloads.Runtime;

internal sealed class DownloadExecutionContext
{
    private readonly ConcurrentDictionary<Guid, byte> musicDirectoryIndexBuildLoggedByWorkflow = new();

    public DownloadExecutionContext(
        EngineSettings engineSettings,
        SoulseekClientManager clientManager,
        IJobSettingsResolver jobSettingsResolver,
        ISongDownloadFallback songDownloadFallback,
        StaleDownloadCoordinator staleDownloadCoordinator,
        DownloadEvents events,
        JobList queue,
        UserSuccessTracker userSuccesses,
        OutputFinalizer outputFinalizer,
        DownloadJobContextStore contexts,
        SourceMutationCoordinator sourceMutations,
        DownloadJobTracker jobs,
        AutoProfileWorkflowReporter autoProfiles,
        SkipEvaluationCoordinator skipEvaluation,
        DownloadRunScope runtime)
    {
        EngineSettings = engineSettings;
        ClientManager = clientManager;
        JobSettingsResolver = jobSettingsResolver;
        SongDownloadFallback = songDownloadFallback;
        StaleDownloadCoordinator = staleDownloadCoordinator;
        Events = events;
        Queue = queue;
        UserSuccesses = userSuccesses;
        OutputFinalizer = outputFinalizer;
        Contexts = contexts;
        SourceMutations = sourceMutations;
        Jobs = jobs;
        AutoProfiles = autoProfiles;
        SkipEvaluation = skipEvaluation;
        Runtime = runtime;
    }

    public EngineSettings EngineSettings { get; }
    public SoulseekClientManager ClientManager { get; }
    public IJobSettingsResolver JobSettingsResolver { get; }
    public ISongDownloadFallback SongDownloadFallback { get; }
    public StaleDownloadCoordinator StaleDownloadCoordinator { get; }
    public DownloadEvents Events { get; }
    public JobList Queue { get; }
    public UserSuccessTracker UserSuccesses { get; }
    public OutputFinalizer OutputFinalizer { get; }
    public DownloadJobContextStore Contexts { get; }
    public SourceMutationCoordinator SourceMutations { get; }
    public DownloadJobTracker Jobs { get; }
    public AutoProfileWorkflowReporter AutoProfiles { get; }
    public SkipEvaluationCoordinator SkipEvaluation { get; }
    public DownloadRunScope Runtime { get; }

    public JobContext Ctx(Job job) => Contexts.Get(job);

    public void RegisterJob(Job job, Job? parent) => Jobs.Register(job, parent);

    public void ObservePreparedAutoProfiles(Job preparedRoot) => AutoProfiles.ObservePreparedRoot(preparedRoot);

    public void EmitAutoProfileFinalSummary(Job rootJob) => AutoProfiles.EmitFinalSummary(rootJob);

    public void RaiseBuildingMusicDirectoryIndex(Job job)
    {
        if (musicDirectoryIndexBuildLoggedByWorkflow.TryAdd(job.WorkflowId, 0))
            Events.RaiseWorkflowMessage(job.WorkflowId, LogLevel.Information, null, "Building music directory index..");
    }
}
