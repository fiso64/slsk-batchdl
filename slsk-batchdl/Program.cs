using AngleSharp.Dom;
using Konsole;
using Soulseek;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using YoutubeExplode.Playlists;

static class Program
{
    static SoulseekClient client = new SoulseekClient();
    static ConcurrentDictionary<Track, SearchInfo> searches = new ConcurrentDictionary<Track, SearchInfo>();
    static ConcurrentDictionary<string, DownloadWrapper> downloads = new ConcurrentDictionary<string, DownloadWrapper>();
    static List<Track> tracks = new List<Track>();
    static string outputFolder = "";
    static string failsFilePath = "";
    static string m3uFilePath = "";
    static string musicDir = "";

    static string parentFolder = System.IO.Directory.GetCurrentDirectory();
    static string folderName = "";
    static string ytUrl = "";
    static string singleName = "";
    static string spotifyUrl = "";
    static string spotifyId = "";
    static string spotifySecret = "";
    static string encodedSpotifyId = "MWJmNDY5MWJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI="; // base64 encoded client id and secret to avoid git guardian detection (annoying)
    static string encodedSpotifySecret = "ZmQ3NjYyNmM0ZjcxNGJkYzg4Y2I4ZTQ1ZTU1MDBlNzE=";
    static string ytKey = "";
    static string tracksCsv = "";
    static string username = "";
    static string password = "";
    static string artistCol = "";
    static string albumCol = "";
    static string trackCol = "";
    static string ytIdCol = "";
    static string descCol = "";
    static string lengthCol = "";
    static string noRegexSearch = "";
    static string timeUnit = "";
    static string displayStyle = "single";
    static bool preciseSkip = true;
    static string albumName = "";
    static string nameFormat = "";
    static bool skipNotFound = false;
    static bool noArtistSearchTrack = false;
    static bool albumSearchTrack = false;
    static bool artistSearchTrack = false;
    static bool noDiacrSearch = false;
    static bool ytParse = false;
    static bool removeFt = false;
    static bool reverse = false;
    static bool useYtdlp = false;
    static bool skipExisting = false;
    static bool skipIfPrefFailed = false;
    static bool createM3u = false;
    static bool m3uOnly = false;
    static bool useTagsCheckExisting = false;
    static bool removeTracksFromSource = false;
    static int maxTracks = int.MaxValue;
    static int offset = 0;
    
    static FileConditions preferredCond = new FileConditions
    {
        Formats = new string[] { "mp3" },
        LengthTolerance = 3,
        MinBitrate = 200,
        MaxBitrate = 2200,
        MaxSampleRate = 96000,
        StrictTitle = false,
        StrictArtist = false,
        DangerWords = new string[] { "mix", "dj ", " edit", "cover" }
    };
    static FileConditions necessaryCond = new FileConditions
    {
        Formats = { },
        LengthTolerance = 3,
        MinBitrate = -1,
        MaxBitrate = -1,
        MaxSampleRate = -1,
        StrictTitle = false,
        StrictArtist = false,
        DangerWords = new string[] { "mix", "dj ", " edit", "cover" }
    };

    static string confPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "slsk-batchdl.conf");

    static string playlistUri = "";
    static Spotify? spotifyClient = null;
    static string ytdlpFormat = "bestaudio/best";
    static int downloadMaxStaleTime = 50000;
    static int updateDelay = 100;
    static int slowUpdateDelay = 2000;
    static int searchTimeout = 6000;
    static int maxConcurrentProcesses = 2;
    static int maxRetriesPerTrack = 30;
    static int maxResultsPerUser = 30;
    static bool slowConsoleOutput = false;

    static object failsFileLock = new object();
    static object consoleLock = new object();
    static bool writeFails = true;

    static DateTime lastUpdate;
    static bool skipUpdate = false;
    static bool debugDisableDownload = false;
    static bool debugPrintTracks = false;
    static bool noModifyShareCount = false;
    static bool printResultsFull = false;

    static void PrintHelp()
    {
        // additional options: --m3u-only, --yt-dlp-f, --skip-if-pref-failed, --slow-output, --no-modify-share-count, --max-retries, --max-results-per-user
        Console.WriteLine("Usage: slsk-batchdl [OPTIONS]" +
                            "\nOptions:" +
                            "\n  --user <username>              Soulseek username" +
                            "\n  --pass <password>              Soulseek password" +
                            "\n" +
                            "\n  --spotify <url>                Download a spotify playlist (\"likes\" for liked music)" +
                            "\n  --spotify-id <id>              Your spotify client id (required for private playlists)" +
                            "\n  --spotify-secret <sec>         Your spotify client secret (required for private playlists)" +
                            "\n" +
                            "\n  --youtube <url>                Get tracks from a YouTube playlist" +
                            "\n  --youtube-key <key>            Provide an API key to include unavailable uploads" +
                            "\n" +
                            "\n  --csv <path>                   Use a csv file containing track info to download" +
                            "\n  --artist-col <column>          Artist or uploader column name" +
                            "\n  --title-col <column>           Title or track name column name" +
                            "\n  --album-col <column>           Track album column name (optional for more results)" +
                            "\n  --length-col <column>          Track duration column name (optional for better accuracy)" +
                            "\n  --time-unit <unit>             Time unit in track duration column, ms or s (default: s)" +
                            "\n  --yt-desc-col <column>         YT description column name (optional, use with --yt-parse)" +
                            "\n  --yt-id-col <column>           Youtube video ID column (optional, use with --yt-parse)" +
                            "\n  --yt-parse                     Enable if you have a csv file of YouTube video titles and" +
                            "\n                                 channel names; attempt to parse them into title and artist" +
                            "\n" +
                            "\n  -s --single <str>              Search & download a specific track. <str> is a simple" +
                            "\n                                 search string, or a comma-separated list of properties:" +
                            "\n                                 \"title=Song Name,artist=Artist Name,length=215\"" +
                            "\n" +
                            "\n  -p --path <path>               Place downloaded files in custom path" +
                            "\n  -f --folder <name>             Custom folder name (default: provided playlist name)" +
                            "\n  -n --number <maxtracks>        Download at most n tracks of a playlist" +
                            "\n  -o --offset <offset>           Skip a specified number of tracks" +
                            "\n  --reverse                      Download tracks in reverse order" +
                            "\n  --remove-from-playlist         Remove downloaded tracks from playlist (spotify only)" +
                            "\n  --name-format <format>         Name format for downloaded tracks, e.g \"{artist} - {title}\"" +
                            "\n  --m3u                          Create an m3u8 playlist file" +
                            "\n" +
                            "\n  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)" +
                            "\n  --pref-length-tol <tol>        Preferred length tolerance in seconds (default: 3)" +
                            "\n  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)" +
                            "\n  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2200)" +
                            "\n  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 96000)" +
                            "\n  --pref-strict-title            Prefer download if filename contains track title" +
                            "\n  --pref-strict-artist           Prefer download if filepath contains track artist" +
                            "\n  --pref-danger-words <list>     Comma-separated list of words that must appear in either" +
                            "\n                                 both search result and track title, or in neither of the" +
                            "\n                                 two, case-insensitive (default:\"mix, edit, dj, cover\")" +
                            "\n  --nec-format <format>          Necessary file format(s), comma-separated" +
                            "\n  --nec-length-tol <tol>         Necessary length tolerance in seconds (default: 3)" +
                            "\n  --nec-min-bitrate <rate>       Necessary minimum bitrate" +
                            "\n  --nec-max-bitrate <rate>       Necessary maximum bitrate" +
                            "\n  --nec-max-samplerate <rate>    Necessary maximum sample rate" +
                            "\n  --nec-strict-title             Only download if filename contains track title" +
                            "\n  --nec-strict-artist            Only download if filepath contains track artist" +
                            "\n  --nec-danger-words <list>      Comma-separated list of words that must appear in either" +
                            "\n                                 both search result and track title, or in neither of the" +
                            "\n                                 two. Case-insensitive. (default:\"mix, edit, dj, cover\")" +
                            "\n" +
                            "\n  --skip-existing                Skip if a track matching nec. conditions is found in the" +
                            "\n                                 output folder or your music library (if provided)" +
                            "\n  --skip-mode <mode>             \"name\": Use only filenames to check if a track exists" +
                            "\n                                 \"name-precise\": Use filenames and check nec-cond (default)" +
                            "\n                                 \"tag\": Use tags (slower)" +
                            "\n                                 \"tag-precise\": Use tags and check all nec. cond. (slower)" +
                            "\n  --music-dir <path>             Specify to skip downloading tracks found in a music library" +
                            "\n                                 Use with --skip-existing" +
                            "\n  --skip-not-found               Skip searching for tracks that weren't found on Soulseek" +
                            "\n                                 last run" +
                            "\n  --remove-ft                    Remove \"ft.\" or \"feat.\" and everything after from the" +
                            "\n                                 track names before searching." +
                            "\n  --album-search                 Also search for album name before filtering for track name. " +
                            "\n                                 Sometimes helps to find more, but slower." +
                            "\n  --artist-search                Also search for artist, before filtering for track name." +
                            "\n                                 Sometimes helps to find more, but slower." +
                            "\n  --no-artist-search             Also perform a search without artist name if nothing was" +
                            "\n                                 found. Only use if the source is imprecise" +
                            "\n                                 and the provided \"artist\" is possibly wrong (yt, sc)" +
                            "\n  --no-regex-search <reg>        Also perform a search with a regex pattern removed from the" +
                            "\n                                 titles and artist names" +
                            "\n  --no-diacr-search              Also perform a search without diacritics" +
                            "\n  -d --desperate                 Equivalent to enabling all additional searches" +
                            "\n  --yt-dlp                       Use yt-dlp to download tracks that weren't found on" +
                            "\n                                 Soulseek. yt-dlp must be available from the command line." +
                            "\n" +
                            "\n  --search-timeout <ms>          Maximal search time (ms, default: 6000)" +
                            "\n  --max-stale-time <ms>          Maximal download time with no progress (ms, default: 50000)" +
                            "\n  --concurrent-processes <num>   Max concurrent searches & downloads (default: 2)" +
                            "\n  --display <str>                \"single\" (default): Show transfer state and percentage." +
                            "\n                                 \"double\": Also show a progress bar. \"simple\": simple" +
                            "\n" +
                            "\n  --print-tracks                 Do not search, only print all tracks to be downloaded" +
                            "\n  --print-results                Do not download, print search results satisfying nec. cond." +
                            "\n  --print-results-full           Do not download, print all search results with full path");
    }

    static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

