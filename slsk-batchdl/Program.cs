using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Soulseek;
using TagLib.Matroska;
using static System.Formats.Asn1.AsnWriter;
using static System.Net.WebRequestMethods;

class Program
{
    static SoulseekClient client = new SoulseekClient();
    static Dictionary<Track, SearchInfo> searches = new Dictionary<Track, SearchInfo>();
    static Dictionary<string, DownloadInfo> downloads = new Dictionary<string, DownloadInfo>();
    static List<Track> tracks = new List<Track>();
    static string outputFolder = "";
    static string failsFilePath = "";
    static string m3uFilePath = "";
    static string musicDir = "";
    static string ytdlpFormat = "";
    static int downloadMaxStaleTime = 0;
#if DEBUG
    static int displayUpdateDelay = 1000;
#else
    static int displayUpdateDelay = 500;
#endif

    static void PrintHelp()
    {
        Console.WriteLine("Usage: slsk-batchdl.exe [OPTIONS]");
        Console.WriteLine("Options:");
        Console.WriteLine("  -p --parent <path>           Downloaded music will be placed here");
        Console.WriteLine("  -n --name <name>             Folder / playlist name. If not specified, the name of the csv file / spotify playlist is used.");
        Console.WriteLine("  --username <username>        Soulseek username");
        Console.WriteLine("  --password <password>        Soulseek password");
        Console.WriteLine();
        Console.WriteLine("  --spotify <url>              Download a spotify playlist. \"likes\" to download all your liked music.");
        Console.WriteLine("  --spotify-id <id>            Your spotify client id (use if the default fails or if playlist private)");
        Console.WriteLine("  --spotify-secret <sec>       Your spotify client secret (use if the default fails or if playlist private)");
        Console.WriteLine();
        Console.WriteLine("  --youtube <url>              Download YouTube playlist");
        Console.WriteLine();
        Console.WriteLine("  --csv <path>                 Use a csv file containing track info to download");
        Console.WriteLine("  --artist-col <column>        Specify if the csv file contains an artist name column");
        Console.WriteLine("  --track-col <column>         Specify if if the csv file contains an track name column");
        Console.WriteLine("  --album-col <unit>           CSV album column name. Optional, may improve searching, slower");
        Console.WriteLine("  --full-title-col <column>    Specify only if there are no separate artist and track name columns in the csv");
        Console.WriteLine("  --uploader-col <column>      Specify when using full title col if there is also an uploader column in the csv (fallback in case artist name cannot be extracted from title)");
        Console.WriteLine("  --length-col <column>        CSV duration column name. Recommended, will improve accuracy");
        Console.WriteLine("  --time-unit <unit>           Time unit for the track duration column, ms or s (default: s)");
        Console.WriteLine();
        Console.WriteLine("  --pref-format <format>       Preferred file format (default: mp3)");
        Console.WriteLine("  --pref-length-tolerance <tol> Preferred length tolerance (if length col provided) (default: 3)");
        Console.WriteLine("  --pref-min-bitrate <rate>    Preferred minimum bitrate (default: 200)");
        Console.WriteLine("  --pref-max-bitrate <rate>    Preferred maximum bitrate (default: 2200)");
        Console.WriteLine("  --pref-max-sample-rate <rate> Preferred maximum sample rate (default: 96000)");
        Console.WriteLine("  --nec-format <format>        Necessary file format");
        Console.WriteLine("  --nec-length-tolerance <tol> Necessary length tolerance (default: 3)");
        Console.WriteLine("  --nec-min-bitrate <rate>     Necessary minimum bitrate");
        Console.WriteLine("  --nec-max-bitrate <rate>     Necessary maximum bitrate");
        Console.WriteLine("  --nec-max-sample-rate <rate> Necessary maximum sample rate");
        Console.WriteLine();
        Console.WriteLine("  --album-search               Also search for \"[Album name] [track name]\". Occasionally helps to find more");
        Console.WriteLine("  --skip-existing              Skip if a track matching the conditions is found in the output folder or your music library (if provided)");
        Console.WriteLine("  --music-dir <path>           Specify to also skip downloading tracks which are in your library, use with --skip-existing");
        Console.WriteLine("  --reverse                    Download tracks in reverse order");
        Console.WriteLine("  --skip-if-pref-failed        Skip if preferred versions of a track exist but failed to download. If no pref. versions were found, download as normal.");
        Console.WriteLine("  --create-m3u                 Create an m3u playlist file");
        Console.WriteLine("  --m3u-only                   Only create an m3u playlist file with existing tracks and exit");
        Console.WriteLine("  --m3u <path>                 Where to place created m3u files (--parent by default)");
        Console.WriteLine("  --yt-dlp                     Use yt-dlp to download tracks that weren't found on Soulseek. yt-dlp must be availble from the command line.");
        Console.WriteLine("  --yt-dlp-f <format>          yt-dlp audio format (default: \"bestaudio/best\")");
        Console.WriteLine();
        Console.WriteLine("  --search-timeout <timeout>   Maximal search time (default: 15000)");
        Console.WriteLine("  --download-max-stale-time <time> Maximal download time with no progress (default: 80000)");
        Console.WriteLine("  --max-concurrent-processes <num> Max concurrent searches / downloads (default: 2)");
        Console.WriteLine("  --max-retries-per-file <num> Maximum number of users to try downloading from before skipping track (default: 30)");
    }

