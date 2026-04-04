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

    public JobQueue? Queue { get; private set; } = null;

    private Dictionary<Guid, JobContext> _contexts = new();

    private JobContext Ctx(Job job) => _contexts[job.Id];

    // ── public state (read by Searcher / Downloader) ─────────────────────────

    public ISoulseekClient?  Client              => _clientManager.Client;
    public bool              IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;
    public IProgressReporter ProgressReporter    => _progressReporter;

    // Session state (Decoupled)
    private readonly SessionRegistry _registry = new();


    // ── injectable CLI callbacks ──────────────────────────────────────────────

    public Func<AlbumQueryJob, Task<AlbumDownloadJob?>>? SelectAlbumVersion { get; set; }

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

        var jobList = await extractor.GetTracks(defaultConfig.input, defaultConfig.maxTracks, defaultConfig.offset, defaultConfig.reverse, defaultConfig);
        if (jobList == null || jobList.Count == 0)
        {
            Logger.Fatal($"Extractor failed to get tracks for input {defaultConfig.input}");
            return;
        }
        Queue = new JobQueue();
        foreach (var j in jobList) Queue.Enqueue(j);

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
        if (Queue == null || Queue.Count == 0) return;

        var cfg0 = Queue[0].Config;
        bool enableParallelSearch = cfg0.parallelAlbumSearch
            && !cfg0.PrintResults && !cfg0.PrintTracks
            && Queue.Jobs.Any(j => j is AlbumQueryJob or AlbumAggregateQueryJob);

        var parallelSearches  = new List<(Job job, Task<(bool, ResponseData)> task)>();
        var parallelSemaphore = new SemaphoreSlim(cfg0.parallelAlbumSearchProcesses);

        Queue[0].PrintLines();

        for (int i = 0; i < Queue.Count; i++)
        {
            if (!enableParallelSearch && i > 0) Console.WriteLine();

            var job    = Queue[i];
            var ctx    = Ctx(job);
            var config = job.Config;

            Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());

            if (ctx.PreprocessTracks)
                Preprocessor.PreprocessJob(job, config);

            if (!enableParallelSearch)
                job.PrintLines();

            var existing = new List<SongJob>();
            var notFound = new List<SongJob>();

            // ── skip checks ──────────────────────────────────────────────────

            if (config.skipNotFound && !config.PrintResults && job is SongListQueryJob sljSkipNF)
            {
                foreach (var song in sljSkipNF.Songs)
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
                if (job is SongListQueryJob sljSkipEx)
                {
                    foreach (var song in sljSkipEx.Songs.Where(s => s.State == TrackState.Initial))
                        if (TrySetAlreadyExists(job, song, TrackSkipperContext.From(ctx, config)))
                            existing.Add(song);
                }
                else if (job.CanBeSkipped && TrySetJobAlreadyExists(job, ctx))
                {
                    Logger.Info($"Download '{job.ToString(true)}' already exists at {(job as DownloadJob)?.DownloadPath}, skipping");
                    ctx.IndexEditor?.Update();
                    ctx.PlaylistEditor?.Update();
                    continue;
                }
            }

            if (config.PrintTracks)
            {
                if (job is SongListQueryJob sljPt)
                    Printing.PrintTracksTbd(sljPt.Songs.Where(s => s.State == TrackState.Initial).ToList(), existing, notFound, isNormal: true, config);
                job.PrintLines();
                continue;
            }

            // ── jobs that need source search first ───────────────────────────

            bool needSourceSearch = job is AlbumQueryJob or AggregateQueryJob or AlbumAggregateQueryJob;

            if (needSourceSearch)
            {
                await _clientManager.WaitUntilReadyAsync(appCts.Token);

                async Task<(bool, ResponseData)> sourceSearch()
                {
                    await parallelSemaphore.WaitAsync();

                    _progressReporter.ReportJobStarted(job, enableParallelSearch);

                    bool         foundSomething = false;
                    ResponseData responseData   = new ResponseData();

                    if (job is AlbumQueryJob albumJob)
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
                        foundSomething = albumJob.FoundFolders.Count > 0;
                    }
                    else if (job is AggregateQueryJob aggJob)
                    {
                        await searcher!.SearchAggregate(aggJob, config, responseData, appCts.Token);
                        foundSomething = aggJob.Songs.Count > 0;
                    }
                    else if (job is AlbumAggregateQueryJob aabJob)
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
                            Queue.Enqueue(aj);
                        }
                        // Mark this job done; it only spawns AlbumJobs.
                        job.State = JobState.Done;
                        foundSomething = newAlbumJobs.Count > 0;
                    }

                    _progressReporter.ReportJobCompleted(job, foundSomething, responseData.lockedFilesCount);

                    if (!foundSomething)
                    {
                        job.State         = JobState.Failed;
                        job.FailureReason = FailureReason.NoSuitableFileFound;

                        if (job is AlbumQueryJob aj)
                            await OnCompleteExecutor.ExecuteAsync(aj, null, Ctx(aj));
                    }

                    parallelSemaphore.Release();
                    return (foundSomething, responseData);
                }

                if (!enableParallelSearch || job is not (AlbumQueryJob or AlbumAggregateQueryJob))
                {
                    (bool found, ResponseData _) = await sourceSearch();

                    if (!found)
                    {
                        if (!config.PrintResults)
                            ctx.IndexEditor?.Update();

                        // AlbumAggregateQueryJob marks itself Done after spawning children; no further action needed.
                        if (job.State == JobState.Done) continue;
                        continue;
                    }

                    if (job.State == JobState.Done) continue; // AlbumAggregateQueryJob converted to children

                    if (config.skipExisting && job is AggregateQueryJob foundAggJob)
                    {
                        var skipCtx = TrackSkipperContext.From(ctx, job.Config);
                        foreach (var song in foundAggJob.Songs)
                            TrySetAlreadyExists(foundAggJob, song, skipCtx);
                    }
                }
                else
                {
                    parallelSearches.Add((job, sourceSearch()));
                    continue;
                }
            }

            if (config.PrintResults)
            {
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await Printing.PrintResults(job, existing, notFound, config, searcher!);
                continue;
            }

            if (parallelSearches.Count > 0 && !(job is AlbumQueryJob or AlbumAggregateQueryJob))
                await FlushParallelSearches(parallelSearches);

            if (!enableParallelSearch || !(job is AlbumQueryJob or AlbumAggregateQueryJob))
                await DispatchDownload(job, ctx, notFound, existing);
        }

        if (parallelSearches.Count > 0)
            await FlushParallelSearches(parallelSearches);

        if (Queue.Count > 0 && !Queue[^1].Config.DoNotDownload)
            Printing.PrintComplete(Queue);
    }


    async Task FlushParallelSearches(List<(Job job, Task<(bool, ResponseData)> task)> parallelSearches)
    {
        await Task.WhenAll(parallelSearches.Select(x => x.task));

        foreach (var (job, task) in parallelSearches)
        {
            (bool found, _) = task.Result;
            var jobCtx = Ctx(job);
            if (found)
            {
                Logger.Info($"Downloading: {job}");
                await DispatchDownload(job, jobCtx, null, null);
            }
            else if (!job.Config.PrintResults)
            {
                job.State         = JobState.Failed;
                job.FailureReason = FailureReason.NoSuitableFileFound;
                jobCtx.IndexEditor?.Update();
            }
        }

        parallelSearches.Clear();
    }


    async Task DispatchDownload(Job job, JobContext ctx, List<SongJob>? notFound, List<SongJob>? existing)
    {
        var config = job.Config;
        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        switch (job)
        {
            case SongDownloadJob sdj:
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessSongDownloadJob(sdj, ctx);
                break;

            case AlbumListJob alj:
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessAlbumListJob(alj, ctx);
                break;

            case SongListQueryJob slj:
                Printing.PrintTracksTbd(slj.Songs.Where(s => s.State == TrackState.Initial).ToList(),
                    existing ?? new(), notFound ?? new(), isNormal: true, config);

                int initialCount = slj.Songs.Count;
                int skipCount    = (notFound?.Count ?? 0) + (existing?.Count ?? 0);
                if (skipCount >= initialCount) return;

                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessSongListJob(slj, ctx);
                break;

            case AlbumQueryJob aj:
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessAlbumJob(aj, Ctx(aj));
                break;

            case AggregateQueryJob ag:
                Printing.PrintTracksTbd(ag.Songs.Where(s => s.State == TrackState.Initial).ToList(),
                    new(), new(), isNormal: false, config);
                await _clientManager.WaitUntilReadyAsync(appCts.Token);
                await ProcessAggregateJob(ag, ctx);
                break;
        }
    }


    // ── per-job-type handlers ─────────────────────────────────────────────────

    async Task ProcessAlbumListJob(AlbumListJob job, JobContext ctx)
    {
        var config = job.Config;
        int failed = 0;

        foreach (var albumJob in job.Albums)
        {
            albumJob.Config ??= job.Config;

            var childCtx = new JobContext
            {
                IndexEditor      = ctx.IndexEditor,
                PlaylistEditor   = ctx.PlaylistEditor,
                PreprocessTracks = false,  // preprocessed with parent AlbumListJob
            };
            _contexts[albumJob.Id] = childCtx;

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

            found = albumJob.FoundFolders.Count > 0;
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

        job.State = (failed == job.Albums.Count && job.Albums.Count > 0) ? JobState.Failed : JobState.Done;
        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
    }


    async Task ProcessSongDownloadJob(SongDownloadJob job, JobContext ctx)
    {
        var config    = job.Config;
        var organizer = new FileManager(job, config);
        var semaphore = new SemaphoreSlim(1);

        // Wrap the pre-resolved target in a SongJob with Candidates pre-populated
        // so SearchAndDownloadSong skips the search phase.
        var song = new SongJob(job.Origin)
        {
            Candidates = new List<FileCandidate> { job.Target },
            ItemNumber = job.ItemNumber,
            LineNumber = job.LineNumber,
        };

        using var cts = new CancellationTokenSource();
        await DownloadSong(song, job, config, organizer, semaphore, cts,
            cancelOnFail: false, removeFromSource: false, organize: true);

        job.State        = song.State == TrackState.Downloaded ? JobState.Done : JobState.Failed;
        job.DownloadPath = song.DownloadPath;

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
    }


    async Task ProcessSongListJob(SongListQueryJob job, JobContext ctx)
    {
        var config = job.Config;
        var songs = job.Songs;
        var progressReporter = new IntervalProgressReporter(TimeSpan.FromSeconds(30), 5, songs);
        var semaphore = new SemaphoreSlim(config.concurrentProcesses);
        var organizer = new FileManager(job, config);

        var downloadTasks = songs.Select(async (song, _) =>
        {
            using var cts       = new CancellationTokenSource();
            bool wasInitial     = song.State == TrackState.Initial;

            await DownloadSong(song, job, config, organizer, semaphore, cts, cancelOnFail: false,
                removeFromSource: true, organize: true);

            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();

            if (wasInitial)
            {
                progressReporter.MaybeReport(song.State);
                int downloaded = songs.Count(s => s.State == TrackState.Downloaded || s.State == TrackState.AlreadyExists);
                int failed     = songs.Count(s => s.State == TrackState.Failed);
                _progressReporter.ReportOverallProgress(downloaded, failed, songs.Count);
            }
        });

        await Task.WhenAll(downloadTasks);

        int dl = songs.Count(s => s.State == TrackState.Downloaded || s.State == TrackState.AlreadyExists);
        int fl = songs.Count(s => s.State == TrackState.Failed);
        _progressReporter.ReportJobComplete(dl, fl, songs.Count);

        if (config.removeTracksFromSource && songs.All(s => s.State == TrackState.Downloaded || s.State == TrackState.AlreadyExists))
            await extractor!.RemoveTrackFromSource(new SongJob(job.QueryTrack));
    }


    async Task ProcessAggregateJob(AggregateQueryJob job, JobContext ctx)
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


    async Task ProcessAlbumJob(AlbumQueryJob job, JobContext ctx)
    {
        var config = job.Config;
        var organizer = new FileManager(job, config);
        List<AlbumFile>? chosenFiles = null;
        AlbumDownloadJob? downloadJob = null;
        var retrievedFolders = new HashSet<string>();
        bool succeeded = false;
        string? filterStr = null;
        int index = 0;
        int albumTrackCountRetries = config.albumTrackCountMaxRetries;

        async Task RunAlbumDownloads(AlbumFolder folder, SemaphoreSlim semaphore, CancellationTokenSource cts)
        {
            var tasks = folder.Files.Select(async af =>
            {
                if (af.State != TrackState.Initial) return;
                var song = AlbumFileToSongJob(af, job);
                await DownloadAlbumFile(af, song, config, organizer, semaphore, cts, cancelOnFail: true);
            });
            await Task.WhenAll(tasks);
        }

        while (job.FoundFolders.Count > 0 && !config.albumArtOnly)
        {
            bool wasInteractive = false;
            bool retrieveCurrent = true;
            index = 0;

            AlbumFolder chosenFolder;

            if (SelectAlbumVersion != null)
            {
                downloadJob = await SelectAlbumVersion(job);
                if (downloadJob == null) { index = -1; break; }  // skip or quit
                config = job.Config;  // callback may have mutated job.Config (e.g. exit interactive mode)
                chosenFolder = downloadJob.Target;
                retrieveCurrent = true;
                index = job.FoundFolders.Contains(chosenFolder) ? job.FoundFolders.IndexOf(chosenFolder) : 0;
            }
            else if (config.interactiveMode)
            {
                wasInteractive = true;
                var interactive = new InteractiveModeManager(job, Queue!, job.FoundFolders, true,
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
                index = job.FoundFolders.Contains(chosenFolder) ? job.FoundFolders.IndexOf(chosenFolder) : 0;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(filterStr))
                {
                    index = job.FoundFolders.FindIndex(f => f.Files.Any(af => af.Candidate.Filename.ContainsIgnoreCase(filterStr)));
                    if (index == -1) break;
                }
                chosenFolder = job.FoundFolders[index];

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
                        job.FoundFolders.RemoveAt(index);
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
                downloadJob ??= new AlbumDownloadJob(chosenFolder, job.Query);
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

                job.CompletedDownload = downloadJob;
                succeeded    = true;
                chosenFiles  = chosenFolder.Files;
                break;
            }
            catch (OperationCanceledException)
            {
                if (userCancelled)
                {
                    Console.WriteLine();
                    Logger.Info("Download cancelled.");

                    if (chosenFolder.Files.Any(af => af.State == TrackState.Downloaded && !string.IsNullOrEmpty(af.DownloadPath)))
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
                job.FoundFolders.RemoveAt(index);
            }
        }

        if (succeeded && chosenFiles != null)
        {
            job.State = JobState.Done;

            var downloadedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.State == TrackState.Downloaded && !string.IsNullOrEmpty(af.DownloadPath));

            if (downloadedAudio.Any())
            {
                downloadJob!.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(af => af.DownloadPath!));
                ctx.IndexEditor?.NotifyJobDownloadPath(job.Id, downloadJob.DownloadPath);
                if (config.removeTracksFromSource)
                    await extractor!.RemoveTrackFromSource(new SongJob(job.QueryTrack));
            }
        }
        else if (index != -1)
        {
            job.State         = JobState.Failed;
            job.FailureReason = FailureReason.NoSuitableFileFound;
        }

        List<AlbumFile>? additionalImages = null;

        if (config.albumArtOnly || (succeeded && config.albumArtOption != AlbumArtOption.Default))
        {
            Logger.Info("Downloading additional images:");
            additionalImages = await DownloadImages(job, ctx, organizer, downloadJob);

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

        if (chosenFiles != null && downloadJob != null && !string.IsNullOrEmpty(downloadJob.DownloadPath))
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
        if (song.State != TrackState.Initial) return;

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
                    song.State         = TrackState.Failed;
                    song.FailureReason = sdEx.Reason;
                    _progressReporter.ReportTrackStateChanged(song);

                    if (cancelOnFail)
                    {
                        cts.Cancel();
                        throw new OperationCanceledException();
                    }
                }
                else if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    song.State         = TrackState.Failed;
                    song.FailureReason = FailureReason.Other;
                    _progressReporter.ReportTrackStateChanged(song);
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
            song.State         = TrackState.Failed;
            song.FailureReason = FailureReason.Other;
            _progressReporter.ReportTrackStateChanged(song);
            cts.Cancel();
            throw new OperationCanceledException();
        }

        if (savedFilePath.Length > 0)
        {
            song.State        = TrackState.Downloaded;
            song.DownloadPath = savedFilePath;
            _progressReporter.ReportTrackStateChanged(song, song.ChosenCandidate);

            if (removeFromSource && config.removeTracksFromSource)
            {
                try { await extractor!.RemoveTrackFromSource(song); }
                catch (Exception ex) { Logger.Error($"Error removing track from source: {ex.Message}"); }
            }
        }

        if (song.State == TrackState.Downloaded && organize)
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

        _progressReporter.ReportSongSearching(song);

        await searcher!.SearchSong(song, config, responseData, appCts.Token,
            onSearch: () => _progressReporter.ReportSongSearching(song));

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
                await downloader!.DownloadFile(candidate, outputPath, song, config, appCts.Token, cts);
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


    /// <summary>
    /// Downloads a single AlbumFile. Creates a synthetic SongJob for the Downloader.
    /// </summary>
    async Task DownloadAlbumFile(AlbumFile af, SongJob song, Config config,
        FileManager organizer, SemaphoreSlim semaphore, CancellationTokenSource cts, bool cancelOnFail)
    {
        if (af.State != TrackState.Initial) return;

        await semaphore.WaitAsync(cts.Token);

        try
        {
            string outputPath = organizer.GetSavePath(af.Candidate.Filename);

            // ReportDownloadStart + ReportDownloadProgress are called inside DownloadFile.
            // Link both the app-level and the album-level CTS so pressing 'c' cancels in-flight downloads.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, cts.Token);
            await downloader!.DownloadFile(af.Candidate, outputPath, song, config, linkedCts.Token, null);

            af.State        = TrackState.Downloaded;
            af.DownloadPath = outputPath;
            _progressReporter.ReportTrackStateChanged(song, af.Candidate);

            if (af.State == TrackState.Downloaded)
                organizer.OrganizeAlbumFile(af);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            af.State         = TrackState.Failed;
            af.FailureReason = FailureReason.Other;
            Logger.DebugError($"Album file download failed: {ex.Message}");
            _progressReporter.ReportTrackStateChanged(song);

            if (cancelOnFail)
            {
                cts.Cancel();
                throw new OperationCanceledException();
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    // Creates a temporary SongJob to pass to Downloader for album file downloads.
    static SongJob AlbumFileToSongJob(AlbumFile af, Job job)
        => new SongJob(af.Info) { ItemNumber = job.ItemNumber, LineNumber = job.LineNumber };


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
            song.State        = TrackState.AlreadyExists;
            song.DownloadPath = path;
        }

        return path != null;
    }

    bool TrySetJobAlreadyExists(Job job, JobContext ctx)
    {
        if (job is not AlbumQueryJob aj) return false;

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
        if (prev.FailureReason == FailureReason.NoSuitableFileFound || prev.State == TrackState.NotFoundLastTime)
        {
            song.State = TrackState.NotFoundLastTime;
            return true;
        }
        return false;
    }

    bool TrySetNotFoundLastTimeForJob(Job job)
    {
        var jobCtx = Ctx(job);
        if (jobCtx.IndexEditor == null) return false;
        IndexEntry? prev = null;

        if (job is AlbumQueryJob aj)
            prev = jobCtx.IndexEditor.PreviousRunResult(aj);

        if (prev == null) return false;
        if (prev.FailureReason == FailureReason.NoSuitableFileFound || prev.State == TrackState.NotFoundLastTime)
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

    async Task<List<AlbumFile>> DownloadImages(AlbumQueryJob job, JobContext ctx, FileManager fileManager, AlbumDownloadJob? downloadJob)
    {
        var result = new List<AlbumFile>();
        var config = job.Config;
        long mSize = 0;
        int  mCount = 0;
        var option = config.albumArtOption;
        var chosenFolder = downloadJob?.Target;

        if (chosenFolder != null)
        {
            string dir = chosenFolder.FolderPath;
            fileManager.SetDefaultFolderName(Path.GetFileName(Utils.NormalizedPath(dir)));
        }

        if (option == AlbumArtOption.Default) return result;

        int[]? sortedLengths = null;
        if (chosenFolder?.Files.Any(af => !af.IsNotAudio) == true)
            sortedLengths = chosenFolder.Files.Where(af => !af.IsNotAudio)
                .Select(af => af.Info.Length).OrderBy(x => x).ToArray();

        var imageFolders = job.FoundFolders
            .Where(f => chosenFolder == null || Searcher.AlbumsAreSimilar(chosenFolder, f, sortedLengths))
            .Select(f => f.Files.Where(af => Utils.IsImageFile(af.Candidate.Filename)).ToList())
            .Where(ls => ls.Count > 0)
            .ToList();

        if (imageFolders.Count == 0)
        { Logger.Info("No images found"); return result; }

        if (imageFolders.Count == 1 && imageFolders[0].All(af => af.State != TrackState.Initial))
        { Logger.Info("No additional images found"); return result; }

        if (option == AlbumArtOption.Largest)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Max(af => af.Candidate.File.Size) / 1024 / 100)
                .ThenByDescending(ls => ls[0].Candidate.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.Candidate.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mSize = chosenFolder.Files
                    .Where(af => af.State == TrackState.Downloaded && Utils.IsImageFile(af.DownloadPath ?? ""))
                    .Select(af => af.Candidate.File.Size)
                    .DefaultIfEmpty(0).Max();
        }
        else if (option == AlbumArtOption.Most)
        {
            imageFolders = imageFolders
                .OrderByDescending(ls => ls.Count)
                .ThenByDescending(ls => ls[0].Candidate.Response.UploadSpeed / 1024 / 300)
                .ThenByDescending(ls => ls.Sum(af => af.Candidate.File.Size) / 1024 / 100)
                .ToList();

            if (chosenFolder != null)
                mCount = chosenFolder.Files.Count(af => af.State == TrackState.Downloaded && Utils.IsImageFile(af.DownloadPath ?? ""));
        }

        bool needsDownload(List<AlbumFile> ls) => option == AlbumArtOption.Most
            ? mCount < ls.Count
            : option == AlbumArtOption.Largest
                ? mSize < ls.Max(af => af.Candidate.File.Size) - 1024 * 50
                : true;

        while (imageFolders.Count > 0)
        {
            int    imgIdx        = 0;
            bool   wasInteractive = config.interactiveMode;
            List<AlbumFile> imgs;

            if (config.interactiveMode && SelectAlbumVersion != null)
            {
                // Wrap image folders as synthetic AlbumFolders for interactive picker.
                var syntheticFolders = imageFolders.Select((ls, idx) => new AlbumFolder(
                    ls[0].Candidate.Response.Username,
                    Utils.GreatestCommonDirectorySlsk(ls.Select(af => af.Candidate.Filename)),
                    ls)).ToList();

                var syntheticJob = new AlbumQueryJob(job.Query) { FoundFolders = syntheticFolders, Config = config };
                _contexts[syntheticJob.Id] = new JobContext();
                var pickedDownload = await SelectAlbumVersion(syntheticJob);
                if (pickedDownload == null) break;
                imgIdx = syntheticFolders.IndexOf(pickedDownload.Target);
                if (imgIdx == -1) break;
                imgs = imageFolders[imgIdx];
            }
            else
            {
                imgs = imageFolders[0];
            }

            imageFolders.RemoveAt(imgIdx);

            if (imgs.All(af => af.State == TrackState.Downloaded || af.State == TrackState.AlreadyExists)
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
                    imgs[0].Candidate.Response.Username,
                    Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.Candidate.Filename)),
                    imgs);
                Printing.PrintAlbum(syntheticFolder);
            }

            fileManager.downloadingAdditionalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.Candidate.Filename)));

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
                    var song = AlbumFileToSongJob(af, job);
                    await DownloadAlbumFile(af, song, config, fileManager, semaphore, cts, cancelOnFail: false);
                    if (af.State == TrackState.Downloaded)
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
                    if (imgs.Any(af => af.State == TrackState.Downloaded && !string.IsNullOrEmpty(af.DownloadPath)))
                    {
                        Console.Write("Delete files? [Y/n] (default: Yes): ");
                        var res = Console.IsInputRedirected ? "" : (Console.ReadLine() ?? "").Trim().ToLower();
                        if (res == "y" || res == "")
                        {
                            var imgFolder = new AlbumFolder(imgs[0].Candidate.Response.Username,
                                Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.Candidate.Filename)), imgs);
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
                            song.State = TrackState.Failed;
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
