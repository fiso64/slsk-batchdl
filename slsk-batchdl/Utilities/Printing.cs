using Jobs;
using Models;
using Enums;
using Konsole;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;

public static class Printing
{
    static readonly object consoleLock = new();

    public static string DisplayString(SongQuery query, Soulseek.File? file = null, SearchResponse? response = null,
        FileConditions? nec = null, FileConditions? pref = null, bool fullpath = false, string customPath = "",
        bool infoFirst = false, bool showUser = true, bool showSpeed = false)
    {
        if (file == null)
            return query.ToString();

        string sampleRate  = file.SampleRate.HasValue ? $"{(file.SampleRate.Value / 1000.0).Normalize()}kHz" : "";
        string bitRate     = file.BitRate.HasValue ? $"{file.BitRate}kbps" : "";
        string fileSize    = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string user        = showUser && response?.Username != null ? response.Username + "\\" : "";
        string speed       = showSpeed && response?.Username != null ? $"({response.UploadSpeed / 1024.0 / 1024.0:F2}MB/s) " : "";
        string fname       = fullpath ? file.Filename : (showUser ? "..\\" : "") + (customPath.Length == 0 ? Utils.GetFileNameSlsk(file.Filename) : customPath);
        string length      = Utils.IsMusicFile(file.Filename) ? (file.Length ?? -1).ToString() + "s" : "";
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

        string necStr  = nec  != null ? $"nec:{nec.GetNotSatisfiedName(file, query, response)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, query, response)}" : "";
        string cond    = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }


