using AngleSharp.Dom;
using Konsole;
using Soulseek;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using File = System.IO.File;
using Directory = System.IO.Directory;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;


static class Program
{
    static SoulseekClient? client = null;
    static ConcurrentDictionary<Track, SearchInfo> searches = new ConcurrentDictionary<Track, SearchInfo>();
    static ConcurrentDictionary<string, DownloadWrapper> downloads = new ConcurrentDictionary<string, DownloadWrapper>();
    static ConcurrentDictionary<string, char> pathsToBeFormatted = new ConcurrentDictionary<string, char>();
    static ConcurrentDictionary<string, char> downloadedFiles = new ConcurrentDictionary<string, char>();
    static ConcurrentDictionary<string, char> downloadedImages = new ConcurrentDictionary<string, char>();
    static ConcurrentBag<(Track, string)> failedDownloads = new ConcurrentBag<(Track, string)>();
    static List<Track> tracks = new List<Track>();
    static List<Track> trackAlbums = new List<Track>();
    static List<Track> trackAlbumsLargestImg = new List<Track>();
    static List<Track> trackAlbumsMostImg = new List<Track>();
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
    static string defaultFolderName = "";
    static string ytUrl = "";
    static string searchStr = "";
    static string spotifyUrl = "";
    static string spotifyId = "";
    static string spotifySecret = "";
    static string encodedSpotifyId = "MWJmNDY5MWJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI=";
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
    static bool album = false;
    static string albumArtOption = "";
    static bool interactiveMode = false;
    static bool albumIgnoreFails = false;
    static int albumTrackCount = -1;
    static char albumTrackCountIneq = '=';
    static string albumCommonPath = "";
    static string regexReplacePattern = "";
    static string regexPatternToReplace = "";
    static string noRegexSearch = "";
    static string timeUnit = "s";
    static string displayStyle = "single";
    static string input = "";
    static bool preciseSkip = true;
    static string nameFormat = "";
    static bool skipNotFound = false;
    static bool desperateSearch = false;
    static bool noRemoveSpecialChars = false;
    static bool artistMaybeWrong = false;
    static bool fastSearch = false;
    static bool ytParse = false;
    static bool removeFt = false;
    static bool removeBrackets = false;
    static bool reverse = false;
    static bool useYtdlp = false;
    static bool skipExisting = false;
    static string m3uOption = "fails";
    static bool useTagsCheckExisting = false;
    static bool removeTracksFromSource = false;
    static bool getDeleted = false;
    static bool removeSingleCharacterSearchTerms = false;
    static int maxTracks = int.MaxValue;
    static int minUsersAggregate = 2;
    static bool relax = false;
    static bool debugInfo = false;
    static int offset = 0;

    static string confPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "slsk-batchdl.conf");

    static string playlistUri = "";
    static Spotify? spotifyClient = null;
    static int downloadMaxStaleTime = 50000;
    static int updateDelay = 100;
    static int searchTimeout = 5000;
    static int maxConcurrentProcesses = 2;
    static int maxRetriesPerTrack = 30;
    static int listenPort = 50000;

    static object consoleLock = new object();

    static DateTime lastUpdate;
    static bool skipUpdate = false;
    static bool debugDisableDownload = false;
    static bool debugPrintTracks = false;
    static bool noModifyShareCount = false;
    static bool printResultsFull = false;
    static bool debugPrintTracksFull = false;
    static bool useRandomLogin = false;
    static bool noWaitForInternet = false;

    static int searchesPerTime = 34;
    static int searchResetTime = 220;
    static RateLimitedSemaphore? searchSemaphore;
    
    private static int successCount = 0;
    private static int failCount = 0;
    private static bool downloadingImages = false;
    private static int tracksRemaining;
    private static M3UEditor? m3uEditor;
    private static CancellationTokenSource? mainLoopCts;

    public enum FailureReasons
    {
        InvalidSearchString,
        OutOfDownloadRetries,
        NoSuitableFileFound,
        AllDownloadsFailed
    }

    static string inputType = "";

    static void PrintHelp()
    {
        // undocumented options:
        // --artist-col, --title-col, --album-col, --length-col, --yt-desc-col, --yt-id-col
        // --remove-brackets, --spotify, --csv, --string, --youtube, --random-login
        // --danger-words, --pref-danger-words, --no-modify-share-count, --no-wait-for-internet
        Console.WriteLine("Usage: slsk-batchdl <input> [OPTIONS]" +
                            "\n" +
                            "\n  <input>                        <input> is one of the following:" +
                            "\n" +
                            "\n                                 Spotify playlist url or 'spotify-likes': Download a spotify" +
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
                            "\n                                 list like 'title=Song Name,artist=Artist Name,length=215'" +
                            "\n                                 Allowed properties are: title, artist, album, length (sec)" +
                            "\n                                 Specify artist and album only to download an album." +
                            "\n" +
                            "\nOptions:" +
                            "\n  --user <username>              Soulseek username" +
                            "\n  --pass <password>              Soulseek password" +
                            "\n" +
                            "\n  -p --path <path>               Download folder" +
                            "\n  -f --folder <name>             Subfolder name. Set to '.' to output directly to the" +
                            "\n                                 download folder (default: playlist/csv name)" +
                            "\n  -n --number <maxtracks>        Download the first n tracks of a playlist" +
                            "\n  -o --offset <offset>           Skip a specified number of tracks" +
                            "\n  -r --reverse                   Download tracks in reverse order" +
                            "\n  --remove-from-playlist         Remove downloaded tracks from playlist (spotify only)" +
                            "\n  --name-format <format>         Name format for downloaded tracks, e.g \"{artist} - {title}\"" +
                            "\n  --fast-search                  Begin downloading as soon as a file satisfying the preferred" +
                            "\n                                 conditions is found. Increases chance to download bad files." +
                            "\n  --m3u <option>                 Create an m3u8 playlist file" +
                            "\n                                 'none': Do not create a playlist file" +
                            "\n                                 'fails' (default): Write only failed downloads to the m3u" +
                            "\n                                 'all': Write successes + fails as comments" +
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
                            "\n  -a --aggregate                 When input is a string: Instead of downloading a single" +
                            "\n                                 track matching the search string, find and download all" +
                            "\n                                 distinct songs associated with the provided artist or track" +
                            "\n                                 title. The input string must be a list of properties." +
                            "\n  --min-users-aggregate <num>    Minimum number of users sharing a track before it is" +
                            "\n                                 downloaded in aggregate mode. Setting it to higher values" +
                            "\n                                 will significantly reduce false positives, but may introduce" +
                            "\n                                 false negatives. Default: 2" +
                            "\n  --relax                        Slightly relax file filtering in aggregate mode to include" +
                            "\n                                 more results" +
                            "\n" +
                            "\n  --interactive                  When downloading albums: Allows to select the wanted album" +
                            "\n  --album-track-count <num>      Specify the exact number of tracks in the album. Folders" +
                            "\n                                 with a different number of tracks will be ignored. Append" +
                            "\n                                 a '+' or '-' to the number for the inequalities >= and <=." +
                            "\n  --album-ignore-fails           When downloading an album and one of the files fails, do not" +
                            "\n                                 skip to the next source and do not delete all successfully" +
                            "\n                                 downloaded files" +
                            "\n  --album-art <option>           When downloading albums, optionally retrieve album images" +
                            "\n                                 from another location:" +
                            "\n                                 'default': Download from the same folder as the music" +
                            "\n                                 'largest': Download from the folder with the largest image" +
                            "\n                                 'most': Download from the folder containing the most images" +
                            "\n" +
                            "\n  -s --skip-existing             Skip if a track matching file conditions is found in the" +
                            "\n                                 output folder or your music library (if provided)" +
                            "\n  --skip-mode <mode>             'name': Use only filenames to check if a track exists" +
                            "\n                                 'name-precise' (default): Use filenames and check conditions" +
                            "\n                                 'tag': Use file tags (slower)" +
                            "\n                                 'tag-precise': Use file tags and check file conditions" +
                            "\n  --music-dir <path>             Specify to skip downloading tracks found in a music library" +
                            "\n                                 Use with --skip-existing" +
                            "\n  --skip-not-found               Skip searching for tracks that weren't found on Soulseek" +
                            "\n                                 during the last run. Fails are read from the m3u file." +
                            "\n" +
                            "\n  --no-remove-special-chars      Do not remove special characters before searching" +
                            "\n  --remove-ft                    Remove 'feat.' and everything after before searching" +
                            "\n  --remove-brackets              Remove square brackets and their contents before searching" +
                            "\n  --regex <regex>                Remove a regexp from all track titles and artist names." +
                            "\n                                 Optionally specify the replacement regex after a semicolon" +
                            "\n  --artist-maybe-wrong           Performs an additional search without the artist name." +
                            "\n                                 Useful for sources like SoundCloud where the \"artist\"" +
                            "\n                                 could just be an uploader. Note that when downloading a" +
                            "\n                                 YouTube playlist via url, this option is set automatically" +
                            "\n                                 on a per track basis, so it is best kept off in that case." +
                            "\n  -d --desperate                 Tries harder to find the desired track by searching for the" +
                            "\n                                 artist/album/title only, then filtering the results." +
                            "\n  --yt-dlp                       Use yt-dlp to download tracks that weren't found on" +
                            "\n                                 Soulseek. yt-dlp must be available from the command line." +
                            "\n" +
                            "\n  --config <path>                Manually specify config file location" +
                            "\n  --search-timeout <ms>          Max search time in ms (default: 5000)" +
                            "\n  --max-stale-time <ms>          Max download time without progress in ms (default: 50000)" +
                            "\n  --concurrent-downloads <num>   Max concurrent downloads (default: 2)" +
                            "\n  --searches-per-time <num>      Max searches per time interval. Higher values may cause" +
                            "\n                                 30-minute bans. (default: 34)" +
                            "\n  --searches-renew-time <sec>    Controls how often available searches are replenished." +
                            "\n                                 Lower values may cause 30-minute bans. (default: 220)" +
                            "\n  --display <option>             Changes how searches and downloads are displayed:" +
                            "\n                                 'single' (default): Show transfer state and percentage" +
                            "\n                                 'double': Transfer state and a large progress bar " +
                            "\n                                 'simple': No download bars or changing percentages" +
                            "\n  --listen-port <port>           Port for incoming connections (default: 50000)" +
                            "\n" +
                            "\n  --print <option>               Print tracks or search results instead of downloading:" +
                            "\n                                 'tracks': Print all tracks to be downloaded" +
                            "\n                                 'tracks-full': Print extended information about all tracks" +
                            "\n                                 'results': Print search results satisfying file conditions" +
                            "\n                                 'results-full': Print search results including full paths" +
                            "\n  --debug                        Print extra debug info");
    }

    static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

