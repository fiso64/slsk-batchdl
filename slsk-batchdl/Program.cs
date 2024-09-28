using AngleSharp.Text;
using Konsole;
using Soulseek;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Sockets;

using Data;
using Enums;
using FileSkippers;
using Extractors;
using static Printing;

using Directory = System.IO.Directory;
using File = System.IO.File;
using SlFile = Soulseek.File;

static partial class Program
{
    public static bool skipUpdate = false;
    public static bool initialized = false;
    public static IExtractor? extractor;
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

        Config.I.LoadAndParse(args);

        if (Config.I.input.Length == 0)
            throw new ArgumentException($"No input provided");

        (Config.I.inputType, extractor) = ExtractorRegistry.GetMatchingExtractor(Config.I.input, Config.I.inputType);

        WriteLine($"Using extractor: {Config.I.inputType}", debugOnly: true);
        
        trackLists = await extractor.GetTracks(Config.I.input, Config.I.maxTracks, Config.I.offset, Config.I.reverse);

        WriteLine("Got tracks", debugOnly: true);

        Config.I.PostProcessArgs();

        trackLists.UpgradeListTypes(Config.I.aggregate, Config.I.album);
        trackLists.SetListEntryOptions();

        m3uEditor = new M3uEditor(trackLists, Config.I.m3uOption);

        await MainLoop();

