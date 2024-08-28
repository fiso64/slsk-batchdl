using AngleSharp.Text;
using Konsole;
using Soulseek;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;

using Data;
using Enums;
using ExistingCheckers;

using Directory = System.IO.Directory;
using File = System.IO.File;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlResponse = Soulseek.SearchResponse;


static partial class Program
{
    public static Extractors.IExtractor? extractor;
    public static ExistingChecker? outputExistingChecker;
    public static ExistingChecker? musicDirExistingChecker;
    public static SoulseekClient? client;
    public static TrackLists? trackLists;
    public static M3uEditor? m3uEditor;
    static RateLimitedSemaphore? searchSemaphore;
    static CancellationTokenSource? mainLoopCts;
    static readonly ConcurrentDictionary<Track, SearchInfo> searches = new();
    static readonly ConcurrentDictionary<string, DownloadWrapper> downloads = new();
    static readonly ConcurrentDictionary<string, int> userSuccessCount = new();
    static bool skipUpdate = false;
    static bool initialized = false;
    static string? soulseekFolderPathPrefix;
    static readonly object consoleLock = new();

    static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int helpIdx = Array.FindIndex(args, x => x == "--help" || x == "-h");
        if (args.Length == 0 || helpIdx >= 0)
        {
            string option = helpIdx + 1 < args.Length ? args[helpIdx + 1] : "";
            Help.PrintHelp(option);
            return;
        }

        bool doContinue = Config.ParseArgsAndReadConfig(args);

        if (!doContinue)
            return;

        if (Config.input.Length == 0)
            throw new ArgumentException($"No input provided");

        (Config.inputType, extractor) = Extractors.ExtractorRegistry.GetMatchingExtractor(Config.input);

        if (Config.inputType == InputType.None)
            throw new ArgumentException($"No matching extractor for input '{Config.input}'");

        WriteLine($"Using extractor: {Config.inputType}", debugOnly: true);

        trackLists = await extractor.GetTracks(Config.maxTracks, Config.offset, Config.reverse);

        WriteLine("Got tracks", debugOnly: true);

        trackLists.UpgradeListTypes(Config.aggregate, Config.album);

        trackLists.SetListEntryOptions();

        Config.PostProcessArgs();

        m3uEditor = new M3uEditor(Config.m3uFilePath, trackLists, Config.m3uOption, Config.offset);

        InitExistingCheckers();

