using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Soulseek;
using Models;
using Enums;
using Extractors;
using Jobs;
using Services;
using Utilities;

using Directory = System.IO.Directory;
using File = System.IO.File;
using SlFile = Soulseek.File;


public class DownloadEngine
{
    private const int updateInterval = 100;

    private IExtractor?  extractor    = null;
    private Searcher?    searcher     = null;
    private Downloader?  downloader   = null;

    private readonly Config              defaultConfig;
    private readonly SoulseekClientManager _clientManager;
    private readonly IProgressReporter   _progressReporter;

    public JobList Queue { get; } = new();

    private Dictionary<Guid, JobContext> _contexts = new();

    private JobContext Ctx(Job job) => _contexts[job.Id];

    // ── public state (read by Searcher / Downloader) ─────────────────────────

    public ISoulseekClient?  Client              => _clientManager.Client;
    public bool              IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;
    public IProgressReporter ProgressReporter    => _progressReporter;

    // Session state (Decoupled)
    private readonly SessionRegistry _registry = new();


    // ── injectable CLI callbacks ──────────────────────────────────────────────

    public Func<AlbumJob, Task<AlbumFolder?>>? SelectAlbumVersion { get; set; }

    // ── cancellation ─────────────────────────────────────────────────────────

    private readonly CancellationTokenSource appCts = new();
    public void Cancel() => appCts.Cancel();

    // ── key interception (CLI-side) ───────────────────────────────────────────

    private bool interceptKeys = false;
    private event EventHandler<ConsoleKey>? keyPressed;

    public void OnKeyPressed(ConsoleKey key)
    {
        if (interceptKeys)
            keyPressed?.Invoke(null, key);
    }

    // ── construction ─────────────────────────────────────────────────────────

    public DownloadEngine(Config config, SoulseekClientManager clientManager, IProgressReporter? progressReporter = null)
    {
        defaultConfig     = config;
        _clientManager    = clientManager;
        _progressReporter = progressReporter ?? NullProgressReporter.Instance;
    }


    // ── top-level entry point ─────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {

        (defaultConfig.inputType, extractor) = ExtractorRegistry.GetMatchingExtractor(defaultConfig.input, defaultConfig.inputType);
        if (extractor == null)
        {
            Logger.Fatal($"Could not find an extractor for input type {defaultConfig.inputType} and input {defaultConfig.input}");
            return;
        }

        Logger.Info($"Input ({defaultConfig.inputType}): {defaultConfig.input}");

        var extracted = await extractor.GetTracks(defaultConfig.input, defaultConfig.maxTracks, defaultConfig.offset, defaultConfig.reverse, defaultConfig);
        if (extracted == null)
        {
            Logger.Fatal($"Extractor failed to get tracks for input {defaultConfig.input}");
            return;
        }
        Queue.Jobs.Add(extracted);

        Logger.Debug("Got tracks");

        // Report the initial track list to the progress reporter.
        var allSongs = Queue.AllSongs().ToList();
        if (allSongs.Count > 0)
            _progressReporter.ReportTrackList(allSongs);

        defaultConfig.PostProcessArgs(Queue);

        Queue.UpgradeToAlbumMode(defaultConfig.album, defaultConfig.aggregate);
        Queue.SetAggregateItemNames();

        _contexts = JobPreparer.PrepareJobs(Queue, defaultConfig);

        if (defaultConfig.NeedLogin)
        {
            await _clientManager.WaitUntilReadyAsync(ct);
            searcher   = new Searcher(Client!, _registry, _registry, _progressReporter, defaultConfig.searchesPerTime, defaultConfig.searchRenewTime);
            downloader = new Downloader(Client!, _clientManager, _registry, _progressReporter);

            _ = Task.Run(() => UpdateLoop(appCts.Token), appCts.Token);
            Logger.Debug("Update task started");
        }

        await MainLoop();