        WriteLine("Mainloop done", debugOnly: true);
    }


    public static async Task InitClientAndUpdateIfNeeded()
    {
        if (initialized)
            return;

        bool needLogin = !Config.I.PrintTracks;
        if (needLogin)
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
                listenPort: Config.I.listenPort
            );

            client = new SoulseekClient(clientOptions);

            if (!Config.I.useRandomLogin && (string.IsNullOrEmpty(Config.I.username) || string.IsNullOrEmpty(Config.I.password)))
                throw new ArgumentException("No soulseek username or password");

            await Login(Config.I.useRandomLogin);

            Search.searchSemaphore = new RateLimitedSemaphore(Config.I.searchesPerTime, TimeSpan.FromSeconds(Config.I.searchRenewTime));
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
        if (Config.I.skipExisting)
        {
            var cond = Config.I.skipExistingPrefCond ? Config.I.preferredCond : Config.I.necessaryCond;

            outputDirSkipper = FileSkipperRegistry.GetSkipper(Config.I.skipMode, Config.I.parentDir, cond, m3uEditor);

            if (Config.I.musicDir.Length > 0)
            {
                if (!Directory.Exists(Config.I.musicDir))
                    Console.WriteLine("Error: Music directory does not exist");
                else
                    musicDirSkipper = FileSkipperRegistry.GetSkipper(Config.I.skipModeMusicDir, Config.I.musicDir, cond, m3uEditor);
            }
        }
    }


    static void PreprocessTracks(TrackListEntry tle)
    {
        PreprocessTrack(tle.source);
        
        for (int k = 0; k < tle.list.Count; k++)
        {
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
        if (Config.I.removeFt)
        {
            track.Title = track.Title.RemoveFt();
            track.Artist = track.Artist.RemoveFt();
        }
        if (Config.I.removeBrackets)
        {
            track.Title = track.Title.RemoveSquareBrackets();
        }
        if (Config.I.regexToReplace.Title.Length + Config.I.regexToReplace.Artist.Length + Config.I.regexToReplace.Album.Length > 0)
        {
            track.Title = Regex.Replace(track.Title, Config.I.regexToReplace.Title, Config.I.regexReplaceBy.Title);
            track.Artist = Regex.Replace(track.Artist, Config.I.regexToReplace.Artist, Config.I.regexReplaceBy.Artist);
            track.Album = Regex.Replace(track.Album, Config.I.regexToReplace.Album, Config.I.regexReplaceBy.Album);
        }
        if (Config.I.artistMaybeWrong)
        {
            track.ArtistMaybeWrong = true;
        }

        track.Artist = track.Artist.Trim();
        track.Album = track.Album.Trim();
        track.Title = track.Title.Trim();
    }


    static void PrepareListEntry(TrackListEntry tle, bool isFirstEntry)
    {
        Config.I.RestoreConditions();

        bool changed = Config.UpdateProfiles(tle);

        Config.I.AddTemporaryConditions(tle.additionalConds, tle.additionalPrefConds);

        string m3uPath;

        if (Config.I.m3uFilePath.Length > 0)
            m3uPath = Config.I.m3uFilePath;
        else
            m3uPath = Path.Join(Config.I.parentDir, tle.defaultFolderName, "sldl.m3u8");

        m3uEditor.option = Config.I.m3uOption;
        m3uEditor.SetPathAndLoad(m3uPath); // does nothing if the path is the same

        if (changed || isFirstEntry)
        {
            InitFileSkippers(); // todo: only do this when a relevant config item changes
        }

        PreprocessTracks(tle);
    }


    static async Task MainLoop()
    {
        for (int i = 0; i < trackLists.lists.Count; i++)
        {
            Console.WriteLine();

            var tle = trackLists[i];

            PrepareListEntry(tle, isFirstEntry: i == 0);

            var existing = new List<Track>();
            var notFound = new List<Track>();

            if (Config.I.skipNotFound && !Config.I.PrintResults)
            {
                if (tle.sourceCanBeSkipped && SetNotFoundLastTime(tle.source))
                    notFound.Add(tle.source);

                if (tle.source.State != TrackState.NotFoundLastTime && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        notFound.AddRange(DoSkipNotFound(tracks));
                }
            }

            if (Config.I.skipExisting && !Config.I.PrintResults && tle.source.State != TrackState.NotFoundLastTime)
            {
                if (tle.sourceCanBeSkipped && SetExisting(tle.source))
                    existing.Add(tle.source);

                if (tle.source.State != TrackState.AlreadyExists && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        existing.AddRange(DoSkipExisting(tracks));
                }
            }

            if (Config.I.PrintTracks)
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

                bool foundSomething = false;
                var responseData = new ResponseData();

                if (tle.source.Type == TrackType.Album)
                {
                    tle.list = await Search.GetAlbumDownloads(tle.source, responseData);
                    foundSomething = tle.list.Count > 0 && tle.list[0].Count > 0;
                }
                else if (tle.source.Type == TrackType.Aggregate)
                {
                    tle.list.Insert(0, await Search.GetAggregateTracks(tle.source, responseData));
                    foundSomething = tle.list.Count > 0 && tle.list[0].Count > 0;
                }
                else if (tle.source.Type == TrackType.AlbumAggregate)
                {
                    var res = await Search.GetAggregateAlbums(tle.source, responseData);

                    foreach (var item in res)
                    {
                        var newSource = new Track(tle.source) { Type = TrackType.Album };
                        var albumTle = new TrackListEntry(item, newSource, needSourceSearch: false, sourceCanBeSkipped: true);
                        albumTle.defaultFolderName = tle.defaultFolderName;
                        trackLists.AddEntry(albumTle);
                    }

                    foundSomething = res.Count > 0;
                }

                if (!foundSomething)
                {
                    var lockedFiles = responseData.lockedFilesCount > 0 ? $" (Found {responseData.lockedFilesCount} locked files)" : "";
                    Console.WriteLine($"No results.{lockedFiles}");

                    if (!Config.I.PrintResults) 
                    {
                        tle.source.State = TrackState.Failed;
                        tle.source.FailureReason = FailureReason.NoSuitableFileFound;
                        m3uEditor.Update();
                    }
                    
                    continue;
                }

                if (Config.I.skipExisting && tle.needSkipExistingAfterSearch)
                {
                    foreach (var tracks in tle.list)
                        existing.AddRange(DoSkipExisting(tracks));
                }

                if (tle.gotoNextAfterSearch)
                {
                    continue;
                }
            }

            if (Config.I.PrintResults)
            {
                await PrintResults(tle, existing, notFound);
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

        if (!Config.I.DoNotDownload && (trackLists.lists.Count > 0 || trackLists.Flattened(false, false).Skip(1).Any()))
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

        var semaphore = new SemaphoreSlim(Config.I.concurrentProcesses);

        var organizer = new FileManager(tle);

        var downloadTasks = tracks.Select(async (track, index) =>
        {
            using var cts = new CancellationTokenSource();
            await DownloadTask(tle, track, semaphore, organizer, cts, false, true, true);
            m3uEditor.Update();
        });

        await Task.WhenAll(downloadTasks);

        if (Config.I.removeTracksFromSource && tracks.All(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
            await extractor.RemoveTrackFromSource(tle.source);
    }


    static async Task DownloadAlbum(TrackListEntry tle)
    {
        var organizer = new FileManager(tle);
        List<Track>? tracks = null;
        var retrievedFolders = new HashSet<string>();
        bool succeeded = false;
        string? soulseekDir = null;

        while (tle.list.Count > 0 && !Config.I.albumArtOnly)
        {
            int index = 0;
            bool wasInteractive = Config.I.interactiveMode;

            if (Config.I.interactiveMode)
            {
                index = await InteractiveModeAlbum(tle.list, !Config.I.noBrowseFolder, retrievedFolders);
                if (index == -1) break;
            }

            tracks = tle.list[index];

            soulseekDir = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));

            organizer.SetRemoteCommonDir(soulseekDir);

            if (!Config.I.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                PrintAlbum(tracks);
            }

            var semaphore = new SemaphoreSlim(999); // Needs to be uncapped due to a bug that causes album downloads to fail after some time
            using var cts = new CancellationTokenSource();

            try
            {
                await RunAlbumDownloads(tle, organizer, tracks, semaphore, cts);

                if (!Config.I.noBrowseFolder && !retrievedFolders.Contains(soulseekDir))
                {
                    Console.WriteLine("Getting all files in folder...");

                    int newFilesFound = await Search.CompleteFolder(tracks, tracks[0].FirstResponse, soulseekDir);
                    retrievedFolders.Add(tracks[0].FirstUsername + '\\' + soulseekDir);

                    if (newFilesFound > 0)
                    {
                        Console.WriteLine($"Found {newFilesFound} more files in the directory, downloading:");
                        await RunAlbumDownloads(tle, organizer, tracks, semaphore, cts);
                    }
                    else
                    {
                        Console.WriteLine("No more files found.");
                    }
                }

                succeeded = true;
                break;
            }
            catch (OperationCanceledException)
            {
                OnAlbumFail(tracks);
            }

            organizer.SetRemoteCommonDir(null);
            tle.list.RemoveAt(index);
        }

        if (succeeded)
        {
            await OnAlbumSuccess(tle, tracks);
        }

        List<Track>? additionalImages = null;
        
        if (Config.I.albumArtOnly || succeeded && Config.I.albumArtOption != AlbumArtOption.Default)
        {
            Console.WriteLine($"\nDownloading additional images:");
            additionalImages = await DownloadImages(tle, tle.list, Config.I.albumArtOption, tracks);
            tracks?.AddRange(additionalImages);
        }

        if (tracks != null && tle.source.DownloadPath.Length > 0)
        {
            organizer.OrganizeAlbum(tracks, additionalImages);
        }

        m3uEditor.Update();
    }


    static async Task RunAlbumDownloads(TrackListEntry tle, FileManager organizer, List<Track> tracks, SemaphoreSlim semaphore, CancellationTokenSource cts)
    {
        var downloadTasks = tracks.Select(async track =>
        {
            await DownloadTask(tle, track, semaphore, organizer, cts, true, true, true);
        });
        await Task.WhenAll(downloadTasks);
    }


    static async Task OnAlbumSuccess(TrackListEntry tle, List<Track>? tracks)
    {
        if (tracks == null)
            return;

        var downloadedAudio = tracks.Where(t => !t.IsNotAudio && t.State == TrackState.Downloaded && t.DownloadPath.Length > 0);

        if (downloadedAudio.Any())
        {
            tle.source.State = TrackState.Downloaded;
            tle.source.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(t => t.DownloadPath));

            if (Config.I.removeTracksFromSource)
            {
                await extractor.RemoveTrackFromSource(tle.source);
            }
        }
    }


    static void OnAlbumFail(List<Track>? tracks)
    {
        if (tracks == null || Config.I.IgnoreAlbumFail)
            return;

        foreach (var track in tracks)
        {
            if (track.DownloadPath.Length > 0 && File.Exists(track.DownloadPath))
            {
                try
                {
                    if (Config.I.DeleteAlbumOnFail)
                    {
                        File.Delete(track.DownloadPath);
                    }
                    else if (Config.I.failedAlbumPath.Length > 0)
                    {
                        var newPath = Path.Join(Config.I.failedAlbumPath, Path.GetRelativePath(Config.I.parentDir, track.DownloadPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                        Utils.Move(track.DownloadPath, newPath);
                    }

                    Utils.DeleteAncestorsIfEmpty(Path.GetDirectoryName(track.DownloadPath), Config.I.parentDir);
                }
                catch (Exception e) 
                {
                    Printing.WriteLine($"Error: Unable to move or delete file '{track.DownloadPath}' after album fail: {e}");
                }
            }
        }
    }


    static async Task<List<Track>> DownloadImages(TrackListEntry tle, List<List<Track>> downloads, AlbumArtOption option, List<Track>? chosenAlbum)
    {
        var fileManager = new FileManager(tle);
        var downloadedImages = new List<Track>();
        long mSize = 0;
        int mCount = 0;

        var fileManager = new FileManager(tle);

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
            bool wasInteractive = Config.I.interactiveMode;

            if (Config.I.interactiveMode)
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

            if (!Config.I.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                PrintAlbum(tracks);
            }

            fileManager.SetRemoteCommonDir(Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename)));

            bool allSucceeded = true;
            var semaphore = new SemaphoreSlim(1);

            foreach (var track in tracks)
            {
                using var cts = new CancellationTokenSource();
                await DownloadTask(null, track, semaphore, fileManager, cts, false, false, false);

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


    static async Task DownloadTask(TrackListEntry? tle, Track track, SemaphoreSlim semaphore, FileManager organizer, CancellationTokenSource? cts, bool cancelOnFail, bool removeFromSource, bool organize)
    {
        if (track.State != TrackState.Initial)
            return;

        await semaphore.WaitAsync(cts.Token);

        int tries = Config.I.unknownErrorRetries;
        string savedFilePath = "";
        SlFile? chosenFile = null;

        while (tries > 0)
        {
            await WaitForLogin();

            cts.Token.ThrowIfCancellationRequested();

            try
            {
                (savedFilePath, chosenFile) = await Search.SearchAndDownload(track, organizer, cts);
            }
            catch (Exception ex)
            {
                WriteLine($"Error: {ex}", debugOnly: true);
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

            if (removeFromSource && Config.I.removeTracksFromSource)
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

        if (track.State == TrackState.Downloaded && organize)
        {
            organizer?.OrganizeAudio(track, chosenFile);
        }

        if (Config.I.onComplete.Length > 0)
        {
            OnComplete(Config.I.onComplete, track);
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
                else if (char.IsControl(key.KeyChar))
                    continue;
                else
                    userInput += key.KeyChar;
            }
        }

        string retrieveAll1 = retrieveFolder ? "| [r]            " : "";
        string retrieveAll2 = retrieveFolder ? "| Load All Files " : "";

        Console.WriteLine();
        WriteLine($" [Up/p] | [Down/n] | [Enter] | [q]                       {retrieveAll1}| [Esc/s]", ConsoleColor.Green);
        WriteLine($" Prev   | Next     | Accept  | Accept & Quit Interactive {retrieveAll2}| Skip", ConsoleColor.Green);
        Console.WriteLine();

        while (true)
        {
            var tracks = list[aidx];
            var response = tracks[0].FirstResponse;
            var username = tracks[0].FirstUsername;

            WriteLine($"[{aidx + 1} / {list.Count}]", ConsoleColor.DarkGray);

            PrintAlbum(tracks);
            Console.WriteLine();

        Loop:
            string userInput = interactiveModeLoop().Trim().ToLower();
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
                    Config.I.interactiveMode = false;
                    return aidx;
                case "r":
                    if (!retrieveFolder)
                        break;
                    var folder = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));
                    if (retrieveFolder && !retrievedFolders.Contains(username + '\\' + folder))
                    {
                        Console.WriteLine("Getting all files in folder...");
                        int newFiles = await Search.CompleteFolder(tracks, response, folder);
                        retrievedFolders.Add(username + '\\' + folder);
                        if (newFiles == 0)
                        {
                            Console.WriteLine("No more files found.");
                            goto Loop;
                        }
                        else
                        {
                            Console.WriteLine($"Found {newFiles} more files in the folder:");
                        }
                    }
                    break;
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
                                    if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > Config.I.maxStaleTime)
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
                        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn) 
                            && !client.State.HasFlag(SoulseekClientStates.LoggingIn) 
                            && !client.State.HasFlag(SoulseekClientStates.Connecting))
                        {
                            WriteLine($"\nDisconnected, logging in\n", ConsoleColor.DarkYellow, true);
                            try { await Login(Config.I.useRandomLogin); }
                            catch (Exception ex)
                            {
                                string banMsg = Config.I.useRandomLogin ? "" : " (possibly a 30-minute ban caused by frequent searches)";
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

            await Task.Delay(Config.I.updateDelay);
        }
    }


    static async Task Login(bool random = false, int tries = 3)
    {
        string user = Config.I.username, pass = Config.I.password;
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
                if (!Config.I.noModifyShareCount)
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

        bool useShellExecute = false;
        int count = 0;

        while (onComplete.Length > 2 && count++ < 2)
        {
            if (onComplete[0] == 's' && onComplete[1] == ':')
            {
                useShellExecute = true;
            }
            else if (onComplete[0].IsDigit() && onComplete[1] == ':')
            {
                if ((int)track.State != int.Parse(onComplete[0].ToString()))
                    return;
            }
            else
            {
                break;
            }
            onComplete = onComplete[2..];
        }

        var process = new Process();
        var startInfo = new ProcessStartInfo();

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
                           .Replace("{extractor}", Config.I.inputType.ToString())
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

        if (!useShellExecute)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        startInfo.UseShellExecute = useShellExecute;
        process.StartInfo = startInfo;

        WriteLine($"on-complete: FileName={startInfo.FileName}, Arguments={startInfo.Arguments}", debugOnly: true);

        process.Start();

        if (!useShellExecute)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

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
        return client != null && client.State.HasFlag(SoulseekClientStates.Connected) && client.State.HasFlag(SoulseekClientStates.LoggedIn);
    }
}


