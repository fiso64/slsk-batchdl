using Models;
using Enums;
using Konsole;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;

public static class Printing
{
    static readonly object consoleLock = new();

    public static string DisplayString(Track t, Soulseek.File? file = null, SearchResponse? response = null, FileConditions? nec = null,
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


    public static void PrintTracks(List<Track> tracks, int number = int.MaxValue, bool fullInfo = false, bool pathsOnly = false, bool showAncestors = true, bool infoFirst = false, bool showUser = true, bool indices = false)
    {
        if (tracks.Count == 0)
            return;

        number = Math.Min(tracks.Count, number);

        string ancestor = "";

        if (!showAncestors)
            ancestor = Utils.GreatestCommonDirectorySlsk(tracks.SelectMany(x => x.Downloads.Select(y => y.Item2.Filename)));

        if (pathsOnly)
        {
            for (int i = 0; i < number; i++)
            {
                foreach (var x in tracks[i].Downloads)
                {
                    if (indices)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($" [{i + 1:D2}]");
                        Console.ResetColor();
                    }
                    if (ancestor.Length == 0)
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, infoFirst: infoFirst, showUser: showUser));
                    else
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, customPath: x.Item2.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
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
                    if (tracks[i].MinAlbumTrackCount != -1 || tracks[i].MaxAlbumTrackCount != -1)
                        Console.WriteLine($"  Min,Max tracks:     {tracks[i].MinAlbumTrackCount},{tracks[i].MaxAlbumTrackCount}");
                    if (tracks[i].Length > -1)
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
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, customPath: x.Item2.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
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
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Item2, x.Item1, customPath: x.Item2.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                    }
                    Console.WriteLine();
                }

