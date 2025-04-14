using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using Soulseek;
using Models;
using Enums;
using Extractors;
using Services;
using Konsole;

using Directory = System.IO.Directory;
using File = System.IO.File;
using SlFile = Soulseek.File;

public static partial class Program
{
    const int updateInterval = 100;
    public static bool initialized = false;
    public static bool skipUpdate = false;
    public static bool interceptKeys = false;
    public static event EventHandler<ConsoleKey>? keyPressed;

    public static ISoulseekClient client = null!;
    public static IExtractor extractor = null!;
    public static Searcher searchService = null!;
    public static TrackLists trackLists = null!;

    public static readonly ConcurrentDictionary<Track, SearchInfo> searches = new();
    public static readonly ConcurrentDictionary<string, DownloadWrapper> downloads = new();
    public static readonly ConcurrentDictionary<string, int> userSuccessCounts = new();
    public static readonly ConcurrentDictionary<string, Track> downloadedFiles = new();

    public static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Help.PrintAndExitIfNeeded(args);

        Logger.SetupExceptionHandling();
        Logger.AddConsole();

        var config = new Config(args);

        Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());

        if (!config.RequiresInput)
        {
            PerformNoInputActions(config);
            Environment.Exit(0);
        }

        (config.inputType, extractor) = ExtractorRegistry.GetMatchingExtractor(config.input, config.inputType);

        Logger.Info($"Input ({config.inputType}): {config.input}");
        
        trackLists = await extractor.GetTracks(config.input, config.maxTracks, config.offset, config.reverse, config);

        Logger.Debug("Got tracks");

        config.PostProcessArgs();

        trackLists.UpgradeListTypes(config.aggregate, config.album);
        trackLists.SetListEntryOptions();

        PrepareListEntries(config);

        await MainLoop();

        Logger.Debug("Exiting");
    }


    public static async Task InitClientAndUpdateIfNeeded(Config config)
    {
        if (initialized)
            return;

        if (config.NeedLogin)
        {
            // If client is not null, assume it's injected for testing
            if (client == null)
            {
                if (!string.IsNullOrEmpty(config.mockFilesDir))
                {
                    client = Tests.ClientTests.MockSoulseekClient.FromLocalPaths(config.mockFilesReadTags, config.mockFilesDir);
                }
                else
                {
                    var connectionOptions = new ConnectionOptions(configureSocket: (socket) =>
                    {
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
                    });

                    var clientOptions = new SoulseekClientOptions(
                        transferConnectionOptions: connectionOptions,
                        serverConnectionOptions: connectionOptions,
                        listenPort: config.listenPort,
                        maximumConcurrentSearches: int.MaxValue
                    );

                    client = new SoulseekClient(clientOptions);
                }
            }

            if (!IsConnectedAndLoggedIn() && !config.useRandomLogin && (string.IsNullOrEmpty(config.username) || string.IsNullOrEmpty(config.password)))
                Config.InputError("No soulseek username or password");

            await Login(config, config.useRandomLogin);

            var searchSemaphore = new RateLimitedSemaphore(config.searchesPerTime, TimeSpan.FromSeconds(config.searchRenewTime));
            searchService = new Searcher(client, searchSemaphore);

            var UpdateTask = Task.Run(() => Update(config));
            Logger.Debug("Update started");
        }

        initialized = true;
    }


    static void PreprocessTracks(TrackListEntry tle)
    {
        static void preprocessTrack(Config config, Track track)
        {
            if (track.IsDirectLink)
            {
                return;
            }
            if (config.removeFt)
            {
                track.Title = track.Title.RemoveFt();
                track.Artist = track.Artist.RemoveFt();
            }
            if (config.removeBrackets)
            {
                track.Title = track.Title.RemoveSquareBrackets();
            }
            if (config.regex != null)
            {
                foreach (var (toReplace, replaceBy) in config.regex)
                {
                    track.Title = Regex.Replace(track.Title, toReplace.Title, replaceBy.Title, RegexOptions.IgnoreCase);
                    track.Artist = Regex.Replace(track.Artist, toReplace.Artist, replaceBy.Artist, RegexOptions.IgnoreCase);
                    track.Album = Regex.Replace(track.Album, toReplace.Album, replaceBy.Album, RegexOptions.IgnoreCase);
                }
            }
            if (config.parseTitleTemplate.Length > 0 && track.Title.Length > 0)
            {
                TrackTemplateParser.TryUpdateTrack(track.Title, config.parseTitleTemplate, track);
            }
            if (config.extractArtist && track.Title.Length > 0)
            {
                (var artist, var title) = Utils.SplitArtistAndTitle(track.Title);
                if (artist != null)
                {
                    track.Artist = artist;
                    track.Title = title;
                }
            }
            if (config.artistMaybeWrong)
            {
                track.ArtistMaybeWrong = true;
            }
            if (config.minAlbumTrackCount > 0)
            {
                track.MinAlbumTrackCount = config.minAlbumTrackCount;
            }
            if (config.maxAlbumTrackCount != -1)
            {
                track.MaxAlbumTrackCount = config.maxAlbumTrackCount;
            }

            track.Artist = track.Artist.Trim();
            track.Album = track.Album.Trim();
            track.Title = track.Title.Trim();
        }

        preprocessTrack(tle.config, tle.source);
        
        for (int k = 0; k < tle.list.Count; k++)
        {
            foreach (var ls in tle.list)
            {
                for (int i = 0; i < ls.Count; i++)
                {
                    preprocessTrack(tle.config, ls[i]);
                }
            }
        }
    }


    static void PrepareListEntries(Config startConfig)
    {
        var editors = new Dictionary<(string path, M3uOption option), M3uEditor>();
        var skippers = new Dictionary<(string dir, SkipMode mode, bool checkCond), TrackSkipper>();

        foreach (var tle in trackLists.lists)
        {
            tle.config = startConfig.Copy();
            tle.config.UpdateProfiles(tle);
            startConfig = tle.config;

            if (tle.extractorCond != null)
            {
                tle.config.necessaryCond.AddConditions(tle.extractorCond);
                tle.extractorCond = null;
            }
            if (tle.extractorPrefCond != null)
            {
                tle.config.preferredCond.AddConditions(tle.extractorPrefCond);
                tle.extractorPrefCond = null;
            }

            var indexOption = tle.config.WillWriteIndex() ? M3uOption.Index : M3uOption.None;
            if (indexOption != M3uOption.None || (tle.config.skipExisting && tle.config.skipMode == SkipMode.Index) || tle.config.skipNotFound)
            {
                string indexPath;
                if (tle.config.indexFilePath.Length > 0)
                    indexPath = tle.config.indexFilePath.Replace("{playlist-name}", tle.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
                else
                    indexPath = Path.Join(tle.config.parentDir, tle.DefaultFolderName(), "_index.csv");

                if (editors.TryGetValue((indexPath, indexOption), out var indexEditor))
                {
                    tle.indexEditor = indexEditor;
                }
                else
                {
                    tle.indexEditor = new M3uEditor(indexPath, trackLists, indexOption, true);
                    editors.Add((indexPath, indexOption), tle.indexEditor);
                }
            }

            var playlistOption = tle.config.writePlaylist ? M3uOption.Playlist : M3uOption.None;
            if (playlistOption != M3uOption.None)
            {
                string m3uPath;
                if (tle.config.m3uFilePath.Length > 0)
                    m3uPath = tle.config.m3uFilePath.Replace("{playlist-name}", tle.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
                else
                    m3uPath = Path.Join(tle.config.parentDir, tle.DefaultFolderName(), tle.DefaultPlaylistName());

                if (editors.TryGetValue((m3uPath, playlistOption), out var playlistEditor))
                {
                    tle.playlistEditor = playlistEditor;
                }
                else
                {
                    tle.playlistEditor = new M3uEditor(m3uPath, trackLists, playlistOption, false);
                    editors.Add((m3uPath, playlistOption), tle.playlistEditor);
                }
            }

            if (tle.config.skipExisting)
            {
                bool checkCond = tle.config.skipCheckCond || tle.config.skipCheckPrefCond;

                if (skippers.TryGetValue((tle.config.parentDir, tle.config.skipMode, checkCond), out var outputDirSkipper))
                {
                    tle.outputDirSkipper = outputDirSkipper;
                }
                else
                {
                    tle.outputDirSkipper = TrackSkipperRegistry.GetSkipper(tle.config.skipMode, tle.config.parentDir, checkCond);
                    skippers.Add((tle.config.parentDir, tle.config.skipMode, checkCond), tle.outputDirSkipper);
                }

                if (tle.config.skipMusicDir.Length > 0)
                {
                    if (skippers.TryGetValue((tle.config.skipMusicDir, tle.config.skipModeMusicDir, checkCond), out var musicDirSkipper))
                    {
                        tle.musicDirSkipper = musicDirSkipper;
                    }
                    else
                    {
                        tle.musicDirSkipper = TrackSkipperRegistry.GetSkipper(tle.config.skipModeMusicDir, tle.config.skipMusicDir, checkCond);
                        skippers.Add((tle.config.skipMusicDir, tle.config.skipModeMusicDir, checkCond), tle.musicDirSkipper);
                    }
                }
            }
        }
    }


    static async Task MainLoop()
    {
        if (trackLists.Count == 0) return;

        var tle0 = trackLists.lists[0];
        bool enableParallelSearch = tle0.config.parallelAlbumSearch && !tle0.config.PrintResults && !tle0.config.PrintTracks && trackLists.lists.Any(x => x.CanParallelSearch);
        var parallelSearches = new List<(TrackListEntry tle, Task<(bool, ResponseData)> task)>();
        var parallelSearchSemaphore = new SemaphoreSlim(tle0.config.parallelAlbumSearchProcesses);

        tle0.PrintLines();

        for (int i = 0; i < trackLists.lists.Count; i++)
        {
            if (!enableParallelSearch && i > 0) Console.WriteLine();

            var tle = trackLists[i];
            var config = tle.config;

            Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());

            if (tle.preprocessTracks) PreprocessTracks(tle);
            if (!enableParallelSearch) tle.PrintLines();

            var existing = new List<Track>();
            var notFound = new List<Track>();

            if (config.skipNotFound && !config.PrintResults)
            {
                if (tle.sourceCanBeSkipped && SetNotFoundLastTime(config, tle.source, tle.indexEditor))
                    notFound.Add(tle.source);

                if (tle.source.State != TrackState.NotFoundLastTime && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        notFound.AddRange(DoSkipNotFound(config, tracks, tle.indexEditor));
                }
            }

            if (config.skipExisting && !config.PrintResults && tle.source.State != TrackState.NotFoundLastTime)
            {
                if (tle.sourceCanBeSkipped && SetExisting(tle, TrackSkipperContext.FromTrackListEntry(tle), tle.source))
                    existing.Add(tle.source);

                if (tle.source.State != TrackState.AlreadyExists && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        existing.AddRange(DoSkipExisting(tle, tracks));
                }
            }

            if (config.PrintTracks)
            {
                if (tle.source.Type == TrackType.Normal)
                {
                    Printing.PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type, config);
                }
                else
                {
                    var tl = new List<Track>();
                    if (tle.source.State == TrackState.Initial) tl.Add(tle.source);
                    Printing.PrintTracksTbd(tl, existing, notFound, tle.source.Type, config, summary: false);
                }
                continue;
            }

            if (tle.sourceCanBeSkipped)
            {
                if (tle.source.State == TrackState.AlreadyExists)
                {
                    Logger.Info($"{tle.source.Type} download '{tle.source.ToString(true)}' already exists at {tle.source.DownloadPath}, skipping");
                    tle.indexEditor?.Update();
                    tle.playlistEditor?.Update();
                    continue;
                }
            
                if (tle.source.State == TrackState.NotFoundLastTime)
                {
                    Logger.Info($"{tle.source.Type} download '{tle.source.ToString(true)}' was not found during a prior run, skipping");
                    continue;
                }
            }

            if (tle.needSourceSearch)
            {
                await InitClientAndUpdateIfNeeded(config);

                ProgressBar? progress = null;

                async Task<(bool, ResponseData)> sourceSearch()
                {
                    await parallelSearchSemaphore.WaitAsync();

                    progress = enableParallelSearch ? Printing.GetProgressBar(config) : null;
                    var part = progress == null ? "" : "  ";
                    Printing.RefreshOrPrint(progress, 0, $"{part}{tle.source.Type} download: {tle.source.ToString(true)}, searching..", print: true);

                    bool foundSomething = false;
                    var responseData = new ResponseData();

                    if (tle.source.Type == TrackType.Album)
                    {
                        if (!tle.source.IsDirectLink)
                        {
                            tle.list = await searchService.GetAlbumDownloads(tle.source, responseData, config);
                        }
                        else
                        {
                            try
                            {
                                Printing.RefreshOrPrint(progress, 0, "Getting files in folder..", true);
                                tle.list = await searchService.GetDirectLinkAlbumFiles(tle.source);
                            }
                            catch (UserOfflineException e)
                            {
                                Logger.Error("Error: " + e.Message);
                            }
                        }
                        foundSomething = tle.list.Count > 0 && tle.list[0].Count > 0;
                    }
                    else if (tle.source.Type == TrackType.Aggregate)
                    {
                        tle.list.Insert(0, await searchService.GetAggregateTracks(tle.source, responseData, config));
                        foundSomething = tle.list.Count > 0 && tle.list[0].Count > 0;
                    }
                    else if (tle.source.Type == TrackType.AlbumAggregate)
                    {
                        var res = await searchService.GetAggregateAlbums(tle.source, responseData, config);

                        foreach (var item in res)
                        {
                            var newSource = new Track(tle.source) { Type = TrackType.Album, ItemNumber = -1 };
                            var albumTle = new TrackListEntry() 
                            {
                                source = newSource,
                                list = item,
                                config = config,
                                needSourceSearch = false,
                                sourceCanBeSkipped = true,
                                preprocessTracks = false,
                                indexEditor = tle.indexEditor,
                                playlistEditor = tle.playlistEditor,
                            };
                            albumTle.itemName = tle.itemName;
                            trackLists.AddEntry(albumTle);
                        }

                        foundSomething = res.Count > 0;
                    }

                    tle.needSourceSearch = false;

                    if (!foundSomething)
                    {
                        tle.source.State = TrackState.Failed;
                        tle.source.FailureReason = FailureReason.NoSuitableFileFound;
                        var lockedFiles = responseData.lockedFilesCount > 0 ? $" (Found {responseData.lockedFilesCount} locked files)" : "";
                        Printing.RefreshOrPrint(progress, 0, $"No results: {tle.source}{lockedFiles}", true);
                        
                        if (tle.source.Type == TrackType.Album)
                            await OnCompleteExecutor.ExecuteAsync(tle, tle.source, tle.indexEditor, tle.playlistEditor);
                    }
                    else if (progress != null)
                    {
                        Printing.RefreshOrPrint(progress, 0, $"Found results: {tle.source}", true);
                    }

                    parallelSearchSemaphore.Release();

                    return (foundSomething, responseData);
                }

                if (!enableParallelSearch || !tle.CanParallelSearch)
                {
                    (bool foundSomething, ResponseData responseData) = await sourceSearch();

                    if (!foundSomething)
                    {
                        if (!config.PrintResults)
                        {
                            tle.source.State = TrackState.Failed;
                            tle.source.FailureReason = FailureReason.NoSuitableFileFound;
                            tle.indexEditor?.Update();
                        }

                        continue;
                    }

                    if (config.skipExisting && tle.needSkipExistingAfterSearch)
                    {
                        foreach (var tracks in tle.list)
                            existing.AddRange(DoSkipExisting(tle, tracks));
                    }

                    if (tle.gotoNextAfterSearch)
                    {
                        continue;
                    }
                }
                else
                {
                    parallelSearches.Add((tle, sourceSearch()));
                    continue;
                }
            }

            if (config.PrintResults)
            {
                await InitClientAndUpdateIfNeeded(config);
                await Printing.PrintResults(tle, existing, notFound, config, searchService);
                continue;
            }

            if (parallelSearches.Count > 0 && !tle.CanParallelSearch)
            {
                await parallelDownloads();
            }

            if (!enableParallelSearch || !tle.CanParallelSearch)
            {
                await download(tle, config, notFound, existing);
            }
        }

        if (parallelSearches.Count > 0)
        {
            await parallelDownloads();
        }

        if (!trackLists[^1].config.DoNotDownload && (trackLists.lists.Count > 0 || trackLists.Flattened(false, false).Skip(1).Any()))
        {
            Printing.PrintComplete(trackLists);
        }

        async Task download(TrackListEntry tle, Config config, List<Track>? notFound, List<Track>? existing)
        {
            tle.indexEditor?.Update();
            tle.playlistEditor?.Update();

            if (tle.source.Type != TrackType.Album)
            {
                Printing.PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type, config);
            }

            if (notFound != null && existing != null && notFound.Count + existing.Count >= tle.list.Sum(x => x.Count))
            {
                return;
            }

            await InitClientAndUpdateIfNeeded(config);

            if (tle.source.Type == TrackType.Normal)
            {
                await DownloadNormal(config, tle);
            }
            else if (tle.source.Type == TrackType.Album)
            {
                await DownloadAlbum(config, tle);
            }
            else if (tle.source.Type == TrackType.Aggregate)
            {
                await DownloadNormal(config, tle);
            }
        }

        async Task parallelDownloads()
        {
            await Task.WhenAll(parallelSearches.Select(x => x.task));

            foreach (var (tle, task) in parallelSearches)
            {
                (bool foundSomething, var responseData) = task.Result;

                if (foundSomething)
                {
                    Logger.Info($"Downloading: {tle.source}");
                    await download(tle, tle.config, null, null);
                }
                else
                {
                    if (!tle.config.PrintResults)
                    {
                        tle.source.State = TrackState.Failed;
                        tle.source.FailureReason = FailureReason.NoSuitableFileFound;
                        tle.indexEditor?.Update();
                    }
                    if (tle.config.skipExisting && tle.needSkipExistingAfterSearch)
                    {
                        foreach (var tracks in tle.list)
                            DoSkipExisting(tle, tracks);
                    }
                }
            }

            parallelSearches.Clear();
        }
    }


    static List<Track> DoSkipExisting(TrackListEntry tle, List<Track> tracks)
    {
        var context = TrackSkipperContext.FromTrackListEntry(tle);
        var existing = new List<Track>();
        foreach (var track in tracks)
        {
            if (SetExisting(tle, context, track))
            {
                existing.Add(track);
            }
        }
        return existing;
    }


    static bool SetExisting(TrackListEntry tle, TrackSkipperContext context, Track track)
    {
        string? path = null;

        if (tle.outputDirSkipper != null)
        {
            if (!tle.outputDirSkipper.IndexIsBuilt)
                tle.outputDirSkipper.BuildIndex();

            tle.outputDirSkipper.TrackExists(track, context, out path);
        }

        if (path == null && tle.musicDirSkipper != null)
        {
            if (!tle.musicDirSkipper.IndexIsBuilt)
            {
                Logger.Info($"Building music directory index..");
                tle.musicDirSkipper.BuildIndex();
            }

            tle.musicDirSkipper.TrackExists(track, context, out path);
        }

        if (path != null)
        {
            track.State = TrackState.AlreadyExists;
            track.DownloadPath = path;
        }

        return path != null;
    }


    static List<Track> DoSkipNotFound(Config config, List<Track> tracks, M3uEditor indexEditor)
    {
        var notFound = new List<Track>();
        foreach (var track in tracks)
        {
            if (SetNotFoundLastTime(config, track, indexEditor))
            {
                notFound.Add(track);
            }
        }
        return notFound;
    }


    static bool SetNotFoundLastTime(Config config, Track track, M3uEditor indexEditor)
    {
        if (indexEditor.TryGetPreviousRunResult(track, out var prevTrack))
        {
            if (prevTrack.FailureReason == FailureReason.NoSuitableFileFound || prevTrack.State == TrackState.NotFoundLastTime)
            {
                track.State = TrackState.NotFoundLastTime;
                return true;
            }
        }
        return false;
    }


    static async Task DownloadNormal(Config config, TrackListEntry tle)
    {
        var tracks = tle.list[0];

        var semaphore = new SemaphoreSlim(config.concurrentProcesses);

        var organizer = new FileManager(tle, config);

        var downloadTasks = tracks.Select(async (track, index) =>
        {
            using var cts = new CancellationTokenSource();
            await DownloadTask(config, tle, track, semaphore, organizer, cts, false, true, true);
            tle.indexEditor?.Update();
            tle.playlistEditor?.Update();
        });

        await Task.WhenAll(downloadTasks);

        if (config.removeTracksFromSource && tracks.All(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
            await extractor.RemoveTrackFromSource(tle.source);
    }


    static async Task DownloadAlbum(Config config, TrackListEntry tle)
    {
        var organizer = new FileManager(tle, config);
        List<Track>? tracks = null;
        var retrievedFolders = new HashSet<string>();
        bool succeeded = false;
        string? soulseekDir = null;
        string? filterStr = null;
        int index = 0;
        int albumTrackCountRetries = config.albumTrackCountMaxRetries;

        async Task runAlbumDownloads(List<Track> tracks, SemaphoreSlim semaphore, CancellationTokenSource cts)
        {
            var downloadTasks = tracks.Select(async track =>
            {
                await DownloadTask(config, tle, track, semaphore, organizer, cts, true, true, true);
            });
            await Task.WhenAll(downloadTasks);
        }

        async Task<int> getFullFolder(List<Track> tracks, string? soulseekDir = null)
        {
            soulseekDir ??= Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));
            if (retrievedFolders.Contains(soulseekDir)) return 0;

            Logger.Info("Getting all files in folder...");
            int newFilesFound = await searchService.CompleteFolder(tracks, tracks[0].FirstResponse, soulseekDir);
            retrievedFolders.Add(soulseekDir);
            return newFilesFound;
        }

        while (tle.list.Count > 0 && !config.albumArtOnly)
        {
            bool wasInteractive = config.interactiveMode;
            bool retrieveCurrent = true;
            index = 0;

            if (config.interactiveMode)
            {
                var interactive = new InteractiveModeManager(tle, tle.list, true, retrievedFolders, searchService, filterStr);
                (index, tracks, retrieveCurrent, filterStr) = await interactive.Run();
                if (index == -1) break;
                if (index == -2) Environment.Exit(0);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(filterStr))
                {
                    index = tle.list.IndexOf(ls => ls.Any(x => x.FirstDownload.Filename.ContainsIgnoreCase(filterStr)));
                    if (index == -1) break;
                }

                tracks = tle.list[index];

                // Need to check track counts again in case search results did not contain full folders.
                // New track count will always be >= old count. Lower bound check in Searcher.GetAlbumDownloads is disabled
                // when track title is non-empty, therefore need to only check lower bound when title is non-empty.
                if (config.albumTrackCountMaxRetries > 0 && (tle.source.MaxAlbumTrackCount > 0 || (tle.source.MinAlbumTrackCount > 0 && tle.source.Title.Length > 0)))
                {
                    await getFullFolder(tracks);
                    int newCount = tracks.Count(t => !t.IsNotAudio);
                    bool failed = false;

                    if (tle.source.MaxAlbumTrackCount > 0 && newCount > tle.source.MaxAlbumTrackCount)
                    {
                        Logger.Info($"New file count ({newCount}) above maximum ({tle.source.MaxAlbumTrackCount}), skipping folder");
                        failed = true;
                    }
                    if (tle.source.MinAlbumTrackCount > 0 && newCount < tle.source.MinAlbumTrackCount)
                    {
                        Logger.Info($"New file count ({newCount}) below minimum ({tle.source.MinAlbumTrackCount}), skipping folder");
                        failed = true;
                    }

                    if (failed)
                    {
                        tle.list.RemoveAt(index);

                        if (--albumTrackCountRetries <= 0)
                        {
                            Logger.Info($"Failed album track count condition {config.albumTrackCountMaxRetries} times, skipping album.");
                            tle.source.State = TrackState.Failed;
                            tle.source.FailureReason = FailureReason.NoSuitableFileFound;
                            break;
                        }

                        continue;
                    }
                }
            }

            soulseekDir = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));

            if (tle.source.IsDirectLink)
                retrievedFolders.Add(soulseekDir);

            organizer.SetRemoteCommonDir(soulseekDir);

            if (!config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                Printing.PrintAlbum(tracks);
            }

            using var semaphore = new SemaphoreSlim(999); // Needs to be uncapped due to a bug that causes album downloads to fail after some time
            using var cts = new CancellationTokenSource();

            bool userCancelled = false;
            void onKeyPressed(object? sender, ConsoleKey key)
            {
                if (key == ConsoleKey.C && !userCancelled)
                {
                    Logger.Debug("User cancelled download");
                    userCancelled = true;
                    cts.Cancel();
                }
            }
            interceptKeys = true;
            keyPressed += onKeyPressed;

            try
            {
                await runAlbumDownloads(tracks, semaphore, cts);

                if (!config.noBrowseFolder && retrieveCurrent && !retrievedFolders.Contains(soulseekDir))
                {
                    Console.WriteLine();
                    int newFilesFound = await getFullFolder(tracks, soulseekDir);
                    if (newFilesFound > 0)
                    {
                        Logger.Info($"Found {newFilesFound} more files, downloading:");
                        await runAlbumDownloads(tracks, semaphore, cts);
                    }
                    else
                    {
                        Logger.Info("No more files found.");
                    }
                }

                succeeded = true;
                break;
            }
            catch (OperationCanceledException)
            {
                if (userCancelled)
                {
                    Console.WriteLine();
                    Logger.Info("Download cancelled. ");

                    if (tracks.Any(t => t.State == TrackState.Downloaded && t.DownloadPath.Length > 0))
                    {
                        var defaultAction = config.DeleteAlbumOnFail ? "Yes" : config.IgnoreAlbumFail ? "No" : $"Move to {config.failedAlbumPath}";
                        Console.Write($"Delete files? [Y/n] (default: {defaultAction}): ");
                        var res = Console.ReadLine().Trim().ToLower();
                        if (res == "y") 
                            OnAlbumFail(tracks, true, config);
                        else if (res == "" && !config.IgnoreAlbumFail) 
                            OnAlbumFail(tracks, config.DeleteAlbumOnFail, config);
                    }

                    if (!config.interactiveMode)
                    {
                        Logger.Info("Entering interactive mode");
                        config.interactiveMode = true;
                        tle.config.UpdateProfiles(tle);
                        tle.PrintLines();
                    }
                }
                else
                {
                    if (!config.IgnoreAlbumFail) 
                        OnAlbumFail(tracks, config.DeleteAlbumOnFail, config); 
                }
            }
            finally
            {
                interceptKeys = false;
                keyPressed -= onKeyPressed;
            }

            if (!succeeded)
            {
                organizer.SetRemoteCommonDir(null);
                tle.list.RemoveAt(index);
            }
        }

        if (succeeded)
        {
            tle.source.State = TrackState.Downloaded;

            var downloadedAudio = tracks.Where(t => !t.IsNotAudio && t.State == TrackState.Downloaded && t.DownloadPath.Length > 0);
            if (downloadedAudio.Any())
            {
                tle.source.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(t => t.DownloadPath));

                if (config.removeTracksFromSource)
                {
                    await extractor.RemoveTrackFromSource(tle.source);
                }
            }
        }
        else if (index != -1)
        {
            tle.source.State = TrackState.Failed;
        }

        List<Track>? additionalImages = null;
        
        if (config.albumArtOnly || succeeded && config.albumArtOption != AlbumArtOption.Default)
        {
            Logger.Info($"Downloading additional images:");
            additionalImages = await DownloadImages(config, tle, tle.list, config.albumArtOption, tle.list[index], organizer);
            tracks?.AddRange(additionalImages);
        }

        if (tracks != null && tle.source.DownloadPath.Length > 0)
        {
            organizer.OrganizeAlbum(tle.source, tracks, additionalImages);
        }

        tle.indexEditor?.Update();
        tle.playlistEditor?.Update();

        await OnCompleteExecutor.ExecuteAsync(tle, tle.source, tle.indexEditor, tle.playlistEditor);
    }


    static void OnAlbumFail(List<Track>? tracks, bool deleteDownloaded, Config config)
    {
        if (tracks == null) return;

        if (deleteDownloaded)
            Logger.Info($"Deleting album files");
        else if (config.failedAlbumPath.Length > 0)
            Logger.Info($"Moving album files to {config.failedAlbumPath}");

        foreach (var track in tracks)
        {
            if (track.DownloadPath.Length > 0 && File.Exists(track.DownloadPath))
            {
                try
                {
                    if (deleteDownloaded || track.DownloadPath.EndsWith(".incomplete"))
                    {
                        File.Delete(track.DownloadPath);
                    }
                    else if (config.failedAlbumPath.Length > 0)
                    {
                        var newPath = Path.Join(config.failedAlbumPath, Path.GetRelativePath(config.parentDir, track.DownloadPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                        Utils.Move(track.DownloadPath, newPath);
                    }

                    Utils.DeleteAncestorsIfEmpty(Path.GetDirectoryName(track.DownloadPath), config.parentDir);
                }
                catch (Exception e) 
                {
                    Logger.Error($"Error: Unable to move or delete file '{track.DownloadPath}' after album fail: {e}");
                }
            }
        }
    }


    static async Task<List<Track>> DownloadImages(Config config, TrackListEntry tle, List<List<Track>> downloads, AlbumArtOption option, List<Track>? chosenAlbum, FileManager fileManager)
    {
        var downloadedImages = new List<Track>();
        long mSize = 0;
        int mCount = 0;

        if (chosenAlbum != null)
        {
            string dir = Utils.GreatestCommonDirectorySlsk(chosenAlbum.Select(t => t.FirstDownload.Filename));
            fileManager.SetDefaultFolderName(Path.GetFileName(Utils.NormalizedPath(dir))); 
        }

        if (option == AlbumArtOption.Default)
            return downloadedImages;

        int[]? sortedLengths = null;

        if (chosenAlbum != null && chosenAlbum.Any(t => !t.IsNotAudio))
            sortedLengths = chosenAlbum.Where(t => !t.IsNotAudio).Select(t => t.Length).OrderBy(x => x).ToArray();

        var albumArts = downloads
            .Where(ls => chosenAlbum == null || searchService.AlbumsAreSimilar(chosenAlbum, ls, sortedLengths))
            .Select(ls => ls.Where(t => Utils.IsImageFile(t.FirstDownload.Filename)))
            .Where(ls => ls.Any());

        if (!albumArts.Any())
        {
            Logger.Info("No images found");
            return downloadedImages;
        }
        else if (!albumArts.Skip(1).Any() && albumArts.First().All(y => y.State != TrackState.Initial))
        {
            Logger.Info("No additional images found");
            return downloadedImages;
        }

        if (option == AlbumArtOption.Largest)
        {
            albumArts = albumArts
                .OrderByDescending(tracks => tracks.Select(t => t.FirstDownload.Size).Max() / 1024 / 100)
                .ThenByDescending(tracks => tracks.First().FirstResponse.UploadSpeed / 1024 / 300)
                .ThenByDescending(tracks => tracks.Select(t => t.FirstDownload.Size).Sum() / 1024 / 100);

            if (chosenAlbum != null)
            {
                mSize = chosenAlbum
                    .Where(t => t.State == TrackState.Downloaded && Utils.IsImageFile(t.DownloadPath))
                    .Select(t => t.FirstDownload.Size)
                    .DefaultIfEmpty(0)
                    .Max();
            }
        }
        else if (option == AlbumArtOption.Most)
        {
            albumArts = albumArts
                .OrderByDescending(tracks => tracks.Count())
                .ThenByDescending(tracks => tracks.First().FirstResponse.UploadSpeed / 1024 / 300)
                .ThenByDescending(tracks => tracks.Select(t => t.FirstDownload.Size).Sum() / 1024 / 100);

            if (chosenAlbum != null)
            {
                mCount = chosenAlbum
                    .Count(t => t.State == TrackState.Downloaded && Utils.IsImageFile(t.DownloadPath));
            }
        }

        var albumArtLists = albumArts.Select(ls => ls.ToList()).ToList();

        bool needImageDownload(List<Track> list)
        {
            if (option == AlbumArtOption.Most)
                return mCount < list.Count;
            else if (option == AlbumArtOption.Largest)
                return mSize < list.Max(t => t.FirstDownload.Size) - 1024 * 50;
            return true;
        }

        string? filterStr = null;

        while (albumArtLists.Count > 0)
        {
            int index = 0;
            bool wasInteractive = config.interactiveMode;
            List<Track> tracks;

            if (config.interactiveMode)
            {
                var interactive = new InteractiveModeManager(tle, albumArtLists, false, null, searchService, filterStr);
                (index, tracks, _, filterStr) = await interactive.Run();
                if (index == -1) break;
                if (index == -2) Environment.Exit(0);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(filterStr))
                {
                    index = albumArtLists.IndexOf(ls => ls.Any(x => x.FirstDownload.Filename.ContainsIgnoreCase(filterStr)));
                    if (index == -1) break;
                }

                tracks = albumArtLists[index];
            }

            albumArtLists.RemoveAt(index);

            bool allDownloaded = tracks.All(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists);
            if (allDownloaded || !wasInteractive && !needImageDownload(tracks))
            {
                Logger.Info("Image requirements already satisfied.");
                return downloadedImages;
            }

            if (!config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                Printing.PrintAlbum(tracks);
            }

            fileManager.downloadingAdditinalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename)));

            bool allSucceeded = true;
            using var semaphore = new SemaphoreSlim(1);
            using var cts = new CancellationTokenSource();

            bool userCancelled = false;
            void onKeyPressed(object? sender, ConsoleKey key)
            {
                if (key == ConsoleKey.C)
                {
                    userCancelled = true;
                    cts.Cancel();
                }
            }
            interceptKeys = true;
            keyPressed += onKeyPressed;

            try
            {
                foreach (var track in tracks)
                {
                    await DownloadTask(config, tle, track, semaphore, fileManager, cts, false, false, false);

                    if (track.State == TrackState.Downloaded)
                        downloadedImages.Add(track);
                    else
                        allSucceeded = false;
                }
            }
            catch (OperationCanceledException)
            {
                if (userCancelled)
                {
                    Console.WriteLine();
                    Logger.Info("Download cancelled. ");
                    if (tracks.Any(t => t.State == TrackState.Downloaded && t.DownloadPath.Length > 0))
                    {
                        Console.Write("Delete files? [Y/n] (default: Yes): ");
                        var res = Console.ReadLine().Trim().ToLower();
                        if (res == "y" || res == "")
                            OnAlbumFail(tracks, true, config);
                    }

                    if (!config.interactiveMode)
                    {
                        Logger.Info("Entering interactive mode");
                        config.interactiveMode = true;
                    }

                    continue;
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                interceptKeys = false;
                keyPressed -= onKeyPressed;
            }

            if (allSucceeded)
                break;
        }

        return downloadedImages;
    }


    static async Task DownloadTask(Config config, TrackListEntry tle, Track track, SemaphoreSlim semaphore, FileManager organizer, CancellationTokenSource cts, bool cancelOnFail, bool removeFromSource, bool organize)
    {
        if (track.State != TrackState.Initial)
            return;

        await semaphore.WaitAsync(cts.Token);

        int tries = config.unknownErrorRetries;
        string savedFilePath = "";
        SlFile? chosenFile = null;

        ProgressBar? progress = null;

        while (tries > 0)
        {
            await WaitForLogin(config);

            cts.Token.ThrowIfCancellationRequested();

            try
            {
                progress = Printing.GetProgressBar(config);
                (savedFilePath, chosenFile) = await searchService.SearchAndDownload(track, organizer, tle, config, progress, cts);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    Logger.DebugError($"{ex}");
                }
                else
                {
                    Logger.Debug($"Cancelled: {track}");
                }

                if (!IsConnectedAndLoggedIn())
                {
                    continue;
                }
                else if (ex is SearchAndDownloadException sdEx)
                {
                    lock (trackLists)
                    {
                        track.State = TrackState.Failed;
                        track.FailureReason = sdEx.reason;
                    }

                    if (cancelOnFail)
                    {
                        cts.Cancel();
                        throw new OperationCanceledException();
                    }
                }
                else if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    lock (trackLists)
                    {
                        track.State = TrackState.Failed;
                        track.FailureReason = FailureReason.Other;
                    }
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
            lock (trackLists)
            {
                track.State = TrackState.Failed;
                track.FailureReason = FailureReason.Other;
            }

            cts.Cancel();
            throw new OperationCanceledException();
        }

        if (savedFilePath.Length > 0)
        {
            lock (trackLists)
            {
                track.State = TrackState.Downloaded;
                track.DownloadPath = savedFilePath;
            }

            if (removeFromSource && config.removeTracksFromSource)
            {
                try
                {
                    await extractor.RemoveTrackFromSource(track);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error while removing track from source: {ex.Message}\n{ex.StackTrace}\n");
                }
            }
        }

        if (track.State == TrackState.Downloaded && organize)
        {
            lock (trackLists)
            {
                organizer?.OrganizeAudio(track, chosenFile);
            }
        }

        if (tle.config.HasOnComplete)
        {
            var savedText = progress.Line1;
            var savedPos = progress.Current;
            Printing.RefreshOrPrint(progress, savedPos, "  OnComplete:".PadRight(14) + $" {track}");
            await OnCompleteExecutor.ExecuteAsync(tle, track, tle.indexEditor, tle.playlistEditor);
            Printing.RefreshOrPrint(progress, savedPos, savedText);
        }

        semaphore.Release();
    }


    static async Task Update(Config startConfig)
    {
        while (true)
        {
            if (!skipUpdate)
            {
                try
                {
                    if (IsConnectedAndLoggedIn())
                    {
                        foreach (var (key, val) in searches)
                        {
                            if (val == null)
                                searches.TryRemove(key, out _);
                        }

                        foreach (var (key, val) in downloads)
                        {
                            if (val != null)
                            {
                                lock (val)
                                {
                                    if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > val.tle.config.maxStaleTime)
                                    {
                                        Logger.Debug($"Cancelling stale download: {val.displayText}");
                                        val.stalled = true;
                                        val.UpdateText();

                                        try { val.cts.Cancel(); } catch { }
                                        downloads.TryRemove(key, out _);
                                    }
                                    else
                                    {
                                        val.UpdateText();
                                    }
                                }
                            }
                            else
                            {
                                downloads.TryRemove(key, out _);
                            }
                        }
                    }
                    else
                    {
                        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn) 
                            && !client.State.HasFlag(SoulseekClientStates.LoggingIn) 
                            && !client.State.HasFlag(SoulseekClientStates.Connecting))
                        {
                            Logger.Warn($"Disconnected, logging in");
                            try { await Login(startConfig, startConfig.useRandomLogin); }
                            catch (Exception ex)
                            {
                                string banMsg = startConfig.useRandomLogin ? "" : " (possibly a 30-minute ban caused by frequent searches)";
                                Logger.Warn($"{ex.Message}{banMsg}");
                            }
                        }

                        foreach (var (key, val) in downloads)
                        {
                            if (val != null)
                                lock (val) { val.UpdateLastChangeTime(updateAllFromThisUser: false, forceChanged: true); }
                            else
                                downloads.TryRemove(key, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"{ex.Message}");
                }

                if (interceptKeys && Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    keyPressed?.Invoke(null, key);
                }
            }

            await Task.Delay(updateInterval);
        }
    }


    static async Task Login(Config config, bool random = false, int tries = 3)
    {
        string user = config.username, pass = config.password;
        if (random)
        {
            var r = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            pass = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
        }

        Logger.Info($"Login {user}");

        while (true)
        {
            try
            {
                Logger.Debug($"Connecting {user}");
                await client.ConnectAsync(user, pass);
                if (!config.noModifyShareCount)
                {
                    Logger.Debug($"Setting share count");
                    await client.SetSharedCountsAsync(50, 1000);
                }
                break;
            }
            catch (Exception e)
            {
                Logger.DebugError($"Exception while logging in: {e}");
                if (!(e is Soulseek.AddressException || e is System.TimeoutException) && --tries == 0)
                    throw;
            }
            await Task.Delay(500);
            Logger.Debug($"Retry login {user}");
        }

        Logger.Debug($"Logged in {user}");
    }


    static void PerformNoInputActions(Config config)
    {
        if (config.printOption.HasFlag(PrintOption.Index))
        {
            if (string.IsNullOrEmpty(config.indexFilePath))
            {
                Logger.Fatal("Error: No index file path provided");
                Environment.Exit(1);
            }

            var indexFilePath = Utils.GetFullPath(Utils.ExpandVariables(config.indexFilePath));
            
            if (!File.Exists(indexFilePath))
            {
                Logger.Fatal($"Error: Index file {indexFilePath} does not exist");
                Environment.Exit(1);
            }

            var index = new M3uEditor(indexFilePath, new TrackLists(), M3uOption.Index, true);
            var data = index.GetPreviousRunData().AsEnumerable();

            if (config.printOption.HasFlag(PrintOption.IndexFailed))
            {
                data = data.Where(t => t.State == TrackState.Failed);
            }

            JsonPrinter.PrintIndexJson(data);
        }
    }


    public static async Task WaitForLogin(Config config)
    {
        while (true)
        {
            Logger.Trace($"Wait for login, state: {client.State}");
            if (IsConnectedAndLoggedIn())
                break;
            await Task.Delay(1000);
        }
    }


    public static bool IsConnectedAndLoggedIn()
    {
        return client != null && client.State.HasFlag(SoulseekClientStates.Connected) && client.State.HasFlag(SoulseekClientStates.LoggedIn);
    }
}
