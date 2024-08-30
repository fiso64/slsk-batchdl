using AngleSharp.Text;
using Konsole;
using Soulseek;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;

using Data;
using Enums;
using FileSkippers;
using static Printing;

using Directory = System.IO.Directory;
using File = System.IO.File;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlResponse = Soulseek.SearchResponse;


static partial class Program
{
    public static bool skipUpdate = false;
    public static bool initialized = false;
    public static Extractors.IExtractor? extractor;
    public static FileSkipper? outputDirSkipper;
    public static FileSkipper? musicDirSkipper;
    public static SoulseekClient? client;
    public static TrackLists? trackLists;
    public static M3uEditor? m3uEditor;
    public static readonly ConcurrentDictionary<Track, SearchInfo> searches = new();
    public static readonly ConcurrentDictionary<string, DownloadWrapper> downloads = new();
    public static readonly ConcurrentDictionary<string, int> userSuccessCount = new();

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

        InitFileSkippers();

        await MainLoop();
        WriteLine("Mainloop done", debugOnly: true);
    }


    public static async Task InitClientAndUpdateIfNeeded()
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

            Search.searchSemaphore = new RateLimitedSemaphore(Config.searchesPerTime, TimeSpan.FromSeconds(Config.searchRenewTime));
        }

        bool needUpdate = needLogin;
        if (needUpdate)
        {
            var UpdateTask = Task.Run(() => Update());
            WriteLine("Update started", debugOnly: true);
        }

        initialized = true;
    }


    static void InitFileSkippers()
    {
        if (Config.skipExisting)
        {
            var cond = Config.skipExistingPrefCond ? Config.preferredCond : Config.necessaryCond;

            if (Config.musicDir.Length == 0 || !Config.outputFolder.StartsWith(Config.musicDir, StringComparison.OrdinalIgnoreCase))
                outputDirSkipper = FileSkipperRegistry.GetChecker(Config.skipMode, Config.outputFolder, cond, m3uEditor);

            if (Config.musicDir.Length > 0)
            {
                if (!Directory.Exists(Config.musicDir))
                    Console.WriteLine("Error: Music directory does not exist");
                else
                    musicDirSkipper = FileSkipperRegistry.GetChecker(Config.skipModeMusicDir, Config.musicDir, cond, m3uEditor);
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
                    PrintTracksTbd(tl, existing, notFound, tle.source.Type, summary: false);
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
                    tle.list = await Search.GetAlbumDownloads(tle.source, responseData);
                }
                else if (tle.source.Type == TrackType.Aggregate)
                {
                    tle.list.Insert(0, await Search.GetAggregateTracks(tle.source, responseData));
                }
                else if (tle.source.Type == TrackType.AlbumAggregate)
                {
                    var res = await Search.GetAggregateAlbums(tle.source, responseData);

                    foreach (var item in res)
                    {
                        var newSource = new Track(tle.source) { Type = TrackType.Album };
                        trackLists.AddEntry(new TrackListEntry(item, newSource, false, true, true, false, false));
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
                await DownloadNormal(tle);
            }
            else if (tle.source.Type == TrackType.Album)
            {
                await DownloadAlbum(tle);
            }
            else if (tle.source.Type == TrackType.Aggregate)
            {
                await DownloadNormal(tle);
            }
        }

        if (!Config.DoNotDownload && (trackLists.lists.Count > 0 || trackLists.Flattened(false, false).Skip(1).Any()))
        {
            PrintComplete(trackLists);
        }
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

        if (outputDirSkipper != null)
        {
            if (!outputDirSkipper.IndexIsBuilt)
                outputDirSkipper.BuildIndex();

            outputDirSkipper.TrackExists(track, out path);
        }

        if (path == null && musicDirSkipper != null)
        {
            if (!musicDirSkipper.IndexIsBuilt)
            {
                Console.WriteLine($"Building music directory index..");
                musicDirSkipper.BuildIndex();
            }

            musicDirSkipper.TrackExists(track, out path);
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


    static async Task DownloadNormal(TrackListEntry tle)
    {
        var tracks = tle.list[0];

        var semaphore = new SemaphoreSlim(Config.concurrentProcesses);

        var organizer = new FileManager(tle);

        var downloadTasks = tracks.Select(async (track, index) =>
        {
            await DownloadTask(tle, track, semaphore, organizer, null, false);
            m3uEditor.Update();
        });

        await Task.WhenAll(downloadTasks);
    }


    static async Task DownloadAlbum(TrackListEntry tle)
    {
        var organizer = new FileManager(tle);
        List<Track>? tracks = null;
        var retrievedFolders = new HashSet<string>();
        bool succeeded = false;
        string? soulseekDir = null;

        while (tle.list.Count > 0)
        {
            int index = 0;
            bool wasInteractive = Config.interactiveMode;

            if (Config.interactiveMode)
            {
                index = await InteractiveModeAlbum(tle.list, !Config.noBrowseFolder, retrievedFolders);
                if (index == -1) break;
            }

            tracks = tle.list[index];

            soulseekDir = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));

            organizer.SetRemoteCommonDir(soulseekDir);

            if (!Config.noBrowseFolder && !Config.interactiveMode && !retrievedFolders.Contains(soulseekDir))
            {
                Console.WriteLine("Getting all files in folder...");
                await Search.CompleteFolder(tracks, tracks[0].FirstResponse, soulseekDir);
                retrievedFolders.Add(tracks[0].FirstUsername + '\\' + soulseekDir);
            }

            if (!Config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                PrintAlbum(tracks);
            }

            var semaphore = new SemaphoreSlim(Config.concurrentProcesses);
            var cts = new CancellationTokenSource();

            try
            {
                var downloadTasks = tracks.Select(async track =>
                {
                    await DownloadTask(tle, track, semaphore, organizer, cts, cancelOnFail: !Config.albumIgnoreFails);
                });
                await Task.WhenAll(downloadTasks);

                succeeded = true;
                break;
            }
            catch (OperationCanceledException) when (!Config.albumIgnoreFails)
            {
                foreach (var track in tracks)
                {
                    if (track.State == TrackState.Downloaded && File.Exists(track.DownloadPath))
                    {
                        try { File.Delete(track.DownloadPath); } catch { }
                    }
                }
            }

            organizer.SetRemoteCommonDir(null);
            tle.list.RemoveAt(index);
        }

        if (tracks != null)
        {
            var downloadedAudio = tracks.Where(t => !t.IsNotAudio && t.State == TrackState.Downloaded && t.DownloadPath.Length > 0);

            if (downloadedAudio.Any())
            {
                tle.source.State = TrackState.Downloaded;
                tle.source.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(t => t.DownloadPath));
            }
        }

        List<Track>? additionalImages = null;
        
        if (Config.albumArtOnly || succeeded && Config.albumArtOption != AlbumArtOption.Default)
        {
            Console.WriteLine($"\nDownloading additional images:");
            additionalImages = await DownloadImages(tle.list, Config.albumArtOption, tracks);
            tracks?.AddRange(additionalImages);
        }

        if (tracks != null && tle.source.DownloadPath.Length > 0)
        {
            organizer.OrganizeAlbum(tracks, additionalImages);
        }

        m3uEditor.Update();
    }


    static async Task<List<Track>> DownloadImages(List<List<Track>> downloads, AlbumArtOption option, List<Track>? chosenAlbum)
    {
        var downloadedImages = new List<Track>();
        long mSize = 0;
        int mCount = 0;

        if (option == AlbumArtOption.Default)
            return downloadedImages;

        int[]? sortedLengths = null;

        if (chosenAlbum != null && chosenAlbum.Count(t => !t.IsNotAudio) > 0)
            sortedLengths = chosenAlbum.Where(t => !t.IsNotAudio).Select(t => t.Length).OrderBy(x => x).ToArray();

        var albumArts = downloads
            .Where(ls => chosenAlbum == null || Search.AlbumsAreSimilar(chosenAlbum, ls, sortedLengths))
            .Select(ls => ls.Where(t => Utils.IsImageFile(t.FirstDownload.Filename)))
            .Where(ls => ls.Any());

        if (!albumArts.Any())
        {
            Console.WriteLine("No images found");
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
                    .Max(t => t.FirstDownload.Size);
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
            if (list.All(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
                return false;
            else if (option == AlbumArtOption.Most)
                return mCount < list.Count;
            else if (option == AlbumArtOption.Largest)
                return mSize < list.Max(t => t.FirstDownload.Size) - 1024 * 50;
            return true;
        }

        while (albumArtLists.Count > 0)
        {
            int index = 0;
            bool wasInteractive = Config.interactiveMode;

            if (Config.interactiveMode)
            {
                index = await InteractiveModeAlbum(albumArtLists, false, null);
                if (index == -1) break;
            }

            var tracks = albumArtLists[index];
            albumArtLists.RemoveAt(index);

            if (!needImageDownload(tracks))
            {
                Console.WriteLine("Image requirements already satisfied.");
                return downloadedImages;
            }

            if (!Config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                PrintAlbum(tracks);
            }

            bool allSucceeded = true;
            var semaphore = new SemaphoreSlim(1);
            var organizer = new FileManager();

            foreach (var track in tracks)
            {
                await DownloadTask(null, track, semaphore, organizer, null, false);

                if (track.State == TrackState.Downloaded)
                    downloadedImages.Add(track);
                else
                    allSucceeded = false;
            }

            if (allSucceeded)
                break;
        }

        return downloadedImages;
    }


    static async Task DownloadTask(TrackListEntry? tle, Track track, SemaphoreSlim semaphore, FileManager organizer, CancellationTokenSource? cts, bool cancelOnFail)
    {
        if (track.State != TrackState.Initial)
            return;

        if (cts != null)
            await semaphore.WaitAsync(cts.Token);
        else
            await semaphore.WaitAsync();

        int tries = Config.unknownErrorRetries;
        string savedFilePath = "";
        SlFile? chosenFile = null;

        while (tries > 0)
        {
            await WaitForLogin();

            cts?.Token.ThrowIfCancellationRequested();

            try
            {
                (savedFilePath, chosenFile) = await Search.SearchAndDownload(track, organizer);
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
                        track.State = TrackState.Failed;
                        track.FailureReason = sdEx.reason;
                    }

                    if (cancelOnFail)
                    {
                        cts?.Cancel();
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

        if (tries == 0 && cancelOnFail)
        {
            cts?.Cancel();
            throw new OperationCanceledException();
        }

        if (savedFilePath.Length > 0)
        {
            lock (trackLists)
            {
                track.State = TrackState.Downloaded;
                track.DownloadPath = savedFilePath;
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

        if (track.State == TrackState.Downloaded && tle != null)
        {
            organizer?.OrganizeAudio(tle, track, chosenFile);
        }

        if (Config.onComplete.Length > 0)
        {
            OnComplete(Config.onComplete, track);
        }

        semaphore.Release();
    }


    static async Task<int> InteractiveModeAlbum(List<List<Track>> list, bool retrieveFolder, HashSet<string>? retrievedFolders)
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
                    return "s";
                else if (key.Key == ConsoleKey.Enter)
                    return userInput;
                else
                    userInput += key.KeyChar;
            }
        }

        WriteLine($"\nPrev [Up/p] / Next [Down/n] / Accept [Enter] / Accept & Exit Interactive [q] / Skip [Esc/s]\n", ConsoleColor.Green);

        while (true)
        {
            var tracks = list[aidx];
            var response = tracks[0].FirstResponse;
            var username = tracks[0].FirstUsername;

            WriteLine($"[{aidx + 1} / {list.Count}]", ConsoleColor.DarkGray);

            var folder = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));
            if (retrieveFolder && !retrievedFolders.Contains(username + '\\' + folder))
            {
                Console.WriteLine("Getting all files in folder...");
                await Search.CompleteFolder(tracks, response, folder);
                retrievedFolders.Add(username + '\\' + folder);
            }

            PrintAlbum(tracks);

            string userInput = interactiveModeLoop().Trim();
            switch (userInput)
            {
                case "p":
                    aidx = (aidx + list.Count - 1) % list.Count;
                    break;
                case "n":
                    aidx = (aidx + 1) % list.Count;
                    break;
                case "s":
                    return -1;
                case "q":
                    Config.interactiveMode = false;
                    return aidx;
                case "":
                    return aidx;
            }
        }
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
}