    public static void PrintTracks(IEnumerable<SongJob> songs, int number = int.MaxValue, bool fullInfo = false,
        bool pathsOnly = false, bool showAncestors = true, bool infoFirst = false, bool showUser = true, bool indices = false)
    {
        var songList = songs.ToList();
        if (songList.Count == 0)
            return;

        number = Math.Min(songList.Count, number);

        string ancestor = "";
        if (!showAncestors)
            ancestor = Utils.GreatestCommonDirectorySlsk(
                songList.SelectMany(s => s.Candidates?.Select(c => c.Filename) ?? Enumerable.Empty<string>()));

        if (pathsOnly)
        {
            for (int i = 0; i < number; i++)
            {
                foreach (var c in songList[i].Candidates ?? Enumerable.Empty<FileCandidate>())
                {
                    if (indices)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($" [{i + 1:D2}]");
                        Console.ResetColor();
                    }
                    if (ancestor.Length == 0)
                        Console.WriteLine("    " + DisplayString(songList[i].Query, c.File, c.Response, infoFirst: infoFirst, showUser: showUser));
                    else
                        Console.WriteLine("    " + DisplayString(songList[i].Query, c.File, c.Response, customPath: c.File.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                }
            }
        }
        else if (!fullInfo)
        {
            for (int i = 0; i < number; i++)
                Console.WriteLine($"  {songList[i]}");
        }
        else
        {
            for (int i = 0; i < number; i++)
            {
                var s = songList[i];
                Console.WriteLine($"  Artist:             {s.Query.Artist}");
                Console.WriteLine($"  Title:              {s.Query.Title}");
                if (!string.IsNullOrEmpty(s.Query.Album))
                    Console.WriteLine($"  Album:              {s.Query.Album}");
                if (s.Query.Length > -1)
                    Console.WriteLine($"  Length:             {s.Query.Length}s");
                if (!string.IsNullOrEmpty(s.DownloadPath))
                    Console.WriteLine($"  Local path:         {s.DownloadPath}");
                if (!string.IsNullOrEmpty(s.Query.URI))
                    Console.WriteLine($"  URL/ID:             {s.Query.URI}");
                if (!string.IsNullOrEmpty(s.Other))
                    Console.WriteLine($"  Other:              {s.Other}");
                if (s.Query.ArtistMaybeWrong)
                    Console.WriteLine($"  Artist maybe wrong: {s.Query.ArtistMaybeWrong}");
                if (s.Candidates != null)
                {
                    Console.WriteLine($"  Shares:             {s.Candidates.Count}");
                    foreach (var c in s.Candidates)
                    {
                        if (ancestor.Length == 0)
                            Console.WriteLine("    " + DisplayString(s.Query, c.File, c.Response, infoFirst: infoFirst, showUser: showUser));
                        else
                            Console.WriteLine("    " + DisplayString(s.Query, c.File, c.Response, customPath: c.File.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                    }
                    if (s.Candidates.Count > 0) Console.WriteLine();
                }

                if (i < number - 1)
                    Console.WriteLine();
            }
        }

        if (number < songList.Count)
            Console.WriteLine($"  ... (etc)");
    }


    public static async Task PrintResults(Job job, List<SongJob> existing, List<SongJob> notFound, Config config, Searcher searchService)
    {
        if (job is SongListQueryJob slj)
        {
            await searchService.SearchAndPrintResults(slj.Songs, config);
        }
        else if (job is AggregateQueryJob ag)
        {
            if (config.printOption.HasFlag(PrintOption.Json))
            {
                JsonPrinter.PrintAggregateJson(ag.Songs.Where(s => s.State == TrackState.Initial));
            }
            else if (config.printOption.HasFlag(PrintOption.Link))
            {
                var first = ag.Songs.FirstOrDefault(s => s.ChosenCandidate != null);
                if (first?.ChosenCandidate != null)
                    PrintLink(first.ChosenCandidate.Username, first.ChosenCandidate.Filename);
            }
            else
            {
                Console.WriteLine($"Results for aggregate {job.ToString(true)}:");
                PrintTracksTbd(ag.Songs.Where(s => s.State == TrackState.Initial).ToList(), existing, notFound, false, config);
            }
        }
        else if (job is AlbumQueryJob albumJob)
        {
            if (config.printOption.HasFlag(PrintOption.Json))
            {
                var foldersToPrint = config.printOption.HasFlag(PrintOption.Full)
                    ? albumJob.FoundFolders
                    : albumJob.FoundFolders.Take(1).ToList();
                JsonPrinter.PrintAlbumJson(foldersToPrint, albumJob);
            }
            else if (config.printOption.HasFlag(PrintOption.Link))
            {
                if (albumJob.FoundFolders.Count > 0)
                    PrintAlbumLink(albumJob.FoundFolders[0]);
            }
            else
            {
                if (!config.printOption.HasFlag(PrintOption.Full))
                    Console.WriteLine($"Result 1 of {albumJob.FoundFolders.Count} for album {job.ToString(true)}:");
                else
                    Console.WriteLine($"Results ({albumJob.FoundFolders.Count}) for album {job.ToString(true)}:");

                if (albumJob.FoundFolders.Count > 0)
                {
                    if (!config.noBrowseFolder)
                        Console.WriteLine("[Skipping full folder retrieval]");

                    foreach (var folder in albumJob.FoundFolders)
                    {
                        PrintAlbum(folder);
                        if (!config.printOption.HasFlag(PrintOption.Full))
                            break;
                    }
                }
            }
        }
        else
        {
            Console.WriteLine("No results.");
        }
    }


    public static void PrintComplete(JobQueue queue)
    {
        int successes = 0, fails = 0;
        foreach (var job in queue.Jobs)
        {
            IEnumerable<SongJob> songs = job switch
            {
                SongListQueryJob slj => slj.Songs,
                AggregateQueryJob ag => ag.Songs,
                _               => Enumerable.Empty<SongJob>(),
            };
            foreach (var s in songs)
            {
                if (s.State == TrackState.Downloaded) successes++;
                else if (s.State == TrackState.Failed)   fails++;
            }
            if (job is AlbumQueryJob albumJob && albumJob.CompletedDownload != null)
            {
                foreach (var f in albumJob.CompletedDownload.Target.Files.Where(f => !f.IsNotAudio))
                {
                    if (f.State == TrackState.Downloaded) successes++;
                    else if (f.State == TrackState.Failed)   fails++;
                }
            }
        }
        if (successes + fails > 1)
        {
            Console.WriteLine();
            Logger.Info($"Completed: {successes} succeeded, {fails} failed.");
        }
    }


    public static void PrintTracksTbd(List<SongJob> toBeDownloaded, List<SongJob> existing, List<SongJob> notFound,
        bool isNormal, Config config, bool summary = true)
    {
        if (isNormal && !config.PrintTracks && toBeDownloaded.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist     = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool full       = config.printOption.HasFlag(PrintOption.Full);
        bool allSkipped = existing.Count + notFound.Count > toBeDownloaded.Count;

        if (summary && (isNormal || skippedTracks.Length > 0))
            Logger.Info($"Downloading {toBeDownloaded.Count} tracks{skippedTracks}{(allSkipped ? '.' : ':')}");

        if (toBeDownloaded.Count > 0)
        {
            bool showAll = !isNormal || config.PrintTracks || config.PrintResults;
            PrintTracks(toBeDownloaded, showAll ? int.MaxValue : 10, full, infoFirst: config.PrintTracks);

            if (full && (existing.Count > 0 || notFound.Count > 0))
                Console.WriteLine("\n-----------------------------------------------\n");
        }

        if (config.PrintTracks || config.PrintResults)
        {
            if (existing.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks already exist:");
                PrintTracks(existing, fullInfo: full, infoFirst: config.PrintTracks);
            }
            if (notFound.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks were not found during a prior run:");
                PrintTracks(notFound, fullInfo: full, infoFirst: config.PrintTracks);
            }
        }
    }


    public static void PrintTrackResults(IEnumerable<(SearchResponse, Soulseek.File)> orderedResults, SongQuery query,
        bool full = false, FileConditions? necCond = null, FileConditions? prefCond = null)
    {
        int count = 0;
        foreach (var (response, file) in orderedResults)
        {
            Console.WriteLine(DisplayString(query, file, response,
                full ? necCond : null, full ? prefCond : null,
                fullpath: full, infoFirst: true, showSpeed: full));
            count++;
        }
        WriteLine($"Total: {count}\n", ConsoleColor.Yellow);
    }


    public static void PrintLink(string username, string filename)
    {
        var link = $"slsk://{username}/{filename.Replace('\\', '/')}";
        Console.WriteLine(link);
    }


    public static void PrintAlbumLink(AlbumFolder folder)
    {
        if (folder.Files.Count == 0) return;
        string directory = Utils.GreatestCommonDirectorySlsk(folder.Files.Select(f => f.Candidate.Filename));
        var link = $"slsk://{folder.Username}/{directory.Replace('\\', '/').TrimEnd('/')}/";
        Console.WriteLine(link);
    }


    public static int PrintAlbum(AlbumFolder folder, bool indices = false)
    {
        if (folder.Files.Count == 0) return 0;

        var firstResponse = folder.Files[0].Candidate.Response;
        string noSlot   = !firstResponse.HasFreeUploadSlot ? ", no upload slots" : "";
        string userInfo = $"{firstResponse.Username} ({((float)firstResponse.UploadSpeed / (1024 * 1024)):F3}MB/s{noSlot})";
        var (parents, propsList) = FolderInfo(folder.Files.Select(f => f.Candidate.File));

        string format     = propsList.FirstOrDefault() ?? "";
        string otherProps = propsList.Count > 1 ? " / " + string.Join(" / ", propsList.Skip(1)) : "";

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"User  : {userInfo}\nFolder: {parents}\nProps : [");
        Console.ForegroundColor = GetFormatColor(format);
        Console.Write(format);
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(otherProps + "]");
        Console.ResetColor();

        string ancestor = Utils.GreatestCommonDirectorySlsk(folder.Files.Select(f => f.Candidate.Filename));
        int i = 0;
        foreach (var af in folder.Files)
        {
            if (indices)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($" [{i + 1:D2}]");
                Console.ResetColor();
            }
            string customPath = ancestor.Length > 0 ? af.Candidate.File.Filename.Replace(ancestor, "").TrimStart('\\') : "";
            Console.WriteLine("    " + DisplayString(af.Info, af.Candidate.File, af.Candidate.Response, customPath: customPath, showUser: false));
            i++;
        }

        return 3 + folder.Files.Count;
    }

    public static string FormatList<T>(ICollection<T> items, Func<T, string> format, string indent = "  ", int maxCount = 10)
    {
        var result = new System.Text.StringBuilder();
        int count = 1;
        foreach (var item in items)
        {
            if (count > 1) result.Append('\n');
            if (count > maxCount) { result.Append($"... and {items.Count - count} more"); break; }
            result.Append(indent);
            result.Append(format(item));
            count++;
        }
        return result.ToString();
    }

    static (string parents, List<string> props) FolderInfo(IEnumerable<SlFile> files)
    {
        int totalLengthInSeconds = files.Sum(f => f.Length ?? 0);
        var sampleRates = files.Where(f => f.SampleRate.HasValue).Select(f => f.SampleRate.Value).OrderBy(r => r).ToList();
        int? modeSampleRate = sampleRates.GroupBy(rate => rate).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();

        var bitRates = files.Where(f => f.BitRate.HasValue).Select(f => f.BitRate.Value).ToList();
        double? meanBitrate = bitRates.Count > 0 ? (double?)bitRates.Average() : null;
        double totalFileSizeInMB = files.Sum(f => f.Size) / (1024.0 * 1024.0);

        TimeSpan totalTimeSpan = TimeSpan.FromSeconds(totalLengthInSeconds);
        string totalLengthFormatted = totalTimeSpan.TotalHours >= 1
            ? string.Format("{0}:{1:D2}:{2:D2}", (int)totalTimeSpan.TotalHours, totalTimeSpan.Minutes, totalTimeSpan.Seconds)
            : string.Format("{0:D2}:{1:D2}", totalTimeSpan.Minutes, totalTimeSpan.Seconds);

        var mostCommonExtension = files.GroupBy(f => Utils.GetExtensionSlsk(f.Filename))
            .OrderByDescending(g => Utils.IsMusicExtension(g.Key)).ThenByDescending(g => g.Count()).First().Key.TrimStart('.');

        List<string> propsList = new() { mostCommonExtension.ToUpper().Trim(), totalLengthFormatted };
        if (modeSampleRate.HasValue)
            propsList.Add($"{(modeSampleRate.Value / 1000.0).Normalize()} kHz");
        if (meanBitrate.HasValue)
            propsList.Add($"{(int)meanBitrate.Value} kbps");
        propsList.Add($"{totalFileSizeInMB:F2} MB");

        string gcp = Utils.GreatestCommonDirectorySlsk(files.Select(x => x.Filename)).TrimEnd('\\');
        int lastIndex = gcp.LastIndexOf('\\');
        if (lastIndex != -1)
        {
            int secondLastIndex = gcp.LastIndexOf('\\', lastIndex - 1);
            gcp = secondLastIndex == -1 ? gcp : gcp[(secondLastIndex + 1)..];
        }

        return (gcp, propsList);
    }

    static ConsoleColor GetFormatColor(string format)
    {
        return format.ToLower() switch
        {
            "flac" => ConsoleColor.DarkYellow,
            "mp3"  => ConsoleColor.DarkRed,
            "ogg"  => ConsoleColor.DarkGreen,
            "wav"  => ConsoleColor.White,
            "opus" => ConsoleColor.DarkBlue,
            "m4a"  => ConsoleColor.Cyan,
            _      => ConsoleColor.Gray,
        };
    }

    public static void RefreshOrPrint(ProgressBar? progress, int current, string item, bool print = false, bool refreshIfOffscreen = false)
    {
        if (progress != null && !Console.IsOutputRedirected && (refreshIfOffscreen || progress.Y >= Console.WindowTop))
        {
            try { progress.Refresh(current, item); }
            catch { }

            if (print)
                Logger.LogNonConsole(Logger.LogLevel.Info, item);
        }
        else if ((progress == null || Console.IsOutputRedirected) && print)
        {
            Logger.Info(item);
        }
    }

    public static void WriteLine(string value, ConsoleColor color = ConsoleColor.Gray)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ResetColor();
    }

    public static void Write(string value, ConsoleColor color = ConsoleColor.Gray)
    {
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ResetColor();
    }

    public static ProgressBar? GetProgressBar(Config config)
    {
        lock (consoleLock)
        {
            if (!config.noProgress)
            {
                try { return new ProgressBar(PbStyle.SingleLine, 100, Console.WindowWidth - 10, character: ' '); }
                catch { return null; }
            }
            return null;
        }
    }
}
