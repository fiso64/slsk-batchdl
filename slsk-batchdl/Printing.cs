using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Konsole;
using Soulseek;
using System.Text.RegularExpressions;

using Data;
using Enums;

using Directory = System.IO.Directory;
using File = System.IO.File;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlResponse = Soulseek.SearchResponse;

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


    public static void PrintTracks(List<Track> tracks, int number = int.MaxValue, bool fullInfo = false, bool pathsOnly = false, bool showAncestors = true, bool infoFirst = false, bool showUser = true)
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
                Console.WriteLine();
            }
        }

        if (number < tracks.Count)
            Console.WriteLine($"  ... (etc)");
    }


    public static async Task PrintResults(TrackListEntry tle, List<Track> existing, List<Track> notFound)
    {
        await Program.InitClientAndUpdateIfNeeded();

        if (tle.source.Type == TrackType.Normal)
        {
            await Search.SearchAndPrintResults(tle.list[0]);
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


    public static void PrintComplete(TrackLists trackLists)
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


    public static void PrintTracksTbd(List<Track> toBeDownloaded, List<Track> existing, List<Track> notFound, TrackType type, bool summary = true)
    {
        if (type == TrackType.Normal && !Config.PrintTracks && toBeDownloaded.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool full = Config.printOption.HasFlag(PrintOption.Full);
        bool allSkipped = existing.Count + notFound.Count > toBeDownloaded.Count;

        if (summary && (type == TrackType.Normal || skippedTracks.Length > 0))
            Console.WriteLine($"Downloading {toBeDownloaded.Count(x => !x.IsNotAudio)} tracks{skippedTracks}{(allSkipped ? '.' : ':')}");

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
        Console.WriteLine();
    }


    public static void PrintAlbum(List<Track> albumTracks)
    {
        if (albumTracks.Count == 0 && albumTracks[0].Downloads.Count == 0)
            return;

        var response = albumTracks[0].FirstResponse;
        string userInfo = $"{response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)";
        var (parents, props) = FolderInfo(albumTracks.Select(x => x.FirstDownload));

        WriteLine($"User  : {userInfo}\nFolder: {parents}\nProps : {props}", ConsoleColor.White);
        PrintTracks(albumTracks.ToList(), pathsOnly: true, showAncestors: false, showUser: false);
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
            .OrderByDescending(g => Utils.IsMusicExtension(g.Key)).ThenByDescending(g => g.Count()).First().Key.TrimStart('.');

        res = $"[{mostCommonExtension.ToUpper()} / {totalLengthFormatted}";

        if (modeSampleRate.HasValue)
            res += $" / {(modeSampleRate.Value / 1000.0).Normalize()} kHz";

        if (meanBitrate.HasValue)
            res += $" / {(int)meanBitrate.Value} kbps";

        res += $" / {totalFileSizeInMB:F2} MB]";

        string gcp = Utils.GreatestCommonDirectory(files.Select(x => x.Filename)).TrimEnd('\\');

        var discPattern = new Regex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$");
        int lastIndex = gcp.LastIndexOf('\\');
        if (lastIndex != -1)
        {
            int secondLastIndex = gcp.LastIndexOf('\\', lastIndex - 1);
            gcp = secondLastIndex == -1 ? gcp[(lastIndex + 1)..] : gcp[(secondLastIndex + 1)..];
        }

        return (gcp, res);
    }


    public static void RefreshOrPrint(ProgressBar? progress, int current, string item, bool print = false, bool refreshIfOffscreen = false)
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
            Program.skipUpdate = true;
            lock (consoleLock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(value);
                Console.ResetColor();
            }

            Program.skipUpdate = false;
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
}

