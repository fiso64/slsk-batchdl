using AngleSharp.Css;
using AngleSharp.Dom;
using Konsole;
using Newtonsoft.Json.Linq;
using Soulseek;
using System;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using TagLib.Id3v2;
using TagLib.Matroska;
using YoutubeExplode.Playlists;

using ProgressBar = Konsole.ProgressBar;


static class Program
{
    static SoulseekClient? client = null;
    static ConcurrentDictionary<Track, SearchInfo> searches = new ConcurrentDictionary<Track, SearchInfo>();
    static ConcurrentDictionary<string, DownloadWrapper> downloads = new ConcurrentDictionary<string, DownloadWrapper>();
    static List<Track> tracks = new List<Track>();
    static string outputFolder = "";
    static string m3uFilePath = "";
    static string musicDir = "";

    static FileConditions preferredCond = new FileConditions
    {
        Formats = new string[] { "mp3" },
        LengthTolerance = 2,
        MinBitrate = 200,
        MaxBitrate = 2200,
        MaxSampleRate = 96000,
        StrictTitle = true,
        StrictArtist = false,
        BannedUsers = { },
        AcceptNoLength = false,
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
        BannedUsers = { },
        AcceptNoLength = true,
    };

    static string parentFolder = System.IO.Directory.GetCurrentDirectory();
    static string folderName = "";
    static string ytUrl = "";
    static string searchStr = "";
    static string spotifyUrl = "";
    static string spotifyId = "";
    static string spotifySecret = "";
    static string encodedSpotifyId = "MWJmNDY5MWJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI="; // base64 encoded client id and secret to avoid git guardian detection (annoying)
    static string encodedSpotifySecret = "ZmQ3NjYyNmM0ZjcxNGJkYzg4Y2I4ZTQ1ZTU1MDBlNzE=";
    static string ytKey = "";
    static string csvPath = "";
    static string username = "";
    static string password = "";
    static string artistCol = "";
    static string albumCol = "";
    static string trackCol = "";
    static string ytIdCol = "";
    static string descCol = "";
    static string lengthCol = "";
    static bool aggregate = false;
    static string removeRegex = "";
    static string noRegexSearch = "";
    static string timeUnit = "s";
    static string displayStyle = "single";
    static string input = "";
    static bool preciseSkip = true;
    static string nameFormat = "";
    static bool skipNotFound = false;
    static bool noArtistSearchTrack = false;
    static bool albumSearchTrack = false;
    static bool artistSearchTrack = false;
    static bool noDiacrSearch = false;
    static bool ytParse = false;
    static bool removeFt = false;
    static bool removeBrackets = false;
    static bool reverse = false;
    static bool useYtdlp = false;
    static bool skipExisting = false;
    static bool createM3u = false;
    static bool m3uOnly = false;
    static bool useTagsCheckExisting = false;
    static bool removeTracksFromSource = false;
    static bool getDeleted = false;
    static bool removeSingleCharacterSearchTerms = false;
    static int maxTracks = int.MaxValue;
    static int minUsersAggregate = 2;
    static bool relax = false;
    static int offset = 0;

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
    static int listenPort = 50000;
    static bool slowConsoleOutput = false;

    static object consoleLock = new object();

    static DateTime lastUpdate;
    static bool skipUpdate = false;
    static bool debugDisableDownload = false;
    static bool debugPrintTracks = false;
    static bool noModifyShareCount = false;
    static bool printResultsFull = false;
    static bool debugPrintTracksFull = false;
    static bool useRandomLogin = false;

    static int searchesPerTime = 34;
    static int searchResetTime = 220;
    static RateLimitedSemaphore? searchSemaphore;

    static string inputType = "";

    static void PrintHelp()
    {
        // undocumented options:
        // --m3u-only, --yt-dlp-f, --slow-output,
        // --no-modify-share-count, --max-retries, --max-results-per-user, --album-search
        // --artist-col, --title-col, --album-col, --length-col, --yt-desc-col, --yt-id-col
        // --remove-brackets, --spotify, --csv, --string, --youtube, --random-login
        // --danger-words, --pref-danger-words
        Console.WriteLine("Usage: slsk-batchdl <input> [OPTIONS]" +
                            "\n" +
                            "\n  <input>                        <input> is one of the following:" +
                            "\n" +
                            "\n                                 Spotify playlist url or \"spotify-likes\": Download a spotify" +
                            "\n                                 playlist or your liked songs. --spotify-id and" +
                            "\n                                 --spotify-secret may be required in addition." +
                            "\n" +
                            "\n                                 Youtube playlist url: Download songs from a youtube playlist." +
                            "\n                                 Provide a --youtube-key to include unavailabe uploads." +
                            "\n" +
                            "\n                                 Path to a local CSV file: Use a csv file containing track" +
                            "\n                                 info to download. The names of the columns should be Artist, " +
                            "\n                                 Title, Album, Length. Only the title column is required, but" +
                            "\n                                 any extra info improves search results." +
                            "\n" +
                            "\n                                 Name of the track, album, or artist to search for:" +
                            "\n                                 Can either be any typical search string or a comma-separated" +
                            "\n                                 list like \"title=Song Name,artist=Artist Name,length=215\"" +
                            "\n                                 Allowed properties are: title, artist, album, length (sec)" +
                            "\n" +
                            "\nOptions:" +
                            "\n  --user <username>              Soulseek username" +
                            "\n  --pass <password>              Soulseek password" +
                            "\n" +
                            "\n  --spotify-id <id>              spotify client ID" +
                            "\n  --spotify-secret <secret>      spotify client secret" +
                            "\n" +
                            "\n  --youtube-key <key>            Youtube data API key" +
                            "\n  --get-deleted                  Attempt to retrieve titles of deleted videos from wayback" +
                            "\n                                 machine. Requires yt-dlp." +
                            "\n" +
                            "\n  --time-format <format>         Time format in Length column of the csv file (e.g h:m:s.ms" +
                            "\n                                 for durations like 1:04:35.123). Default: s" +
                            "\n  --yt-parse                     Enable if the csv file contains YouTube video titles and" +
                            "\n                                 channel names; attempt to parse them into title and artist" +
                            "\n                                 names." +
                            "\n" +
                            "\n  -a --aggregate                 When input is a string: Instead of downloading a single" +
                            "\n                                 track matching the search string, find and download all" +
                            "\n                                 distinct songs associated with the provided artist, album," +
                            "\n                                 or track title. Input string must be a list of properties." +
                            "\n  --min-users-aggregate <num>    Minimum number of users sharing a track before it is" +
                            "\n                                 downloaded in aggregate mode. Setting it to higher values" +
                            "\n                                 will significantly reduce false positives, but may introduce" +
                            "\n                                 false negatives. Default: 2" +
                            "\n  --relax                        Slightly relax file filtering in aggregate mode to include" +
                            "\n                                 more results" +
                            "\n" +
                            "\n  -p --path <path>               Download folder" +
                            "\n  -f --folder <name>             Subfolder name (default: playlist/csv name)" +
                            "\n  -n --number <maxtracks>        Download the first n tracks of a playlist" +
                            "\n  -o --offset <offset>           Skip a specified number of tracks" +
                            "\n  -r --reverse                   Download tracks in reverse order" +
                            "\n  --remove-from-playlist         Remove downloaded tracks from playlist (spotify only)" +
                            "\n  --name-format <format>         Name format for downloaded tracks, e.g \"{artist} - {title}\"" +
                            "\n  --m3u                          Create an m3u8 playlist file" +
                            "\n" +
                            "\n  --format <format>              Accepted file format(s), comma-separated" +
                            "\n  --length-tol <sec>             Length tolerance in seconds (default: 3)" +
                            "\n  --min-bitrate <rate>           Minimum file bitrate" +
                            "\n  --max-bitrate <rate>           Maximum file bitrate" +
                            "\n  --max-samplerate <rate>        Maximum file sample rate" +
                            "\n  --strict-title                 Only download if filename contains track title" +
                            "\n  --strict-artist                Only download if filepath contains track artist" +
                            "\n  --banned-users <list>          Comma-separated list of users to ignore" +
                            "\n" +
                            "\n  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)" +
                            "\n  --pref-length-tol <sec>        Preferred length tolerance in seconds (default: 2)" +
                            "\n  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)" +
                            "\n  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2200)" +
                            "\n  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 96000)" +
                            "\n  --pref-strict-artist           Prefer download if filepath contains track artist" +
                            "\n  --pref-banned-users <list>     Comma-separated list of users to deprioritize" +
                            "\n" +
                            "\n  -s --skip-existing             Skip if a track matching file conditions is found in the" +
                            "\n                                 output folder or your music library (if provided)" +
                            "\n  --skip-mode <mode>             name: Use only filenames to check if a track exists" +
                            "\n                                 name-precise (default): Use filenames and check conditions" +
                            "\n                                 tag: Use file tags (slower)" +
                            "\n                                 tag-precise: Use file tags and check file conditions" +
                            "\n  --music-dir <path>             Specify to skip downloading tracks found in a music library" +
                            "\n                                 Use with --skip-existing" +
                            "\n  --skip-not-found               Skip searching for tracks that weren't found on Soulseek" +
                            "\n                                 during the last run." +
                            "\n  --remove-ft                    Remove \"ft.\" or \"feat.\" and everything after from the" +
                            "\n                                 track names before searching" +
                            "\n  --remove-regex <regex>         Remove a regex from all track titles and artist names" +
                            "\n  --no-artist-search             Perform an additional search without artist name if nothing" +
                            "\n                                 was found. Useful for sources such as youtube or soundcloud" +
                            "\n                                 where the \"artist\" could just be an uploader." +
                            "\n  --artist-search                Also try to find track by searching for the artist only" +
                            "\n  --no-diacr-search              Also perform a search without diacritics" +
                            "\n  --no-regex-search <regex>      Also perform a search without a regex pattern" +
                            "\n  --yt-dlp                       Use yt-dlp to download tracks that weren't found on" +
                            "\n                                 Soulseek. yt-dlp must be available from the command line." +
                            "\n" +
                            "\n  --config <path>                Manually specify config file location" +
                            "\n  --search-timeout <ms>          Max search time in ms (default: 6000)" +
                            "\n  --max-stale-time <ms>          Max download time without progress in ms (default: 50000)" +
                            "\n  --concurrent-downloads <num>   Max concurrent downloads (default: 2)" +
                            "\n  --searches-per-time <num>      Max searches per time interval. Higher values may cause" +
                            "\n                                 30-minute bans. (default: 34)" +
                            "\n  --searches-time <sec>          Controls how often available searches are replenished." +
                            "\n                                 Lower values may cause 30-minute bans. (default: 220)" +
                            "\n  --display <option>             Changes how searches and downloads are displayed:" +
                            "\n                                 single (default): Show transfer state and percentage" +
                            "\n                                 double: Transfer state and a large progress bar " +
                            "\n                                 simple: No download bars or changing percentages" +
                            "\n  --listen-port <port>           Port for incoming connections (default: 50000)" +
                            "\n" +
                            "\n  --print <option>               Print tracks or search results instead of downloading:" +
                            "\n                                 tracks: Print all tracks to be downloaded" +
                            "\n                                 tracks-full: Print extended information about all tracks" +
                            "\n                                 results: Print search results satisfying file conditions" +
                            "\n                                 results-full: Print search results including full paths");
    }