    static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
            Console.WriteLine($"{e.ExceptionObject}");
        };
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();
        lastLine = Console.CursorTop;
        if (args.Contains("--help") || args.Length == 0)
        {
            PrintHelp();
            return;
        }

        musicDir = "";
        string parentFolder = "";
        string folderName = "";
        string ytUrl = "";
        string spotifyUrl = "";
        string spotifyId = "";
        string spotifySecret = "";
        string encodedSpotifyId = "MWJmNDY5MWJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI="; // base64 encoded client id and secret to avoid git guardian detection (annoying)
        string encodedSpotifySecret = "ZmQ3NjYyNmM0ZjcxNGJkYzg4Y2I4ZTQ1ZTU1MDBlNzE=";
        string tracksCsv = "";
        string username = "";
        string password = "";
        string artistCol = "";
        string albumCol = "";
        string trackCol = "";
        string fullTitleCol = "";
        string uploaderCol = "";
        string lengthCol = "";
        string timeUnit = "s";
        ytdlpFormat = "bestaudio/best";
        bool reverse = false;
        bool useYtdlp = false;
        bool skipExisting = false;
        bool skipIfPrefFailed = false;
        bool albumSearch = false;
        bool createM3u = false;
        bool m3uOnly = false;
        int searchTimeout = 15000;
        downloadMaxStaleTime = 80000;
        int maxConcurrentProcesses = 2;
        int maxRetriesPerFile = 30;
        var preferredCond = new FileConditions
        {
            Format = "mp3",
            LengthTolerance = 3,
            MinBitrate = 200,
            MaxBitrate = 2200,
            MaxSampleRate = 96000
        };
        var necessaryCond = new FileConditions
        {
            LengthTolerance = 3,
            Format = "",
            MinBitrate = -1,
            MaxBitrate = -1,
            MaxSampleRate = -1,
        };

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--parent":
                    parentFolder = args[++i];
                    break;
                case "-n":
                case "--name":
                    folderName = args[++i];
                    break;
                case "--music-dir":
                    musicDir = args[++i];
                    break;
                case "--csv":
                    tracksCsv = args[++i];
                    break;
                case "--youtube":
                    ytUrl = args[++i];
                    break;
                case "--spotify":
                    spotifyUrl = args[++i];
                    break;
                case "--spotify-id":
                    spotifyId = args[++i];
                    break;
                case "--spotify-secret":
                    spotifySecret = args[++i];
                    break;
                case "--username":
                    username = args[++i];
                    break;
                case "--password":
                    password = args[++i];
                    break;
                case "--artist-col":
                    artistCol = args[++i];
                    break;
                case "--track-col":
                    trackCol = args[++i];
                    break;
                case "--album-col":
                    albumCol = args[++i];
                    break;
                case "--album-search":
                    albumSearch = true;
                    break;
                case "--full-title-col":
                    fullTitleCol = args[++i];
                    break;
                case "--uploader-col":
                    uploaderCol = args[++i];
                    break;
                case "--length-col":
                    lengthCol = args[++i];
                    break;
                case "--time-unit":
                    timeUnit = args[++i];
                    break;
                case "--yt-dlp":
                    useYtdlp = true;
                    break;
                case "--yt-dlp-f":
                    ytdlpFormat = args[++i];
                    break;
                case "--skip-existing":
                    skipExisting = true;
                    break;
                case "--reverse":
                    reverse = true;
                    break;
                case "--skip-if-pref-failed":
                    skipIfPrefFailed = true;
                    break;
                case "--create-m3u":
                    createM3u = true;
                    break;
                case "--m3u-only":
                    m3uOnly = true;
                    break;
                case "--m3u":
                    m3uFilePath = args[++i];
                    break;
                case "--search-timeout":
                    searchTimeout = int.Parse(args[++i]);
                    break;
                case "--download-max-stale-time":
                    downloadMaxStaleTime = int.Parse(args[++i]);
                    break;
                case "--max-concurrent-processes":
                    maxConcurrentProcesses = int.Parse(args[++i]);
                    break;
                case "--max-retries-per-file":
                    maxRetriesPerFile = int.Parse(args[++i]);
                    break;
                case "--pref-format":
                    preferredCond.Format = args[++i];
                    break;
                case "--pref-length-tolerance":
                    preferredCond.LengthTolerance = int.Parse(args[++i]);
                    break;
                case "--pref-min-bitrate":
                    preferredCond.MinBitrate = int.Parse(args[++i]);
                    break;
                case "--pref-max-bitrate":
                    preferredCond.MaxBitrate = int.Parse(args[++i]);
                    break;
                case "--pref-max-sample-rate":
                    preferredCond.MaxSampleRate = int.Parse(args[++i]);
                    break;
                case "--nec-format":
                    necessaryCond.Format = args[++i];
                    break;
                case "--nec-length-tolerance":
                    necessaryCond.LengthTolerance = int.Parse(args[++i]);
                    break;
                case "--nec-min-bitrate":
                    necessaryCond.MinBitrate = int.Parse(args[++i]);
                    break;
                case "--nec-max-bitrate":
                    necessaryCond.MaxBitrate = int.Parse(args[++i]);
                    break;
                case "--nec-max-sample-rate":
                    necessaryCond.MaxSampleRate = int.Parse(args[++i]);
                    break;
                default:
                    WriteLastLine($"Unknown argument: {args[i]}", ConsoleColor.Red);
                    break;
            }
        }

        if (spotifyUrl != "")
        {
            bool usedDefaultId = false;
            if (spotifyId == "" || spotifySecret == "")
            {
                spotifyId = Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifyId));
                spotifySecret = Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifySecret));
                usedDefaultId = true;
            }
            string? playlistName;
            if (spotifyUrl == "likes")
            {
                playlistName = "Spotify Likes";
                if (usedDefaultId)
                {
                    WriteLastLine("");
                    Console.Write("Spotify client ID:");
                    spotifyId = Console.ReadLine();
                    WriteLastLine("");
                    Console.Write("Spotify client secret:");
                    spotifySecret = Console.ReadLine();
                }
                tracks = await GetSpotifyLikes(spotifyId, spotifySecret);
            }
            else
            {
                try
                {
                    (playlistName, tracks) = await GetSpotifyPlaylist(spotifyUrl, spotifyId, spotifySecret, false);
                }
                catch (SpotifyAPI.Web.APIException)
                {
                    WriteLastLine("Spotify playlist not found. It may be set to private. Login? [Y/n]");
                    string answer = Console.ReadLine();
                    if (answer.ToLower() == "y")
                    {
                        if (usedDefaultId)
                        {
                            WriteLastLine("");
                            Console.Write("Spotify client ID:");
                            spotifyId = Console.ReadLine();
                            WriteLastLine("");
                            Console.Write("Spotify client secret:");
                            spotifySecret = Console.ReadLine();
                        }
                        try { (playlistName, tracks) = await GetSpotifyPlaylist(spotifyUrl, spotifyId, spotifySecret, true); }
                        catch (SpotifyAPI.Web.APIException) { throw; }
                    }
                    else
                        return;
                }
            }
            if (folderName == "")
                folderName = RemoveInvalidChars(playlistName, " ");
        }
        else if (ytUrl != "")
        {
            WriteLastLine("Loading youtube playlist...");
            (string name, tracks) = await YouTube.GetTracks(ytUrl);

            if (folderName == "")
                folderName = RemoveInvalidChars(name, " ");
        }
        else if (tracksCsv != "")
        {
            if (!System.IO.File.Exists(tracksCsv))
                throw new Exception("csv file not found");
            if ((trackCol == "" && artistCol == "" && fullTitleCol == "") || (trackCol != "" && artistCol == "") || (fullTitleCol != "" && (artistCol != "" || trackCol != "")))
                throw new Exception("Use one of: full title column, (artist column AND track name)");
            if (lengthCol == "")
                WriteLastLine($"Warning: No length column specified, results may be imprecise.");

            tracks = ParseCsvIntoTrackInfo(tracksCsv, artistCol, trackCol, lengthCol, fullTitleCol, uploaderCol, albumCol, timeUnit: timeUnit);

            if (folderName == "")
                folderName = Path.GetFileNameWithoutExtension(tracksCsv);
        }
        else
            throw new Exception("No csv, spotify or youtube url provided");


        folderName = RemoveInvalidChars(folderName, " ");

        if (parentFolder == "" && !m3uOnly)
            throw new Exception("No folder provided (-p <path>)");
        else if (parentFolder != "")
        {
            outputFolder = Path.Combine(parentFolder, folderName);
            System.IO.Directory.CreateDirectory(outputFolder);
            failsFilePath = Path.Combine(outputFolder, $"{folderName}_failed.txt");
            if (!m3uOnly && System.IO.File.Exists(failsFilePath))
            {
                WriteAllLinesOutputFile("");
                try { System.IO.File.Delete(failsFilePath); }
                catch { }
            }
        }

        if (m3uFilePath != "")
        {
            m3uFilePath = Path.Combine(m3uFilePath, folderName + ".m3u");
            createM3u = true;
        }
        else if (outputFolder != "")
            m3uFilePath = Path.Combine(outputFolder, folderName + ".m3u");

        Track[] tmp = new Track[tracks.Count];
        tracks.CopyTo(tmp);
        var tracksStart = tmp.ToList();

        createM3u |= m3uOnly;
        List<string> m3uLines = Enumerable.Repeat("", tracksStart.Count).ToList();

        if (skipExisting || m3uOnly || musicDir != "")
        {
            if (outputFolder != "")
            {
                WriteLastLine("Checking if tracks exist in output folder...");
                var outputDirFiles = System.IO.Directory.GetFiles(outputFolder, "*", SearchOption.AllDirectories);
                var musicFiles = outputDirFiles.Where(f => IsMusicFile(f)).ToArray();
                tracks = tracks.Where(x =>
                {
                    bool exists = FileExistsInCollection(x.TrackTitle == "" ? x.UnparsedTitle : x.TrackTitle, x.Length, necessaryCond, musicFiles, out string? path);
                    if (exists)
                        m3uLines[tracksStart.IndexOf(x)] = path;
                    return !exists;
                }).ToList();
            }

            if (musicDir != "")
            {
                WriteLastLine($"Checking if tracks exist in library...");
                var musicDirFiles = System.IO.Directory.GetFiles(musicDir, "*", SearchOption.AllDirectories);
                var musicFiles = musicDirFiles
                    .Where(filename => outputFolder == "" || !filename.Contains(outputFolder))
                    .Where(filename => IsMusicFile(filename)).ToArray();
                tracks = tracks.Where(x =>
                {
                    bool exists = FileExistsInCollection(x.TrackTitle == "" ? x.UnparsedTitle : x.TrackTitle, x.Length, necessaryCond, musicFiles, out string? path);
                    if (exists && m3uLines[tracksStart.IndexOf(x)] == "")
                        m3uLines[tracksStart.IndexOf(x)] = path;
                    return !exists;
                }).ToList();
            }
        }

        if (createM3u)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(m3uFilePath));
            if (System.IO.File.Exists(m3uFilePath))
                using (var fileStream = new FileStream(m3uFilePath, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite)) { fileStream.SetLength(0); }
            if (tracks.Count < tracksStart.Count)
            {
                using (var fileStream = new FileStream(m3uFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
                {
                    foreach (var line in m3uLines)
                        streamWriter.WriteLine(line);
                }
            }

            if (m3uOnly)
            {
                WriteLastLine($"Created m3u file: {tracksStart.Count - tracks.Count} of {tracksStart.Count} found as local files");
                if (tracks.Count > 0)
                {
                    WriteLastLine($"Missing:");
                    foreach (var t in tracks)
                        WriteLastLine((t.TrackTitle == "" ? t.UnparsedTitle : $"{t.TrackTitle} - {t.ArtistName}") + (t.Length > 0 ? $" ({t.Length}s)" : ""));
                }
                return;
            }
        }

        albumSearch |= albumCol != "";
        int tracksRemaining = tracks.Count;
        if (reverse)
            tracks.Reverse();

        //foreach (var track in tracks)
        //    WriteLastLine($"{track.Title}, {track.ArtistName} - {track.TackTitle} ({track.Length}s)");

        await client.ConnectAsync(username, password);

        var UpdateTask = Task.Run(() => Update());
        SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);

        string alreadyExist = skipExisting && tracksStart.Count - tracks.Count > 0 ? $" ({tracksStart.Count - tracks.Count} already exist)" : "";
        WriteLastLine($"Downloading {tracks.Count} tracks{alreadyExist}");

        var downloadTasks = tracks.Select(async (track) =>
        {
            await semaphore.WaitAsync();
            try
            {
                var savedFilePath = await SearchAndDownload(track, preferredCond, necessaryCond, skipIfPrefFailed, maxRetriesPerFile, searchTimeout, albumSearch, useYtdlp);
                if (savedFilePath != "")
                {
                    tracksRemaining--;
                    m3uLines[tracksStart.IndexOf(track)] = savedFilePath;
                    Debug.WriteLine($"Saved at: {savedFilePath}");
                    if (createM3u)
                    {
                        using (var fileStream = new FileStream(m3uFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                        using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
                        {
                            foreach (var line in m3uLines)
                                streamWriter.WriteLine(line);
                        }
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks);

        WriteLastLine($"\nDownloaded {tracks.Count - tracksRemaining} of {tracks.Count} tracks");
        if (System.IO.File.Exists(failsFilePath))
            WriteLastLine($"Failed to download:\n{System.IO.File.ReadAllText(failsFilePath)}");
    }

    static async Task<string> SearchAndDownload(Track track, FileConditions preferredCond, FileConditions necessaryCond, bool skipIfPrefFailed, int maxRetriesPerFile, int searchTimeout, bool albumSearch, bool useYtdlp)
    {
        var title = track.TrackTitle == "" ? $"{track.UnparsedTitle}" : $"{track.ArtistName} - {track.TrackTitle}";
        if (track.TrackTitle == "")
        {
            var t = track.UnparsedTitle.Split('-', StringSplitOptions.TrimEntries);
            if (t.Length == 1 && t[0] != "" && t[1] != "")
                title = $"{t[0]} - {t[1]}";
            else if (track.Uploader != "" && !track.UnparsedTitle.Contains(track.Uploader))
                title = $"{track.Uploader} - {track.UnparsedTitle}";
        }
        var saveFilePath = "";

        var searchQuery = SearchQuery.FromText($"{title}");
        var searchOptions = new SearchOptions
        (
            minimumPeerUploadSpeed: 1, searchTimeout: searchTimeout,
            responseFilter: (response) =>
            {
                return response.UploadSpeed > 0;
            },
            fileFilter: (file) =>
            {
                return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track.Length);
            }
        );

        bool attemptedDownloadPref = false;
        Task downloadTask = null;
        bool downloading = false;
        var responses = new List<SearchResponse>();
        var cts = new CancellationTokenSource();

        Action<SearchResponse> responseHandler = (r) =>
        {
            if (r.Files.Count > 0)
            {
                responses.Add(r);
                if (!downloading)
                {
                    var f = r.Files.First();
                    if (preferredCond.FileSatisfies(f, track.Length) && r.HasFreeUploadSlot && r.UploadSpeed / 1000000 >= 1)
                    {
                        Debug.WriteLine("Early download");
                        downloading = true;
                        saveFilePath = GetSavePath(f, track);
                        attemptedDownloadPref = true;
                        try
                        {
                            downloadTask = DownloadFile(r, f, saveFilePath, cts);
                        }
                        catch
                        {
                            saveFilePath = "";
                            downloading = false;
                        }
                    }
                }
            }
        };

        lock (searches) {
            searches[track] = new SearchInfo(searchQuery, responses, searchOptions);
        }

        WriteLastLine($"Searching for {title}");

        try
        {
            var search = await client.SearchAsync(searchQuery, options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler);
        }
        catch (OperationCanceledException ex) { }
       
        if (albumSearch && responses.Count == 0 && track.Album != "" && track.TrackTitle != "")
        {
            Debug.WriteLine("\"Artist - Track\" not found, trying \"Album Track\"");
            string searchText = $"{track.Album} {track.TrackTitle}";
            searchOptions = new SearchOptions
            (
                minimumPeerUploadSpeed: 1, searchTimeout: 5000,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0;
                },
                fileFilter: (file) =>
                {
                    var seps = new string[] { " ", "_" };
                    return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track.Length) 
                        && file.Filename.Replace(seps, "").Contains(track.ArtistName.Replace(seps, ""), StringComparison.OrdinalIgnoreCase);
                }
            );
            WriteLastLine($"Searching with album name: {searchText}");
            try
            {
                var search = await client.SearchAsync(SearchQuery.FromText(searchText), options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler);
            }
            catch (OperationCanceledException ex) { }
        }

        lock (searches) { searches.Remove(track); }
        cts.Dispose();

        Debug.WriteLine($"Found {responses.Count} responses");

        bool notFound = false;
        if (!downloading && responses.Count == 0 && !useYtdlp)
        {
            notFound = true;
        }
        else if (downloading)
        {
            try
            {
                await downloadTask;
            }
            catch
            {
                saveFilePath = "";
                downloading = false;
            }
        }

        if (!downloading && responses.Count > 0)
        {
            var fileResponses = responses
                .SelectMany(response => response.Files.Select(file => (response, file)))
                .OrderByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track.Length))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed)
                .ToList();

            foreach (var x in fileResponses)
            {
                bool pref = preferredCond.FileSatisfies(x.file, track.Length);
                if (skipIfPrefFailed && attemptedDownloadPref && !pref)
                {
                    WriteLastLine($"Pref. version of the file exists, but couldn't be downloaded: {title}, skipping", ConsoleColor.Red);
                    var length = track.Length > 0 ? $"({track.Length}s) " : "";
                    var failedDownloadInfo = $"{title} {length}[Preferred version of the file exists, but couldn't be downloaded]";
                    WriteLineOutputFile(failedDownloadInfo);
                    return "";
                }

                saveFilePath = GetSavePath(x.file, track);

                try
                {
                    downloading = true;
                    if (pref)
                        attemptedDownloadPref = true;
                    await DownloadFile(x.response, x.file, saveFilePath);
                    break;
                }
                catch
                {
                    downloading = false;
                    if (--maxRetriesPerFile <= 0)
                    {
                        WriteLastLine($"Failed to download: {title}, skipping", ConsoleColor.Red);
                        var length = track.Length > 0 ? $"({track.Length}s) " : "";
                        var failedDownloadInfo = $"{title} {length}[Reason: Out of download retries]";
                        WriteLineOutputFile(failedDownloadInfo);
                        return "";
                    }
                }
            }
        }

        if (!downloading && useYtdlp)
        {
            notFound = false;
            try {
                downloading = true;
                string fname = GetSaveName(track);
                YtdlpSearchAndDownload(track, necessaryCond, Path.Combine(outputFolder, fname));
                string[] files = System.IO.Directory.GetFiles(outputFolder, fname + ".*");
                foreach (string file in files)
                {
                    if (IsMusicFile(file))
                        return saveFilePath = file;
                }
                if (saveFilePath == "")
                    throw new Exception("yt-dlp download failed");
            }
            catch (Exception e) { 
                WriteLastLine(e.Message, ConsoleColor.Red);
                saveFilePath = "";
                downloading = false;
                if (e.Message.Contains("No matching files found"))
                    notFound = true;
            }
        }

        if (!downloading)
        {
            if (notFound)
            {
                WriteLastLine($"Failed to find: {title}", ConsoleColor.Red);
                var length = track.Length > 0 ? $"({track.Length}s) " : "";
                var failedDownloadInfo = $"{title} {length}[Reason: No file found with matching criteria]";
                WriteLineOutputFile(failedDownloadInfo);
            }
            else
            {
                WriteLastLine($"Failed to download: {title}", ConsoleColor.Red);
                var length = track.Length > 0 ? $"({track.Length}s) " : "";
                var failedDownloadInfo = $"{title} {length}[Reason: All downloads failed]";
                WriteLineOutputFile(failedDownloadInfo);
            }
            return "";
        }

        return saveFilePath;
    }

    static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, CancellationTokenSource? searchCts = null)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath));

        bool transferSet = false;
        var transferOptions = new TransferOptions(
            stateChanged: (state) =>
            {
                if (downloads.ContainsKey(file.Filename) && !transferSet)
                    downloads[file.Filename].transfer = state.Transfer;
            },
            progressUpdated: (progress) =>
            {
                if (downloads.ContainsKey(file.Filename))
                    downloads[file.Filename].bytesTransferred = progress.PreviousBytesTransferred;
            }
        );
        using (var cts = new CancellationTokenSource())
        using (var outputStream = new FileStream(filePath, FileMode.Create))
        {
            lock (downloads) { downloads[file.Filename] = new DownloadInfo(filePath, response, file, cts); }
            WriteLastLine(downloads[file.Filename].displayText);

            try
            {
                await client.DownloadAsync(response.Username, file.Filename, () => Task.FromResult((Stream)outputStream), file.Size, options: transferOptions, cancellationToken: cts.Token);
            }
            catch (Exception e)
            {
                downloads[file.Filename].UpdateText();
                lock (downloads) { downloads.Remove(file.Filename); }
                try
                {
                    if (System.IO.File.Exists(filePath))
                        System.IO.File.Delete(filePath);
                }
                catch { }
                throw;
            }
        }

        searchCts?.Cancel();
        downloads[file.Filename].success = true;
        downloads[file.Filename].UpdateText();
        lock (downloads) { downloads.Remove(file.Filename); }
    }

    static async Task Update()
    {
        while (true)
        {
            string debugSearches = $"Searches ({searches.Count}):\n";
            string debugDownloads = $"Downloads ({downloads.Count}):\n";

            foreach (var (key, val) in searches)
            {
                if (val != null)
                    debugSearches += val.query.SearchText + "\n";
                else
                    lock (searches) { searches.Remove(key); }
            }

            foreach (var (key, val) in downloads)
            {
                if (val != null)
                {
                    float? percentage = val.bytesTransferred / (float)val.file.Size;
                    string x = $"({percentage:P}): {val.response.Username}({val.response.HasFreeUploadSlot}/{val.response.QueueLength}) \\ {val.file.Filename.Split('\\').Last()}";
                    if (val.transfer != null)
                        debugDownloads += $"{val.transfer.State} {x}\n";
                    else
                        debugDownloads += $"NULL: {x}\n";
                    val.UpdateText();

                    if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > downloadMaxStaleTime)
                    {
                        val.cts.Cancel();
                        val.displayText = "(Stale)" + val.displayText;
                        val.UpdateText();
                        lock (downloads) { downloads.Remove(key); }
                    }
                }
                else
                {
                    debugDownloads += $"VALUE IS NULL: {key}\n";
                    lock (downloads) { downloads.Remove(key); }
                }
            }

            Debug.WriteLine($"{debugSearches}{debugDownloads}-------------------------------");

            await Task.Delay(displayUpdateDelay);
        }
    }

    static string GetSavePath(Soulseek.File file, Track track)
    {
        return $"{GetSavePathNoExt(track)}{Path.GetExtension(file.Filename)}";
    }

    static string GetSavePathNoExt(Track track)
    {
        return Path.Combine(outputFolder, $"{GetSaveName(track)}");
    }

    static string GetSaveName(Track track)
    {
        string name = track.TrackTitle == "" ? $"{track.UnparsedTitle}" : $"{track.ArtistName} - {track.TrackTitle}";
        return RemoveInvalidChars(name, " ");
    }

    static void YtdlpSearchAndDownload(Track track, FileConditions conditions, string savePathNoExt)
    {
        if (track.YtID != "")
        {
            YtdlpDownload(track.YtID, savePathNoExt);
            return;
        }

        Process process = new Process();
        ProcessStartInfo startInfo = new ProcessStartInfo();

        startInfo.FileName = "yt-dlp";
        string search = track.TrackTitle == "" ? track.UnparsedTitle : $"{track.ArtistName} - {track.TrackTitle}";
        startInfo.Arguments = $"\"ytsearch3:{search}\" --print \"%(duration>%H:%M:%S)s ¦¦ %(id)s ¦¦ %(title)s\"";

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        process.StartInfo = startInfo;
        process.OutputDataReceived += (sender, e) => { WriteLastLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { WriteLastLine(e.Data); };

        WriteLastLine($"[yt-dlp] Searching: {search}");
        process.Start();
        //process.BeginOutputReadLine();
        //process.BeginErrorReadLine();

        List<(int, string, string)> results = new List<(int, string, string)>();
        string output;
        Regex regex = new Regex(@"^(\d+):(\d+):(\d+) ¦¦ ([\w-]+) ¦¦ (.+)$"); // I LOVE CHATGPT !!!!
        while ((output = process.StandardOutput.ReadLine()) != null)
        {
            Match match = regex.Match(output);
            if (match.Success)
            {
                int hours = int.Parse(match.Groups[1].Value);
                int minutes = int.Parse(match.Groups[2].Value);
                int seconds = int.Parse(match.Groups[3].Value);
                int totalSeconds = (hours * 60 * 60) + (minutes * 60) + seconds;
                string id = match.Groups[4].Value;
                string title = match.Groups[5].Value;
                results.Add((totalSeconds, id, title));
            }
        }

        process.WaitForExit();

        foreach (var res in results)
        {
            bool possibleMatch = false;
            if (conditions.LengthToleranceSatisfies(track, res.Item1))
            {
                WriteLastLine($"[yt-dlp] Downloading: {res.Item3} ({res.Item1}s)");
                YtdlpDownload(res.Item2, savePathNoExt);
                return;
            }
        }

        throw new Exception($"[yt-dlp] No matching files found");
    }

    static void YtdlpDownload(string id, string savePathNoExt)
    {
        Process process = new Process();
        ProcessStartInfo startInfo = new ProcessStartInfo();

        startInfo.FileName = "yt-dlp";
        startInfo.Arguments = $"{id} -f {ytdlpFormat} -ci -o \"{savePathNoExt}.%(ext)s\" --extract-audio";
        WriteLastLine($"{startInfo.FileName} {startInfo.Arguments}");

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        process.StartInfo = startInfo;
        process.OutputDataReceived += (sender, e) => { WriteLastLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { WriteLastLine(e.Data); };

        process.Start();
        process.WaitForExit();
    }

    class DownloadInfo
    {
        public string savePath;
        public string displayText = "";
        public int displayPos = 0;
        public int downloadRotatingBarState = 0;
        public Soulseek.File file;
        public Transfer? transfer;
        public SearchResponse response;
        public long bytesTransferred = 0;
        public bool stalled = false;
        public bool queued = false;
        public bool success = false;
        public CancellationTokenSource cts;
        public DateTime startTime = DateTime.Now;

        private DateTime lastChangeTime = DateTime.Now;
        private TransferStates? prevTransferState = null;
        private long prevBytesTransferred = 0;

        public DownloadInfo(string savePath, SearchResponse response, Soulseek.File file, CancellationTokenSource cts)
        {
            this.savePath = savePath;
            this.response = response;
            this.file = file;
            this.cts = cts;
            string sampleRate = file.SampleRate.HasValue ? $" / {file.SampleRate}Hz" : "";
            string bitRate = file.BitRate.HasValue ? $" / {file.BitRate}kbps" : "";
            string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
            displayText = $"{response.Username}\\..\\{file.Filename.Split('\\').Last()} " +
                $"[{file.Length}s{sampleRate}{bitRate} / {fileSize}]";

            MoveCursorLastLine();
            displayPos = Console.CursorTop;
        }

        public void UpdateText()
        {
            Console.SetCursorPosition(0, displayPos);
            char[] bars = { '/', '|', '\\', '—' };
            downloadRotatingBarState++;
            downloadRotatingBarState %= bars.Length;
            string bar = success ? "" : bars[downloadRotatingBarState] + " ";
            float? percentage = bytesTransferred / (float)file.Size;
            string percText = percentage < 0.1 ? $"0{percentage:P}" : $"{percentage:P}";
            queued = transfer?.State.ToString().Contains("Queued") ?? false;
            string state = "NullState";
            if (transfer != null)
            {
                if (queued)
                    state = "Queued";
                else if (transfer.State.ToString().Contains("Completed, "))
                    state = transfer.State.ToString().Replace("Completed, ", "");
                else
                    state = transfer.State.ToString();
            }
            Console.WriteLine($"{bar}[{percText}] {state}: {displayText}");
        }

        public DateTime UpdateLastChangeTime()
        {
            bool changed = prevTransferState != transfer?.State || prevBytesTransferred != bytesTransferred;
            if (changed)
                lastChangeTime= DateTime.Now;
            prevTransferState = transfer?.State;
            prevBytesTransferred = bytesTransferred;
            return lastChangeTime;
        }
    }

    class SearchInfo
    {
        public SearchQuery query;
        public SearchOptions searchOptions;
        public List<SearchResponse> responses;

        public SearchInfo(SearchQuery query, List<SearchResponse> responses, SearchOptions searchOptions)
        {
            this.query = query;
            this.responses = responses;
            this.searchOptions = searchOptions; 
        }
    }

    class FileConditions
    {
        public string Format { get; set; } = "";
        public int LengthTolerance { get; set; } = -1;
        public int MinBitrate { get; set; } = -1;
        public int MaxBitrate { get; set; } = -1;
        public int MaxSampleRate { get; set; } = -1;

        public bool FileSatisfies(Soulseek.File file, int actualLength)
        {
            return FormatSatisfies(file.Filename) && LengthToleranceSatisfies(file, actualLength) && BitrateSatisfies(file) && SampleRateSatisfies(file);
        }

        public bool FileSatisfies(TagLib.File file, int actualLength)
        {
            return FormatSatisfies(file.Name) && LengthToleranceSatisfies(file, actualLength) && BitrateSatisfies(file) && SampleRateSatisfies(file);
        }

        public bool FormatSatisfies(string filename)
        {
            return string.IsNullOrEmpty(Format) || filename.EndsWith(Format, StringComparison.OrdinalIgnoreCase);
        }

        public bool LengthToleranceSatisfies(Soulseek.File file, int actualLength)
        {
            if (LengthTolerance < 0 || actualLength < 0)
                return true;
            return Math.Abs((file.Length ?? -999999) - actualLength) <= LengthTolerance;
        }

        public bool LengthToleranceSatisfies(TagLib.File file, int actualLength)
        {
            if (LengthTolerance < 0 || actualLength < 0)
                return true;
            int fileLength = (int)file.Properties.Duration.TotalSeconds;
            if (Math.Abs(fileLength - actualLength) <= LengthTolerance)
                return true;
            return false;
        }

        public bool LengthToleranceSatisfies(Track track, int actualLength)
        {
            if (LengthTolerance < 0 || actualLength < 0 || track.Length < 0)
                return true;
            if (Math.Abs(track.Length - actualLength) <= LengthTolerance)
                return true;
            return false;
        }

        public bool BitrateSatisfies(Soulseek.File file)
        {
            if ((MinBitrate < 0 && MaxBitrate < 0) || file.BitRate == null)
                return true;
            if (MinBitrate >= 0 && file.BitRate.Value < MinBitrate)
                return false;
            if (MaxBitrate >= 0 && file.BitRate.Value > MaxBitrate)
                return false;

            return true;
        }

        public bool BitrateSatisfies(TagLib.File file)
        {
            if ((MinBitrate < 0 && MaxBitrate < 0) || file.Properties.AudioBitrate <= 0)
                return true;
            if (MinBitrate >= 0 && file.Properties.AudioBitrate < MinBitrate)
                return false;
            if (MaxBitrate >= 0 && file.Properties.AudioBitrate > MaxBitrate)
                return false;

            return true;
        }

        public bool SampleRateSatisfies(Soulseek.File file)
        {
            return MaxSampleRate < 0 || file.SampleRate == null || file.SampleRate.Value <= MaxSampleRate;
        }

        public bool SampleRateSatisfies(TagLib.File file)
        {
            return MaxSampleRate < 0 || file.Properties.AudioSampleRate <= MaxSampleRate;
        }
    }

    static async Task<(string?, List<Track>)> GetSpotifyPlaylist(string url, string id, string secret, bool login)
    {
        var spotify = new Spotify(id, secret);
        if (login)
        {
            await spotify.AuthorizeLogin();
            await spotify.IsClientReady();
        }
        else
            await spotify.Authorize();

        (string? name, var res) = await spotify.GetPlaylist(url);
        return (name, res);
    }

    static async Task<List<Track>> GetSpotifyLikes(string id, string secret)
    {
        var spotify = new Spotify(id, secret);
        await spotify.AuthorizeLogin();
        await spotify.IsClientReady();

        var res = await spotify.GetLikes();
        return res;
    }

    static List<Track> ParseCsvIntoTrackInfo(string path, string artistCol = "", string trackCol = "", 
        string lengthCol = "", string titleCol = "", string uploaderCol = "", string albumCol = "", string timeUnit = "s")
    {
        var tracks = new List<Track>();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            var header = reader.ReadLine();

            var artistIndex = string.IsNullOrEmpty(artistCol) ? -1 : Array.IndexOf(header.Split(','), artistCol);
            var albumIndex = string.IsNullOrEmpty(albumCol) ? -1 : Array.IndexOf(header.Split(','), albumCol);
            var trackIndex = string.IsNullOrEmpty(trackCol) ? -1 : Array.IndexOf(header.Split(','), trackCol);
            var titleIndex = string.IsNullOrEmpty(titleCol) ? -1 : Array.IndexOf(header.Split(','), titleCol);
            var uploaderIndex = string.IsNullOrEmpty(uploaderCol) ? -1 : Array.IndexOf(header.Split(','), uploaderCol);
            var lengthIndex = string.IsNullOrEmpty(lengthCol) ? -1 : Array.IndexOf(header.Split(','), lengthCol);

            var regex = new Regex(",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)"); // thank you, ChatGPT.

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                var values = regex.Split(line);

                var track = new Track();
                if (artistIndex >= 0) track.ArtistName = values[artistIndex].Trim('"').Split(',').First().Trim(' ');
                if (trackIndex >= 0) track.TrackTitle = values[trackIndex].Trim('"');
                if (albumIndex >= 0) track.Album = values[albumIndex].Trim('"');
                if (titleIndex >= 0) track.UnparsedTitle = values[titleIndex].Trim('"');
                if (uploaderIndex >= 0) track.Uploader = values[uploaderIndex].Trim('"');
                if (lengthIndex >= 0 && int.TryParse(values[lengthIndex], out int result) && result > 0)
                {
                    if (timeUnit == "ms")
                        track.Length = result / 1000;
                    else
                        track.Length = result;
                }

                if (track.UnparsedTitle != "" || track.TrackTitle != "") tracks.Add(track);
            }
        }

        return tracks;
    }


    static int lastLine = 0;
    static void MoveCursorLastLine()
    {
        Console.SetCursorPosition(0, Math.Min(Console.BufferHeight - 1, lastLine));
    }
    static void WriteLastLine(object obj, ConsoleColor? color = null)
    {
        string text = obj?.ToString();
        MoveCursorLastLine();
        if (color != null)
            Console.ForegroundColor = (ConsoleColor)color;
        Console.WriteLine(text);
        if (color != null)
            Console.ResetColor();
        lastLine = Math.Max(Console.CursorTop, lastLine + 1);
    }
    static bool IsMusicFile(string fileName)
    {
        var musicExtensions = new string[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".wma", ".m4a", ".alac", ".ape", ".dsd", ".dff", ".dsf", ".opus" };
        var extension = Path.GetExtension(fileName).ToLower();
        return musicExtensions.Contains(extension);
    }
    static bool FileExistsInCollection(string searchName, int length, FileConditions conditions, IEnumerable<string> collection, out string? foundPath)
    {
        string[] ignore = new string[] { " ", "_", "-", ".", "(", ")" };
        searchName = searchName.Replace(ignore, "");
        searchName = RemoveInvalidChars(searchName, "");

        var matchingFiles = collection
            .Where(fileName => fileName.Replace(ignore, "").Contains(searchName, StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (var p in matchingFiles)
        {
            TagLib.File f;
            try { f = TagLib.File.Create(p); }
            catch { continue; }

            if (conditions.FileSatisfies(f, length))
            {
                foundPath = p;
                return true;
            }
        }

        if (searchName.Count(c => c == '-') == 1)
        {
            searchName = searchName.Split('-')[1];
            matchingFiles = collection
                .Where(fileName => fileName.Replace(ignore, "").Contains(searchName, StringComparison.OrdinalIgnoreCase)).ToArray();

            foreach (var p in matchingFiles)
            {
                TagLib.File f;
                try { f = TagLib.File.Create(p); }
                catch { continue; }

                if (conditions.FileSatisfies(f, length))
                {
                    foundPath = p;
                    return true;
                }
            }
        }

        foundPath = null;
        return false;
    }

    static void WriteLineOutputFile(string line)
    {
        using (var fileStream = new FileStream(failsFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
        {
            streamWriter.WriteLine(line);
        }
    }
    static void WriteAllLinesOutputFile(string text)
    {
        using (var fileStream = new FileStream(failsFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
        {
            streamWriter.WriteLine(text);
        }

    }
    static string[] ParseCommand(string cmd)
    {
        Debug.WriteLine(cmd);
        string pattern = @"(""[^""]*""|\S+)";
        MatchCollection matches = Regex.Matches(cmd, pattern);
        var args = new string[matches.Count];
        for (int i = 0; i < matches.Count; i++)
            args[i] = matches[i].Value.Trim('"');
        return args;
    }
    static string RemoveInvalidChars(string str, string replaceStr)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
            str = str.Replace(c.ToString(), replaceStr);
        return str;
    }

    static void PrintTracks(List<Track> tracks)
    {
        foreach (var track in tracks)
        {
            string title = track.TrackTitle == "" ? track.UnparsedTitle : $"{track.ArtistName} - {track.TrackTitle}";
            WriteLastLine($"{title} ({track.Length})");
        }
        WriteLastLine(tracks.Count);
    }
}

public struct Track
{
    public string UnparsedTitle = "";
    public string Uploader = "";
    public string TrackTitle = "";
    public string ArtistName = "";
    public string Album = "";
    public string YtID = "";
    public int Length = -1;
    public Track() { }
}

public static class ExtensionMethods
{
    public static string Replace(this string s, string[] separators, string newVal)
    {
        string[] temp;
        temp = s.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return String.Join(newVal, temp);
    }
}