                if (i < number - 1)
                    Console.WriteLine();
            }
        }

        if (number < tracks.Count)
            Console.WriteLine($"  ... (etc)");
    }


    public static async Task PrintResults(TrackListEntry tle, List<Track> existing, List<Track> notFound, Config config, Searcher searchService)
    {
        if (tle.source.Type == TrackType.Normal)
        {
            await searchService.SearchAndPrintResults(tle.list[0], config);
        }
        else if (tle.source.Type == TrackType.Aggregate)
        {
            if (config.printOption.HasFlag(PrintOption.Json))
            {
                var tracks = tle.list[0].Where(t => t.State == TrackState.Initial).ToList();
                JsonPrinter.PrintAggregateJson(tracks);
            }
            else if (config.printOption.HasFlag(PrintOption.Link))
            {
                var first = tle.list[0].First();
                PrintLink(first.FirstResponse.Username, first.FirstDownload.Filename);
            }
            else
            {
                Console.WriteLine($"Results for aggregate {tle.source.ToString(true)}:");
                PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type, config);
            }
        }
        else if (tle.source.Type == TrackType.Album)
        {
            if (config.printOption.HasFlag(PrintOption.Json))
            {
                var albumsToPrint = config.printOption.HasFlag(PrintOption.Full)
                    ? tle.list
                    : tle.list.Take(1).ToList();

                JsonPrinter.PrintAlbumJson(albumsToPrint, tle.source);
            }
            else if (config.printOption.HasFlag(PrintOption.Link))
            {
                PrintAlbumLink(tle.list[0]);
            }
            else
            {
                if (!config.printOption.HasFlag(PrintOption.Full))
                    Console.WriteLine($"Result 1 of {tle.list.Count} for album {tle.source.ToString(true)}:");
                else
                    Console.WriteLine($"Results ({tle.list.Count}) for album {tle.source.ToString(true)}:");

                if (tle.list.Count > 0 && tle.list[0].Count > 0)
                {
                    if (!config.noBrowseFolder)
                        Console.WriteLine("[Skipping full folder retrieval]");

                    foreach (var ls in tle.list)
                    {
                        PrintAlbum(ls);

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


    public static void PrintComplete(TrackLists trackLists)
    {
        var ls = trackLists.Flattened(true, false);
        int successes = 0, fails = 0;
        foreach (var x in ls)
        {
            if (x.State == TrackState.Downloaded)
                successes++;
            else if (x.State == TrackState.Failed)
                fails++;
        }
        if (successes + fails > 1)
        {
            Console.WriteLine();
            Logger.Info($"Completed: {successes} succeeded, {fails} failed.");
        }
    }


    public static void PrintTracksTbd(List<Track> toBeDownloaded, List<Track> existing, List<Track> notFound, TrackType type, Config config, bool summary = true)
    {
        if (type == TrackType.Normal && !config.PrintTracks && toBeDownloaded.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool full = config.printOption.HasFlag(PrintOption.Full);
        bool allSkipped = existing.Count + notFound.Count > toBeDownloaded.Count;

        if (summary && (type == TrackType.Normal || skippedTracks.Length > 0))
            Logger.Info($"Downloading {toBeDownloaded.Count(x => !x.IsNotAudio)} tracks{skippedTracks}{(allSkipped ? '.' : ':')}");

        if (toBeDownloaded.Count > 0)
        {
            bool showAll = type != TrackType.Normal || config.PrintTracks || config.PrintResults;
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


    public static void PrintTrackResults(IEnumerable<(SearchResponse, Soulseek.File)> orderedResults, Track track, bool full = false, FileConditions? necCond = null, FileConditions? prefCond = null)
    {
        int count = 0;
        foreach (var (response, file) in orderedResults)
        {
            Console.WriteLine(Printing.DisplayString(track, file, response,
                full ? necCond : null, full ? prefCond : null,
                fullpath: full, infoFirst: true, showSpeed: full));
            count += 1;
        }
        WriteLine($"Total: {count}\n", ConsoleColor.Yellow);
    }


    public static void PrintLink(string username, string filename)
    {
        var link = $"slsk://{username}/{filename.Replace('\\', '/')}";
        Console.WriteLine(link);
    }


    public static void PrintAlbumLink(List<Track> albumTracks)
    {
        if (albumTracks.Count == 0) return;
        var username = albumTracks[0].FirstResponse.Username;
        var directory = Utils.GreatestCommonDirectorySlsk(albumTracks.Select(t => t.FirstDownload.Filename));
        var link = $"slsk://{username}/{directory.Replace('\\', '/').TrimEnd('/')}/";
        Console.WriteLine(link);
    }


    public static int PrintAlbum(List<Track> albumTracks, bool indices = false)
    {
        if (albumTracks.Count == 0 && albumTracks[0].Downloads.Count == 0)
            return 0;

        var response = albumTracks[0].FirstResponse;
        string noSlot = !response.HasFreeUploadSlot ? ", no upload slots" : "";
        string userInfo = $"{response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s{noSlot})";
        var (parents, propsList) = FolderInfo(albumTracks.Select(x => x.FirstDownload));

        string format = propsList.FirstOrDefault() ?? "";
        string otherProps = propsList.Count > 1 ? " / " + string.Join(" / ", propsList.Skip(1)) : "";

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"User  : {userInfo}\nFolder: {parents}\nProps : [");
        Console.ForegroundColor = GetFormatColor(format);
        Console.Write(format);
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(otherProps + "]");
        Console.ResetColor();
        PrintTracks(albumTracks.ToList(), pathsOnly: true, showAncestors: false, showUser: false, indices: true);

        return 3 + albumTracks.Count;
    }

    public static string FormatList<T>(ICollection<T> items, Func<T, string> format, string indent = "  ", int maxCount = 10)
    {
        var result = new System.Text.StringBuilder();

        int count = 1;

        foreach (var item in items)
        {
            if (count > 1)
            {
                result.Append('\n');
            }

            if (count > maxCount)
            {
                result.Append($"... and {items.Count - count} more");
                break;
            }

            result.Append(indent);
            result.Append(format(item));
            count += 1;
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
            "mp3" => ConsoleColor.DarkRed,
            "ogg" => ConsoleColor.DarkGreen,
            "wav" => ConsoleColor.White,
            "opus" => ConsoleColor.DarkBlue,
            "m4a" => ConsoleColor.Cyan,
            _ => ConsoleColor.Gray,
        };
    }

    public static void RefreshOrPrint(ProgressBar? progress, int current, string item, bool print = false, bool refreshIfOffscreen = false)
    {
        if (progress != null && !Console.IsOutputRedirected && (refreshIfOffscreen || progress.Y >= Console.WindowTop))
        {
            try { progress.Refresh(current, item); }
            catch { }

            if (print)
            {
                Logger.LogNonConsole(Logger.LogLevel.Info, item);
            }
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
                try
                {
                    return new ProgressBar(PbStyle.SingleLine, 100, Console.WindowWidth - 10, character: ' ');
                }
                catch
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }
    }
}