    static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

#if WINDOWS
        try
        {
            if (Console.BufferHeight <= 50)
                WriteLine("Windows: Recommended to use the command prompt instead of terminal app to avoid printing issues.", ConsoleColor.DarkYellow);
        }
        catch { }
#endif

        if (args.Contains("--help") || args.Contains("-h") || args.Length == 0)
        {
            PrintHelp();
            return;
        }

        bool confPathChanged = false;
        int idx = Array.IndexOf(args, "--config");
        if (idx != -1) {
            confPath = args[idx + 1];
            confPathChanged = true;
        }

        if (System.IO.File.Exists(confPath) || confPathChanged)
        {
            string confArgs = System.IO.File.ReadAllText(confPath);
            List<string> finalArgs = new List<string>();
            finalArgs.AddRange(ParseCommand(confArgs));
            finalArgs.AddRange(args);
            args = finalArgs.ToArray();
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("-"))
            {
                switch (args[i])
                {
                    case "-i":
                    case "--input":
                        input = args[++i];
                        break;
                    case "--spotify":
                        inputType = "spotify";
                        break;
                    case "--youtube":
                        inputType = "youtube";
                        break;
                    case "--csv":
                        inputType = "csv";
                        break;
                    case "--string":
                        inputType = "string";
                        break;
                    case "-p":
                    case "--path":
                        parentFolder = args[++i];
                        break;
                    case "--config":
                        confPath = args[++i];
                        break;
                    case "-f":
                    case "--folder":
                        folderName = args[++i];
                        break;
                    case "--music-dir":
                        musicDir = args[++i];
                        break;
                    case "-a":
                    case "--aggregate":
                        aggregate = true;
                        break;
                    case "--min-users-aggregate":
                        minUsersAggregate = int.Parse(args[++i]);
                        break;
                    case "--relax":
                        relax = true;
                        break;
                    case "--no-artist-search":
                        noArtistSearchTrack = true;
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
                    case "--random-login":
                        useRandomLogin = true;
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
                    case "--print":
                        string opt = args[++i];
                        if (opt == "tracks")
                        {
                            debugPrintTracks = true;
                            debugDisableDownload = true;
                        }
                        else if (opt == "tracks-full")
                        {
                            debugPrintTracks = true;
                            debugPrintTracksFull = true;
                            debugDisableDownload = true;
                        }
                        else if (opt == "results")
                            debugDisableDownload = true;
                        else if (opt == "results-full")
                        {
                            debugDisableDownload = true;
                            printResultsFull = true;
                        }
                        else
                            throw new ArgumentException($"Unknown print option {opt}");
                        break;
                    case "--yt-parse":
                        ytParse = true;
                        break;
                    case "--length-col":
                        lengthCol = args[++i];
                        break;
                    case "--time-format":
                        timeUnit = args[++i];
                        break;
                    case "--yt-dlp":
                        useYtdlp = true;
                        break;
                    case "--yt-dlp-f":
                        ytdlpFormat = args[++i];
                        break;
                    case "-s":
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
                    case "--remove-brackets":
                        removeBrackets = true;
                        break;
                    case "--get-deleted":
                        getDeleted = true;
                        break;
                    case "--remove-regex":
                        removeRegex = args[++i];
                        break;
                    case "--no-regex-search":
                        noRegexSearch = args[++i];
                        break;
                    case "-r":
                    case "--reverse":
                        reverse = true;
                        break;
                    case "--m3u":
                        createM3u = true;
                        break;
                    case "--m3u-only":
                        m3uOnly = true;
                        break;
                    case "--listen-port":
                        listenPort = int.Parse(args[++i]);
                        break;
                    case "--search-timeout":
                        searchTimeout = int.Parse(args[++i]);
                        break;
                    case "--max-stale-time":
                        downloadMaxStaleTime = int.Parse(args[++i]);
                        break;
                    case "--concurrent-downloads":
                        maxConcurrentProcesses = int.Parse(args[++i]);
                        break;
                    case "--searches-per-time":
                        searchesPerTime = int.Parse(args[++i]);
                        break;
                    case "--searches-time":
                        searchResetTime = int.Parse(args[++i]);
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
                    case "--pref-banned-users":
                        preferredCond.BannedUsers = args[++i].Split(',');
                        break;
                    case "--format":
                        necessaryCond.Formats = args[++i].Split(',', StringSplitOptions.TrimEntries);
                        break;
                    case "--length-tol":
                        necessaryCond.LengthTolerance = int.Parse(args[++i]);
                        break;
                    case "--min-bitrate":
                        necessaryCond.MinBitrate = int.Parse(args[++i]);
                        break;
                    case "--max-bitrate":
                        necessaryCond.MaxBitrate = int.Parse(args[++i]);
                        break;
                    case "--max-samplerate":
                        necessaryCond.MaxSampleRate = int.Parse(args[++i]);
                        break;
                    case "--danger-words":
                        necessaryCond.DangerWords = args[++i].Split(',');
                        break;
                    case "--strict-title":
                        necessaryCond.StrictTitle = true;
                        break;
                    case "--strict-artist":
                        necessaryCond.StrictArtist = true;
                        break;
                    case "--banned-users":
                        necessaryCond.BannedUsers = args[++i].Split(',');
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
                                throw new ArgumentException($"Invalid display style \"{args[i]}\"");
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
                                throw new ArgumentException($"Invalid skip mode \"{args[i]}\"");
                        }
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            else
            {
                if (input == "")
                    input = args[i];
                else
                    throw new ArgumentException($"Invalid argument \"{input}\"");
            }
        }

        client = new SoulseekClient(new SoulseekClientOptions(listenPort: listenPort));

        if (input == "")
            throw new ArgumentException($"No input provided");

        if (ytKey != "")
            YouTube.apiKey = ytKey;

        if (debugDisableDownload)
            maxConcurrentProcesses = 1;

        searchSemaphore = new RateLimitedSemaphore(searchesPerTime, TimeSpan.FromSeconds(searchResetTime));

        int max = reverse ? int.MaxValue : maxTracks;
        int off = reverse ? 0 : offset;

