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

    // ── public state (read by Searcher / Downloader) ─────────────────────────

    public ISoulseekClient?  Client              => _clientManager.Client;
    public bool              IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;
    public IProgressReporter ProgressReporter    => _progressReporter;

    // In-flight searches: keyed on the SongJob whose search is active.
    public readonly ConcurrentDictionary<SongJob,  SearchInfo>     searches        = new();
    // In-flight downloads: keyed on the remote filename.
    public readonly ConcurrentDictionary<string,   ActiveDownload> downloads       = new();
    // Prevents re-downloading the same (user, file) pair across multiple SongJobs.
    public readonly ConcurrentDictionary<string,   SongJob>        downloadedFiles = new();
    // Per-user success counters used by ResultSorter for down-ranking.
    public readonly ConcurrentDictionary<string,   int>            userSuccessCounts = new();


    // ── injectable CLI callbacks ──────────────────────────────────────────────

    public Func<AlbumJob, List<AlbumFolder>, Task<AlbumFolder?>>? OnAlbumVersionRequired { get; set; }

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

    public DownloadEngine(Config config, ISoulseekClient? client = null, IProgressReporter? progressReporter = null)
    {
        defaultConfig     = config;
        _clientManager    = new SoulseekClientManager(defaultConfig, client);
        _progressReporter = progressReporter ?? NullProgressReporter.Instance;
    }


    // ── top-level entry point ─────────────────────────────────────────────────

    public async Task RunAsync()
    {
        if (!defaultConfig.RequiresInput)
        {
            PerformNoInputActions(defaultConfig);
            return;
        }

        (defaultConfig.inputType, extractor) = ExtractorRegistry.GetMatchingExtractor(defaultConfig.input, defaultConfig.inputType);
        if (extractor == null)
        {
            Logger.Fatal($"Could not find an extractor for input type {defaultConfig.inputType} and input {defaultConfig.input}");
            return;
        }

        Logger.Info($"Input ({defaultConfig.inputType}): {defaultConfig.input}");

        Queue = await extractor.GetTracks(defaultConfig.input, defaultConfig.maxTracks, defaultConfig.offset, defaultConfig.reverse, defaultConfig);
        if (Queue == null)
        {
            Logger.Fatal($"Extractor failed to get tracks for input {defaultConfig.input}");
            return;
        }

        Logger.Debug("Got tracks");

        // Report the initial track list to the progress reporter.
        var allSongs = Queue.AllSongs().ToList();
        if (allSongs.Count > 0)
            _progressReporter.ReportTrackList(allSongs);

        defaultConfig.PostProcessArgs(Queue);

        Queue.UpgradeToAlbumMode(defaultConfig.album, defaultConfig.aggregate);
        Queue.SetAggregateItemNames();

        JobPreparer.PrepareJobs(Queue, defaultConfig);

        if (defaultConfig.NeedLogin)
        {
            await EnsureClientReadyAsync(defaultConfig);
            searcher   = new Searcher(this, defaultConfig.searchesPerTime, defaultConfig.searchRenewTime);
            downloader = new Downloader(this);

            _ = Task.Run(() => UpdateLoop(appCts.Token), appCts.Token);
            Logger.Debug("Update task started");
        }

        await MainLoop();

        Logger.Debug("Exiting");
        appCts.Cancel();
    }


    public async Task EnsureClientReadyAsync(Config config)
    {
        if (!config.NeedLogin) return;
        if (_clientManager.IsConnectedAndLoggedIn) return;

        try
        {
            await _clientManager.EnsureConnectedAndLoggedInAsync(config, appCts.Token);

            if (searcher == null && _clientManager.Client != null)
            {
                searcher   = new Searcher(this, config.searchesPerTime, config.searchRenewTime);
                downloader = new Downloader(this);
                Logger.Debug("Searcher/Downloader initialized.");
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("Client initialization cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Logger.Fatal($"Failed to initialize Soulseek client: {ex.Message}");
            throw;
        }
    }


    // ── main dispatch loop ────────────────────────────────────────────────────

    async Task MainLoop()
    {
        if (Queue == null || Queue.Count == 0) return;

        var cfg0 = Queue[0].Config;
        bool enableParallelSearch = cfg0.parallelAlbumSearch
            && !cfg0.PrintResults && !cfg0.PrintTracks
            && Queue.Jobs.Any(j => j is AlbumJob or AggregateAlbumJob);

        var parallelSearches  = new List<(DownloadJob job, Task<(bool, ResponseData)> task)>();
        var parallelSemaphore = new SemaphoreSlim(cfg0.parallelAlbumSearchProcesses);

        Queue[0].PrintLines();

        for (int i = 0; i < Queue.Count; i++)
        {
            if (!enableParallelSearch && i > 0) Console.WriteLine();

            var job    = Queue[i];
            var config = job.Config;

            Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());

            if (job.PreprocessTracks)
                Preprocessor.PreprocessJob(job);

            if (!enableParallelSearch)
                job.PrintLines();

            var existing = new List<SongJob>();
            var notFound = new List<SongJob>();

            // ── skip checks ──────────────────────────────────────────────────

            if (config.skipNotFound && !config.PrintResults && job is SongListJob sljSkipNF)
            {
                foreach (var song in sljSkipNF.Songs)
                    if (TrySetNotFoundLastTime(song, job.IndexEditor))
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
                if (job is SongListJob sljSkipEx)
                {
                    foreach (var song in sljSkipEx.Songs.Where(s => s.State == TrackState.Initial))
                        if (TrySetAlreadyExists(job, song, TrackSkipperContext.FromJob(job)))
                            existing.Add(song);
                }
                else if (job.CanBeSkipped && TrySetJobAlreadyExists(job))
                {
                    Logger.Info($"Download '{job.ToString(true)}' already exists at {job.DownloadPath}, skipping");
                    job.IndexEditor?.Update();
                    job.PlaylistEditor?.Update();
                    continue;
                }
            }

            if (config.PrintTracks)
            {
                if (job is SongListJob sljPt)
                    Printing.PrintTracksTbd(sljPt.Songs.Where(s => s.State == TrackState.Initial).ToList(), existing, notFound, isNormal: true, config);
                job.PrintLines();
                continue;
            }

            // ── jobs that need source search first ───────────────────────────

            bool needSourceSearch = job is AlbumJob or AggregateJob or AggregateAlbumJob;

            if (needSourceSearch)
            {
                await EnsureClientReadyAsync(config);

                async Task<(bool, ResponseData)> sourceSearch()
                {
                    await parallelSemaphore.WaitAsync();

                    _progressReporter.ReportJobSearching(job, enableParallelSearch);

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
                        foundSomething = albumJob.FoundFolders.Count > 0;
                    }
                    else if (job is AggregateJob aggJob)
                    {
                        await searcher!.SearchAggregate(aggJob, config, responseData, appCts.Token);
                        foundSomething = aggJob.Songs.Count > 0;
                    }
                    else if (job is AggregateAlbumJob aabJob)
                    {
                        var newAlbumJobs = await searcher!.SearchAggregateAlbum(aabJob, config, responseData, appCts.Token);
                        foreach (var aj in newAlbumJobs)
                        {
                            aj.PreprocessTracks = false;
                            aj.IndexEditor      = job.IndexEditor;
                            aj.PlaylistEditor   = job.PlaylistEditor;
                            aj.ItemName         = job.ItemName;
                            Queue.Enqueue(aj);
                        }
                        // Mark this job done; it only spawns AlbumJobs.
                        job.State = JobState.Done;
                        foundSomething = newAlbumJobs.Count > 0;
                    }

                    _progressReporter.ReportJobSearchResult(job, foundSomething, responseData.lockedFilesCount);

                    if (!foundSomething)
                    {
                        job.State         = JobState.Failed;
                        job.FailureReason = FailureReason.NoSuitableFileFound;

                        if (job is AlbumJob aj)
                            await OnCompleteExecutor.ExecuteAsync(aj, null, aj.IndexEditor, aj.PlaylistEditor);
                    }

                    parallelSemaphore.Release();
                    return (foundSomething, responseData);
                }

                if (!enableParallelSearch || job is not (AlbumJob or AggregateAlbumJob))
                {
                    (bool found, ResponseData _) = await sourceSearch();

                    if (!found)
                    {
                        if (!config.PrintResults)
                            job.IndexEditor?.Update();

                        // AggregateAlbumJob marks itself Done after spawning children; no further action needed.
                        if (job.State == JobState.Done) continue;
                        continue;
                    }

                    if (job.State == JobState.Done) continue; // AggregateAlbumJob converted to children

                    if (config.skipExisting && job is AggregateJob foundAggJob)
                    {
                        var ctx = TrackSkipperContext.FromJob(foundAggJob);
                        foreach (var song in foundAggJob.Songs)
                        {
                            TrySetAlreadyExists(foundAggJob, song, ctx);
                        }
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
                await EnsureClientReadyAsync(config);
                await Printing.PrintResults(job, existing, notFound, config, searcher!);
                continue;
            }

            if (parallelSearches.Count > 0 && !(job is AlbumJob or AggregateAlbumJob))
                await FlushParallelSearches(parallelSearches);

            if (!enableParallelSearch || !(job is AlbumJob or AggregateAlbumJob))
                await DispatchDownload(job, config, notFound, existing);
        }

        if (parallelSearches.Count > 0)
            await FlushParallelSearches(parallelSearches);

        if (Queue.Count > 0 && !Queue[^1].Config.DoNotDownload)
            Printing.PrintComplete(Queue);
    }


    async Task FlushParallelSearches(List<(DownloadJob job, Task<(bool, ResponseData)> task)> parallelSearches)
    {
        await Task.WhenAll(parallelSearches.Select(x => x.task));

        foreach (var (job, task) in parallelSearches)
        {
            (bool found, _) = task.Result;
            if (found)
            {
                Logger.Info($"Downloading: {job}");
                await DispatchDownload(job, job.Config, null, null);
            }
            else if (!job.Config.PrintResults)
            {
                job.State         = JobState.Failed;
                job.FailureReason = FailureReason.NoSuitableFileFound;
                job.IndexEditor?.Update();
            }
        }

        parallelSearches.Clear();
    }


    async Task DispatchDownload(DownloadJob job, Config config, List<SongJob>? notFound, List<SongJob>? existing)
    {
        job.IndexEditor?.Update();
        job.PlaylistEditor?.Update();

        switch (job)
        {
            case SongListJob slj:
                Printing.PrintTracksTbd(slj.Songs.Where(s => s.State == TrackState.Initial).ToList(),
                    existing ?? new(), notFound ?? new(), isNormal: true, config);

                int initialCount = slj.Songs.Count;
                int skipCount    = (notFound?.Count ?? 0) + (existing?.Count ?? 0);
                if (skipCount >= initialCount) return;

                await EnsureClientReadyAsync(config);
                await ProcessSongListJob(slj, config);
                break;

            case AlbumJob aj:
                await EnsureClientReadyAsync(config);
                await ProcessAlbumJob(aj, config);
                break;

            case AggregateJob ag:
                Printing.PrintTracksTbd(ag.Songs.Where(s => s.State == TrackState.Initial).ToList(),
                    new(), new(), isNormal: false, config);
                await EnsureClientReadyAsync(config);
                await ProcessAggregateJob(ag, config);
                break;
        }
    }


    // ── per-job-type handlers ─────────────────────────────────────────────────

    async Task ProcessSongListJob(SongListJob job, Config config)
    {
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

            job.IndexEditor?.Update();
            job.PlaylistEditor?.Update();

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


    async Task ProcessAggregateJob(AggregateJob job, Config config)
    {
        var songs     = job.Songs;
        var semaphore = new SemaphoreSlim(config.concurrentProcesses);
        var organizer = new FileManager(job, config);

        var downloadTasks = songs.Select(async song =>
        {
            using var cts = new CancellationTokenSource();
            await DownloadSong(song, job, config, organizer, semaphore, cts, cancelOnFail: false,
                removeFromSource: false, organize: true);
            job.IndexEditor?.Update();
            job.PlaylistEditor?.Update();
        });

        await Task.WhenAll(downloadTasks);
    }


    async Task ProcessAlbumJob(AlbumJob job, Config config)
    {
        var organizer = new FileManager(job, config);
        List<AlbumFile>? chosenFiles = null;
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
                await DownloadAlbumFile(af, song, job, config, organizer, semaphore, cts, cancelOnFail: true);
            });
            await Task.WhenAll(tasks);
        }

        while (job.FoundFolders.Count > 0 && !config.albumArtOnly)
        {
            bool wasInteractive = config.interactiveMode;
            bool retrieveCurrent = true;
            index = 0;

            AlbumFolder chosenFolder;

            if (config.interactiveMode)
            {
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

            if (!config.interactiveMode && !wasInteractive)
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
                job.ChosenFolder = chosenFolder;
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

                    if (!config.interactiveMode)
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
                job.ChosenFolder = null;
            }
        }

        if (succeeded && chosenFiles != null)
        {
            job.State = JobState.Done;

            var downloadedAudio = chosenFiles
                .Where(af => !af.IsNotAudio && af.State == TrackState.Downloaded && !string.IsNullOrEmpty(af.DownloadPath));

            if (downloadedAudio.Any())
            {
                job.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(af => af.DownloadPath!));
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
            additionalImages = await DownloadImages(job, config, organizer);

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

        job.IndexEditor?.Update();
        job.PlaylistEditor?.Update();

        await OnCompleteExecutor.ExecuteAsync(job, null, job.IndexEditor, job.PlaylistEditor);
    }


    // ── single-song download ──────────────────────────────────────────────────

    async Task DownloadSong(SongJob song, DownloadJob job, Config config, FileManager organizer,
        SemaphoreSlim semaphore, CancellationTokenSource cts, bool cancelOnFail,
        bool removeFromSource, bool organize)
    {
        if (song.State != TrackState.Initial) return;

        await semaphore.WaitAsync(cts.Token);

        int    tries         = config.unknownErrorRetries;
        string savedFilePath = "";

        while (tries > 0)
        {
            await EnsureClientReadyAsync(config);
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

        if (job.Config.HasOnComplete)
        {
            _progressReporter.ReportOnCompleteStart(song);
            await OnCompleteExecutor.ExecuteAsync(job, song, job.IndexEditor, job.PlaylistEditor);
            _progressReporter.ReportOnCompleteEnd(song);
        }

        semaphore.Release();
    }


    /// <summary>
    /// Searches for candidates for <paramref name="song"/> then downloads the best one.
    /// Returns (savedFilePath, chosenFile).
    /// Throws <see cref="SearchAndDownloadException"/> on unrecoverable search/download failures.
    /// </summary>
    async Task<(string, SlFile?)> SearchAndDownloadSong(SongJob song, DownloadJob job, Config config,
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
                userSuccessCounts.AddOrUpdate(candidate.Username, 1, (_, c) => c + 1);
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
    async Task DownloadAlbumFile(AlbumFile af, SongJob song, AlbumJob job, Config config,
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
    static SongJob AlbumFileToSongJob(AlbumFile af, AlbumJob job)
        => new SongJob(af.Info) { ItemNumber = job.ItemNumber, LineNumber = job.LineNumber };


    // ── skip helpers ──────────────────────────────────────────────────────────

    bool TrySetAlreadyExists(DownloadJob job, SongJob song, TrackSkipperContext ctx)
    {
        string? path = null;

        if (job.OutputDirSkipper != null)
        {
            if (!job.OutputDirSkipper.IndexIsBuilt) job.OutputDirSkipper.BuildIndex();
            job.OutputDirSkipper.SongExists(song, ctx, out path);
        }

        if (path == null && job.MusicDirSkipper != null)
        {
            if (!job.MusicDirSkipper.IndexIsBuilt)
            {
                Logger.Info("Building music directory index..");
                job.MusicDirSkipper.BuildIndex();
            }
            job.MusicDirSkipper.SongExists(song, ctx, out path);
        }

        if (path != null)
        {
            song.State        = TrackState.AlreadyExists;
            song.DownloadPath = path;
        }

        return path != null;
    }

    bool TrySetJobAlreadyExists(DownloadJob job)
    {
        if (job is not AlbumJob aj) return false;

        var ctx = TrackSkipperContext.FromJob(job);
        string? path = null;

        if (job.OutputDirSkipper != null)
        {
            if (!job.OutputDirSkipper.IndexIsBuilt) job.OutputDirSkipper.BuildIndex();
            job.OutputDirSkipper.AlbumExists(aj, ctx, out path);
        }

        if (path == null && job.MusicDirSkipper != null)
        {
            if (!job.MusicDirSkipper.IndexIsBuilt)
            {
                Logger.Info("Building music directory index..");
                job.MusicDirSkipper.BuildIndex();
            }
            job.MusicDirSkipper.AlbumExists(aj, ctx, out path);
        }

        if (path != null)
        {
            job.State        = JobState.Skipped;
            job.DownloadPath = path;
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

    bool TrySetNotFoundLastTimeForJob(DownloadJob job)
    {
        if (job.IndexEditor == null) return false;
        IndexEntry? prev = null;

        if (job is AlbumJob aj)
            prev = job.IndexEditor.PreviousRunResult(aj);

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

    async Task<List<AlbumFile>> DownloadImages(AlbumJob job, Config config, FileManager fileManager)
    {
        var result = new List<AlbumFile>();
        long mSize = 0;
        int  mCount = 0;
        var option = config.albumArtOption;

        if (job.ChosenFolder != null)
        {
            string dir = job.ChosenFolder.FolderPath;
            fileManager.SetDefaultFolderName(Path.GetFileName(Utils.NormalizedPath(dir)));
        }

        if (option == AlbumArtOption.Default) return result;

        int[]? sortedLengths = null;
        if (job.ChosenFolder?.Files.Any(af => !af.IsNotAudio) == true)
            sortedLengths = job.ChosenFolder.Files.Where(af => !af.IsNotAudio)
                .Select(af => af.Info.Length).OrderBy(x => x).ToArray();

        var imageFolders = job.FoundFolders
            .Where(f => job.ChosenFolder == null || Searcher.AlbumsAreSimilar(job.ChosenFolder, f, sortedLengths))
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

            if (job.ChosenFolder != null)
                mSize = job.ChosenFolder.Files
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

            if (job.ChosenFolder != null)
                mCount = job.ChosenFolder.Files.Count(af => af.State == TrackState.Downloaded && Utils.IsImageFile(af.DownloadPath ?? ""));
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

            if (config.interactiveMode && OnAlbumVersionRequired != null)
            {
                // Wrap image folders as synthetic AlbumFolders for interactive picker.
                var syntheticFolders = imageFolders.Select((ls, idx) => new AlbumFolder(
                    ls[0].Candidate.Response.Username,
                    Utils.GreatestCommonDirectorySlsk(ls.Select(af => af.Candidate.Filename)),
                    ls)).ToList();

                var syntheticJob = new AlbumJob(job.Query) { FoundFolders = syntheticFolders, Config = config };
                var picked = await OnAlbumVersionRequired(syntheticJob, syntheticFolders);
                if (picked == null) break;
                imgIdx = syntheticFolders.IndexOf(picked);
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
                    await DownloadAlbumFile(af, song, job, config, fileManager, semaphore, cts, cancelOnFail: false);
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
                    // Prune completed searches.
                    foreach (var (key, _) in searches)
                        searches.TryRemove(key, out _);

                    // Check for stale downloads.
                    foreach (var (key, ad) in downloads)
                    {
                        if (ad == null) { downloads.TryRemove(key, out _); continue; }

                        var song = ad.Song;
                        int maxStale = ad.Song.FileSize > 0 ? defaultConfig.maxStaleTime : int.MaxValue;
                        if (song.LastActivityTime.HasValue &&
                            (DateTime.Now - song.LastActivityTime.Value).TotalMilliseconds > maxStale)
                        {
                            Logger.Debug($"Cancelling stale download: {song}");
                            song.State = TrackState.Failed;
                            try { ad.Cts.Cancel(); } catch { }
                            downloads.TryRemove(key, out _);
                        }
                    }
                }
                else
                {
                    if (Client != null
                        && !Client.State.HasFlag(SoulseekClientStates.LoggedIn)
                        && !Client.State.HasFlag(SoulseekClientStates.LoggingIn)
                        && !Client.State.HasFlag(SoulseekClientStates.Connecting))
                    {
                        Logger.Warn("Disconnected, logging in");
                        try
                        {
                            await _clientManager.EnsureConnectedAndLoggedInAsync(defaultConfig, cancellationToken);
                            Logger.Info(_clientManager.IsConnectedAndLoggedIn ? "Reconnected successfully." : "Reconnect attempt did not succeed immediately.");
                        }
                        catch (OperationCanceledException) { Logger.Info("Reconnect cancelled."); break; }
                        catch (Exception ex)
                        {
                            string banMsg = defaultConfig.useRandomLogin ? "" : " (possibly a 30-minute ban)";
                            Logger.Warn($"Reconnect failed: {ex.Message}{banMsg}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"{ex.Message}");
            }

            await Task.Delay(updateInterval);
        }
    }


    // ── print-index (no network required) ────────────────────────────────────

    void PerformNoInputActions(Config config)
    {
        if (config.printOption.HasFlag(PrintOption.Index))
        {
            if (string.IsNullOrEmpty(config.indexFilePath))
            { Logger.Fatal("Error: No index file path provided"); return; }

            var indexFilePath = Utils.GetFullPath(Utils.ExpandVariables(config.indexFilePath));
            if (!File.Exists(indexFilePath))
            { Logger.Fatal($"Error: Index file {indexFilePath} does not exist"); return; }

            var index = new M3uEditor(indexFilePath, new JobQueue(), M3uOption.Index, true);
            var data  = index.GetPreviousRunData().AsEnumerable();

            if (config.printOption.HasFlag(PrintOption.IndexFailed))
                data = data.Where(e => e.State == TrackState.Failed);

            JsonPrinter.PrintIndexJson(data);
        }
    }
}