        Logger.Debug("Exiting");
        appCts.Cancel();
    }


    // ── main dispatch loop ────────────────────────────────────────────────────

    async Task MainLoop()
    {
        if (Queue.Jobs.Count == 0) return;

        Queue.Jobs[0].PrintLines();

        for (int i = 0; i < Queue.Jobs.Count; i++)
        {
            if (i > 0) Console.WriteLine();

            var job    = Queue.Jobs[i];
            var ctx    = Ctx(job);
            var config = job.Config;

            Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());

            if (ctx.PreprocessTracks)
                Preprocessor.PreprocessJob(job, config);

            job.PrintLines();

            var existing = new List<SongJob>();
            var notFound = new List<SongJob>();

            // ── skip checks ──────────────────────────────────────────────────

            if (config.skipNotFound && !config.PrintResults && job is JobList sljSkipNF)
            {
                foreach (var song in sljSkipNF.Jobs.OfType<SongJob>())
                    if (TrySetNotFoundLastTime(song, ctx.IndexEditor))
                        notFound.Add(song);
            }
            else if (config.skipNotFound && !config.PrintResults && job.CanBeSkipped)
            {
                if (TrySetNotFoundLastTimeForJob(job))
                {
                    Logger.Info($"Download '{job.ToString(true)}' was not found during a prior run, skipping");
                    continue;
                }
            }

            if (config.skipExisting && !config.PrintResults)
            {
                if (job is JobList sljSkipEx)
                {
                    foreach (var song in sljSkipEx.Jobs.OfType<SongJob>().Where(s => s.State == JobState.Pending))
                        if (TrySetAlreadyExists(job, song, TrackSkipperContext.From(ctx, config)))
                            existing.Add(song);
                }
                else if (job.CanBeSkipped && TrySetJobAlreadyExists(job, ctx))
                {
                    Logger.Info($"Download '{job.ToString(true)}' already exists at {(job as AlbumJob)?.DownloadPath}, skipping");
                    ctx.IndexEditor?.Update();
                    ctx.PlaylistEditor?.Update();
                    continue;
                }
            }

            if (config.PrintTracks)
            {
                if (job is JobList sljPt)
                    Printing.PrintTracksTbd(sljPt.Jobs.OfType<SongJob>().Where(s => s.State == JobState.Pending).ToList(), existing, notFound, isNormal: true, config);
                job.PrintLines();
                continue;
            }

            // ── jobs that need source search first ───────────────────────────

            bool needSourceSearch = job is AlbumJob or AggregateJob or AlbumAggregateJob;

            if (needSourceSearch)
            {
                await _clientManager.WaitUntilReadyAsync(appCts.Token);

                _progressReporter.ReportJobStarted(job, parallel: false);

                bool         foundSomething = false;
                ResponseData responseData   = new ResponseData();

                if (job is AlbumJob albumJob)
                {
                    if (!albumJob.Query.IsDirectLink)
                        await searcher!.SearchAlbum(albumJob, config, responseData, appCts.Token);
                    else
                    {
                        try
                        {
                            _progressReporter.ReportJobFolderRetrieving(job);
                            await searcher!.SearchDirectLinkAlbum(albumJob, appCts.Token);
                        }
                        catch (UserOfflineException e)
                        {
                            Logger.Error("Error: " + e.Message);
                        }
                    }
                    foundSomething = albumJob.Results.Count > 0;
                }
                else if (job is AggregateJob aggJob)
                {
                    await searcher!.SearchAggregate(aggJob, config, responseData, appCts.Token);
                    foundSomething = aggJob.Songs.Count > 0;
                }
                else if (job is AlbumAggregateJob aabJob)
                {
                    var newAlbumJobs = await searcher!.SearchAggregateAlbum(aabJob, config, responseData, appCts.Token);
                    foreach (var aj in newAlbumJobs)
                    {
                        aj.ItemName = job.ItemName;
                        aj.Config   = job.Config;
                        var childCtx = new JobContext
                        {
                            IndexEditor      = ctx.IndexEditor,
                            PlaylistEditor   = ctx.PlaylistEditor,
                            PreprocessTracks = false,
                        };
                        _contexts[aj.Id] = childCtx;
                        Queue.Jobs.Add(aj);
                    }
                    job.State = JobState.Done;
                    foundSomething = newAlbumJobs.Count > 0;
                }

                _progressReporter.ReportJobCompleted(job, foundSomething, responseData.lockedFilesCount);

                if (!foundSomething)
                {
                    job.State         = JobState.Failed;
                    job.FailureReason = FailureReason.NoSuitableFileFound;

                    if (job is AlbumJob aj)
                        await OnCompleteExecutor.ExecuteAsync(aj, null, Ctx(aj));

                    if (!config.PrintResults)
                        ctx.IndexEditor?.Update();

                    continue;
                }

                if (job.State == JobState.Done) continue; // AlbumAggregateJob converted to children

                if (config.skipExisting && job is AggregateJob foundAggJob)
                {
                    var skipCtx = TrackSkipperContext.From(ctx, job.Config);
                    foreach (var song in foundAggJob.Songs)
                        TrySetAlreadyExists(foundAggJob, song, skipCtx);
                }
            }

            if (config.PrintResults)
            {
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await Printing.PrintResults(job, existing, notFound, config, searcher!);
                continue;
            }

            await DispatchDownload(job, ctx, notFound, existing);
        }

        if (Queue.Jobs.Count > 0 && !Queue.Jobs[^1].Config.DoNotDownload)
            Printing.PrintComplete(Queue);
    }



    async Task DispatchDownload(Job job, JobContext ctx, List<SongJob>? notFound, List<SongJob>? existing)
    {
        var config = job.Config;
        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        switch (job)
        {
            case SongJob sj:
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessSongJob(sj, ctx);
                break;

            case JobList jl when jl.Jobs.Count > 0 && jl.Jobs[0] is AlbumJob:
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessJobList(jl, ctx);
                break;

            case JobList jl:
            {
                var songs = jl.Jobs.OfType<SongJob>().ToList();
                Printing.PrintTracksTbd(songs.Where(s => s.State == JobState.Pending).ToList(),
                    existing ?? new(), notFound ?? new(), isNormal: true, config);

                int initialCount = songs.Count;
                int skipCount    = (notFound?.Count ?? 0) + (existing?.Count ?? 0);
                if (skipCount >= initialCount) return;

                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessSongListJob(jl, ctx);
                break;
            }

            case AlbumJob aj:
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessAlbumJob(aj, Ctx(aj));
                break;

            case AggregateJob ag:
                Printing.PrintTracksTbd(ag.Songs.Where(s => s.State == JobState.Pending).ToList(),
                    new(), new(), isNormal: false, config);
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessAggregateJob(ag, ctx);
                break;
        }
    }


    // ── per-job-type handlers ─────────────────────────────────────────────────

    async Task ProcessJobList(JobList job, JobContext ctx)
    {
        var config = job.Config;
        int failed = 0;

        foreach (var albumJob in job.Jobs.OfType<AlbumJob>())
        {
            albumJob.Config ??= job.Config;
            var childCtx = Ctx(albumJob);

            // Skip check
            if (config.skipExisting && albumJob.CanBeSkipped && TrySetJobAlreadyExists(albumJob, childCtx))
            {
                Logger.Info($"Download '{albumJob.ToString(true)}' already exists, skipping");
                childCtx.IndexEditor?.Update();
                continue;
            }

            // Source search
            Console.WriteLine();
            _progressReporter.ReportJobStarted(albumJob, parallel: false);

            var responseData = new ResponseData();
            bool found = false;

            if (!albumJob.Query.IsDirectLink)
                await searcher!.SearchAlbum(albumJob, config, responseData, appCts.Token);
            else
            {
                try
                {
                    _progressReporter.ReportJobFolderRetrieving(albumJob);
                    await searcher!.SearchDirectLinkAlbum(albumJob, appCts.Token);
                }
                catch (UserOfflineException e)
                {
                    Logger.Error("Error: " + e.Message);
                }
            }

            found = albumJob.Results.Count > 0;
            _progressReporter.ReportJobCompleted(albumJob, found, responseData.lockedFilesCount);

            if (!found)
            {
                albumJob.State         = JobState.Failed;
                albumJob.FailureReason = FailureReason.NoSuitableFileFound;
                await OnCompleteExecutor.ExecuteAsync(albumJob, null, childCtx);
                failed++;
                childCtx.IndexEditor?.Update();
                continue;
            }

            // Download
            await ProcessAlbumJob(albumJob, childCtx);

            if (albumJob.State != JobState.Done) failed++;
        }

        job.State = (failed == job.Jobs.Count && job.Jobs.Count > 0) ? JobState.Failed : JobState.Done;
        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
    }


    async Task ProcessSongJob(SongJob job, JobContext ctx)
    {
        var config    = job.Config;
        var organizer = new FileManager(job, config);
        var semaphore = new SemaphoreSlim(1);

        // If ResolvedTarget is set, pre-populate Candidates so search is skipped.
        if (job.ResolvedTarget != null && job.Candidates == null)
            job.Candidates = new List<FileCandidate> { job.ResolvedTarget };

        using var cts = new CancellationTokenSource();
        await DownloadSong(job, job, config, organizer, semaphore, cts,
            cancelOnFail: false, removeFromSource: false, organize: true);

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
    }


    async Task ProcessSongListJob(JobList job, JobContext ctx)
    {
        var config = job.Config;
        var songs = job.Jobs.OfType<SongJob>().ToList();
        var progressReporter = new IntervalProgressReporter(TimeSpan.FromSeconds(30), 5, songs);
        var semaphore = new SemaphoreSlim(config.concurrentProcesses);
        var organizer = new FileManager(job, config);

        var downloadTasks = songs.Select(async (song, _) =>
        {
            using var cts       = new CancellationTokenSource();
            bool wasInitial     = song.State == JobState.Pending;

            await DownloadSong(song, job, config, organizer, semaphore, cts, cancelOnFail: false,
                removeFromSource: true, organize: true);

            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();

            if (wasInitial)
            {
                progressReporter.MaybeReport(song.State);
                int downloaded = songs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
                int failed     = songs.Count(s => s.State == JobState.Failed);
                _progressReporter.ReportOverallProgress(downloaded, failed, songs.Count);
            }
        });

        await Task.WhenAll(downloadTasks);

        int dl = songs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
        int fl = songs.Count(s => s.State == JobState.Failed);
        _progressReporter.ReportListProgress(job, dl, fl, songs.Count);

        if (config.removeTracksFromSource && songs.All(s => s.State == JobState.Done || s.State == JobState.AlreadyExists))
            await extractor!.RemoveTrackFromSource(new SongJob(job.QueryTrack));
    }


    async Task ProcessAggregateJob(AggregateJob job, JobContext ctx)
    {
        var config    = job.Config;
        var songs     = job.Songs;
        var semaphore = new SemaphoreSlim(config.concurrentProcesses);
        var organizer = new FileManager(job, config);

        var downloadTasks = songs.Select(async song =>
        {
            using var cts = new CancellationTokenSource();
            await DownloadSong(song, job, config, organizer, semaphore, cts, cancelOnFail: false,
                removeFromSource: false, organize: true);
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
        });

        await Task.WhenAll(downloadTasks);
    }


    async Task ProcessAlbumJob(AlbumJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(job, config);
        List<SongJob>? chosenFiles = null;
        var retrievedFolders = new HashSet<string>();
        bool succeeded = false;
        string? filterStr = null;
        int index = 0;
        int albumTrackCountRetries = config.albumTrackCountMaxRetries;

        async Task RunAlbumDownloads(AlbumFolder folder, SemaphoreSlim semaphore, CancellationTokenSource cts)
        {
            var tasks = folder.Files.Select(async af =>
            {
                if (af.State != JobState.Pending) return;
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await DownloadSong(af, job, config, organizer, semaphore, cts, cancelOnFail: true,
                    removeFromSource: false, organize: true);
            });
            await Task.WhenAll(tasks);
        }

        while (job.Results.Count > 0 && !config.albumArtOnly)
        {
            bool wasInteractive = false;
            bool retrieveCurrent = true;
            index = 0;

            AlbumFolder chosenFolder;

            if (SelectAlbumVersion != null)
            {
                chosenFolder = await SelectAlbumVersion(job);
                if (chosenFolder == null) { index = -1; break; }  // skip or quit
                config = job.Config;  // callback may have mutated job.Config (e.g. exit interactive mode)
                retrieveCurrent = true;
                index = job.Results.Contains(chosenFolder) ? job.Results.IndexOf(chosenFolder) : 0;
            }
            else if (config.interactiveMode)
            {
                wasInteractive = true;
                var interactive = new InteractiveModeManager(job, Queue!, job.Results, true,
                    retrievedFolders,
                    async (f) => await RetrieveFullFolderCancellableAsync(f, config),
                    filterStr);
                var result = await interactive.Run();

                filterStr = result.FilterStr;

                if (result.Index == -1) break;           // 's': skip this album
                if (result.Index == -2) return;          // 'q': quit entirely
                if (result.ExitInteractiveMode)
                    config = job.Config;                 // 'y': profile update happened inside IMM

                if (result.Folder == null) break;
                chosenFolder = result.Folder;
                retrieveCurrent = result.RetrieveCurrentFolder;
                index = job.Results.Contains(chosenFolder) ? job.Results.IndexOf(chosenFolder) : 0;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(filterStr))
                {
                    index = job.Results.FindIndex(f => f.Files.Any(af => af.ResolvedTarget!.Filename.ContainsIgnoreCase(filterStr)));
                    if (index == -1) break;
                }
                chosenFolder = job.Results[index];

                // Verify track counts after full folder retrieval if needed.
                if (config.albumTrackCountMaxRetries > 0
                    && (job.Query.MaxTrackCount > 0 || (job.Query.MinTrackCount > 0 && job.Query.Album.Length > 0)))
                {
                    if (!retrievedFolders.Contains(chosenFolder.FolderPath))
                    {
                        (var wasCancelled, _) = await RetrieveFullFolderCancellableAsync(chosenFolder, config,
                            "Verifying album track count.\n    Retrieving full folder contents... (Press 'c' to cancel)");
                        if (!wasCancelled)
                            retrievedFolders.Add(chosenFolder.FolderPath);
                    }
                    int newCount = chosenFolder.Files.Count(af => !af.IsNotAudio);
                    bool trackCountFailed = false;
                    if (job.Query.MaxTrackCount > 0 && newCount > job.Query.MaxTrackCount)
                    { Logger.Info($"New file count ({newCount}) above maximum ({job.Query.MaxTrackCount}), skipping folder"); trackCountFailed = true; }
                    if (job.Query.MinTrackCount > 0 && newCount < job.Query.MinTrackCount)
                    { Logger.Info($"New file count ({newCount}) below minimum ({job.Query.MinTrackCount}), skipping folder"); trackCountFailed = true; }

                    if (trackCountFailed)
                    {
                        job.Results.RemoveAt(index);
                        if (--albumTrackCountRetries <= 0)
                        {
                            Logger.Info($"Failed album track count condition {config.albumTrackCountMaxRetries} times, skipping album.");
                            job.State         = JobState.Failed;
                            job.FailureReason = FailureReason.NoSuitableFileFound;
                            break;
                        }
                        continue;
                    }
                }
            }

            if (job.Query.IsDirectLink)
                retrievedFolders.Add(chosenFolder.FolderPath);

            organizer.SetremoteBaseDir(chosenFolder.FolderPath);

            if (SelectAlbumVersion == null && !config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                Printing.PrintAlbum(chosenFolder);
            }

            using var semaphore = new SemaphoreSlim(999); // SemaphoreSlim uncapped — see §8 stale-detection fix note
            using var cts       = new CancellationTokenSource();

            bool userCancelled = false;
            void onKey(object? s, ConsoleKey k)
            {
                if (k == ConsoleKey.C && !userCancelled)
                { userCancelled = true; cts.Cancel(); }
            }
            interceptKeys = true;
            keyPressed   += onKey;

            try
            {
                await RunAlbumDownloads(chosenFolder, semaphore, cts);

                if (!config.noBrowseFolder && retrieveCurrent && !retrievedFolders.Contains(chosenFolder.FolderPath))
                {
                    Console.WriteLine();
                    (var wasCancelled, var newFilesFound) = await RetrieveFullFolderCancellableAsync(chosenFolder, config);
                    if (!wasCancelled)
                    {
                        retrievedFolders.Add(chosenFolder.FolderPath);
                        if (newFilesFound > 0)
                        {
                            Logger.Info($"Found {newFilesFound} more files, downloading:");
                            await RunAlbumDownloads(chosenFolder, semaphore, cts);
                        }
                        else
                        {
                            Logger.Info("No more files found.");
                        }
                    }
                }

                job.ResolvedTarget = chosenFolder;
                succeeded          = true;
                chosenFiles        = chosenFolder.Files;
                break;
            }
            catch (OperationCanceledException)
            {
                if (userCancelled)
                {
                    Console.WriteLine();
                    Logger.Info("Download cancelled.");

                    if (chosenFolder.Files.Any(af => af.State == JobState.Done && !string.IsNullOrEmpty(af.DownloadPath)))
                    {
                        string defaultAction = config.DeleteAlbumOnFail ? "Yes" : config.IgnoreAlbumFail ? "No" : $"Move to {config.failedAlbumPath}";
                        Console.Write($"Delete files? [Y/n] (default: {defaultAction}): ");
                        var res = Console.IsInputRedirected ? "" : (Console.ReadLine() ?? "").Trim().ToLower();
                        if (res == "y")
                            HandleAlbumFail(chosenFolder, deleteDownloaded: true, config);
                        else if (res == "" && !config.IgnoreAlbumFail)
                            HandleAlbumFail(chosenFolder, config.DeleteAlbumOnFail, config);
                    }

                    if (SelectAlbumVersion == null && !config.interactiveMode)
                    {
                        Logger.Info("Entering interactive mode");
                        config.interactiveMode = true;
                        job.Config = job.Config.UpdateProfiles(job, Queue!);
                        job.PrintLines();
                    }
                }
                else
                {
                    if (!config.IgnoreAlbumFail)
                        HandleAlbumFail(chosenFolder, config.DeleteAlbumOnFail, config);
                }
            }
            finally
            {
                interceptKeys  = false;
                keyPressed    -= onKey;
            }

            if (!succeeded)
            {
                organizer.SetremoteBaseDir(null);
                job.Results.RemoveAt(index);
            }
        }

        if (succeeded && chosenFiles != null)
        {
            job.State = JobState.Done;

            var downloadedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.State == JobState.Done && !string.IsNullOrEmpty(af.DownloadPath));

            if (downloadedAudio.Any())
            {
                job.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(af => af.DownloadPath!));
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, job.DownloadPath);
                if (config.removeTracksFromSource)
                    await extractor!.RemoveTrackFromSource(new SongJob(job.QueryTrack));
            }
        }
        else if (index != -1)
        {
            job.State         = JobState.Failed;
            job.FailureReason = FailureReason.NoSuitableFileFound;
        }

        List<SongJob>? additionalImages = null;

        if (config.albumArtOnly || (succeeded && config.albumArtOption != AlbumArtOption.Default))
        {
            Logger.Info("Downloading additional images:");
            additionalImages = await DownloadImages(job, ctx, organizer, job.ResolvedTarget);

            if (chosenFiles != null && additionalImages?.Any() == true)
            {
                var addedPaths = additionalImages
                    .Select(af => Utils.NormalizedPath(af.DownloadPath ?? ""))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToHashSet();

                chosenFiles.RemoveAll(af => af.IsNotAudio
                    && !string.IsNullOrEmpty(af.DownloadPath)
                    && addedPaths.Contains(Utils.NormalizedPath(af.DownloadPath)));

                chosenFiles.AddRange(additionalImages);
            }
        }

        if (chosenFiles != null && !string.IsNullOrEmpty(job.DownloadPath))
            organizer.OrganizeAlbum(job, chosenFiles, additionalImages);

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await OnCompleteExecutor.ExecuteAsync(job, null, ctx);
    }


    // ── single-song download ──────────────────────────────────────────────────

    async Task DownloadSong(SongJob song, Job job, Config config, FileManager organizer,
        SemaphoreSlim semaphore, CancellationTokenSource cts, bool cancelOnFail,
        bool removeFromSource, bool organize)
    {
        if (song.State != JobState.Pending) return;

        await semaphore.WaitAsync(cts.Token);

        int    tries         = config.unknownErrorRetries;
        string savedFilePath = "";

        while (tries > 0)
        {
            await _clientManager.WaitUntilReadyAsync(appCts.Token);
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                (savedFilePath, _) = await SearchAndDownloadSong(song, job, config, organizer, cts);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    Logger.DebugError($"{ex}");
                else
                    Logger.Debug($"Cancelled: {song}");

                if (!_clientManager.IsConnectedAndLoggedIn)
                {
                    continue;
                }
                else if (ex is SearchAndDownloadException sdEx)
                {
                    song.State         = JobState.Failed;
                    song.FailureReason = sdEx.Reason;
                    _progressReporter.ReportStateChanged(song);

                    if (cancelOnFail)
                    {
                        cts.Cancel();
                        throw new OperationCanceledException();
                    }
                }
                else if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    song.State         = JobState.Failed;
                    song.FailureReason = FailureReason.Other;
                    _progressReporter.ReportStateChanged(song);
                    throw;
                }
                else
                {
                    tries--;
                    continue;
                }
            }

            break;
        }

        if (tries == 0 && cancelOnFail)
        {
            song.State         = JobState.Failed;
            song.FailureReason = FailureReason.Other;
            _progressReporter.ReportStateChanged(song);
            cts.Cancel();
            throw new OperationCanceledException();
        }

        if (savedFilePath.Length > 0)
        {
            song.State        = JobState.Done;
            song.DownloadPath = savedFilePath;
            _progressReporter.ReportStateChanged(song, song.ChosenCandidate);

            if (removeFromSource && config.removeTracksFromSource)
            {
                try { await extractor!.RemoveTrackFromSource(song); }
                catch (Exception ex) { Logger.Error($"Error removing track from source: {ex.Message}"); }
            }
        }

        if (song.State == JobState.Done && organize)
            organizer.OrganizeSong(song);

        var jobCtx2 = Ctx(job);
        if (job.Config.HasOnComplete)
        {
            _progressReporter.ReportOnCompleteStart(song);
            await OnCompleteExecutor.ExecuteAsync(job, song, jobCtx2);
            _progressReporter.ReportOnCompleteEnd(song);
        }

        semaphore.Release();
    }


    /// <summary>
    /// Searches for candidates for <paramref name="song"/> then downloads the best one.
    /// Returns (savedFilePath, chosenFile).
    /// Throws <see cref="SearchAndDownloadException"/> on unrecoverable search/download failures.
    /// </summary>
    async Task<(string, SlFile?)> SearchAndDownloadSong(SongJob song, Job job, Config config,
        FileManager organizer, CancellationTokenSource cts)
    {
        var responseData = new ResponseData();

        // Skip search if candidates are pre-set (ResolvedTarget / direct download).
        if (song.Candidates == null)
        {
            _progressReporter.ReportSongSearching(song);

            await searcher!.SearchSong(song, config, responseData, appCts.Token,
                onSearch: () => _progressReporter.ReportSongSearching(song));
        }

        var candidates = song.Candidates;

        if (candidates == null || candidates.Count == 0)
        {
            _progressReporter.ReportSongNotFound(song);
            throw new NoSuitableFileFoundException();
        }

        // Try candidates in order until one succeeds.
        int tried = 0;
        foreach (var candidate in candidates)
        {
            tried++;
            string outputPath = organizer.GetSavePath(candidate.Filename);

            try
            {
                // ReportDownloadStart is called inside DownloadFile (via Downloader).
                await downloader!.DownloadFile(candidate, outputPath, song, config, appCts.Token);
                _registry.UserSuccessCounts.AddOrUpdate(candidate.Username, 1, (_, c) => c + 1);
                return (outputPath, candidate.File);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.DebugError($"Download attempt {tried} failed: {ex.Message}");
                if (tried >= candidates.Count)
                {
                    _progressReporter.ReportSongFailed(song);
                    throw new AllDownloadsFailedException();
                }
            }
        }

        throw new NoSuitableFileFoundException();
    }




    // ── skip helpers ──────────────────────────────────────────────────────────

    bool TrySetAlreadyExists(Job job, SongJob song, TrackSkipperContext skipCtx)
    {
        string? path = null;
        var jobCtx = Ctx(job);

        if (jobCtx.OutputDirSkipper != null)
        {
            if (!jobCtx.OutputDirSkipper.IndexIsBuilt) jobCtx.OutputDirSkipper.BuildIndex();
            jobCtx.OutputDirSkipper.SongExists(song, skipCtx, out path);
        }

        if (path == null && jobCtx.MusicDirSkipper != null)
        {
            if (!jobCtx.MusicDirSkipper.IndexIsBuilt)
            {
                Logger.Info("Building music directory index..");
                jobCtx.MusicDirSkipper.BuildIndex();
            }
            jobCtx.MusicDirSkipper.SongExists(song, skipCtx, out path);
        }

        if (path != null)
        {
            song.State        = JobState.AlreadyExists;
            song.DownloadPath = path;
        }

        return path != null;
    }

    bool TrySetJobAlreadyExists(Job job, JobContext ctx)
    {
        if (job is not AlbumJob aj) return false;

        var skipCtx = TrackSkipperContext.From(ctx, job.Config);
        string? path = null;

        if (ctx.OutputDirSkipper != null)
        {
            if (!ctx.OutputDirSkipper.IndexIsBuilt) ctx.OutputDirSkipper.BuildIndex();
            ctx.OutputDirSkipper.AlbumExists(aj, skipCtx, out path);
        }

        if (path == null && ctx.MusicDirSkipper != null)
        {
            if (!ctx.MusicDirSkipper.IndexIsBuilt)
            {
                Logger.Info("Building music directory index..");
                ctx.MusicDirSkipper.BuildIndex();
            }
            ctx.MusicDirSkipper.AlbumExists(aj, skipCtx, out path);
        }

        if (path != null)
        {
            job.State = JobState.Skipped;
            ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, path);
        }

        return path != null;
    }

    bool TrySetNotFoundLastTime(SongJob song, M3uEditor? indexEditor)
    {
        if (indexEditor == null) return false;
        var prev = indexEditor.PreviousRunResult(song);
        if (prev == null) return false;
        if (prev.FailureReason == FailureReason.NoSuitableFileFound || prev.State == JobState.NotFoundLastTime)
        {
            song.State = JobState.NotFoundLastTime;
            return true;
        }
        return false;
    }

    bool TrySetNotFoundLastTimeForJob(Job job)
    {
        var jobCtx = Ctx(job);
        if (jobCtx.IndexEditor == null) return false;
        IndexEntry? prev = null;

        if (job is AlbumJob aj)
            prev = jobCtx.IndexEditor.PreviousRunResult(aj);

        if (prev == null) return false;
        if (prev.FailureReason == FailureReason.NoSuitableFileFound || prev.State == JobState.NotFoundLastTime)
        {
            job.State = JobState.Skipped;
            return true;
        }
        return false;
    }


    // ── album failure handling ────────────────────────────────────────────────

    void HandleAlbumFail(AlbumFolder folder, bool deleteDownloaded, Config config)
    {
        if (deleteDownloaded)
            Logger.Info("Deleting album files");
        else if (config.failedAlbumPath.Length > 0)
            Logger.Info($"Moving album files to {config.failedAlbumPath}");

        foreach (var af in folder.Files)
        {
            if (string.IsNullOrEmpty(af.DownloadPath) || !File.Exists(af.DownloadPath)) continue;
            try
            {
                if (deleteDownloaded || af.DownloadPath.EndsWith(".incomplete"))
                {
                    File.Delete(af.DownloadPath);
                }
                else if (config.failedAlbumPath.Length > 0)
                {
                    var newPath = Path.Join(config.failedAlbumPath, Path.GetRelativePath(config.parentDir, af.DownloadPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Utils.Move(af.DownloadPath, newPath);
                }

                Utils.DeleteAncestorsIfEmpty(Path.GetDirectoryName(af.DownloadPath)!, config.parentDir);
            }
            catch (Exception e)
            {
                Logger.Error($"Error: Unable to move or delete file '{af.DownloadPath}' after album fail: {e}");
            }
        }
    }


    // ── folder retrieval ──────────────────────────────────────────────────────

    public async Task<(bool WasCancelled, int FileCount)> RetrieveFullFolderCancellableAsync(
        AlbumFolder folder, Config config, string? customMessage = null)
    {
        customMessage ??= "Getting all files in folder... (Press 'c' to cancel)";
        Logger.Info(customMessage);

        using var cts = new CancellationTokenSource();
        var task      = searcher!.CompleteFolder(folder, cts.Token);

        while (!task.IsCompleted)
        {
            if (Console.KeyAvailable && !Console.IsInputRedirected)
            {
                if (Console.ReadKey(true).Key == ConsoleKey.C)
                {
                    cts.Cancel();
                    try { await task; } catch (OperationCanceledException) { }
                    Logger.Info("Folder retrieval cancelled by user.");
                    return (true, 0);
                }
            }
            await Task.Delay(100);
        }

        int fileCount = await task;
        return (false, fileCount);
    }


    // ── album art download ────────────────────────────────────────────────────

    async Task<List<SongJob>> DownloadImages(AlbumJob job, JobContext ctx, FileManager fileManager, AlbumFolder? chosenFolder)
    {
        var result = new List<SongJob>();
        var config = job.Config;
        long mSize = 0;
        int  mCount = 0;
        var option = config.albumArtOption;

        if (chosenFolder != null)
        {
            string dir = chosenFolder.FolderPath;
            fileManager.SetDefaultFolderName(Path.GetFileName(Utils.NormalizedPath(dir)));
        }

        if (option == AlbumArtOption.Default) return result;

        int[]? sortedLengths = null;
        if (chosenFolder?.Files.Any(af => !af.IsNotAudio) == true)
            sortedLengths = chosenFolder.Files.Where(af => !af.IsNotAudio)
                .Select(af => af.Query.Length).OrderBy(x => x).ToArray();

        var imageFolders = job.Results
            .Where(f => chosenFolder == null || Searcher.AlbumsAreSimilar(chosenFolder, f, sortedLengths))
            .Select(f => f.Files.Where(af => Utils.IsImageFile(af.ResolvedTarget!.Filename)).ToList())
            .Where(ls => ls.Count > 0)
            .ToList();

        if (imageFolders.Count == 0)
        { Logger.Info("No images found"); return result; }

        if (imageFolders.Count == 1 && imageFolders[0].All(af => af.State != JobState.Pending))
        { Logger.Info("No additional images found"); return result; }

        if (option == AlbumArtOption.Largest)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Max(af => af.ResolvedTarget!.File.Size) / 1024 / 100)
                .ThenByDescending(ls => ls[0].ResolvedTarget!.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.ResolvedTarget!.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mSize = chosenFolder.Files
                    .Where(af => af.State == JobState.Done && Utils.IsImageFile(af.DownloadPath ?? ""))
                    .Select(af => af.ResolvedTarget!.File.Size)
                    .DefaultIfEmpty(0).Max();
        }
        else if (option == AlbumArtOption.Most)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Count)
                .ThenByDescending(ls => ls[0].ResolvedTarget!.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.ResolvedTarget!.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mCount = chosenFolder.Files.Count(af => af.State == JobState.Done && Utils.IsImageFile(af.DownloadPath ?? ""));
        }

        bool needsDownload(List<SongJob> ls) => option == AlbumArtOption.Most
            ? mCount < ls.Count
            : option == AlbumArtOption.Largest
                ? mSize < ls.Max(af => af.ResolvedTarget!.File.Size) - 1024 * 50
                : true;

        while (imageFolders.Count > 0)
        {
            int    imgIdx        = 0;
            bool   wasInteractive = config.interactiveMode;
            List<SongJob> imgs;

            if (config.interactiveMode && SelectAlbumVersion != null)
            {
                // Wrap image folders as synthetic AlbumFolders for interactive picker.
                var syntheticFolders = imageFolders.Select((ls, idx) => new AlbumFolder(
                    ls[0].ResolvedTarget!.Response.Username,
                    Utils.GreatestCommonDirectorySlsk(ls.Select(af => af.ResolvedTarget!.Filename)),
                    ls)).ToList();

                var syntheticJob = new AlbumJob(job.Query) { Results = syntheticFolders, Config = config };
                _contexts[syntheticJob.Id] = new JobContext();
                var pickedFolder = await SelectAlbumVersion(syntheticJob);
                if (pickedFolder == null) break;
                imgIdx = syntheticFolders.IndexOf(pickedFolder);
                if (imgIdx == -1) break;
                imgs = imageFolders[imgIdx];
            }
            else
            {
                imgs = imageFolders[0];
            }

            imageFolders.RemoveAt(imgIdx);

            if (imgs.All(af => af.State == JobState.Done || af.State == JobState.AlreadyExists)
                || (!wasInteractive && !needsDownload(imgs)))
            {
                Logger.Info("Image requirements already satisfied.");
                return result;
            }

            if (!config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                // Print images as a mini-album.
                var syntheticFolder = new AlbumFolder(
                    imgs[0].ResolvedTarget!.Response.Username,
                    Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename)),
                    imgs);
                Printing.PrintAlbum(syntheticFolder);
            }

            fileManager.downloadingAdditionalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename)));

            bool allSucceeded = true;
            using var semaphore = new SemaphoreSlim(1);
            using var cts       = new CancellationTokenSource();

            bool userCancelled = false;
            void onKey(object? s, ConsoleKey k) { if (k == ConsoleKey.C) { userCancelled = true; cts.Cancel(); } }
            interceptKeys = true;
            keyPressed   += onKey;

            try
            {
                foreach (var af in imgs)
                {
                    if (af.ResolvedTarget != null && af.Candidates == null)
                        af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                    await DownloadSong(af, job, config, fileManager, semaphore, cts, cancelOnFail: false,
                        removeFromSource: false, organize: true);
                    if (af.State == JobState.Done)
                        result.Add(af);
                    else
                        allSucceeded = false;
                }
            }
            catch (OperationCanceledException)
            {
                if (userCancelled)
                {
                    Console.WriteLine();
                    Logger.Info("Download cancelled.");
                    if (imgs.Any(af => af.State == JobState.Done && !string.IsNullOrEmpty(af.DownloadPath)))
                    {
                        Console.Write("Delete files? [Y/n] (default: Yes): ");
                        var res = Console.IsInputRedirected ? "" : (Console.ReadLine() ?? "").Trim().ToLower();
                        if (res == "y" || res == "")
                        {
                            var imgFolder = new AlbumFolder(imgs[0].ResolvedTarget!.Response.Username,
                                Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename)), imgs);
                            HandleAlbumFail(imgFolder, true, config);
                        }
                    }
                    if (!config.interactiveMode)
                    { Logger.Info("Entering interactive mode"); config.interactiveMode = true; }
                    continue;
                }
                throw;
            }
            finally
            {
                interceptKeys  = false;
                keyPressed    -= onKey;
            }

            if (allSucceeded) break;
        }

        return result;
    }


    // ── update / stale-detection loop ─────────────────────────────────────────

    async Task UpdateLoop(CancellationToken cancellationToken)
    {
        while (!appCts.IsCancellationRequested)
        {
            try
            {
                if (_clientManager.IsConnectedAndLoggedIn)
                {
                    // Prune completed searches (or those without a handler task)
                    foreach (var (song, info) in _registry.Searches)
                        if (info.Task == null || info.Task.IsCompleted)
                            _registry.Searches.TryRemove(song, out _);

                    // Check for stale downloads
                    foreach (var (filename, ad) in _registry.Downloads)
                    {
                        if (ad == null) { _registry.Downloads.TryRemove(filename, out _); continue; }

                        var song = ad.Song;
                        int maxStale = ad.Song.FileSize > 0 ? defaultConfig.maxStaleTime : int.MaxValue;
                        if (song.LastActivityTime.HasValue &&
                            (DateTime.Now - song.LastActivityTime.Value).TotalMilliseconds > maxStale)
                        {
                            Logger.Debug($"Cancelling stale download: {song}");
                            song.State = JobState.Failed;
                            try { ad.Cts.Cancel(); } catch { }
                            _registry.Downloads.TryRemove(filename, out _);
                        }
                    }
                }

                await Task.Delay(updateInterval, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error($"Error in update loop: {ex.Message}");
                try { await Task.Delay(1000, cancellationToken); } catch { break; }
            }
        }
    }
}
