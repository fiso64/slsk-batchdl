using System.Collections.Concurrent;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Core.Transfers.Downloads.ManualSelection;

internal sealed class ManualSelectionCoordinator
{
    private readonly Func<Guid, Job?> getJob;
    private readonly DownloadJobContextStore contexts;
    private readonly Action<Job, Job?> registerJob;
    private readonly Action<Job> observePreparedAutoProfiles;
    private readonly Action<Job> resumeJob;
    private readonly Func<Job, Task> flushTerminalEffects;
    private readonly Func<Job, bool> isSuccessfulTerminal;
    private readonly ConcurrentDictionary<Guid, Guid> aggregateParentByAlbumId = new();
    private readonly ConcurrentDictionary<Guid, byte> closedAggregateSelections = new();

    public ManualSelectionCoordinator(
        Func<Guid, Job?> getJob,
        DownloadJobContextStore contexts,
        Action<Job, Job?> registerJob,
        Action<Job> observePreparedAutoProfiles,
        Action<Job> resumeJob,
        Func<Job, Task> flushTerminalEffects,
        Func<Job, bool> isSuccessfulTerminal)
    {
        this.getJob = getJob;
        this.contexts = contexts;
        this.registerJob = registerJob;
        this.observePreparedAutoProfiles = observePreparedAutoProfiles;
        this.resumeJob = resumeJob;
        this.flushTerminalEffects = flushTerminalEffects;
        this.isSuccessfulTerminal = isSuccessfulTerminal;
    }

    public bool TryStart(
        Guid sourceJobId,
        AlbumFolder selectedFolder,
        AlbumQuery? albumQuery,
        Action<AlbumJob>? configureSelection,
        out AlbumJob? selectedJob)
    {
        selectedJob = null;
        var sourceJob = getJob(sourceJobId);

        if (sourceJob is AlbumJob albumJob && CanStartManualAlbumSelection(albumJob))
        {
            StartExistingAlbumSelection(albumJob, selectedFolder, configureSelection);
            selectedJob = albumJob;
            return true;
        }

        if (sourceJob is AlbumAggregateJob aggregateJob && aggregateJob.IsAwaitingSelection)
        {
            var childAlbum = FindAggregateAlbumForSelection(aggregateJob, selectedFolder, albumQuery);
            if (childAlbum == null)
                return false;

            EnsureManualAggregateAlbumChildPrepared(aggregateJob, childAlbum);
            StartExistingAlbumSelection(childAlbum, selectedFolder, configureSelection);
            selectedJob = childAlbum;
            return true;
        }

        return false;
    }

    public async Task<bool> CompleteAsync(Guid jobId)
    {
        var job = getJob(jobId);
        if (job == null || !job.IsAwaitingSelection || job.Config == null)
            return false;

        if (job is AlbumAggregateJob aggregateJob)
        {
            closedAggregateSelections.TryAdd(aggregateJob.Id, 0);
            await TryFinalizeClosedAggregateSelectionAsync(aggregateJob);
            return true;
        }

        job.Fail(JobFailureReason.NoMatchingResults);
        await flushTerminalEffects(job);
        return true;
    }

    public async Task<bool> SkipAsync(Guid jobId)
    {
        var job = getJob(jobId);
        if (job == null || !job.IsAwaitingSelection || job.Config == null)
            return false;

        job.SetSkipped(JobSkipReason.Manual);
        await flushTerminalEffects(job);
        return true;
    }

    public async Task TryFinalizeClosedAggregateSelectionForAlbumAsync(AlbumJob albumJob)
    {
        if (aggregateParentByAlbumId.TryGetValue(albumJob.Id, out var aggregateId)
            && getJob(aggregateId) is AlbumAggregateJob aggregateJob)
        {
            await TryFinalizeClosedAggregateSelectionAsync(aggregateJob);
        }
    }