        await MainLoop();
        WriteLine("Mainloop done", debugOnly: true);
    }


    static async Task InitClientAndUpdateIfNeeded()
    {
        if (initialized)
            return;

        bool needLogin = !Config.PrintTracks;
        if (needLogin)
        {
            client = new SoulseekClient(new SoulseekClientOptions(listenPort: Config.listenPort));
            if (!Config.useRandomLogin && (string.IsNullOrEmpty(Config.username) || string.IsNullOrEmpty(Config.password)))
                throw new ArgumentException("No soulseek username or password");
            await Login(Config.useRandomLogin);
        }

        bool needUpdate = needLogin;
        if (needUpdate)
        {
            var UpdateTask = Task.Run(() => Update());
            WriteLine("Update started", debugOnly: true);
        }

        searchSemaphore = new RateLimitedSemaphore(Config.searchesPerTime, TimeSpan.FromSeconds(Config.searchRenewTime));
        initialized = true;
    }


    static void InitExistingCheckers()
    {
        if (Config.skipExisting)
        {
            var cond = Config.skipExistingPrefCond ? Config.preferredCond : Config.necessaryCond;

            if (Config.musicDir.Length == 0 || !Config.outputFolder.StartsWith(Config.musicDir, StringComparison.OrdinalIgnoreCase))
                outputExistingChecker = ExistingCheckerRegistry.GetChecker(Config.skipMode, Config.outputFolder, cond, m3uEditor);

            if (Config.musicDir.Length > 0)
            {
                if (!Directory.Exists(Config.musicDir))
                    Console.WriteLine("Error: Music directory does not exist");
                else
                    musicDirExistingChecker = ExistingCheckerRegistry.GetChecker(Config.skipModeMusicDir, Config.musicDir, cond, m3uEditor);
            }
        }
    }


    static void PreprocessTracks(TrackListEntry tle)
    {
        for (int k = 0; k < tle.list.Count; k++)
        {
            PreprocessTrack(tle.source);
            foreach (var ls in tle.list)
            {
                for (int i = 0; i < ls.Count; i++)
                {
                    PreprocessTrack(ls[i]);
                }
            }
        }
    }
    

    static void PreprocessTrack(Track track)
    {
        if (Config.removeFt)
        {
            track.Title = track.Title.RemoveFt();
            track.Artist = track.Artist.RemoveFt();
        }
        if (Config.removeBrackets)
        {
            track.Title = track.Title.RemoveSquareBrackets();
        }
        if (Config.regexToReplace.Title.Length + Config.regexToReplace.Artist.Length + Config.regexToReplace.Album.Length > 0)
        {
            track.Title = Regex.Replace(track.Title, Config.regexToReplace.Title, Config.regexReplaceBy.Title);
            track.Artist = Regex.Replace(track.Artist, Config.regexToReplace.Artist, Config.regexReplaceBy.Artist);
            track.Album = Regex.Replace(track.Album, Config.regexToReplace.Album, Config.regexReplaceBy.Album);
        }
        if (Config.artistMaybeWrong)
        {
            track.ArtistMaybeWrong = true;
        }

        track.Artist = track.Artist.Trim();
        track.Album = track.Album.Trim();
        track.Title = track.Title.Trim();
    }


    static async Task MainLoop()
    {
        for (int i = 0; i < trackLists.lists.Count; i++)
        {
            if (i > 0) Console.WriteLine();

            var tle = trackLists[i];

            Config.UpdateArgs(tle);

            PreprocessTracks(tle);

            var existing = new List<Track>();
            var notFound = new List<Track>();
            var responseData = new ResponseData();

            if (Config.skipNotFound && !Config.PrintResults)
            {
                if (tle.sourceCanBeSkipped && SetNotFoundLastTime(tle.source))
                    notFound.Add(tle.source);

                if (tle.source.State != TrackState.NotFoundLastTime && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        notFound.AddRange(DoSkipNotFound(tracks));
                }
            }

            if (Config.skipExisting && !Config.PrintResults && tle.source.State != TrackState.NotFoundLastTime)
            {
                if (tle.sourceCanBeSkipped && SetExisting(tle.source))
                    existing.Add(tle.source);

                if (tle.source.State != TrackState.AlreadyExists && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        existing.AddRange(DoSkipExisting(tracks));
                }
            }

            if (Config.PrintTracks)
            {
                if (tle.source.Type == TrackType.Normal)
                {
                    PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type);
                }
                else
                {
                    var tl = new List<Track>();
                    if (tle.source.State == TrackState.Initial) tl.Add(tle.source);
                    PrintTracksTbd(tl, existing, notFound, tle.source.Type);
                }
                continue;
            }

            if (tle.sourceCanBeSkipped)
            {
                if (tle.source.State == TrackState.AlreadyExists)
                {
                    Console.WriteLine($"{tle.source.Type} download '{tle.source.ToString(true)}' already exists at {tle.source.DownloadPath}, skipping");
                    continue;
                }
            
                if (tle.source.State == TrackState.NotFoundLastTime)
                {
                    Console.WriteLine($"{tle.source.Type} download '{tle.source.ToString(true)}' was not found during a prior run, skipping");
                    continue;
                }
            }

            if (tle.needSourceSearch)
            {
                await InitClientAndUpdateIfNeeded();

                Console.WriteLine($"{tle.source.Type} download: {tle.source.ToString(true)}, searching..");

                if (tle.source.Type == TrackType.Album)
                {
                    tle.list = await GetAlbumDownloads(tle.source, responseData);
                }
                else if (tle.source.Type == TrackType.Aggregate)
                {
                    tle.list.Insert(0, await GetAggregateTracks(tle.source, responseData));
                }
                else if (tle.source.Type == TrackType.AlbumAggregate)
                {
                    var res = await GetAggregateAlbums(tle.source, responseData);

                    foreach (var item in res)
                    {
                        var newSource = new Track(tle.source) { Type = TrackType.Album };
                        trackLists.AddEntry(new TrackListEntry(item, newSource, false, true, true, true, false, false));
                    }
                }

                if (Config.skipExisting && tle.needSkipExistingAfterSearch)
                {
                    foreach (var tracks in tle.list)
                        existing.AddRange(DoSkipExisting(tracks));
                }

                if (tle.gotoNextAfterSearch)
                {
                    continue;
                }
            }

            if (Config.PrintResults)
            {
                await PrintResults(tle, existing, notFound);
                continue;
            }

            if (tle.needSourceSearch && (tle.list.Count == 0 || !tle.list.Any(x => x.Count > 0)))
            {
                string lockedFilesStr = responseData.lockedFilesCount > 0 ? $" (Found {responseData.lockedFilesCount} locked files)" : "";
                Console.WriteLine($"No results.{lockedFilesStr}");

                tle.source.State = TrackState.Failed;
                tle.source.FailureReason = FailureReason.NoSuitableFileFound;

                m3uEditor.Update();
                continue;
            }

            m3uEditor.Update();

            if (tle.source.Type != TrackType.Album)
            {
                PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type);
            }

            if (notFound.Count + existing.Count >= tle.list.Sum(x => x.Count))
            {
                continue;
            }

            await InitClientAndUpdateIfNeeded();

            if (tle.source.Type == TrackType.Normal)
            {
                await TracksDownloadNormal(tle);
            }
            else if (tle.source.Type == TrackType.Album)
            {
                await TracksDownloadAlbum(tle);
            }
            else if (tle.source.Type == TrackType.Aggregate)
            {
                await TracksDownloadNormal(tle);
            }
        }

        if (!Config.DoNotDownload && (trackLists.lists.Count > 0 || trackLists.Flattened(false, false).Skip(1).Any()))
        {
            PrintComplete();
        }
    }


    static async Task PrintResults(TrackListEntry tle, List<Track> existing, List<Track> notFound)
    {
        await InitClientAndUpdateIfNeeded();

        if (tle.source.Type == TrackType.Normal)
        {
            await SearchAndPrintResults(tle.list[0]);
        }
        else if (tle.source.Type == TrackType.Aggregate)
        {
            Console.WriteLine(new string('-', 60));
            Console.WriteLine($"Results for aggregate {tle.source.ToString(true)}:");
            PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type);
        }
        else if (tle.source.Type == TrackType.Album)
        {
            Console.WriteLine(new string('-', 60));

            if (!Config.printOption.HasFlag(PrintOption.Full))
                Console.WriteLine($"Result 1 of {tle.list.Count} for album {tle.source.ToString(true)}:");
            else
                Console.WriteLine($"Results ({tle.list.Count}) for album {tle.source.ToString(true)}:");

            if (tle.list.Count > 0 && tle.list[0].Count > 0)
            {
                if (!Config.noBrowseFolder)
                    Console.WriteLine("[Skipping full folder retrieval]");

                foreach (var ls in tle.list)
                {
                    PrintAlbum(ls);

                    if (!Config.printOption.HasFlag(PrintOption.Full))
                        break;
                }
            }
            else
            {
                Console.WriteLine("No results.");
            }
        }
    }


    static void PrintComplete()
    {
        var ls = trackLists.Flattened(true, true);
        int successes = 0, fails = 0;
        foreach (var x in ls)
        {
            if (x.State == TrackState.Downloaded)
                successes++;
            else if (x.State == TrackState.Failed)
                fails++;
        }
        if (successes + fails > 1)
            Console.WriteLine($"\nCompleted: {successes} succeeded, {fails} failed.");
    }


    static void PrintTracksTbd(List<Track> toBeDownloaded, List<Track> existing, List<Track> notFound, TrackType type)
    {
        if (type == TrackType.Normal && !Config.PrintTracks && toBeDownloaded.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool full = Config.printOption.HasFlag(PrintOption.Full);

        if (type == TrackType.Normal || skippedTracks.Length > 0)
            Console.WriteLine($"Downloading {toBeDownloaded.Count(x => !x.IsNotAudio)} tracks{skippedTracks}");

        if (toBeDownloaded.Count > 0)
        {
            bool showAll = type != TrackType.Normal || Config.PrintTracks || Config.PrintResults;
            PrintTracks(toBeDownloaded, showAll ? int.MaxValue : 10, full, infoFirst: Config.PrintTracks);

            if (full && (existing.Count > 0 || notFound.Count > 0))
                Console.WriteLine("\n-----------------------------------------------\n");
        }

        if (Config.PrintTracks || Config.PrintResults)
        {
            if (existing.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks already exist:");
                PrintTracks(existing, fullInfo: full, infoFirst: Config.PrintTracks);
            }
            if (notFound.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks were not found during a prior run:");
                PrintTracks(notFound, fullInfo: full, infoFirst: Config.PrintTracks);
            }
        }
    }


    static void PrintAlbum(List<Track> albumTracks, bool retrieveAll = false)
    {
        if (albumTracks.Count == 0 && albumTracks[0].Downloads.Count == 0)
            return;

        var response = albumTracks[0].Downloads[0].Item1;
        string userInfo = $"{response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)";
        var (parents, props) = FolderInfo(albumTracks.SelectMany(x => x.Downloads.Select(d => d.Item2)));

        Console.WriteLine();
        WriteLine($"User  : {userInfo}\nFolder: {parents}\nProps : {props}", ConsoleColor.White);
        PrintTracks(albumTracks.Where(t => t.State == TrackState.Initial).ToList(), pathsOnly: true, showAncestors: true, showUser: false);
        Console.WriteLine();
    }


    static List<Track> DoSkipExisting(List<Track> tracks)
    {
        var existing = new List<Track>();
        foreach (var track in tracks)
        {
            if (SetExisting(track))
            {
                existing.Add(track);
            }
        }
        return existing;
    }


    static bool SetExisting(Track track)
    {
        string? path = null;

        if (outputExistingChecker != null)
        {
            if (!outputExistingChecker.IndexIsBuilt)
                outputExistingChecker.BuildIndex();

            outputExistingChecker.TrackExists(track, out path);
        }

        if (path == null && musicDirExistingChecker != null)
        {
            if (!musicDirExistingChecker.IndexIsBuilt)
            {
                Console.WriteLine($"Building music directory index..");
                musicDirExistingChecker.BuildIndex();
            }

            musicDirExistingChecker.TrackExists(track, out path);
        }

        if (path != null)
        {
            track.State = TrackState.AlreadyExists;
            track.DownloadPath = path;
        }

        return path != null;
    }


    static List<Track> DoSkipNotFound(List<Track> tracks)
    {
        var notFound = new List<Track>();
        foreach (var track in tracks)
        {
            if (SetNotFoundLastTime(track))
            {
                notFound.Add(track);
            }
        }
        return notFound;
    }


    static bool SetNotFoundLastTime(Track track)
    {
        if (m3uEditor.TryGetPreviousRunResult(track, out var prevTrack))
        {
            if (prevTrack.FailureReason == FailureReason.NoSuitableFileFound || prevTrack.State == TrackState.NotFoundLastTime)
            {
                track.State = TrackState.NotFoundLastTime;
                return true;
            }
        }
        return false;
    }


    static async Task TracksDownloadNormal(TrackListEntry tle)
    {
        var tracks = tle.list[0];

        var semaphore = new SemaphoreSlim(Config.concurrentProcesses);

        var copy = new List<Track>(tracks);
        var downloadTasks = copy.Select(async (track, index) =>
        {
            if (track.State == TrackState.AlreadyExists || (track.State == TrackState.NotFoundLastTime && Config.skipNotFound))
                return;

            await semaphore.WaitAsync();

            int tries = Config.unknownErrorRetries;
            string savedFilePath = "";

            while (tries > 0)
            {
                await WaitForLogin();

                try
                {
                    WriteLine($"Search and download {track}", debugOnly: true);
                    savedFilePath = await SearchAndDownload(track);
                }
                catch (Exception ex)
                {
                    WriteLine($"Exception thrown: {ex}", debugOnly: true);
                    if (!IsConnectedAndLoggedIn())
                    {
                        continue;
                    }
                    else if (ex is SearchAndDownloadException sdEx)
                    {
                        lock (trackLists)
                        {
                            tracks[index].State = TrackState.Failed;
                            tracks[index].FailureReason = sdEx.reason;
                        }
                    }
                    else
                    {
                        WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                        tries--;
                        continue;
                    }
                }

                break;
            }

            if (savedFilePath.Length > 0)
            {
                lock (trackLists) 
                {
                    tracks[index].State = TrackState.Downloaded;
                    tracks[index].DownloadPath = savedFilePath; 
                }

                if (Config.removeTracksFromSource)
                {
                    try
                    {
                        await extractor.RemoveTrackFromSource(track);
                    }
                    catch (Exception ex)
                    {
                        WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                    }
                }
            }

            m3uEditor.Update();

            if (Config.onComplete.Length > 0)
            {
                OnComplete(Config.onComplete, tracks[index]);
            }

            semaphore.Release();
        });

        await Task.WhenAll(downloadTasks);
    }


    static async Task TracksDownloadAlbum(TrackListEntry tle) // this is shit
    {
        var list = tle.list;
        var dlFiles = new ConcurrentDictionary<string, bool>();
        var dlAdditionalImages = new ConcurrentDictionary<string, bool>();
        var retrievedFolders = new HashSet<string>();
        var tracks = new List<Track>();
        bool downloadingImages = false;
        bool albumDlFailed = false;
        string savedOutputFolder = Config.outputFolder;

        var curAlbumArtOption = Config.albumArtOption == AlbumArtOption.MostLargest ? AlbumArtOption.Most : Config.albumArtOption;

        void prepareImageDownload(AlbumArtOption option)
        {
            var albumArtList = list
                //.Where(tracks => tracks)
                .Select(tracks => tracks.Where(t => Utils.IsImageFile(t.Downloads[0].Item2.Filename)))
                .Where(tracks => tracks.Any());

            if (option == AlbumArtOption.Largest)
            {
                list = albumArtList
                    .OrderByDescending(tracks => tracks.Select(t => t.Downloads[0].Item2.Size).Max() / 1024 / 100)
                    .ThenByDescending(tracks => tracks.First().Downloads[0].Item1.UploadSpeed / 1024 / 300)
                    .ThenByDescending(tracks => tracks.Select(t => t.Downloads[0].Item2.Size).Sum() / 1024 / 100)
                    .Select(x => x.ToList()).ToList();
            }
            else if (option == AlbumArtOption.Most)
            {
                list = albumArtList
                    .OrderByDescending(tracks => tracks.Count())
                    .ThenByDescending(tracks => tracks.First().Downloads[0].Item1.UploadSpeed / 1024 / 300)
                    .ThenByDescending(tracks => tracks.Select(t => t.Downloads[0].Item2.Size).Sum() / 1024 / 100)
                    .Select(x => x.ToList()).ToList();
            }
        }

        bool needImageDownload(AlbumArtOption option)
        {
            bool need = true;

            if (option == AlbumArtOption.Most)
            {
                need = dlFiles.Keys.Count(x => Utils.IsImageFile(x) && File.Exists(x)) < list[0].Count;
            }
            else if (option == AlbumArtOption.Largest)
            {
                long curMax = dlFiles.Keys.Where(x => Utils.IsImageFile(x) && File.Exists(x)).Max(x => new FileInfo(x).Length);
                need = curMax < list[0].Max(t => t.Downloads[0].Item2.Size) - 1024 * 50;
            }

            return need;
        }

        if (Config.albumArtOnly)
        {
            prepareImageDownload(curAlbumArtOption);
            downloadingImages = true;
        }

        int idx = -1;
        while (list.Count > 0)
        {
            idx++;

            mainLoopCts = new CancellationTokenSource();
            albumDlFailed = false;

            if (Config.interactiveMode)
                tracks = await InteractiveModeAlbum(list, !downloadingImages, retrievedFolders);
            else
                tracks = list[0];

            soulseekFolderPathPrefix = GetCommonPathPrefix(tracks);

            if (tle.placeInSubdir && Config.nameFormat.Length == 0 && (idx == 0 || !downloadingImages))
            {
                string name = tle.useRemoteDirname ? Utils.GetBaseNameSlsk(soulseekFolderPathPrefix) : tle.source.ToString(true);
                Config.outputFolder = Path.Join(savedOutputFolder, name);
            }

            if (!downloadingImages)
            {
                if (!Config.noBrowseFolder && !Config.interactiveMode && !retrievedFolders.Contains(soulseekFolderPathPrefix))
                {
                    Console.WriteLine("Getting all files in folder...");
                    var response = tracks[0].Downloads[0].Item1;
                    await CompleteFolder(tracks, response, soulseekFolderPathPrefix);
                    retrievedFolders.Add(soulseekFolderPathPrefix);
                }
            }

            if (!Config.interactiveMode)
                PrintAlbum(tracks);

            if (!downloadingImages)
            {
                if (tracks.All(t => t.State != TrackState.Initial || (!Config.interactiveMode && t.IsNotAudio)))
                    goto imgDl;
                if (list.Count <= 1 && tracks.All(t => t.State != TrackState.Initial))
                    goto imgDl;
            }

            var semaphore = new SemaphoreSlim(Config.concurrentProcesses);
            var copy = new List<Track>(tracks);

            try
            {
                var downloadTasks = copy.Select(async (track, index) =>
                {
                    if (track.State != TrackState.Initial)
                        return;

                    await semaphore.WaitAsync(mainLoopCts.Token);

                    int tries = Config.unknownErrorRetries;
                    string savedFilePath = "";

                    while (tries > 0)
                    {
                        await WaitForLogin();
                        mainLoopCts.Token.ThrowIfCancellationRequested();

                        try
                        {
                            savedFilePath = await SearchAndDownload(track);
                        }
                        catch (Exception ex)
                        {
                            if (!IsConnectedAndLoggedIn())
                            {
                                continue;
                            }
                            else if (ex is SearchAndDownloadException sdEx)
                            {
                                lock (trackLists)
                                {
                                    tracks[index].State = TrackState.Failed;
                                    tracks[index].FailureReason = sdEx.reason;
                                }

                                if (!Config.albumIgnoreFails)
                                {
                                    mainLoopCts.Cancel();
                                    foreach (var (key, dl) in downloads)
                                    {
                                        lock (dl)
                                        {
                                            dl.cts.Cancel();
                                            if (File.Exists(dl.savePath)) File.Delete(dl.savePath);
                                            downloads.TryRemove(key, out _);
                                        }
                                    }
                                    throw new OperationCanceledException();
                                }
                            }
                            else
                            {
                                WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                                tries--;
                                continue;
                            }
                        }

                        break;
                    }

                    if (savedFilePath.Length > 0)
                    {
                        dlFiles.TryAdd(savedFilePath, true);

                        lock (trackLists)
                        {
                            tracks[index].State = TrackState.Downloaded;
                            tracks[index].DownloadPath = savedFilePath;
                            if (downloadingImages)
                            {
                                dlAdditionalImages.TryAdd(savedFilePath, true);
                            }
                        }
                    }

                    if (Config.onComplete.Length > 0)
                    {
                        OnComplete(Config.onComplete, tracks[index]);
                    }

                    semaphore.Release();
                });

                await Task.WhenAll(downloadTasks);
            }
            catch (OperationCanceledException)
            {
                if (!Config.albumIgnoreFails)
                {
                    if (!downloadingImages)
                        albumDlFailed = true;
                    var setToClear = downloadingImages ? dlAdditionalImages : dlFiles;
                    foreach (var path in setToClear.Keys)
                        if (File.Exists(path)) File.Delete(path);
                    setToClear.Clear();
                    list.RemoveAt(0);
                    continue;
                }
            }

        imgDl:
            bool needDownloadAgain = Config.albumArtOption == AlbumArtOption.MostLargest && curAlbumArtOption == AlbumArtOption.Most;

            if ((!downloadingImages || needDownloadAgain) && !albumDlFailed && Config.albumArtOption != AlbumArtOption.Default)
            {
                if (curAlbumArtOption == AlbumArtOption.Most && downloadingImages && Config.albumArtOption == AlbumArtOption.MostLargest)
                {
                    curAlbumArtOption = AlbumArtOption.Largest;
                }

                prepareImageDownload(curAlbumArtOption);

                if (Config.interactiveMode || needImageDownload(curAlbumArtOption))
                {
                    downloadingImages = true;
                    continue;
                }
                else if (Config.albumArtOption == AlbumArtOption.MostLargest && curAlbumArtOption == AlbumArtOption.Most)
                {
                    curAlbumArtOption = AlbumArtOption.Largest;
                    prepareImageDownload(curAlbumArtOption);
                    if (Config.interactiveMode || needImageDownload(curAlbumArtOption))
                    {
                        downloadingImages = true;
                        continue;
                    }
                }
            }

            break;
        }

        bool success = tracks.All(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists);

        ApplyNamingFormatsNonAudio(tracks);

        if (!Config.albumArtOnly && success)
        {
            tle.source.State = TrackState.Downloaded;
            tle.source.DownloadPath = Utils.GreatestCommonPath(tracks.Where(x => x.DownloadPath.Length > 0).Select(x => x.DownloadPath), Path.DirectorySeparatorChar);
        }

        m3uEditor.Update();
        soulseekFolderPathPrefix = "";
        Config.outputFolder = savedOutputFolder;
    }


    static string GetCommonPathPrefix(List<Track> tracks)
    {
        if (tracks.Count == 1)
            return Utils.GetDirectoryNameSlsk(tracks.First().Downloads[0].Item2.Filename);
        else
            return Utils.GreatestCommonPath(tracks.SelectMany(x => x.Downloads.Select(y => y.Item2.Filename)), dirsep: '\\');
    }


    static async Task<List<Track>> InteractiveModeAlbum(List<List<Track>> list, bool retrieveFolder, HashSet<string> retrievedFolders)
    {
        int aidx = 0;
        static string interactiveModeLoop()
        {
            string userInput = "";
            while (true)
            {
                var key = Console.ReadKey(false);
                if (key.Key == ConsoleKey.DownArrow)
                    return "n";
                else if (key.Key == ConsoleKey.UpArrow)
                    return "p";
                else if (key.Key == ConsoleKey.Escape)
                    return "c";
                else if (key.Key == ConsoleKey.Enter)
                    return userInput;
                else
                    userInput += key.KeyChar;
            }
        }
        Console.WriteLine($"\nPrev [Up/p] / Next [Down/n] / Accept [Enter] / Accept & Exit Interactive Mode [q] / Cancel [Esc/c]");
        while (true)
        {
            Console.WriteLine();
            var tracks = list[aidx];
            var response = tracks[0].Downloads[0].Item1;

            var folder = GetCommonPathPrefix(tracks);
            if (retrieveFolder && !Config.noBrowseFolder && !retrievedFolders.Contains(folder))
            {
                Console.WriteLine("Getting all files in folder...");
                await CompleteFolder(tracks, response, folder);
                retrievedFolders.Add(folder);
            }

            string userInfo = $"{response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)";
            var (parents, props) = FolderInfo(tracks.SelectMany(x => x.Downloads.Select(d => d.Item2)));

            WriteLine($"[{aidx + 1} / {list.Count}]", ConsoleColor.DarkGray);
            WriteLine($"User  : {userInfo}\nFolder: {parents}\nProps : {props}", ConsoleColor.White);
            PrintTracks(tracks, pathsOnly: true, showAncestors: true, showUser: false);

            string userInput = interactiveModeLoop();
            switch (userInput)
            {
                case "p":
                    aidx = (aidx + list.Count - 1) % list.Count;
                    break;
                case "n":
                    aidx = (aidx + 1) % list.Count;
                    break;
                case "c":
                    return new List<Track>();
                case "q":
                    Config.interactiveMode = false;
                    list.RemoveAt(aidx);
                    return tracks;
                case "":
                    return tracks;
            }
        }
    }


    static (string parents, string props) FolderInfo(IEnumerable<SlFile> files)
    {
        string res = "";
        int totalLengthInSeconds = files.Sum(f => f.Length ?? 0);
        var sampleRates = files.Where(f => f.SampleRate.HasValue).Select(f => f.SampleRate.Value).OrderBy(r => r).ToList();

        int? modeSampleRate = sampleRates.GroupBy(rate => rate).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();

        var bitRates = files.Where(f => f.BitRate.HasValue).Select(f => f.BitRate.Value).ToList();
        double? meanBitrate = bitRates.Count > 0 ? (double?)bitRates.Average() : null;

        double totalFileSizeInMB = files.Sum(f => f.Size) / (1024.0 * 1024.0);

        TimeSpan totalTimeSpan = TimeSpan.FromSeconds(totalLengthInSeconds);
        string totalLengthFormatted;
        if (totalTimeSpan.TotalHours >= 1)
            totalLengthFormatted = string.Format("{0}:{1:D2}:{2:D2}", (int)totalTimeSpan.TotalHours, totalTimeSpan.Minutes, totalTimeSpan.Seconds);
        else
            totalLengthFormatted = string.Format("{0:D2}:{1:D2}", totalTimeSpan.Minutes, totalTimeSpan.Seconds);

        var mostCommonExtension = files.GroupBy(f => Utils.GetExtensionSlsk(f.Filename))
            .OrderByDescending(g => Utils.IsMusicExtension(g.Key)).ThenByDescending(g => g.Count()).First().Key;

        res = $"[{mostCommonExtension.ToUpper()} / {totalLengthFormatted}";

        if (modeSampleRate.HasValue)
            res += $" / {(modeSampleRate.Value / 1000.0).Normalize()} kHz";

        if (meanBitrate.HasValue)
            res += $" / {(int)meanBitrate.Value} kbps";

        res += $" / {totalFileSizeInMB:F2} MB]";

        string gcp;

        if (files.Skip(1).Any())
            gcp = Utils.GreatestCommonPath(files.Select(x => x.Filename), '\\').TrimEnd('\\');
        else
            gcp = Utils.GetDirectoryNameSlsk(files.First().Filename);

        var discPattern = new Regex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$");
        int lastIndex = gcp.LastIndexOf('\\');
        if (lastIndex != -1)
        {
            int secondLastIndex = gcp.LastIndexOf('\\', lastIndex - 1);
            gcp = secondLastIndex == -1 ? gcp[(lastIndex + 1)..] : gcp[(secondLastIndex + 1)..];
        }

        return (gcp, res);
    }


    static async Task Login(bool random = false, int tries = 3)
    {
        string user = Config.username, pass = Config.password;
        if (random)
        {
            var r = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            pass = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
        }

        WriteLine($"Login {user}");

        while (true)
        {
            try
            {
                WriteLine($"Connecting {user}", debugOnly: true);
                await client.ConnectAsync(user, pass);
                if (!Config.noModifyShareCount)
                {
                    WriteLine($"Setting share count", debugOnly: true);
                    await client.SetSharedCountsAsync(20, 100);
                }
                break;
            }
            catch (Exception e)
            {
                WriteLine($"Exception while logging in: {e}", debugOnly: true);
                if (!(e is Soulseek.AddressException || e is System.TimeoutException) && --tries == 0)
                    throw;
            }
            await Task.Delay(500);
            WriteLine($"Retry login {user}", debugOnly: true);
        }

        WriteLine($"Logged in {user}", debugOnly: true);
    }


    static async Task Update()
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
                                searches.TryRemove(key, out _); // reminder: removing from a dict in a foreach is allowed in newer .net versions
                        }

                        foreach (var (key, val) in downloads)
                        {
                            if (val != null)
                            {
                                lock (val)
                                {
                                    if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > Config.maxStaleTime)
                                    {
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
                        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn | SoulseekClientStates.LoggingIn | SoulseekClientStates.Connecting))
                        {
                            WriteLine($"\nDisconnected, logging in\n", ConsoleColor.DarkYellow, true);
                            try { await Login(Config.useRandomLogin); }
                            catch (Exception ex)
                            {
                                string banMsg = Config.useRandomLogin ? "" : " (possibly a 30-minute ban caused by frequent searches)";
                                WriteLine($"{ex.Message}{banMsg}", ConsoleColor.DarkYellow, true);
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
                    WriteLine($"\n{ex.Message}\n", ConsoleColor.DarkYellow, true);
                }
            }

            await Task.Delay(Config.updateDelay);
        }
    }


    static void OnComplete(string onComplete, Track track)
    {
        if (onComplete.Length == 0)
            return;
        else if (onComplete.Length > 2 && onComplete[0].IsDigit() && onComplete[1] == ':')
        {
            if ((int)track.State != int.Parse(onComplete[0].ToString()))
                return;
            onComplete = onComplete[2..];
        }

        Process process = new Process();
        ProcessStartInfo startInfo = new ProcessStartInfo();

        onComplete = onComplete.Replace("{title}", track.Title)
                           .Replace("{artist}", track.Artist)
                           .Replace("{album}", track.Album)
                           .Replace("{uri}", track.URI)
                           .Replace("{length}", track.Length.ToString())
                           .Replace("{artist-maybe-wrong}", track.ArtistMaybeWrong.ToString())
                           .Replace("{type}", track.Type.ToString())
                           .Replace("{is-not-audio}", track.IsNotAudio.ToString())
                           .Replace("{failure-reason}", track.FailureReason.ToString())
                           .Replace("{path}", track.DownloadPath)
                           .Replace("{state}", track.State.ToString())
                           .Replace("{extractor}", Config.inputType.ToString())
                           .Trim();

        if (onComplete[0] == '"')
        {
            int e = onComplete.IndexOf('"', 1);
            if (e > 1)
            {
                startInfo.FileName = onComplete[1..e];
                startInfo.Arguments = onComplete.Substring(e + 1, onComplete.Length - e - 1);
            }
            else
            {
                startInfo.FileName = onComplete.Trim('"');
            }
        }
        else
        {
            string[] parts = onComplete.Split(' ', 2);
            startInfo.FileName = parts[0];
            startInfo.Arguments = parts.Length > 1 ? parts[1] : "";
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        process.StartInfo = startInfo;

        WriteLine($"on-complete: FileName={startInfo.FileName}, Arguments={startInfo.Arguments}", debugOnly: true);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
    }


    static string GetSavePath(string sourceFname)
    {
        return $"{GetSavePathNoExt(sourceFname)}{Path.GetExtension(sourceFname)}";
    }

    static string GetSavePathNoExt(string sourceFname)
    {
        string outTo = Config.outputFolder;
        if (!string.IsNullOrEmpty(soulseekFolderPathPrefix))
        {
            string add = sourceFname.Replace(soulseekFolderPathPrefix, "").Replace(Utils.GetFileNameSlsk(sourceFname), "").Trim('\\').Trim();
            if (add.Length > 0) outTo = Path.Join(Config.outputFolder, add.Replace('\\', Path.DirectorySeparatorChar));
        }
        return Path.Combine(outTo, $"{GetSaveName(sourceFname)}");
    }

    static string GetSaveName(string sourceFname)
    {
        string name = Utils.GetFileNameWithoutExtSlsk(sourceFname);
        return name.ReplaceInvalidChars(Config.invalidReplaceStr);
    }

    static void ApplyNamingFormatsNonAudio(List<Track> tracks)
    {
        if (!Config.nameFormat.Replace('\\', '/').Contains('/'))
            return;

        var audioFilePaths = tracks.Where(t => t.DownloadPath.Length > 0 && !t.IsNotAudio).Select(t => t.DownloadPath);

        string outputFolder = Utils.GreatestCommonPath(audioFilePaths, Path.DirectorySeparatorChar);

        foreach (var track in tracks)
        {
            if (!track.IsNotAudio || track.State != TrackState.Downloaded)
                continue;

            string newFilePath = Path.Join(outputFolder, Path.GetFileName(track.DownloadPath));

            if (track.DownloadPath != newFilePath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFilePath));
                Utils.Move(track.DownloadPath, newFilePath);

                string prevParent = Path.GetDirectoryName(track.DownloadPath);

                if (prevParent != Config.outputFolder && Utils.GetRecursiveFileCount(prevParent) == 0)
                    Directory.Delete(prevParent, true);

                track.DownloadPath = newFilePath;
            }
        }
    }

    static string ApplyNamingFormat(string filepath, Track track)
    {
        if (Config.nameFormat.Length == 0 || !Utils.IsMusicFile(filepath))
            return filepath;

        string dir = Path.GetDirectoryName(filepath) ?? "";
        string add = dir.Length > 0 ? Path.GetRelativePath(Config.outputFolder, dir) : "";
        string newFilePath = NamingFormat(filepath, Config.nameFormat, track);

        if (filepath != newFilePath)
        {
            dir = Path.GetDirectoryName(newFilePath) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);

            try
            {
                Utils.Move(filepath, newFilePath);
            }
            catch (Exception ex)
            {
                WriteLine($"\nFailed to move: {ex.Message}\n", ConsoleColor.DarkYellow, true);
                return filepath;
            }

            if (add.Length > 0 && add != "." && Utils.GetRecursiveFileCount(Path.Join(Config.outputFolder, add)) == 0)
                try { Directory.Delete(Path.Join(Config.outputFolder, add), true); } catch { }
        }

        return newFilePath;
    }

    static string NamingFormat(string filepath, string format, Track track)
    {
        string newName = format;
        TagLib.File? file = null;

        try { file = TagLib.File.Create(filepath); }
        catch { }

        Regex regex = new Regex(@"(\{(?:\{??[^\{]*?\}))");
        MatchCollection matches = regex.Matches(newName);

        while (matches.Count > 0)
        {
            foreach (Match match in matches.Cast<Match>())
            {
                string inner = match.Groups[1].Value;
                inner = inner[1..^1];

                var options = inner.Split('|');
                string chosenOpt = "";

                foreach (var opt in options)
                {
                    string[] parts = Regex.Split(opt, @"\([^\)]*\)");
                    string[] result = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
                    if (result.All(x => GetVarValue(x, file, filepath, track).Length > 0))
                    {
                        chosenOpt = opt;
                        break;
                    }
                }

                chosenOpt = Regex.Replace(chosenOpt, @"\([^()]*\)|[^()]+", match =>
                {
                    if (match.Value.StartsWith("(") && match.Value.EndsWith(")"))
                        return match.Value[1..^1].ReplaceInvalidChars(Config.invalidReplaceStr, removeSlash: false);
                    else
                        return GetVarValue(match.Value, file, filepath, track).ReplaceInvalidChars(Config.invalidReplaceStr);
                });

                string old = match.Groups[1].Value;
                old = old.StartsWith("{{") ? old[1..] : old;
                newName = newName.Replace(old, chosenOpt);
            }

            matches = regex.Matches(newName);
        }

        if (newName != format)
        {
            string directory = Path.GetDirectoryName(filepath) ?? "";
            string extension = Path.GetExtension(filepath);
            char dirsep = Path.DirectorySeparatorChar;
            newName = newName.Replace('/', dirsep);
            var x = newName.Split(dirsep, StringSplitOptions.RemoveEmptyEntries);
            newName = string.Join(dirsep, x.Select(x => x.ReplaceInvalidChars(Config.invalidReplaceStr).Trim(' ', '.')));
            string newFilePath = Path.Combine(directory, newName + extension);
            return newFilePath;
        }

        return filepath;
    }

    static string GetVarValue(string x, TagLib.File? file, string filepath, Track track)
    {
        switch (x)
        {
            case "artist":
                return file?.Tag.FirstPerformer ?? "";
            case "artists":
                return file != null ? string.Join(" & ", file.Tag.Performers) : "";
            case "albumartist":
                return file?.Tag.FirstAlbumArtist ?? "";
            case "albumartists":
                return file != null ? string.Join(" & ", file.Tag.AlbumArtists) : "";
            case "title":
                return file?.Tag.Title ?? "";
            case "album":
                return file?.Tag.Album ?? "";
            case "sartist":
            case "sartists":
                return track.Artist;
            case "stitle":
                return track.Title;
            case "salbum":
                return track.Album;
            case "year":
                return file?.Tag.Year.ToString() ?? "";
            case "track":
                return file?.Tag.Track.ToString("D2") ?? "";
            case "disc":
                return file?.Tag.Disc.ToString() ?? "";
            case "filename":
                return Path.GetFileNameWithoutExtension(filepath);
            case "foldername":
                return track.FirstDownload != null ? 
                    Utils.GetBaseNameSlsk(Utils.GetDirectoryNameSlsk(track.FirstDownload.Filename)) : Config.defaultFolderName;
            case "default-foldername":
                return Config.defaultFolderName;
            case "extractor":
                return Config.inputType.ToString();
            default:
                return "";
        }
    }

    static string DisplayString(Track t, Soulseek.File? file = null, SearchResponse? response = null, FileConditions? nec = null,
        FileConditions? pref = null, bool fullpath = false, string customPath = "", bool infoFirst = false, bool showUser = true, bool showSpeed = false)
    {
        if (file == null)
            return t.ToString();

        string sampleRate = file.SampleRate.HasValue ? $"{(file.SampleRate.Value / 1000.0).Normalize()}kHz" : "";
        string bitRate = file.BitRate.HasValue ? $"{file.BitRate}kbps" : "";
        string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string user = showUser && response?.Username != null ? response.Username + "\\" : "";
        string speed = showSpeed && response?.Username != null ? $"({response.UploadSpeed / 1024.0 / 1024.0:F2}MB/s) " : "";
        string fname = fullpath ? file.Filename : (showUser ? "..\\" : "") + (customPath.Length == 0 ? Utils.GetFileNameSlsk(file.Filename) : customPath);
        string length = Utils.IsMusicFile(file.Filename) ? (file.Length ?? -1).ToString() + "s" : "";
        string displayText;
        if (!infoFirst)
        {
            string info = string.Join('/', new string[] { length, sampleRate + bitRate, fileSize }.Where(value => value.Length > 0));
            displayText = $"{speed}{user}{fname} [{info}]";
        }
        else
        {
            string info = string.Join('/', new string[] { length.PadRight(4), (sampleRate + bitRate).PadRight(8), fileSize.PadLeft(6) });
            displayText = $"[{info}] {speed}{user}{fname}";
        }

        string necStr = nec != null ? $"nec:{nec.GetNotSatisfiedName(file, t, response)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, t, response)}" : "";
        string cond = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }

    static void PrintTracks(List<Track> tracks, int number = int.MaxValue, bool fullInfo = false, bool pathsOnly = false, bool showAncestors = false, bool infoFirst = false, bool showUser = true)
    {
        number = Math.Min(tracks.Count, number);

        string ancestor = "";

        if (showAncestors)
            ancestor = Utils.GreatestCommonPath(tracks.SelectMany(x => x.Downloads.Select(y => y.Item2.Filename)), Path.DirectorySeparatorChar);

        if (pathsOnly)
        {
            for (int i = 0; i < number; i++)
            {
                foreach (var x in tracks[i].Downloads)
                {
                    if (ancestor.Length == 0)
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, infoFirst: infoFirst, showUser: showUser));
                    else
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, customPath: x.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst, showUser: showUser));
                }
            }
        }
        else if (!fullInfo)
        {
            for (int i = 0; i < number; i++)
            {
                Console.WriteLine($"  {tracks[i]}");
            }
        }
        else
        {
            for (int i = 0; i < number; i++)
            {
                if (!tracks[i].IsNotAudio)
                {
                    Console.WriteLine($"  Artist:             {tracks[i].Artist}");
                    if (!string.IsNullOrEmpty(tracks[i].Title) || tracks[i].Type == TrackType.Normal)
                        Console.WriteLine($"  Title:              {tracks[i].Title}");
                    if (!string.IsNullOrEmpty(tracks[i].Album) || tracks[i].Type == TrackType.Album)
                        Console.WriteLine($"  Album:              {tracks[i].Album}");
                    if (tracks[i].Length > -1 || tracks[i].Type == TrackType.Normal)
                        Console.WriteLine($"  Length:             {tracks[i].Length}s");
                    if (!string.IsNullOrEmpty(tracks[i].DownloadPath))
                        Console.WriteLine($"  Local path:         {tracks[i].DownloadPath}");
                    if (!string.IsNullOrEmpty(tracks[i].URI))
                        Console.WriteLine($"  URL/ID:             {tracks[i].URI}");
                    if (tracks[i].Type != TrackType.Normal)
                        Console.WriteLine($"  Type:               {tracks[i].Type}");
                    if (!string.IsNullOrEmpty(tracks[i].Other))
                        Console.WriteLine($"  Other:              {tracks[i].Other}");
                    if (tracks[i].ArtistMaybeWrong)
                        Console.WriteLine($"  Artist maybe wrong: {tracks[i].ArtistMaybeWrong}");
                    if (tracks[i].Downloads != null)
                    {
                        Console.WriteLine($"  Shares:             {tracks[i].Downloads.Count}");
                        foreach (var x in tracks[i].Downloads)
                        {
                            if (ancestor.Length == 0)
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, infoFirst: infoFirst, showUser: showUser));
                            else
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, customPath: x.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst, showUser: showUser));
                        }
                        if (tracks[i].Downloads?.Count > 0) Console.WriteLine();
                    }
                }
                else
                {
                    Console.WriteLine($"  File:               {Utils.GetFileNameSlsk(tracks[i].Downloads[0].Item2.Filename)}");
                    Console.WriteLine($"  Shares:             {tracks[i].Downloads.Count}");
                    foreach (var x in tracks[i].Downloads)
                    {
                        if (ancestor.Length == 0)
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, infoFirst: infoFirst, showUser: showUser));
                        else
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, customPath: x.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst, showUser: showUser));
                    }
                    Console.WriteLine();
                }
                Console.WriteLine();
            }
        }

        if (number < tracks.Count)
            Console.WriteLine($"  ... (etc)");
    }

    static void RefreshOrPrint(ProgressBar? progress, int current, string item, bool print = false, bool refreshIfOffscreen = false)
    {
        if (progress != null && !Console.IsOutputRedirected && (refreshIfOffscreen || progress.Y >= Console.WindowTop))
        {
            try { progress.Refresh(current, item); }
            catch { }
        }
        else if ((Config.displayMode == DisplayMode.Simple || Console.IsOutputRedirected) && print)
            Console.WriteLine(item);
    }

    public static void WriteLine(string value, ConsoleColor color = ConsoleColor.Gray, bool safe = false, bool debugOnly = false)
    {
        if (debugOnly && !Config.debugInfo)
            return;
        if (!safe)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ResetColor();
        }
        else
        {
            skipUpdate = true;
            lock (consoleLock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(value);
                Console.ResetColor();
            }

            skipUpdate = false;
        }
    }

    public static ProgressBar? GetProgressBar(DisplayMode style)
    {
        lock (consoleLock)
        {
            ProgressBar? progress = null;
            if (style == DisplayMode.Double)
                progress = new ProgressBar(PbStyle.DoubleLine, 100, Console.WindowWidth - 40, character: '―');
            else if (style != DisplayMode.Simple)
                progress = new ProgressBar(PbStyle.SingleLine, 100, Console.WindowWidth - 10, character: ' ');
            return progress;
        }
    }

    public static async Task WaitForLogin()
    {
        while (true)
        {
            WriteLine($"Wait for login, state: {client.State}", debugOnly: true);
            if (IsConnectedAndLoggedIn())
                break;
            await Task.Delay(1000);
        }
    }

    public static bool IsConnectedAndLoggedIn()
    {
        return client != null && (client.State & (SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn)) != 0;
    }

    static readonly List<string> bannedTerms = new()
    {
        "depeche mode", "beatles", "prince revolutions", "michael jackson", "coexist", "bob dylan", "enter shikari",
        "village people", "lenny kravitz", "beyonce", "beyoncé", "lady gaga", "jay z", "kanye west", "rihanna",
        "adele", "kendrick lamar", "bad romance", "born this way", "weeknd", "broken hearted", "highway 61 revisited",
        "west gold digger", "west good life"
    };
}


