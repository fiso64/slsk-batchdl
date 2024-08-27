using Soulseek;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Data;
using Enums;

using File = System.IO.File;
using Directory = System.IO.Directory;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;


static partial class Program
{
    static async Task<string> SearchAndDownload(Track track, ResponseData? responseData = null)
    {
        if (Config.DoNotDownload)
            throw new Exception();

        responseData ??= new ResponseData();
        var progress = GetProgressBar(Config.displayMode);
        var results = new SlDictionary();
        var fsResults = new SlDictionary();
        var cts = new CancellationTokenSource();
        var saveFilePath = "";
        Task? downloadTask = null;
        var fsDownloadLock = new object();
        int fsResultsStarted = 0;
        int downloading = 0;
        bool notFound = false;
        bool searchEnded = false;
        string fsUser = "";
        string fsFile = "";

        if (track.Downloads != null)
        {
            results = track.Downloads;
            goto downloads;
        }

        RefreshOrPrint(progress, 0, $"Waiting: {track}", false);

        string searchText = $"{track.Artist} {track.Title}".Trim();
        var removeChars = new string[] { " ", "_", "-" };

        searches.TryAdd(track, new SearchInfo(results, progress));

        void fastSearchDownload()
        {
            lock (fsDownloadLock)
            {
                if (downloading == 0 && !searchEnded)
                {
                    downloading = 1;
                    var (r, f) = fsResults.MaxBy(x => x.Value.Item1.UploadSpeed).Value;
                    saveFilePath = GetSavePath(f.Filename);
                    fsUser = r.Username;
                    fsFile = f.Filename;
                    downloadTask = DownloadFile(r, f, saveFilePath, track, progress, cts);
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

                if (Config.fastSearch && userSuccessCount.GetValueOrDefault(r.Username, 0) > Config.downrankOn)
                {
                    var f = r.Files.First();

                    if (r.HasFreeUploadSlot && r.UploadSpeed / 1024.0 / 1024.0 >= Config.fastSearchMinUpSpeed
                        && FileConditions.BracketCheck(track, InferTrack(f.Filename, track)) && Config.preferredCond.FileSatisfies(f, track, r))
                    {
                        fsResults.TryAdd(r.Username + '\\' + f.Filename, (r, f));
                        if (Interlocked.Exchange(ref fsResultsStarted, 1) == 0)
                        {
                            Task.Delay(Config.fastSearchDelay).ContinueWith(tt => fastSearchDownload());
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
                searchTimeout: Config.searchTimeout,
                removeSingleCharacterSearchTerms: Config.removeSingleCharacterSearchTerms,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && necCond.BannedUsersSatisfies(response);
                },
                fileFilter: (file) =>
                {
                    return Utils.IsMusicFile(file.Filename) && necCond.FileSatisfies(file, track, null);
                });
        }

        void onSearch() => RefreshOrPrint(progress, 0, $"Searching: {track}", true);
        await RunSearches(track, results, getSearchOptions, responseHandler, cts.Token, onSearch);

        searches.TryRemove(track, out _);
        searchEnded = true;
        lock (fsDownloadLock) { }

        if (downloading == 0 && results.IsEmpty && !Config.useYtdlp)
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
                userSuccessCount.AddOrUpdate(fsUser, 1, (k, v) => v + 1);
            }
            catch
            {
                saveFilePath = "";
                downloading = 0;
                results.TryRemove(fsUser + "\\" + fsFile, out _);
                userSuccessCount.AddOrUpdate(fsUser, -1, (k, v) => v - 1);
            }
        }

        cts.Dispose();

    downloads:

        if (downloading == 0 && !results.IsEmpty)
        {
            var orderedResults = OrderedResults(results, track, true);

            int trackTries = Config.maxRetriesPerTrack;
            async Task<bool> process(SlResponse response, SlFile file)
            {
                saveFilePath = GetSavePath(file.Filename);
                try
                {
                    downloading = 1;
                    await DownloadFile(response, file, saveFilePath, track, progress);
                    userSuccessCount.AddOrUpdate(response.Username, 1, (k, v) => v + 1);
                    return true;
                }
                catch (Exception e)
                {
                    downloading = 0;
                    if (!IsConnectedAndLoggedIn())
                        throw;
                    userSuccessCount.AddOrUpdate(response.Username, -1, (k, v) => v - 1);
                    if (--trackTries <= 0)
                    {
                        RefreshOrPrint(progress, 0, $"Out of download retries: {track}", true);
                        WriteLine("Last error was: " + e.Message, ConsoleColor.DarkYellow, true);
                        throw new SearchAndDownloadException(FailureReason.OutOfDownloadRetries);
                    }
                    return false;
                }
            }

            // the first result is usually fine, no need to sort the entire sequence
            var fr = orderedResults.First();
            bool success = await process(fr.response, fr.file);

            if (!success)
            {
                fr = orderedResults.Skip(1).FirstOrDefault();
                if (fr != default)
                {
                    if (userSuccessCount.GetValueOrDefault(fr.response.Username, 0) > Config.ignoreOn)
                    {
                        success = await process(fr.response, fr.file);
                    }
                    if (!success)
                    {
                        foreach (var (response, file) in orderedResults.Skip(2))
                        {
                            if (userSuccessCount.GetValueOrDefault(response.Username, 0) <= Config.ignoreOn)
                                continue;
                            success = await process(response, file);
                            if (success) break;
                        }
                    }
                }
            }
        }

        if (downloading == 0 && Config.useYtdlp)
        {
            notFound = false;
            try
            {
                RefreshOrPrint(progress, 0, $"yt-dlp search: {track}", true);
                var ytResults = await Extractors.YouTube.YtdlpSearch(track);

                if (ytResults.Count > 0)
                {
                    foreach (var (length, id, title) in ytResults)
                    {
                        if (Config.necessaryCond.LengthToleranceSatisfies(length, track.Length))
                        {
                            string saveFilePathNoExt = GetSavePathNoExt(title);
                            downloading = 1;
                            RefreshOrPrint(progress, 0, $"yt-dlp download: {track}", true);
                            saveFilePath = await Extractors.YouTube.YtdlpDownload(id, saveFilePathNoExt, Config.ytdlpArgument);
                            RefreshOrPrint(progress, 100, $"Succeded: yt-dlp completed download for {track}", true);
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                saveFilePath = "";
                downloading = 0;
                RefreshOrPrint(progress, 0, $"{e.Message}", true);
                throw new SearchAndDownloadException(FailureReason.NoSuitableFileFound);
            }
        }

        if (downloading == 0)
        {
            if (notFound)
            {
                string lockedFilesStr = responseData.lockedFilesCount > 0 ? $" (Found {responseData.lockedFilesCount} locked files)" : "";
                RefreshOrPrint(progress, 0, $"Not found: {track}{lockedFilesStr}", true);
                throw new SearchAndDownloadException(FailureReason.NoSuitableFileFound);
            }
            else
            {
                RefreshOrPrint(progress, 0, $"All downloads failed: {track}", true);
                throw new SearchAndDownloadException(FailureReason.AllDownloadsFailed);
            }
        }

        if (Config.nameFormat.Length > 0)
            saveFilePath = ApplyNamingFormat(saveFilePath, track);

        return Path.GetFullPath(saveFilePath);
    }


    static async Task<List<List<Track>>> GetAlbumDownloads(Track track, ResponseData responseData)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        SearchOptions getSearchOptions(int timeout, FileConditions nec, FileConditions prf) =>
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: Config.removeSingleCharacterSearchTerms,
                searchTimeout: timeout,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && nec.BannedUsersSatisfies(response);
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
        var cts = new CancellationTokenSource();

        await RunSearches(track, results, getSearchOptions, handler, cts.Token);

        string fullPath((SearchResponse r, Soulseek.File f) x) { return x.r.Username + "\\" + x.f.Filename; }

        var orderedResults = OrderedResults(results, track, false, false, albumMode: true);

        var discPattern = new Regex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$");
        bool canMatchDiscPattern = !discPattern.IsMatch(track.Album) && !discPattern.IsMatch(track.Artist);
        var directoryStructure = new Dictionary<string, List<(SlResponse response, SlFile file)>>();

        foreach (var x in orderedResults)
        {
            var path = fullPath(x);
            var dirpath = path[..path.LastIndexOf('\\')];

            if (!directoryStructure.ContainsKey(dirpath))
            {
                directoryStructure[dirpath] = new() { x };
            }
            else
            {
                directoryStructure[dirpath].Add(x);
            }
        }

        if (canMatchDiscPattern)
        {
            foreach (var key in directoryStructure.Keys.ToArray())
            {
                var dirname = key[(key.LastIndexOf('\\') + 1)..];
            
                if (discPattern.IsMatch(dirname))
                {
                    directoryStructure.Remove(key, out var val);
                    var newKey = key[..key.LastIndexOf('\\')];

                    if (directoryStructure.ContainsKey(newKey))
                    {
                        directoryStructure[newKey].AddRange(val);
                    }
                    else
                    {
                        directoryStructure[newKey] = val;
                    }
                }
            }
        }

        foreach ((var key, var val) in directoryStructure)
        {
            foreach ((var key2, var val2) in directoryStructure)
            {
                if (key == key2)
                    continue;

                if ((key2 + '\\').StartsWith(key + '\\'))
                {
                    val.AddRange(val2);
                    directoryStructure.Remove(key2);
                }
            }
        }

        int min, max;
        if (Config.minAlbumTrackCount > -1 || Config.maxAlbumTrackCount > -1)
        {
            min = Config.minAlbumTrackCount;
            max = Config.maxAlbumTrackCount;
        }
        else
        {
            min = track.MinAlbumTrackCount;
            max = track.MaxAlbumTrackCount;
        }

        bool countIsGood(int count) => count >= min && (max == -1 || count <= max);

        var result = new List<List<Track>>();

        foreach ((var key, var val) in directoryStructure)
        {
            int musicFileCount = val.Count(x => Utils.IsMusicFile(x.file.Filename));

            if (!countIsGood(musicFileCount))
                continue;

            var ls = new List<Track>();

            foreach (var x in val)
            {
                var t = new Track
                {
                    Artist = track.Artist,
                    Album = track.Album,
                    Length = x.file.Length ?? -1,
                    IsNotAudio = !Utils.IsMusicFile(x.file.Filename),
                    Downloads = new ConcurrentDictionary<string, (SlResponse, SlFile file)>(
                    new Dictionary<string, (SlResponse response, SlFile file)> { { x.response.Username + '\\' + x.file.Filename, x } })
                };
                ls.Add(t);
            }

            ls = ls.OrderBy(t => t.IsNotAudio).ThenBy(t => t.Downloads.First().Value.Item2.Filename).ToList();

            result.Add(ls);
        }

        if (result.Count == 0)
            result.Add(new List<Track>());

        return result;
    }


    static async Task<List<Track>> GetAggregateTracks(Track track, ResponseData responseData)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        SearchOptions getSearchOptions(int timeout, FileConditions nec, FileConditions prf) =>
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: Config.removeSingleCharacterSearchTerms,
                searchTimeout: timeout,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && nec.BannedUsersSatisfies(response);
                },
                fileFilter: (file) =>
                {
                    return Utils.IsMusicFile(file.Filename) && nec.FileSatisfies(file, track, null);
                    //&& FileConditions.StrictString(file.Filename, track.ArtistName, ignoreCase: true)
                    //&& FileConditions.StrictString(file.Filename, track.TrackTitle, ignoreCase: true)
                    //&& FileConditions.StrictString(file.Filename, track.Album, ignoreCase: true);
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
        var cts = new CancellationTokenSource();

        await RunSearches(track, results, getSearchOptions, handler, cts.Token);

        string artistName = track.Artist.Trim();
        string trackName = track.Title.Trim();
        string albumName = track.Album.Trim();

        var fileResponses = results.Select(x => x.Value);

        var equivalentFiles = EquivalentFiles(track, fileResponses).ToList();

        if (!Config.relax)
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
                kvp.Item1.Downloads = new SlDictionary(
                    kvp.Item2.ToDictionary(item => { return item.response.Username + "\\" + item.file.Filename; }, item => item));
                return kvp.Item1;
            }).ToList();

        return tracks;
    }