        if (inputType=="youtube" || (inputType == "" && input.StartsWith("http") && input.Contains("youtu"))) 
        {
            ytUrl = input;
            inputType = "youtube";

            string name;
            List<Track>? deleted = null;

            if (getDeleted)
            {
                Console.WriteLine("Getting deleted videos..");
                var archive = new YouTube.YouTubeArchiveRetriever();
                deleted = await archive.RetrieveDeleted(ytUrl);
            }
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
            if (deleted != null)
            {
                tracks.InsertRange(0, deleted);
            }

            if (folderName == "")
                folderName = ReplaceInvalidChars(name, " ");

            YouTube.StopService();
        }
        else if (inputType == "spotify" || (inputType == "" && (input.StartsWith("http") && input.Contains("spotify")) || input == "spotify-likes")) 
        {
            spotifyUrl = input;
            inputType = "spotify";

            string? playlistName;
            bool usedDefaultId = false;
            bool login = spotifyUrl == "spotify-likes" || removeTracksFromSource;

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

            if (spotifyUrl == "spotify-likes")
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
                folderName = ReplaceInvalidChars(playlistName, " ");
        }
        else if (inputType == "csv" || (inputType == "" && Path.GetExtension(input).Equals(".csv", StringComparison.OrdinalIgnoreCase))) 
        {
            csvPath = input;
            inputType = "csv";

            if (!System.IO.File.Exists(csvPath))
                throw new Exception("CSV file not found");

            Console.WriteLine("Parsing CSV track info");
            tracks = await ParseCsvIntoTrackInfo(csvPath, artistCol, trackCol, lengthCol, albumCol, descCol, ytIdCol, timeUnit, ytParse);
            tracks = tracks.Skip(off).Take(max).ToList();

            if (folderName == "")
                folderName = Path.GetFileNameWithoutExtension(csvPath);
        }
        else 
        {
            searchStr = input;
            inputType = "string";
            var music = ParseTrackArg(searchStr);
            removeSingleCharacterSearchTerms = music.TrackTitle.Length != 1 && music.ArtistName.Length != 1;

            if (!aggregate) 
                tracks.Add(music);
            else
            {
                removeSingleCharacterSearchTerms = music.ArtistName == "" && music.TrackTitle.Length > 1;
                if (folderName == "")
                    folderName = ReplaceInvalidChars(searchStr, " ");

                await Login();

                var x = new List<string>();
                if (music.ArtistName != "")
                    x.Add($"artist: {music.ArtistName}");
                if (music.TrackTitle != "")
                    x.Add($"title: {music.TrackTitle}");
                if (music.Album != "")
                    x.Add($"album: {music.Album}");
                if (music.Length >= 0)
                    x.Add($"length: {music.Length}s");

                Console.WriteLine($"Searching for tracks associated with {string.Join(", ", x)}");
                tracks = await GetUniqueRelatedTracks(music);
            }
        }

        if (reverse)
        {
            tracks.Reverse();
            tracks = tracks.Skip(offset).Take(maxTracks).ToList();
        }