#if WINDOWS
        try
        {
            if (Console.BufferHeight <= 50 && displayStyle != "simple")
                WriteLine("Windows: Recommended to use the command prompt instead of terminal app to avoid printing issues.");
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
        if (idx != -1)
        {
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
                    case "--regex":
                        string s = args[++i].Replace("\\;", "<<semicol>>");
                        var parts = s.Split(";", StringSplitOptions.RemoveEmptyEntries).ToArray();
                        regexPatternToReplace = parts[0];
                        if (parts.Length > 1)
                            regexReplacePattern = parts[1];
                        regexPatternToReplace.Replace("<<semicol>>", ";");
                        regexReplacePattern.Replace("<<semicol>>", ";");
                        break;
                    case "--no-regex-search":
                        noRegexSearch = args[++i];
                        break;
                    case "-r":
                    case "--reverse":
                        reverse = true;
                        break;
                    case "--m3u":
                        m3uOption = args[++i];
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
                    case "--searches-renew-time":
                        searchResetTime = int.Parse(args[++i]);
                        break;
                    case "--max-retries":
                        maxRetriesPerTrack = int.Parse(args[++i]);
                        break;
                    case "--album-track-count":
                        string a = args[++i];
                        if (a.Last() == '+' || a.Last() == '-')
                        {
                            albumTrackCountIneq = a.Last();
                            a = a.Substring(0, a.Length - 1);
                        }
                        albumTrackCount = int.Parse(a);
                        break;
                    case "--album-art":
                        switch (args[++i])
                        {
                            case "largest":
                            case "most":
                                albumArtOption = args[i];
                                break;
                            case "default":
                                albumArtOption = "";
                                break;
                            default:
                                throw new ArgumentException($"Invalid album art download mode \'{args[i]}\'");
                        }
                        break;
                    case "--album-ignore-fails":
                        albumIgnoreFails = true;
                        break;
                    case "--interactive":
                        interactiveMode = true;
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
                    case "--no-modify-share-count":
                        noModifyShareCount = true;
                        break;
                    case "--skip-existing-use-tags":
                        skipExisting = true;
                        useTagsCheckExisting = true;
                        break;
                    case "-d":
                    case "--desperate":
                        desperateSearch = true;
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
                                throw new ArgumentException($"Invalid skip mode \'{args[i]}\'");
                        }
                        break;
                    case "--no-remove-special-chars":
                        noRemoveSpecialChars = true;
                        break;
                    case "--artist-maybe-wrong":
                        artistMaybeWrong = true;
                        break;
                    case "--fast-search":
                        fastSearch = true;
                        break;
                    case "--debug":
                        debugInfo = true;
                        break;
                    case "--no-wait-for-internet":
                        noWaitForInternet = true;
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
                    throw new ArgumentException($"Invalid argument \'{args[i]}\'. Input is already set to \'{input}\'");
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

        if (inputType == "youtube" || (inputType == "" && input.StartsWith("http") && input.Contains("youtu")))
        {
            WriteLine("Youtube download", debugOnly: true);
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

            defaultFolderName = ReplaceInvalidChars(name, " ");

            YouTube.StopService();
        }
        else if (inputType == "spotify" || (inputType == "" && (input.StartsWith("http") && input.Contains("spotify")) || input == "spotify-likes"))
        {
            WriteLine("Spotify download", debugOnly: true);
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
            defaultFolderName = ReplaceInvalidChars(playlistName, " ");
        }
        else if (inputType == "csv" || (inputType == "" && Path.GetExtension(input).Equals(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            WriteLine("CSV download", debugOnly: true);
            csvPath = input;
            inputType = "csv";

            if (!System.IO.File.Exists(csvPath))
                throw new Exception("CSV file not found");

            Console.WriteLine("Parsing CSV track info");
            tracks = await ParseCsvIntoTrackInfo(csvPath, artistCol, trackCol, lengthCol, albumCol, descCol, ytIdCol, timeUnit, ytParse);
            tracks = tracks.Skip(off).Take(max).ToList();

            defaultFolderName = Path.GetFileNameWithoutExtension(csvPath);
        }
        else
        {
            WriteLine("String download", debugOnly: true);
            searchStr = input;
            inputType = "string";
            var music = ParseTrackArg(searchStr);
            removeSingleCharacterSearchTerms = false;

            if (!aggregate && music.TrackTitle != "")
                tracks.Add(music);
            else
            {
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

                if (music.TrackTitle == "" && music.Album != "")
                {
                    Console.WriteLine($"Searching for album: {string.Join(", ", x)}");
                    music.TrackIsAlbum = true;
                    album = true;
                    trackAlbums = await GetAlbum(music);
                    if (trackAlbums.Count == 0)
                    {
                        Console.WriteLine("No results");
                        return;
                    }
                    if (albumArtOption != "")
                    {
                        var trackAlbumsImg = trackAlbums.Select(t => {
                            t.Downloads = new SlDictionary(t.Downloads.Where(d => Utils.IsImageFile(d.Value.Item2.Filename))); return t;
                        }).Where(t => t.Downloads.Count > 0);
                        trackAlbums = trackAlbums.Select(t => {
                            t.Downloads = new SlDictionary(t.Downloads.Where(d => !Utils.IsImageFile(d.Value.Item2.Filename))); return t;
                        }).Where(t => t.Downloads.Count > 0).ToList();
                        trackAlbumsLargestImg = trackAlbumsImg
                            .OrderByDescending(t => t.Downloads.Select(d => d.Value.Item2.Size).Max())
                            .ThenByDescending(t => t.Downloads.Count()).ToList();
                        trackAlbumsMostImg = trackAlbumsImg
                            .OrderByDescending(t => t.Downloads.Count())
                            .ThenByDescending(t => t.Downloads.Sum(d => d.Value.Item2.Size)).ToList();
                    }
                    if (debugDisableDownload && !debugPrintTracks)
                        tracks = trackAlbums;
                    else
                    {
                        if (!interactiveMode)
                            tracks = PopTrackAlbums();
                        else
                            InteractiveModeAlbum();
                    }
                }
                else
                {
                    Console.WriteLine($"Searching for tracks associated with {string.Join(", ", x)}");
                    aggregate = true;
                    tracks = await GetUniqueRelatedTracks(music);
                }

                if (aggregate || album)
                    defaultFolderName = ReplaceInvalidChars(music.ToString(true), " ").Trim();
                else
                    defaultFolderName = ".";
            }
        }

        WriteLine("Got tracks", debugOnly: true);

        if (reverse)
        {
            tracks.Reverse();
            tracks = tracks.Skip(offset).Take(maxTracks).ToList();
        }

        for (int i = 0; i < tracks.Count; i++)
        {
            Track track = tracks[i];
            if (removeFt)
            {
                track.TrackTitle = track.TrackTitle.RemoveFt();
                track.ArtistName = track.ArtistName.RemoveFt();
            }
            if (removeBrackets)
            {
                track.TrackTitle = track.TrackTitle.RemoveSquareBrackets();
            }
            if (regexPatternToReplace != "")
            {
                track.TrackTitle = Regex.Replace(track.TrackTitle, regexPatternToReplace, regexReplacePattern);
                track.ArtistName = Regex.Replace(track.ArtistName, regexPatternToReplace, regexReplacePattern);
            }
            if (artistMaybeWrong)
            {
                track.ArtistMaybeWrong = true;
            }
            tracks[i] = track;
        }

        if (folderName == "")
            folderName = defaultFolderName;
        if (folderName == ".")
            folderName = "";
        folderName = folderName.Replace("\\", "/");
        folderName = String.Join('/', folderName.Split("/").Select(x => ReplaceInvalidChars(x, " ").Trim()));
        folderName = folderName.Replace('/', Path.DirectorySeparatorChar);

        outputFolder = Path.Combine(parentFolder, folderName);

        if (m3uFilePath != "")
            m3uFilePath = Path.Combine(m3uFilePath, (folderName == "" ? "playlist" : folderName) + ".m3u8");
        else
            m3uFilePath = Path.Combine(outputFolder, (folderName == "" ? "playlist" : folderName) + ".m3u8");

        var tracksStart = new List<Track>(tracks);
        m3uEditor = new M3UEditor(m3uFilePath, outputFolder, tracksStart, offset);
        int notFoundCount = 0;
        int existingCount = 0;

        if (skipExisting)
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

            existingCount = tracksStart.Count - tracks.Count;

            if (m3uOption=="all" && !debugDisableDownload && !debugPrintTracks)
            {
                foreach (var x in existing)
                    m3uEditor.WriteSuccess(x.Value, x.Key, false);
            }
        }

        if (skipNotFound && m3uEditor.HasFails())
        {
            for (int i = tracks.Count - 1; i >= 0; i--)
            {
                if (m3uEditor.HasFail(tracks[i], out string reason) && reason == nameof(FailureReasons.NoSuitableFileFound))
                    tracks.Remove(tracks[i]);
            }

            notFoundCount = tracksStart.Count - tracks.Count - existingCount;
        }

        tracksRemaining = tracks.Count;

        string notFoundLastTime = notFoundCount > 0 ? $"{notFoundCount} not found" : "";
        string alreadyExist = existingCount > 0 ? $"{existingCount} already exist" : "";
        notFoundLastTime = alreadyExist != "" && notFoundLastTime != "" ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist + notFoundLastTime != "" ? $" ({alreadyExist}{notFoundLastTime})" : "";

        if (debugPrintTracks)
        {
            Console.WriteLine($"\n{tracks.Count(x => !x.IsNotAudio)} tracks{skippedTracks}");
            Console.WriteLine($"\nTo be downloaded:");
            PrintTracks(tracks, fullInfo: debugPrintTracksFull);
            var skipped = tracksStart.Where(t => !tracks.Contains(t)).ToList();
            if (skipped.Count > 0)
            {
                if (debugPrintTracksFull)
                {
                    Console.WriteLine("\n#############################################\n");
                }
                Console.WriteLine($"\nSkipped:");
                PrintTracks(skipped, fullInfo: debugPrintTracksFull);
            }
            return;
        }
        else if (!(interactiveMode && album) && (tracks.Count > 1 || skippedTracks != ""))
        {
            PrintTracks(tracks, album ? int.MaxValue : 10);
            Console.WriteLine($"Downloading {tracks.Count(x => !x.IsNotAudio)} tracks{skippedTracks}\n");
        }

        if (!useRandomLogin && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)))
            throw new ArgumentException("No soulseek username or password");

        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
            await Login(useRandomLogin);

        var UpdateTask = Task.Run(() => Update());
        WriteLine("Update started", debugOnly: true);

        await MainLoop();
        WriteLine("Mainloop done", debugOnly: true);


        if (album && downloadedFiles.Count > 0)
        {
            foreach (var path in pathsToBeFormatted.Keys)
            {
                var newPath = ApplyNamingFormat(path, true);
                downloadedFiles.TryRemove(path, out _);
                downloadedFiles.TryAdd(newPath, char.MinValue);
            }
            pathsToBeFormatted.Clear();
        }

        if (tracks.Count > 1)
            Console.WriteLine($"\n\nDownloaded {successCount} of {successCount + failCount} files");
        if (failedDownloads.Count > 0 && !debugDisableDownload && !album && input != "string")
            Console.WriteLine($"\nFailed:\n{string.Join("\n", failedDownloads.Select(x => $"{x.Item1} [{x.Item2}]"))}");
    }


    static async Task MainLoop()
    {
        WriteLine("Main loop", debugOnly: true);
        while (true)
        {
            bool albumDlFailed = false;
            mainLoopCts = new CancellationTokenSource();
            SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);

            try
            {
                var downloadTasks = tracks.Select(async (track) =>
                {
                    await semaphore.WaitAsync(mainLoopCts.Token);
                    int tries = 2;
                retry:
                    await WaitForNetworkAndLogin();
                    mainLoopCts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var savedFilePath = await SearchAndDownload(track);
                        var curSet = downloadingImages ? downloadedImages : downloadedFiles;
                        curSet.TryAdd(savedFilePath, char.MinValue);
                        Interlocked.Increment(ref successCount);
                        if (removeTracksFromSource && !string.IsNullOrEmpty(spotifyUrl))
                            spotifyClient.RemoveTrackFromPlaylist(playlistUri, track.URI);
                        if (m3uOption == "all" && !debugDisableDownload && Utils.IsMusicFile(savedFilePath))
                            m3uEditor.WriteSuccess(savedFilePath, track);
                    }
                    catch (Exception ex)
                    {
                        if (ex is SearchAndDownloadException)
                        {
                            Interlocked.Increment(ref failCount);
                            if (m3uOption == "fails" || m3uOption == "all" && !debugDisableDownload && inputType != "string" && !album)
                                m3uEditor.WriteFail(ex.Message, track);
                            failedDownloads.Add((track, ex.Message));
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

                        if (album && !albumIgnoreFails)
                        {
                            mainLoopCts.Cancel();
                            lock (downloads)
                            {
                                foreach (var (key, dl) in downloads)
                                {
                                    dl.cts.Cancel();
                                    if (File.Exists(dl.savePath)) File.Delete(dl.savePath);
                                }
                            }
                            return;
                        }
                    }
                    finally { semaphore.Release(); }

                    if (successCount+failCount != 0 && (successCount + failCount) % 50 == 0)
                        WriteLine($"\nSuccesses: {successCount}, fails: {failCount}, tracks left: {tracksRemaining}\n", ConsoleColor.Yellow, true);

                    Interlocked.Decrement(ref tracksRemaining);
                });

                await Task.WhenAll(downloadTasks);
            }
            catch (OperationCanceledException)
            {
                if (album && !albumIgnoreFails)
                {
                    albumDlFailed = true;
                    var curSet = downloadingImages ? downloadedImages : downloadedFiles;

                    foreach (var path in curSet.Keys)
                        if (File.Exists(path)) File.Delete(path);
                    curSet.Clear();
                    pathsToBeFormatted.Clear();

                    var nextStr = trackAlbums.Count > 0 ? ", trying next download" : "";
                    WriteLine($"\n{(downloadingImages ? "Image" : "Album")} download failed{nextStr}");
                    if (trackAlbums.Count > 0)
                    {
                        if (!interactiveMode)
                            tracks = PopTrackAlbums();
                        else
                            InteractiveModeAlbum();
                        successCount = 0;
                        failCount = 0;
                        continue;
                    }
                }
            }
            if (album && !albumDlFailed && !downloadingImages && albumArtOption != "")
            {
                if (albumArtOption == "most")
                    trackAlbums = trackAlbumsMostImg;
                else if (albumArtOption == "largest")
                    trackAlbums = trackAlbumsLargestImg;
                else
                {
                    trackAlbums = trackAlbumsMostImg;
                }

                if (trackAlbums.Count > 0)
                {
                    downloadingImages = true;
                    WriteLine($"\nDownloading images");
                    if (!interactiveMode)
                        tracks = PopTrackAlbums();
                    else
                        InteractiveModeAlbum();
                    continue;
                }
            }

            break;
        }
    }


    static void InteractiveModeAlbum()
    {
        int aidx = 0;
        var interactiveModeLoop = () =>
        {
            string userInput = "";
            while (true)
            {
                var key = Console.ReadKey(false);
                if (key.Key == ConsoleKey.DownArrow)
                    return "n";
                else if (key.Key == ConsoleKey.UpArrow)
                    return "p";
                else if (key.Key == ConsoleKey.Escape)
                    return "c";
                else if (key.Key == ConsoleKey.Enter)
                    return userInput;
                else
                    userInput += key.KeyChar;
            }
        };
        Console.WriteLine($"\nPrev [Up/p] / Next [Down/n] / Accept [Enter] / Accept & Exit Interactive Mode [q] / Cancel [Esc/c]");
        while (true)
        {
            Console.WriteLine();
            tracks = SetTrackAlbums(aidx);
            var response = tracks[0].Downloads.First().Value.Item1;
            Console.WriteLine($"User: {response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)");
            PrintTracks(tracks, pathsOnly: true, showAncestors: true);
            Console.WriteLine();
            Console.WriteLine($"Folder {aidx + 1}/{trackAlbums.Count} [Up/Down/Enter/Esc]");
            string userInput = interactiveModeLoop();
            switch (userInput)
            {
                case "p":
                    aidx = (aidx + trackAlbums.Count - 1) % trackAlbums.Count;
                    break;
                case "n":
                    aidx = (aidx + 1) % trackAlbums.Count;
                    break;
                case "c":
                    tracks = new List<Track>();
                    return;
                case "q":
                    interactiveMode = false;
                    trackAlbums.RemoveAt(aidx);
                    return;
                case "":
                    return;
            }
        }
    }


    static List<Track> PopTrackAlbums()
    {
        var t = SetTrackAlbums(0);
        trackAlbums.RemoveAt(0);
        return t;
    }

    static List<Track> SetTrackAlbums(int index)
    {
        var t = trackAlbums[index].Downloads
            .Select(x => {
                var t = InferTrack(x.Value.Item2.Filename, trackAlbums[0]);
                t.Downloads = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
                t.Downloads.TryAdd(x.Key, x.Value);
                t.IsNotAudio = !Utils.IsMusicFile(x.Value.Item2.Filename);
                t.TrackIsAlbum = false;
                return t;
            })
            .OrderBy(t => t.IsNotAudio)
            .ThenBy(t => t.Downloads.First().Value.Item2.Filename)
            .ToList();
        albumCommonPath = Utils.GreatestCommonPath(t.SelectMany(x => x.Downloads.Select(y => y.Value.Item2.Filename)), dirsep: '\\');
        return t;
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
        WriteLine($"Login {user}", debugOnly: true);

        while (true)
        {
            try
            {
                await WaitForInternetConnection();
                WriteLine($"Connecting {user}", debugOnly: true);
                await client.ConnectAsync(user, pass);
                if (!noModifyShareCount) {
                    WriteLine($"Setting share count", debugOnly: true);
                    await client.SetSharedCountsAsync(10, 50);
                }
                break;
            }
            catch (Exception e) {
                WriteLine($"Exception while logging in: {e}", debugOnly: true);
                if (--tries == 0) throw;
            }
            WriteLine($"Retry login {user}", debugOnly: true);
        }

        WriteLine($"Logged in {user}", debugOnly: true);
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

        string searchText = $"{track.ArtistName} {track.TrackTitle}".Trim();
        var removeChars = new string[] { " ", "_", "-" };

        searches.TryAdd(track, new SearchInfo(results, progress));

        Action<SearchResponse> responseHandler = (SearchResponse r) =>
        {
            if (r.Files.Count() > 0)
            {
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));

                if (fastSearch)
                {
                    var f = r.Files.First();
                    if (r.HasFreeUploadSlot && r.UploadSpeed / 1000000 >= 1 && preferredCond.FileSatisfies(f, track, r))
                    {
                        lock (downloadingLocker)
                        {
                            if (!downloading)
                            {
                                downloading = true;
                                saveFilePath = GetSavePath(f.Filename, track);
                                downloadTask = DownloadFile(r, f, saveFilePath, track, progress, cts);
                                downloadTask.ContinueWith(task =>
                                {
                                    lock (downloadingLocker)
                                    {
                                        downloading = false;
                                        saveFilePath = "";
                                        results.TryRemove(r.Username + "\\" + f.Filename, out _);
                                        badUsers.Add(r.Username);
                                    }
                                }, TaskContinuationOptions.OnlyOnFaulted);
                            }
                        }
                    }
                }
            }
        };

        var getSearchOptions = (int timeout, FileConditions necCond, FileConditions prfCond) =>
        {
            return new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                searchTimeout: searchTimeout,
                removeSingleCharacterSearchTerms: removeSingleCharacterSearchTerms,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && necCond.BannedUsersSatisfies(response);
                },
                fileFilter: (file) =>
                {
                    return Utils.IsMusicFile(file.Filename) && (necCond.FileSatisfies(file, track, null) || printResultsFull);
                });
        };

        var onSearch = () => RefreshOrPrint(progress, 0, $"Searching: {track}", true);
        await RunSearches(track, results, getSearchOptions, responseHandler, cts.Token, onSearch);

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
            var ignoredResults = new ConcurrentDictionary<string, (SlResponse, SlFile)>();
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
                catch (Exception e)
                {
                    downloading = false;
                    if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                        throw;
                    newBadUsers.Add(x.response.Username);
                    if (--maxRetriesPerTrack <= 0)
                    {
                        RefreshOrPrint(progress, 0, $"Out of download retries: {track}", true);
                        WriteLine("Last error was: " + e.Message, ConsoleColor.DarkYellow, true);
                        throw new SearchAndDownloadException(nameof(FailureReasons.OutOfDownloadRetries));
                    }
                }
            }
        }

        if (!downloading && useYtdlp)
        {
            notFound = false;
            try {
                RefreshOrPrint(progress, 0, $"yt-dlp search: {track}", true);
                var ytResults = await YouTube.YtdlpSearch(track);

                if (ytResults.Count > 0)
                {
                    foreach (var res in ytResults)
                    {
                        if (necessaryCond.LengthToleranceSatisfies(track, res.length))
                        {
                            string saveFilePathNoExt = GetSavePathNoExt(res.title, track);
                            downloading = true;
                            RefreshOrPrint(progress, 0, $"yt-dlp download: {track}", true);
                            saveFilePath = await YouTube.YtdlpDownload(res.id, saveFilePathNoExt);
                            RefreshOrPrint(progress, 100, $"Succeded: yt-dlp completed download for {track}", true);
                            break;
                        }
                    }
                }
            }
            catch (Exception e) {
                saveFilePath = "";
                downloading = false;
                RefreshOrPrint(progress, 0, $"{e.Message}", true);
                throw new SearchAndDownloadException(nameof(FailureReasons.NoSuitableFileFound));
            }
        }

        if (!downloading)
        {
            if (notFound)
            {
                RefreshOrPrint(progress, 0, $"Not found: {track}", true);   
                throw new SearchAndDownloadException(nameof(FailureReasons.NoSuitableFileFound));
            }
            else
            {
                RefreshOrPrint(progress, 0, $"All downloads failed: {track}", true);
                throw new SearchAndDownloadException(nameof(FailureReasons.AllDownloadsFailed));
            }
        }

        if (nameFormat != "" && !useYtdlp)
            saveFilePath = ApplyNamingFormat(saveFilePath);

        return saveFilePath;
    }


    public class SearchAndDownloadException: Exception
    {
        public SearchAndDownloadException(string text = "") : base(text) { }
    }


    static async Task<List<Track>> GetAlbum(Track track)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        var getSearchOptions = (int timeout, FileConditions nec, FileConditions prf) => 
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: removeSingleCharacterSearchTerms,
                searchTimeout: timeout,
                responseFilter: (response) =>
                {
                    return response.UploadSpeed > 0 && nec.BannedUsersSatisfies(response);
                }
                //fileFilter: (file) => {
                //    return FileConditions.StrictString(GetDirectoryNameSlsk(file.Filename), track.ArtistName, ignoreCase: true)
                //        && FileConditions.StrictString(GetDirectoryNameSlsk(file.Filename), track.Album, ignoreCase: true);
                //}
            );
        Action<SearchResponse> handler = (r) => {
            if (r.Files.Count() > 0)
            {
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));
            }
        };
        var cts = new CancellationTokenSource();

        await RunSearches(track, results, getSearchOptions, handler, cts.Token);

        var fullPath = ((SearchResponse r, Soulseek.File f) x) => { return x.r.Username + "\\" + x.f.Filename; };

        var groupedLists = OrderedResults(results, track, albumMode: false)
            .GroupBy(x => fullPath(x).Substring(0, fullPath(x).LastIndexOf('\\')));

        var musicFolders = groupedLists
            .Where(group => group.Any(x => Utils.IsMusicFile(x.file.Filename)))
            .Select(x => (x.Key, x.ToList()))
            .ToList();

        var nonMusicFolders = groupedLists
            .Where(group => !group.Any(x => Utils.IsMusicFile(x.file.Filename)))
            .ToList();

        var discPattern = new Regex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$");
        if (!discPattern.IsMatch(track.Album))
        {
            for (int i = 0; i < musicFolders.Count; i++)
            {
                var (folderKey, files) = musicFolders[i];
                var parentFolderName = GetFileNameSlsk(folderKey);
                if (discPattern.IsMatch(parentFolderName))
                {
                    var parentFolderKey = GetDirectoryNameSlsk(folderKey);
                    var parentFolderItem = musicFolders.FirstOrDefault(x => x.Key == parentFolderKey);
                    if (parentFolderItem != default)
                    {
                        parentFolderItem.Item2.AddRange(files);
                        musicFolders.RemoveAt(i);
                        i--;
                    }
                    else
                        musicFolders[i] = (parentFolderKey, files);
                }
            }
        }

        foreach (var nonMusicFolder in nonMusicFolders)
        {
            foreach (var musicFolder in musicFolders)
            {
                if (nonMusicFolder.Key.StartsWith(musicFolder.Key))
                {
                    musicFolder.Item2.AddRange(nonMusicFolder);
                    break;
                }
            }
        }

        foreach (var (_, files) in musicFolders)
            files.Sort((x, y) => x.file.Filename.CompareTo(y.file.Filename));


        var fileCounts = musicFolders.Select(x =>
            x.Item2.Count(x => Utils.IsMusicFile(x.file.Filename))
        ).ToList();

        var countIsGood = (int count, int wantedCount) => {
            if (wantedCount == -1)
                return true;
            if (albumTrackCountIneq == '+')
                return count >= wantedCount;
            else if (albumTrackCountIneq == '-')
                return count <= wantedCount;
            else
                return count == wantedCount;
        };

        var result = musicFolders
            .Where(x => countIsGood(x.Item2.Count(rf => Utils.IsMusicFile(rf.file.Filename)), albumTrackCount))
            .Select(ls => new Track {
                ArtistName = track.ArtistName,
                Album = track.Album,
                TrackIsAlbum = true,
                Downloads = new ConcurrentDictionary<string, (SearchResponse response, Soulseek.File file)>(
                    ls.Item2.ToDictionary(y => y.response.Username + "\\" + y.file.Filename, y => y))
            }).ToList();

        return result;
    }


    static async Task<List<Track>> GetUniqueRelatedTracks(Track track)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        var getSearchOptions = (int timeout, FileConditions nec, FileConditions prf) => 
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: removeSingleCharacterSearchTerms,
                searchTimeout: timeout,
                responseFilter: (response) => {
                    return response.UploadSpeed > 0 && nec.BannedUsersSatisfies(response);
                },
                fileFilter: (file) => {
                    return Utils.IsMusicFile(file.Filename) && nec.FileSatisfies(file, track, null);
                        //&& FileConditions.StrictString(file.Filename, track.ArtistName, ignoreCase: true)
                        //&& FileConditions.StrictString(file.Filename, track.TrackTitle, ignoreCase: true)
                        //&& FileConditions.StrictString(file.Filename, track.Album, ignoreCase: true);
                }
            );
        Action<SearchResponse> handler = (r) => {
            if (r.Files.Count() > 0)
            {
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));
            }
        };
        var cts = new CancellationTokenSource();

        await RunSearches(track, results, getSearchOptions, handler, cts.Token);

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


    static IOrderedEnumerable<(Track, IEnumerable<(SlResponse response, SlFile file)>)> EquivalentFiles(Track track, IEnumerable<(SlResponse, SlFile)> fileResponses, int minShares=-1)
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


    static IOrderedEnumerable<(SlResponse response, SlFile file)> OrderedResults(IEnumerable<KeyValuePair<string, (SlResponse, SlFile)>> results, Track track, IEnumerable<string>? ignoreUsers=null, bool useInfer=false, bool useLevenshtein=true, bool albumMode=false)
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

        bool useBracketCheck = true;
        if (albumMode)
        {
            useBracketCheck = false;
            useLevenshtein = false;
        }

        var random = new Random();
        return results.Select(kvp => (response: kvp.Value.Item1, file: kvp.Value.Item2))
                .OrderByDescending(x => !ignoreUsers?.Contains(x.response.Username))
                .ThenByDescending(x => necessaryCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => (x.file.Length != null && x.file.Length > 0) || preferredCond.AcceptNoLength)
                .ThenByDescending(x => preferredCond.BannedUsersSatisfies(x.response))
                .ThenByDescending(x => !useBracketCheck || bracketCheck(x))
                .ThenByDescending(x => preferredCond.StrictTitleSatisfies(x.file.Filename, track.TrackTitle))
                .ThenByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FormatSatisfies(x.file.Filename))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 600)
                .ThenByDescending(x => albumMode || FileConditions.StrictString(x.file.Filename, track.TrackTitle))
                .ThenByDescending(x => !albumMode || FileConditions.StrictString(GetDirectoryNameSlsk(x.file.Filename), track.Album))
                .ThenByDescending(x => FileConditions.StrictString(x.file.Filename, track.ArtistName))
                .ThenByDescending(x => !useLevenshtein || levenshtein(x) <= 5)
                .ThenByDescending(x => x.response.UploadSpeed / 300)
                .ThenByDescending(x => (x.file.BitRate ?? 0) / 70)
                .ThenByDescending(x => useInfer ? infTrack(x).Item2 : 0)
                .ThenByDescending(x => random.Next());
    }


    static async Task RunSearches(Track track, SlDictionary results, 
        Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions, 
        Action<SearchResponse> responseHandler, CancellationToken ct, Action? onSearch = null)
    {
        bool artist = track.ArtistName != "";
        bool title = track.TrackTitle != "";
        bool album = track.Album != "";

        string search = GetSearchString(track);
        var searchTasks = new List<Task>();

        var defaultSearchOpts = getSearchOptions(searchTimeout, necessaryCond, preferredCond);
        searchTasks.Add(Search(search, defaultSearchOpts, responseHandler, ct, onSearch));

        if (search.RemoveDiacriticsIfExist(out string noDiacrSearch) && !track.ArtistMaybeWrong)
            searchTasks.Add(Search(noDiacrSearch, defaultSearchOpts, responseHandler, ct, onSearch));

        await Task.WhenAll(searchTasks);

        if (results.Count == 0 && track.ArtistMaybeWrong && title)
        {
            var cond = new FileConditions(necessaryCond);
            var infTrack = InferTrack(track.TrackTitle, new Track());
            cond.StrictTitle = infTrack.TrackTitle == track.TrackTitle;
            cond.StrictArtist = false;
            var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
            searchTasks.Add(Search($"{infTrack.ArtistName} {infTrack.TrackTitle}", opts, responseHandler, ct, onSearch));
        }

        if (desperateSearch)
        {
            await Task.WhenAll(searchTasks);

            if (results.Count == 0 && !track.ArtistMaybeWrong)
            {
                if (artist && album && title)
                {
                    var cond = new FileConditions(necessaryCond);
                    cond.StrictTitle = true;
                    cond.StrictAlbum = true;
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track.ArtistName} {track.Album}", opts, responseHandler, ct, onSearch));
                }
                if (artist && title && track.Length != -1 && necessaryCond.LengthTolerance != -1)
                {
                    var cond = new FileConditions(necessaryCond);
                    cond.LengthTolerance = -1;
                    cond.StrictTitle = true;
                    cond.StrictArtist = true;
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track.ArtistName} {track.TrackTitle}", opts, responseHandler, ct, onSearch));
                }
            }

            await Task.WhenAll(searchTasks);

            if (results.Count == 0)
            {
                var track2 = track.ArtistMaybeWrong ? InferTrack(track.TrackTitle, new Track()) : track;

                if (track.Album.Length > 3 && album)
                {
                    var cond = new FileConditions(necessaryCond);
                    cond.StrictAlbum = true;
                    cond.StrictTitle = !track.ArtistMaybeWrong;
                    cond.StrictArtist = !track.ArtistMaybeWrong;
                    cond.LengthTolerance = -1;
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track.Album}", opts, responseHandler, ct, onSearch));
                }
                if (track2.TrackTitle.Length > 3 && artist)
                {
                    var cond = new FileConditions(necessaryCond);
                    cond.StrictTitle = !track.ArtistMaybeWrong;
                    cond.StrictArtist = !track.ArtistMaybeWrong;
                    cond.LengthTolerance = -1;
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track2.TrackTitle}", opts, responseHandler, ct, onSearch));
                }
                if (track2.ArtistName.Length > 3 && title)
                {
                    var cond = new FileConditions(necessaryCond);
                    cond.StrictTitle = !track.ArtistMaybeWrong;
                    cond.StrictArtist = !track.ArtistMaybeWrong;
                    cond.LengthTolerance = -1;
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track2.ArtistName}", opts, responseHandler, ct, onSearch));
                }
            }
        }

        await Task.WhenAll(searchTasks);
    }

    static async Task Search(string search, SearchOptions opts, Action<SearchResponse> rHandler, CancellationToken ct, Action? onSearch = null)
    {
        await searchSemaphore.WaitAsync();
        await WaitForInternetConnection();
        try
        {
            search = CleanSearchString(search);
            var q = SearchQuery.FromText(search);
            var searchTasks = new List<Task>();
            onSearch?.Invoke();
            await client.SearchAsync(q, options: opts, cancellationToken: ct, responseHandler: rHandler);
        }
        catch (OperationCanceledException) { }
    }


    public static string GetSearchString(Track track)
    {
        if (track.TrackTitle != "")
            return track.ArtistName + " " + track.TrackTitle;
        else if (track.Album != "")
            return track.ArtistName + " " + track.Album;
        return track.ArtistName;
    }


    public static string CleanSearchString(string str)
    {
        string old = str;
        if (regexPatternToReplace != "")
        {
            old = str;
            str = Regex.Replace(str, regexPatternToReplace, regexReplacePattern).Trim();
            if (str == "") str = old;
        }
        if (!noRemoveSpecialChars)
        {
            old = str;
            str = str.ReplaceSpecialChars(" ").RemoveConsecutiveWs().Trim();
            if (str == "") str = old;
        }
        foreach (var banned in bannedTerms)
        {
            string b1 = banned;
            string b2 = banned.Replace(" ", "-");
            string b3 = banned.Replace(" ", "_");
            string b4 = banned.Replace(" ", "");
            foreach (var s in new string[] { b1, b2, b3, b4 })
                str = str.Replace(s, "*" + s.Substring(1), StringComparison.OrdinalIgnoreCase);
        }

        return str.Trim();
    }


    public static Track InferTrack(string filename, Track defaultTrack)
    {
        Track t = new Track(defaultTrack);
        filename = GetFileNameWithoutExtSlsk(filename).Replace(" — ", " - ").Replace("_", " ").RemoveConsecutiveWs().Trim();

        var trackNumStart = new Regex(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)");
        var trackNumMiddle = new Regex(@"(?<=- )((\d-)?\d{2,3}|\d{2,3}\.?)\s+");

        if (trackNumStart.IsMatch(filename))
        {
            filename = trackNumStart.Replace(filename, "", 1).Trim();
            if (filename.StartsWith("- "))
                filename = filename.Substring(2).Trim();
        }
        else
        {
            filename = trackNumMiddle.Replace(filename, "<<tracknum>>", 1).Trim();
            filename = Regex.Replace(filename, @"-\s*<<tracknum>>\s*-", "-");
            filename = filename.Replace("<<tracknum>>", "");
        }

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
            if (downloads.ContainsKey(file.Filename))
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
                        {
                            if (val == null) 
                                searches.TryRemove(key, out _);
                        }

                        foreach (var (key, val) in downloads)
                        {
                            if (val != null)
                            {
                                val.UpdateText();

                                if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > downloadMaxStaleTime)
                                {
                                    val.stalled = true;
                                    val.UpdateText();

                                    try { val.cts.Cancel(); } catch { }
                                    downloads.TryRemove(key, out _);
                                }
                            }
                            else
                            {
                                downloads.TryRemove(key, out _);
                            }
                        }
                    }
                    else if (!client.State.HasFlag(SoulseekClientStates.LoggedIn | SoulseekClientStates.LoggingIn | SoulseekClientStates.Connecting))
                    {
                        WriteLine($"\nDisconnected, logging in\n", ConsoleColor.DarkYellow, true);
                        try { await Login(useRandomLogin); }
                        catch (Exception ex)
                        {
                            string banMsg = useRandomLogin ? "" : " (possibly a 30-minute ban caused by frequent searches)";
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

        public DateTime UpdateLastChangeTime(bool propagate=true, bool forceChanged=false)
        {
            bool changed = prevTransferState != transfer?.State || prevBytesTransferred != bytesTransferred;
            if (changed || forceChanged)
            {
                lastChangeTime= DateTime.Now;
                stalled = false;
                if (propagate)
                {
                    foreach (var (_, dl) in downloads)
                    {
                        if (dl != this && dl.response.Username == response.Username) 
                            dl.UpdateLastChangeTime(propagate: false, forceChanged: true);
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


    class FileConditions
    {
        public int LengthTolerance = -1;
        public int MinBitrate = -1;
        public int MaxBitrate = -1;
        public int MaxSampleRate = -1;
        public bool StrictTitle = false;
        public bool StrictArtist = false;
        public bool StrictAlbum = false;
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
                && StrictAlbumSatisfies(file.Filename, track.Album) && BannedUsersSatisfies(response);
        }

        public bool FileSatisfies(TagLib.File file, Track track)
        {
            return DangerWordSatisfies(file.Name, track.TrackTitle, track.ArtistName) && FormatSatisfies(file.Name) 
                && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file) 
                && StrictTitleSatisfies(file.Name, track.TrackTitle) && StrictArtistSatisfies(file.Name, track.ArtistName)
                && StrictAlbumSatisfies(file.Name, track.Album);
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

        public bool StrictAlbumSatisfies(string fname, string alname)
        {
            if (!StrictAlbum || alname == "")
                return true;

            return StrictString(GetDirectoryNameSlsk(fname), alname, StrictStringRegexRemove, StrictStringDiacrRemove);
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
                WriteLine($"Warning: No track name column found", ConsoleColor.DarkYellow);
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

                if (track.TrackTitle != "" || track.ArtistName != "" || track.Album != "") 
                    tracks.Add(track);
            }
        }

        if (ytParse)
            YouTube.StopService();

        return tracks;
    }

    static string GetSavePath(string sourceFname, Track track)
    {
        return $"{GetSavePathNoExt(sourceFname, track)}{Path.GetExtension(sourceFname)}";
    }

    static string GetSavePathNoExt(string sourceFname, Track track)
    {
        string outTo = outputFolder;
        if (album && albumCommonPath != "")
        {
            string add = sourceFname.Replace(albumCommonPath, "").Replace(GetFileNameSlsk(sourceFname),"").Trim('\\').Trim();
            if (add!="") outTo = Path.Join(outputFolder, add.Replace('\\', Path.DirectorySeparatorChar));
        }
        return Path.Combine(outTo, $"{GetSaveName(sourceFname, track)}");
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

    public static string GetFileNameSlsk(string fname)
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

    static string ApplyNamingFormat(string filepath, bool applyToNonAudio=false)
    {
        if (nameFormat == "")
            return filepath;

        string newFilePath = filepath;
        string add = Path.GetRelativePath(outputFolder, Path.GetDirectoryName(filepath));
        if (Utils.IsMusicFile(filepath))
            newFilePath = NamingFormat(filepath, nameFormat);
        else if (album && nameFormat.Replace("\\", "/").Contains('/'))
        {
            if (applyToNonAudio && downloadedFiles.Keys.Where(x => Utils.IsMusicFile(x)).Count() > 0)
                newFilePath = Path.Join(Utils.GreatestCommonPath(downloadedFiles.Keys.Where(x => Utils.IsMusicFile(x))), add, Path.GetFileName(filepath));
            else
            {
                pathsToBeFormatted.TryAdd(filepath, char.MinValue);
                return filepath;
            }
        }
        else
            return filepath;
        if (filepath != newFilePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newFilePath));
            Utils.Move(filepath, newFilePath);
            if (add != "" && add != "." && Utils.GetRecursiveFileCount(Path.Join(outputFolder, add)) == 0)
                Directory.Delete(Path.Join(outputFolder, add), true);
        }

        return newFilePath;
    }

    static string NamingFormat(string filepath, string format)
    {
        string newName = format;
        TagLib.File? file = null;

        try { file = TagLib.File.Create(filepath); }
        catch { return filepath; }

        Regex regex = new Regex(@"(\{(?:\{??[^\{]*?\}))");
        MatchCollection matches = regex.Matches(newName);

        while (matches.Count > 0)
        {
            foreach (Match match in matches)
            {
                string inner = match.Groups[1].Value.Trim('{').Trim('}');

                var options = inner.Split('|');
                string chosenOpt = "";

                foreach (var opt in options)
                {
                    string[] parts = Regex.Split(opt, @"\([^\)]*\)");
                    string[] result = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
                    if (result.All(x => GetTagValue(file, x) != "")) {
                        chosenOpt = opt;
                        break;
                    }
                }

                chosenOpt = Regex.Replace(chosenOpt, @"\([^()]*\)|[^()]+", match =>
                {
                    if (match.Value.StartsWith("(") && match.Value.EndsWith(")"))
                        return match.Value.Substring(1, match.Value.Length-2);
                    else
                        return GetTagValue(file, match.Value);
                });
                string old = match.Groups[1].Value;
                old = old.StartsWith("{{") ? old.Substring(1) : old;
                newName = newName.Replace(old, chosenOpt);
            }

            matches = regex.Matches(newName);
        }


        if (newName != format)
        {
            string directory = Path.GetDirectoryName(filepath);
            string dirsep = Path.DirectorySeparatorChar.ToString();
            string extension = Path.GetExtension(filepath);
            newName = newName.Replace(new string[] { "/", "\\" }, dirsep);
            var x = newName.Split(dirsep, StringSplitOptions.RemoveEmptyEntries);
            newName = string.Join(dirsep, x.Select(x => ReplaceInvalidChars(x, " ")));
            string newFilePath = Path.Combine(directory, newName + extension);
            return newFilePath;
        }

        return filepath;
    }

    static string GetTagValue(TagLib.File file, string tag)
    {
        switch (tag)
        {
            case "artist":
                return (file.Tag.FirstPerformer ?? "").RemoveFt();
            case "artists":
                return string.Join(" & ", file.Tag.Performers).RemoveFt();
            case "album_artist":
                return (file.Tag.FirstAlbumArtist ?? "").RemoveFt();
            case "album_artists":
                return string.Join(" & ", file.Tag.AlbumArtists).RemoveFt();
            case "title":
                return file.Tag.Title ?? "";
            case "album":
                return file.Tag.Album ?? "";
            case "year":
                return file.Tag.Year.ToString() ?? "";
            case "track":
                return file.Tag.Track.ToString("D2") ?? "";
            case "disc":
                return file.Tag.Disc.ToString() ?? "";
            case "filename":
                return Path.GetFileNameWithoutExtension(file.Name);
            case "default_foldername":
                return defaultFolderName;
            default:
                return "";
        }
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

        string fullpath = filename;
        filename = Path.GetFileNameWithoutExtension(filename);
        filename = filename.ReplaceInvalidChars("");
        filename = filename.Replace(ignore, "").ToLower();

        if (filename.Contains(searchName) && FileConditions.StrictString(fullpath, searchName2, ignoreCase:true))
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
        var musicFiles = files.Where(filename => Utils.IsMusicFile(filename)).ToArray();

        if (!useTags)
        {
            tracks.RemoveAll(x =>
            {
                if (x.IsNotAudio) return false;
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
                if (x.IsNotAudio) return false;
                bool exists = TrackExistsInCollection(x, necessaryCond, musicIndex, out string? path, precise);
                if (exists) existing.TryAdd(x, path);
                return exists;
            });
        }

        return existing;
    }

    static string[] ParseCommand(string cmd)
    {
        WriteLine(cmd, debugOnly: true);
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

    static string ReplaceSpecialChars(this string str, string replaceStr)
    {
        string special = ";:'\"|?!<>*/\\[]{}()-–—&%^$#@+=`~_";
        foreach (char c in special)
            str = str.Replace(c.ToString(), replaceStr);
        return str;
    }

    static string DisplayString(Track t, Soulseek.File? file=null, SearchResponse? response=null, FileConditions? nec=null, 
        FileConditions? pref=null, bool fullpath=false, string customPath="")
    {
        if (file == null)
            return t.ToString();

        string sampleRate = file.SampleRate.HasValue ? $"{file.SampleRate}Hz/" : "";
        string bitRate = file.BitRate.HasValue ? $"{file.BitRate}kbps/" : "";
        string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string fname = fullpath ? "\\" + file.Filename : "\\..\\" + (customPath == "" ? GetFileNameSlsk(file.Filename) : customPath);
        string length = Utils.IsMusicFile(file.Filename) ? (file.Length ?? -1).ToString() + "s/" : ""; 
        string displayText = $"{response?.Username ?? ""}{fname} [{length}{sampleRate}{bitRate}{fileSize}]";

        string necStr = nec != null ? $"nec:{nec.GetNotSatisfiedName(file, t, response)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, t, response)}" : "";
        string cond = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }

    static void PrintTracks(List<Track> tracks, int number = int.MaxValue, bool fullInfo=false, bool pathsOnly=false, bool showAncestors=false)
    {
        number = Math.Min(tracks.Count, number);

        string ancestor = "";

        if (showAncestors)
            ancestor = Utils.GreatestCommonPath(tracks.SelectMany(x => x.Downloads.Select(y => y.Value.Item2.Filename)));

        if (pathsOnly) {
            for (int i = 0; i < number; i++)
                foreach (var x in tracks[i].Downloads)
                {
                    if (ancestor == "")
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1));
                    else
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, "")));
                }
        }
        else if (!fullInfo) {
            for (int i = 0; i < number; i++)
                Console.WriteLine($"  {tracks[i]}");
        }
        else {
            for (int i = 0; i < number; i++) {
                if (!tracks[i].IsNotAudio)
                {
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
                            if (ancestor == "")
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1));
                            else
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, "")));
                        }
                        if (tracks[i].Downloads?.Count > 0) Console.WriteLine();
                    }
                }
                else
                {
                    Console.WriteLine($"  File:               {GetFileNameSlsk(tracks[i].Downloads.First().Value.Item2.Filename)}");
                    Console.WriteLine($"  Shares:             {tracks[i].Downloads.Count}");
                    foreach (var x in tracks[i].Downloads) {
                        if (ancestor == "")
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1));
                        else
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, "")));
                    }
                    Console.WriteLine();
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

    public static void WriteLine(string value, ConsoleColor color=ConsoleColor.Gray, bool safe=false, bool debugOnly=false)
    {
        if (debugOnly && !debugInfo)
            return;
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
        if (noWaitForInternet)
            return;
        while (true)
        {
            WriteLine("Wait for internet", debugOnly: true);
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
            WriteLine("Wait for login", debugOnly: true);
            if (client.State.HasFlag(SoulseekClientStates.LoggedIn))
                break;
            await Task.Delay(500);     
        }
    }

    static List<string> bannedTerms = new List<string>()
    {
        "depeche mode", "beatles", "prince revolutions", "michael jackson", "coexist", "bob dylan", "enter shikari",
        "village people", "lenny kravitz", "beyonce", "beyoncé", "lady gaga", "jay z", "kanye west", "rihanna",
        "adele", "kendrick lamar", "bad romance", "born this way", "weeknd", "broken hearted", "highway 61 revisited",
        "west gold digger", "west good life"
    };
}