    static async Task<List<List<List<Track>>>> GetAggregateAlbums(Track track, ResponseData responseData)
    {
        int maxDiff = Config.necessaryCond.LengthTolerance;

        if (maxDiff < 0)
            maxDiff = 3;

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

        var albums = await GetAlbumDownloads(track, responseData);

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
                        var t1 = InferTrack(album[0].Downloads.First().Value.Item2.Filename, new Track());
                        var t2 = InferTrack(res[i][0][0].Downloads.First().Value.Item2.Filename, new Track());

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
                lengthsList.Add(lengths);
                res.Add(new List<List<Track>> { album });
            }
        }

        res = res.Where(x => x.Count >= Config.minSharesAggregate).OrderByDescending(x => x.Count).ToList();

        return res; // Note: The nested lists are still ordered according to OrderedResults
    }


    static async Task<List<(string dir, SlFile file)>> GetAllFilesInFolder(string user, string folderPrefix)
    {
        var browseOptions = new BrowseOptions();
        var res = new List<(string dir, SlFile file)>();

        folderPrefix = folderPrefix.TrimEnd('\\') + '\\';
        var userFileList = await client.BrowseAsync(user, browseOptions);

        foreach (var dir in userFileList.Directories)
        {
            string dirname = dir.Name.TrimEnd('\\') + '\\';
            if (dirname.StartsWith(folderPrefix))
            {
                res.AddRange(dir.Files.Select(x => (dir.Name, x)));
            }
        }
        return res;

        // It would be much better to use GetDirectoryContentsAsync. Unfortunately it only returns the file
        // names without full paths, and DownloadAsync needs full paths in order to download files.
        // Therefore it would not be possible to download any files that are in a subdirectory of the folder.

        // var dir = await client.GetDirectoryContentsAsync(user, folderPrefix);
        // var res = dir.Files.Select(x => (folderPrefix, x)).ToList();
        // return res;
    }


    static async Task CompleteFolder(List<Track> tracks, SearchResponse response, string folder)
    {
        try
        {
            var allFiles = await GetAllFilesInFolder(response.Username, folder);

            if (allFiles.Count > tracks.Count)
            {
                var paths = tracks.Select(x => x.Downloads.First().Value.Item2.Filename).ToHashSet();
                var first = tracks[0];

                foreach ((var dir, var file) in allFiles)
                {
                    var fullPath = dir + '\\' + file.Filename;
                    if (!paths.Contains(fullPath))
                    {
                        var newFile = new SlFile(file.Code, fullPath, file.Size, file.Extension, file.Attributes);
                        var t = new Track
                        {
                            Artist = first.Artist,
                            Album = first.Album,
                            IsNotAudio = !Utils.IsMusicFile(file.Filename),
                            Downloads = new ConcurrentDictionary<string, (SlResponse, SlFile file)>(
                                new Dictionary<string, (SlResponse response, SlFile file)> { { response.Username + '\\' + fullPath, (response, newFile) } })
                        };
                        tracks.Add(t);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLine($"Error getting complete list of files: {ex}", ConsoleColor.DarkYellow);
        }
    }


    static IOrderedEnumerable<(Track, IEnumerable<(SlResponse response, SlFile file)>)> EquivalentFiles(Track track,
        IEnumerable<(SlResponse, SlFile)> fileResponses, int minShares = -1)
    {
        if (minShares == -1)
            minShares = Config.minSharesAggregate;

        Track inferTrack((SearchResponse r, Soulseek.File f) x)
        {
            var t = InferTrack(x.f.Filename, track);
            t.Length = x.f.Length ?? -1;
            return t;
        }

        var res = fileResponses
            .GroupBy(inferTrack, new TrackStringComparer(ignoreCase: true))
            .Where(group => group.Select(x => x.Item1.Username).Distinct().Count() >= minShares)
            .SelectMany(group =>
            {
                var sortedTracks = group.OrderBy(t => t.Item2.Length).Where(x => x.Item2.Length != null).ToList();
                var groups = new List<(Track, List<(SearchResponse, Soulseek.File)>)>();
                var noLengthGroup = group.Where(x => x.Item2.Length == null);
                for (int i = 0; i < sortedTracks.Count;)
                {
                    var subGroup = new List<(SearchResponse, Soulseek.File)> { sortedTracks[i] };
                    int j = i + 1;
                    while (j < sortedTracks.Count)
                    {
                        int l1 = (int)sortedTracks[j].Item2.Length;
                        int l2 = (int)sortedTracks[i].Item2.Length;
                        if (Config.necessaryCond.LengthTolerance == -1 || Math.Abs(l1 - l2) <= Config.necessaryCond.LengthTolerance)
                        {
                            subGroup.Add(sortedTracks[j]);
                            j++;
                        }
                        else break;
                    }
                    var t = new Track(group.Key);
                    t.Length = (int)sortedTracks[i].Item2.Length;
                    groups.Add((t, subGroup));
                    i = j;
                }

                if (noLengthGroup.Any())
                {
                    if (groups.Count > 0 && !Config.preferredCond.AcceptNoLength)
                        groups.First().Item2.AddRange(noLengthGroup);
                    else
                        groups.Add((group.Key, noLengthGroup.ToList()));
                }

                return groups.Where(subGroup => subGroup.Item2.Select(x => x.Item1.Username).Distinct().Count() >= minShares)
                    .Select(subGroup => (subGroup.Item1, subGroup.Item2.AsEnumerable()));
            }).OrderByDescending(x => x.Item2.Count());

        return res;
    }


    static IOrderedEnumerable<(SlResponse response, SlFile file)> OrderedResults(IEnumerable<KeyValuePair<string, (SlResponse, SlFile)>> results,
        Track track, bool useInfer = false, bool useLevenshtein = true, bool albumMode = false)
    {
        bool useBracketCheck = true;
        if (albumMode)
        {
            useBracketCheck = false;
            useLevenshtein = false;
            useInfer = false;
        }

        Dictionary<string, (Track, int)>? infTracksAndCounts = null;
        if (useInfer)
        {
            var equivalentFiles = EquivalentFiles(track, results.Select(x => x.Value), 1);
            infTracksAndCounts = equivalentFiles
                .SelectMany(t => t.Item2, (t, f) => new { t.Item1, f.response.Username, f.file.Filename, Count = t.Item2.Count() })
                .ToSafeDictionary(x => $"{x.Username}\\{x.Filename}", y => (y.Item1, y.Count));
        }

        (Track, int) inferredTrack((SearchResponse response, Soulseek.File file) x)
        {
            string key = $"{x.response.Username}\\{x.file.Filename}";
            if (infTracksAndCounts != null && infTracksAndCounts.ContainsKey(key))
                return infTracksAndCounts[key];
            return (new Track(), 0);
        }

        int levenshtein((SearchResponse response, Soulseek.File file) x)
        {
            Track t = inferredTrack(x).Item1;
            string t1 = track.Title.RemoveFt().ReplaceSpecialChars("").Replace(" ", "").Replace("_", "").ToLower();
            string t2 = t.Title.RemoveFt().ReplaceSpecialChars("").Replace(" ", "").Replace("_", "").ToLower();
            return Utils.Levenshtein(t1, t2);
        }

        var random = new Random();
        return results.Select(kvp => (response: kvp.Value.Item1, file: kvp.Value.Item2))
                .Where(x => userSuccessCount.GetValueOrDefault(x.response.Username, 0) > Config.ignoreOn)
                .OrderByDescending(x => userSuccessCount.GetValueOrDefault(x.response.Username, 0) > Config.downrankOn)
                .ThenByDescending(x => Config.necessaryCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => Config.preferredCond.BannedUsersSatisfies(x.response))
                .ThenByDescending(x => (x.file.Length != null && x.file.Length > 0) || Config.preferredCond.AcceptNoLength)
                .ThenByDescending(x => !useBracketCheck || FileConditions.BracketCheck(track, inferredTrack(x).Item1)) // downrank result if it contains '(' or '[' and the title does not (avoid remixes)
                .ThenByDescending(x => Config.preferredCond.StrictTitleSatisfies(x.file.Filename, track.Title))
                .ThenByDescending(x => Config.preferredCond.StrictArtistSatisfies(x.file.Filename, track.Title))
                .ThenByDescending(x => Config.preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => Config.preferredCond.FormatSatisfies(x.file.Filename))
                .ThenByDescending(x => Config.preferredCond.StrictAlbumSatisfies(x.file.Filename, track.Album))
                .ThenByDescending(x => Config.preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => Config.preferredCond.SampleRateSatisfies(x.file))
                .ThenByDescending(x => Config.preferredCond.BitDepthSatisfies(x.file))
                .ThenByDescending(x => Config.preferredCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 1024 / 650)
                .ThenByDescending(x => albumMode || FileConditions.StrictString(x.file.Filename, track.Title))
                .ThenByDescending(x => !albumMode || FileConditions.StrictString(Utils.GetDirectoryNameSlsk(x.file.Filename), track.Album))
                .ThenByDescending(x => FileConditions.StrictString(x.file.Filename, track.Artist, boundarySkipWs: false))
                .ThenByDescending(x => useInfer ? inferredTrack(x).Item2 : 0) // sorts by the number of occurences of this track
                .ThenByDescending(x => x.response.UploadSpeed / 1024 / 350)
                .ThenByDescending(x => (x.file.BitRate ?? 0) / 80)
                .ThenByDescending(x => useLevenshtein ? levenshtein(x) / 5 : 0) // sorts by the distance between the track title and the inferred title of the search result
                .ThenByDescending(x => random.Next());
    }


    static async Task RunSearches(Track track, SlDictionary results, Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions,
        Action<SearchResponse> responseHandler, CancellationToken? ct = null, Action? onSearch = null)
    {
        bool artist = track.Artist.Length > 0;
        bool title = track.Title.Length > 0;
        bool album = track.Album.Length > 0;

        string search = GetSearchString(track);
        var searchTasks = new List<Task>();

        var defaultSearchOpts = getSearchOptions(Config.searchTimeout, Config.necessaryCond, Config.preferredCond);
        searchTasks.Add(Search(search, defaultSearchOpts, responseHandler, ct, onSearch));

        if (search.RemoveDiacriticsIfExist(out string noDiacrSearch) && !track.ArtistMaybeWrong)
        {
            searchTasks.Add(Search(noDiacrSearch, defaultSearchOpts, responseHandler, ct, onSearch));
        }

        await Task.WhenAll(searchTasks);

        if (results.IsEmpty && track.ArtistMaybeWrong && title)
        {
            var cond = new FileConditions(Config.necessaryCond);
            var infTrack = InferTrack(track.Title, new Track());
            cond.StrictTitle = infTrack.Title == track.Title;
            cond.StrictArtist = false;
            var opts = getSearchOptions(Math.Min(Config.searchTimeout, 5000), cond, Config.preferredCond);
            searchTasks.Add(Search($"{infTrack.Artist} {infTrack.Title}", opts, responseHandler, ct, onSearch));
        }

        if (Config.desperateSearch)
        {
            await Task.WhenAll(searchTasks);

            if (results.IsEmpty && !track.ArtistMaybeWrong)
            {
                if (artist && album && title)
                {
                    var cond = new FileConditions(Config.necessaryCond)
                    {
                        StrictTitle = true,
                        StrictAlbum = true
                    };
                    var opts = getSearchOptions(Math.Min(Config.searchTimeout, 5000), cond, Config.preferredCond);
                    searchTasks.Add(Search($"{track.Artist} {track.Album}", opts, responseHandler, ct, onSearch));
                }
                if (artist && title && track.Length != -1 && Config.necessaryCond.LengthTolerance != -1)
                {
                    var cond = new FileConditions(Config.necessaryCond)
                    {
                        LengthTolerance = -1,
                        StrictTitle = true,
                        StrictArtist = true
                    };
                    var opts = getSearchOptions(Math.Min(Config.searchTimeout, 5000), cond, Config.preferredCond);
                    searchTasks.Add(Search($"{track.Artist} {track.Title}", opts, responseHandler, ct, onSearch));
                }
            }

            await Task.WhenAll(searchTasks);

            if (results.IsEmpty)
            {
                var track2 = track.ArtistMaybeWrong ? InferTrack(track.Title, new Track()) : track;

                if (track.Album.Length > 3 && album)
                {
                    var cond = new FileConditions(Config.necessaryCond)
                    {
                        StrictAlbum = true,
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(Config.searchTimeout, 5000), cond, Config.preferredCond);
                    searchTasks.Add(Search($"{track.Album}", opts, responseHandler, ct, onSearch));
                }
                if (track2.Title.Length > 3 && artist)
                {
                    var cond = new FileConditions(Config.necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(Config.searchTimeout, 5000), cond, Config.preferredCond);
                    searchTasks.Add(Search($"{track2.Title}", opts, responseHandler, ct, onSearch));
                }
                if (track2.Artist.Length > 3 && title)
                {
                    var cond = new FileConditions(Config.necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(Config.searchTimeout, 5000), cond, Config.preferredCond);
                    searchTasks.Add(Search($"{track2.Artist}", opts, responseHandler, ct, onSearch));
                }
            }
        }

        await Task.WhenAll(searchTasks);
    }


    static async Task Search(string search, SearchOptions opts, Action<SearchResponse> rHandler, CancellationToken? ct = null, Action? onSearch = null)
    {
        await searchSemaphore.WaitAsync();
        try
        {
            search = CleanSearchString(search);
            var q = SearchQuery.FromText(search);
            onSearch?.Invoke();
            await client.SearchAsync(q, options: opts, cancellationToken: ct, responseHandler: rHandler);
        }
        catch (OperationCanceledException) { }
    }


    static async Task SearchAndPrintResults(List<Track> tracks)
    {
        foreach (var track in tracks)
        {
            Console.WriteLine($"Results for {track}:");

            SearchOptions getSearchOptions(int timeout, FileConditions necCond, FileConditions prfCond)
            {
                return new SearchOptions(
                    minimumResponseFileCount: 1,
                    minimumPeerUploadSpeed: 1,
                    searchTimeout: Config.searchTimeout,
                    removeSingleCharacterSearchTerms: Config.removeSingleCharacterSearchTerms,
                    responseFilter: (response) =>
                    {
                        return response.UploadSpeed > 0 && necCond.BannedUsersSatisfies(response);
                    },
                    fileFilter: (file) =>
                    {
                        return Utils.IsMusicFile(file.Filename) && (necCond.FileSatisfies(file, track, null) || Config.PrintResultsFull);
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

            await RunSearches(track, results, getSearchOptions, responseHandler);

            if (Config.DoNotDownload && results.IsEmpty)
            {
                WriteLine($"No results", ConsoleColor.Yellow);
            }
            else
            {
                var orderedResults = OrderedResults(results, track, true);
                int count = 0;
                Console.WriteLine();
                foreach (var (response, file) in orderedResults)
                {
                    Console.WriteLine(DisplayString(track, file, response,
                        Config.PrintResultsFull ? Config.necessaryCond : null, Config.PrintResultsFull ? Config.preferredCond : null,
                        fullpath: Config.PrintResultsFull, infoFirst: true, showSpeed: Config.PrintResultsFull));
                    count += 1;
                }
                WriteLine($"Total: {count}\n", ConsoleColor.Yellow);
            }

            Console.WriteLine();
        }
    }


    static string GetSearchString(Track track)
    {
        if (track.Title.Length > 0)
            return (track.Artist + " " + track.Title).Trim();
        else if (track.Album.Length > 0)
            return (track.Artist + " " + track.Album).Trim();
        return track.Artist.Trim();
    }


    static string CleanSearchString(string str)
    {
        string old;
        if (!Config.noRemoveSpecialChars)
        {
            old = str;
            str = str.ReplaceSpecialChars(" ").Trim().RemoveConsecutiveWs();
            if (str.Length == 0) str = old;
        }
        foreach (var banned in bannedTerms)
        {
            string b1 = banned;
            string b2 = banned.Replace(" ", "-");
            string b3 = banned.Replace(" ", "_");
            string b4 = banned.Replace(" ", "");
            foreach (var s in new string[] { b1, b2, b3, b4 })
                str = str.Replace(s, string.Concat("*", s.AsSpan(1)), StringComparison.OrdinalIgnoreCase);
        }

        return str.Trim();
    }


    static Track InferTrack(string filename, Track defaultTrack)
    {
        var t = new Track(defaultTrack);
        filename = Utils.GetFileNameWithoutExtSlsk(filename).Replace(" — ", " - ").Replace('_', ' ').Trim().RemoveConsecutiveWs();

        var trackNumStart = new Regex(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)");
        //var trackNumMiddle = new Regex(@"\s+-\s+(\d{2,3})(?: -|\.|)\s+|\s+-(\d{2,3})-\s+");
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


    static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, Track track, ProgressBar progress, CancellationTokenSource? searchCts = null)
    {
        if (Config.DoNotDownload)
            throw new Exception();

        await WaitForLogin();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        string origPath = filePath;
        filePath += ".incomplete";

        var transferOptions = new TransferOptions(
            stateChanged: (state) =>
            {
                if (downloads.TryGetValue(file.Filename, out var x))
                    x.transfer = state.Transfer;
            },
            progressUpdated: (progress) =>
            {
                if (downloads.TryGetValue(file.Filename, out var x))
                    x.bytesTransferred = progress.PreviousBytesTransferred;
            }
        );

        try
        {
            using var cts = new CancellationTokenSource();
            using var outputStream = new FileStream(filePath, FileMode.Create);
            var wrapper = new DownloadWrapper(origPath, response, file, track, cts, progress);
            downloads.TryAdd(file.Filename, wrapper);

            // Attempt to make it resume downloads after a network interruption.
            // Does not work: The resumed download will be queued until it goes stale.
            // The host (slskd) reports that "Another upload to {user} is already in progress"
            // when attempting to resume. Must wait until timeout, which can take minutes.

            int maxRetries = 3;
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await client.DownloadAsync(response.Username, file.Filename,
                        () => Task.FromResult((Stream)outputStream),
                        file.Size, startOffset: outputStream.Position,
                        options: transferOptions, cancellationToken: cts.Token);

                    break;
                }
                catch (SoulseekClientException)
                {
                    retryCount++;

                    if (retryCount >= maxRetries || IsConnectedAndLoggedIn())
                        throw;

                    await WaitForLogin();
                }
            }
        }
        catch
        {
            if (File.Exists(filePath))
                try { File.Delete(filePath); } catch { }
            downloads.TryRemove(file.Filename, out var d);
            if (d != null)
                lock (d) { d.UpdateText(); }
            throw;
        }

        try { searchCts?.Cancel(); }
        catch { }

        try { Utils.Move(filePath, origPath); }
        catch (IOException) { WriteLine($"Failed to rename .incomplete file", ConsoleColor.DarkYellow, true); }

        downloads.TryRemove(file.Filename, out var x);
        if (x != null)
        {
            lock (x)
            {
                x.success = true;
                x.UpdateText();
            }
        }
    }


    public class SearchAndDownloadException : Exception
    {
        public FailureReason reason;
        public SearchAndDownloadException(FailureReason reason, string text = "") : base(text) { this.reason = reason; }
    }


    class DownloadWrapper
    {
        public string savePath;
        public string displayText = "";
        public int downloadRotatingBarState = 0;
        public Soulseek.File file;
        public Transfer? transfer;
        public SearchResponse response;
        public ProgressBar progress;
        public Track track;
        public long bytesTransferred = 0;
        public bool stalled = false;
        public bool queued = false;
        public bool success = false;
        public CancellationTokenSource cts;
        public DateTime startTime = DateTime.Now;
        public DateTime lastChangeTime = DateTime.Now;

        TransferStates? prevTransferState = null;
        long prevBytesTransferred = 0;
        bool updatedTextDownload = false;
        bool updatedTextSuccess = false;
        readonly char[] bars = { '|', '/', '—', '\\' };

        public DownloadWrapper(string savePath, SearchResponse response, Soulseek.File file, Track track, CancellationTokenSource cts, ProgressBar progress)
        {
            this.savePath = savePath;
            this.response = response;
            this.file = file;
            this.cts = cts;
            this.track = track;
            this.progress = progress;
            this.displayText = DisplayString(track, file, response);

            RefreshOrPrint(progress, 0, "Initialize: " + displayText, true);
            RefreshOrPrint(progress, 0, displayText, false);
        }

        public void UpdateText()
        {
            downloadRotatingBarState++;
            downloadRotatingBarState %= bars.Length;
            float? percentage = bytesTransferred / (float)file.Size;
            queued = (transfer?.State & TransferStates.Queued) != 0;
            string bar;
            string state;
            bool downloading = false;

            if (stalled)
            {
                state = "Stalled";
                bar = "";
            }
            else if (transfer != null)
            {
                if (queued)
                    state = "Queued";
                else if ((transfer.State & TransferStates.Initializing) != 0)
                    state = "Initialize";
                else if ((transfer.State & TransferStates.Completed) != 0)
                {
                    var flag = transfer.State & (TransferStates.Succeeded | TransferStates.Cancelled 
                        | TransferStates.TimedOut | TransferStates.Errored | TransferStates.Rejected 
                        | TransferStates.Aborted);
                    state = flag.ToString();

                    if (flag == TransferStates.Succeeded)
                        success = true;
                }
                else
                {
                    state = transfer.State.ToString();
                    if ((transfer.State & TransferStates.InProgress) != 0)
                        downloading = true;
                }

                bar = success ? "" : bars[downloadRotatingBarState] + " ";
            }
            else
            {
                state = "NullState";
                bar = "";
            }

            string txt = $"{bar}{state}:".PadRight(14) + $" {displayText}";
            bool needSimplePrintUpdate = (downloading && !updatedTextDownload) || (success && !updatedTextSuccess);
            updatedTextDownload |= downloading;
            updatedTextSuccess |= success;

            Console.ResetColor();
            RefreshOrPrint(progress, (int)((percentage ?? 0) * 100), txt, needSimplePrintUpdate, needSimplePrintUpdate);

        }

        public DateTime UpdateLastChangeTime(bool updateAllFromThisUser = true, bool forceChanged = false)
        {
            bool changed = prevTransferState != transfer?.State || prevBytesTransferred != bytesTransferred;
            if (changed || forceChanged)
            {
                lastChangeTime = DateTime.Now;
                stalled = false;
                if (updateAllFromThisUser)
                {
                    foreach (var (_, dl) in downloads)
                    {
                        if (dl != this && dl.response.Username == response.Username)
                            dl.UpdateLastChangeTime(updateAllFromThisUser: false, forceChanged: true);
                    }
                }
            }
            prevTransferState = transfer?.State;
            prevBytesTransferred = bytesTransferred;
            return lastChangeTime;
        }
    }


    class SearchInfo
    {
        public ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results;
        public ProgressBar progress;

        public SearchInfo(ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results, ProgressBar progress)
        {
            this.results = results;
            this.progress = progress;
        }
    }

}