        if (!aggregate)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                Track track = tracks[i];
                if (removeFt) {
                    track.TrackTitle = track.TrackTitle.RemoveFt();
                    track.ArtistName = track.ArtistName.RemoveFt();
                }
                if (removeBrackets) {
                    track.TrackTitle = track.TrackTitle.RemoveSquareBrackets();
                }
                if (removeRegex != "") {
                    track.TrackTitle = Regex.Replace(track.TrackTitle, removeRegex, "");
                    track.ArtistName = Regex.Replace(track.ArtistName, removeRegex, "");
                }
                tracks[i] = track;
            }
        }

        folderName = ReplaceInvalidChars(folderName, " ");

        outputFolder = Path.Combine(parentFolder, folderName);

        if (m3uFilePath != "")
            m3uFilePath = Path.Combine(m3uFilePath, folderName + ".m3u8");
        else 
            m3uFilePath = Path.Combine(outputFolder, folderName + ".m3u8");

        var tracksStart = new List<Track>(tracks);
        var m3uEditor = new M3UEditor(m3uFilePath, outputFolder, tracksStart, offset);

        createM3u |= m3uOnly;
        if (skipExisting || m3uOnly)
        {
            var existing = new Dictionary<Track, string>();
            if (!(musicDir != "" && outputFolder.StartsWith(musicDir, StringComparison.OrdinalIgnoreCase)) && System.IO.Directory.Exists(outputFolder))
            {
                Console.WriteLine($"Checking if tracks exist in output folder");
                var d = RemoveTracksIfExist(tracks, outputFolder, necessaryCond, useTagsCheckExisting, preciseSkip);
                d.ToList().ForEach(x => existing.TryAdd(x.Key, x.Value));
            }
            if (musicDir != "" && System.IO.Directory.Exists(musicDir))
            {
                Console.WriteLine($"Checking if tracks exist in library");
                var d = RemoveTracksIfExist(tracks, musicDir, necessaryCond, useTagsCheckExisting, preciseSkip);
                d.ToList().ForEach(x => existing.TryAdd(x.Key, x.Value));
            }
            else if (musicDir != "" && !System.IO.Directory.Exists(musicDir))
                Console.WriteLine($"Path does not exist: {musicDir}");

            if (createM3u && !debugDisableDownload && !debugPrintTracks) {
                foreach (var x in existing)
                    m3uEditor.WriteSuccess(x.Value, x.Key, false);
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

        int tracksCount2 = tracks.Count;

        if (System.IO.File.Exists(m3uEditor.path) && skipNotFound)
        {
            string m3uText = m3uEditor.ReadAllText();
            foreach (var track in tracks.ToList())
            {
                if (m3uText.Contains(track.ToString() + " [No suitable file found]"))
                    tracks.Remove(track);
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
            PrintTracks(tracks, fullInfo: debugPrintTracksFull);
            var skipped = tracksStart.Where(t => !tracks.Contains(t)).ToList();
            if (skipped.Count > 0) {
                if (debugPrintTracksFull) {
                    Console.WriteLine("\n#############################################\n");
                }
                Console.WriteLine($"\nSkipped:");
                PrintTracks(skipped, fullInfo: debugPrintTracksFull);
            }
            return;
        }
        else if (tracks.Count > 1 || skippedTracks != "")
        {
            PrintTracks(tracks, 10);
            Console.WriteLine($"Downloading {tracks.Count} tracks{skippedTracks}\n");
        }

        if (!useRandomLogin && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)))
            throw new ArgumentException("No soulseek username or password");
        
        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn)) 
            await Login(useRandomLogin);

        int successCount = 0;
        int failCount = 0;

        var UpdateTask = Task.Run(() => Update());

        SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);
        var downloadTasks = tracks.Select(async (track) =>
        {
            await semaphore.WaitAsync();
            int tries = 2;
        retry:
            try
            {
                await WaitForNetworkAndLogin();
                var savedFilePath = await SearchAndDownload(track);
                Interlocked.Increment(ref successCount);
                if (removeTracksFromSource && !string.IsNullOrEmpty(spotifyUrl))
                    spotifyClient.RemoveTrackFromPlaylist(playlistUri, track.URI);
                if (createM3u && !debugDisableDownload)
                    m3uEditor.WriteSuccess(savedFilePath, track);
            }
            catch (Exception ex)
            {
                if (ex is SearchAndDownloadException)
                {
                    Interlocked.Increment(ref failCount);
                    if (!debugDisableDownload && inputType != "string")
                        m3uEditor.WriteFail(ex.Message, track);
                }
                else if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                {
                    goto retry;
                }
                else
                {
                    WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                    if (tries-- > 0)
                        goto retry;
                    Interlocked.Increment(ref failCount);
                }
            }
            finally { semaphore.Release(); }

            if ((successCount + failCount + 1) % 50 == 0)
                WriteLine($"\nSuccesses: {successCount}, fails: {failCount}, tracks left: {tracksRemaining}\n", ConsoleColor.Yellow, true);

            Interlocked.Decrement(ref tracksRemaining);
        });

        await Task.WhenAll(downloadTasks);

        if (tracks.Count > 1)
            Console.WriteLine($"\n\nDownloaded {tracks.Count - failCount} of {tracks.Count} tracks");
        if (System.IO.File.Exists(m3uEditor.path))
            Console.WriteLine($"\nFailed:\n{string.Join("\n", m3uEditor.ReadAllLines().Where(x => x.StartsWith("# Failed:")))}");
    }


    static async Task Login(bool random=false, int tries=3)
    {
        string user = username, pass = password;
        if (random)
        {
            var r = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            pass = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
        }

        while (true)
        {
            try
            {
                await WaitForInternetConnection();
                await client.ConnectAsync(user, pass);
                if (!noModifyShareCount)
                    await client.SetSharedCountsAsync(10, 50);
                break;
            }
            catch {
                if (--tries == 0)
                    throw;
            }
        }
    }


    static async Task<string> SearchAndDownload(Track track)
    {
        Console.ResetColor();
        ProgressBar? progress = GetProgressBar(displayStyle);
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        var badUsers = new ConcurrentBag<string>();
        var cts = new CancellationTokenSource();
        var saveFilePath = "";
        Task? downloadTask = null;
        object downloadingLocker = new object();
        bool downloading = false;
        bool notFound = false;

        if (track.Downloads != null) {
            results = track.Downloads;
            goto downloads;
        }

        RefreshOrPrint(progress, 0, $"Waiting: {track}", false);

        var title = $"{track.ArtistName} {track.TrackTitle}".Trim();
        string searchText = $"{title}";
        var removeChars = new string[] { " ", "_", "-" };

        if (track.TrackTitle.Replace(removeChars, "").ReplaceInvalidChars("") == "")
        {
            RefreshOrPrint(progress, 0, $"Track title only contains invalid characters: {title}, not searching", true);
            throw new SearchAndDownloadException($"Track title has only invalid chars");
        }

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
                    //var f = r.Files.First();
                    //if (r.HasFreeUploadSlot && r.UploadSpeed / 1000000 >= 1 && cond.FileSatisfies(f, track, r))
                    //{
                    //    lock (downloadingLocker)
                    //    {
                    //        if (!downloading)
                    //        {
                    //            downloading = true;
                    //            saveFilePath = GetSavePath(f.Filename, track);
                    //            downloadTask = DownloadFile(r, f, saveFilePath, track, progress, cts);
                    //            downloadTask.ContinueWith(task => {
                    //                lock (downloadingLocker)
                    //                {
                    //                    downloading = false;
                    //                    saveFilePath = "";
                    //                    results.TryRemove(r.Username + "\\" + f.Filename, out _);
                    //                    badUsers.Add(r.Username);
                    //                }
                    //            }, TaskContinuationOptions.OnlyOnFaulted);
                    //        }
                    //    }
                    //}
                }
            };
        }

        SearchOptions getSearchOptions(int timeout, FileConditions cond) 
        {
            return new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                searchTimeout: searchTimeout,
                removeSingleCharacterSearchTerms: removeSingleCharacterSearchTerms,
                responseFilter: (response) => {
                    return response.UploadSpeed > 0
                            && cond.BannedUsersSatisfies(response);
                },
                fileFilter: (file) => {
                    return IsMusicFile(file.Filename) && (cond.FileSatisfies(file, track, null) || printResultsFull);
                });
        }

        var responseHandler = getResponseHandler(preferredCond, maxResultsPerUser);
        var responseHandlerUncapped = getResponseHandler(preferredCond);
        var searchOptions = getSearchOptions(searchTimeout, necessaryCond);

        var onSearch = () => RefreshOrPrint(progress, 0, $"Searching: {track}", true);
        await RunSearches(searchText, searchOptions, responseHandler, cts.Token, onSearch);

        if (results.Count == 0 && albumSearchTrack && track.Album != "")
        {
            searchText = $"{track.Album}";
            var necCond2 = new FileConditions(necessaryCond);
            necCond2.StrictTitle = true;
            searchOptions = getSearchOptions(Math.Min(5000, searchTimeout), necCond2);

            RefreshOrPrint(progress, 0, $"Waiting (album name search): {track}");
            onSearch = () => RefreshOrPrint(progress, 0, $"Searching (album name): {track}");
            await RunSearches(searchText, searchOptions, responseHandlerUncapped, cts.Token, onSearch);
        }

        if (results.Count == 0 && (noArtistSearchTrack || track.ArtistMaybeWrong) && !string.IsNullOrEmpty(track.ArtistName))
        {
            searchText = $"{track.TrackTitle}";
            var necCond2 = new FileConditions(necessaryCond);
            necCond2.LengthTolerance = Math.Min(necCond2.LengthTolerance, 1);
            necCond2.StrictArtist = true;
            searchOptions = getSearchOptions(Math.Min(5000, searchTimeout), necCond2);

            RefreshOrPrint(progress, 0, $"Waiting (no artist name search): {track}");
            onSearch = () => RefreshOrPrint(progress, 0, $"Searching (no artist name): {track}");
            await RunSearches(searchText, searchOptions, responseHandlerUncapped, cts.Token, onSearch);
        }

        if (results.Count == 0 && artistSearchTrack && !string.IsNullOrEmpty(track.ArtistName))
        {
            searchText = $"{track.ArtistName}";
            var necCond2 = new FileConditions(necessaryCond);
            necCond2.StrictTitle = true;
            searchOptions = getSearchOptions(Math.Min(6000, searchTimeout), necCond2);

            RefreshOrPrint(progress, 0, $"Waiting (artist name search): {track}");
            onSearch = () => RefreshOrPrint(progress, 0, $"Searching (artist name): {track}");
            await RunSearches(searchText, searchOptions, responseHandlerUncapped, cts.Token, onSearch);
        }

        lock (downloadingLocker) { }
        searches.TryRemove(track, out _);

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

    downloads:

        if (debugDisableDownload && results.Count == 0)
        {
            WriteLine($"No results", ConsoleColor.Yellow);
            return "";
        }
        else if (!downloading && results.Count > 0)
        {
            var random = new Random();
            var fileResponses = OrderedResults(results, track, badUsers, true);

            if (debugDisableDownload)
            {
                foreach (var x in fileResponses) {
                    Console.WriteLine(DisplayString(track, x.file, x.response,
                        (printResultsFull ? necessaryCond : null), (printResultsFull ? preferredCond : null), printResultsFull));
                }
                WriteLine($"Total: {fileResponses.Count()}\n", ConsoleColor.Yellow);
                return "";
            }

            var newBadUsers = new ConcurrentBag<string>();
            var ignoredResults = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
            foreach (var x in fileResponses)
            {
                if (newBadUsers.Contains(x.response.Username))
                {
                    ignoredResults.TryAdd(x.response.Username + "\\" + x.file.Filename, (x.response, x.file));
                    continue;
                }
                saveFilePath = GetSavePath(x.file.Filename, track);
                try
                {
                    downloading = true;
                    await DownloadFile(x.response, x.file, saveFilePath, track, progress);
                    break;
                }
                catch
                {
                    downloading = false;
                    if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                        throw;
                    newBadUsers.Add(x.response.Username);
                    if (--maxRetriesPerTrack <= 0)
                    {
                        RefreshOrPrint(progress, 0, $"Out of download retries: {track}, skipping", true);
                        throw new SearchAndDownloadException("Out of download retries");
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
                throw new SearchAndDownloadException("No suitable file found");
            }
            else
            {
                RefreshOrPrint(progress, 0, $"Failed to download: {track}, skipping", true);
                throw new SearchAndDownloadException("All downloads failed");
            }
        }

        if (nameFormat != "")
            saveFilePath = ApplyNamingFormat(saveFilePath, nameFormat);

        return saveFilePath;
    }


    public class SearchAndDownloadException: Exception
    {
        public SearchAndDownloadException(string text = "") : base(text) { }
    }


    static async Task<List<Track>> GetUniqueRelatedTracks(Track track)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        var opts = new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: removeSingleCharacterSearchTerms,
                searchTimeout: searchTimeout,
                responseFilter: (response) => {
                    return response.UploadSpeed > 0
                            && necessaryCond.BannedUsersSatisfies(response);
                },
                fileFilter: (file) => {
                    return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track, null)
                        && FileConditions.StrictString(file.Filename, track.ArtistName, ignoreCase: true)
                        && FileConditions.StrictString(file.Filename, track.TrackTitle, ignoreCase: true)
                        && FileConditions.StrictString(file.Filename, track.Album, ignoreCase: true);
        });
        Action<SearchResponse> handler = (r) => {
            if (r.Files.Count() > 0)
            {
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));
            }
        };
        var cts = new CancellationTokenSource();

        string search = "";
        if (track.TrackTitle != "")
            search = track.TrackTitle;
        else if (track.Album != "")
            search = track.Album;
        if (track.ArtistName != "" && search == "")
            search = track.ArtistName;
        else if (track.ArtistName != "")
            search = track.ArtistName + " " + search;

        await RunSearches(search, opts, handler, cts.Token);

        string artistName = track.ArtistName.Trim();
        string trackName = track.TrackTitle.Trim();
        string albumName = track.Album.Trim();

        var fileResponses = results.Select(x => x.Value);

        var equivalentFiles = EquivalentFiles(track, fileResponses).ToList();

        if (!relax)
        {
            equivalentFiles = equivalentFiles
                .Where(x => FileConditions.StrictString(x.Item1.TrackTitle, track.TrackTitle, ignoreCase: true)
                        && (FileConditions.StrictString(x.Item1.ArtistName, track.ArtistName, ignoreCase: true) 
                            || FileConditions.StrictString(x.Item1.TrackTitle, track.ArtistName, ignoreCase: true)
                                && x.Item1.TrackTitle.ContainsInBrackets(track.ArtistName, ignoreCase: true)))
                .ToList();
        }

        var tracks = equivalentFiles
            .Select(kvp => {
                kvp.Item1.Downloads = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>(
                    kvp.Item2.ToDictionary(item => { return item.response.Username + "\\" + item.file.Filename; }, item => item));
                return kvp.Item1; 
            }).ToList();

        return tracks;
    }


    static IOrderedEnumerable<(Track, IEnumerable<(SearchResponse response, Soulseek.File file)>)> EquivalentFiles(Track track, IEnumerable<(SearchResponse, Soulseek.File)> fileResponses, int minShares=-1)
    {
        if (minShares == -1)
            minShares = minUsersAggregate;

        var inferTrack = ((SearchResponse r, Soulseek.File f) x) => {
            Track t = track;
            t.Length = x.f.Length ?? -1;
            return InferTrack(x.f.Filename, t);
        };

        var res = fileResponses
            .GroupBy(inferTrack, new TrackStringComparer(ignoreCase: true))
            .Where(group => group.Select(x => x.Item1.Username).Distinct().Count() >= minShares)
            .SelectMany(group => {
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
                        if (Math.Abs(l1 - l2) <= necessaryCond.LengthTolerance)
                        {
                            subGroup.Add(sortedTracks[j]);
                            j++;
                        }
                        else break;
                    }
                    Track t = group.Key;
                    t.Length = (int)sortedTracks[i].Item2.Length;
                    groups.Add((t, subGroup));
                    i = j;
                }

                if (noLengthGroup.Count() > 0)
                {
                    if (groups.Count() > 0 && !preferredCond.AcceptNoLength)
                        groups.First().Item2.AddRange(noLengthGroup);
                    else
                        groups.Add((group.Key, noLengthGroup.ToList()));
                }

                return groups.Where(subGroup => subGroup.Item2.Select(x => x.Item1.Username).Distinct().Count() >= minShares)
                    .Select(subGroup => (subGroup.Item1, subGroup.Item2.AsEnumerable()));
            }).OrderByDescending(x => x.Item2.Count());

        return res;
    }


    static IOrderedEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(IEnumerable<KeyValuePair<string, (SearchResponse, Soulseek.File)>> results, Track track, IEnumerable<string> ignoreUsers, bool useInfer=false)
    {
        Dictionary<string, (Track, int)>? result = null;
        if (useInfer)
        {
            var equivalentFiles = EquivalentFiles(track, results.Select(x => x.Value), 1);
            result = equivalentFiles
                .SelectMany(t => t.Item2, (t, f) => new { t.Item1, f.response.Username, f.file.Filename, Count = t.Item2.Count() })
                .ToSafeDictionary(
                    x => $"{x.Username}\\{x.Filename}",
                    x => (x.Item1, x.Count));
        }

        var infTrack = ((SearchResponse response, Soulseek.File file) x) => {
            string key = $"{x.response.Username}\\{x.file.Filename}";
            if (result != null && result.ContainsKey(key))
                return result[key];
            return (new Track(), 0);
        };

        var bracketCheck = ((SearchResponse response, Soulseek.File file) x) => {
            Track inferredTrack = infTrack(x).Item1;
            string t1 = track.TrackTitle.RemoveFt().Replace('[', '(');
            string t2 = inferredTrack.TrackTitle.RemoveFt().Replace('[', '(');
            return track.ArtistMaybeWrong || t1.Contains('(') || !t2.Contains('(');
        };

        var levenshtein = ((SearchResponse response, Soulseek.File file) x) => {
            Track inferredTrack = infTrack(x).Item1;
            string t1 = track.TrackTitle.ReplaceInvalidChars("").Replace(" ", "").Replace("_", "").RemoveFt().ToLower();
            string t2 = inferredTrack.TrackTitle.ReplaceInvalidChars("").Replace(" ", "").Replace("_", "").RemoveFt().ToLower();
            return Utils.Levenshtein(t1, t2);
        };

        var random = new Random();
        return results.Select(kvp => (response: kvp.Value.Item1, file: kvp.Value.Item2))
                .OrderByDescending(x => !ignoreUsers.Contains(x.response.Username))
                .ThenByDescending(x => necessaryCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => (x.file.Length != null && x.file.Length > 0) || preferredCond.AcceptNoLength)
                .ThenByDescending(x => preferredCond.BannedUsersSatisfies(x.response))
                .ThenByDescending(x => bracketCheck(x))
                .ThenByDescending(x => preferredCond.StrictTitleSatisfies(x.file.Filename, track.TrackTitle))
                .ThenByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FormatSatisfies(x.file.Filename))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 600)
                .ThenByDescending(x => FileConditions.StrictString(x.file.Filename, track.TrackTitle))
                .ThenByDescending(x => FileConditions.StrictString(x.file.Filename, track.ArtistName))
                .ThenByDescending(x => levenshtein(x) <= 5)
                .ThenByDescending(x => x.response.UploadSpeed / 300)
                .ThenByDescending(x => (x.file.BitRate ?? 0) / 70)
                .ThenByDescending(x => infTrack(x).Item2)
                .ThenByDescending(x => random.Next());
    }


    static async Task RunSearches(string search, SearchOptions opts, Action<SearchResponse> rHandler, CancellationToken ct, Action? onSearch=null)
    {
        await searchSemaphore.WaitAsync();
        await WaitForInternetConnection();
        try
        {
            var q = SearchQuery.FromText(search);
            var searchTasks = new List<Task>();
            onSearch?.Invoke();
            searchTasks.Add(client.SearchAsync(q, options: opts, cancellationToken: ct, responseHandler: rHandler));

            if (noDiacrSearch && search.RemoveDiacriticsIfExist(out string newSearch))
            {
                await searchSemaphore.WaitAsync();
                var searchQuery2 = SearchQuery.FromText(newSearch);
                searchTasks.Add(client.SearchAsync(searchQuery2, options: opts, cancellationToken: ct, responseHandler: rHandler));
            }
            if (!string.IsNullOrEmpty(noRegexSearch) && search.RemoveRegexIfExist(noRegexSearch, out string newSearch2))
            {
                await searchSemaphore.WaitAsync();
                var searchQuery2 = SearchQuery.FromText(newSearch2);
                searchTasks.Add(client.SearchAsync(searchQuery2, options: opts, cancellationToken: ct, responseHandler: rHandler));
            }

            await Task.WhenAll(searchTasks);
        }
        catch (OperationCanceledException) { }
    }


    static Track InferTrack(string filename, Track defaultTrack)
    {
        Track t = new Track(defaultTrack);
        filename = GetFileNameWithoutExtSlsk(filename).Replace(" — ", " - ").Replace("_", " ").RemoveConsecutiveWs().Trim();

        var trackNumStart = new Regex(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)");
        var trackNumMiddle = new Regex(@"(?<=- )((\d-)?\d{2,3}|\d{2,3}\.?)\s+");

        if (trackNumStart.IsMatch(filename))
            filename = trackNumStart.Replace(filename, "", 1).Trim();
        else
            filename = trackNumMiddle.Replace(filename, "", 1).Trim();

        if (filename.StartsWith("- ")) 
            filename = filename.Substring(2).Trim();

        string aname = t.ArtistName.Trim();
        string tname = t.TrackTitle.Trim();
        string alname = t.Album.Trim();
        string fname = filename;

        fname = fname.Replace("—", "-").Replace("_", " ").Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveConsecutiveWs().Trim();
        tname = tname.Replace("—", "-").Replace("_", " ").Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        aname = aname.Replace("—", "-").Replace("_", " ").Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        alname = alname.Replace("—", "-").Replace("_", " ").Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();

        bool maybeRemix = aname != "" && Regex.IsMatch(fname, @$"\({Regex.Escape(aname)} .+\)", RegexOptions.IgnoreCase);
        string[] parts = fname.Split(new string[] { " - " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] realParts = filename.Split(new string[] { " - " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != realParts.Length)
            realParts = parts;

        if (parts.Length == 1)
        {
            if (maybeRemix)
                t.ArtistMaybeWrong = true;
            t.TrackTitle = parts[0];
        }
        else if (parts.Length == 2)
        {
            t.ArtistName = realParts[0];
            t.TrackTitle = realParts[1];

            if (!parts[0].ContainsIgnoreCase(aname) || !parts[1].ContainsIgnoreCase(tname))
            {
                t.ArtistMaybeWrong = true;
                //if (!maybeRemix && parts[0].ContainsIgnoreCase(tname) && parts[1].ContainsIgnoreCase(aname))
                //{
                //    t.ArtistName = realParts[1];
                //    t.TrackTitle = realParts[0];
                //}
            }
            
        }
        else if (parts.Length == 3)
        {
            bool hasTitle = tname != "" && parts[2].ContainsIgnoreCase(tname);
            if (hasTitle)
                t.TrackTitle = realParts[2];

            int artistPos = -1;
            if (aname != "")
            {
                if (parts[0].ContainsIgnoreCase(aname))
                    artistPos = 0;
                else if (parts[1].ContainsIgnoreCase(aname))
                    artistPos = 1;
                else
                    t.ArtistMaybeWrong = true;
            }
            int albumPos = -1;
            if (alname != "")
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
                t.ArtistName = realParts[0] + " - " + realParts[1];
            }
            else if (artistPos >= 0)
            {
                t.ArtistName = parts[artistPos];
            }

            t.TrackTitle = parts[2];
        }

        if (t.TrackTitle == "")
        {
            t.TrackTitle = fname;
            t.ArtistMaybeWrong = true;
        }

        t.TrackTitle = t.TrackTitle.RemoveFt();
        t.ArtistName = t.ArtistName.RemoveFt();

        return t;
    }


    static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, Track track, ProgressBar progress, CancellationTokenSource? searchCts=null)
    {
        if (debugDisableDownload)
            throw new Exception();

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        string origPath = filePath;
        filePath = filePath + ".incomplete";

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

        try
        {
            using (var cts = new CancellationTokenSource())
            using (var outputStream = new FileStream(filePath, FileMode.Create))
            {
                lock (downloads)
                    downloads.TryAdd(file.Filename, new DownloadWrapper(origPath, response, file, track, cts, progress));
                await WaitForInternetConnection();
                await client.DownloadAsync(response.Username, file.Filename, () => Task.FromResult((Stream)outputStream), file.Size, options: transferOptions, cancellationToken: cts.Token);
            }
        }
        catch
        {
            if (System.IO.File.Exists(filePath))
                try { System.IO.File.Delete(filePath); } catch { }
            downloads[file.Filename].UpdateText();
            downloads.TryRemove(file.Filename, out _);
            throw;
        }

        try { searchCts?.Cancel(); }
        catch { }
        try { System.IO.File.Move(filePath, origPath, true); }
        catch (IOException) { WriteLine($"Failed to rename .incomplete file", ConsoleColor.DarkYellow, true); }
        downloads[file.Filename].success = true;
        downloads[file.Filename].UpdateText();
        downloads.TryRemove(file.Filename, out _);
    }

    static async Task Update()
    {
        if (slowConsoleOutput)
            updateDelay = slowUpdateDelay;

        while (true)
        {
            lastUpdate = DateTime.Now;

            if (!skipUpdate)
            {
                try
                {
                    if (client.State.HasFlag(SoulseekClientStates.LoggedIn))
                    {
                        foreach (var (key, val) in searches)
                            if (val == null) searches.TryRemove(key, out _);

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
                    else if (!client.State.HasFlag(SoulseekClientStates.LoggedIn | SoulseekClientStates.LoggingIn | SoulseekClientStates.Connecting))
                    {
                        WriteLine($"\nDisconnected, logging in\n", ConsoleColor.DarkYellow, true);
                        try { await Login(useRandomLogin); }
                        catch (Exception ex)
                        {
                            string banMsg = useRandomLogin ? "" : " (likely a 30-minute ban caused by frequent searches)";
                            WriteLine($"{ex.Message}{banMsg}", ConsoleColor.DarkYellow, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLine($"\n{ex.Message}\n", ConsoleColor.DarkYellow, true);
                }
            }

            await Task.Delay(updateDelay);
        }
    }


    static async Task<string> YtdlpSearchAndDownload(Track track, ProgressBar progress)
    {
        //if (track.URI != "")
        //{
        //    string videoTitle = (await YouTube.GetVideoInfo(track.URI)).title;
        //    string saveFilePathNoExt = GetSavePathNoExt(videoTitle, track);
        //    await YtdlpDownload(track.URI, saveFilePathNoExt, progress);
        //    return saveFilePathNoExt;
        //}

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
            this.displayText = DisplayString(track, file, response);

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
            RefreshOrPrint(progress, (int)((percentage ?? 0) * 100), $"{txt} {displayText}", needSimplePrintUpdate, needSimplePrintUpdate);

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
        public string[] BannedUsers = { };
        public string StrictStringRegexRemove = "";
        public bool StrictStringDiacrRemove = true;
        public bool AcceptNoLength = false;

        public FileConditions() { }

        public FileConditions(FileConditions other)
        {
            Array.Resize(ref Formats, other.Formats.Length);
            Array.Copy(other.Formats, Formats, other.Formats.Length);
            LengthTolerance = other.LengthTolerance;
            MinBitrate = other.MinBitrate;
            MaxBitrate = other.MaxBitrate;
            MaxSampleRate = other.MaxSampleRate;
            DangerWords = other.DangerWords.ToArray();
            BannedUsers = other.BannedUsers.ToArray();
            AcceptNoLength = other.AcceptNoLength;
            StrictArtist = other.StrictArtist;
            StrictTitle = other.StrictTitle;
        }

        public bool FileSatisfies(Soulseek.File file, Track track, SearchResponse? response)
        {
            return DangerWordSatisfies(file.Filename, track.TrackTitle, track.ArtistName) && FormatSatisfies(file.Filename) 
                && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file) 
                && StrictTitleSatisfies(file.Filename, track.TrackTitle) && StrictArtistSatisfies(file.Filename, track.ArtistName) 
                && BannedUsersSatisfies(response);
        }

        public bool FileSatisfies(TagLib.File file, Track track)
        {
            return DangerWordSatisfies(file.Name, track.TrackTitle, track.ArtistName) && FormatSatisfies(file.Name) 
                && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file) 
                && StrictTitleSatisfies(file.Name, track.TrackTitle) && StrictArtistSatisfies(file.Name, track.ArtistName);
        }

        public bool DangerWordSatisfies(string fname, string tname, string aname)
        {
            if (tname == "")
                return true;

            fname = GetFileNameWithoutExtSlsk(fname).Replace(" — ", " - ");
            tname = tname.Replace(" — ", " - ");

            foreach (var word in DangerWords)
            {
                if (fname.ContainsIgnoreCase(word) ^ tname.ContainsIgnoreCase(word))
                {
                    if (!(fname.Contains(" - ") && fname.ContainsIgnoreCase(word) && aname.ContainsIgnoreCase(word)))
                    {
                        if (word == "mix")
                            return fname.ContainsIgnoreCase("original mix") || tname.ContainsIgnoreCase("original mix");
                        else
                            return false;
                    }
                }
            }

            return true;
        }

        public bool StrictTitleSatisfies(string fname, string tname, bool noPath = true)
        {
            if (!StrictTitle || tname == "")
                return true;

            fname = noPath ? GetFileNameWithoutExtSlsk(fname) : fname;
            return StrictString(fname, tname, StrictStringRegexRemove, StrictStringDiacrRemove);
        }

        public bool StrictArtistSatisfies(string fname, string aname)
        {
            if (!StrictArtist || aname == "")
                return true;

            return StrictString(fname, aname, StrictStringRegexRemove, StrictStringDiacrRemove);
        }

        public static bool StrictString(string fname, string tname, string regexRemove = "", bool diacrRemove = true, bool ignoreCase = false)
        {
            if (string.IsNullOrEmpty(tname))
                return true;

            fname = fname.Replace("_", " ").ReplaceInvalidChars(" ", true, false);
            fname = regexRemove != "" ? Regex.Replace(fname, regexRemove, "") : fname;
            fname = diacrRemove ? fname.RemoveDiacritics() : fname;
            fname = fname.Trim();
            tname = tname.Replace("_", " ").ReplaceInvalidChars(" ", true, false);
            tname = regexRemove != "" ? Regex.Replace(tname, regexRemove, "") : tname;
            tname = diacrRemove ? tname.RemoveDiacritics() : tname;
            tname = tname.Trim();

            return fname.ContainsWithBoundary(tname, ignoreCase);
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
            if (file.Length == null)
                return AcceptNoLength;
            return Math.Abs((int)file.Length - actualLength) <= LengthTolerance;
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

        public bool BannedUsersSatisfies(SearchResponse? response)
        {
            return response == null || !BannedUsers.Any(x => x == response.Username);
        }

        public string GetNotSatisfiedName(Soulseek.File file, Track track, SearchResponse? response)
        {
            if (!DangerWordSatisfies(file.Filename, track.TrackTitle, track.ArtistName))
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
            if (!BannedUsersSatisfies(response))
                return "BannedUsers fails";
            return "Satisfied";                               
        }

        public string GetNotSatisfiedName(TagLib.File file, Track track)
        {
            if (!DangerWordSatisfies(file.Name, track.TrackTitle, track.ArtistName))
                return "DangerWord fails";
            if (!FormatSatisfies(file.Name))
                return "Format fails";
            if (!LengthToleranceSatisfies(file, track.Length))
                return "Length fails";
            if (!BitrateSatisfies(file))
                return "Bitrate fails";
            if (!SampleRateSatisfies(file))
                return "SampleRate fails";
            if (!StrictTitleSatisfies(file.Name, track.TrackTitle))
                return "StrictTitle fails";
            if (!StrictArtistSatisfies(file.Name, track.ArtistName))
                return "StrictArtist fails";
            return "Satisfied";
        }
    }


    static async Task<List<Track>> ParseCsvIntoTrackInfo(string path, string? artistCol = "", string? trackCol = "", 
        string? lengthCol = "", string? albumCol = "", string? descCol = "", string? ytIdCol = "", string timeUnit = "s", bool ytParse = false)
    {
        var tracks = new List<Track>();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            string[] possibleDelimiters = new[] { ",", ";", "\t", "|" };
            string firstLine = reader.ReadLine();
            string d = possibleDelimiters.OrderByDescending(delimiter => firstLine.Split(new[] { delimiter }, StringSplitOptions.None).Length).First();

            var header = firstLine.Split(new[] { d }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string?[] cols = { artistCol, albumCol, trackCol, lengthCol, descCol, ytIdCol };
            string[][] aliases = {
                new[] { "artist", "artist name", "artists", "artist names" },
                new[] { "album", "album name", "album title" },
                new[] { "title", "song", "track title", "track name", "song name", "track" },
                new[] { "length", "duration", "track length", "track duration", "song length", "song duration" },
                new[] { "description", "youtube description" },
                new[] { "id", "youtube id", "url" }
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

            if (cols[2] == "")
                throw new Exception($"No track name column found");
            if (cols[0] == "")
                WriteLine($"Warning: No artist column found, results may be imprecise", ConsoleColor.DarkYellow);
            if (cols[3] == "")
                WriteLine($"Warning: No duration column found, results may be imprecise", ConsoleColor.DarkYellow);

            var artistIndex = string.IsNullOrEmpty(cols[0]) ? -1 : Array.IndexOf(header, cols[0]);
            var albumIndex = string.IsNullOrEmpty(cols[1]) ? -1 : Array.IndexOf(header, cols[1]);
            var trackIndex = string.IsNullOrEmpty(cols[2]) ? -1 : Array.IndexOf(header, cols[2]);
            var lengthIndex = string.IsNullOrEmpty(cols[3]) ? -1 : Array.IndexOf(header, cols[3]);
            var descIndex = string.IsNullOrEmpty(cols[4]) ? -1 : Array.IndexOf(header, cols[4]);
            var ytIdIndex = string.IsNullOrEmpty(cols[5]) ? -1 : Array.IndexOf(header, cols[5]);

            var regex = new Regex($"{Regex.Escape(d)}(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)");

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                var values = regex.Split(line);
                if (!values.Any(t => !string.IsNullOrEmpty(t.Trim())))
                    continue;

                var desc = "";

                var track = new Track();
                if (artistIndex >= 0) track.ArtistName = values[artistIndex].Trim('"');
                if (trackIndex >= 0) track.TrackTitle = values[trackIndex].Trim('"');
                if (albumIndex >= 0) track.Album = values[albumIndex].Trim('"');
                if (descIndex >= 0) desc = values[descIndex].Trim('"');
                if (ytIdIndex >= 0) track.URI = values[ytIdIndex].Trim('"');
                if (lengthIndex >= 0) {
                    try {
                        track.Length = (int)ParseTrackLength(values[lengthIndex].Trim('"'), timeUnit);
                    }
                    catch {
                        WriteLine($"Couldn't parse track length \"{values[lengthIndex]}\" with format \"{timeUnit}\" for \"{track}\"", ConsoleColor.DarkYellow);
                    }
                }

                if (ytParse)
                    track = await YouTube.ParseTrackInfo(track.TrackTitle, track.ArtistName, track.URI, track.Length, true, desc);

                if (track.TrackTitle != "") tracks.Add(track);
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
        return ReplaceInvalidChars(name, " ");
    }

    static string GetAsPathSlsk(string fname)
    {
        return fname.Replace('\\', Path.DirectorySeparatorChar);
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

            if (format.Contains("{artist}") && string.IsNullOrEmpty(file.Tag.FirstPerformer))
                return filepath;
            if (format.Contains("{artists}") && (file.Tag.Performers == null || file.Tag.Performers.Length == 0))
                return filepath;
            if (format.Contains("{title}") && string.IsNullOrEmpty(file.Tag.Title))
                return filepath;

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
                string newFilePath = Path.Combine(directory, ReplaceInvalidChars(newName, " ") + extension);
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
        string searchName = track.TrackTitle.Replace(ignore, "").ToLower();
        searchName = searchName.ReplaceInvalidChars("").RemoveFt().RemoveSquareBrackets();
        searchName = searchName == "" ? track.TrackTitle : searchName;

        string searchName2 = ""; 
        if (searchName.Length <= 3) {
            searchName2 = track.ArtistName.Replace(ignore, "").ToLower();
            searchName2 = searchName2.ReplaceInvalidChars("").RemoveFt().RemoveSquareBrackets();
            searchName2 = searchName2 == "" ? track.ArtistName : searchName2;
        }

        filename = Path.GetFileNameWithoutExtension(filename);
        filename = filename.ReplaceInvalidChars("");
        filename = filename.Replace(ignore, "").ToLower();

        if (filename.Contains(searchName) && filename.Contains(searchName2))
        {
            return true;
        }
        else if ((track.ArtistMaybeWrong || track.ArtistName == "") && track.TrackTitle.Contains(" - "))
        {
            searchName = track.TrackTitle.Substring(track.TrackTitle.IndexOf(" - ") + 3).Replace(ignore, "").ToLower();
            searchName = searchName.ReplaceInvalidChars("").RemoveFt().RemoveSquareBrackets();
            if (searchName != "")
            {
                if (filename.Contains(searchName))
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
        string artist = track.ArtistName.ToLower().Replace(" ", "").RemoveFt();
        string title = track.TrackTitle.ToLower().Replace(" ", "").RemoveFt().RemoveSquareBrackets();

        foreach (var f in collection)
        {
            foundPath = f.Name;

            if (precise && !conditions.FileSatisfies(f, track))
                continue;
            if (string.IsNullOrEmpty(f.Tag.Title) || string.IsNullOrEmpty(f.Tag.FirstPerformer))
            {
                if (TrackMatchesFilename(track, f.Name))
                    return true;
                continue;
            }

            string fileArtist = f.Tag.FirstPerformer.ToLower().Replace(" ", "").RemoveFt();
            string fileTitle = f.Tag.Title.ToLower().Replace(" ", "").RemoveFt().RemoveSquareBrackets();

            bool durCheck = conditions.LengthToleranceSatisfies(f, track.Length);
            bool check1 = (artist.Contains(fileArtist) || (track.ArtistMaybeWrong && title.Contains(fileArtist)));
            bool check2 = !precise && fileTitle.Length >= 6 && durCheck;

            if ((check1 || check2) && (precise || conditions.DangerWordSatisfies(fileTitle, title, artist)))
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
                if (exists) existing.TryAdd(x, path);
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
                if (exists) existing.TryAdd(x, path);
                return exists;
            });
        }

        return existing;
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
                            track.Length = (int)ParseTrackLength(val, "s");
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

        if (track.TrackTitle == "" && track.Album == "" && track.ArtistName == "")
            throw new ArgumentException("Track string must contain title, album or artist.");

        return track;
    }

    static double ParseTrackLength(string duration, string format)
    {
        if (string.IsNullOrEmpty(format))
            throw new ArgumentException("Duration format string empty");
        duration = Regex.Replace(duration, "[a-zA-Z]", "");
        var formatParts = Regex.Split(format, @"\W+");
        var durationParts = Regex.Split(duration, @"\W+").Where(s => !string.IsNullOrEmpty(s)).ToArray();

        double totalSeconds = 0;

        for (int i = 0; i < formatParts.Length; i++)
        {
            switch (formatParts[i])
            {
                case "h":
                    totalSeconds += double.Parse(durationParts[i]) * 3600;
                    break;
                case "m":
                    totalSeconds += double.Parse(durationParts[i]) * 60;
                    break;
                case "s":
                    totalSeconds += double.Parse(durationParts[i]);
                    break;
                case "ms":
                    totalSeconds += double.Parse(durationParts[i]) / Math.Pow(10, durationParts[i].Length);
                    break;
            }
        }

        return totalSeconds;
    }

    static string ReplaceInvalidChars(this string str, string replaceStr, bool windows = false, bool removeSlash = true)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (windows)
            invalidChars = new char[] { ':', '|', '?', '>', '<', '*', '"', '/', '\\' };
        if (!removeSlash)
            invalidChars = invalidChars.Where(c => c != '/' && c != '\\').ToArray();
        foreach (char c in invalidChars)
            str = str.Replace(c.ToString(), replaceStr);
        return str.Replace("\\", replaceStr).Replace("/", replaceStr);
    }

    static string DisplayString(Track t, Soulseek.File? file=null, SearchResponse? response=null, FileConditions? nec=null, 
        FileConditions? pref=null, bool fullpath=false)
    {
        if (file == null)
            return t.ToString();

        string sampleRate = file.SampleRate.HasValue ? $"/{file.SampleRate}Hz" : "";
        string bitRate = file.BitRate.HasValue ? $"/{file.BitRate}kbps" : "";
        string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string fname = fullpath ? "\\" + file.Filename : "\\..\\" + GetFileNameSlsk(file.Filename);
        string length = (file.Length ?? -1).ToString(); 
        string displayText = $"{response?.Username ?? ""}{fname} [{length}s{sampleRate}{bitRate}/{fileSize}]";

        string necStr = nec != null ? $"nec:{nec.GetNotSatisfiedName(file, t, response)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, t, response)}" : "";
        string cond = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }

    static void PrintTracks(List<Track> tracks, int number = int.MaxValue, bool fullInfo=false)
    {
        number = Math.Min(tracks.Count, number);

        if (!fullInfo) {
            for (int i = 0; i < number; i++)
                Console.WriteLine($"  {tracks[i]}");
        }
        else {
            for (int i = 0; i < number; i++) {
                Console.WriteLine($"  Title:              {tracks[i].TrackTitle}");
                Console.WriteLine($"  Artist:             {tracks[i].ArtistName}");
                Console.WriteLine($"  Length:             {tracks[i].Length}s");
                if (!string.IsNullOrEmpty(tracks[i].Album))
                    Console.WriteLine($"  Album:              {tracks[i].Album}");
                if (!string.IsNullOrEmpty(tracks[i].URI))
                    Console.WriteLine($"  URL/ID:             {tracks[i].URI}");
                if (tracks[i].ArtistMaybeWrong)
                    Console.WriteLine($"  Artist maybe wrong: {tracks[i].ArtistMaybeWrong}");    
                if (tracks[i].Downloads != null) {
                    Console.WriteLine($"  Shares:             {tracks[i].Downloads.Count}");
                    foreach (var x in tracks[i].Downloads) {
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1));
                    }
                    if (tracks[i].Downloads?.Count > 0) Console.WriteLine();
                }
                Console.WriteLine();
            }
        }

        if (number < tracks.Count)
            Console.WriteLine($"  ... (etc)");
    }

    static void RefreshOrPrint(ProgressBar? progress, int current, string item, bool print = false, bool refreshIfOffscreen = false)
    {
        if (progress != null && !Console.IsOutputRedirected && (refreshIfOffscreen || progress.Y >= Console.WindowTop))
        {
            try { progress.Refresh(current, item); }
            catch { }
        }
        else if ((displayStyle == "simple" || Console.IsOutputRedirected) && print)
            Console.WriteLine(item);
    }

    public static void WriteLine(string value, ConsoleColor color, bool safe = false)
    {
        if (!safe)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ResetColor();
        }
        else
        {
            skipUpdate = true;
            lock (consoleLock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(value);
                Console.ResetColor();
            }
            skipUpdate = false;
        }
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

    public static async Task WaitForNetworkAndLogin()
    {
        await WaitForInternetConnection();

        while (true)
        {
            if (client.State.HasFlag(SoulseekClientStates.LoggedIn))
                break;
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
    public ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>? Downloads = null;

    public Track() { }

    public Track(Track other)
    {
        TrackTitle = other.TrackTitle;
        ArtistName = other.ArtistName;
        Album = other.Album;
        Length = other.Length;
        URI = other.URI;
        ArtistMaybeWrong = other.ArtistMaybeWrong;
        Downloads = other.Downloads;
    }

    public override string ToString()
    {
        var length = Length > 0 ? $" ({Length}s)" : "";
        if (!string.IsNullOrEmpty(ArtistName))
            return $"{ArtistName} - {TrackTitle}{length}";
        else
            return $"{TrackTitle}{length}";
    }
}


class TrackStringComparer : IEqualityComparer<Track>
{
    private bool _ignoreCase = false;
    public TrackStringComparer(bool ignoreCase = false) {
        _ignoreCase = ignoreCase;
    }

    public bool Equals(Track a, Track b)
    {
        if (a.Equals(b))
            return true;

        var comparer = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return string.Equals(a.TrackTitle, b.TrackTitle, comparer)
            && string.Equals(a.ArtistName, b.ArtistName, comparer)
            && string.Equals(a.Album, b.Album, comparer);
    }

    public int GetHashCode(Track a)
    {
        unchecked
        {
            int hash = 17;
            string trackTitle = _ignoreCase ? a.TrackTitle.ToLower() : a.TrackTitle;
            string artistName = _ignoreCase ? a.ArtistName.ToLower() : a.ArtistName;
            string album = _ignoreCase ? a.Album.ToLower() : a.Album;

            hash = hash * 23 + trackTitle.GetHashCode();
            hash = hash * 23 + artistName.GetHashCode();
            hash = hash * 23 + album.GetHashCode();

            return hash;
        }
    }
}


public class M3UEditor
{
    public readonly List<Track> tracks;
    public string path;
    public string outputFolder;
    public int offset = 0;

    public M3UEditor(string m3uPath, string outputFolder, List<Track> tracks, int offset = 0)
    {
        this.tracks = new List<Track>(tracks);
        this.outputFolder = Path.GetFullPath(outputFolder);
        this.offset = offset;
        path = Path.GetFullPath(m3uPath);
    }

    public void WriteAtIndex(string text, int idx, bool overwrite=true)
    {
        lock (tracks)
        {
            var lines = new List<string>();

            using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(file))
            {
                while (!reader.EndOfStream)
                    lines.Add(reader.ReadLine());
            }

            while (idx + offset >= lines.Count)
                lines.Add("");

            if (overwrite || string.IsNullOrWhiteSpace(lines[idx + offset]))
            {
                lines[idx + offset] = text;
                using (var file = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(file))
                {
                    foreach (string line in lines)
                        writer.WriteLine(line);
                }
            }
        }
    }

    public void WriteSuccess(string filename, Track track, bool overwrite=true)
    {
        filename = Path.GetRelativePath(Path.GetDirectoryName(path), filename);
        int idx = tracks.IndexOf(track);
        if (idx != -1)
            WriteAtIndex(filename, idx, overwrite);
        else
            throw new ArgumentException("Track not found");
    }

    public void WriteFail(string reason, Track track, bool overwrite=true)
    {
        int idx = tracks.IndexOf(track);
        if (idx != -1)
            WriteAtIndex($"# Failed: {track} [{reason}]", idx, overwrite);
        else
            throw new ArgumentException("Track not found");
    }

    public string ReadAllText()
    {
        using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var streamReader = new StreamReader(fileStream))
            return streamReader.ReadToEnd();
    }

    public string[] ReadAllLines()
    {
        return ReadAllText().Split('\n');
    }
}


class RateLimitedSemaphore
{
    private readonly int maxCount;
    private readonly TimeSpan resetTimeSpan;
    private readonly SemaphoreSlim semaphore;
    private long nextResetTimeTicks;
    private readonly object resetTimeLock = new object();

    public RateLimitedSemaphore(int maxCount, TimeSpan resetTimeSpan)
    {
        this.maxCount = maxCount;
        this.resetTimeSpan = resetTimeSpan;
        this.semaphore = new SemaphoreSlim(maxCount, maxCount);
        this.nextResetTimeTicks = (DateTimeOffset.UtcNow + this.resetTimeSpan).UtcTicks;
    }

    private void TryResetSemaphore()
    {
        if (!(DateTimeOffset.UtcNow.UtcTicks > Interlocked.Read(ref this.nextResetTimeTicks)))
            return;

        lock (this.resetTimeLock)
        {
            var currentTime = DateTimeOffset.UtcNow;
            if (currentTime.UtcTicks > Interlocked.Read(ref this.nextResetTimeTicks))
            {
                this.semaphore.Release(this.maxCount - this.semaphore.CurrentCount);
                var newResetTimeTicks = (currentTime + this.resetTimeSpan).UtcTicks;
                Interlocked.Exchange(ref this.nextResetTimeTicks, newResetTimeTicks);
            }
        }
    }

    public async Task WaitAsync()
    {
        TryResetSemaphore();
        var semaphoreTask = this.semaphore.WaitAsync();

        while (!semaphoreTask.IsCompleted)
        {
            var ticks = Interlocked.Read(ref this.nextResetTimeTicks);
            var nextResetTime = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
            var delayTime = nextResetTime - DateTimeOffset.UtcNow;
            var delayTask = delayTime >= TimeSpan.Zero ? Task.Delay(delayTime) : Task.CompletedTask;

            await Task.WhenAny(semaphoreTask, delayTask);
            TryResetSemaphore();
        }
    }
}


public static class Utils
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

    public static string RemoveFt(this string str, bool removeParentheses=true, bool onlyIfNonempty=true)
    {
        string[] ftStrings = { "feat.", "ft." };
        string orig = str;
        foreach (string ftStr in ftStrings)
        {
            int ftIndex = str.IndexOf(ftStr, StringComparison.OrdinalIgnoreCase);

            if (ftIndex != -1)
            {
                if (removeParentheses)
                {
                    int openingParenthesesIndex = str.LastIndexOf('(', ftIndex);
                    int closingParenthesesIndex = str.IndexOf(')', ftIndex);
                    int openingBracketIndex = str.LastIndexOf('[', ftIndex);
                    int closingBracketIndex = str.IndexOf(']', ftIndex);

                    if (openingParenthesesIndex != -1 && closingParenthesesIndex != -1)
                        str = str.Remove(openingParenthesesIndex, closingParenthesesIndex - openingParenthesesIndex + 1);
                    else if (openingBracketIndex != -1 && closingBracketIndex != -1)
                        str = str.Remove(openingBracketIndex, closingBracketIndex - openingBracketIndex + 1);
                    else
                        str = str.Substring(0, ftIndex);
                }
                else
                    str = str.Substring(0, ftIndex);
            }
        }
        if (onlyIfNonempty)
            str = str.TrimEnd() == "" ? orig : str;
        return str.TrimEnd();
    }

    public static string RemoveConsecutiveWs(this string input)
    {
        return Regex.Replace(input, @"\s+", " ");
    }

    public static string RemoveSquareBrackets(this string str)
    {
        return Regex.Replace(str, @"\[[^\]]*\]", "").Trim();
    }

    public static bool RemoveDiacriticsIfExist(this string s, out string res)
    {
        res = s.RemoveDiacritics();
        return res != s;
    }

    public static bool ContainsIgnoreCase(this string s, string other)
    {
        return s.Contains(other, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ContainsWithBoundary(this string str, string value, bool ignoreCase = false)
    {
        string boundaryChars = @"\s|-|\.|\\|\/|^|$|_|—|\(|\)|\[|\]|,";
        string pattern = $"(?<={boundaryChars}){Regex.Escape(value)}(?={boundaryChars})";
        RegexOptions options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.IsMatch(str, pattern, options);
    }

    public static bool ContainsInBrackets(this string str, string searchTerm, bool ignoreCase=false)
    {
        var regex = new Regex(@"\[(.*?)\]|\((.*?)\)");
        var matches = regex.Matches(str);
        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (Match match in matches)
        {
            if (match.Value.Contains(searchTerm, comp))
                return true;
        }

        return false;
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

    public static Dictionary<K, V> ToSafeDictionary<T, K, V>(this IEnumerable<T> source, Func<T, K> keySelector, Func<T, V> valSelector)
    {
        var d = new Dictionary<K, V>();
        foreach (var element in source)
        {
            if (!d.ContainsKey(keySelector(element)))
                d.Add(keySelector(element), valSelector(element));
        }
        return d;
    }

    public static int Levenshtein(string source, string target)
    {
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;

        var distance = new int[source.Length + 1, target.Length + 1];

        for (var i = 0; i <= source.Length; i++)
            distance[i, 0] = i;

        for (var j = 0; j <= target.Length; j++)
            distance[0, j] = j;

        for (var i = 1; i <= source.Length; i++)
        {
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[source.Length, target.Length];
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