public struct Track
{
    public string TrackTitle = "";
    public string ArtistName = "";
    public string Album = "";
    public string URI = "";
    public int Length = -1;
    public bool ArtistMaybeWrong = false;
    public bool TrackIsAlbum = false;
    public bool IsNotAudio = false;
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
        TrackIsAlbum = other.TrackIsAlbum;
        IsNotAudio = other.IsNotAudio;
    }

    public override string ToString()
    {
        return ToString(false);
    }

    public string ToString(bool noInfo = false)
    {
        var length = Length > 0 && !noInfo ? $" ({Length}s)" : "";
        var album = TrackIsAlbum && !noInfo ? " (album)" : "";
        var artist = ArtistName != "" ? $"{ArtistName} - " : "";
        string str = "";
        if (IsNotAudio)
            str = $"{Program.GetFileNameSlsk(Downloads.First().Value.Item2.Filename)}";
        else if (TrackIsAlbum)
            str = $"{artist}{Album}{album}";
        else
            str = $"{artist}{TrackTitle}{length}";

        return str;
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
        Directory.CreateDirectory(Path.GetDirectoryName(path));

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

    public bool HasFails()
    {
        if (File.Exists(path) && ReadAllLines().Where(x => x.StartsWith("# Failed: ")).Count() > 0)
            return true;
        return false;
    }

    public bool HasFail(Track track, out string reason)
    {
        reason = "";
        if (!HasFails())
            return false;
        foreach (var x in ReadAllLines())
        {
            if (x.StartsWith($"# Failed: {track}"))
            {
                var matches = Regex.Matches(x, @"\[([^\[\]]+)\]");
                if (matches.Count > 0)
                    reason = matches[matches.Count - 1].Groups[1].Value;
                return true;
            }
        }
        return false;
    }

    public List<string> GetFails()
    {
        return ReadAllLines().Where(x => x.StartsWith("# Failed: ")).Select(x => x.Replace("# Failed: ","")).ToList();
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
    public static bool IsMusicFile(string fileName)
    {
        var musicExtensions = new string[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".wma", ".m4a", ".alac", ".ape", ".opus" };
        var extension = Path.GetExtension(fileName).ToLower();
        return musicExtensions.Contains(extension);
    }

    public static bool IsImageFile(string fileName)
    {
        var exts = new string[] { ".jpg", ".jpeg", ".png" };
        var extension = Path.GetExtension(fileName).ToLower();
        return exts.Contains(extension);
    }

    public static int GetRecursiveFileCount(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        int count = Directory.GetFiles(directory).Length;
        foreach (string subDirectory in Directory.GetDirectories(directory))
            count += GetRecursiveFileCount(subDirectory);

        return count;
    }

    public static void Move(string sourceFilePath, string destinationFilePath)
    {
        if (File.Exists(destinationFilePath))
            File.Delete(destinationFilePath);
        File.Move(sourceFilePath, destinationFilePath);
    }

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

    public static string GreatestCommonPath(IEnumerable<string> paths, char dirsep='-')
    {
        var commonPath = paths.First();
        foreach (var path in paths.Skip(1))
        {
            commonPath = GetCommonPath(commonPath, path, dirsep);
        }
        return commonPath;
    }

    private static string GetCommonPath(string path1, string path2, char dirsep = '-')
    {
        if (dirsep == '-')
            dirsep = Path.DirectorySeparatorChar;
        var minLength = Math.Min(path1.Length, path2.Length);
        var commonPathLength = 0;
        for (int i = 0; i < minLength; i++)
        {
            if (path1[i] != path2[i])
                break;
            if (path1[i] == dirsep)
                commonPathLength = i + 1;
        }
        return path1.Substring(0, commonPathLength);
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
