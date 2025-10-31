using Soulseek;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Models;
using Enums;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;
using AngleSharp.Text;


public class Searcher
{
    private readonly DownloaderApplication app;
    private RateLimitedSemaphore searchSemaphore;

    public Searcher(DownloaderApplication app, int searchesPerTime, int searchRenewTime)
    {
        this.app = app;
        searchSemaphore = new RateLimitedSemaphore(searchesPerTime, TimeSpan.FromSeconds(searchRenewTime));
    }

    // very messy function that does everything
    public async Task<(string, SlFile?)> SearchAndDownload(Track track, FileManager organizer, TrackListEntry tle, Config config, Konsole.ProgressBar? progress, CancellationTokenSource? cts = null)
    {
        if (config.DoNotDownload)
            throw new Exception();

        IEnumerable<(SlResponse response, SlFile file)>? orderedResults = null;
        var responseData = new ResponseData();
        var results = new SlDictionary();
        var fsResults = new SlDictionary();
        using var searchCts = new CancellationTokenSource();
        var saveFilePath = "";
        SlFile? chosenFile = null;
        Task? downloadTask = null;
        var fsDownloadLock = new object();
        int fsResultsStarted = 0;
        int downloading = 0;
        bool notFound = false;
        bool searchEnded = false;
        string? fsUser = null;

        if (track.Downloads != null)
        {
            orderedResults = track.Downloads;
            goto downloads;
        }

        Printing.RefreshOrPrint(progress, 0, $"Waiting: {track}", false);

        app.searches.TryAdd(track, new SearchInfo(results, progress));

        void fastSearchDownload()
        {
            lock (fsDownloadLock)
            {
                if (downloading == 0 && !searchEnded)
                {
                    downloading = 1;
                    var (r, f) = fsResults.MaxBy(x => x.Value.Item1.UploadSpeed).Value;
                    saveFilePath = organizer.GetSavePath(f.Filename);
                    fsUser = r.Username;
                    chosenFile = f;
                    downloadTask = new Downloader(app).DownloadFile(r, f, saveFilePath, track, progress, tle, config, cts?.Token, searchCts);
                }
            }
        }

        void responseHandler(SearchResponse r)
        {
            if (r.Files.Count > 0)
            {
                responseData.lockedFilesCount += r.LockedFileCount;

                foreach (var file in r.Files)
                    results.TryAdd(r.Username + '\\' + file.Filename, (r, file));

                if (config.fastSearch && app.userSuccessCounts.GetValueOrDefault(r.Username, 0) > config.downrankOn)
                {
                    var f = r.Files.First();

                    if (r.HasFreeUploadSlot && r.UploadSpeed / 1024.0 / 1024.0 >= config.fastSearchMinUpSpeed
                        && FileConditions.BracketCheck(track, InferTrack(f.Filename, track)) && config.preferredCond.FileSatisfies(f, track, r))
                    {
                        fsResults.TryAdd(r.Username + '\\' + f.Filename, (r, f));
                        if (Interlocked.Exchange(ref fsResultsStarted, 1) == 0)
                        {
                            Task.Delay(config.fastSearchDelay).ContinueWith(tt => fastSearchDownload());
                        }
                    }
                }
            }
        }

        SearchOptions getSearchOptions(int timeout, FileConditions necCond, FileConditions prfCond)
        {
            return new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                searchTimeout: config.searchTimeout,
                removeSingleCharacterSearchTerms: config.removeSingleCharacterSearchTerms,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && necCond.BannedUsersSatisfies(response);
                },
                fileFilter: (file) =>
                {
                    return necCond.FileSatisfies(file, track, null);
                });
        }

        void onSearch() => Printing.RefreshOrPrint(progress, 0, $"Searching: {track}", true);
        await RunSearches(track, results, getSearchOptions, responseHandler, config, searchCts.Token, onSearch);

        app.searches.TryRemove(track, out _);
        searchEnded = true;

        Logger.Debug($"{results.Count} results found: {track}");
        if (results.Count > 0)
        {
            Logger.Debug(Printing.FormatList(results, format: result => $"{result.Value.Item1.Username}: {result.Value.Item2.Filename}"));
        }

        lock (fsDownloadLock) { }

        if (downloading == 0 && results.IsEmpty && !config.useYtdlp)
        {
            notFound = true;
        }
        else if (downloading == 1)
        {
            try
            {
                if (downloadTask == null || downloadTask.IsFaulted || downloadTask.IsCanceled)
                    throw new TaskCanceledException();
                await downloadTask;
                app.userSuccessCounts.AddOrUpdate(fsUser, 1, (k, v) => v + 1);
            }
            catch
            {
                saveFilePath = "";
                downloading = 0;
                if (chosenFile != null && fsUser != null)
                {
                    results.TryRemove(fsUser + '\\' + chosenFile.Filename, out _);
                    app.userSuccessCounts.AddOrUpdate(fsUser, -1, (k, v) => v - 1);
                }
            }
        }

        searchCts.Dispose();

    downloads:

        if (downloading == 0 && (!results.IsEmpty || orderedResults != null))
        {
            if (orderedResults == null)
                orderedResults = ResultSorter.OrderedResults(results, track, config, app.userSuccessCounts, useInfer: true);

            int trackTries = config.maxRetriesPerTrack;
            async Task<bool> process(SlResponse response, SlFile file)
            {
                saveFilePath = organizer.GetSavePath(file.Filename);
                chosenFile = file;
                try
                {
                    downloading = 1;
                    await new Downloader(app).DownloadFile(response, file, saveFilePath, track, progress, tle, config, cts?.Token);
                    app.userSuccessCounts.AddOrUpdate(response.Username, 1, (k, v) => v + 1);
                    return true;
                }
                catch (Exception e)
                {
                    if (e is OperationCanceledException && cts != null && cts.IsCancellationRequested)
                        throw;

                    Logger.DebugError($"Download Error: {e}");

                    chosenFile = null;
                    saveFilePath = "";
                    downloading = 0;

                    if (!app.IsConnectedAndLoggedIn)
                        throw;

                    app.userSuccessCounts.AddOrUpdate(response.Username, -1, (k, v) => v - 1);
                    if (--trackTries <= 0)
                    {
                        Printing.RefreshOrPrint(progress, 0, $"Out of download retries: {track}", true);
                        Printing.WriteLine("Last error was: " + e.Message, ConsoleColor.DarkYellow);
                        throw new OutOfDownloadRetriesException(e);
                    }
                    return false;
                }
            }

            foreach (var (response, file) in orderedResults)
            {
                if (app.userSuccessCounts.GetValueOrDefault(response.Username, 0) <= config.ignoreOn)
                    continue;
                bool success = await process(response, file);
                if (success) break;
            }
        }

        if (downloading == 0 && config.useYtdlp)
        {
            notFound = false;
            try
            {
                Printing.RefreshOrPrint(progress, 0, $"yt-dlp search: {track}", true);
                var ytResults = await Extractors.YouTube.YtdlpSearch(track);

                if (ytResults.Count > 0)
                {
                    foreach (var (length, id, title) in ytResults)
                    {
                        if (config.necessaryCond.LengthToleranceSatisfies(length, track.Length))
                        {
                            string saveFilePathNoExt = organizer.GetSavePathNoExt(title);
                            downloading = 1;
                            Printing.RefreshOrPrint(progress, 0, $"yt-dlp download: {track}", true);
                            saveFilePath = await Extractors.YouTube.YtdlpDownload(id, saveFilePathNoExt, config.ytdlpArgument);
                            Printing.RefreshOrPrint(progress, 100, $"Succeded: yt-dlp completed download for {track}", true);
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                saveFilePath = "";
                downloading = 0;
                Printing.RefreshOrPrint(progress, 0, $"{e.Message}", true);
                throw new NoSuitableFileFoundException("yt-dlp failed to find or download a suitable video.", e);
            }
        }

        if (downloading == 0)
        {
            if (notFound)
            {
                string lockedFilesStr = responseData.lockedFilesCount > 0 ? $" (Found {responseData.lockedFilesCount} locked files)" : "";
                Printing.RefreshOrPrint(progress, 0, $"Not found: {track}{lockedFilesStr}", true);
                throw new NoSuitableFileFoundException(lockedFilesStr.Trim());
            }
            else
            {
                Printing.RefreshOrPrint(progress, 0, $"All downloads failed: {track}", true);
                throw new AllDownloadsFailedException();
            }
        }

        return (Utils.GetFullPath(saveFilePath), chosenFile);
    }


    public async Task<List<List<Track>>> GetAlbumDownloads(Track track, ResponseData responseData, Config config)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        SearchOptions getSearchOptions(int timeout, FileConditions nec, FileConditions prf) =>
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: config.removeSingleCharacterSearchTerms,
                searchTimeout: timeout,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && nec.BannedUsersSatisfies(response);
                },
                fileFilter: (file) =>
                {
                    return !Utils.IsMusicFile(file.Filename) || nec.FileSatisfies(file, track, null);
                }
            );
        void handler(SlResponse r)
        {
            responseData.lockedFilesCount += r.LockedFileCount;

            if (r.Files.Count > 0)
            {
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + '\\' + file.Filename, (r, file));
            }
        }
        using var cts = new CancellationTokenSource();

        await RunSearches(track, results, getSearchOptions, handler, config, cts.Token);

        string fullPath((SearchResponse r, Soulseek.File f) x) { return x.r.Username + '\\' + x.f.Filename; }

        // order results first
        // the following loops should preserve the order
        var orderedResults = ResultSorter.OrderedResults(results, track, config, app.userSuccessCounts, false, false, albumMode: true);

        //Printing.PrintTrackResults(orderedResults, track, true, config.necessaryCond, config.preferredCond);

        var discPattern = new Regex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$");
        bool canMatchDiscPattern = !discPattern.IsMatch(track.Album) && !discPattern.IsMatch(track.Artist);
        var directoryStructure = new Dictionary<string, (List<(SlResponse response, SlFile file)> list, int index)>();

        // construct directory structure and save indices of the files as they appear in orderedResults
        int idx = 0;
        foreach (var x in orderedResults)
        {
            var path = fullPath(x);
            var dirpath = path[..path.LastIndexOf('\\')];
            var dirname = dirpath[(dirpath.LastIndexOf('\\') + 1)..];

            if (canMatchDiscPattern && discPattern.IsMatch(dirname))
            {
                dirpath = dirpath[..dirpath.LastIndexOf('\\')];
            }

            if (!directoryStructure.ContainsKey(dirpath))
            {
                directoryStructure[dirpath] = (new() { x }, idx);
            }
            else
            {
                directoryStructure[dirpath].list.Add(x);
            }

            idx++;
        }

        var sortedKeys = directoryStructure.Keys.OrderBy(key => key).ToList();
        var toRemove = new HashSet<string>();

        // Merge child directories (like Artist/Album/Pictures) into parent directories
        // (like Artist/Album) in case they exist
        for (int i = 0; i < sortedKeys.Count; i++)
        {
            var key = sortedKeys[i];
            if (toRemove.Contains(key))
                continue;

            for (int j = i + 1; j < sortedKeys.Count; j++)
            {
                var key2 = sortedKeys[j];
                if (toRemove.Contains(key2))
                    continue;

                if ((key2 + '\\').StartsWith(key + '\\'))
                {
                    // merge list with larger index into the list with the smaller index
                    if (directoryStructure[key].index <= directoryStructure[key2].index)
                    {
                        directoryStructure[key].list.AddRange(directoryStructure[key2].list);
                        toRemove.Add(key2);
                    }
                    else
                    {
                        directoryStructure[key2].list.AddRange(directoryStructure[key].list);
                        toRemove.Add(key);
                        key = key2;
                    }
                }
                else if (!(key2 + '\\').StartsWith(key))
                {
                    break;
                }
            }
        }

        foreach (var key in toRemove)
        {
            directoryStructure.Remove(key);
        }

        int min, max;

        min = track.MinAlbumTrackCount;
        max = track.MaxAlbumTrackCount;

        // If the search query includes track title then the results will not have full folders.
        // Need to check the actual track count later in Program.DownloadAlbum.
        if (track.Title.Length > 0)
        {
            min = -1;
        }

        bool countIsGood(int count) => count >= min && (max == -1 || count <= max);

        var result = new List<List<Track>>();

        foreach ((var key, var val) in directoryStructure)
        {
            int musicFileCount = val.list.Count(x => Utils.IsMusicFile(x.file.Filename));

            if (musicFileCount == 0 || !countIsGood(musicFileCount))
                continue;

            var ls = new List<Track>();

            foreach (var x in val.list)
            {
                var t = new Track
                {
                    Artist = track.Artist,
                    Album = track.Album,
                    Length = x.file.Length ?? -1,
                    IsNotAudio = !Utils.IsMusicFile(x.file.Filename),
                    Downloads = new() { x },
                };
                ls.Add(t);
            }

            ls = ls.OrderBy(t => t.IsNotAudio).ThenBy(t => t.Downloads[0].Item2.Filename).ToList();

            result.Add(ls);
        }

        if (result.Count == 0)
            result.Add(new List<Track>());

        return result;
    }


    public async Task<List<List<Track>>> GetDirectLinkAlbumFiles(Track track)
    {
        var parts = track.URI["slsk://".Length..].Split('/', 2);
        var username = parts[0];
        var directory = parts[1].TrimEnd('/').Replace('/', '\\');

        var files = await GetAllFilesInFolder(username, directory);

        var res = new List<List<Track>> { new List<Track>() };
        var response = new SearchResponse(username, -1, false, -1, -1, null);

        foreach (var x in files)
        {
            var path = x.dir.TrimEnd('\\') + '\\' + x.file.Filename;
            var file = new Soulseek.File(x.file.Code, path, x.file.Size, x.file.Extension);

            res[0].Add(new Track()
            {
                Length = x.file.Length ?? -1,
                IsNotAudio = !Utils.IsMusicFile(x.file.Filename),
                Downloads = new() { (response, file) },
            });
        }

        return res;
    }


    public async Task<List<Track>> GetAggregateTracks(Track track, ResponseData responseData, Config config)
    {
        var results = new SlDictionary();
        SearchOptions getSearchOptions(int timeout, FileConditions nec, FileConditions prf) =>
            new(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: config.removeSingleCharacterSearchTerms,
                searchTimeout: timeout,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && nec.BannedUsersSatisfies(response);
                },
                fileFilter: (file) =>
                {
                    return nec.FileSatisfies(file, track, null);
                }
            );
        void handler(SlResponse r)
        {
            responseData.lockedFilesCount += r.LockedFileCount;

            if (r.Files.Count > 0)
            {
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));
            }
        }
        using var cts = new CancellationTokenSource();

        await RunSearches(track, results, getSearchOptions, handler, config, cts.Token);

        string artistName = track.Artist.Trim();
        string trackName = track.Title.Trim();
        string albumName = track.Album.Trim();

        var equivalentFiles = EquivalentFiles(track, results.Select(x => x.Value), config)
            .Select(x => (x.Item1, ResultSorter.OrderedResults(x.Item2, track, config, app.userSuccessCounts, false, false, false))).ToList();

        if (!config.relax)
        {
            equivalentFiles = equivalentFiles
                .Where(x => FileConditions.StrictString(x.Item1.Title, track.Title, ignoreCase: true)
                        && (FileConditions.StrictString(x.Item1.Artist, track.Artist, ignoreCase: true, boundarySkipWs: false)
                            || FileConditions.StrictString(x.Item1.Title, track.Artist, ignoreCase: true, boundarySkipWs: false)
                                && x.Item1.Title.ContainsInBrackets(track.Artist, ignoreCase: true)))
                .ToList();
        }

        var tracks = equivalentFiles
            .Select(kvp =>
            {
                kvp.Item1.Downloads = kvp.Item2.ToList();
                return kvp.Item1;
            }).ToList();

        return tracks;
    }


    public async Task<List<List<List<Track>>>> GetAggregateAlbums(Track track, ResponseData responseData, Config config)
    {
        int maxDiff = config.aggregateLengthTol;

        bool lengthsAreSimilar(int[] sorted1, int[] sorted2)
        {
            if (sorted1.Length != sorted2.Length)
                return false;

            for (int i = 0; i < sorted1.Length; i++)
            {
                if (Math.Abs(sorted1[i] - sorted2[i]) > maxDiff)
                    return false;
            }

            return true;
        }

        var albums = await GetAlbumDownloads(track, responseData, config);

        var sortedLengthLists = new List<(int[] lengths, List<Track> album, string username)>();

        foreach (var album in albums)
        {
            if (album.Count == 0)
            {
                continue;
            }

            var sortedLengths = album.Where(x => !x.IsNotAudio).Select(x => x.Length).OrderBy(x => x).ToArray();
            string user = album[0].FirstUsername;
            sortedLengthLists.Add((sortedLengths, album, user));
        }

        var usernamesList = new List<HashSet<string>>();
        var lengthsList = new List<int[]>();
        var res = new List<List<List<Track>>>();

        foreach ((var lengths, var album, var user) in sortedLengthLists)
        {
            bool found = false;

            for (int i = 0; i < lengthsList.Count; i++)
            {
                if (lengthsAreSimilar(lengths, lengthsList[i]))
                {
                    if (lengths.Length == 1 && lengthsList[i].Length == 1)
                    {
                        var t1 = InferTrack(album[0].Downloads[0].Item2.Filename, new Track());
                        var t2 = InferTrack(res[i][0][0].Downloads[0].Item2.Filename, new Track());

                        if ((t2.Artist.ContainsIgnoreCase(t1.Artist) || t1.Artist.ContainsIgnoreCase(t2.Artist))
                            && (t2.Title.ContainsIgnoreCase(t1.Title) || t1.Title.ContainsIgnoreCase(t2.Title)))
                        {
                            found = true;
                        }
                    }
                    else
                    {
                        found = true;
                    }

                    if (found)
                    {
                        usernamesList[i].Add(user);
                        res[i].Add(album);
                        break;
                    }
                }
            }

            if (found)
            {
                continue;
            }
            else
            {
                usernamesList.Add(new() { user });
                lengthsList.Add(lengths);
                res.Add(new List<List<Track>> { album });
            }
        }

        res = res.Select((x, i) => (x, i))
            .Where(x => usernamesList[x.i].Count >= config.minSharesAggregate)
            .OrderByDescending(x => usernamesList[x.i].Count)
            .Select(x => x.x)
            .ToList();

        return res; // Note: The nested lists are still ordered according to OrderedResults
    }


    public async Task<List<(string dir, SlFile file)>> GetAllFilesInFolder(string user, string folderPrefix, CancellationToken? cancellationToken = null)
    {
        var browseOptions = new BrowseOptions();
        var res = new List<(string dir, SlFile file)>();

        folderPrefix = folderPrefix.TrimEnd('\\') + '\\';
        var userFileList = await app.Client.BrowseAsync(user, browseOptions, cancellationToken);

        foreach (var dir in userFileList.Directories)
        {
            string dirname = dir.Name.TrimEnd('\\') + '\\';
            if (dirname.StartsWith(folderPrefix))
            {
                res.AddRange(dir.Files.Select(x => (dir.Name, x)));
            }
        }
        return res;
    }


    public async Task<int> CompleteFolder(List<Track> tracks, SearchResponse response, string folder, CancellationToken? cancellationToken = null)
    {
        int newFiles = 0;
        try
        {
            List<(string dir, SlFile file)> allFiles;
            try
            {
                allFiles = await GetAllFilesInFolder(response.Username, folder, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception e)
            {
                Logger.Error($"Error getting all files in directory '{folder}: {e}'");
                return 0;
            }

            if (allFiles.Count > tracks.Count)
            {
                var paths = tracks.Select(x => x.Downloads[0].Item2.Filename).ToHashSet();
                var first = tracks[0];

                foreach ((var dir, var file) in allFiles)
                {
                    var fullPath = dir + '\\' + file.Filename;
                    if (!paths.Contains(fullPath))
                    {
                        newFiles++;
                        var newFile = new SlFile(file.Code, fullPath, file.Size, file.Extension, file.Attributes);
                        var t = new Track
                        {
                            Artist = first.Artist,
                            Album = first.Album,
                            IsNotAudio = !Utils.IsMusicFile(file.Filename),
                            Downloads = new() { (response, newFile) }
                        };
                        tracks.Add(t);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error getting complete list of files: {ex}");
        }
        return newFiles;
    }


    public static IEnumerable<(Track, IEnumerable<(SlResponse response, SlFile file)>)> EquivalentFiles(
        Track track,
        IEnumerable<(SlResponse, SlFile)> fileResponses,
        Config config,
        int minShares = -1)
    {
        if (minShares == -1)
            minShares = config.minSharesAggregate;

        Track inferTrack((SearchResponse r, Soulseek.File f) x)
        {
            var t = InferTrack(x.f.Filename, track);
            t.Length = x.f.Length ?? -1;
            return t;
        }

        var groups = fileResponses
            .GroupBy(inferTrack, new TrackComparer(ignoreCase: true, config.aggregateLengthTol))
            .Select(x => (x, x.Select(y => y.Item1.Username).Distinct().Count()))
            .Where(x => x.Item2 >= minShares)
            .OrderByDescending(x => x.Item2)
            .Select(x => x.x)
            .Select(x =>
            {
                if (x.Key.Length == -1)
                    x.Key.Length = x.FirstOrDefault(y => y.Item2.Length != null).Item2?.Length ?? -1;
                return (x.Key, x.AsEnumerable());
            });

        return groups;
    }

    public async Task RunSearches(Track track, SlDictionary results, Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions,
        Action<SearchResponse> responseHandler, Config config, CancellationToken? ct = null, Action? onSearch = null)
    {
        bool artist = track.Artist.Length > 0;
        bool title = track.Title.Length > 0;
        bool album = track.Album.Length > 0;

        string search = GetSearchString(track);
        var searchTasks = new List<Task>();

        var defaultSearchOpts = getSearchOptions(config.searchTimeout, config.necessaryCond, config.preferredCond);
        searchTasks.Add(DoSearch(search, defaultSearchOpts, responseHandler, config, ct, onSearch));

        if (search.RemoveDiacriticsIfExist(out string noDiacrSearch) && !track.ArtistMaybeWrong)
        {
            searchTasks.Add(DoSearch(noDiacrSearch, defaultSearchOpts, responseHandler, config, ct, onSearch));
        }

        await Task.WhenAll(searchTasks);

        if (results.IsEmpty && track.ArtistMaybeWrong && title)
        {
            var cond = new FileConditions(config.necessaryCond);
            var infTrack = InferTrack(track.Title, new Track());
            cond.StrictTitle = infTrack.Title == track.Title;
            cond.StrictArtist = false;
            var opts = getSearchOptions(Math.Min(config.searchTimeout, 5000), cond, config.preferredCond);
            searchTasks.Add(DoSearch($"{infTrack.Artist} {infTrack.Title}", opts, responseHandler, config, ct, onSearch));
        }

        if (config.desperateSearch)
        {
            await Task.WhenAll(searchTasks);

            if (results.IsEmpty && !track.ArtistMaybeWrong)
            {
                if (artist && album && title)
                {
                    var cond = new FileConditions(config.necessaryCond)
                    {
                        StrictTitle = true,
                        StrictAlbum = true
                    };
                    var opts = getSearchOptions(Math.Min(config.searchTimeout, 5000), cond, config.preferredCond);
                    searchTasks.Add(DoSearch($"{track.Artist} {track.Album}", opts, responseHandler, config, ct, onSearch));
                }
                if (artist && title && track.Length != -1 && config.necessaryCond.LengthTolerance != -1)
                {
                    var cond = new FileConditions(config.necessaryCond)
                    {
                        LengthTolerance = -1,
                        StrictTitle = true,
                        StrictArtist = true
                    };
                    var opts = getSearchOptions(Math.Min(config.searchTimeout, 5000), cond, config.preferredCond);
                    searchTasks.Add(DoSearch($"{track.Artist} {track.Title}", opts, responseHandler, config, ct, onSearch));
                }
            }

            await Task.WhenAll(searchTasks);

            if (results.IsEmpty)
            {
                var track2 = track.ArtistMaybeWrong ? InferTrack(track.Title, new Track()) : track;

                if (track.Album.Length > 3 && album)
                {
                    var cond = new FileConditions(config.necessaryCond)
                    {
                        StrictAlbum = true,
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(config.searchTimeout, 5000), cond, config.preferredCond);
                    searchTasks.Add(DoSearch($"{track.Album}", opts, responseHandler, config, ct, onSearch));
                }
                if (track2.Title.Length > 3 && artist)
                {
                    var cond = new FileConditions(config.necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(config.searchTimeout, 5000), cond, config.preferredCond);
                    searchTasks.Add(DoSearch($"{track2.Title}", opts, responseHandler, config, ct, onSearch));
                }
                if (track2.Artist.Length > 3 && title)
                {
                    var cond = new FileConditions(config.necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(config.searchTimeout, 5000), cond, config.preferredCond);
                    searchTasks.Add(DoSearch($"{track2.Artist}", opts, responseHandler, config, ct, onSearch));
                }
            }
        }

        await Task.WhenAll(searchTasks);
    }


    async Task DoSearch(string search, SearchOptions opts, Action<SearchResponse> rHandler, Config config, CancellationToken? ct = null, Action? onSearch = null)
    {
        await searchSemaphore.WaitAsync();
        try
        {
            search = CleanSearchString(search, !config.noRemoveSpecialChars);
            var q = SearchQuery.FromText(search);
            onSearch?.Invoke();
            await app.Client.SearchAsync(q, options: opts, cancellationToken: ct, responseHandler: rHandler);
        }
        catch (OperationCanceledException) { }
    }


    public async Task SearchAndPrintResults(List<Track> tracks, Config config)
    {
        foreach (var track in tracks)
        {
            if (!config.NonVerbosePrint)
                Console.WriteLine($"Results for {track}:");

            SearchOptions getSearchOptions(int timeout, FileConditions necCond, FileConditions prfCond)
            {
                return new SearchOptions(
                    minimumResponseFileCount: 1,
                    minimumPeerUploadSpeed: 1,
                    searchTimeout: config.searchTimeout,
                    removeSingleCharacterSearchTerms: config.removeSingleCharacterSearchTerms,
                    responseFilter: (response) =>
                    {
                        return response.UploadSpeed > 0 && necCond.BannedUsersSatisfies(response);
                    },
                    fileFilter: (file) =>
                    {
                        return (necCond.FileSatisfies(file, track, null) || config.PrintFull);
                    });
            }

            var results = new SlDictionary();

            void responseHandler(SearchResponse r)
            {
                if (r.Files.Count > 0)
                {
                    foreach (var file in r.Files)
                        results.TryAdd(r.Username + '\\' + file.Filename, (r, file));
                }
            }

            await RunSearches(track, results, getSearchOptions, responseHandler, config);

            if (config.DoNotDownload && results.IsEmpty)
            {
                if (config.printOption.HasFlag(PrintOption.Json))
                {
                    JsonPrinter.PrintTrackResultJson(track, Enumerable.Empty<(SearchResponse, Soulseek.File)>());
                }

                if (!config.NonVerbosePrint)
                    Printing.WriteLine($"No results", ConsoleColor.Yellow);
            }
            else
            {
                var orderedResults = ResultSorter.OrderedResults(results, track, config, app.userSuccessCounts, useInfer: true);

                if (!config.NonVerbosePrint)
                    Console.WriteLine();

                if (config.printOption.HasFlag(PrintOption.Json))
                {
                    JsonPrinter.PrintTrackResultJson(track, orderedResults, config.printOption.HasFlag(PrintOption.Full));
                }
                else if (config.printOption.HasFlag(PrintOption.Link))
                {
                    var first = orderedResults.First();
                    Printing.PrintLink(first.response.Username, first.file.Filename);
                }
                else
                {
                    Printing.PrintTrackResults(orderedResults, track, config.PrintFull, config.necessaryCond, config.preferredCond);
                }
            }

            if (!config.NonVerbosePrint)
                Console.WriteLine();
        }
    }


    static string GetSearchString(Track track)
    {
        if (track.Type == TrackType.Album)
        {
            if (track.Album.Length > 0)
                return (track.Artist + " " + track.Album + " " + track.Title).Trim();
            if (track.Title.Length > 0)
                return (track.Artist + " " + track.Title).Trim();
            return track.Artist.Trim();
        }
        else
        {
            if (track.Title.Length > 0)
                return (track.Artist + " " + track.Title).Trim();
            else if (track.Album.Length > 0)
                return (track.Artist + " " + track.Album).Trim();
            return track.Artist.Trim();
        }
    }


    static string CleanSearchString(string str, bool removeSpecialChars)
    {
        str = str.ToLower();
        string old;

        if (removeSpecialChars)
        {
            old = str;
            str = str.ReplaceSpecialChars(" ").Trim().RemoveConsecutiveWs();
            if (str.Length == 0) str = old;
        }

        foreach (var banned in bannedTerms)
        {
            if (banned.All(x => str.Contains(x)))
            {
                str = str.Replace(banned[0], string.Concat("*", banned[0].AsSpan(1)));
            }
        }

        return str.Trim();
    }


    public static Track InferTrack(string filename, Track defaultTrack, TrackType type = TrackType.Normal)
    {
        var t = new Track(defaultTrack)
        {
            ItemNumber = -1,
            Type = type
        };

        filename = Utils.GetFileNameWithoutExtSlsk(filename);

        // Special case: filename is of the form "(tracknum) [Artist] Title"
        if (filename.Length >= 6 && filename[0] == '(' && filename[1].IsDigit() && filename[2].IsDigit() && filename[3] == ')' && filename[4] == ' ' && filename[5] == '[')
        {
            int closingBracketIndex = filename.IndexOf(']', 6);
            if (closingBracketIndex > 6)
            {
                int titleStartIndex = closingBracketIndex + 1;
                if (titleStartIndex < filename.Length && filename[titleStartIndex] == ' ')
                    titleStartIndex++;

                if (titleStartIndex < filename.Length)
                {
                    t.Artist = filename[6..closingBracketIndex];
                    t.Title = filename[titleStartIndex..];
                }
            }
        }

        filename = filename.Replace(" — ", " - ").Replace('_', ' ').Trim().RemoveConsecutiveWs();

        var trackNumStart = new Regex(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)");
        var trackNumMiddle = new Regex(@"(?<= - )((\d-)?\d{2,3}|\d{2,3}\.?)\s+");
        var trackNumMiddleAlt = new Regex(@"\s+-(\d{2,3})-\s+");

        if (trackNumStart.IsMatch(filename))
        {
            filename = trackNumStart.Replace(filename, "", 1).Trim();
            if (filename.StartsWith("- "))
                filename = filename[2..].Trim();
        }
        else
        {
            var reg = trackNumMiddle.IsMatch(filename) ? trackNumMiddle : (trackNumMiddleAlt.IsMatch(filename) ? trackNumMiddleAlt : null);
            if (reg != null && !reg.IsMatch(defaultTrack.ToString(noInfo: true)))
            {
                filename = reg.Replace(filename, "<<tracknum>>", 1).Trim();
                filename = Regex.Replace(filename, @"-\s*<<tracknum>>\s*-", "-");
                filename = filename.Replace("<<tracknum>>", "");
            }
        }

        string aname = t.Artist.Trim();
        string tname = t.Title.Trim();
        string alname = t.Album.Trim();
        string fname = filename;

        fname = fname.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveConsecutiveWs().Trim();
        tname = tname.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        aname = aname.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        alname = alname.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();

        bool maybeRemix = aname.Length > 0 && Regex.IsMatch(fname, @$"\({Regex.Escape(aname)} .+\)", RegexOptions.IgnoreCase);
        string[] parts = fname.Split(new string[] { " - " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] realParts = filename.Split(new string[] { " - " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != realParts.Length)
            realParts = parts;

        if (parts.Length == 1)
        {
            if (maybeRemix)
                t.ArtistMaybeWrong = true;
            t.Title = parts[0];
        }
        else if (parts.Length == 2)
        {
            t.Artist = realParts[0];
            t.Title = realParts[1];

            if (!parts[0].ContainsIgnoreCase(aname) || !parts[1].ContainsIgnoreCase(tname))
            {
                t.ArtistMaybeWrong = true;
            }
        }
        else if (parts.Length == 3)
        {
            bool hasTitle = tname.Length > 0 && parts[2].ContainsIgnoreCase(tname);
            if (hasTitle)
                t.Title = realParts[2];

            int artistPos = -1;
            if (aname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(aname))
                    artistPos = 0;
                else if (parts[1].ContainsIgnoreCase(aname))
                    artistPos = 1;
                else
                    t.ArtistMaybeWrong = true;
            }
            int albumPos = -1;
            if (alname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(alname))
                    albumPos = 0;
                else if (parts[1].ContainsIgnoreCase(alname))
                    albumPos = 1;
            }
            if (artistPos >= 0 && artistPos == albumPos)
            {
                artistPos = 0;
                albumPos = 1;
            }
            if (artistPos == -1 && maybeRemix)
            {
                t.ArtistMaybeWrong = true;
                artistPos = 0;
                albumPos = 1;
            }
            if (artistPos == -1 && albumPos == -1)
            {
                t.ArtistMaybeWrong = true;
                t.Artist = realParts[0] + " - " + realParts[1];
            }
            else if (artistPos >= 0)
            {
                t.Artist = parts[artistPos];
            }

            t.Title = parts[2];
        }
        else
        {
            int artistPos = -1, titlePos = -1;

            if (aname.Length > 0)
            {
                var s = parts.Select((p, i) => (p, i)).Where(x => x.p.ContainsIgnoreCase(aname));
                if (s.Any())
                {
                    artistPos = s.MinBy(x => Math.Abs(x.p.Length - aname.Length)).i;
                    if (artistPos != -1)
                        t.Artist = parts[artistPos];
                }
            }
            if (tname.Length > 0)
            {
                var ss = parts.Select((p, i) => (p, i)).Where(x => x.i != artistPos && x.p.ContainsIgnoreCase(tname));
                if (ss.Any())
                {
                    titlePos = ss.MinBy(x => Math.Abs(x.p.Length - tname.Length)).i;
                    if (titlePos != -1)
                        t.Title = parts[titlePos];
                }
            }
        }

        if (t.Title.Length == 0)
        {
            t.Title = fname;
            t.ArtistMaybeWrong = true;
        }
        else if (t.Artist.Length > 0 && !t.Title.ContainsIgnoreCase(defaultTrack.Title) && !t.Artist.ContainsIgnoreCase(defaultTrack.Artist))
        {
            string[] x = { t.Artist, t.Album, t.Title };

            var perm = (0, 1, 2);
            (int, int, int)[] permutations = { (0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0) };

            foreach (var p in permutations)
            {
                if (x[p.Item1].ContainsIgnoreCase(defaultTrack.Artist) && x[p.Item3].ContainsIgnoreCase(defaultTrack.Title))
                {
                    perm = p;
                    break;
                }
            }

            t.Artist = x[perm.Item1];
            t.Album = x[perm.Item2];
            t.Title = x[perm.Item3];
        }

        t.Title = t.Title.RemoveFt();
        t.Artist = t.Artist.RemoveFt();

        return t;
    }


    public bool AlbumsAreSimilar(List<Track> album1, List<Track> album2, int[]? album1SortedLengths = null, int tolerance = 3)
    {
        if (album1SortedLengths != null && album1SortedLengths.Length != album2.Count(t => !t.IsNotAudio))
            return false;
        else if (album1.Count(t => !t.IsNotAudio) != album2.Count(t => !t.IsNotAudio))
            return false;

        if (album1SortedLengths == null)
            album1SortedLengths = album1.Where(t => !t.IsNotAudio).Select(t => t.Length).OrderBy(x => x).ToArray();

        var album2SortedLengths = album2.Where(t => !t.IsNotAudio).Select(t => t.Length).OrderBy(x => x).ToArray();

        for (int i = 0; i < album1SortedLengths.Length; i++)
        {
            if (Math.Abs(album1SortedLengths[i] - album2SortedLengths[i]) > tolerance)
                return false;
        }

        return true;
    }

    // copyright is joke
    public static readonly string[][] bannedTerms =
    {
        new string[] { "depeche", "mode" },
        new string[] { "beatles" },
        new string[] { "prince", "revolutions" },
        new string[] { "michael", "jackson" },
        new string[] { "coexist" },
        new string[] { "bob", "dylan" },
        new string[] { "enter", "shikari" },
        new string[] { "village", "people" },
        new string[] { "lenny", "kravitz" },
        new string[] { "beyonce" },
        new string[] { "beyoncé" },
        new string[] { "lady", "gaga" },
        new string[] { "jay", "z" },
        new string[] { "kanye", "west" },
        new string[] { "rihanna" },
        new string[] { "adele" },
        new string[] { "kendrick", "lamar" },
        new string[] { "romance", "bad" },
        new string[] { "born", "this", "way" },
        new string[] { "weeknd" },
        new string[] { "broken", "hearted" },
        new string[] { "highway", "61", "revisited" },
        new string[] { "west", "gold", "digger" },
        new string[] { "west", "good", "life" },
        new string[] { "hold", "my", "hand" },
        new string[] { "ymca" },
        new string[] { "navy", "in", "the" },
        new string[] { "macho" },
        new string[] { "west", "go" },
        new string[] { "hot", "cop" },
        new string[] { "phone", "sex", "over", "the" },
        new string[] { "minaj" },
        new string[] { "government", "hooker" },
        new string[] { "wayne", "lil" },
        new string[] { "mood", "4", "eva" },
        new string[] { "ghosts", "again" },
        new string[] { "purple", "rain" }
    };
}

public abstract class SearchAndDownloadException : Exception
{
    public FailureReason Reason { get; }

    protected SearchAndDownloadException(FailureReason reason, string message) : base(message)
    {
        Reason = reason;
    }

    protected SearchAndDownloadException(FailureReason reason, string message, Exception innerException) : base(message, innerException)
    {
        Reason = reason;
    }
}

public class OutOfDownloadRetriesException : SearchAndDownloadException
{
    public OutOfDownloadRetriesException(Exception innerException)
        : base(FailureReason.OutOfDownloadRetries, "Exceeded maximum download retries for the track.", innerException)
    {
    }
}

public class NoSuitableFileFoundException : SearchAndDownloadException
{
    public NoSuitableFileFoundException(string? details = null)
        : base(FailureReason.NoSuitableFileFound, $"No suitable file was found on Soulseek. {details}".Trim())
    {
    }

    public NoSuitableFileFoundException(string details, Exception innerException)
        : base(FailureReason.NoSuitableFileFound, $"No suitable file was found on Soulseek: {details}", innerException)
    {
    }
}

public class AllDownloadsFailedException : SearchAndDownloadException
{
    public AllDownloadsFailedException()
        : base(FailureReason.AllDownloadsFailed, "All potential downloads for the track failed.")
    {
    }
}
