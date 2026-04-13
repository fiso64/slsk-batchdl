using Soulseek;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Jobs;
using Models;
using Enums;
using Utilities;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;
using Settings;


public partial class Searcher
{
    private readonly ISoulseekClient client;
    private readonly ISearchRegistry searchRegistry;
    private readonly IUserStats userStats;
    private readonly IProgressReporter progressReporter;
    private readonly RateLimitedSemaphore rateSemaphore;
    private readonly SemaphoreSlim concurrencySemaphore;

    public Searcher(ISoulseekClient client,
                    ISearchRegistry searchRegistry,
                    IUserStats userStats,
                    IProgressReporter progressReporter,
                    int searchesPerTime, int searchRenewTime, int concurrentSearches = 2)
    {
        this.client = client;
        this.searchRegistry = searchRegistry;
        this.userStats = userStats;
        this.progressReporter = progressReporter;
        rateSemaphore = new RateLimitedSemaphore(searchesPerTime, TimeSpan.FromSeconds(searchRenewTime));
        concurrencySemaphore = new SemaphoreSlim(concurrentSearches);
    }


    // ── song search ─────────────────────────────────────────────────────────

    // Populates song.Candidates (ordered best-first).
    // onFastSearchCandidate: called when a highly-ranked candidate is found early,
    // before the full search completes, so the engine can start a provisional download.
    public async Task SearchSong(SongJob song, DownloadSettings config, ResponseData responseData,
        CancellationToken ct, Action? onSearch = null,
        Action<FileCandidate>? onFastSearchCandidate = null)
    {
        var results = new SlDictionary();
        searchRegistry.Searches.TryAdd(song, new SearchInfo(results, null));

        void responseHandler(SearchResponse r)
        {
            if (r.Files.Count == 0) return;
            responseData.lockedFilesCount += r.LockedFileCount;

            foreach (var file in r.Files)
                results.TryAdd(r.Username + '\\' + file.Filename, (r, file));

            if (onFastSearchCandidate != null && config.Search.FastSearch
                && userStats.UserSuccessCounts.GetValueOrDefault(r.Username, 0) > config.Search.DownrankOn)
            {
                var f = r.Files.First();
                var candidate = new FileCandidate(r, f);
                if (r.HasFreeUploadSlot && r.UploadSpeed / 1024.0 / 1024.0 >= config.Search.FastSearchMinUpSpeed
                    && FileConditions.BracketCheck(song.Query, InferSongQuery(f.Filename, song.Query))
                    && config.Search.PreferredCond.FileSatisfies(f, song.Query, r))
                {
                    onFastSearchCandidate(candidate);
                }
            }
        }

        SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf) =>
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                searchTimeout: timeout,
                removeSingleCharacterSearchTerms: config.Search.RemoveSingleCharSearchTerms,
                responseFilter: r => r.UploadSpeed > 0 && nec.BannedUsersSatisfies(r),
                fileFilter: f => nec.FileSatisfies(f, song.Query, null));

        progressReporter.ReportSearchStart(song);
        await concurrencySemaphore.WaitAsync(ct);
        try { await RunSearches(song.Query, results, getOpts, responseHandler, config, ct, onSearch); }
        finally { concurrencySemaphore.Release(); }

        searchRegistry.Searches.TryRemove(song, out _);

        Logger.Debug($"{results.Count} results found: {song}");
        progressReporter.ReportSearchResult(song, results.Count);

        if (!results.IsEmpty)
        {
            Logger.Debug(Printing.FormatList(results,
                format: r => $"{r.Value.Item1.Username}: {r.Value.Item2.Filename}"));
        }

        responseData.lockedFilesCount += results.Values
            .Select(x => x.Item1).Distinct()
            .Sum(r => 0); // already counted per-response above

        var ordered = ResultSorter.OrderedResults(results, song.Query, config, userStats.UserSuccessCounts, useInfer: true);
        song.Candidates = [.. ordered.Select(x => new FileCandidate(x.response, x.file))];
    }


    // ── album search ─────────────────────────────────────────────────────────

    // Populates job.Results with candidate AlbumFolders found on the network.
    public async Task SearchAlbum(AlbumJob job, DownloadSettings config, ResponseData responseData, CancellationToken ct)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        // For folder-matching (Album), use only the album name. For the network search keyword
        // (Title), fall back to the song-title hint when no album name is known.
        var query = new SongQuery
        {
            Artist = job.Query.Artist,
            Title = job.Query.Album.Length > 0 ? job.Query.Album : job.Query.SearchHint,
            Album = job.Query.Album,
            ArtistMaybeWrong = job.Query.ArtistMaybeWrong,
            IsDirectLink = job.Query.IsDirectLink,
        };

        SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf) =>
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: config.Search.RemoveSingleCharSearchTerms,
                searchTimeout: timeout,
                responseFilter: r => r.UploadSpeed > 0 && nec.BannedUsersSatisfies(r),
                fileFilter: f => !Utils.IsMusicFile(f.Filename) || nec.FileSatisfies(f, query, null));

        void handler(SlResponse r)
        {
            responseData.lockedFilesCount += r.LockedFileCount;
            if (r.Files.Count > 0)
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + '\\' + file.Filename, (r, file));
        }

        using var cts = new CancellationTokenSource();
        await concurrencySemaphore.WaitAsync(cts.Token);
        try { await RunSearches(query, results, getOpts, handler, config, cts.Token); }
        finally { concurrencySemaphore.Release(); }

        job.Results = BuildAlbumFolders(results, job.Query, config);  // was job.FoundFolders
    }

    // Populates job.Results from a direct slsk:// link (no network search).
    public async Task SearchDirectLinkAlbum(AlbumJob job, CancellationToken ct)
    {
        var parts = job.Query.URI["slsk://".Length..].Split('/', 2);
        var username = parts[0];
        var directory = parts[1].TrimEnd('/').Replace('/', '\\');

        var rawFiles = await GetAllFilesInFolder(username, directory, ct);
        var response = new SearchResponse(username, -1, false, -1, -1, null);

        var files = new List<SongJob>();
        foreach (var (dir, file) in rawFiles)
        {
            var fullPath = dir.TrimEnd('\\') + '\\' + file.Filename;
            var slFile = new Soulseek.File(file.Code, fullPath, file.Size, file.Extension);
            var candidate = new FileCandidate(response, slFile);
            var info = InferSongQuery(file.Filename, new SongQuery { Artist = job.Query.Artist, Album = job.Query.Album });
            files.Add(new SongJob(info) { ResolvedTarget = candidate });
        }

        if (files.Count > 0)
            job.Results = new List<AlbumFolder> { new AlbumFolder(username, directory, files) };
        else
            job.Results = new List<AlbumFolder>();
    }


    // ── aggregate search ─────────────────────────────────────────────────────

    // Populates job.Songs: one SongJob per distinct inferred track version found.
    public async Task SearchAggregate(AggregateJob job, DownloadSettings config, ResponseData responseData, CancellationToken ct)
    {
        var results = new SlDictionary();

        SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf) =>
            new(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: config.Search.RemoveSingleCharSearchTerms,
                searchTimeout: timeout,
                responseFilter: r => r.UploadSpeed > 0 && nec.BannedUsersSatisfies(r),
                fileFilter: f => nec.FileSatisfies(f, job.Query, null));

        void handler(SlResponse r)
        {
            responseData.lockedFilesCount += r.LockedFileCount;
            if (r.Files.Count > 0)
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));
        }

        using var cts = new CancellationTokenSource();
        await concurrencySemaphore.WaitAsync(cts.Token);
        try { await RunSearches(job.Query, results, getOpts, handler, config, cts.Token); }
        finally { concurrencySemaphore.Release(); }

        var equivalentFiles = EquivalentFiles(job.Query, results.Select(x => x.Value), config)
            .Select(x => (x.query, ResultSorter.OrderedResults(x.candidates.Select(c => (c.Response, c.File)), x.query, config, userStats.UserSuccessCounts, false, false, false)))
            .ToList();

        if (!config.Search.Relax)
        {
            equivalentFiles = equivalentFiles
                .Where(x => FileConditions.StrictString(x.query.Title, job.Query.Title, ignoreCase: true)
                    && (FileConditions.StrictString(x.query.Artist, job.Query.Artist, ignoreCase: true, boundarySkipWs: false)
                        || FileConditions.StrictString(x.query.Title, job.Query.Artist, ignoreCase: true, boundarySkipWs: false)
                            && x.query.Title.ContainsInBrackets(job.Query.Artist, ignoreCase: true)))
                .ToList();
        }

        job.Songs = equivalentFiles.Select(x =>
        {
            var s = new SongJob(x.query);
            s.Candidates = x.Item2.Select(r => new FileCandidate(r.response, r.file)).ToList();
            return s;
        }).ToList();
    }

    // Returns new AlbumJobs (one per distinct album version found on the network).
    public async Task<List<AlbumJob>> SearchAggregateAlbum(AlbumAggregateJob job, DownloadSettings config, ResponseData responseData, CancellationToken ct)
    {
        var tempJob = new AlbumJob(job.Query);
        await SearchAlbum(tempJob, config, responseData, ct);
        var albums = tempJob.Results;

        int maxDiff = config.Search.AggregateLengthTol;

        bool LengthsAreSimilar(int[] s1, int[] s2)
        {
            if (s1.Length != s2.Length) return false;
            for (int i = 0; i < s1.Length; i++)
                if (Math.Abs(s1[i] - s2[i]) > maxDiff) return false;
            return true;
        }

        // Group albums by similar track lengths (and single-track disambiguation).
        var byLength = new List<(int[] lengths, List<AlbumFolder> versions, HashSet<string> users)>();

        foreach (var folder in albums)
        {
            if (folder.Files.Count == 0) continue;
            var sortedLengths = folder.Files
                .Where(f => !f.IsNotAudio)
                .Select(f => f.ResolvedTarget!.File.Length ?? -1)
                .OrderBy(x => x).ToArray();

            bool matched = false;
            for (int i = 0; i < byLength.Count; i++)
            {
                if (!LengthsAreSimilar(sortedLengths, byLength[i].lengths)) continue;

                if (sortedLengths.Length == 1)
                {
                    var rep1 = byLength[i].versions[0].Files.FirstOrDefault(f => !f.IsNotAudio);
                    var rep2 = folder.Files.FirstOrDefault(f => !f.IsNotAudio);
                    if (rep1 != null && rep2 != null)
                    {
                        var q1 = InferSongQuery(rep1.ResolvedTarget!.Filename, new SongQuery());
                        var q2 = InferSongQuery(rep2.ResolvedTarget!.Filename, new SongQuery());
                        if (!(q2.Artist.ContainsIgnoreCase(q1.Artist) || q1.Artist.ContainsIgnoreCase(q2.Artist))
                            || !(q2.Title.ContainsIgnoreCase(q1.Title) || q1.Title.ContainsIgnoreCase(q2.Title)))
                            continue;
                    }
                }

                byLength[i].versions.Add(folder);
                byLength[i].users.Add(folder.Username);
                matched = true;
                break;
            }

            if (!matched)
                byLength.Add((sortedLengths, new List<AlbumFolder> { folder }, new HashSet<string> { folder.Username }));
        }

        return byLength
            .Where(x => x.users.Count >= config.Search.MinSharesAggregate)
            .OrderByDescending(x => x.users.Count)
            .Select(x =>
            {
                var newJob = new AlbumJob(job.Query);
                newJob.Results = x.versions;
                return newJob;
            })
            .ToList();
    }


    // ── folder browse ────────────────────────────────────────────────────────

    public async Task<List<(string dir, SlFile file)>> GetAllFilesInFolder(string user, string folderPrefix, CancellationToken? ct = null)
    {
        var res = new List<(string dir, SlFile file)>();
        folderPrefix = folderPrefix.TrimEnd('\\') + '\\';
        var userFileList = await client.BrowseAsync(user, new BrowseOptions(), ct);
        foreach (var dir in userFileList.Directories)
        {
            string dirname = dir.Name.TrimEnd('\\') + '\\';
            if (dirname.StartsWith(folderPrefix))
                res.AddRange(dir.Files.Select(x => (dir.Name, x)));
        }
        return res;
    }

    // Appends any new files found in the remote folder to folder.Files.
    // Returns the number of newly added files.
    public async Task<int> CompleteFolder(AlbumFolder folder, CancellationToken? ct = null)
    {
        int newFiles = 0;
        try
        {
            List<(string dir, SlFile file)> allFiles;
            try
            {
                allFiles = await GetAllFilesInFolder(folder.Username, folder.FolderPath, ct);
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception e) { Logger.Error($"Error getting all files in '{folder.FolderPath}': {e}"); return 0; }

            var existing = folder.Files.Select(f => f.ResolvedTarget!.Filename).ToHashSet();
            var firstInfo = folder.Files.FirstOrDefault(f => !f.IsNotAudio)?.Query ?? new SongQuery();
            var firstResp = folder.Files.FirstOrDefault()?.ResolvedTarget?.Response
                            ?? new SearchResponse(folder.Username, -1, false, -1, -1, null);

            foreach (var (dir, file) in allFiles)
            {
                // file.Filename from BrowseAsync is already the full path (same as in search
                // results), not just the basename — do not prepend dir again.
                if (existing.Contains(file.Filename)) continue;

                newFiles++;
                var slFile = new SlFile(file.Code, file.Filename, file.Size, file.Extension, file.Attributes);
                var candidate = new FileCandidate(firstResp, slFile);
                var info = InferSongQuery(file.Filename, new SongQuery { Artist = firstInfo.Artist, Album = firstInfo.Album });
                folder.Files.Add(new SongJob(info) { ResolvedTarget = candidate });
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error completing folder: {ex}");
        }
        return newFiles;
    }


    // ── print-mode helper ────────────────────────────────────────────────────

    // Called when config.PrintResults = true.
    public async Task SearchAndPrintResults(IEnumerable<SongJob> songs, DownloadSettings config)
    {
        foreach (var song in songs)
        {
            if (!config.NonVerbosePrint)
                Console.WriteLine($"Results for {song}:");

            var results = new SlDictionary();

            SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf) =>
                new SearchOptions(
                    minimumResponseFileCount: 1,
                    minimumPeerUploadSpeed: 1,
                    searchTimeout: config.Search.SearchTimeout,
                    removeSingleCharacterSearchTerms: config.Search.RemoveSingleCharSearchTerms,
                    responseFilter: r => r.UploadSpeed > 0 && nec.BannedUsersSatisfies(r),
                    fileFilter: f => nec.FileSatisfies(f, song.Query, null) || config.PrintFull);

            void responseHandler(SearchResponse r)
            {
                if (r.Files.Count > 0)
                    foreach (var file in r.Files)
                        results.TryAdd(r.Username + '\\' + file.Filename, (r, file));
            }

            await RunSearches(song.Query, results, getOpts, responseHandler, config);

            if (config.DoNotDownload && results.IsEmpty)
            {
                if (config.PrintOption.HasFlag(PrintOption.Json))
                    JsonPrinter.PrintTrackResultJson(song.Query, []);
                if (!config.NonVerbosePrint)
                    Printing.WriteLine("No results", ConsoleColor.Yellow);
            }
            else
            {
                var orderedResults = ResultSorter.OrderedResults(results, song.Query, config, userStats.UserSuccessCounts, useInfer: true);

                if (!config.NonVerbosePrint)
                    Console.WriteLine();

                if (config.PrintOption.HasFlag(PrintOption.Json))
                    JsonPrinter.PrintTrackResultJson(song.Query, orderedResults, config.PrintOption.HasFlag(PrintOption.Full));
                else if (config.PrintOption.HasFlag(PrintOption.Link))
                    Printing.PrintLink(orderedResults.First().response.Username, orderedResults.First().file.Filename);
                else
                    Printing.PrintTrackResults(orderedResults, song.Query, config.PrintFull, config.Search.NecessaryCond, config.Search.PreferredCond);
            }

            if (!config.NonVerbosePrint)
                Console.WriteLine();
        }
    }


    // ── query inference ───────────────────────────────────────────────────────

    [GeneratedRegex(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)")]
    private static partial Regex TrackNumStartRegex();

    [GeneratedRegex(@"(?<= - )((\d-)?\d{2,3}|\d{2,3}\.?)\s+")]
    private static partial Regex TrackNumMiddleRegex();

    [GeneratedRegex(@"\s+-(\d{2,3})-\s+")]
    private static partial Regex TrackNumMiddleAltRegex();

    [GeneratedRegex(@"-\s*<<tracknum>>\s*-")]
    private static partial Regex TrackNumPlaceholderRegex();

    public static SongQuery InferSongQuery(string filename, SongQuery defaultQuery)
    {
        string artist = defaultQuery.Artist;
        string title = defaultQuery.Title;
        string album = defaultQuery.Album;
        bool artistMaybeWrong = defaultQuery.ArtistMaybeWrong;

        filename = Utils.GetFileNameWithoutExtSlsk(filename);

        // Special case: "(NN) [Artist] Title"
        if (filename.Length >= 6 && filename[0] == '(' && char.IsDigit(filename[1]) && char.IsDigit(filename[2])
            && filename[3] == ')' && filename[4] == ' ' && filename[5] == '[')
        {
            int close = filename.IndexOf(']', 6);
            if (close > 6)
            {
                int titleStart = close + 1;
                if (titleStart < filename.Length && filename[titleStart] == ' ') titleStart++;
                if (titleStart < filename.Length)
                {
                    artist = filename[6..close];
                    title = filename[titleStart..];
                    return new SongQuery(defaultQuery) { Artist = artist.RemoveFt(), Title = title.RemoveFt(), ArtistMaybeWrong = false };
                }
            }
        }

        filename = filename.Replace(" — ", " - ").Replace('_', ' ').Trim().RemoveConsecutiveWs();

        if (TrackNumStartRegex().IsMatch(filename))
        {
            filename = TrackNumStartRegex().Replace(filename, "", 1).Trim();
            if (filename.StartsWith("- ")) filename = filename[2..].Trim();
        }
        else
        {
            var reg = TrackNumMiddleRegex().IsMatch(filename) ? TrackNumMiddleRegex()
                    : TrackNumMiddleAltRegex().IsMatch(filename) ? TrackNumMiddleAltRegex() : null;
            if (reg != null && !reg.IsMatch(defaultQuery.ToString(noInfo: true)))
            {
                filename = reg.Replace(filename, "<<tracknum>>", 1).Trim();
                filename = TrackNumPlaceholderRegex().Replace(filename, "-");
                filename = filename.Replace("<<tracknum>>", "");
            }
        }

        string aname = artist.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        string tname = title.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        string alname = album.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        string fname = filename.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveConsecutiveWs().Trim();

        bool maybeRemix = aname.Length > 0 && Regex.IsMatch(fname, @$"\({Regex.Escape(aname)} .+\)", RegexOptions.IgnoreCase);
        string[] parts = fname.Split([" - "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] realParts = filename.Split([" - "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != realParts.Length) realParts = parts;

        if (parts.Length == 1)
        {
            if (maybeRemix) artistMaybeWrong = true;
            title = parts[0];
        }
        else if (parts.Length == 2)
        {
            artist = realParts[0];
            title = realParts[1];
            if (maybeRemix)
                artistMaybeWrong = true;
            else if (!parts[0].ContainsIgnoreCase(aname) || !parts[1].ContainsIgnoreCase(tname))
                artistMaybeWrong = true;
        }
        else if (parts.Length == 3)
        {
            bool hasTitle = tname.Length > 0 && parts[2].ContainsIgnoreCase(tname);
            if (hasTitle) title = realParts[2];

            int artistPos = -1, albumPos = -1;
            if (aname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(aname)) artistPos = 0;
                else if (parts[1].ContainsIgnoreCase(aname)) artistPos = 1;
                else artistMaybeWrong = true;
            }
            if (alname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(alname)) albumPos = 0;
                else if (parts[1].ContainsIgnoreCase(alname)) albumPos = 1;
            }
            if (artistPos >= 0 && artistPos == albumPos) { artistPos = 0; albumPos = 1; }
            if (artistPos == -1 && maybeRemix) { artistMaybeWrong = true; artistPos = 0; albumPos = 1; }

            if (artistPos == -1 && albumPos == -1)
            { artistMaybeWrong = true; artist = realParts[0] + " - " + realParts[1]; }
            else if (artistPos >= 0)
            { artist = parts[artistPos]; }

            title = parts[2];
        }
        else
        {
            if (aname.Length > 0)
            {
                var s = parts.Select((p, i) => (p, i)).Where(x => x.p.ContainsIgnoreCase(aname));
                if (s.Any())
                {
                    int pos = s.MinBy(x => Math.Abs(x.p.Length - aname.Length)).i;
                    artist = parts[pos];
                }
            }
            if (tname.Length > 0)
            {
                int artistPos2 = artist == defaultQuery.Artist ? -1 :
                    parts.Select((p, i) => (p, i)).FirstOrDefault(x => x.p == artist).i;
                var ss = parts.Select((p, i) => (p, i)).Where(x => x.i != artistPos2 && x.p.ContainsIgnoreCase(tname));
                if (ss.Any())
                    title = parts[ss.MinBy(x => Math.Abs(x.p.Length - tname.Length)).i];
            }
        }

        if (title.Length == 0)
        {
            title = fname;
            artistMaybeWrong = true;
        }
        else if (artist.Length > 0 && !title.ContainsIgnoreCase(defaultQuery.Title) && !artist.ContainsIgnoreCase(defaultQuery.Artist))
        {
            string[] x = [artist, album, title];
            var perm = (0, 1, 2);
            (int, int, int)[] permutations = [(0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0)];
            foreach (var p in permutations)
            {
                if (x[p.Item1].ContainsIgnoreCase(defaultQuery.Artist) && x[p.Item3].ContainsIgnoreCase(defaultQuery.Title))
                { perm = p; break; }
            }
            artist = x[perm.Item1];
            album = x[perm.Item2];
            title = x[perm.Item3];
        }

        return new SongQuery(defaultQuery)
        {
            Artist = artist.RemoveFt().Trim(),
            Title = title.RemoveFt().Trim(),
            Album = album.Trim(),
            ArtistMaybeWrong = artistMaybeWrong,
        };
    }

    public static IEnumerable<(SongQuery query, IEnumerable<FileCandidate> candidates)> EquivalentFiles(
        SongQuery query,
        IEnumerable<(SlResponse, SlFile)> fileResponses,
        DownloadSettings config,
        int minShares = -1)
    {
        if (minShares == -1) minShares = config.Search.MinSharesAggregate;

        SongQuery infer((SearchResponse r, Soulseek.File f) x)
        {
            var q = InferSongQuery(x.f.Filename, query);
            return new SongQuery(q) { Length = x.f.Length ?? -1 };
        }

        return fileResponses
            .GroupBy(infer, new SongQueryComparer(ignoreCase: true, config.Search.AggregateLengthTol))
            .Select(g => (g, g.Select(x => x.Item1.Username).Distinct().Count()))
            .Where(x => x.Item2 >= minShares)
            .OrderByDescending(x => x.Item2)
            .Select(x =>
            {
                var grp = x.g;
                var inferQ = grp.Key;
                // fill in length from results if unknown
                if (inferQ.Length == -1)
                {
                    int len = grp.FirstOrDefault(y => y.Item2.Length != null).Item2?.Length ?? -1;
                    inferQ = new SongQuery(inferQ) { Length = len };
                }
                return (inferQ, grp.Select(y => new FileCandidate(y.Item1, y.Item2)));
            });
    }

    public static bool AlbumsAreSimilar(AlbumFolder f1, AlbumFolder f2, int[]? f1SortedLengths = null, int tolerance = 3)
    {
        var audio1 = f1.Files.Where(f => !f.IsNotAudio).ToList();
        var audio2 = f2.Files.Where(f => !f.IsNotAudio).ToList();
        if (audio1.Count != audio2.Count) return false;

        f1SortedLengths ??= audio1.Select(f => f.ResolvedTarget!.File.Length ?? -1).OrderBy(x => x).ToArray();
        var s2 = audio2.Select(f => f.ResolvedTarget!.File.Length ?? -1).OrderBy(x => x).ToArray();

        for (int i = 0; i < f1SortedLengths.Length; i++)
            if (Math.Abs(f1SortedLengths[i] - s2[i]) > tolerance) return false;

        return true;
    }


    // ── internal search plumbing ──────────────────────────────────────────────

    public async Task RunSearches(SongQuery query, SlDictionary results,
        Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions,
        Action<SearchResponse> responseHandler, DownloadSettings config,
        CancellationToken? ct = null, Action? onSearch = null)
    {
        bool artist = query.Artist.Length > 0;
        bool title = query.Title.Length > 0;
        bool album = query.Album.Length > 0;

        string search = GetSearchString(query, isAlbum: false);
        var searchTasks = new List<Task>();

        var defaultOpts = getSearchOptions(config.Search.SearchTimeout, config.Search.NecessaryCond, config.Search.PreferredCond);
        searchTasks.Add(DoSearch(search, defaultOpts, responseHandler, config, ct, onSearch));

        if (search.RemoveDiacriticsIfExist(out string noDiacr) && !query.ArtistMaybeWrong)
            searchTasks.Add(DoSearch(noDiacr, defaultOpts, responseHandler, config, ct, onSearch));

        await Task.WhenAll(searchTasks);

        if (results.IsEmpty && query.ArtistMaybeWrong && title)
        {
            var inferred = InferSongQuery(query.Title, new SongQuery());
            var cond = new FileConditions(config.Search.NecessaryCond) { StrictTitle = inferred.Title == query.Title, StrictArtist = false };
            var opts = getSearchOptions(Math.Min(config.Search.SearchTimeout, 5000), cond, config.Search.PreferredCond);
            searchTasks.Add(DoSearch($"{inferred.Artist} {inferred.Title}", opts, responseHandler, config, ct, onSearch));
        }

        if (config.Search.DesperateSearch)
        {
            await Task.WhenAll(searchTasks);

            if (results.IsEmpty && !query.ArtistMaybeWrong)
            {
                if (artist && album && title)
                {
                    var cond = new FileConditions(config.Search.NecessaryCond) { StrictTitle = true, StrictAlbum = true };
                    searchTasks.Add(DoSearch($"{query.Artist} {query.Album}",
                        getSearchOptions(Math.Min(config.Search.SearchTimeout, 5000), cond, config.Search.PreferredCond),
                        responseHandler, config, ct, onSearch));
                }
                if (artist && title && query.Length != -1 && config.Search.NecessaryCond.LengthTolerance != -1)
                {
                    var cond = new FileConditions(config.Search.NecessaryCond) { LengthTolerance = -1, StrictTitle = true, StrictArtist = true };
                    searchTasks.Add(DoSearch($"{query.Artist} {query.Title}",
                        getSearchOptions(Math.Min(config.Search.SearchTimeout, 5000), cond, config.Search.PreferredCond),
                        responseHandler, config, ct, onSearch));
                }
            }

            await Task.WhenAll(searchTasks);

            if (results.IsEmpty)
            {
                var q2 = query.ArtistMaybeWrong ? InferSongQuery(query.Title, new SongQuery()) : query;

                if (query.Album.Length > 3 && album)
                {
                    var cond = new FileConditions(config.Search.NecessaryCond)
                    { StrictAlbum = true, StrictTitle = !query.ArtistMaybeWrong, StrictArtist = !query.ArtistMaybeWrong, LengthTolerance = -1 };
                    searchTasks.Add(DoSearch(query.Album, getSearchOptions(Math.Min(config.Search.SearchTimeout, 5000), cond, config.Search.PreferredCond),
                        responseHandler, config, ct, onSearch));
                }
                if (q2.Title.Length > 3 && artist)
                {
                    var cond = new FileConditions(config.Search.NecessaryCond)
                    { StrictTitle = !query.ArtistMaybeWrong, StrictArtist = !query.ArtistMaybeWrong, LengthTolerance = -1 };
                    searchTasks.Add(DoSearch(q2.Title, getSearchOptions(Math.Min(config.Search.SearchTimeout, 5000), cond, config.Search.PreferredCond),
                        responseHandler, config, ct, onSearch));
                }
                if (q2.Artist.Length > 3 && title)
                {
                    var cond = new FileConditions(config.Search.NecessaryCond)
                    { StrictTitle = !query.ArtistMaybeWrong, StrictArtist = !query.ArtistMaybeWrong, LengthTolerance = -1 };
                    searchTasks.Add(DoSearch(q2.Artist, getSearchOptions(Math.Min(config.Search.SearchTimeout, 5000), cond, config.Search.PreferredCond),
                        responseHandler, config, ct, onSearch));
                }
            }
        }

        await Task.WhenAll(searchTasks);
    }

    private async Task DoSearch(string search, SearchOptions opts, Action<SearchResponse> rHandler,
        DownloadSettings config, CancellationToken? ct = null, Action? onSearch = null)
    {
        await rateSemaphore.WaitAsync();
        try
        {
            search = CleanSearchString(search, !config.Search.NoRemoveSpecialChars);
            var q = SearchQuery.FromText(search);
            onSearch?.Invoke();
            await client.SearchAsync(q, options: opts, cancellationToken: ct, responseHandler: rHandler);
        }
        catch (OperationCanceledException) { }
    }

    private static string GetSearchString(SongQuery query, bool isAlbum)
    {
        if (isAlbum)
        {
            if (query.Album.Length > 0)
                return (query.Artist + " " + query.Album).Trim();
            if (query.Title.Length > 0)
                return (query.Artist + " " + query.Title).Trim();
            return query.Artist.Trim();
        }
        else
        {
            if (query.Title.Length > 0)
                return (query.Artist + " " + query.Title).Trim();
            else if (query.Album.Length > 0)
                return (query.Artist + " " + query.Album).Trim();
            return query.Artist.Trim();
        }
    }

    private static string CleanSearchString(string str, bool removeSpecialChars)
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
                str = str.Replace(banned[0], string.Concat("*", banned[0].AsSpan(1)));
        }
        return str.Trim();
    }

    [GeneratedRegex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$")]
    private static partial Regex DiscPatternRegex();

    // Builds AlbumFolders from raw search results for album searches.
    private static List<AlbumFolder> BuildAlbumFolders(
        ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results,
        AlbumQuery query, DownloadSettings config)
    {
        bool canMatchDisc = !DiscPatternRegex().IsMatch(query.Album) && !DiscPatternRegex().IsMatch(query.Artist);

        // Build directory buckets ordered by first-appearance in ResultSorter output.
        var bridgeQuery = new SongQuery
        {
            Artist = query.Artist,
            Title = query.Album.Length > 0 ? query.Album : query.SearchHint,
            Album = query.Album,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };
        // Use a placeholder SongQuery-based sort to preserve ordering.
        var orderedResults = ResultSorter.OrderedResults(results, bridgeQuery, config,
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(), false, false, albumMode: true);

        // Key = "username\folderpath" (for uniqueness across users); value stores them separately.
        var dirStructure = new Dictionary<string, (string username, string folderPath, List<(SlResponse r, SlFile f)> list, int idx)>();
        int idx = 0;

        foreach (var (response, file) in orderedResults)
        {
            string username = response.Username;
            string folderPath = file.Filename[..file.Filename.LastIndexOf('\\')];
            string dirName = folderPath[(folderPath.LastIndexOf('\\') + 1)..];

            if (canMatchDisc && DiscPatternRegex().IsMatch(dirName))
                folderPath = folderPath[..folderPath.LastIndexOf('\\')];

            string key = username + '\\' + folderPath;
            if (!dirStructure.TryGetValue(key, out (string username, string folderPath, List<(SlResponse r, SlFile f)> list, int idx) value))
                dirStructure[key] = (username, folderPath, new List<(SlResponse, SlFile)> { (response, file) }, idx);
            else
                value.list.Add((response, file));

            idx++;
        }

        // Merge child directories (e.g., Artist/Album/Scans) into parent (Artist/Album).
        var sortedKeys = dirStructure.Keys.OrderBy(k => k).ToList();
        var toRemove = new HashSet<string>();

        for (int i = 0; i < sortedKeys.Count; i++)
        {
            var key = sortedKeys[i];
            if (toRemove.Contains(key)) continue;
            for (int j = i + 1; j < sortedKeys.Count; j++)
            {
                var key2 = sortedKeys[j];
                if (toRemove.Contains(key2)) continue;
                if ((key2 + '\\').StartsWith(key + '\\'))
                {
                    if (dirStructure[key].idx <= dirStructure[key2].idx)
                    { dirStructure[key].list.AddRange(dirStructure[key2].list); toRemove.Add(key2); }
                    else
                    { dirStructure[key2].list.AddRange(dirStructure[key].list); toRemove.Add(key); key = key2; }
                }
                else if (!(key2 + '\\').StartsWith(key)) break;
            }
        }
        foreach (var k in toRemove) dirStructure.Remove(k);

        int min = query.MinTrackCount;
        int max = query.MaxTrackCount;
        // If album was searched with a title hint, we can't enforce min count yet (folder may be partial).
        // For now, leave min/max enforcement to DownloadEngine after CompleteFolder.

        var folders = new List<AlbumFolder>();

        foreach (var (_, (username, folderPath, list, _)) in dirStructure)
        {
            int musicCount = list.Count(x => Utils.IsMusicFile(x.f.Filename));
            if (musicCount == 0) continue;
            if (max != -1 && musicCount > max) continue;
            if (min > 0 && musicCount < min) continue;

            var files = list
                .OrderBy(x => !Utils.IsMusicFile(x.f.Filename))
                .ThenBy(x => x.f.Filename)
                .Select(x =>
                {
                    var info = InferSongQuery(x.f.Filename, new SongQuery { Artist = query.Artist, Album = query.Album });
                    return new SongJob(info) { ResolvedTarget = new FileCandidate(x.r, x.f) };
                })
                .ToList();

            folders.Add(new AlbumFolder(username, folderPath, files));
        }

        return folders;
    }

    // copyright is joke
    public static readonly string[][] bannedTerms =
    [
        ["depeche", "mode"],
        ["beatles"],
        ["prince", "revolutions"],
        ["michael", "jackson"],
        ["coexist"],
        ["bob", "dylan"],
        ["enter", "shikari"],
        ["village", "people"],
        ["lenny", "kravitz"],
        ["beyonce"],
        ["beyoncé"],
        ["lady", "gaga"],
        ["jay", "z"],
        ["kanye", "west"],
        ["rihanna"],
        ["adele"],
        ["kendrick", "lamar"],
        ["romance", "bad"],
        ["born", "this", "way"],
        ["weeknd"],
        ["broken", "hearted"],
        ["highway", "61", "revisited"],
        ["west", "gold", "digger"],
        ["west", "good", "life"],
        ["hold", "my", "hand"],
        ["ymca"],
        ["navy", "in", "the"],
        ["macho"],
        ["west", "go"],
        ["hot", "cop"],
        ["phone", "sex", "over", "the"],
        ["minaj"],
        ["government", "hooker"],
        ["wayne", "lil"],
        ["mood", "4", "eva"],
        ["ghosts", "again"],
        ["purple", "rain"],
    ];
}


public abstract class SearchAndDownloadException : Exception
{
    public FailureReason Reason { get; }
    protected SearchAndDownloadException(FailureReason reason, string message) : base(message) { Reason = reason; }
    protected SearchAndDownloadException(FailureReason reason, string message, Exception inner) : base(message, inner) { Reason = reason; }
}

public class OutOfDownloadRetriesException : SearchAndDownloadException
{
    public OutOfDownloadRetriesException(Exception inner)
        : base(FailureReason.OutOfDownloadRetries, "Out of download retries.", inner) { }
}

public class NoSuitableFileFoundException : SearchAndDownloadException
{
    public NoSuitableFileFoundException(string? details = null)
        : base(FailureReason.NoSuitableFileFound, details ?? "") { }
    public NoSuitableFileFoundException(string details, Exception inner)
        : base(FailureReason.NoSuitableFileFound, details, inner) { }
}

public class AllDownloadsFailedException : SearchAndDownloadException
{
    public AllDownloadsFailedException()
        : base(FailureReason.AllDownloadsFailed, "All downloads failed.") { }
}