    private void StartExistingAlbumSelection(AlbumJob albumJob, AlbumFolder selectedFolder, Action<AlbumJob>? configureSelection)
    {
        albumJob.ClearFailure();
        albumJob.ResolvedTarget = selectedFolder;
        configureSelection?.Invoke(albumJob);
        if (!albumJob.Results.Any(folder => SameAlbumFolder(folder, selectedFolder)))
            albumJob.Results.Insert(0, selectedFolder);
        albumJob.ResetToPending();
        resumeJob(albumJob);
    }

    private static bool CanStartManualAlbumSelection(AlbumJob albumJob)
        => albumJob.DownloadBehavior == DownloadBehavior.Manual
            && (albumJob.IsAwaitingSelection || albumJob.IsUnsuccessfulTerminal);

    private static AlbumJob? FindAggregateAlbumForSelection(AlbumAggregateJob aggregateJob, AlbumFolder selectedFolder, AlbumQuery? albumQuery)
        => aggregateJob.Albums.FirstOrDefault(album =>
            (albumQuery == null || AlbumQueriesEqual(album.Query, albumQuery))
            && album.Results.Any(folder => SameAlbumFolder(folder, selectedFolder)));

    private void EnsureManualAggregateAlbumChildPrepared(AlbumAggregateJob aggregateJob, AlbumJob albumJob)
    {
        albumJob.WorkflowId = aggregateJob.WorkflowId;
        albumJob.Config = aggregateJob.Config;
        albumJob.ItemName ??= albumJob.ToString(noInfo: true);
        albumJob.DownloadBehaviorPolicy = albumJob.DownloadBehaviorPolicy with { Album = DownloadBehavior.Manual };

        aggregateParentByAlbumId[albumJob.Id] = aggregateJob.Id;
        registerJob(albumJob, aggregateJob);

        if (contexts.ContainsKey(albumJob.Id))
            return;

        var parentCtx = contexts.Get(aggregateJob);
        contexts[albumJob.Id] = new JobContext
        {
            IndexEditor = parentCtx.IndexEditor,
            PlaylistEditor = parentCtx.PlaylistEditor,
            OutputDirSkipper = parentCtx.OutputDirSkipper,
            MusicDirSkipper = parentCtx.MusicDirSkipper,
            OutputScope = parentCtx.OutputScope,
            PreprocessTracks = false,
        };

        observePreparedAutoProfiles(albumJob);
    }

    private async Task TryFinalizeClosedAggregateSelectionAsync(AlbumAggregateJob aggregateJob)
    {
        if (!closedAggregateSelections.ContainsKey(aggregateJob.Id))
            return;

        if (aggregateJob.LifecycleState == JobLifecycleState.Terminal)
            return;

        var selectedAlbums = aggregateParentByAlbumId
            .Where(pair => pair.Value == aggregateJob.Id)
            .Select(pair => getJob(pair.Key))
            .OfType<AlbumJob>()
            .ToList();

        if (selectedAlbums.Count == 0)
        {
            aggregateJob.Fail(JobFailureReason.NoMatchingResults);
            await flushTerminalEffects(aggregateJob);
            return;
        }

        if (selectedAlbums.Any(IsActiveManualSelectionChild))
            return;

        if (selectedAlbums.All(isSuccessfulTerminal))
            aggregateJob.SetDone();
        else
            aggregateJob.Fail(JobFailureReason.NoMatchingResults);

        await flushTerminalEffects(aggregateJob);
    }

    private static bool IsActiveManualSelectionChild(Job job)
        => job.LifecycleState != JobLifecycleState.Terminal;

    private static bool SameAlbumFolder(AlbumFolder left, AlbumFolder right)
        => string.Equals(left.Username, right.Username, StringComparison.Ordinal)
            && string.Equals(left.FolderPath, right.FolderPath, StringComparison.Ordinal);

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.URI, right.URI, StringComparison.OrdinalIgnoreCase);
}
