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

    private Searcher?    searcher     = null;
    private Downloader?  downloader   = null;

    private readonly Config              defaultConfig;
    private readonly SoulseekClientManager _clientManager;
    private readonly IProgressReporter   _progressReporter;

    public JobList Queue { get; } = new();

    private readonly ConcurrentDictionary<Guid, JobContext> _contexts = new();

    private JobContext Ctx(Job job) => _contexts[job.Id];

    // ── public state (read by Searcher / Downloader) ─────────────────────────

    public ISoulseekClient?  Client              => _clientManager.Client;
    public bool              IsConnectedAndLoggedIn => _clientManager.IsConnectedAndLoggedIn;
    public IProgressReporter ProgressReporter    => _progressReporter;

    // Session state (Decoupled)
    private readonly SessionRegistry _registry = new();


    // ── injectable CLI callbacks ──────────────────────────────────────────────

    public Func<AlbumJob, Task<AlbumFolder?>>? SelectAlbumVersion { get; set; }

    // ── concurrency semaphores ────────────────────────────────────────────────

    // Limits simultaneous extractor runs to avoid API rate limits.
    // Search concurrency is handled inside Searcher (concurrencySemaphore).
    private readonly SemaphoreSlim _extractorSemaphore;

    // ── cancellation ─────────────────────────────────────────────────────────

    private readonly CancellationTokenSource appCts = new();
    public void Cancel() => appCts.Cancel();

    // TODO: per-job cancellation via job.Cancel() — expose Cts on Job, let the CLI wire 'c' to
    // a numbered list of running jobs so the user can pick which one to cancel. The engine should
    // not listen for console keys. See PLAN.md §Cancellation.


    // ── construction ─────────────────────────────────────────────────────────

    public DownloadEngine(Config config, SoulseekClientManager clientManager, IProgressReporter? progressReporter = null)
    {
        defaultConfig       = config;
        _clientManager      = clientManager;
        _progressReporter   = progressReporter ?? NullProgressReporter.Instance;
        _extractorSemaphore = new SemaphoreSlim(config.concurrentExtractors);
    }


    // ── top-level entry point ─────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        defaultConfig.PostProcessArgs(Queue);

        // Wrap the CLI input in an ExtractJob and add it to the persistent queue.
        var rootExtractJob = new ExtractJob(defaultConfig.input, defaultConfig.inputType);
        Queue.Jobs.Add(rootExtractJob);

        foreach (var (id, ctx) in JobPreparer.PrepareJobs(Queue, defaultConfig))
            _contexts[id] = ctx;

        if (defaultConfig.NeedLogin)
        {
            await _clientManager.EnsureConnectedAndLoggedInAsync(defaultConfig, ct);
            await _clientManager.WaitUntilReadyAsync(ct);
            searcher   = new Searcher(Client!, _registry, _registry, _progressReporter, defaultConfig.searchesPerTime, defaultConfig.searchRenewTime, defaultConfig.concurrentSearches);
            downloader = new Downloader(Client!, _clientManager, _registry, _progressReporter);

            _ = Task.Run(() => UpdateLoop(appCts.Token), appCts.Token);
            Logger.Debug("Update task started");
        }

        await ProcessJob(rootExtractJob);

        if (Queue.Jobs.Count > 0 && !Queue.Jobs[^1].Config.DoNotDownload)
            Printing.PrintComplete(Queue);

        Logger.Debug("Exiting");
        appCts.Cancel();
    }


    // ── recursive job processor ───────────────────────────────────────────────

    async Task ProcessJob(Job job, IExtractor? extractor = null, CancellationToken parentToken = default)
    {
        // Create a per-job CTS linked to both the engine-wide appCts and the parent job's token
        // (if any). Cancelling this job propagates to all descendants; cancelling the parent
        // propagates here automatically. ExtractJob passes parentToken (not its own token) when
        // recursing into its Result so that the Result is a sibling, not a child, in the hierarchy.
        job.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, parentToken);

        // ── ExtractJob: run extractor, set Result, recurse ───────────────────
        if (job is ExtractJob ej)
        {
            var (inputType, ex) = ExtractorRegistry.GetMatchingExtractor(ej.Input, ej.InputType ?? InputType.None);
            ej.InputType = inputType;

            Logger.Info($"Input ({inputType}): {ej.Input}");
            ej.State = JobState.Extracting;
            _progressReporter.ReportExtractionStarted(ej);

            Job extracted;
            await _extractorSemaphore.WaitAsync(ej.Cts!.Token);
            try
            {
                extracted = await ex.GetTracks(ej.Input, defaultConfig.maxTracks, defaultConfig.offset, defaultConfig.reverse, ej.Config);
            }
            catch (Exception e)
            {
                Logger.Fatal($"Extractor failed: {e.Message}");
                ej.State = JobState.Failed;
                return;
            }
            finally
            {
                _extractorSemaphore.Release();
            }

            ej.Result = extracted;
            ej.State  = JobState.Done;
            _progressReporter.ReportExtractionCompleted(ej, extracted);

            Logger.Debug("Got tracks");

            // Post-extraction transforms — album/aggregate upgrades and name assignment
            // apply to the direct children of the extracted JobList (if it is one).
            if (extracted is JobList extractedList)
            {
                extractedList.UpgradeToAlbumMode(ej.Config.album, ej.Config.aggregate);
                extractedList.SetAggregateItemNames();
            }
            else
            {
                // Bare job (e.g. AlbumJob from StringExtractor) — wrap in a temporary list
                // just to run UpgradeToAlbumMode, then unwrap.
                var wrapper = new JobList();
                wrapper.Jobs.Add(extracted);
                wrapper.UpgradeToAlbumMode(ej.Config.album, ej.Config.aggregate);
                if (wrapper.Jobs.Count == 1 && !ReferenceEquals(wrapper.Jobs[0], extracted))
                {
                    ej.Result = wrapper.Jobs[0];
                    extracted = ej.Result;
                }
            }

            // Propagate provenance from ExtractJob to the extracted result.
            extracted.LineNumber = ej.LineNumber;
            extracted.ItemNumber = ej.ItemNumber;
            // For a single-song JobList, also stamp the inner song (used by RemoveTrackFromSource).
            if (extracted is JobList ejl && ejl.Jobs.Count == 1 && ejl.Jobs[0] is SongJob innerSong)
            {
                innerSong.LineNumber = ej.LineNumber;
                innerSong.ItemNumber = ej.ItemNumber;
            }

            // Report the initial track list.
            var allSongs = (extracted is JobList jlr ? jlr.AllSongs() : extracted is SongJob sjs ? new[] { sjs }.AsEnumerable() : Enumerable.Empty<SongJob>()).ToList();
            if (allSongs.Count > 0)
                _progressReporter.ReportTrackList(allSongs);

            // Prepare contexts for the extracted subtree, inheriting from the ExtractJob's context.
            var newContexts = JobPreparer.PrepareSubtree(extracted, ej.Config);
            foreach (var (id, ctx) in newContexts)
                _contexts[id] = ctx;

            // Pass parentToken (not ej.Cts.Token): the Result is a sibling of the ExtractJob in
            // the CTS hierarchy. Cancelling the ExtractJob after extraction completes has no effect
            // on the already-running Result; the Result can be cancelled independently.
            await ProcessJob(extracted, ex, parentToken);
            return;
        }

        // ── JobList: list-level setup, fan-out, list-level cleanup ──────────
        if (job is JobList jl)
        {
            var ctx    = _contexts.TryGetValue(jl.Id, out var c) ? c : null;
            var config = jl.Config ?? defaultConfig;

            Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());

            if (ctx?.PreprocessTracks == true)
                Preprocessor.PreprocessJob(jl, config);

            jl.PrintLines();

            // ── skip checks for direct SongJob children ──────────────────────
            var directSongs = jl.Jobs.OfType<SongJob>().ToList();
            var existing    = new List<SongJob>();
            var notFound    = new List<SongJob>();

            if (directSongs.Count > 0 && !config.PrintResults)
            {
                if (ctx != null && config.skipNotFound)
                    foreach (var song in directSongs)
                        if (TrySetNotFoundLastTime(song, ctx.IndexEditor))
                            notFound.Add(song);

                if (ctx != null && config.skipExisting)
                    foreach (var song in directSongs.Where(s => s.State == JobState.Pending))
                        if (TrySetAlreadyExists(jl, song, TrackSkipperContext.From(ctx, config)))
                            existing.Add(song);

                Printing.PrintTracksTbd(directSongs.Where(s => s.State == JobState.Pending).ToList(),
                    existing, notFound, isNormal: true, config);
            }

            if (config.PrintTracks)
            {
                jl.PrintLines();
                return;
            }

            if (config.PrintResults && directSongs.Count > 0)
            {
                await _clientManager.WaitUntilReadyAsync(jl.Cts!.Token);
                await Printing.PrintResults(jl, existing, notFound, config, searcher!);
                return;
            }

            ctx?.IndexEditor?.Update();
            ctx?.PlaylistEditor?.Update();

            // ── fan-out ───────────────────────────────────────────────────────
            if (directSongs.Count > 0)
            {
                var intervalReporter = new IntervalProgressReporter(TimeSpan.FromSeconds(30), 5, directSongs);

                await Task.WhenAll(jl.Jobs.ToList().Select(async child =>
                {
                    bool wasInitial = child is SongJob s && s.State == JobState.Pending;
                    await ProcessJob(child, extractor, jl.Cts!.Token);

                    if (wasInitial && child is SongJob song)
                    {
                        ctx?.IndexEditor?.Update();
                        ctx?.PlaylistEditor?.Update();
                        intervalReporter.MaybeReport(song.State);
                        int dl = directSongs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
                        int fl = directSongs.Count(s => s.State == JobState.Failed);
                        _progressReporter.ReportOverallProgress(dl, fl, directSongs.Count);

                        if (config.removeTracksFromSource && extractor != null && song.State == JobState.Done)
                        {
                            try { await extractor.RemoveTrackFromSource(song); }
                            catch (Exception ex) { Logger.Error($"Error removing track from source: {ex.Message}"); }
                        }
                    }
                }));

                int dlFinal = directSongs.Count(s => s.State == JobState.Done || s.State == JobState.AlreadyExists);
                int flFinal = directSongs.Count(s => s.State == JobState.Failed);
                _progressReporter.ReportListProgress(jl, dlFinal, flFinal, directSongs.Count);

                // If all succeeded, also call whole-list removal (e.g. list.txt removes the source file).
                if (config.removeTracksFromSource && extractor != null
                    && directSongs.All(s => s.State == JobState.Done || s.State == JobState.AlreadyExists))
                {
                    try { await extractor.RemoveTrackFromSource(new SongJob(new SongQuery { Title = jl.ItemName ?? "" })); }
                    catch { /* list-level removal is best-effort */ }
                }
            }
            else
            {
                await Task.WhenAll(jl.Jobs.ToList().Select(child => ProcessJob(child, extractor, jl.Cts!.Token)));
            }

            return;
        }

        // ── Leaf jobs: skip checks, search, download ─────────────────────────
        await ProcessLeafJob(job);
    }

    async Task ProcessLeafJob(Job job)
    {
        var ctx    = Ctx(job);
        var config = job.Config;

        Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());

        if (ctx.PreprocessTracks)
            Preprocessor.PreprocessJob(job, config);

        job.PrintLines();

        // ── skip checks ──────────────────────────────────────────────────────

        if (config.skipNotFound && !config.PrintResults && job.CanBeSkipped)
        {
            if (TrySetNotFoundLastTimeForJob(job))
            {
                Logger.Info($"Download '{job.ToString(true)}' was not found during a prior run, skipping");
                return;
            }
        }

        if (config.skipExisting && !config.PrintResults && job.CanBeSkipped && TrySetJobAlreadyExists(job, ctx))
        {
            Logger.Info($"Download '{job.ToString(true)}' already exists at {(job as AlbumJob)?.DownloadPath}, skipping");
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            return;
        }

        if (config.PrintTracks)
        {
            job.PrintLines();
            return;
        }

        // ── source search ─────────────────────────────────────────────────────

        if (job is AlbumJob or AggregateJob or AlbumAggregateJob)
        {
            await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

            _progressReporter.ReportJobStarted(job);

            bool         foundSomething = false;
            ResponseData responseData   = new ResponseData();

            if (job is AlbumJob albumJob)
            {
                if (!albumJob.Query.IsDirectLink)
                    await searcher!.SearchAlbum(albumJob, config, responseData, job.Cts!.Token);
                else
                {
                    try
                    {
                        _progressReporter.ReportJobFolderRetrieving(job);
                        await searcher!.SearchDirectLinkAlbum(albumJob, job.Cts!.Token);
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
                await searcher!.SearchAggregate(aggJob, config, responseData, job.Cts!.Token);
                foundSomething = aggJob.Songs.Count > 0;
            }
            else if (job is AlbumAggregateJob aabJob)
            {
                var newAlbumJobs = await searcher!.SearchAggregateAlbum(aabJob, config, responseData, job.Cts!.Token);

                job.State = JobState.Done;
                foundSomething = newAlbumJobs.Count > 0;
                _progressReporter.ReportJobCompleted(job, foundSomething, responseData.lockedFilesCount);

                if (foundSomething)
                {
                    var albumList = new JobList(job.ItemName, newAlbumJobs);
                    foreach (var aj in newAlbumJobs)
                    {
                        aj.ItemName = job.ItemName;
                        aj.Config   = job.Config;
                        _contexts[aj.Id] = new JobContext
                        {
                            IndexEditor      = ctx.IndexEditor,
                            PlaylistEditor   = ctx.PlaylistEditor,
                            PreprocessTracks = false,
                        };
                    }
                    _contexts[albumList.Id] = new JobContext
                    {
                        IndexEditor      = ctx.IndexEditor,
                        PlaylistEditor   = ctx.PlaylistEditor,
                        PreprocessTracks = false,
                    };
                    await ProcessJob(albumList, null, job.Cts!.Token);
                }
                return;
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

                return;
            }

            if (config.skipExisting && job is AggregateJob foundAggJob)
            {
                var skipCtx = TrackSkipperContext.From(ctx, job.Config);
                foreach (var song in foundAggJob.Songs)
                    TrySetAlreadyExists(foundAggJob, song, skipCtx);
            }
        }

        if (config.PrintResults)
        {
            await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);
            await Printing.PrintResults(job, new(), new(), config, searcher!);
            return;
        }

        // ── download ─────────────────────────────────────────────────────────

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await _clientManager.WaitUntilReadyAsync(job.Cts!.Token);

        switch (job)
        {
            case SongJob sj:
                await ProcessSongJob(sj, ctx);
                break;

            case AlbumJob aj:
                await ProcessAlbumJob(aj, ctx);
                break;

            case AggregateJob ag:
                Printing.PrintTracksTbd(ag.Songs.Where(s => s.State == JobState.Pending).ToList(),
                    new(), new(), isNormal: false, config);
                await ProcessAggregateJob(ag, ctx);
                break;
        }
    }


    // ── per-job-type handlers ─────────────────────────────────────────────────

    async Task ProcessSongJob(SongJob job, JobContext ctx)
    {
        var config    = job.Config;
        var organizer = new FileManager(job, config);

        // If ResolvedTarget is set, pre-populate Candidates so search is skipped.
        if (job.ResolvedTarget != null && job.Candidates == null)
            job.Candidates = new List<FileCandidate> { job.ResolvedTarget };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
        await DownloadSong(job, job, config, organizer, cts,
            cancelOnFail: false, organize: true);

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();
    }


    async Task ProcessAggregateJob(AggregateJob job, JobContext ctx)
    {
        var config    = job.Config;
        var songs     = job.Songs;
        var organizer = new FileManager(job, config);

        var downloadTasks = songs.Select(async song =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);
            await DownloadSong(song, job, config, organizer, cts, cancelOnFail: false, organize: true);
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

        async Task RunAlbumDownloads(AlbumFolder folder, CancellationTokenSource cts)
        {
            var tasks = folder.Files.Select(async af =>
            {
                if (af.State != JobState.Pending) return;
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await DownloadSong(af, job, config, organizer, cts, cancelOnFail: true, organize: true);
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
                wasInteractive = true;
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
                        await RetrieveFullFolderAsync(chosenFolder, config,
                            "Verifying album track count.\n    Retrieving full folder contents...", job.Cts!.Token);
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

            if (!wasInteractive)
                _progressReporter.ReportAlbumDownloadStarted(job, chosenFolder);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            try
            {
                await RunAlbumDownloads(chosenFolder, cts);

                if (!config.noBrowseFolder && retrieveCurrent && !retrievedFolders.Contains(chosenFolder.FolderPath))
                {
                    var newFilesFound = await RetrieveFullFolderAsync(chosenFolder, config, ct: job.Cts!.Token);
                    retrievedFolders.Add(chosenFolder.FolderPath);
                    if (newFilesFound > 0)
                    {
                        Logger.Info($"Found {newFilesFound} more files, downloading:");
                        await RunAlbumDownloads(chosenFolder, cts);
                    }
                    else
                    {
                        Logger.Info("No more files found.");
                    }
                }

                job.ResolvedTarget = chosenFolder;
                succeeded          = true;
                chosenFiles        = chosenFolder.Files;
                break;
            }
            catch (OperationCanceledException)
            {
                if (!config.IgnoreAlbumFail)
                    HandleAlbumFail(chosenFolder, config.DeleteAlbumOnFail, config);
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
                // Note: album jobs have no parent extractor reference here; RemoveTrackFromSource
                // for albums is handled at the JobList fan-out level if needed.
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

        _progressReporter.ReportAlbumDownloadCompleted(job);

        ctx.IndexEditor?.Update();
        ctx.PlaylistEditor?.Update();

        await OnCompleteExecutor.ExecuteAsync(job, null, ctx);
    }


    // ── single-song download ──────────────────────────────────────────────────

    async Task DownloadSong(SongJob song, Job job, Config config, FileManager organizer,
        CancellationTokenSource cts, bool cancelOnFail, bool organize)
    {
        if (song.State != JobState.Pending) return;

        int    tries         = config.unknownErrorRetries;
        string savedFilePath = "";

        while (tries > 0)
        {
            await _clientManager.WaitUntilReadyAsync(cts.Token);
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
            song.State = JobState.Searching;
            _progressReporter.ReportSongSearching(song);

            if (!config.fastSearch)
            {
                await searcher!.SearchSong(song, config, responseData, cts.Token,
                    onSearch: () => _progressReporter.ReportSongSearching(song));
            }
            else
            {
                // Fast-search: start the search as a background task and race it against a
                // provisional download of the first qualifying candidate.
                // The search concurrency slot is held by SearchSong internally; cancelling
                // searchCts causes SearchSong to return and release it naturally.
                using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                Task<(string path, FileCandidate? candidate)>? fastDownloadTask = null;

                var searchTask = searcher!.SearchSong(song, config, responseData, searchCts.Token,
                    onSearch: () => _progressReporter.ReportSongSearching(song),
                    onFastSearchCandidate: fc =>
                    {
                        if (fastDownloadTask == null)
                        {
                            Logger.Debug($"Fast-search: starting provisional download from {fc.Username}");
                            string outputPath = organizer.GetSavePath(fc.Filename);
                            fastDownloadTask = downloader!
                                .DownloadFile(fc, outputPath, song, config, searchCts.Token)
                                .ContinueWith(t =>
                                {
                                    if (t.IsCompletedSuccessfully)
                                        return (outputPath, (FileCandidate?)fc);
                                    return ("", (FileCandidate?)null);
                                }, TaskScheduler.Default);
                        }
                    });

                // Wait for whichever finishes first.
                var neverComplete = new TaskCompletionSource<(string, FileCandidate?)>();
                await Task.WhenAny(fastDownloadTask ?? neverComplete.Task, searchTask);

                if (fastDownloadTask?.IsCompletedSuccessfully == true)
                {
                    var (fastPath, fastCandidate) = fastDownloadTask.Result;
                    if (fastPath.Length > 0 && fastCandidate != null)
                    {
                        // Fast download won — cancel the search. SearchSong will throw
                        // OperationCanceledException and release the concurrency slot internally.
                        searchCts.Cancel();
                        try { await searchTask; } catch (OperationCanceledException) { }
                        _registry.UserSuccessCounts.AddOrUpdate(fastCandidate.Username, 1, (_, c) => c + 1);
                        Logger.Debug("Fast-search: provisional download succeeded");
                        return (fastPath, fastCandidate.File);
                    }
                    // Fast download failed — fall through, wait for full search results.
                    Logger.Debug("Fast-search: provisional download failed, waiting for full search");
                }

                await searchTask;
            }
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
                song.State = JobState.Downloading;
                // ReportDownloadStart is called inside DownloadFile (via Downloader).
                await downloader!.DownloadFile(candidate, outputPath, song, config, cts.Token);
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

    public async Task<int> RetrieveFullFolderAsync(
        AlbumFolder folder, Config config, string? customMessage = null, CancellationToken ct = default)
    {
        customMessage ??= "Getting all files in folder...";
        Logger.Info(customMessage);

        return await searcher!.CompleteFolder(folder, ct == default ? appCts.Token : ct);
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
            bool   wasInteractive = SelectAlbumVersion != null;
            List<SongJob> imgs;

            if (SelectAlbumVersion != null)
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

            if (!wasInteractive)
            {
                var syntheticFolder = new AlbumFolder(
                    imgs[0].ResolvedTarget!.Response.Username,
                    Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename)),
                    imgs);
                _progressReporter.ReportAlbumDownloadStarted(job, syntheticFolder);
            }

            fileManager.downloadingAdditionalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(imgs.Select(af => af.ResolvedTarget!.Filename)));

            bool allSucceeded = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts!.Token);

            foreach (var af in imgs)
            {
                if (af.ResolvedTarget != null && af.Candidates == null)
                    af.Candidates = new List<FileCandidate> { af.ResolvedTarget };
                await DownloadSong(af, job, config, fileManager, cts, cancelOnFail: false, organize: true);
                if (af.State == JobState.Done)
                    result.Add(af);
                else
                    allSucceeded = false;
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
