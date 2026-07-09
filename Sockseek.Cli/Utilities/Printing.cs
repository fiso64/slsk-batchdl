using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using SearchResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

public static class Printing
{
    public static readonly object ConsoleLock = new();
    internal static Action<string, ConsoleColor>? LiveWriteLine { get; set; }

    private static bool _isBuffering;
    private static readonly List<(string value, ConsoleColor color, bool isNewLine)> _buffer = new();

    public static void SetBuffering(bool value)
    {
        lock (ConsoleLock)
        {
            if (_isBuffering && !value)
                Flush();
            _isBuffering = value;
        }
    }

    public static void Flush()
    {
        lock (ConsoleLock)
        {
            foreach (var (value, color, isNewLine) in _buffer)
            {
                if (isNewLine)
                    WriteLine(value, color, force: true);
                else
                    Write(value, color, force: true);
            }
            _buffer.Clear();
        }
    }

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
        Console.ResetColor();
        var songList = songs.ToList();
        if (songList.Count == 0)
            return;

        number = Math.Min(songList.Count, number);

        string ancestor = "";
        if (!showAncestors)
            ancestor = Utils.GreatestCommonDirectorySlsk(
                songList.SelectMany(s => s.Candidates?.Select(c => c.Filename) ?? []));

        if (pathsOnly)
        {
            for (int i = 0; i < number; i++)
            {
                foreach (var c in songList[i].Candidates ?? Enumerable.Empty<FileCandidate>())
                {
                    if (indices)
                    {
                        Write($" [{i + 1:D2}]", ConsoleColor.DarkGray);
                    }
                    if (ancestor.Length == 0)
                        WriteLine("    " + DisplayString(songList[i].Query, c.File, c.Response, infoFirst: infoFirst, showUser: showUser));
                    else
                        WriteLine("    " + DisplayString(songList[i].Query, c.File, c.Response, customPath: c.File.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                }
            }
        }
        else if (!fullInfo)
        {
            for (int i = 0; i < number; i++)
                WriteLine($"  {songList[i]}");
        }
        else
        {
            for (int i = 0; i < number; i++)
            {
                var s = songList[i];
                WriteLine($"  Artist:             {s.Query.Artist}");
                WriteLine($"  Title:              {s.Query.Title}");
                if (!string.IsNullOrEmpty(s.Query.Album))
                    WriteLine($"  Album:              {s.Query.Album}");
                if (s.Query.Length > -1)
                    WriteLine($"  Length:             {s.Query.Length}s");
                if (!string.IsNullOrEmpty(s.DownloadPath))
                    WriteLine($"  Local path:         {s.DownloadPath}");
                if (!string.IsNullOrEmpty(s.Query.URI))
                    WriteLine($"  URL/ID:             {s.Query.URI}");
                if (!string.IsNullOrEmpty(s.Other))
                    WriteLine($"  Other:              {s.Other}");
                if (s.Query.ArtistMaybeWrong)
                    WriteLine($"  Artist maybe wrong: {s.Query.ArtistMaybeWrong}");
                if (s.Candidates != null)
                {
                    WriteLine($"  Shares:             {s.Candidates.Count}");
                    foreach (var c in s.Candidates)
                    {
                        if (ancestor.Length == 0)
                            WriteLine("    " + DisplayString(s.Query, c.File, c.Response, infoFirst: infoFirst, showUser: showUser));
                        else
                            WriteLine("    " + DisplayString(s.Query, c.File, c.Response, customPath: c.File.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                    }
                    if (s.Candidates.Count > 0) WriteLine();
                }

                if (i < number - 1)
                    WriteLine();
            }
        }

        if (number < songList.Count)
            WriteLine($"  ... (etc)");
    }


    public static void PrintResults(Job job, PrintOption printOption, SearchSettings search)
        => ResultPrintFormatter.Print(job, printOption, search);

    public static void PrintComplete(JobList queue)
    {
        var (successes, fails, skipped) = CountUserFacingCompletionsDetailed(queue);
        PrintComplete(successes, fails, skipped);
    }

    internal static (int Successes, int Fails) CountUserFacingCompletions(JobList queue)
    {
        var (successes, fails, _) = CountUserFacingCompletionsDetailed(queue);
        return (successes, fails);
    }

    internal static (int Successes, int Fails, int Skipped) CountUserFacingCompletionsDetailed(JobList queue)
    {
        int successes = 0, fails = 0;
        int skipped = 0;
        var visited = new HashSet<Guid>();

        foreach (var job in queue.Jobs)
            CountUserFacingCompletion(job, parent: null, visited, ref successes, ref fails, ref skipped);

        return (successes, fails, skipped);
    }

    private static void CountUserFacingCompletion(
        Job job,
        Job? parent,
        ISet<Guid> visited,
        ref int successes,
        ref int fails,
        ref int skipped)
    {
        if (!visited.Add(job.Id))
            return;

        if (job.Config?.DoNotDownload == true)
            return;

        switch (job)
        {
            case ExtractJob extractJob:
                if (extractJob.Result != null)
                    CountUserFacingCompletion(extractJob.Result, parent: null, visited, ref successes, ref fails, ref skipped);
                return;

            case JobList jobList:
                foreach (var child in jobList.Jobs)
                    CountUserFacingCompletion(child, jobList, visited, ref successes, ref fails, ref skipped);
                return;

            case AggregateJob aggregateJob:
                foreach (var song in aggregateJob.Songs)
                    CountUserFacingCompletion(song, aggregateJob, visited, ref successes, ref fails, ref skipped);
                return;

            case AlbumAggregateJob albumAggregateJob:
                foreach (var album in albumAggregateJob.Albums)
                    CountUserFacingCompletion(album, albumAggregateJob, visited, ref successes, ref fails, ref skipped);
                return;

            case RetrieveFolderJob:
                return;
        }

        if (job is SongJob && parent is AlbumJob)
            return;

        if (IsSuccessfulCompletion(job)) successes++;
        else if (IsManualSkipCompletion(job)) skipped++;
        else if (job.IsUnsuccessfulTerminal) fails++;
    }

    public static void PrintComplete(int successes, int fails)
        => PrintComplete(successes, fails, skipped: 0);

    public static void PrintComplete(int successes, int fails, int skipped)
    {
        if (successes + fails + skipped > 1 || fails > 0 || skipped > 0)
        {
            WriteLine();
            var skippedPart = skipped > 0 ? $", {skipped} skipped" : "";
            SockseekLog.Info($"Completed: {successes} succeeded{skippedPart}, {fails} failed.");
        }
    }

    public static bool IsSuccessfulCompletion(Job job)
        => job.IsSuccessfulTerminal;

    public static bool IsManualSkipCompletion(Job job)
        => job.TerminalOutcome == JobTerminalOutcome.Skipped
            && job.SkipReason == JobSkipReason.Manual;


    public static void PrintTracksTbd(List<SongJob> toBeDownloaded, List<SongJob> existing, List<SongJob> notFound,
        bool isNormal, PrintOption printOption, bool summary = true)
    {
        bool printTracks  = printOption.HasFlag(PrintOption.Jobs);
        bool printResults = (printOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
        bool full         = printOption.HasFlag(PrintOption.Full);

        if (isNormal && !printTracks && toBeDownloaded.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist     = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool allSkipped = existing.Count + notFound.Count > toBeDownloaded.Count;
        bool printOnly = printTracks || printResults;

        if (summary && printOnly && toBeDownloaded.Count > 0)
        {
            var label = printResults && !isNormal ? "aggregate tracks" : "tracks to download";
            WriteLine($"{toBeDownloaded.Count} {label}:");
        }
        else if (summary && !printOnly && (isNormal || skippedTracks.Length > 0))
        {
            SockseekLog.Info($"Downloading {toBeDownloaded.Count} tracks{skippedTracks}{(allSkipped ? '.' : ':')}");
        }

        if (toBeDownloaded.Count > 0)
        {
            bool showAll = !isNormal || printTracks || printResults;
            int limit = showAll ? int.MaxValue : 10;
            PrintTracks(toBeDownloaded, limit, full, infoFirst: printTracks);
            if (!showAll && toBeDownloaded.Count > limit)
                WriteLine($"  ... and {toBeDownloaded.Count - limit} more");

            if (full && (existing.Count > 0 || notFound.Count > 0))
                WriteLine("\n-----------------------------------------------\n");
        }

        if (existing.Count > 0)
        {
            WriteLine($"{(toBeDownloaded.Count > 0 ? "\n" : "")}{existing.Count} tracks already exist:");
            PrintTracks(existing, fullInfo: full, infoFirst: printTracks);
        }
        if (notFound.Count > 0)
        {
            WriteLine($"{(toBeDownloaded.Count > 0 || existing.Count > 0 ? "\n" : "")}{notFound.Count} tracks were not found during a prior run:");
            PrintTracks(notFound, fullInfo: full, infoFirst: printTracks);
        }
    }


    public static void PrintTrackResults(IEnumerable<(SearchResponse, Soulseek.File)> orderedResults, SongQuery query,
        bool full = false, FileConditions? necCond = null, FileConditions? prefCond = null)
    {
        Console.ResetColor();
        int count = 0;
        foreach (var (response, file) in orderedResults)
        {
            WriteLine(DisplayString(query, file, response,
                full ? necCond : null, full ? prefCond : null,
                fullpath: full, infoFirst: true, showSpeed: full));
            count++;
        }
        WriteLine($"Total: {count}\n", ConsoleColor.Yellow);
    }


    public static void PrintLink(string username, string filename)
    {
        var link = $"slsk://{username}/{filename.Replace('\\', '/')}";
        WriteLine(link);
    }


    public static void PrintAlbumLink(AlbumFolder folder)
    {
        if (folder.Files.Count == 0) return;
        string directory = Utils.GreatestCommonDirectorySlsk(folder.Files.Select(f => f.Filename));
        var link = $"slsk://{folder.Username}/{directory.Replace('\\', '/').TrimEnd('/')}/";
        WriteLine(link);
    }


    public static void PrintAlbumHeader(AlbumFolder folder, bool force = false)
    {
        if (folder.Files.Count == 0) return;

        lock (ConsoleLock)
        {
            Console.ResetColor();
            var firstResponse = folder.Files[0].Candidate.Response;
            string noSlot   = !firstResponse.HasFreeUploadSlot ? ", no upload slots" : "";
            string userInfo = $"{firstResponse.Username} ({((float)firstResponse.UploadSpeed / (1024 * 1024)):F3}MB/s{noSlot})";
            var (parents, propsList) = FolderInfo(folder.Files.Select(f => f.Candidate.File), folder.FolderPath);

            string format     = propsList.FirstOrDefault() ?? "";
            string otherProps = propsList.Count > 1 ? " / " + string.Join(" / ", propsList.Skip(1)) : "";

            Write($"User  : {userInfo}\nFolder: {parents}\nProps :[", ConsoleColor.White, force: force);
            Write(format, GetFormatColor(format), force: force);
            WriteLine(otherProps + "]", ConsoleColor.White, force: force);
        }
    }

    public static int PrintAlbum(AlbumFolder folder, bool indices = false, bool force = false)
    {
        if (folder.Files.Count == 0) return 0;

        Console.ResetColor();
        PrintAlbumHeader(folder, force);

        string ancestor = folder.FolderPath.TrimEnd('\\');
        int i = 0;
        foreach (var af in folder.Files)
        {
            if (indices)
            {
                Write($" [{i + 1:D2}]", ConsoleColor.DarkGray, force: force);
            }
            string customPath = PathRelativeToFolder(af.Candidate.File.Filename, ancestor);
            WriteLine("    " + DisplayString(af.Query, af.Candidate.File, af.Candidate.Response, customPath: customPath, showUser: false), ConsoleColor.Gray, force: force);
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

    private static string PathRelativeToFolder(string filename, string folderPath)
    {
        if (folderPath.Length == 0)
            return "";

        return filename.StartsWith(folderPath + "\\", StringComparison.OrdinalIgnoreCase)
            ? filename[(folderPath.Length + 1)..]
            : Utils.GetFileNameSlsk(filename);
    }

    static (string parents, List<string> props) FolderInfo(IEnumerable<SlFile> files, string? folderPath = null)
    {
        var fileList = files.ToList();
        int totalLengthInSeconds = fileList.Sum(f => f.Length ?? 0);
        var sampleRates = fileList.Where(f => f.SampleRate.HasValue).Select(f => f.SampleRate.GetValueOrDefault()).OrderBy(r => r).ToList();
        int? modeSampleRate = sampleRates.GroupBy(rate => rate).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();

        var bitRates = fileList.Where(f => f.BitRate.HasValue).Select(f => f.BitRate.GetValueOrDefault()).ToList();
        double? meanBitrate = bitRates.Count > 0 ? (double?)bitRates.Average() : null;
        double totalFileSizeInMB = fileList.Sum(f => f.Size) / (1024.0 * 1024.0);

        TimeSpan totalTimeSpan = TimeSpan.FromSeconds(totalLengthInSeconds);
        string totalLengthFormatted = totalTimeSpan.TotalHours >= 1
            ? string.Format("{0}:{1:D2}:{2:D2}", (int)totalTimeSpan.TotalHours, totalTimeSpan.Minutes, totalTimeSpan.Seconds)
            : string.Format("{0:D2}:{1:D2}", totalTimeSpan.Minutes, totalTimeSpan.Seconds);

        var mostCommonExtension = fileList.GroupBy(f => Utils.GetExtensionSlsk(f.Filename))
            .OrderByDescending(g => Utils.IsMusicExtension(g.Key)).ThenByDescending(g => g.Count()).First().Key.TrimStart('.');

        List<string> propsList = new() { mostCommonExtension.ToUpper().Trim(), totalLengthFormatted };
        if (modeSampleRate.HasValue)
            propsList.Add($"{(modeSampleRate.Value / 1000.0).Normalize()} kHz");
        if (meanBitrate.HasValue)
            propsList.Add($"{(int)meanBitrate.Value} kbps");
        propsList.Add($"{totalFileSizeInMB:F2} MB");

        string gcp = string.IsNullOrWhiteSpace(folderPath)
            ? Utils.GreatestCommonDirectorySlsk(fileList.Select(x => x.Filename)).TrimEnd('\\')
            : folderPath.TrimEnd('\\');
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

    public static void RefreshOrPrint(int current, string item, bool print = false)
    {
        if (print)
            SockseekLog.Info(item);
    }

    public static void WriteLine(string value = "", ConsoleColor color = ConsoleColor.Gray, bool force = false)
    {
        if (!force)
        {
            lock (ConsoleLock)
            {
                if (_isBuffering)
                {
                    _buffer.Add((value, color, true));
                    return;
                }
            }
        }

        if (!force && LiveWriteLine is { } liveWriteLine)
        {
            liveWriteLine(value, color);
            return;
        }

        lock (ConsoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ResetColor();
        }
    }

    public static void Write(string value, ConsoleColor color = ConsoleColor.Gray, bool force = false)
    {
        if (!force)
        {
            lock (ConsoleLock)
            {
                if (_isBuffering)
                {
                    _buffer.Add((value, color, false));
                    return;
                }
            }
        }

        lock (ConsoleLock)
        {
            Console.ForegroundColor = color;
            Console.Write(value);
            Console.ResetColor();
        }
    }
}