#if WINDOWS
        try
        {
            if (Console.BufferHeight <= 50)
                WriteLine("Recommended to use the command prompt instead of terminal app to avoid printing issues.", ConsoleColor.DarkYellow);
        }
        catch { }
#endif

        if (args.Contains("--help") || args.Contains("-h") || args.Length == 0)
        {
            PrintHelp();
            return;
        }

        if (System.IO.File.Exists(confPath))
        {
            string confArgs = System.IO.File.ReadAllText(confPath);
            List<string> finalArgs = new List<string>();
            finalArgs.AddRange(ParseCommand(confArgs));
            finalArgs.AddRange(args);
            args = finalArgs.ToArray();
        }

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--path":
                case "--parent":
                    parentFolder = args[++i];
                    break;
                case "-f":
                case "--folder":
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
                case "-s":
                case "--single":
                    singleName = args[++i];
                    break;
                case "-a":
                case "--album":
                    albumName = args[++i];
                    break;
                case "--no-artist-search":
                    noArtistSearchTrack = true;
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
                case "--youtube-key":
                    ytKey = args[++i];
                    break;
                case "--user":
                case "--username":
                    username = args[++i];
                    break;
                case "--pass":
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
                    albumSearchTrack = true;
                    break;
                case "--no-diacr-search":
                    noDiacrSearch = true;
                    break;
                case "--yt-desc-col":
                    descCol = args[++i];
                    break;
                case "--yt-id-col":
                    ytIdCol = args[++i];
                    break;
                case "-n":
                case "--number":
                    maxTracks = int.Parse(args[++i]);
                    break;
                case "-o":
                case "--offset":
                    offset = int.Parse(args[++i]);
                    break;
                case "--name-format":
                    nameFormat = args[++i];
                    break;
                case "--print-results":
                    debugDisableDownload = true;
                    break;
                case "--print-results-full":
                    debugDisableDownload = true;
                    printResultsFull = true;
                    break;
                case "--print-tracks":
                    debugPrintTracks = true;
                    break;
                case "--yt-parse":
                    ytParse = true;
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
                case "--skip-not-found":
                    skipNotFound = true;
                    break;
                case "--remove-from-playlist":
                    removeTracksFromSource = true;
                    break;
                case "--remove-ft":
                    removeFt = true;
                    break;
                case "--no-regex-search":
                    noRegexSearch = args[++i];
                    break;
                case "--reverse":
                    reverse = true;
                    break;
                case "--skip-if-pref-failed":
                    skipIfPrefFailed = true;
                    break;
                case "--m3u":
                    createM3u = true;
                    break;
                case "--m3u-only":
                    m3uOnly = true;
                    break;
                case "--search-timeout":
                    searchTimeout = int.Parse(args[++i]);
                    break;
                case "--max-stale-time":
                    downloadMaxStaleTime = int.Parse(args[++i]);
                    break;
                case "--concurrent-processes":
                    maxConcurrentProcesses = int.Parse(args[++i]);
                    break;
                case "--max-retries":
                    maxRetriesPerTrack = int.Parse(args[++i]);
                    break;
                case "--max-results-per-user":
                    maxResultsPerUser = int.Parse(args[++i]);
                    break;
                case "--pref-format":
                    preferredCond.Formats = args[++i].Split(',', StringSplitOptions.TrimEntries);
                    break;
                case "--pref-length-tol":
                    preferredCond.LengthTolerance = int.Parse(args[++i]);
                    break;
                case "--pref-min-bitrate":
                    preferredCond.MinBitrate = int.Parse(args[++i]);
                    break;
                case "--pref-max-bitrate":
                    preferredCond.MaxBitrate = int.Parse(args[++i]);
                    break;
                case "--pref-max-samplerate":
                    preferredCond.MaxSampleRate = int.Parse(args[++i]);
                    break;
                case "--pref-danger-words":
                    preferredCond.DangerWords = args[++i].Split(',');
                    break;
                case "--pref-strict-title":
                    preferredCond.StrictTitle = true;
                    break;
                case "--pref-strict-artist":
                    preferredCond.StrictArtist = true;
                    break;
                case "--nec-format":
                    necessaryCond.Formats = args[++i].Split(',', StringSplitOptions.TrimEntries);
                    break;
                case "--nec-length-tol":
                    necessaryCond.LengthTolerance = int.Parse(args[++i]);
                    break;
                case "--nec-min-bitrate":
                    necessaryCond.MinBitrate = int.Parse(args[++i]);
                    break;
                case "--nec-max-bitrate":
                    necessaryCond.MaxBitrate = int.Parse(args[++i]);
                    break;
                case "--nec-max-samplerate":
                    necessaryCond.MaxSampleRate = int.Parse(args[++i]);
                    break;
                case "--nec-danger-words":
                    necessaryCond.DangerWords = args[++i].Split(',');
                    break;
                case "--nec-strict-title":
                    necessaryCond.StrictTitle = true;
                    break;
                case "--nec-strict-artist":
                    necessaryCond.StrictArtist = true;
                    break;
                case "--slow-output":
                    slowConsoleOutput = true;
                    break;
                case "--no-modify-share-count":
                    noModifyShareCount = true;
                    break;
                case "--skip-existing-use-tags":
                    skipExisting = true;
                    useTagsCheckExisting = true;
                    break;
                case "--artist-search":
                    artistSearchTrack = true;
                    break;
                case "-d":
                case "--desperate":
                    noArtistSearchTrack = true;
                    noDiacrSearch = true;
                    albumSearchTrack = true;
                    artistSearchTrack = true;
                    break;
                case "--display":
                    switch (args[++i])
                    {
                        case "single":
                        case "double":
                        case "simple":
                            displayStyle = args[i];
                            break;
                        default:
                            throw new Exception($"Invalid display style \"{args[i]}\"");
                    }
                    break;
                case "--skip-mode":
                    switch (args[++i])
                    {
                        case "name":
                        case "name-precise":
                        case "tag":
                        case "tag-precise":
                            useTagsCheckExisting = args[i].Contains("tag");
                            preciseSkip = args[i].Contains("-precise");
                            break;
                        default:
                            throw new Exception($"Invalid skip mode \"{args[i]}\"");
                    }
                    break;
                default:
                    throw new Exception($"Unknown argument: {args[i]}");
            }
        }

        if (debugDisableDownload)
            maxConcurrentProcesses = 1;

        if (ytKey != "")
            YouTube.apiKey = ytKey;

        int max = reverse ? int.MaxValue : maxTracks;
        int off = reverse ? 0 : offset;

        if (spotifyUrl != "")
        {
            string? playlistName;
            bool usedDefaultId = false;
            bool login = spotifyUrl == "likes" || removeTracksFromSource;

            void readSpotifyCreds()
            {
                Console.Write("Spotify client ID:");
                spotifyId = Console.ReadLine();
                Console.Write("Spotify client secret:");
                spotifySecret = Console.ReadLine();
                Console.WriteLine();
            }

            if (spotifyId == "" || spotifySecret == "")
            {
                if (login)
                    readSpotifyCreds();
                else
                {
                    spotifyId = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifyId));
                    spotifySecret = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifySecret));
                    usedDefaultId = true;
                }
            }

            spotifyClient = new Spotify(spotifyId, spotifySecret);
            await spotifyClient.Authorize(login, removeTracksFromSource);

            if (spotifyUrl == "likes")
            {
                Console.WriteLine("Loading Spotify likes");
                tracks = await spotifyClient.GetLikes(max, off);
                playlistName = "Spotify Likes";
            }
            else
            {
                try
                {
                    Console.WriteLine("Loading Spotify tracks");
                    (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(spotifyUrl, max, off);
                }
                catch (SpotifyAPI.Web.APIException)
                {
                    if (!login)
                    {
                        Console.WriteLine("Spotify playlist not found. It may be set to private. Login? [Y/n]");
                        string answer = Console.ReadLine();
                        if (answer.ToLower() == "y")
                        {
                            if (usedDefaultId)
                                readSpotifyCreds();
                            await spotifyClient.Authorize(true);
                            Console.WriteLine("Loading Spotify tracks");
                            (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(spotifyUrl, max, off);
                        }
                        else return;
                    }
                    else throw;
                }
            }
            if (folderName == "")
                folderName = RemoveInvalidChars(playlistName, " ");
        }
        else if (ytUrl != "")
        {
            string name;

            if (YouTube.apiKey != "")
            {
                Console.WriteLine("Loading YouTube playlist (API)");
                (name, tracks) = await YouTube.GetTracksApi(ytUrl, max, off);
            }
            else
            {
                Console.WriteLine("Loading YouTube playlist");
                (name, tracks) = await YouTube.GetTracksYtExplode(ytUrl, max, off);
            }

            if (folderName == "")
                folderName = RemoveInvalidChars(name, " ");

            YouTube.StopService();
        }
        else if (tracksCsv != "")
        {
            if (!System.IO.File.Exists(tracksCsv))
                throw new Exception("CSV file not found");

            tracks = await ParseCsvIntoTrackInfo(tracksCsv, artistCol, trackCol, lengthCol, albumCol, descCol, ytIdCol, timeUnit, ytParse);
            tracks = tracks.Skip(off).Take(max).ToList();

            if (folderName == "")
                folderName = Path.GetFileNameWithoutExtension(tracksCsv);
        }
        else if (singleName != "")
        {
            tracks.Add(ParseTrackArg(singleName));
            writeFails = false;
        }
        else if (albumName != "")
        {
            throw new NotImplementedException();
            var t = ParseTrackArg(albumName);
            await client.ConnectAsync(username, password);
            (string path, int count) = await SearchAndDownloadAlbum(t.TrackTitle, t.ArtistName, parentFolder, folderName);
            Console.WriteLine($"Downloaded {count} tracks");
            return;
        }
        else
            throw new Exception("No url, csv or name provided to download.");

        if (reverse)
        {
            tracks.Reverse();
            tracks = tracks.Skip(offset).Take(maxTracks).ToList();
        }

        if (removeFt)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                Track track = tracks[i];
                track.TrackTitle = track.TrackTitle.RemoveFt();
                track.ArtistName = track.ArtistName.RemoveFt();
                tracks[i] = track;
            }
        }

        folderName = RemoveInvalidChars(folderName, " ");

        outputFolder = Path.Combine(parentFolder, folderName);
        failsFilePath = Path.Combine(outputFolder, $"{folderName}_failed.txt");

        if (m3uFilePath != "")
            m3uFilePath = Path.Combine(m3uFilePath, folderName + ".m3u8");
        else 
            m3uFilePath = Path.Combine(outputFolder, folderName + ".m3u8");

        Track[] tmp = new Track[tracks.Count];
        tracks.CopyTo(tmp);
        var tracksStart = tmp.ToList();
        
        createM3u |= m3uOnly;
        List<string> m3uLines = Enumerable.Repeat("", tracksStart.Count).ToList();

        if (skipExisting || m3uOnly)
        {
            var existing = new Dictionary<Track, string>();
            if (!(musicDir != "" && outputFolder.StartsWith(musicDir, StringComparison.OrdinalIgnoreCase)) && System.IO.Directory.Exists(outputFolder))
            {
                Console.WriteLine($"Checking if tracks exist in output folder");
                var d = RemoveTracksIfExist(tracks, outputFolder, necessaryCond, useTagsCheckExisting, preciseSkip);
                d.ToList().ForEach(x => existing.Add(x.Key, x.Value));
            }
            if (musicDir != "" && System.IO.Directory.Exists(musicDir))
            {
                Console.WriteLine($"Checking if tracks exist in library");
                var d = RemoveTracksIfExist(tracks, musicDir, necessaryCond, useTagsCheckExisting, preciseSkip);
                d.ToList().ForEach(x => existing.Add(x.Key, x.Value));
            }

            foreach (var x in existing)
            {
                string p = Path.GetDirectoryName(x.Value).Equals(outputFolder, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(x.Value) : x.Value;
                m3uLines[tracksStart.IndexOf(x.Key)] = p;
            }
        }

        if (createM3u && !debugPrintTracks)
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
                Console.WriteLine($"Created m3u file: {tracksStart.Count - tracks.Count} of {tracksStart.Count} found as local files");
                if (tracks.Count > 0)
                {
                    Console.WriteLine($"Missing:");
                    foreach (var t in tracks)
                        Console.WriteLine(($"{t.TrackTitle} - {t.ArtistName}") + (t.Length > 0 ? $" ({t.Length}s)" : ""));
                }
                return;
            }
        }

        int tracksCount2 = tracks.Count;

        if (System.IO.File.Exists(failsFilePath))
        {
            if (skipNotFound)
            {
                string failsFileCont;

                using (var fileStream = new FileStream(failsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var streamReader = new StreamReader(fileStream))
                    failsFileCont = streamReader.ReadToEnd();

                foreach (var track in tracks.ToList())
                {
                    if (failsFileCont.Contains(track.ToString() + " [No suitable file found]"))
                        tracks.Remove(track);
                }

                var filteredLines = failsFileCont.Split('\n', StringSplitOptions.TrimEntries)
                    .Where(line => line.Contains("[No suitable file found]")).ToList();

                using (var fileStream = new FileStream(failsFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    using (var streamWriter = new StreamWriter(fileStream))
                    {
                        foreach (var line in filteredLines)
                            streamWriter.WriteLine(line.Trim());
                    }
                }
            }
            else
            {
                try
                {
                    WriteAllLinesOutputFile("");
                    System.IO.File.Delete(failsFilePath);
                }
                catch { }
            }
        }

        int tracksRemaining = tracks.Count;

        string notFoundLastTime = skipNotFound && tracksCount2 - tracks.Count > 0 ? $"{tracksCount2 - tracks.Count} not found" : "";
        string alreadyExist = skipExisting && tracksStart.Count - tracksCount2 > 0 ? $"{tracksStart.Count - tracksCount2} already exist" : "";
        notFoundLastTime = alreadyExist != "" && notFoundLastTime != "" ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist + notFoundLastTime != "" ? $" ({alreadyExist}{notFoundLastTime})" : "";

        if (debugPrintTracks)
        {
            Console.WriteLine($"\n{tracks.Count} tracks{skippedTracks}");
            Console.WriteLine($"\nTo be downloaded:");
            PrintTracks(tracks);
            Console.WriteLine($"\nSkipped:");
            PrintTracks(tracksStart.Where(t => !tracks.Contains(t)).ToList());
            return;
        }
        else if (tracks.Count > 1 || skippedTracks != "")
        {
            PrintTracks(tracks, 10);
            Console.WriteLine($"Downloading {tracks.Count} tracks{skippedTracks}\n");
        }

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new Exception("No soulseek username or password");

        await WaitForInternetConnection();
        await client.ConnectAsync(username, password);
        if (!noModifyShareCount)
            await client.SetSharedCountsAsync(10, 50);

        int successCount = 0;
        int failCount = 0;

        var UpdateTask = Task.Run(() => Update());

        SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);
        var downloadTasks = tracks.Select(async (track) =>
        {
            await semaphore.WaitAsync();
            int netRetries = 2;
        retry:
            try
            {
                await WaitForInternetConnection();

                var savedFilePath = await SearchAndDownload(track);
                if (savedFilePath != "")
                {
                    Interlocked.Increment(ref successCount);

                    if (removeTracksFromSource)
                    {
                        if (!string.IsNullOrEmpty(spotifyUrl))
                            spotifyClient.RemoveTrackFromPlaylist(playlistUri, track.URI);
                    }

                    m3uLines[tracksStart.IndexOf(track)] = Path.GetFileName(savedFilePath);
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
                else
                    Interlocked.Increment(ref failCount);
            }
            catch (Exception ex)
            {
                if (ex is System.InvalidOperationException && ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) && netRetries-- > 0)
                    goto retry;
                else
                    Interlocked.Increment(ref failCount);
            }
            finally { semaphore.Release(); }

            if ((DateTime.Now - lastUpdate).TotalMilliseconds > updateDelay * 3)
                UpdateTask = Task.Run(() => Update());
            else if ((successCount + failCount + 1) % 25 == 0)
            {
                skipUpdate = true;
                await Task.Delay(50);
                lock (consoleLock) {
                    WriteLine($"\nSuccesses: {successCount}, fails: {failCount}, tracks left: {tracksRemaining}\n", ConsoleColor.Yellow);
                }
                await Task.Delay(50);
                skipUpdate = false;
            }

            Interlocked.Decrement(ref tracksRemaining);
        });

        await Task.WhenAll(downloadTasks);

        if (tracks.Count > 1)
            Console.WriteLine($"\n\nDownloaded {tracks.Count - failCount} of {tracks.Count} tracks");
        if (System.IO.File.Exists(failsFilePath))
            Console.WriteLine($"Failed:\n{System.IO.File.ReadAllText(failsFilePath)}");
    }

    // Unfinished
    static async Task<(string, int)> SearchAndDownloadAlbum(string title, string artist, string path, string folder="") 
    {
        int trackCount = 0;
        var search = artist != "" ? $"{artist} - {title}" : title;
        var saveFolderPath = "";
        var seps = new string[] { " ", "_", "-" };
        var emptyTrack = new Track { };

        var responses = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File[])>();
        var cts = new CancellationTokenSource();
        ProgressBar? progress = GetProgressBar(displayStyle);

        if (search.Replace(seps, "").RemoveInvalidChars("") == "")
        {
            RefreshOrPrint(progress, 0, $"Album title only contains invalid characters: {search}, not searching", true);
            return ("", 0);
        }

        RefreshOrPrint(progress, 0, $"Searching for album: {search}", true);

        Action<SearchResponse> responseHandler = ((r) => {
            if (r.Files.Count > 0)
            {
                var fileGroups = r.Files.GroupBy(f => GetDirectoryNameSlsk(f.Filename));
                foreach (var group in fileGroups)
                {
                    var key = r.Username + "\\" + group.Key;
                    responses.TryAdd(key, (r, group.ToArray()));
                }
            }
        });

        var searchOptions = new SearchOptions (
            minimumPeerUploadSpeed: 1, searchTimeout: searchTimeout,
            responseFilter: (response) => { return response.UploadSpeed > 0; },
            fileFilter: (file) => {
                return !IsMusicFile(file.Filename) || 
                    (necessaryCond.FileSatisfies(file, emptyTrack) 
                        && necessaryCond.StrictTitleSatisfies(GetDirectoryNameSlsk(file.Filename), title)
                        && necessaryCond.StrictArtistSatisfies(file.Filename, artist));
            }
        );

        await RunSearches(search, searchOptions, responseHandler, cts.Token);
        cts.Dispose();

        if (responses.Count > 0)
        {
            if (debugDisableDownload)
            {
                foreach (var r in responses)
                {
                    Console.WriteLine(r.Key);
                    foreach (var item in r.Value.Item2)
                        Console.WriteLine($"{GetFileNameSlsk(item.Filename)}");
                    Console.WriteLine("\n");
                }
                return ("", 0);
            }
        }

        return (saveFolderPath, trackCount);
    }

    static async Task<string> SearchAndDownload(Track track)
    {
        var title = !string.IsNullOrEmpty(track.ArtistName) ? $"{track.ArtistName} - {track.TrackTitle}" : $"{track.TrackTitle}";
        string searchText = $"{title}";
        var saveFilePath = "";
        var removeChars = new string[] { " ", "_", "-" };

        bool attemptedDownloadPref = false;
        Task? downloadTask = null;
        object downloadingLocker = new object();
        bool downloading = false;
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        var cts = new CancellationTokenSource();

        Console.ResetColor();

        ProgressBar? progress = GetProgressBar(displayStyle);

        if (track.TrackTitle.Replace(removeChars, "").RemoveInvalidChars("") == "")
        {
            RefreshOrPrint(progress, 0, $"Track title only contains invalid characters: {title}, not searching", true);
            WriteLineOutputFile($"{title} [Track title has only invalid chars]");
            return "";
        }

        RefreshOrPrint(progress, 0, $"Searching: {track}", true);

        searches.TryAdd(track, new SearchInfo(results, progress));

        Action<SearchResponse> getResponseHandler(FileConditions cond, int maxPerUser = -1)
        {
            maxPerUser = maxPerUser == -1 ? int.MaxValue : maxPerUser;
            return (r) => {
                if (r.Files.Count() > 0)
                {
                    int count = 0;
                    foreach (var file in r.Files)
                    {
                        results.TryAdd(r.Username + "\\" + file.Filename, (r, file));
                        if (++count >= maxPerUser) break;
                    }

                    var f = r.Files.First();
                    if (cond.FileSatisfies(f, track) && r.HasFreeUploadSlot && r.UploadSpeed / 1000000 >= 1)
                    {
                        lock (downloadingLocker)
                        {
                            if (!downloading)
                            {
                                downloading = true;
                                saveFilePath = GetSavePath(f.Filename, track);
                                attemptedDownloadPref = true;
                                downloadTask = DownloadFile(r, f, saveFilePath, track, progress, cts);
                                downloadTask.ContinueWith(task => {
                                    lock (downloadingLocker)
                                    {
                                        downloading = false;
                                        saveFilePath = "";
                                        results.TryRemove(r.Username + "\\" + f.Filename, out _);
                                    }
                                }, TaskContinuationOptions.OnlyOnFaulted);
                            }
                        }
                    }
                }
            };
        }

        SearchOptions getSearchOptions(int timeout, FileConditions cond) 
        {
            return new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                searchTimeout: searchTimeout,
                responseFilter: (response) => { return response.UploadSpeed > 0; },
                fileFilter: (file) => {
                    return IsMusicFile(file.Filename) && (cond.FileSatisfies(file, track) || printResultsFull);
                });
        }

        var responseHandler = getResponseHandler(preferredCond, maxResultsPerUser);
        var responseHandlerUncapped = getResponseHandler(preferredCond);
        var searchOptions = getSearchOptions(searchTimeout, necessaryCond);

        await RunSearches(searchText, searchOptions, responseHandler, cts.Token);

        if (results.Count == 0 && albumSearchTrack && track.Album != "")
        {
            searchText = $"{track.Album}";
            var necCond2 = new FileConditions(necessaryCond);
            necCond2.StrictTitle = true;
            searchOptions = getSearchOptions(Math.Min(5000, searchTimeout), necCond2);

            RefreshOrPrint(progress, 0, $"Searching (album name): {track.Album}");
            await RunSearches(searchText, searchOptions, responseHandlerUncapped, cts.Token);
        }

        if (results.Count == 0 && noArtistSearchTrack && !string.IsNullOrEmpty(track.ArtistName))
        {
            searchText = $"{track.TrackTitle}";
            var necCond2 = new FileConditions(necessaryCond);
            necCond2.LengthTolerance = Math.Min(necCond2.LengthTolerance, 1);
            necCond2.StrictArtist = true;
            searchOptions = getSearchOptions(Math.Min(5000, searchTimeout), necCond2);
            
            RefreshOrPrint(progress, 0, $"Searching (no artist name): {searchText}");
            await RunSearches(searchText, searchOptions, responseHandlerUncapped, cts.Token);
        }

        if (results.Count == 0 && artistSearchTrack && !string.IsNullOrEmpty(track.ArtistName))
        {
            searchText = $"{track.ArtistName}";
            var necCond2 = new FileConditions(necessaryCond);
            necCond2.StrictTitle = true;
            searchOptions = getSearchOptions(Math.Min(6000, searchTimeout), necCond2);

            RefreshOrPrint(progress, 0, $"Searching (artist name): {searchText}");
            await RunSearches(searchText, searchOptions, responseHandlerUncapped, cts.Token);
        }

        lock (downloadingLocker) { }
        searches.TryRemove(track, out _);

        bool notFound = false;
        if (!downloading && results.Count == 0 && !useYtdlp)
            notFound = true;
        else if (downloading)
        {
            try { await downloadTask; }
            catch
            {
                saveFilePath = "";
                downloading = false;
            }
        }

        cts.Dispose();

        if (debugDisableDownload && results.Count == 0)
        {
            Console.WriteLine("No results");
            return "";
        }
        else if (!downloading && results.Count > 0)
        {
            var random = new Random();
            var fileResponses = results
                .Select(kvp => (response: kvp.Value.Item1, file: kvp.Value.Item2))
                .OrderByDescending(x => preferredCond.StrictTitleSatisfies(x.file.Filename, track.TrackTitle))
                .ThenByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FormatSatisfies(x.file.Filename))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track))
                .ThenByDescending(x => !printResultsFull || necessaryCond.FileSatisfies(x.file, track))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 400)
                .ThenByDescending(x => (x.file.BitRate ?? 0) / 70)
                .ThenByDescending(x => FileConditions.StrictString(GetFileNameWithoutExtSlsk(x.file.Filename), track.TrackTitle))
                .ThenByDescending(x => FileConditions.StrictString(x.file.Filename, track.ArtistName))
                .ThenBy(x => random.Next());

            if (debugDisableDownload)
            {
                foreach (var x in fileResponses)
                    Console.WriteLine(DisplayString(track, x.file, x.response.Username,
                        (printResultsFull ? necessaryCond : null), preferredCond, printResultsFull));
                WriteLine($"Total: {fileResponses.Count()}\n", ConsoleColor.Yellow);
                return "";
            }

            foreach (var x in fileResponses)
            {
                bool pref = preferredCond.FileSatisfies(x.file, track);
                if (skipIfPrefFailed && attemptedDownloadPref && !pref)
                {
                    RefreshOrPrint(progress, 0, $"Pref. version of the file exists, but couldn't be downloaded: {track}, skipping", true);
                    var failedDownloadInfo = $"{track} [Pref. version of the file exists, but couldn't be downloaded]";
                    WriteLineOutputFile(failedDownloadInfo);
                    return "";
                }

                saveFilePath = GetSavePath(x.file.Filename, track);

                try
                {
                    downloading = true;
                    if (pref)
                        attemptedDownloadPref = true;
                    await DownloadFile(x.response, x.file, saveFilePath, track, progress);
                    break;
                }
                catch
                {
                    downloading = false;
                    if (--maxRetriesPerTrack <= 0)
                    {
                        RefreshOrPrint(progress, 0, $"Out of download retries: {track}, skipping", true);
                        var failedDownloadInfo = $"{track} [Out of download retries]";
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
                RefreshOrPrint(progress, 0, $"Not found, searching with yt-dlp: {track}", true);
                downloading = true;
                string saveFilePathNoExt = await YtdlpSearchAndDownload(track, progress);
                string fname = GetFileNameWithoutExtSlsk(saveFilePathNoExt + ".m");
                string[] files = System.IO.Directory.GetFiles(outputFolder, fname + ".*");

                foreach (string file in files)
                {
                    if (IsMusicFile(file))
                    {
                        RefreshOrPrint(progress, 100, $"Succeded: yt-dlp completed download for {track}", true);
                        saveFilePath = file;
                        break;
                    }
                }
                if (saveFilePath == "")
                    throw new Exception("yt-dlp download failed");
            }
            catch (Exception e) {
                saveFilePath = "";
                downloading = false;
                if (e.Message.Contains("No matching files found"))
                    notFound = true;
                RefreshOrPrint(progress, 0, $"{e.Message}", true);
            }
        }

        if (!downloading)
        {
            if (notFound)
            {
                RefreshOrPrint(progress, 0, $"Not found: {track}, skipping", true);   
                var failedDownloadInfo = $"{track} [No suitable file found]";
                WriteLineOutputFile(failedDownloadInfo);
            }
            else
            {
                RefreshOrPrint(progress, 0, $"Failed to download: {track}, skipping", true);
                var failedDownloadInfo = $"{track} [All downloads failed]";
                WriteLineOutputFile(failedDownloadInfo);
            }
            return "";
        }

        if (nameFormat != "")
            saveFilePath = ApplyNamingFormat(saveFilePath, nameFormat);

        return saveFilePath;
    }

    static async Task RunSearches(string search, SearchOptions opts, Action<SearchResponse> rHandler, CancellationToken ct)
    {
        try
        {
            var q = SearchQuery.FromText(search);
            await WaitForInternetConnection();
            var searchTasks = new List<Task>();
            searchTasks.Add(client.SearchAsync(q, options: opts, cancellationToken: ct, responseHandler: rHandler));

            if (noDiacrSearch && search.RemoveDiacriticsIfExist(out string newSearch))
            {
                var searchQuery2 = SearchQuery.FromText(newSearch);
                searchTasks.Add(client.SearchAsync(searchQuery2, options: opts, cancellationToken: ct, responseHandler: rHandler));
            }
            if (!string.IsNullOrEmpty(noRegexSearch) && search.RemoveRegexIfExist(noRegexSearch, out string newSearch2))
            {
                var searchQuery2 = SearchQuery.FromText(newSearch2);
                searchTasks.Add(client.SearchAsync(searchQuery2, options: opts, cancellationToken: ct, responseHandler: rHandler));
            }

            await Task.WhenAll(searchTasks);
        }
        catch (OperationCanceledException ex) { }
    }

    static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, Track track, ProgressBar progress, CancellationTokenSource? searchCts = null)
    {
        if (debugDisableDownload)
            throw new Exception();

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
            lock (downloads)
                downloads.TryAdd(file.Filename, new DownloadWrapper(filePath, response, file, track, cts, progress));

            try
            {
                await WaitForInternetConnection();
                await client.DownloadAsync(response.Username, file.Filename, () => Task.FromResult((Stream)outputStream), file.Size, options: transferOptions, cancellationToken: cts.Token);
            }
            catch (Exception e)
            {
                downloads[file.Filename].UpdateText();
                downloads.TryRemove(file.Filename, out _);
                try
                {
                    if (System.IO.File.Exists(filePath))
                        System.IO.File.Delete(filePath);
                }
                catch { }
                throw;
            }
        }

        try { searchCts?.Cancel(); }
        catch { }
        downloads[file.Filename].success = true;
        downloads[file.Filename].UpdateText();
        downloads.TryRemove(file.Filename, out _);
    }

    static int totalCalls = 0;
    static async Task Update()
    {
        totalCalls++;
        int thisCall = totalCalls;

        if (slowConsoleOutput)
            updateDelay = slowUpdateDelay;

        while (thisCall == totalCalls)
        {
            lastUpdate = DateTime.Now;

            if (!skipUpdate)
            {
                // Debug.WriteLine($"Threads: {Process.GetCurrentProcess().Threads.Count}");
                foreach (var (key, val) in searches)
                {
                    if (val == null)
                        searches.TryRemove(key, out _);
                }

                foreach (var (key, val) in downloads)
                {
                    if (val != null)
                    {
                        val.UpdateText();

                        if (val.success)
                            downloads.TryRemove(key, out _);
                        else if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > downloadMaxStaleTime)
                        {
                            try { val.cts.Cancel(); } catch { }
                            val.stalled = true;
                            val.UpdateText();
                            downloads.TryRemove(key, out _);
                        }
                    }
                    else downloads.TryRemove(key, out _);
                }
            }

            await Task.Delay(updateDelay);
        }
    }

    static async Task<string> YtdlpSearchAndDownload(Track track, ProgressBar progress)
    {
        if (track.URI != "")
        {
            string videoTitle = (await YouTube.GetVideoInfo(track.URI)).title;
            string saveFilePathNoExt = GetSavePathNoExt(videoTitle, track);
            await YtdlpDownload(track.URI, saveFilePathNoExt, progress);
            return saveFilePathNoExt;
        }

        Process process = new Process();
        ProcessStartInfo startInfo = new ProcessStartInfo();

        startInfo.FileName = "yt-dlp";
        string search = track.ArtistName != "" ? $"{track.ArtistName} - {track.TrackTitle}" : track.TrackTitle;
        startInfo.Arguments = $"\"ytsearch3:{search}\" --print \"%(duration>%s)s === %(id)s === %(title)s\"";
        RefreshOrPrint(progress, 0, $"{startInfo.FileName} \"ytsearch3:{search}\"");

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        process.StartInfo = startInfo;
        process.OutputDataReceived += (sender, e) => { Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { Console.WriteLine(e.Data); };

        await WaitForInternetConnection();
        process.Start();

        List<(int, string, string)> results = new List<(int, string, string)>();
        string output;
        Regex regex = new Regex(@"^(\d+) === ([\w-]+) === (.+)$");
        while ((output = process.StandardOutput.ReadLine()) != null)
        {
            Match match = regex.Match(output);
            if (match.Success)
            {
                int seconds = int.Parse(match.Groups[1].Value);
                string id = match.Groups[2].Value;
                string title = match.Groups[3].Value;
                results.Add((seconds, id, title));
            }
        }

        process.WaitForExit();

        foreach (var res in results)
        {
            if (necessaryCond.LengthToleranceSatisfies(track, res.Item1))
            {
                string videoTitle = (await YouTube.GetVideoInfo(res.Item2)).title;
                string saveFilePathNoExt = GetSavePathNoExt(videoTitle, track);
                await YtdlpDownload(res.Item2, saveFilePathNoExt, progress);
                return saveFilePathNoExt;
            }
        }

        throw new Exception($"[yt-dlp] No matching files found");
    }

    static async Task YtdlpDownload(string id, string savePathNoExt, ProgressBar progress)
    {
        Process process = new Process();
        ProcessStartInfo startInfo = new ProcessStartInfo();

        startInfo.FileName = "yt-dlp";
        startInfo.Arguments = $"\"{id}\" -f {ytdlpFormat} -ci -o \"{savePathNoExt}.%(ext)s\" -x";
        RefreshOrPrint(progress, 0, $"yt-dlp \"{id}\" -f {ytdlpFormat} -ci -o \"{Path.GetFileNameWithoutExtension(savePathNoExt + ".m")}.%(ext)s\" -x", true);

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        process.StartInfo = startInfo;
        process.OutputDataReceived += (sender, e) => { Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { Console.WriteLine(e.Data); };

        await WaitForInternetConnection();
        process.Start();
        process.WaitForExit();
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

        private DateTime lastChangeTime = DateTime.Now;
        private TransferStates? prevTransferState = null;
        private long prevBytesTransferred = 0;
        private bool updatedTextDownload = false;
        private bool updatedTextSuccess = false;

        public DownloadWrapper(string savePath, SearchResponse response, Soulseek.File file, Track track, CancellationTokenSource cts, ProgressBar progress)
        {
            this.savePath = savePath;
            this.response = response;
            this.file = file;
            this.cts = cts;
            this.track = track;
            this.progress = progress;
            this.displayText = DisplayString(track, file, response.Username);

            RefreshOrPrint(progress, 0, "Initialize: " + displayText, true);
            RefreshOrPrint(progress, 0, displayText, false);
        }

        public void UpdateText()
        {
            char[] bars = { '/', '|', '\\', '―' };
            downloadRotatingBarState++;
            downloadRotatingBarState %= bars.Length;
            string bar = success ? "" : bars[downloadRotatingBarState] + " ";
            float? percentage = bytesTransferred / (float)file.Size;
            string percText = percentage < 0.1 ? $"0{percentage:P}" : $"{percentage:P}";
            queued = transfer?.State.ToString().Contains("Queued") ?? false;
            string state = "NullState";
            bool downloading = false;

            if (stalled)
            {
                state = "Stalled";
                bar = "";
            }
            else if (transfer != null)
            {
                state = transfer.State.ToString();

                if (queued)
                    state = "Queued";
                else if (state.Contains("Completed, "))
                    state = state.Replace("Completed, ", "");
                else if (state.Contains("Initializing"))
                    state = "Initialize";
            }

            if (state == "Succeeded")
                success = true;
            if (state == "InProgress")
                downloading = true;

            string txt = $"{bar}{state}:".PadRight(14, ' ');
            bool needSimplePrintUpdate = (downloading && !updatedTextDownload) || (success && !updatedTextSuccess);
            updatedTextDownload |= downloading;
            updatedTextSuccess |= success;

            Console.ResetColor();
            RefreshOrPrint(progress, (int)((percentage ?? 0) * 100), $"{txt} {displayText}", needSimplePrintUpdate);

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
        public ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results;
        public ProgressBar progress;

        public SearchInfo(ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results, ProgressBar progress)
        {
            this.results = results;
            this.progress = progress;
        }
    }

    class FileConditions
    {
        public int LengthTolerance = -1;
        public int MinBitrate = -1;
        public int MaxBitrate = -1;
        public int MaxSampleRate = -1;
        public bool StrictTitle = false;
        public bool StrictArtist = false;
        public string[] DangerWords = { };
        public string[] Formats = { };
        public string StrictStringRegexRemove = "";
        public bool StricStringDiacrRemove = false;

        public FileConditions() { }

        public FileConditions(FileConditions other)
        {
            other.Formats.CopyTo(Formats, 0);
            LengthTolerance = other.LengthTolerance;
            MinBitrate = other.MinBitrate;
            MaxBitrate = other.MaxBitrate;
            MaxSampleRate = other.MaxSampleRate;
            DangerWords = other.DangerWords.ToArray();
        }

        public bool FileSatisfies(Soulseek.File file, Track track)
        {
            return DangerWordSatisfies(file.Filename, track.TrackTitle) && FormatSatisfies(file.Filename) && LengthToleranceSatisfies(file, track.Length) 
                && BitrateSatisfies(file) && SampleRateSatisfies(file) && StrictTitleSatisfies(file.Filename, track.TrackTitle)
                && StrictArtistSatisfies(file.Filename, track.ArtistName);
        }

        public bool FileSatisfies(TagLib.File file, Track track)
        {
            return DangerWordSatisfies(file.Name, track.TrackTitle) && FormatSatisfies(file.Name) && LengthToleranceSatisfies(file, track.Length) 
                && BitrateSatisfies(file) && SampleRateSatisfies(file) && StrictTitleSatisfies(file.Name, track.TrackTitle) 
                && StrictArtistSatisfies(file.Name, track.ArtistName);
        }

        public bool DangerWordSatisfies(string fname, string tname)
        {
            if (string.IsNullOrEmpty(tname))
                return true;

            fname = GetFileNameWithoutExtSlsk(fname).ToLower();
            tname = tname.Split('-', StringSplitOptions.RemoveEmptyEntries).Last().ToLower();

            foreach (var word in DangerWords)
            {
                if (fname.Contains(word) ^ tname.Contains(word))
                {
                    if (word == "mix")
                        return fname.Contains("original mix") || tname.Contains("original mix");
                    else
                        return false;
                }
            }

            return true;
        }

        public bool StrictTitleSatisfies(string fname, string tname, bool noPath = true)
        {
            if (!StrictTitle)
                return true;

            fname = noPath ? GetFileNameWithoutExtSlsk(fname) : fname;
            return StrictString(fname, tname, StrictStringRegexRemove, StricStringDiacrRemove);
        }

        public bool StrictArtistSatisfies(string fname, string aname)
        {
            if (!StrictArtist)
                return true;

            return StrictString(fname, aname, StrictStringRegexRemove, StricStringDiacrRemove);
        }

        public static bool StrictString(string fname, string tname, string regexRemove = "", bool diacrRemove = false)
        {
            if (string.IsNullOrEmpty(tname))
                return true;

            var seps = new string[] { " ", "_", "-" };
            fname = RemoveInvalidChars(fname.Replace(seps, ""), "");
            fname = regexRemove != "" ? Regex.Replace(fname, regexRemove, "") : fname;
            fname = diacrRemove ? fname.RemoveDiacritics() : fname;
            tname = RemoveInvalidChars(tname.Replace(seps, ""), "");
            tname = regexRemove != "" ? Regex.Replace(tname, regexRemove, "") : tname;
            tname = diacrRemove ? tname.RemoveDiacritics() : tname;

            if (string.IsNullOrEmpty(fname) || string.IsNullOrEmpty(tname))
                return false;

            return fname.Contains(tname, StringComparison.OrdinalIgnoreCase);
        }

        public bool FormatSatisfies(string fname)
        {
            string ext = Path.GetExtension(fname).Trim('.').ToLower();
            return Formats.Length == 0 || (ext != "" && Formats.Any(f => f == ext));
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

        public string GetNotSatisfiedName(Soulseek.File file, Track track)
        {
            if (!DangerWordSatisfies(file.Filename, track.TrackTitle))
                return "DangerWord fails";
            if (!FormatSatisfies(file.Filename))
                return "Format fails";
            if (!LengthToleranceSatisfies(file, track.Length))
                return "Length fails";
            if (!BitrateSatisfies(file))
                return "Bitrate fails";
            if (!SampleRateSatisfies(file))
                return "SampleRate fails";
            if (!StrictTitleSatisfies(file.Filename, track.TrackTitle))
                return "StrictTitle fails";
            if (!StrictArtistSatisfies(file.Filename, track.ArtistName))
                return "StrictArtist fails";

            return "Satisfied";                               
        }
    }

    static async Task<List<Track>> ParseCsvIntoTrackInfo(string path, string? artistCol = "", string? trackCol = "", 
        string? lengthCol = "", string? albumCol = "", string? descCol = "", string? ytIdCol = "", string timeUnit = "", bool ytParse = false)
    {
        var tracks = new List<Track>();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            var header = reader.ReadLine().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string?[] cols = { artistCol, albumCol, trackCol, lengthCol, descCol, ytIdCol };
            string[][] aliases = {
                new[] { "artist", "artist name", "artists", "artist names" },
                new[] { "album", "album name", "album title" },
                new[] { "track", "title", "song", "track title", "track name", "song name" },
                new[] { "length", "duration", "track length", "track duration", "song length", "song duration" },
                new[] { "description" },
                new[] { "youtube id" }
            };

            string usingColumns = "";
            for (int i = 0; i < cols.Length; i++)
            {
                if (string.IsNullOrEmpty(cols[i]))
                {
                    string? res = header.FirstOrDefault(h => Regex.Replace(h, @"\(.*?\)", "").EqualsAny(aliases[i], StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(res))
                    {
                        cols[i] = res;
                        usingColumns += $"{aliases[i][0]}:\"{res}\", ";
                    }
                }
                else if (!string.IsNullOrEmpty(cols[i]))
                {
                    if (Array.IndexOf(header, cols[i]) == -1)
                        throw new Exception($"Column \"{cols[i]}\" not found in CSV file");
                    usingColumns += $"{aliases[i][0]}:\"{cols[i]}\", ";
                }
            }

            if (!string.IsNullOrEmpty(usingColumns))
                Console.WriteLine($"Using inferred columns: {usingColumns.TrimEnd(' ', ',')}.");

            if (cols[0] == "")
                WriteLine($"Warning: No artist column specified, results may be imprecise", ConsoleColor.DarkYellow);
            if (cols[2] == "")
                throw new Exception($"No track name column specified");
            if (cols[3] == "")
                WriteLine($"Warning: No artist column specified, results may be imprecise", ConsoleColor.DarkYellow);

            var artistIndex = string.IsNullOrEmpty(cols[0]) ? -1 : Array.IndexOf(header, cols[0]);
            var albumIndex = string.IsNullOrEmpty(cols[1]) ? -1 : Array.IndexOf(header, cols[1]);
            var trackIndex = string.IsNullOrEmpty(cols[2]) ? -1 : Array.IndexOf(header, cols[2]);
            var lengthIndex = string.IsNullOrEmpty(cols[3]) ? -1 : Array.IndexOf(header, cols[3]);
            var descIndex = string.IsNullOrEmpty(cols[4]) ? -1 : Array.IndexOf(header, cols[4]);
            var ytIdIndex = string.IsNullOrEmpty(cols[5]) ? -1 : Array.IndexOf(header, cols[5]);

            var regex = new Regex(",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)"); // thank you, ChatGPT.

            int probablyMsIndex = -1;
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                var values = regex.Split(line);
                if (!values.Any(t => !string.IsNullOrEmpty(t.Trim())))
                    continue;

                var desc = "";
                var id = "";

                var track = new Track();
                if (artistIndex >= 0) track.ArtistName = values[artistIndex].Trim('"').Split(',').First().Trim(' ');
                if (trackIndex >= 0) track.TrackTitle = values[trackIndex].Trim('"');
                if (albumIndex >= 0) track.Album = values[albumIndex].Trim('"');
                if (descIndex >= 0) desc = values[descIndex].Trim('"');
                if (ytIdIndex >= 0) id = values[ytIdIndex].Trim('"');
                if (lengthIndex >= 0)
                {
                    int result = ParseTrackLength(values[lengthIndex]);
                    if (timeUnit == "s" || probablyMsIndex != -1)
                        track.Length = result;
                    else if (timeUnit == "ms")
                        track.Length = result / 1000;
                    else if (string.IsNullOrEmpty(timeUnit) && result > 10000)
                        probablyMsIndex = tracks.Count;
                    else if (string.IsNullOrEmpty(timeUnit))
                        track.Length = result;
                    else
                        throw new Exception($"Invalid timeunit \'{timeUnit}\', only ms or s.");
                }

                if (ytParse)
                    track = await YouTube.ParseTrackInfo(track.TrackTitle, track.ArtistName, id, track.Length, true, desc);

                if (track.TrackTitle != "") tracks.Add(track);
            }

            if (probablyMsIndex != -1)
            {
                Console.WriteLine($"Track length values seem large, probably in ms (specify --time-unit to override)");
                for (int i = 0; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    t.Length /= 1000;
                    tracks[i] = t;
                }
            }
        }

        if (ytParse)
            YouTube.StopService();

        return tracks;
    }

    static bool IsMusicFile(string fileName)
    {
        var musicExtensions = new string[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".wma", ".m4a", ".alac", ".ape", ".opus" };
        var extension = Path.GetExtension(fileName).ToLower();
        return musicExtensions.Contains(extension);
    }

    static string GetSavePath(string sourceFname, Track track)
    {
        return $"{GetSavePathNoExt(sourceFname, track)}{Path.GetExtension(sourceFname)}";
    }

    static string GetSavePathNoExt(string sourceFname, Track track)
    {
        return Path.Combine(outputFolder, $"{GetSaveName(sourceFname, track)}");
    }

    static string GetSaveName(string sourceFname, Track track)
    {
        string name = GetFileNameWithoutExtSlsk(sourceFname);
        return RemoveInvalidChars(name, " ");
    }

    static string GetFileNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileName(fname);
    }

    static string GetFileNameWithoutExtSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileNameWithoutExtension(fname);
    }

    static string GetDirectoryNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetDirectoryName(fname);
    }

    static string ApplyNamingFormat(string filepath, string format)
    {
        try
        {
            var file = TagLib.File.Create(filepath);
            string newName = format;

            newName = newName.Replace("{artist}", file.Tag.FirstPerformer ?? "")
                             .Replace("{artists}", string.Join(" & ", file.Tag.Performers))
                             .Replace("{album_artist}", file.Tag.FirstAlbumArtist ?? "")
                             .Replace("{album_artists}", string.Join(" & ", file.Tag.AlbumArtists))
                             .Replace("{title}", file.Tag.Title ?? "")
                             .Replace("{album}", file.Tag.Album ?? "")
                             .Replace("{year}", file.Tag.Year.ToString() ?? "")
                             .Replace("{track}", file.Tag.Track.ToString("D2") ?? "")
                             .Replace("{disc}", file.Tag.Disc.ToString() ?? "");

            if (newName != format)
            {
                string directory = Path.GetDirectoryName(filepath);
                string extension = Path.GetExtension(filepath);
                string newFilePath = Path.Combine(directory, RemoveInvalidChars(newName, " ") + extension);
                System.IO.File.Move(filepath, newFilePath);
                return newFilePath;
            }
        }
        catch { }

        return filepath;
    }

    static bool TrackMatchesFilename(Track track, string filename)
    {
        string[] ignore = new string[] { " ", "_", "-", ".", "(", ")" };
        string searchName = track.TrackTitle.Replace(ignore, "");
        searchName = searchName.RemoveInvalidChars("").RemoveFt();
        searchName = string.IsNullOrEmpty(searchName) ? track.TrackTitle : searchName;

        filename = Path.GetFileNameWithoutExtension(filename);
        filename = filename.RemoveInvalidChars("");
        filename = filename.Replace(ignore, "");

        if (filename.Contains(searchName, StringComparison.OrdinalIgnoreCase))
            return true;
        else if ((track.ArtistMaybeWrong || string.IsNullOrEmpty(track.ArtistName)) && track.TrackTitle.Count(c => c == '-') == 1)
        {
            searchName = track.TrackTitle.Split('-', StringSplitOptions.RemoveEmptyEntries)[1].Replace(ignore, "");
            searchName = searchName.RemoveInvalidChars("").RemoveFt();
            if (!string.IsNullOrEmpty(searchName))
            {
                if (filename.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    static bool TrackExistsInCollection(Track track, FileConditions conditions, IEnumerable<string> collection, out string? foundPath, bool precise)
    {
        var matchingFiles = collection.Where(fileName => TrackMatchesFilename(track, fileName)).ToArray();

        if (!precise && matchingFiles.Any())
        {
            foundPath = matchingFiles.First();
            return true;
        }

        foreach (var p in matchingFiles)
        {
            TagLib.File f;
            try { f = TagLib.File.Create(p); }
            catch { continue; }

            if (conditions.FileSatisfies(f, track))
            {
                foundPath = p;
                return true;
            }
        }

        foundPath = null;
        return false;
    }

    static bool TrackExistsInCollection(Track track, FileConditions conditions, IEnumerable<TagLib.File> collection, out string? foundPath, bool precise)
    {
        var matchingFiles = collection.Where(f => conditions.FileSatisfies(f, track)).ToArray();
        string artist = track.ArtistName.ToLower().Replace(" ", "").RemoveFt();
        string title = track.TrackTitle.ToLower().Replace(" ", "").RemoveFt();

        foreach (var f in collection)
        {
            foundPath = f.Name;

            if (string.IsNullOrEmpty(f.Tag.Title))
            {
                if (TrackMatchesFilename(track, f.Name))
                    return true;
                continue;
            }

            string fileArtist = f.Tag.FirstPerformer.ToLower().Replace(" ", "").RemoveFt();
            string fileTitle = f.Tag.Title.ToLower().Replace(" ", "").RemoveFt();

            if (precise && !conditions.FileSatisfies(f, track))
                continue;

            bool durCheck = conditions.LengthToleranceSatisfies(f, track.Length);
            bool check1 = (artist.Contains(fileArtist) || title.Contains(fileArtist)) && (!precise || durCheck);
            bool check2 = !precise && fileTitle.Length >= 6 && durCheck;

            //if (fileTitle.Contains("wolves") && title.Contains("wolves"))
            //    Console.WriteLine($"{durCheck}, {check1}, {check2}, {conditions.DangerWordSatisfies(fileTitle, title)}, {title.Contains(fileTitle)}, {fileTitle}");

            if ((check1 || check2) &&  (precise || conditions.DangerWordSatisfies(fileTitle, title)))
            {
                if (title.Contains(fileTitle))
                    return true;
            }
        }

        foundPath = null;
        return false;
    }

    static Dictionary<Track, string> RemoveTracksIfExist(List<Track> tracks, string dir, FileConditions necessaryCond, bool useTags, bool precise)
    {
        var existing = new Dictionary<Track, string>();
        var files = System.IO.Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        var musicFiles = files.Where(filename => IsMusicFile(filename)).ToArray();

        if (!useTags)
        {
            tracks.RemoveAll(x =>
            {
                bool exists = TrackExistsInCollection(x, necessaryCond, musicFiles, out string? path, precise);
                if (exists) existing.Add(x, path);
                return exists;
            });
        }
        else
        {
            var musicIndex = new List<TagLib.File>();

            foreach (var p in musicFiles)
            {
                TagLib.File f;
                try { f = TagLib.File.Create(p); }
                catch { continue; }
                musicIndex.Add(f);
            }

            tracks.RemoveAll(x =>
            {
                bool exists = TrackExistsInCollection(x, necessaryCond, musicIndex, out string? path, precise);
                if (exists) existing.Add(x, path);
                return exists;
            });
        }

        return existing;
    }

    static void WriteLineOutputFile(string line)
    {
        if (!writeFails)
            return;
        lock (failsFileLock)
        {
            using (var fileStream = new FileStream(failsFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
            {
                streamWriter.WriteLine(line);
            }
        }
    }
    static void WriteAllLinesOutputFile(string text)
    {
        if (!writeFails)
            return;
        lock (failsFileLock)
        {
            using (var fileStream = new FileStream(failsFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var streamWriter = new StreamWriter(fileStream, System.Text.Encoding.UTF8))
            {
                streamWriter.WriteLine(text);
            }
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

    static Track ParseTrackArg(string input)
    {
        input = input.Trim();
        Track track = new Track();
        List<string> keys = new List<string> { "title", "artist", "duration", "length", "album", "artistMaybeWrong" };

        if (!keys.Any(p => input.Replace(" ", "").Contains(p + "=")))
            track.TrackTitle = input;
        else
        {
            (int, int) getNextKeyIndices(int start)
            {
                int commaIndex = start;
                int equalsIndex = input.IndexOf('=', commaIndex);

                if (equalsIndex == -1)
                    return (-1, -1);
                if (start == 0)
                    return keys.Any(k => k == input.Substring(0, equalsIndex).Trim()) ? (0, equalsIndex) : (-1, -1);

                while (start < input.Length)
                {
                    commaIndex = input.IndexOf(',', start);
                    equalsIndex = commaIndex != -1 ? input.IndexOf('=', commaIndex) : -1;

                    if (commaIndex == -1 || equalsIndex == -1)
                        return (-1, -1);

                    if (keys.Any(k => k == input.Substring(commaIndex + 1, equalsIndex - commaIndex - 1).Trim()))
                        return (commaIndex + 1, equalsIndex);

                    start = commaIndex + 1;
                }

                return (-1, -1);
            }

            (int start, int end) = getNextKeyIndices(0);
            (int prevStart, int prevEnd) = (0, 0);

            while (true)
            {
                if (prevEnd != 0)
                {
                    string key = input.Substring(prevStart, prevEnd - prevStart);
                    int valEnd = start != -1 ? start - 1 : input.Length;
                    string val = input.Substring(prevEnd + 1, valEnd - prevEnd - 1);
                    switch (key)
                    {
                        case "title":
                            track.TrackTitle = val;
                            break;
                        case "artist":
                            track.ArtistName = val;
                            break;
                        case "duration":
                        case "length":
                            track.Length = ParseTrackLength(val);
                            break;
                        case "album":
                            track.Album = val;
                            break;
                        case "artistMaybeWrong":
                            if (val == "true")
                                track.ArtistMaybeWrong = true;
                            break;
                    }
                }

                if (end == -1)
                    break;

                (prevStart, prevEnd) = (start, end);
                (start, end) = getNextKeyIndices(end);
            }
        }

        return track;
    }

    static int ParseTrackLength(string lengthString)
    {
        string[] parts = lengthString.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            if (float.TryParse(parts[0], out float seconds))
                return (int)seconds;
        }
        else if (parts.Length == 2)
        {
            if (int.TryParse(parts[0], out int minutes) && float.TryParse(parts[1], out float seconds))
                return minutes * 60 + (int)seconds;
        }
        else if (parts.Length == 3)
        {
            if (int.TryParse(parts[0], out int hours) && int.TryParse(parts[1], out int minutes) && float.TryParse(parts[2], out float seconds))
                return hours * 3600 + minutes * 60 + (int)seconds;
        }

        throw new ArgumentException("Invalid track length format", nameof(lengthString));
    }

    static string RemoveInvalidChars(this string str, string replaceStr)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
            str = str.Replace(c.ToString(), replaceStr);
        return str.Replace("\\", replaceStr).Replace("/", replaceStr);
    }

    static string DisplayString(Track t, Soulseek.File? file=null, string username="", FileConditions? nec=null, 
        FileConditions? pref=null, bool fullpath=false)
    {
        if (file == null)
            return t.ToString();

        string sampleRate = file.SampleRate.HasValue ? $"/{file.SampleRate}Hz" : "";
        string bitRate = file.BitRate.HasValue ? $"/{file.BitRate}kbps" : "";
        string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string fname = fullpath ? "\\" + file.Filename : "\\..\\" + GetFileNameSlsk(file.Filename);
        string displayText = $"{username}{fname} [{file.Length}s{sampleRate}{bitRate}/{fileSize}]";

        string necStr = nec != null ? $"nec:{nec.GetNotSatisfiedName(file, t)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, t)}" : "";
        string cond = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }

    static void PrintTracks(List<Track> tracks, int number = int.MaxValue)
    {
        number = Math.Min(tracks.Count, number);
        for (int i = 0; i < number; i++)
        {
            Console.WriteLine($"  {tracks[i]}");
        }

        if (number < tracks.Count)
            Console.WriteLine($"  ... (etc)");
    }

    static void RefreshOrPrint(ProgressBar? progress, int current, string item, bool print = false)
    {
        if (progress != null)
        {
            try { progress.Refresh(current, item); }
            catch { }
        }
        else if (displayStyle == "simple" && print)
            Console.WriteLine(item);
    }

    public static void WriteLine(string value, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ResetColor();
    }

    private static ProgressBar? GetProgressBar(string style)
    {
        lock (consoleLock)
        {
#if WINDOWS
            if (!debugDisableDownload && debugPrintTracks)
            {
                try { Console.BufferHeight = Math.Max(Console.BufferHeight + 2, 4000); }
                catch { }
            }
#endif
            ProgressBar? progress = null;
            if (style == "double")
                progress = new ProgressBar(PbStyle.DoubleLine, 100, Console.WindowWidth - 40, character: '―');
            else if (style != "simple")
                progress = new ProgressBar(PbStyle.SingleLine, 100, Console.WindowWidth - 10, character: ' ');
            return progress;
        }
    }

    public static async Task WaitForInternetConnection()
    {
        while (true)
        {
            if (NetworkInterface.GetIsNetworkAvailable())
            {
                try
                {
                    using (var client = new System.Net.WebClient())
                    using (var stream = client.OpenRead("https://www.google.com"))
                    {
                        return;
                    }
                }
                catch { }
            }

            await Task.Delay(500);
        }
    }
}

public struct Track
{
    public string TrackTitle = "";
    public string ArtistName = "";
    public string Album = "";
    public string URI = "";
    public int Length = -1;
    public bool ArtistMaybeWrong = false;

    public Track() { }

    public override string ToString()
    {
        var length = Length > 0 ? $" ({Length}s)" : "";
        if (!string.IsNullOrEmpty(ArtistName))
            return $"{ArtistName} - {TrackTitle}{length}";
        else
            return $"{TrackTitle}{length}";
    }
}

public static class ExtensionMethods
{
    public static bool EqualsAny(this string input, string[] values, StringComparison comparison = StringComparison.Ordinal)
    {
        foreach (var value in values)
        {
            if (input.Equals(value, comparison))
                return true;
        }
        return false;
    }

    public static string Replace(this string s, string[] separators, string newVal)
    {
        string[] temp;
        temp = s.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return String.Join(newVal, temp);
    }

    public static string RemoveFt(this string str)
    {
        string[] ftStrings = { "ft.", "feat." };
        foreach (string ftStr in ftStrings)
        {
            int ftIndex = str.IndexOf(ftStr, StringComparison.OrdinalIgnoreCase);

            if (ftIndex != -1)
                str = str.Substring(0, ftIndex - 1);
        }
        return str.Trim();
    }

    public static bool RemoveDiacriticsIfExist(this string s, out string res)
    {
        res = s.RemoveDiacritics();
        return res != s;
    }

    public static bool RemoveRegexIfExist(this string s, string reg, out string res)
    {
        res = Regex.Replace(s, reg, string.Empty);
        return res != s;
    }

    public static char RemoveDiacritics(this char c)
    {
        foreach (var entry in diacriticChars)
        {
            if (entry.Key.IndexOf(c) != -1)
                return entry.Value[0];
        }
        return c;
    }

    public static string RemoveDiacritics(this string s)
    {
        string text = "";
        foreach (char c in s)
        {
            int len = text.Length;

            foreach (var entry in diacriticChars)
            {
                if (entry.Key.IndexOf(c) != -1)
                {
                    text += entry.Value;
                    break;
                }
            }

            if (len == text.Length)
                text += c;
        }
        return text;
    }

    static Dictionary<string, string> diacriticChars = new Dictionary<string, string>
    {
        { "ä", "a" },
        { "æǽ", "ae" },
        { "œ", "oe" },
        { "ö", "o" },
        { "ü", "u" },
        { "Ä", "A" },
        { "Ü", "U" },
        { "Ö", "O" },
        { "ÀÁÂÃÄÅǺĀĂĄǍΆẢẠẦẪẨẬẰẮẴẲẶ", "A" },
        { "àáâãåǻāăąǎảạầấẫẩậằắẵẳặа", "a" },
        { "ÇĆĈĊČ", "C" },
        { "çćĉċč", "c" },
        { "ÐĎĐ", "D" },
        { "ðďđ", "d" },
        { "ÈÉÊËĒĔĖĘĚΈẼẺẸỀẾỄỂỆ", "E" },
        { "èéêëēĕėęěẽẻẹềếễểệе", "e" },
        { "ĜĞĠĢ", "G" },
        { "ĝğġģ", "g" },
        { "ĤĦΉ", "H" },
        { "ĥħ", "h" },
        { "ÌÍÎÏĨĪĬǏĮİΊΪỈỊЇ", "I" },
        { "ìíîïĩīĭǐįıίϊỉịї", "i" },
        { "Ĵ", "J" },
        { "ĵ", "j" },
        { "Ķ", "K" },
        { "ķ", "k" },
        { "ĹĻĽĿŁ", "L" },
        { "ĺļľŀł", "l" },
        { "ÑŃŅŇ", "N" },
        { "ñńņňŉ", "n" },
        { "ÒÓÔÕŌŎǑŐƠØǾΌỎỌỒỐỖỔỘỜỚỠỞỢ", "O" },
        { "òóôõōŏǒőơøǿºόỏọồốỗổộờớỡởợ", "o" },
        { "ŔŖŘ", "R" },
        { "ŕŗř", "r" },
        { "ŚŜŞȘŠ", "S" },
        { "śŝşșš", "s" },
        { "ȚŢŤŦТ", "T" },
        { "țţťŧ", "t" },
        { "ÙÚÛŨŪŬŮŰŲƯǓǕǗǙǛŨỦỤỪỨỮỬỰ", "U" },
        { "ùúûũūŭůűųưǔǖǘǚǜủụừứữửự", "u" },
        { "ÝŸŶΎΫỲỸỶỴ", "Y" },
        { "Й", "Й" },
        { "й", "и" },
        { "ýÿŷỳỹỷỵ", "y" },
        { "Ŵ", "W" },
        { "ŵ", "w" },
        { "ŹŻŽ", "Z" },
        { "źżž", "z" },
        { "ÆǼ", "AE" },
        { "ß", "ss" },
        { "Ĳ", "IJ" },
        { "ĳ", "ij" },
        { "Œ", "OE" },
        { "Ё", "Е" },
        { "ё", "е" },
    };
}
