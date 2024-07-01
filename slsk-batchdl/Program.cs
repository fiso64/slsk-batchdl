using AngleSharp.Dom;
using Konsole;
using Soulseek;
using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using AngleSharp.Text;
using System.Diagnostics;

using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using File = System.IO.File;
using Directory = System.IO.Directory;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;


// todo
// - Why does it use so much CPU and memory?
// - Very slow startup time on linux

// undocumented options
// --on-complete
// --artist-col, --title-col, --album-col, --length-col, --yt-desc-col, --yt-id-col, --album-track-count-col
// --input-type, --login, --random-login, --no-modify-share-count, --unknown-error-retries
// --fails-to-deprioritize (=1), --fails-to-ignore (=2), --invalid-replace-str
// --cond, --pref, --danger-words, --pref-danger-words, --strict-title, --strict-artist, --strict-album
// --fast-search-delay, --fast-search-min-up-speed
// --min-album-track-count, --max-album-track-count, --extract-max-track-count

static class Program
{
    static FileConditions necessaryCond = new()
    {

    };

    static FileConditions preferredCond = new()
    {
        Formats = new string[] { "mp3" },
        LengthTolerance = 3,
        MinBitrate = 200,
        MaxBitrate = 2500,
        MaxSampleRate = 48000,
        StrictTitle = true,
        StrictAlbum = true,
        AcceptNoLength = false,
    };

    static SoulseekClient? client = null;
    static TrackLists trackLists = new();
    static ConcurrentDictionary<Track, SearchInfo> searches = new();
    static ConcurrentDictionary<string, DownloadWrapper> downloads = new();
    static ConcurrentDictionary<string, int> userSuccessCount = new();
    static int deprioritizeOn = -1;
    static int ignoreOn = -2;
    static string outputFolder = "";
    static string m3uFilePath = "";
    static string musicDir = "";

    static string parentFolder = Directory.GetCurrentDirectory();
    static string folderName = "";
    static string defaultFolderName = "";
    static string ytUrl = "";
    static string searchStr = "";
    static string spotifyUrl = "";
    static string spotifyId = "";
    static string spotifySecret = "";
    static string ytKey = "";
    static string csvPath = "";
    static int csvColumnCount = -1;
    static string username = "";
    static string password = "";
    static string artistCol = "";
    static string albumCol = "";
    static string trackCol = "";
    static string ytIdCol = "";
    static string descCol = "";
    static string trackCountCol = "";
    static string lengthCol = "";
    static bool aggregate = false;
    static bool album = false;
    static string albumArtOption = "";
    static bool albumArtOnly = false;
    static bool interactiveMode = false;
    static bool albumIgnoreFails = false;
    static int minAlbumTrackCount = -1;
    static int maxAlbumTrackCount = -1;
    static bool setAlbumMinTrackCount = true;
    static bool setAlbumMaxTrackCount = false;
    static string albumCommonPath = "";
    static Track regexToReplace = new Track();
    static Track regexReplaceBy = new Track();
    static string timeUnit = "s";
    static string displayStyle = "single";
    static string input = "";
    static string nameFormat = "";
    static string invalidReplaceStr = " ";
    static bool skipNotFound = false;
    static bool desperateSearch = false;
    static bool noRemoveSpecialChars = false;
    static bool artistMaybeWrong = false;
    static bool fastSearch = false;
    static int fastSearchDelay = 300;
    static double fastSearchMinUpSpeed = 1.0;
    static bool ytParse = false;
    static bool removeFt = false;
    static bool removeBrackets = false;
    static bool reverse = false;
    static bool useYtdlp = false;
    static string ytdlpArgument = "";
    static bool skipExisting = false;
    static string m3uOption = "fails";
    static bool removeTracksFromSource = false;
    static bool getDeleted = false;
    static bool deletedOnly = false;
    static bool removeSingleCharacterSearchTerms = false;
    static int maxTracks = int.MaxValue;
    static int minUsersAggregate = 2;
    static bool relax = false;
    static bool debugInfo = false;
    static int offset = 0;
    static string onComplete = "";

    static string confPath = "";

    static Spotify? spotifyClient = null;
    static string playlistUri = "";
    static int downloadMaxStaleTime = 50000;
    static int updateDelay = 100;
    static int searchTimeout = 5000;
    static int maxConcurrentProcesses = 2;
    static int unknownErrorRetries = 2;
    static int maxRetriesPerTrack = 30;
    static int listenPort = 50000;

    static object consoleLock = new object();
    static object csvLock = new object();

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

    private static M3UEditor? m3uEditor;
    private static CancellationTokenSource? mainLoopCts;

    static InputType inputType = InputType.None;

    public enum InputType
    {
        None,
        Spotify,
        YouTube,
        Bandcamp,
        String,
        CSV
    };

    static SkipMode skipMode = SkipMode.NamePrecise;

    public enum SkipMode
    {
        Name,
        NamePrecise,
        Tag,
        TagPrecise,
        M3u,
    };

    static void PrintHelp()
    {
        Console.WriteLine("Usage: sldl <input> [OPTIONS]" +
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
                            "\n                                 info to download. The names of the columns should be Artist," +
                            "\n                                 Title, Album, Length. Only the title or album column is" +
                            "\n                                 required, but extra info may improve search results." +
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
                            "\n  -p --path <path>               Download directory" +
                            "\n  -f --folder <name>             Subfolder name. Set to '.' to output directly to the" +
                            "\n                                 download folder (default: playlist/csv name)" +
                            "\n  -n --number <maxtracks>        Download the first n tracks of a playlist" +
                            "\n  -o --offset <offset>           Skip a specified number of tracks" +
                            "\n  -r --reverse                   Download tracks in reverse order" +
                            "\n  --name-format <format>         Name format for downloaded tracks, e.g \"{artist} - {title}\"" +
                            "\n  --fast-search                  Begin downloading as soon as a file satisfying the preferred" +
                            "\n                                 conditions is found. Higher chance to download wrong files." +
                            "\n  --remove-from-source           Remove downloaded tracks from source playlist or CSV file " +
                            "\n                                 (spotify and CSV only)" +
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
                            "\n  --deleted-only                 Only retrieve & download deleted music. Combine with --print" +
                            "\n                                 tracks-full to display a list of all deleted titles & urls." +
                            "\n" +
                            "\n  --time-format <format>         Time format in Length column of the csv file (e.g h:m:s.ms" +
                            "\n                                 for durations like 1:04:35.123). Default: s" +
                            "\n  --yt-parse                     Enable if the csv file contains YouTube video titles and" +
                            "\n                                 channel names; attempt to parse them into title and artist" +
                            "\n                                 names." +
                            "\n" +
                            "\n  --format <format>              Accepted file format(s), comma-separated" +
                            "\n  --length-tol <sec>             Length tolerance in seconds" +
                            "\n  --min-bitrate <rate>           Minimum file bitrate" +
                            "\n  --max-bitrate <rate>           Maximum file bitrate" +
                            "\n  --min-samplerate <rate>        Minimum file sample rate" +
                            "\n  --max-samplerate <rate>        Maximum file sample rate" +
                            "\n  --min-bitdepth <depth>         Minimum bit depth" +
                            "\n  --max-bitdepth <depth>         Maximum bit depth" +
                            "\n  --banned-users <list>          Comma-separated list of users to ignore" +
                            "\n" +
                            "\n  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)" +
                            "\n  --pref-length-tol <sec>        Preferred length tolerance in seconds (default: 3)" +
                            "\n  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)" +
                            "\n  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2500)" +
                            "\n  --pref-min-samplerate <rate>   Preferred minimum sample rate" +
                            "\n  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 48000)" +
                            "\n  --pref-min-bitdepth <depth>    Preferred minimum bit depth" +
                            "\n  --pref-max-bitdepth <depth>    Preferred maximum bit depth" +
                            "\n  --pref-banned-users <list>     Comma-separated list of users to deprioritize" +
                            "\n" +
                            "\n  --strict-conditions            Skip files with missing properties instead of accepting by" +
                            "\n                                 default; if --min-bitrate is set, ignores any files with" +
                            "\n                                 unknown bitrate." +
                            "\n" +
                            "\n  -a --album                     Album download mode" +
                            "\n  -t --interactive               When downloading albums: Allows to select the wanted album" +
                            "\n  --album-track-count <num>      Specify the exact number of tracks in the album. Folders" +
                            "\n                                 with a different number of tracks will be ignored. Append" +
                            "\n                                 a '+' or '-' after the number for the inequalities >= and <=" +
                            "\n  --album-ignore-fails           When downloading an album and one of the files fails, do not" +
                            "\n                                 skip to the next source and do not delete all successfully" +
                            "\n                                 downloaded files" +
                            "\n  --album-art <option>           Retrieve additional images after downloading the album:" +
                            "\n                                 'largest': Download from the folder with the largest image" +
                            "\n                                 'most': Download from the folder containing the most images" +
                            "\n  --album-art-only               Only download album art for the provided album" +
                            "\n" +
                            "\n  -g --aggregate                 Instead of downloading a single track matching the input," +
                            "\n                                 find and download all distinct songs associated with the" +
                            "\n                                 provided artist, album, or track title." +
                            "\n  --min-users-aggregate <num>    Minimum number of users sharing a track before it is" +
                            "\n                                 downloaded in aggregate mode. Setting it to higher values" +
                            "\n                                 will significantly reduce false positives, but also cause it" +
                            "\n                                 to ignore rarer songs. Default: 2" +
                            "\n  --relax-filtering              Slightly relax file filtering in aggregate mode to include" +
                            "\n                                 more results" +
                            "\n" +
                            "\n  -s --skip-existing             Skip if a track matching file conditions is found in the" +
                            "\n                                 output folder or your music library (if provided)" +
                            "\n  --skip-mode <mode>             'name': Use only filenames to check if a track exists" +
                            "\n                                 'name-precise' (default): Use filenames and check conditions" +
                            "\n                                 'tag': Use file tags (slower)" +
                            "\n                                 'tag-precise': Use file tags and check file conditions" +
                            "\n                                 'm3u': Skip all tracks that don't have a fail entry in m3u" +
                            "\n  --music-dir <path>             Specify to also skip downloading tracks found in a music" +
                            "\n                                 library. Use with --skip-existing" +
                            "\n  --skip-not-found               Skip searching for tracks that weren't found on Soulseek" +
                            "\n                                 during the last run. Fails are read from the m3u file." +
                            "\n" +
                            "\n  --no-remove-special-chars      Do not remove special characters before searching" +
                            "\n  --remove-ft                    Remove 'feat.' and everything after before searching" +
                            "\n  --remove-brackets              Remove square brackets and their contents before searching" +
                            "\n  --regex <regex>                Remove a regexp from all track titles and artist names." +
                            "\n                                 Optionally specify a replacement regex after a semicolon." +
                            "\n                                 Add 'T:', 'A:' or 'L:' at the start to only apply this to" +
                            "\n                                 the track title, artist, or album respectively." +
                            "\n  --artist-maybe-wrong           Performs an additional search without the artist name." +
                            "\n                                 Useful for sources like SoundCloud where the \"artist\"" +
                            "\n                                 could just be an uploader. Note that when downloading a" +
                            "\n                                 YouTube playlist via url, this option is set automatically" +
                            "\n                                 on a per-track basis, so it is best kept off in that case." +
                            "\n  -d --desperate                 Tries harder to find the desired track by searching for the" +
                            "\n                                 artist/album/title only, then filtering. (slower search)" +
                            "\n  --yt-dlp                       Use yt-dlp to download tracks that weren't found on" +
                            "\n                                 Soulseek. yt-dlp must be available from the command line." +
                            "\n  --yt-dlp-argument <str>        The command line arguments when running yt-dlp. Default:" +
                            "\n                                 \"{id}\" -f bestaudio/best -cix -o \"{savepath}.%(ext)s\"" +
                            "\n                                 Available vars are: {id}, {savedir}, {savepath} (w/o ext)." +
                            "\n                                 Note that with -x, yt-dlp will download webms in case" +
                            "\n                                 ffmpeg is unavailable." +
                            "\n" +
                            "\n  -c --config <path>             Set config file location" +
                            "\n  --search-timeout <ms>          Max search time in ms (default: 5000)" +
                            "\n  --max-stale-time <ms>          Max download time without progress in ms (default: 50000)" +
                            "\n  --concurrent-downloads <num>   Max concurrent downloads (default: 2)" +
                            "\n  --searches-per-time <num>      Max searches per time interval. Higher values may cause" +
                            "\n                                 30-minute bans. (default: 34)" +
                            "\n  --searches-renew-time <sec>    Controls how often available searches are replenished." +
                            "\n                                 Lower values may cause 30-minute bans. (default: 220)" +
                            "\n  --display-mode <option>        Changes how searches and downloads are displayed:" +
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
                            "\n  --debug                        Print extra debug info" +
                            "\n" +
                            "\n  Note: Acronyms of two- and --three-word-arguments are also accepted, e.g. --twa");
    }

    static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--help") || args.Contains("-h") || args.Length == 0)
        {
            PrintHelp();
            return;
        }

#if WINDOWS
        try
        {
            if (Console.BufferHeight <= 50 && displayStyle != "simple")
                WriteLine("Windows: Recommended to use the default command prompt to avoid printing issues.");
        }
        catch { }
#endif

        confPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sldl.conf");
        string old = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "slsk-batchdl.conf");

        if (!File.Exists(confPath) && File.Exists(old))
            confPath = old;

        args = args.SelectMany(arg =>
        {
            if (arg.Length > 3 && arg.StartsWith("--") && arg.Contains('='))
            {
                var parts = arg.Split('=', 2);
                return new[] { parts[0], parts[1] };
            }
            return new[] { arg };
        }).ToArray();

        bool confPathChanged = false;
        int idx = Array.LastIndexOf(args, "-c");
        int idx2 = Array.LastIndexOf(args, "--config");
        idx = idx > -1 ? idx : idx2;
        if (idx != -1)
        {
            confPath = args[idx + 1];
            confPathChanged = true;
        }

        if ((File.Exists(confPath) || confPathChanged) && confPath != "none")
        {
            if (File.Exists(Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath)))
                confPath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath);

            var finalArgs = new List<string>(ParseConfig(confPath));
            finalArgs.AddRange(args);
            args = finalArgs.ToArray();
        }

        if (args.Contains("--strict") || args.Contains("--strict-conditions") || args.Contains("--sc"))
        {
            preferredCond.AcceptMissingProps = false;
            necessaryCond.AcceptMissingProps = false;
            preferredCond.UnsetClientSpecificFields();
            necessaryCond.UnsetClientSpecificFields();
        }

        args = args.SelectMany(arg => 
        {
            if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && !arg.Contains(' ') && arg.ToLower() == arg)
                return arg.Substring(1).Select(c => $"-{c}");
            return new[] { arg };
        }).ToArray();

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
                    case "--it":
                    case "--input-type":
                        inputType = args[++i].ToLower().Trim() switch
                        {
                            "none" => InputType.None,
                            "csv" => InputType.CSV,
                            "youtube" => InputType.YouTube,
                            "spotify" => InputType.Spotify,
                            "bandcamp" => InputType.Bandcamp,
                            "string" => InputType.String,
                            _ => throw new ArgumentException($"Invalid input type '{args[i]}'"),
                        };
                        break;
                    case "-p":
                    case "--path":
                        parentFolder = args[++i];
                        break;
                    case "-c":
                    case "--config":
                        confPath = args[++i];
                        break;
                    case "-f":
                    case "--folder":
                        folderName = args[++i];
                        break;
                    case "-m":
                    case "--md":
                    case "--music-dir":
                        musicDir = args[++i];
                        break;
                    case "-g":
                    case "--aggregate":
                        aggregate = true;
                        break;
                    case "--mua":
                    case "--min-users-aggregate":
                        minUsersAggregate = int.Parse(args[++i]);
                        break;
                    case "--rf":
                    case "--relax":
                    case "--relax-filtering":
                        relax = true;
                        break;
                    case "--si":
                    case "--spotify-id":
                        spotifyId = args[++i];
                        break;
                    case "--ss":
                    case "--spotify-secret":
                        spotifySecret = args[++i];
                        break;
                    case "--yk":
                    case "--youtube-key":
                        ytKey = args[++i];
                        break;
                    case "-l":
                    case "--login":
                        var login = args[++i].Split(';',2);
                        username = login[0];
                        password = login[1];
                        break;
                    case "--user":
                    case "--username":
                        username = args[++i];
                        break;
                    case "--pass":
                    case "--password":
                        password = args[++i];
                        break;
                    case "--rl":
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
                    case "--album-track-count-col":
                        trackCountCol = args[++i];
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
                    case "--nf":
                    case "--name-format":
                        nameFormat = args[++i];
                        break;
                    case "--invalid-replace-str":
                        invalidReplaceStr = args[++i];
                        break;
                    case "--p":
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
                    case "--pt":
                    case "--print-tracks":
                        debugPrintTracks = true;
                        debugDisableDownload = true;
                        break;
                    case "--ptf":
                    case "--print-tracks-full":
                        debugPrintTracks = true;
                        debugPrintTracksFull = true;
                        debugDisableDownload = true;
                        break;
                    case "--pr":
                    case "--print-results":
                        debugDisableDownload = true;
                        break;
                    case "--prf":
                    case "--print-results-full":
                        debugDisableDownload = true;
                        printResultsFull = true;
                        break;
                    case "--yp":
                    case "--yt-parse":
                        ytParse = true;
                        break;
                    case "--length-col":
                        lengthCol = args[++i];
                        break;
                    case "--tf":
                    case "--time-format":
                        timeUnit = args[++i];
                        break;
                    case "--yd":
                    case "--yt-dlp":
                        useYtdlp = true;
                        break;
                    case "-s":
                    case "--se":
                    case "--skip-existing":
                        skipExisting = true;
                        break;
                    case "--snf":
                    case "--skip-not-found":
                        skipNotFound = true;
                        break;
                    case "--rfp":
                    case "--rfs":
                    case "--remove-from-source":
                    case "--remove-from-playlist":
                        removeTracksFromSource = true;
                        break;
                    case "--rft":
                    case "--remove-ft":
                        removeFt = true;
                        break;
                    case "--rb":
                    case "--remove-brackets":
                        removeBrackets = true;
                        break;
                    case "--gd":
                    case "--get-deleted":
                        getDeleted = true;
                        break;
                    case "--do":
                    case "--deleted-only":
                        getDeleted = true;
                        deletedOnly = true;
                        break;
                    case "--re":
                    case "--regex":
                        string s = args[++i].Replace("\\;", "<<semicol>>");
                        string applyTo = "TAL";

                        if (s.Length > 2 && s[1] == ':' && (s[0] == 'T' || s[0] == 'A' || s[0] == 'L'))
                        {
                            applyTo = s[0].ToString();
                            s = s.Substring(2);
                        }

                        var parts = s.Split(";").ToArray();
                        string toReplace = parts[0].Replace("<<semicol>>", ";");
                        string replaceBy = parts.Length > 1 ? parts[1].Replace("<<semicol>>", ";") : "";
                        
                        if (applyTo.Contains('T'))
                        {
                            regexToReplace.Title = toReplace;
                            regexReplaceBy.Title = replaceBy;
                        }
                        if (applyTo.Contains('A'))
                        {
                            regexToReplace.Artist = toReplace;
                            regexReplaceBy.Artist = replaceBy;
                        }
                        if (applyTo.Contains('L'))
                        {
                            regexToReplace.Album = toReplace;
                            regexReplaceBy.Album = replaceBy;
                        }
                        break;
                    case "-r":
                    case "--reverse":
                        reverse = true;
                        break;
                    case "--m3u":
                    case "--m3u8":
                        m3uOption = args[++i];
                        break;
                    case "--lp":
                    case "--port":
                    case "--listen-port":
                        listenPort = int.Parse(args[++i]);
                        break;
                    case "--st":
                    case "--timeout":
                    case "--search-timeout":
                        searchTimeout = int.Parse(args[++i]);
                        break;
                    case "--mst":
                    case "--stale-time":
                    case "--max-stale-time":
                        downloadMaxStaleTime = int.Parse(args[++i]);
                        break;
                    case "--cp":
                    case "--cd":
                    case "--processes":
                    case "--concurrent-processes":
                    case "--concurrent-downloads":
                        maxConcurrentProcesses = int.Parse(args[++i]);
                        break;
                    case "--spt":
                    case "--searches-per-time":
                        searchesPerTime = int.Parse(args[++i]);
                        break;
                    case "--srt":
                    case "--searches-renew-time":
                        searchResetTime = int.Parse(args[++i]);
                        break;
                    case "--mr":
                    case "--retries":
                    case "--max-retries":
                        maxRetriesPerTrack = int.Parse(args[++i]);
                        break;
                    case "--atc":
                    case "--album-track-count":
                        string a = args[++i];
                        if (a == "-1")
                        {
                            minAlbumTrackCount = -1;
                            maxAlbumTrackCount = -1;
                        }
                        else if (a.Last() == '-')
                        {
                            maxAlbumTrackCount = int.Parse(a.Substring(0, a.Length - 1));
                        }
                        else if (a.Last() == '+')
                        {
                            minAlbumTrackCount = int.Parse(a.Substring(0, a.Length - 1));
                        }
                        else
                        {
                            minAlbumTrackCount = int.Parse(a);
                            maxAlbumTrackCount = minAlbumTrackCount;
                        }
                        break;
                    case "--matc":
                    case "--min-album-track-count":
                        minAlbumTrackCount = int.Parse(args[++i]);
                        break;
                    case "--Matc":
                    case "--max-album-track-count":
                        maxAlbumTrackCount = int.Parse(args[++i]);
                        break;
                    case "--eMtc":
                    case "--extract-max-track-count":
                        setAlbumMaxTrackCount = true;
                        break;
                    case "--aa":
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
                                throw new ArgumentException($"Invalid album art download mode '{args[i]}'");
                        }
                        break;
                    case "--aao":
                    case "--aa-only":
                    case "--album-art-only":
                        albumArtOnly = true;
                        if (albumArtOption == "")
                        {
                            albumArtOption = "largest";
                        }
                        preferredCond = new FileConditions();
                        necessaryCond = new FileConditions();
                        break;
                    case "--aif":
                    case "--album-ignore-fails":
                        albumIgnoreFails = true;
                        break;
                    case "-t":
                    case "--interactive":
                        interactiveMode = true;
                        break;
                    case "--pf":
                    case "--paf":
                    case "--pref-format":
                        preferredCond.Formats = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "--plt":
                    case "--pref-tolerance":
                    case "--pref-length-tol":
                    case "--pref-length-tolerance":
                        preferredCond.LengthTolerance = int.Parse(args[++i]);
                        break;
                    case "--pmbr":
                    case "--pref-min-bitrate":
                        preferredCond.MinBitrate = int.Parse(args[++i]);
                        break;
                    case "--pMbr":
                    case "--pref-max-bitrate":
                        preferredCond.MaxBitrate = int.Parse(args[++i]);
                        break;
                    case "--pmsr":
                    case "--pref-min-samplerate":
                        preferredCond.MinSampleRate = int.Parse(args[++i]);
                        break;
                    case "--pMsr":
                    case "--pref-max-samplerate":
                        preferredCond.MaxSampleRate = int.Parse(args[++i]);
                        break;
                    case "--pmbd":
                    case "--pref-min-bitdepth":
                        preferredCond.MinBitDepth = int.Parse(args[++i]);
                        break;
                    case "--pMbd":
                    case "--pref-max-bitdepth":
                        preferredCond.MaxBitDepth = int.Parse(args[++i]);
                        break;
                    case "--pdw":
                    case "--pref-danger-words":
                        preferredCond.DangerWords = args[++i].Split(',');
                        break;
                    case "--pst":
                    case "--pstt":
                    case "--pref-strict-title":
                        preferredCond.StrictTitle = true;
                        break;
                    case "--psa":
                    case "--pref-strict-artist":
                        preferredCond.StrictArtist = true;
                        break;
                    case "--psal":
                    case "--pref-strict-album":
                        preferredCond.StrictAlbum = true;
                        break;
                    case "--pbu":
                    case "--pref-banned-users":
                        preferredCond.BannedUsers = args[++i].Split(',');
                        break;
                    case "--af":
                    case "--format":
                        necessaryCond.Formats = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "--lt":
                    case "--tolerance":
                    case "--length-tol":
                    case "--length-tolerance":
                        necessaryCond.LengthTolerance = int.Parse(args[++i]);
                        break;
                    case "--mbr":
                    case "--min-bitrate":
                        necessaryCond.MinBitrate = int.Parse(args[++i]);
                        break;
                    case "--Mbr":
                    case "--max-bitrate":
                        necessaryCond.MaxBitrate = int.Parse(args[++i]);
                        break;
                    case "--msr":
                    case "--min-samplerate":
                        necessaryCond.MinSampleRate = int.Parse(args[++i]);
                        break;
                    case "--Msr":
                    case "--max-samplerate":
                        necessaryCond.MaxSampleRate = int.Parse(args[++i]);
                        break;
                    case "--mbd":
                    case "--min-bitdepth":
                        necessaryCond.MinBitDepth = int.Parse(args[++i]);
                        break;
                    case "--Mbd":
                    case "--max-bitdepth":
                        necessaryCond.MaxBitDepth = int.Parse(args[++i]);
                        break;
                    case "--dw":
                    case "--danger-words":
                        necessaryCond.DangerWords = args[++i].Split(',');
                        break;
                    case "--stt":
                    case "--strict-title":
                        necessaryCond.StrictTitle = true;
                        break;
                    case "--sa":
                    case "--strict-artist":
                        necessaryCond.StrictArtist = true;
                        break;
                    case "--sal":
                    case "--strict-album":
                        necessaryCond.StrictAlbum = true;
                        break;
                    case "--bu":
                    case "--banned-users":
                        necessaryCond.BannedUsers = args[++i].Split(',');
                        break;
                    case "--c":
                    case "--cond":
                    case "--conditions":
                        ParseConditions(necessaryCond, args[++i]);
                        break;
                    case "--pc":
                    case "--pref":
                    case "--preferred-conditions":
                        ParseConditions(preferredCond, args[++i]);
                        break;
                    case "--nmsc":
                    case "--no-modify-share-count":
                        noModifyShareCount = true;
                        break;
                    case "-d":
                    case "--desperate":
                        desperateSearch = true;
                        break;
                    case "--dm":
                    case "--display":
                    case "--display-mode":
                        switch (args[++i])
                        {
                            case "single":
                            case "double":
                            case "simple":
                                displayStyle = args[i];
                                break;
                            default:
                                throw new ArgumentException($"Invalid display style '{args[i]}'");
                        }
                        break;
                    case "--sm":
                    case "--skip-mode":
                        skipMode = args[++i].ToLower().Trim() switch
                        {
                            "name" => SkipMode.Name,
                            "name-precise" => SkipMode.NamePrecise,
                            "tag" => SkipMode.Tag,
                            "tag-precise" => SkipMode.TagPrecise,
                            "m3u" => SkipMode.M3u,
                            _ => throw new ArgumentException($"Invalid skip mode '{args[i]}'"),
                        };
                        break;
                    case "--nrsc":
                    case "--no-remove-special-chars":
                        noRemoveSpecialChars = true;
                        break;
                    case "--amw":
                    case "--artist-maybe-wrong":
                        artistMaybeWrong = true;
                        break;
                    case "--fs":
                    case "--fast-search":
                        fastSearch = true;
                        break;
                    case "--fsd":
                    case "--fast-search-delay":
                        fastSearchDelay = int.Parse(args[++i]);
                        break;
                    case "--fsmus":
                    case "--fast-search-min-up-speed":
                        fastSearchMinUpSpeed = double.Parse(args[++i]);
                        break;
                    case "--debug":
                        debugInfo = true;
                        break;
                    case "--sc":
                    case "--strict":
                    case "--strict-conditions":
                        preferredCond.AcceptMissingProps = false;
                        necessaryCond.AcceptMissingProps = false;
                        break;
                    case "--yda":
                    case "--yt-dlp-argument":
                        ytdlpArgument = args[++i];
                        break;
                    case "-a":
                    case "--album":
                        album = true;
                        break;
                    case "--oc":
                    case "--on-complete":
                        onComplete = args[++i];
                        break;
                    case "--ftd":
                    case "--fails-to-deprioritize":
                        deprioritizeOn = -int.Parse(args[++i]);
                        break;
                    case "--fti":
                    case "--fails-to-ignore":
                        ignoreOn = -int.Parse(args[++i]);
                        break;
                    case "--unknown-error-retries":
                        unknownErrorRetries = int.Parse(args[++i]);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            else
            {
                if (input == "")
                    input = args[i].Trim();
                else
                    throw new ArgumentException($"Invalid argument \'{args[i]}\'. Input is already set to \'{input}\'");
            }
        }

        if (input == "")
            throw new ArgumentException($"No input provided");

        if (ytKey != "")
            YouTube.apiKey = ytKey;

        if (debugDisableDownload)
            maxConcurrentProcesses = 1;

        ignoreOn = Math.Min(ignoreOn, deprioritizeOn);

        if (inputType == InputType.YouTube || (inputType == InputType.None && input.StartsWith("http") && input.Contains("youtu")))
        {
            WriteLine("Youtube download", debugOnly: true);
            await YoutubeInput();
        }
        else if (inputType == InputType.Spotify || (inputType == InputType.None && (input.StartsWith("http") && input.Contains("spotify")) || input == "spotify-likes"))
        {
            WriteLine("Spotify download", debugOnly: true);
            await SpotifyInput();
        }
        else if (inputType == InputType.Bandcamp || (inputType == InputType.None && input.StartsWith("http") && input.Contains("bandcamp")))
        {
            WriteLine("Bandcamp download", debugOnly: true);
            await BandcampInput();
        }
        else if (inputType == InputType.CSV || (inputType == InputType.None && input.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            WriteLine("CSV download", debugOnly: true);
            await CsvInput();
        }
        else
        {
            WriteLine("String download", debugOnly: true);
            await StringInput();
        }

        WriteLine("Got tracks", debugOnly: true);

        if (reverse)
        {
            trackLists.Reverse();
            trackLists = TrackLists.FromFlatList(trackLists.Flattened().Skip(offset).Take(maxTracks).ToList(), aggregate, album);
        }

        PreprocessTrackList(trackLists);

        if (folderName == "")
            folderName = defaultFolderName;
        if (folderName == ".")
            folderName = "";
        folderName = folderName.Replace("\\", "/");
        folderName = String.Join('/', folderName.Split("/").Select(x => x.ReplaceInvalidChars(invalidReplaceStr).Trim()));
        folderName = folderName.Replace('/', Path.DirectorySeparatorChar);

        outputFolder = Path.Combine(parentFolder, folderName);
        m3uFilePath = Path.Combine((m3uFilePath != "" ? m3uFilePath : outputFolder), (folderName == "" ? "playlist" : folderName) + ".m3u8");
        m3uOption = debugDisableDownload ? "none" : m3uOption;
        m3uEditor = new M3UEditor(m3uFilePath, outputFolder, trackLists, offset, m3uOption);

        bool needLogin = !(debugPrintTracks && trackLists.lists.All(x => x.type == TrackLists.ListType.Normal));
        if (needLogin)
        {
            client = new SoulseekClient(new SoulseekClientOptions(listenPort: listenPort));
            if (!useRandomLogin && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)))
                throw new ArgumentException("No soulseek username or password");
            await Login(useRandomLogin);
        }

        bool needUpdate = needLogin;
        if (needUpdate)
        {
            var UpdateTask = Task.Run(() => Update());
            WriteLine("Update started", debugOnly: true);
        }

        searchSemaphore = new RateLimitedSemaphore(searchesPerTime, TimeSpan.FromSeconds(searchResetTime));

        await MainLoop();
        WriteLine("Mainloop done", debugOnly: true);
    }


    static async Task YoutubeInput()
    {
        int max = reverse ? int.MaxValue : maxTracks;
        int off = reverse ? 0 : offset;
        ytUrl = input;
        inputType = InputType.YouTube;

        string name;
        List<Track>? deleted = null;
        List<Track> tracks = new();

        if (getDeleted)
        {
            Console.WriteLine("Getting deleted videos..");
            var archive = new YouTube.YouTubeArchiveRetriever();
            deleted = await archive.RetrieveDeleted(ytUrl, printFailed: deletedOnly);
        }
        if (!deletedOnly)
        {
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
        }
        else
        {
            name = await YouTube.GetPlaylistTitle(ytUrl);
        }
        if (deleted != null)
        {
            tracks.InsertRange(0, deleted);
        }

        YouTube.StopService();
        trackLists.AddEntry(tracks);

        if (album || aggregate)
            trackLists = TrackLists.FromFlatList(trackLists.Flattened().ToList(), aggregate, album);

        defaultFolderName = name.ReplaceInvalidChars(invalidReplaceStr);
    }


    static async Task SpotifyInput()
    {
        int max = reverse ? int.MaxValue : maxTracks;
        int off = reverse ? 0 : offset;

        spotifyUrl = input;
        inputType = InputType.Spotify;

        string? playlistName;
        bool needLogin = spotifyUrl == "spotify-likes" || removeTracksFromSource;
        List<Track> tracks;

        static void readSpotifyCreds()
        {
            Console.Write("Spotify client ID:");
            spotifyId = Console.ReadLine();
            Console.Write("Spotify client secret:");
            spotifySecret = Console.ReadLine();
            Console.WriteLine();
        }

        if (needLogin && (spotifyId == "" || spotifySecret == ""))
        {
            readSpotifyCreds();
        }

        spotifyClient = new Spotify(spotifyId, spotifySecret);
        await spotifyClient.Authorize(needLogin, removeTracksFromSource);

        if (spotifyUrl == "spotify-likes")
        {
            Console.WriteLine("Loading Spotify likes");
            tracks = await spotifyClient.GetLikes(max, off);
            playlistName = "Spotify Likes";

            trackLists.AddEntry(tracks);
            if (album || aggregate)
                trackLists = TrackLists.FromFlatList(trackLists.Flattened().ToList(), aggregate, album);
        }
        else if (spotifyUrl.Contains("/album/"))
        {
            Console.WriteLine("Loading Spotify album");
            (var source, tracks) = await spotifyClient.GetAlbum(spotifyUrl);
            playlistName = source.ToString(noInfo: true);
            trackLists.AddEntry(TrackLists.ListType.Album, source);
            if (minAlbumTrackCount == -1 && setAlbumMinTrackCount && (maxAlbumTrackCount == -1 || maxAlbumTrackCount >= tracks.Count))
            {
                minAlbumTrackCount = tracks.Count;
            }
            if (maxAlbumTrackCount == -1 && setAlbumMaxTrackCount && (minAlbumTrackCount == -1 || minAlbumTrackCount <= tracks.Count))
            {
                maxAlbumTrackCount = tracks.Count;
            }
        }
        else
        {
            try
            {
                Console.WriteLine("Loading Spotify playlist");
                (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(spotifyUrl, max, off);
            }
            catch (SpotifyAPI.Web.APIException)
            {
                if (!needLogin && !spotifyClient.UsedDefaultCredentials)
                {
                    await spotifyClient.Authorize(true, removeTracksFromSource);
                    (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(spotifyUrl, max, off);
                }
                else if (!needLogin)
                {
                    Console.WriteLine("Spotify playlist not found. It may be set to private. Login? [Y/n]");
                    if (Console.ReadLine()?.ToLower().Trim() == "y")
                    {
                        readSpotifyCreds();
                        spotifyClient = new Spotify(spotifyId, spotifySecret);
                        await spotifyClient.Authorize(true, removeTracksFromSource);
                        Console.WriteLine("Loading Spotify playlist");
                        (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(spotifyUrl, max, off);
                    }
                    else
                    {
                        Environment.Exit(0);
                        return;
                    }
                }
                else throw;
            }

            trackLists.AddEntry(tracks);
            if (album || aggregate)
                trackLists = TrackLists.FromFlatList(trackLists.Flattened().ToList(), aggregate, album);
        }


        defaultFolderName = playlistName.ReplaceInvalidChars(invalidReplaceStr);
    }


    static async Task BandcampInput()
    {
        inputType = InputType.Bandcamp;
        bool isAlbum = !input.Contains("/track/");

        var web = new HtmlWeb();
        var doc = await web.LoadFromWebAsync(input);

        var nameSection = doc.DocumentNode.SelectSingleNode("//div[@id='name-section']");
        var name = nameSection.SelectSingleNode(".//h2[@class='trackTitle']").InnerText.UnHtmlString().Trim();

        if (isAlbum)
        {
            var artist = nameSection.SelectSingleNode(".//h3/span/a").InnerText.UnHtmlString().Trim();
            var track = new Track() { Artist=artist, Album=name, IsAlbum=true };
            trackLists.AddEntry(TrackLists.ListType.Album, track);

            if (minAlbumTrackCount == -1 && setAlbumMinTrackCount || maxAlbumTrackCount == -1 && setAlbumMaxTrackCount)
            {
                var trackTable = doc.DocumentNode.SelectSingleNode("//*[@id='track_table']");
                int n = trackTable.SelectNodes(".//tr").Count;
                if (minAlbumTrackCount == -1 && setAlbumMinTrackCount && (maxAlbumTrackCount == -1 || maxAlbumTrackCount >= n))
                {
                    minAlbumTrackCount = n;
                }
                if (maxAlbumTrackCount == -1 && setAlbumMaxTrackCount && (minAlbumTrackCount == -1 || minAlbumTrackCount <= n))
                {
                    maxAlbumTrackCount = n;
                }
            }

            defaultFolderName = track.ToString(true).ReplaceInvalidChars(invalidReplaceStr).Trim();
        }
        else
        {
            var album = nameSection.SelectSingleNode(".//h3[@class='albumTitle']/span/a").InnerText.UnHtmlString().Trim();
            var artist = nameSection.SelectSingleNode(".//h3[@class='albumTitle']/span[last()]/a").InnerText.UnHtmlString().Trim();
            //var timeParts = doc.DocumentNode.SelectSingleNode("//span[@class='time_total']").InnerText.Trim().Split(':');
            var track = new Track() { Artist=artist, Title=name, Album=album };
            trackLists.AddEntry(track);

            defaultFolderName = ".";
        }
    }


    static async Task CsvInput()
    {
        int max = reverse ? int.MaxValue : maxTracks;
        int off = reverse ? 0 : offset;

        csvPath = input;
        inputType = InputType.CSV;

        if (!File.Exists(csvPath))
            throw new FileNotFoundException("CSV file not found");

        var tracks = await ParseCsvIntoTrackInfo(csvPath, artistCol, trackCol, lengthCol, albumCol, descCol, ytIdCol, trackCountCol, timeUnit, ytParse);
        tracks = tracks.Skip(off).Take(max).ToList();
        trackLists = TrackLists.FromFlatList(tracks, aggregate, album);
        defaultFolderName = Path.GetFileNameWithoutExtension(csvPath);
    }


    static async Task StringInput()
    {
        searchStr = input;
        inputType = InputType.String;
        var music = ParseTrackArg(searchStr, album);
        bool isAlbum = false;

        if (album)
        {
            trackLists.AddEntry(TrackLists.ListType.Album, new Track(music) { IsAlbum = true });
        }
        else if (!aggregate && music.Title != "")
        {
            trackLists.AddEntry(music);
        }
        else if (aggregate)
        {
            trackLists.AddEntry(TrackLists.ListType.Aggregate, music);
        }
        else if (music.Title == "" && music.Album != "")
        {
            isAlbum = true;
            music.IsAlbum = true;
            trackLists.AddEntry(TrackLists.ListType.Album, music);
        }
        else
        {
            throw new ArgumentException("Need track title or album");
        }

        if (aggregate || isAlbum || album)
            defaultFolderName = music.ToString(true).ReplaceInvalidChars(invalidReplaceStr).Trim();
        else
            defaultFolderName = ".";
    }


    static async Task MainLoop()
    {
        for (int i = 0; i < trackLists.lists.Count; i++)
        {
            var (list, type, source) = trackLists.lists[i];

            var existing = new List<Track>();
            var notFound = new List<Track>();

            if (skipNotFound)
            { 
                (notFound, source) = SkipNotFound(list[0], source);
                trackLists.SetSource(source, i);
                foreach (var tracks in list.Skip(1)) SkipNotFound(tracks, source);
            }

            if (trackLists.lists.Count > 1 || type != TrackLists.ListType.Normal)
            {
                string sourceStr = type == TrackLists.ListType.Normal ? "" : $": {source.ToString(noInfo: type == TrackLists.ListType.Album)}";
                bool needSearchStr = type == TrackLists.ListType.Normal || skipNotFound && source.TrackState == Track.State.NotFoundLastTime;
                string searchStr = needSearchStr ? "" : $", searching..";
                Console.WriteLine($"{Enum.GetName(typeof(TrackLists.ListType), type)} download{sourceStr}{searchStr}");
            }

            if (!(skipNotFound && source.TrackState == Track.State.NotFoundLastTime))
            {
                if (type == TrackLists.ListType.Normal)
                {
                    // list[0] should already contain the tracks
                }
                else if (type == TrackLists.ListType.Album)
                {
                    list = await GetAlbumDownloads(source);
                    trackLists.SetList(list, i);
                    if (!debugDisableDownload && (list.Count == 0 || list[0].Count == 0))
                    {
                        source = new Track(source) { TrackState = Track.State.Failed, FailureReason = nameof(FailureReasons.NoSuitableFileFound) };
                        trackLists.SetSource(source, i);
                    }
                }
                else if (type == TrackLists.ListType.Aggregate)
                {
                    list[0] = await GetUniqueRelatedTracks(source);
                    if (list[0].Count == 0)
                    {
                        source = new Track(source) { TrackState = Track.State.Failed, FailureReason = nameof(FailureReasons.NoSuitableFileFound) };
                        trackLists.SetSource(source, i);
                    }
                }
            }

            if (skipExisting && list != null)
            {
                existing = DoSkipExisting(list[0], print: i==0, useCache: trackLists.lists.Count > 1);
                foreach (var tracks in list.Skip(1)) DoSkipExisting(tracks, false, useCache: trackLists.lists.Count > 1);
            }

            m3uEditor.Update();

            if (list != null && (!interactiveMode || debugPrintTracks))
            {
                PrintTracksTbd(list[0].Where(t => t.TrackState == Track.State.Initial).ToList(), existing, notFound, type);
            }

            if (debugPrintTracks || list?.Count == 0 || list?[0].Count == 0)
            {
                if (i < trackLists.lists.Count - 1) Console.WriteLine();
                continue;
            }

            if (type == TrackLists.ListType.Normal)
            {
                await TracksDownloadNormal(list[0]);
            }
            else if (type == TrackLists.ListType.Album && list != null)
            {
                await TracksDownloadAlbum(list, albumArtOnly);
            }
            else if (type == TrackLists.ListType.Aggregate)
            {
                await TracksDownloadNormal(list[0]);
            }

            if (i < trackLists.lists.Count - 1)
            {
                Console.WriteLine();
            }
        }

        if (!debugDisableDownload && trackLists.CombinedTrackList().Count > 1)
        {
            PrintComplete();
        }
    }


    static void PreprocessTrackList(TrackLists trackLists)
    {
        for (int k = 0; k < trackLists.lists.Count; k++)
        {
            var (list, type, source) = trackLists.lists[k];
            trackLists.lists[k] = (list, type, PreprocessTrack(source));
            foreach (var ls in list)
            {
                for (int i = 0; i < ls.Count; i++)
                {
                    ls[i] = PreprocessTrack(ls[i]);
                }
            }
        }
    }


    static Track PreprocessTrack(Track track)
    {
        if (removeFt)
        {
            track.Title = track.Title.RemoveFt();
            track.Artist = track.Artist.RemoveFt();
        }
        if (removeBrackets)
        {
            track.Title = track.Title.RemoveSquareBrackets();
        }
        if (regexToReplace.Title + regexToReplace.Artist + regexToReplace.Album != "")
        {
            track.Title = Regex.Replace(track.Title, regexToReplace.Title, regexReplaceBy.Title);
            track.Artist = Regex.Replace(track.Artist, regexToReplace.Artist, regexReplaceBy.Artist);
            track.Album = Regex.Replace(track.Album, regexToReplace.Album, regexReplaceBy.Album);
        }
        if (artistMaybeWrong)
        {
            track.ArtistMaybeWrong = true;
        }

        track.Artist = track.Artist.Trim();
        track.Album = track.Album.Trim();
        track.Title = track.Title.Trim();

        return track;
    }


    static void PrintComplete()
    {
        var ls = trackLists.CombinedTrackList();
        int successes = 0, fails = 0;
        foreach (var x in ls)
        {
            if (x.TrackState == Track.State.Downloaded)
                successes++;
            else if (x.TrackState == Track.State.Failed)
                fails++;
        }
        if (successes + fails > 1)
            Console.WriteLine($"\nCompleted: {successes} succeeded, {fails} failed.");
    }


    static void PrintTracksTbd(List<Track> tracks, List<Track> existing, List<Track> notFound, TrackLists.ListType type)
    {
        if (type == TrackLists.ListType.Normal && !debugPrintTracks && tracks.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist != "" && notFoundLastTime != "" ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist + notFoundLastTime != "" ? $" ({alreadyExist}{notFoundLastTime})" : "";

        Console.WriteLine($"Downloading {tracks.Count(x => !x.IsNotAudio)} tracks{skippedTracks}");

        if (type != TrackLists.ListType.Album)
        {
            if (tracks.Count > 0)
            {
                bool showAll = type != TrackLists.ListType.Normal || debugPrintTracks;
                PrintTracks(tracks, showAll ? int.MaxValue : 10, debugPrintTracksFull, infoFirst: debugPrintTracks);
                if (debugPrintTracksFull && (existing.Count > 0 || notFound.Count > 0))
                    Console.WriteLine("\n-----------------------------------------------\n");
            }
        }
        else if (!interactiveMode && tracks.Count > 0 && !tracks[0].Downloads.IsEmpty)
        {
            var response = tracks[0].Downloads.First().Value.Item1;
            string userInfo = $"{response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)";
            var (parents, props) = FolderInfo(tracks.SelectMany(x => x.Downloads.Select(d => d.Value.Item2)));

            Console.WriteLine();
            WriteLine($"User  : {userInfo}\nFolder: {parents}\nProps : {props}", ConsoleColor.White);
            PrintTracks(tracks.Where(t => t.TrackState == Track.State.Initial).ToList(), pathsOnly: true, showAncestors: true, showUser: false);
            Console.WriteLine();
        }

        if (debugPrintTracks)
        {
            if (existing.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks already exist:");
                PrintTracks(existing, fullInfo: debugPrintTracksFull, infoFirst: debugPrintTracks);
            }
            if (notFound.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks were not found during the last run:");
                PrintTracks(notFound, fullInfo: debugPrintTracksFull, infoFirst: debugPrintTracks);
            }
        }
    }


    static List<Track> DoSkipExisting(List<Track> tracks, bool print, bool useCache)
    {
        var existing = new Dictionary<Track, string>();

        if (skipMode == SkipMode.M3u)
        {
            existing = SkipExistingM3u(tracks);
        }
        else
        {
            if (!(musicDir != "" && outputFolder.StartsWith(musicDir, StringComparison.OrdinalIgnoreCase)) && System.IO.Directory.Exists(outputFolder))
            {
                var d = SkipExisting(tracks, outputFolder, necessaryCond, skipMode, useCache);
                d.ToList().ForEach(x => existing.TryAdd(x.Key, x.Value));
            }
            if (musicDir != "" && System.IO.Directory.Exists(musicDir))
            {
                if (print) Console.WriteLine($"Checking if tracks exist in library..");
                var d = SkipExisting(tracks, musicDir, necessaryCond, skipMode, useCache);
                d.ToList().ForEach(x => existing.TryAdd(x.Key, x.Value));
            }
            else if (musicDir != "" && !System.IO.Directory.Exists(musicDir))
                if (print) Console.WriteLine($"Music dir does not exist: {musicDir}");
        }

        return existing.Select(x => x.Key).ToList();
    }


    static (List<Track>, Track) SkipNotFound(List<Track> tracks, Track source)
    {
        List<Track> notFound = new List<Track>();
        if (m3uEditor.HasFail(source, out string? reason) && reason == nameof(FailureReasons.NoSuitableFileFound))
        {
            notFound.Add(source);
            source = new Track(source) { TrackState = Track.State.NotFoundLastTime };
        }
        for (int i = tracks.Count - 1; i >= 0; i--)
        {
            if (m3uEditor.HasFail(tracks[i], out reason) && reason == nameof(FailureReasons.NoSuitableFileFound))
            {
                notFound.Add(tracks[i]);
                tracks[i] = new Track(tracks[i]) { TrackState = Track.State.NotFoundLastTime };
            }
        }
        return (notFound, source);
    }


    static async Task TracksDownloadNormal(List<Track> tracks)
    {
        SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);

        var copy = new List<Track>(tracks);
        var downloadTasks = copy.Select(async (track, index) =>
        {
            if (track.TrackState == Track.State.Exists || track.TrackState == Track.State.NotFoundLastTime)
                return;

            await semaphore.WaitAsync();

            int tries = unknownErrorRetries;
            string savedFilePath = "";

            while (tries > 0)
            {
                await WaitForLogin();

                try
                {
                    WriteLine($"Search and download {track}", debugOnly: true);
                    savedFilePath = await SearchAndDownload(track);
                }
                catch (Exception ex)
                {
                    WriteLine($"Exception thrown: {ex}", debugOnly: true);
                    if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                    {
                        continue;
                    }
                    else if (ex is SearchAndDownloadException)
                    {
                        lock (trackLists) { tracks[index] = new Track(track) { TrackState = Track.State.Failed, FailureReason = ex.Message }; }
                    }
                    else
                    {
                        WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                        tries--;
                        continue;
                    }
                }

                break;
            }

            if (savedFilePath != "")
            {
                lock (trackLists) { tracks[index] = new Track(track) { TrackState = Track.State.Downloaded, DownloadPath = savedFilePath }; }

                if (removeTracksFromSource)
                {
                    try
                    {
                        await RemoveTrackFromSource(track);
                    }
                    catch (Exception ex) 
                    {
                        WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                    }
                }
            }

            m3uEditor.Update();

            if (onComplete != "")
            {
                OnComplete(onComplete, tracks[index]);
            }

            semaphore.Release();
        });

        await Task.WhenAll(downloadTasks);
    }


    static async Task TracksDownloadAlbum(List<List<Track>> list, bool imagesOnly) // bad
    {
        var dlFiles = new ConcurrentDictionary<string, bool>();
        var dlAdditionalImages = new ConcurrentDictionary<string, bool>();
        var tracks = new List<Track>();
        bool downloadingImages = false;
        bool albumDlFailed = false;
        var listRef = list; 

        void prepareImageDownload()
        {
            var albumArtList = list.Select(tracks => tracks.Where(t => Utils.IsImageFile(t.Downloads.First().Value.Item2.Filename))).Where(tracks => tracks.Any());
            if (albumArtOption == "largest")
            {
                list = albumArtList // shitty shortcut
                    .OrderByDescending(tracks => tracks.Select(t => t.Downloads.First().Value.Item2.Size).Max() / 1024 / 100)
                    .ThenByDescending(tracks => tracks.First().Downloads.First().Value.Item1.UploadSpeed / 1024 / 300)
                    .ThenByDescending(tracks => tracks.Select(t => t.Downloads.First().Value.Item2.Size).Sum() / 1024 / 100)
                    .Select(x => x.ToList()).ToList();
            }
            else if (albumArtOption == "most")
            {
                list = albumArtList // shitty shortcut
                    .OrderByDescending(tracks => tracks.Count())
                    .ThenByDescending(tracks => tracks.First().Downloads.First().Value.Item1.UploadSpeed / 1024 / 300)
                    .ThenByDescending(tracks => tracks.Select(t => t.Downloads.First().Value.Item2.Size).Sum() / 1024 / 100)
                    .Select(x => x.ToList()).ToList();
            }
            downloadingImages = true;
        }

        if (imagesOnly)
        {
            prepareImageDownload();
        }

        int idx = -1;
        while (list.Count > 0)
        {
            idx++;
            albumDlFailed = false;
            tracks = interactiveMode ? InteractiveModeAlbum(list) : list[0];

            if (!downloadingImages && tracks.All(t => t.TrackState != Track.State.Initial || (!interactiveMode && t.IsNotAudio)))
                goto imgDl;
            if (list.Count <= 1 && tracks.All(t => t.TrackState != Track.State.Initial))
                goto imgDl;

            mainLoopCts = new CancellationTokenSource();
            albumCommonPath = Utils.GreatestCommonPath(tracks.SelectMany(x => x.Downloads.Select(y => y.Value.Item2.Filename)), dirsep: '\\');
            SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);
            var copy = new List<Track>(tracks);

            if (!interactiveMode && idx > 0 && tracks.Count > 0 && !tracks[0].Downloads.IsEmpty)
            {
                var response = tracks[0].Downloads.First().Value.Item1;
                string userInfo = $"{response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)";
                var (parents, props) = FolderInfo(tracks.SelectMany(x => x.Downloads.Select(d => d.Value.Item2)));

                Console.WriteLine();
                WriteLine($"User  : {userInfo}\nFolder: {parents}\nProps : {props}", ConsoleColor.White);
                PrintTracks(tracks.Where(t => t.TrackState == Track.State.Initial).ToList(), pathsOnly: true, showAncestors: true, showUser: false);
                Console.WriteLine();
            }

            try
            {
                var downloadTasks = copy.Select(async (track, index) =>
                {
                    if (track.TrackState != Track.State.Initial)
                        return;

                    await semaphore.WaitAsync(mainLoopCts.Token);
                    
                    int tries = unknownErrorRetries;
                    string savedFilePath = "";

                    while (tries > 0)
                    {
                        await WaitForLogin();
                        mainLoopCts.Token.ThrowIfCancellationRequested();

                        try
                        {
                            savedFilePath = await SearchAndDownload(track);
                        }
                        catch (Exception ex)
                        {
                            if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                            {
                                continue;
                            }
                            else if (ex is SearchAndDownloadException)
                            {
                                lock (trackLists)
                                {
                                    tracks[index] = new Track(track) { TrackState = Track.State.Failed, FailureReason = ex.Message };
                                    if (downloadingImages)
                                        ReplaceTrack(listRef, track, tracks[index]); // shitty shortcut
                                }

                                if (!albumIgnoreFails)
                                {
                                    mainLoopCts.Cancel();
                                    foreach (var (key, dl) in downloads)
                                    {
                                        lock (dl)
                                        {
                                            dl.cts.Cancel();
                                            if (File.Exists(dl.savePath)) File.Delete(dl.savePath);
                                            downloads.TryRemove(key, out _);
                                        }
                                    }
                                    throw new OperationCanceledException();
                                }
                            }
                            else
                            {
                                WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                                tries--;
                                continue;
                            }
                        }

                        break;
                    } 

                    if (savedFilePath != "")
                    {
                        dlFiles.TryAdd(savedFilePath, true);

                        lock (trackLists)
                        {
                            tracks[index] = new Track(track) { TrackState = Track.State.Downloaded, DownloadPath = savedFilePath };
                            if (downloadingImages)
                            {
                                dlAdditionalImages.TryAdd(savedFilePath, true);
                                ReplaceTrack(listRef, track, tracks[index]); // shitty shortcut
                            }
                        }
                    }

                    if (onComplete != "")
                    {
                        OnComplete(onComplete, tracks[index]);
                    }

                    semaphore.Release();
                });

                await Task.WhenAll(downloadTasks);
            }
            catch (OperationCanceledException)
            {
                if (!albumIgnoreFails)
                {
                    if (!downloadingImages)
                        albumDlFailed = true;
                    var setToClear = downloadingImages ? dlAdditionalImages : dlFiles;
                    foreach (var path in setToClear.Keys)
                        if (File.Exists(path)) File.Delete(path);
                    setToClear.Clear();
                    list.RemoveAt(0);
                    continue;
                }
            }

        imgDl:
            if (!downloadingImages && !albumDlFailed && albumArtOption != "")
            {
                prepareImageDownload();
                bool needImageDl = true;

                if (!interactiveMode && list.Count > 0)
                {
                    if (albumArtOption == "most")
                    {
                        needImageDl = dlFiles.Keys.Count(x => Utils.IsImageFile(x) && File.Exists(x)) < list[0].Count;
                    }
                    else if (albumArtOption == "largest")
                    {
                        long curMax = dlFiles.Keys.Where(x => Utils.IsImageFile(x) && File.Exists(x)).Max(x => new FileInfo(x).Length);
                        needImageDl = curMax < list[0].Max(t => t.Downloads.First().Value.Item2.Size) - 1024 * 50;
                    }
                }
                
                if (needImageDl)
                {
                    continue;
                }
            }

            break;
        }

        ApplyNamingFormatsNonAudio(listRef);
        m3uEditor.Update();
        albumCommonPath = "";
    }


    static void ReplaceTrack(List<List<Track>> list, Track oldTrack, Track newTrack) // shitty shortcut
    {
        foreach (var sublist in list)
        {
            for (int i = 0; i < sublist.Count; i++)
            {
                if (sublist[i].Equals(oldTrack))
                {
                    sublist[i] = newTrack;
                    return;
                }
            }
        }
    }


    static List<Track> InteractiveModeAlbum(List<List<Track>> list)
    {
        int aidx = 0;
        static string interactiveModeLoop()
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
        }
        Console.WriteLine($"\nPrev [Up/p] / Next [Down/n] / Accept [Enter] / Accept & Exit Interactive Mode [q] / Cancel [Esc/c]");
        while (true)
        {
            Console.WriteLine();
            var tracks = list[aidx];
            var response = tracks[0].Downloads.First().Value.Item1;

            string userInfo = $"{response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)";
            var (parents, props) = FolderInfo(tracks.SelectMany(x => x.Downloads.Select(d => d.Value.Item2)));

            WriteLine($"[{aidx + 1} / {list.Count}]", ConsoleColor.DarkGray);
            WriteLine($"User  : {userInfo}\nFolder: {parents}\nProps : {props}", ConsoleColor.White);
            PrintTracks(tracks.Where(t => t.TrackState == Track.State.Initial).ToList(), pathsOnly: true, showAncestors: true, showUser: false);

            string userInput = interactiveModeLoop();
            switch (userInput)
            {
                case "p":
                    aidx = (aidx + list.Count - 1) % list.Count;
                    break;
                case "n":
                    aidx = (aidx + 1) % list.Count;
                    break;
                case "c":
                    return new List<Track>();
                case "q":
                    interactiveMode = false;
                    list.RemoveAt(aidx);
                    return tracks;
                case "":
                    return tracks;
            }
        }
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

        var mostCommonExtension = files.GroupBy(f => GetExtensionSlsk(f.Filename))
            .OrderByDescending(g => Utils.IsMusicExtension(g.Key)).ThenByDescending(g => g.Count()).First().Key;

        res = $"[{mostCommonExtension.ToUpper()} / {totalLengthFormatted}";

        if (modeSampleRate.HasValue)
            res += $" / {(modeSampleRate.Value/1000.0).Normalize()} kHz";

        if (meanBitrate.HasValue)
            res += $" / {(int)meanBitrate.Value} kbps";

        res += $" / {totalFileSizeInMB:F2} MB]";

        string gcp;

        if (files.Count() > 1)
            gcp = Utils.GreatestCommonPath(files.Select(x => x.Filename), '\\').TrimEnd('\\');
        else
            gcp = GetDirectoryNameSlsk(files.First().Filename);

        var discPattern = new Regex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$");
        int lastIndex = gcp.LastIndexOf('\\');
        if (lastIndex != -1)
        {
            int secondLastIndex = gcp.LastIndexOf('\\', lastIndex - 1);
            gcp = secondLastIndex == -1 ? gcp.Substring(lastIndex + 1) : gcp.Substring(secondLastIndex + 1);
        }

        return (gcp, res);
    }


    static async Task RemoveTrackFromSource(Track track)
    {
        if (inputType == InputType.Spotify && track.URI != "")
        {
            await spotifyClient.RemoveTrackFromPlaylist(playlistUri, track.URI);
        }
        else if (inputType == InputType.CSV && track.CsvRow != -1)
        {
            lock (csvLock)
            {
                string[] lines = File.ReadAllLines(csvPath, System.Text.Encoding.UTF8);

                if (lines.Length > track.CsvRow)
                {
                    lines[track.CsvRow] = new string(',', Math.Max(0, csvColumnCount - 1));
                    File.WriteAllLines(csvPath, lines, System.Text.Encoding.UTF8);
                }
            }
        }
    }


    static async Task Login(bool random = false, int tries = 3)
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
                if (!(e is Soulseek.AddressException || e is System.TimeoutException) && --tries == 0)
                    throw;
            }
            await Task.Delay(500);
            WriteLine($"Retry login {user}", debugOnly: true);
        }

        WriteLine($"Logged in {user}", debugOnly: true);
    }


    static async Task<string> SearchAndDownload(Track track)
    {
        Console.ResetColor();
        ProgressBar? progress = GetProgressBar(displayStyle);
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
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));

                if (fastSearch && !debugDisableDownload && userSuccessCount.GetValueOrDefault(r.Username, 0) > deprioritizeOn)
                {
                    var f = r.Files.First();

                    if (r.HasFreeUploadSlot && r.UploadSpeed/1024.0/1024.0 >= fastSearchMinUpSpeed 
                        && BracketCheck(track, InferTrack(f.Filename, track)) && preferredCond.FileSatisfies(f, track, r))
                    {
                        fsResults.TryAdd(r.Username + "\\" + f.Filename, (r, f));
                        if (Interlocked.Exchange(ref fsResultsStarted, 1) == 0)
                        {
                            Task.Delay(fastSearchDelay).ContinueWith(tt => fastSearchDownload());
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
        }

        void onSearch() => RefreshOrPrint(progress, 0, $"Searching: {track}", true);
        await RunSearches(track, results, getSearchOptions, responseHandler, cts.Token, onSearch);

        searches.TryRemove(track, out _);
        searchEnded = true;
        lock (fsDownloadLock) { }

        if (downloading==0 && results.IsEmpty && !useYtdlp)
        {
            notFound = true;
        }
        else if (downloading==1)
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

        if (debugDisableDownload && results.IsEmpty)
        {
            WriteLine($"No results", ConsoleColor.Yellow);
            return "";
        }
        else if (downloading==0 && !results.IsEmpty)
        {
            var random = new Random();
            var orderedResults = OrderedResults(results, track, true);

            if (debugDisableDownload)
            {
                int count = 0;
                Console.WriteLine();
                foreach (var (response, file) in orderedResults) {
                    Console.WriteLine(DisplayString(track, file, response,
                        printResultsFull ? necessaryCond : null, printResultsFull ? preferredCond : null, 
                        fullpath: printResultsFull, infoFirst: true, showSpeed: printResultsFull));
                    count += 1;
                }
                WriteLine($"Total: {count}\n", ConsoleColor.Yellow);
                return "";
            }

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
                    if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                        throw;
                    userSuccessCount.AddOrUpdate(response.Username, -1, (k, v) => v - 1);
                    if (--maxRetriesPerTrack <= 0)
                    {
                        RefreshOrPrint(progress, 0, $"Out of download retries: {track}", true);
                        WriteLine("Last error was: " + e.Message, ConsoleColor.DarkYellow, true);
                        throw new SearchAndDownloadException(nameof(FailureReasons.OutOfDownloadRetries));
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
                if (fr != default && userSuccessCount.GetValueOrDefault(fr.response.Username, 0) > ignoreOn)
                {
                    success = await process(fr.response, fr.file);
                }

                if (!success && fr != default)
                {
                    foreach (var (response, file) in orderedResults.Skip(2))
                    {
                        if (userSuccessCount.GetValueOrDefault(response.Username, 0) <= ignoreOn)
                            continue;
                        success = await process(response, file);
                        if (success) break;
                    }
                }
            }
        }

        if (downloading == 0 && useYtdlp)
        {
            notFound = false;
            try 
            {
                RefreshOrPrint(progress, 0, $"yt-dlp search: {track}", true);
                var ytResults = await YouTube.YtdlpSearch(track);

                if (ytResults.Count > 0)
                {
                    foreach (var (length, id, title) in ytResults)
                    {
                        if (necessaryCond.LengthToleranceSatisfies(length, track.Length))
                        {
                            string saveFilePathNoExt = GetSavePathNoExt(title);
                            downloading = 1;
                            RefreshOrPrint(progress, 0, $"yt-dlp download: {track}", true);
                            saveFilePath = await YouTube.YtdlpDownload(id, saveFilePathNoExt, ytdlpArgument);
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
                throw new SearchAndDownloadException(nameof(FailureReasons.NoSuitableFileFound));
            }
        }

        if (downloading == 0)
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

        if (nameFormat != "")
            saveFilePath = ApplyNamingFormat(saveFilePath, track);

        return saveFilePath;
    }


    public class SearchAndDownloadException : Exception
    {
        public SearchAndDownloadException(string text = "") : base(text) { }
    }


    static async Task<List<List<Track>>> GetAlbumDownloads(Track track) // slow
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        SearchOptions getSearchOptions(int timeout, FileConditions nec, FileConditions prf) =>
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
        void handler(SlResponse r)
        {
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

        if (debugDisableDownload && !debugPrintTracks)
        {
            Console.WriteLine();
            foreach (var (response, file) in orderedResults)
            {
                Console.WriteLine(DisplayString(track, file, response,
                        printResultsFull ? necessaryCond : null, printResultsFull ? preferredCond : null,
                        fullpath: printResultsFull, infoFirst: true, showSpeed: printResultsFull));
            }
            WriteLine($"Total: {orderedResults.Count()}\n", ConsoleColor.Yellow);
            return default;
        }

        var groupedLists = orderedResults.GroupBy(x => fullPath(x).Substring(0, fullPath(x).LastIndexOf('\\')));
        var musicFolders = new List<(string Key, List<(SlResponse response, SlFile file)>)>();
        var nonMusicFolders = new List<IGrouping<string, (SlResponse response, SlFile file)>>();

        foreach (var group in groupedLists)
        {
            if (group.Any(x => Utils.IsMusicFile(x.file.Filename)))
                musicFolders.Add((group.Key, group.ToList()));
            else
                nonMusicFolders.Add(group);
        }

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
                string x = nonMusicFolder.Key.TrimEnd('\\') + '\\';
                if (x.StartsWith(musicFolder.Key.TrimEnd('\\') + '\\'))
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

        int min, max;
        if (track.MinAlbumTrackCount != -1 || track.MaxAlbumTrackCount != -1)
        {
            min = track.MinAlbumTrackCount;
            max = track.MaxAlbumTrackCount;
        }
        else
        {
            min = minAlbumTrackCount;
            max = maxAlbumTrackCount;
        }

        bool countIsGood(int count) => count >= min && (max == -1 || count <= max);

        var result = musicFolders
            .Where(x => countIsGood(x.Item2.Count(rf => Utils.IsMusicFile(rf.file.Filename))))
            .Select(ls => ls.Item2.Select(x => {
                var t = new Track
                {
                    Artist = track.Artist,
                    Album = track.Album,
                    IsNotAudio = !Utils.IsMusicFile(x.file.Filename),
                    Downloads = new ConcurrentDictionary<string, (SlResponse, SlFile file)>(
                        new Dictionary<string, (SlResponse response, SlFile file)> { { x.response.Username + "\\" + x.file.Filename, x } })
                };
                return skipExisting ? InferTrack(x.file.Filename, t) : t;
            })
            .OrderBy(t => t.IsNotAudio)
            .ThenBy(t => t.Downloads.First().Value.Item2.Filename)
            .ToList()).Where(ls => ls.Count > 0).ToList();

        if (result.Count == 0)
            result.Add(new List<Track>()); 

        return result;
    }


    static async Task<List<Track>> GetUniqueRelatedTracks(Track track)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        SearchOptions getSearchOptions(int timeout, FileConditions nec, FileConditions prf) =>
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: removeSingleCharacterSearchTerms,
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

        if (!relax)
        {
            equivalentFiles = equivalentFiles
                .Where(x => FileConditions.StrictString(x.Item1.Title, track.Title, ignoreCase: true)
                        && (FileConditions.StrictString(x.Item1.Artist, track.Artist, ignoreCase: true, boundarySkipWs: false) 
                            || FileConditions.StrictString(x.Item1.Title, track.Artist, ignoreCase: true, boundarySkipWs: false)
                                && x.Item1.Title.ContainsInBrackets(track.Artist, ignoreCase: true)))
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


    static IOrderedEnumerable<(Track, IEnumerable<(SlResponse response, SlFile file)>)> EquivalentFiles(Track track, 
        IEnumerable<(SlResponse, SlFile)> fileResponses, int minShares=-1)
    {
        if (minShares == -1)
            minShares = minUsersAggregate;

        Track inferTrack((SearchResponse r, Soulseek.File f) x)
        {
            Track t = track;
            t.Length = x.f.Length ?? -1;
            return InferTrack(x.f.Filename, t);
        }

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
                        if (necessaryCond.LengthTolerance == -1 || Math.Abs(l1 - l2) <= necessaryCond.LengthTolerance)
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

                if (noLengthGroup.Any())
                {
                    if (groups.Count > 0 && !preferredCond.AcceptNoLength)
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
        Track track, bool useInfer=false, bool useLevenshtein=true, bool albumMode=false)
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
                .Where(x => userSuccessCount.GetValueOrDefault(x.response.Username, 0) > ignoreOn)
                .OrderByDescending(x => userSuccessCount.GetValueOrDefault(x.response.Username, 0) > deprioritizeOn)
                .ThenByDescending(x => necessaryCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => preferredCond.BannedUsersSatisfies(x.response))
                .ThenByDescending(x => (x.file.Length != null && x.file.Length > 0) || preferredCond.AcceptNoLength)
                .ThenByDescending(x => !useBracketCheck || BracketCheck(track, inferredTrack(x).Item1)) // deprioritize result if it contains '(' or '[' and the title does not (avoid remixes)
                .ThenByDescending(x => preferredCond.StrictTitleSatisfies(x.file.Filename, track.Title))
                .ThenByDescending(x => preferredCond.StrictArtistSatisfies(x.file.Filename, track.Title))
                .ThenByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.FormatSatisfies(x.file.Filename))
                .ThenByDescending(x => preferredCond.StrictAlbumSatisfies(x.file.Filename, track.Album))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.SampleRateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.BitDepthSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 1024 / 650)
                .ThenByDescending(x => albumMode || FileConditions.StrictString(x.file.Filename, track.Title))
                .ThenByDescending(x => !albumMode || FileConditions.StrictString(GetDirectoryNameSlsk(x.file.Filename), track.Album))
                .ThenByDescending(x => FileConditions.StrictString(x.file.Filename, track.Artist, boundarySkipWs: false))
                .ThenByDescending(x => useInfer ? inferredTrack(x).Item2 : 0) // sorts by the number of occurences of this track
                .ThenByDescending(x => x.response.UploadSpeed / 1024 / 350)
                .ThenByDescending(x => (x.file.BitRate ?? 0) / 80)
                .ThenByDescending(x => useLevenshtein ? levenshtein(x) / 5 : 0) // sorts by the distance between the track title and the inferred title of the search result
                .ThenByDescending(x => random.Next());
    }


    static bool BracketCheck(Track track, Track other)
    {
        string t1 = track.Title.RemoveFt().Replace('[', '(');
        if (t1.Contains('('))
            return true;

        string t2 = other.Title.RemoveFt().Replace('[', '(');
        if (!t2.Contains('('))
            return true;

        return false;
    }


    static async Task RunSearches(Track track, SlDictionary results, Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions, 
        Action<SearchResponse> responseHandler, CancellationToken ct, Action? onSearch = null)
    {
        bool artist = track.Artist != "";
        bool title = track.Title != "";
        bool album = track.Album != "";

        string search = GetSearchString(track);
        var searchTasks = new List<Task>();

        var defaultSearchOpts = getSearchOptions(searchTimeout, necessaryCond, preferredCond);
        searchTasks.Add(Search(search, defaultSearchOpts, responseHandler, ct, onSearch));

        if (search.RemoveDiacriticsIfExist(out string noDiacrSearch) && !track.ArtistMaybeWrong)
            searchTasks.Add(Search(noDiacrSearch, defaultSearchOpts, responseHandler, ct, onSearch));

        await Task.WhenAll(searchTasks);

        if (results.IsEmpty && track.ArtistMaybeWrong && title)
        {
            var cond = new FileConditions(necessaryCond);
            var infTrack = InferTrack(track.Title, new Track());
            cond.StrictTitle = infTrack.Title == track.Title;
            cond.StrictArtist = false;
            var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
            searchTasks.Add(Search($"{infTrack.Artist} {infTrack.Title}", opts, responseHandler, ct, onSearch));
        }

        if (desperateSearch)
        {
            await Task.WhenAll(searchTasks);

            if (results.IsEmpty && !track.ArtistMaybeWrong)
            {
                if (artist && album && title)
                {
                    var cond = new FileConditions(necessaryCond)
                    {
                        StrictTitle = true,
                        StrictAlbum = true
                    };
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track.Artist} {track.Album}", opts, responseHandler, ct, onSearch));
                }
                if (artist && title && track.Length != -1 && necessaryCond.LengthTolerance != -1)
                {
                    var cond = new FileConditions(necessaryCond)
                    {
                        LengthTolerance = -1,
                        StrictTitle = true,
                        StrictArtist = true
                    };
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track.Artist} {track.Title}", opts, responseHandler, ct, onSearch));
                }
            }

            await Task.WhenAll(searchTasks);

            if (results.IsEmpty)
            {
                var track2 = track.ArtistMaybeWrong ? InferTrack(track.Title, new Track()) : track;

                if (track.Album.Length > 3 && album)
                {
                    var cond = new FileConditions(necessaryCond)
                    {
                        StrictAlbum = true,
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track.Album}", opts, responseHandler, ct, onSearch));
                }
                if (track2.Title.Length > 3 && artist)
                {
                    var cond = new FileConditions(necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track2.Title}", opts, responseHandler, ct, onSearch));
                }
                if (track2.Artist.Length > 3 && title)
                {
                    var cond = new FileConditions(necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track2.Artist}", opts, responseHandler, ct, onSearch));
                }
            }
        }

        await Task.WhenAll(searchTasks);
    }


    static async Task Search(string search, SearchOptions opts, Action<SearchResponse> rHandler, CancellationToken ct, Action? onSearch = null)
    {
        await searchSemaphore.WaitAsync();
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
        if (track.Title != "")
            return (track.Artist + " " + track.Title).Trim();
        else if (track.Album != "")
            return (track.Artist + " " + track.Album).Trim();
        return track.Artist.Trim();
    }


    public static string CleanSearchString(string str)
    {
        string old;
        if (!noRemoveSpecialChars)
        {
            old = str;
            str = str.ReplaceSpecialChars(" ").Trim().RemoveConsecutiveWs();
            if (str == "") str = old;
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


    public static Track InferTrack(string filename, Track defaultTrack)
    {
        Track t = new Track(defaultTrack);
        filename = GetFileNameWithoutExtSlsk(filename).Replace(" — ", " - ").Replace("_", " ").Trim().RemoveConsecutiveWs();

        var trackNumStart = new Regex(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)");
        //var trackNumMiddle = new Regex(@"\s+-\s+(\d{2,3})(?: -|\.|)\s+|\s+-(\d{2,3})-\s+");
        var trackNumMiddle = new Regex(@"(?<= - )((\d-)?\d{2,3}|\d{2,3}\.?)\s+");
        var trackNumMiddleAlt = new Regex(@"\s+-(\d{2,3})-\s+");

        if (trackNumStart.IsMatch(filename))
        {
            filename = trackNumStart.Replace(filename, "", 1).Trim();
            if (filename.StartsWith("- "))
                filename = filename.Substring(2).Trim();
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
            bool hasTitle = tname != "" && parts[2].ContainsIgnoreCase(tname);
            if (hasTitle)
                t.Title = realParts[2];

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

            if (aname != "")
            {
                var s = parts.Select((p, i) => (p, i)).Where(x => x.p.ContainsIgnoreCase(aname));
                if (s.Any())
                {
                    artistPos = s.MinBy(x => Math.Abs(x.p.Length - aname.Length)).i;
                    if (artistPos != -1)
                        t.Artist = parts[artistPos];
                }
            }
            if (tname != "")
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

        if (t.Title == "")
        {
            t.Title = fname;
            t.ArtistMaybeWrong = true;
        }
        else if (t.Artist != "" && !t.Title.ContainsIgnoreCase(defaultTrack.Title) && !t.Artist.ContainsIgnoreCase(defaultTrack.Artist))
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


    static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, Track track, ProgressBar progress, CancellationTokenSource? searchCts=null)
    {
        if (debugDisableDownload)
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
            downloads.TryAdd(file.Filename, new DownloadWrapper(origPath, response, file, track, cts, progress));
            await client.DownloadAsync(response.Username, file.Filename, () => Task.FromResult((Stream)outputStream), file.Size, options: transferOptions, cancellationToken: cts.Token);
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


    static async Task Update()
    {
        while (true)
        {
            if (!skipUpdate)
            {
                try
                {
                    if (client.State.HasFlag(SoulseekClientStates.LoggedIn))
                    {
                        foreach (var (key, val) in searches)
                        {
                            if (val == null)
                                searches.TryRemove(key, out _); // reminder: removing from a dict in a foreach is allowed in newer .net versions
                        }

                        foreach (var (key, val) in downloads)
                        {
                            if (val != null)
                            {
                                lock (val)
                                {
                                    if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > downloadMaxStaleTime)
                                    {
                                        val.stalled = true;
                                        val.UpdateText();

                                        try { val.cts.Cancel(); } catch { }
                                        downloads.TryRemove(key, out _);
                                    }
                                    else
                                    {
                                        val.UpdateText();
                                    }
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


    static void OnComplete(string onComplete, Track track)
    {
        if (onComplete == "")
            return;
        else if (onComplete.Length > 2 && onComplete[0].IsDigit() && onComplete[1] == ':')
        {
            if ((int)track.TrackState != int.Parse(onComplete[0].ToString()))
                return;
            onComplete = onComplete.Substring(2);
        }

        Process process = new Process();
        ProcessStartInfo startInfo = new ProcessStartInfo();

        onComplete = onComplete.Replace("{title}", track.Title)
                           .Replace("{artist}", track.Artist)
                           .Replace("{album}", track.Album)
                           .Replace("{uri}", track.URI)
                           .Replace("{length}", track.Length.ToString())
                           .Replace("{artist-maybe-wrong}", track.ArtistMaybeWrong.ToString())
                           .Replace("{is-album}", track.IsAlbum.ToString())
                           .Replace("{is-not-audio}", track.IsNotAudio.ToString())
                           .Replace("{failure-reason}", track.FailureReason)
                           .Replace("{path}", track.DownloadPath)
                           .Replace("{state}", track.TrackState.ToString())
                           .Trim();

        if (onComplete[0] == '"')
        {
            int e = onComplete.IndexOf('"', 1);
            if (e > 1)
            {
                startInfo.FileName = onComplete.Substring(1, e - 1);
                startInfo.Arguments = onComplete.Substring(e + 1, onComplete.Length - e - 1);
            }
            else
            {
                startInfo.FileName = onComplete.Trim('"');
            }
        }
        else
        {
            string[] parts = onComplete.Split(' ', 2);
            startInfo.FileName = parts[0];
            startInfo.Arguments = parts.Length > 1 ? parts[1] : "";
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        process.StartInfo = startInfo;

        process.Start();
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
        public int MinSampleRate = -1;
        public int MaxSampleRate = -1;
        public int MinBitDepth = -1;
        public int MaxBitDepth = -1;
        public bool StrictTitle = false;
        public bool StrictArtist = false;
        public bool StrictAlbum = false;
        public string[] DangerWords = Array.Empty<string>();
        public string[] Formats = Array.Empty<string>();
        public string[] BannedUsers = Array.Empty<string>();
        public string StrictStringRegexRemove = "";
        public bool StrictStringDiacrRemove = true;
        public bool AcceptNoLength = true;
        public bool AcceptMissingProps = true;

        public FileConditions() { }

        public FileConditions(FileConditions other)
        {
            LengthTolerance = other.LengthTolerance;
            MinBitrate = other.MinBitrate;
            MaxBitrate = other.MaxBitrate;
            MinSampleRate = other.MinSampleRate;
            MaxSampleRate = other.MaxSampleRate;
            AcceptNoLength = other.AcceptNoLength;
            StrictArtist = other.StrictArtist;
            StrictTitle = other.StrictTitle;
            MinBitDepth = other.MinBitDepth;
            MaxBitDepth = other.MaxBitDepth;
            Formats = other.Formats.ToArray();
            DangerWords = other.DangerWords.ToArray();
            BannedUsers = other.BannedUsers.ToArray();
        }

        public void UnsetClientSpecificFields()
        {
            MinBitrate = -1;
            MaxBitrate = -1;
            MinSampleRate = -1;
            MaxSampleRate = -1;
            MinBitDepth = -1;
            MaxBitDepth = -1;
        }

        public bool FileSatisfies(Soulseek.File file, Track track, SearchResponse? response)
        {
            return DangerWordSatisfies(file.Filename, track.Title, track.Artist) && FormatSatisfies(file.Filename) 
                && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file) 
                && StrictTitleSatisfies(file.Filename, track.Title) && StrictArtistSatisfies(file.Filename, track.Artist) 
                && StrictAlbumSatisfies(file.Filename, track.Album) && BannedUsersSatisfies(response) && BitDepthSatisfies(file);
        }

        public bool FileSatisfies(TagLib.File file, Track track)
        {
            return DangerWordSatisfies(file.Name, track.Title, track.Artist) && FormatSatisfies(file.Name) 
                && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file) 
                && StrictTitleSatisfies(file.Name, track.Title) && StrictArtistSatisfies(file.Name, track.Artist)
                && StrictAlbumSatisfies(file.Name, track.Album) && BitDepthSatisfies(file);
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
            return StrictString(fname, tname, StrictStringRegexRemove, StrictStringDiacrRemove, ignoreCase: true);
        }

        public bool StrictArtistSatisfies(string fname, string aname)
        {
            if (!StrictArtist || aname == "")
                return true;

            return StrictString(fname, aname, StrictStringRegexRemove, StrictStringDiacrRemove, ignoreCase: true, boundarySkipWs: false);
        }

        public bool StrictAlbumSatisfies(string fname, string alname)
        {
            if (!StrictAlbum || alname == "")
                return true;

            return StrictString(GetDirectoryNameSlsk(fname), alname, StrictStringRegexRemove, StrictStringDiacrRemove, ignoreCase: true);
        }

        public static bool StrictString(string fname, string tname, string regexRemove = "", bool diacrRemove = true, bool ignoreCase = true, bool boundarySkipWs = true)
        {
            if (string.IsNullOrEmpty(tname))
                return true;

            fname = fname.Replace("_", " ").ReplaceInvalidChars(" ", true, false);
            fname = regexRemove != "" ? Regex.Replace(fname, regexRemove, "") : fname;
            fname = diacrRemove ? fname.RemoveDiacritics() : fname;
            fname = fname.Trim().RemoveConsecutiveWs();
            tname = tname.Replace("_", " ").ReplaceInvalidChars(" ", true, true);
            tname = regexRemove != "" ? Regex.Replace(tname, regexRemove, "") : tname;
            tname = diacrRemove ? tname.RemoveDiacritics() : tname;
            tname = tname.Trim().RemoveConsecutiveWs();

            if (boundarySkipWs)
                return fname.ContainsWithBoundaryIgnoreWs(tname, ignoreCase, acceptLeftDigit: true);
            else
                return fname.ContainsWithBoundary(tname, ignoreCase);
        }

        public bool FormatSatisfies(string fname)
        {
            string ext = Path.GetExtension(fname).Trim('.').ToLower();
            return Formats.Length == 0 || (ext != "" && Formats.Any(f => f == ext));
        }

        public bool LengthToleranceSatisfies(Soulseek.File file, int wantedLength)
        {
            return LengthToleranceSatisfies(file.Length, wantedLength);
        }

        public bool LengthToleranceSatisfies(TagLib.File file, int wantedLength)
        {
            return LengthToleranceSatisfies((int)file.Properties.Duration.TotalSeconds, wantedLength);
        }

        public bool LengthToleranceSatisfies(int? length, int wantedLength)
        {
            if (LengthTolerance < 0 || wantedLength < 0)
                return true;
            if (length == null || length < 0)
                return AcceptNoLength && AcceptMissingProps;
            return Math.Abs((int)length - wantedLength) <= LengthTolerance;
        }

        public bool BitrateSatisfies(Soulseek.File file)
        {
            return BitrateSatisfies(file.BitRate);
        }

        public bool BitrateSatisfies(TagLib.File file)
        {
            return BitrateSatisfies(file.Properties.AudioBitrate);
        }

        public bool BitrateSatisfies(int? bitrate)
        {
            return BoundCheck(bitrate, MinBitrate, MaxBitrate);
        }

        public bool SampleRateSatisfies(Soulseek.File file)
        {
            return SampleRateSatisfies(file.SampleRate);
        }

        public bool SampleRateSatisfies(TagLib.File file)
        {
            return SampleRateSatisfies(file.Properties.AudioSampleRate);
        }

        public bool SampleRateSatisfies(int? sampleRate)
        {
            return BoundCheck(sampleRate, MinSampleRate, MaxSampleRate);
        }

        public bool BitDepthSatisfies(Soulseek.File file)
        {
            return BitDepthSatisfies(file.BitDepth);
        }

        public bool BitDepthSatisfies(TagLib.File file)
        {
            return BitDepthSatisfies(file.Properties.BitsPerSample);
        }

        public bool BitDepthSatisfies(int? bitdepth)
        {
            return BoundCheck(bitdepth, MinBitDepth, MaxBitDepth);
        }

        public bool BoundCheck(int? num, int min, int max)
        {
            if (max < 0 && min < 0)
                return true;
            if (num == null || num < 0)
                return AcceptMissingProps;
            if (num < min || max != -1 && num > max)
                return false;
            return true;
        }

        public bool BannedUsersSatisfies(SearchResponse? response)
        {
            return response == null || !BannedUsers.Any(x => x == response.Username);
        }

        public string GetNotSatisfiedName(Soulseek.File file, Track track, SearchResponse? response)
        {
            if (!DangerWordSatisfies(file.Filename, track.Title, track.Artist))
                return "DangerWord fails";
            if (!FormatSatisfies(file.Filename))
                return "Format fails";
            if (!LengthToleranceSatisfies(file, track.Length))
                return "Length fails";
            if (!BitrateSatisfies(file))
                return "Bitrate fails";
            if (!SampleRateSatisfies(file))
                return "SampleRate fails";
            if (!StrictTitleSatisfies(file.Filename, track.Title))
                return "StrictTitle fails";
            if (!StrictArtistSatisfies(file.Filename, track.Artist))
                return "StrictArtist fails";
            if (!BitDepthSatisfies(file))
                return "BitDepth fails";
            if (!BannedUsersSatisfies(response))
                return "BannedUsers fails";
            return "Satisfied";                               
        }

        public string GetNotSatisfiedName(TagLib.File file, Track track)
        {
            if (!DangerWordSatisfies(file.Name, track.Title, track.Artist))
                return "DangerWord fails";
            if (!FormatSatisfies(file.Name))
                return "Format fails";
            if (!LengthToleranceSatisfies(file, track.Length))
                return "Length fails";
            if (!BitrateSatisfies(file))
                return "Bitrate fails";
            if (!SampleRateSatisfies(file))
                return "SampleRate fails";
            if (!StrictTitleSatisfies(file.Name, track.Title))
                return "StrictTitle fails";
            if (!StrictArtistSatisfies(file.Name, track.Artist))
                return "StrictArtist fails";
            if (!BitDepthSatisfies(file))
                return "BitDepth fails";
            return "Satisfied";
        }
    }


    static async Task<List<Track>> ParseCsvIntoTrackInfo(string path, string artistCol = "", string trackCol = "",
        string lengthCol = "", string albumCol = "", string descCol = "", string ytIdCol = "", string trackCountCol = "", string timeUnit = "s", bool ytParse = false)
    {
        var tracks = new List<Track>();
        using var sr = new StreamReader(path, System.Text.Encoding.UTF8);
        var parser = new SmallestCSV.SmallestCSVParser(sr);

        int index = 0;
        var header = parser.ReadNextRow();
        while (header == null || header.Count == 0 || !header.Any(t => t.Trim() != ""))
        {
            index++;
            header = parser.ReadNextRow();
        }

        string[] cols = { artistCol, albumCol, trackCol, lengthCol, descCol, ytIdCol, trackCountCol };
        string[][] aliases = {
            new[] { "artist", "artist name", "artists", "artist names" },
            new[] { "album", "album name", "album title" },
            new[] { "title", "song", "track title", "track name", "song name", "track" },
            new[] { "length", "duration", "track length", "track duration", "song length", "song duration" },
            new[] { "description", "youtube description" },
            new[] { "url", "id", "youtube id" },
            new[] { "track count", "album track count" }
        };

        string usingColumns = "";
        for (int i = 0; i < cols.Length; i++)
        {
            if (string.IsNullOrEmpty(cols[i]))
            {
                string? res = header.FirstOrDefault(h => Regex.Replace(h, @"\(.*?\)", "").Trim().EqualsAny(aliases[i], StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(res))
                {
                    cols[i] = res;
                    usingColumns += $"{aliases[i][0]}:\"{res}\", ";
                }
            }
            else
            {
                if (header.IndexOf(cols[i]) == -1)
                    throw new Exception($"Column \"{cols[i]}\" not found in CSV file");
                usingColumns += $"{aliases[i][0]}:\"{cols[i]}\", ";
            }
        }

        int foundCount = cols.Count(col => col != "");
        if (!string.IsNullOrEmpty(usingColumns))
            Console.WriteLine($"Using columns: {usingColumns.TrimEnd(' ', ',')}.");
        else if (foundCount == 0)
            throw new Exception("No columns specified and couldn't determine automatically");

        int[] indices = cols.Select(col => col == "" ? -1 : header.IndexOf(col)).ToArray();
        int artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex, trackCountIndex;
        (artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex, trackCountIndex) = (indices[0], indices[1], indices[2], indices[3], indices[4], indices[5], indices[6]);

        while (true)
        {
            index++;
            var values = parser.ReadNextRow();
            if (values == null)
                break;
            if (!values.Any(t => t.Trim() != ""))
                continue;
            while (values.Count < foundCount)
                values.Add("");

            if (csvColumnCount == -1)
                csvColumnCount = values.Count;

            var desc = "";
            var track = new Track() { CsvRow = index };

            if (artistIndex >= 0) track.Artist = values[artistIndex];
            if (trackIndex >= 0) track.Title = values[trackIndex];
            if (albumIndex >= 0) track.Album = values[albumIndex];
            if (descIndex >= 0) desc = values[descIndex];
            if (ytIdIndex >= 0) track.URI = values[ytIdIndex];
            if (trackCountIndex >= 0)
            {
                string a = values[trackCountIndex].Trim();
                if (a == "-1")
                {
                    track.MinAlbumTrackCount = -1;
                    track.MaxAlbumTrackCount = -1;
                }
                else if (a.Last() == '-' && int.TryParse(a.AsSpan(0, a.Length - 1), out int n))
                {
                    track.MaxAlbumTrackCount = n;
                }
                else if (a.Last() == '+' && int.TryParse(a.AsSpan(0, a.Length - 1), out n))
                {
                    track.MinAlbumTrackCount = n;
                }
                else if (int.TryParse(a, out n))
                {
                    track.MinAlbumTrackCount = n;
                    track.MaxAlbumTrackCount = n;
                }
            }
            if (lengthIndex >= 0)
            {
                try
                {
                    track.Length = (int)ParseTrackLength(values[lengthIndex], timeUnit);
                }
                catch
                {
                    WriteLine($"Couldn't parse track length \"{values[lengthIndex]}\" with format \"{timeUnit}\" for \"{track}\"", ConsoleColor.DarkYellow);
                }
            }

            if (ytParse)
                track = await YouTube.ParseTrackInfo(track.Title, track.Artist, track.URI, track.Length, desc);

            if (track.Title != "" || track.Artist != "" || track.Album != "")
                tracks.Add(track);
        }

        if (ytParse)
            YouTube.StopService();

        return tracks;
    }

    static string GetSavePath(string sourceFname)
    {
        return $"{GetSavePathNoExt(sourceFname)}{Path.GetExtension(sourceFname)}";
    }

    static string GetSavePathNoExt(string sourceFname)
    {
        string outTo = outputFolder;
        if (albumCommonPath != "")
        {
            string add = sourceFname.Replace(albumCommonPath, "").Replace(GetFileNameSlsk(sourceFname),"").Trim('\\').Trim();
            if (add!="") outTo = Path.Join(outputFolder, add.Replace('\\', Path.DirectorySeparatorChar));
        }
        return Path.Combine(outTo, $"{GetSaveName(sourceFname)}");
    }

    static string GetSaveName(string sourceFname)
    {
        string name = GetFileNameWithoutExtSlsk(sourceFname);
        return name.ReplaceInvalidChars(invalidReplaceStr);
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

    static string GetExtensionSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetExtension(fname).TrimStart('.');
    }

    static string GetDirectoryNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetDirectoryName(fname);
    }

    static void ApplyNamingFormatsNonAudio(List<List<Track>> list)
    {
        if (!nameFormat.Replace("\\", "/").Contains('/'))
            return;

        var downloadedTracks = list.SelectMany(x => x)
            .Where(x => x.DownloadPath != "" && !x.IsNotAudio)
            .Select(x => x.DownloadPath).Distinct().ToList();

        if (downloadedTracks.Count == 0)
            return;

        for (int i = 0; i < list.Count; i++)
        {
            for (int j = 0; j < list[i].Count; j++)
            {
                var track = list[i][j];
                if (!track.IsNotAudio || track.TrackState != Track.State.Downloaded)
                    continue;
                string filepath = track.DownloadPath;
                string add = Path.GetRelativePath(outputFolder, Path.GetDirectoryName(filepath));
                string newFilePath = Path.Join(Utils.GreatestCommonPath(downloadedTracks), add, Path.GetFileName(filepath));
                if (filepath != newFilePath)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newFilePath));
                    Utils.Move(filepath, newFilePath);
                    if (add != "" && add != "." && Utils.GetRecursiveFileCount(Path.Join(outputFolder, add)) == 0)
                        Directory.Delete(Path.Join(outputFolder, add), true);
                    list[i][j] = new Track(track) { DownloadPath = newFilePath };
                }
            }
        }
    }

    static string ApplyNamingFormat(string filepath, Track track)
    {
        if (nameFormat == "" || !Utils.IsMusicFile(filepath))
            return filepath;

        string dir = Path.GetDirectoryName(filepath) ?? "";
        string add = dir != "" ? Path.GetRelativePath(outputFolder, dir) : "";
        string newFilePath = NamingFormat(filepath, nameFormat, track);

        if (filepath != newFilePath)
        {
            dir = Path.GetDirectoryName(newFilePath) ?? "";
            if (dir != "") Directory.CreateDirectory(dir);

            try
            {
                Utils.Move(filepath, newFilePath);
            }
            catch (Exception ex)
            {
                WriteLine($"\nFailed to move: {ex.Message}\n", ConsoleColor.DarkYellow, true);
                return filepath;
            }

            if (add != "" && add != "." && Utils.GetRecursiveFileCount(Path.Join(outputFolder, add)) == 0)
                try { Directory.Delete(Path.Join(outputFolder, add), true); } catch { }
        }

        return newFilePath;
    }

    static string NamingFormat(string filepath, string format, Track track)
    {
        string newName = format;
        TagLib.File? file = null;

        try { file = TagLib.File.Create(filepath); }
        catch { }

        Regex regex = new Regex(@"(\{(?:\{??[^\{]*?\}))");
        MatchCollection matches = regex.Matches(newName);

        while (matches.Count > 0)
        {
            foreach (Match match in matches.Cast<Match>())
            {
                string inner = match.Groups[1].Value;
                inner = inner.Substring(1, inner.Length - 2);

                var options = inner.Split('|');
                string chosenOpt = "";

                foreach (var opt in options)
                {
                    string[] parts = Regex.Split(opt, @"\([^\)]*\)");
                    string[] result = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
                    if (result.All(x => GetVarValue(x, file, filepath, track) != "")) {
                        chosenOpt = opt;
                        break;
                    }
                }

                chosenOpt = Regex.Replace(chosenOpt, @"\([^()]*\)|[^()]+", match =>
                {
                    if (match.Value.StartsWith("(") && match.Value.EndsWith(")"))
                        return match.Value.Substring(1, match.Value.Length-2).ReplaceInvalidChars(invalidReplaceStr, removeSlash: false);
                    else
                        return GetVarValue(match.Value, file, filepath, track).ReplaceInvalidChars(invalidReplaceStr);
                });

                string old = match.Groups[1].Value;
                old = old.StartsWith("{{") ? old.Substring(1) : old;
                newName = newName.Replace(old, chosenOpt);
            }

            matches = regex.Matches(newName);
        }

        if (newName != format)
        {
            string directory = Path.GetDirectoryName(filepath) ?? "";
            string extension = Path.GetExtension(filepath);
            char dirsep = Path.DirectorySeparatorChar;
            newName = newName.Replace('/', dirsep);
            var x = newName.Split(dirsep, StringSplitOptions.RemoveEmptyEntries);
            newName = string.Join(dirsep, x.Select(x => x.ReplaceInvalidChars(invalidReplaceStr).Trim(' ', '.')));
            string newFilePath = Path.Combine(directory, newName + extension);
            return newFilePath;
        }

        return filepath;
    }

    static string GetVarValue(string x, TagLib.File? file, string filepath, Track track)
    {
        switch (x)
        {
            case "artist":
                return file?.Tag.FirstPerformer ?? "";
            case "artists":
                return file != null ? string.Join(" & ", file.Tag.Performers) : "";
            case "albumartist":
                return file?.Tag.FirstAlbumArtist ?? "";
            case "albumartists":
                return file != null ? string.Join(" & ", file.Tag.AlbumArtists) : "";
            case "title":
                return file?.Tag.Title ?? "";
            case "album":
                return file?.Tag.Album ?? "";
            case "sartist":
            case "sartists":
                return track.Artist;
            case "stitle":
                return track.Title;
            case "salbum":
                return track.Album;
            case "year":
                return file?.Tag.Year.ToString() ?? "";
            case "track":
                return file?.Tag.Track.ToString("D2") ?? "";
            case "disc":
                return file?.Tag.Disc.ToString() ?? "";
            case "filename":
                return Path.GetFileNameWithoutExtension(filepath);
            case "foldername":
                return defaultFolderName;
            default:
                return "";
        }
    }

    static bool TrackMatchesFilename(Track track, string filename)
    {
        if (track.Title.Trim() == "" || filename.Trim() == "")
            return false;

        string[] ignore = new string[] { " ", "_", "-", ".", "(", ")", "[", "]" };

        string preprocess1(string s, bool removeSlash = true)
        {
            s = s.ReplaceInvalidChars("", false, removeSlash).Replace(ignore, "").ToLower();
            s = s.RemoveFt().RemoveDiacritics();
            return s;
        }

        string preprocess2(string s, bool removeSlash = true)
        {
            s = s.ReplaceInvalidChars("", false, removeSlash).ToLower().RemoveDiacritics();
            return s;
        }

        string preprocess3(string s)
        {
            s = s.ToLower().RemoveDiacritics();
            return s;
        }

        string title = preprocess1(track.Title);
        string artist = preprocess1(track.Artist);
        string fname = preprocess1(Path.GetFileNameWithoutExtension(filename));
        string path = preprocess1(filename, false);

        if (title == "" || fname == "")
        {
            title = preprocess2(track.Title);
            artist = preprocess2(track.Artist);
            fname = preprocess2(Path.GetFileNameWithoutExtension(filename));
            path = preprocess2(filename, false);

            if (title == "" || fname == "")
            {
                title = preprocess3(track.Title);
                artist = preprocess3(track.Artist);
                fname = preprocess3(Path.GetFileNameWithoutExtension(filename));
                path = preprocess3(filename);

                if (title == "" || fname == "")
                {
                    return false;
                }
            }
        }

        if (fname.Contains(title) && path.Contains(artist))
        {
            return true;
        }
        else if ((track.ArtistMaybeWrong || track.Artist == "") && track.Title.Contains(" - "))
        {
            title = preprocess1(track.Title.Substring(track.Title.IndexOf(" - ") + 3));
            if (title != "")
            {
                if (preprocess1(filename, false).Contains(title))
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
        string artist = track.Artist.ToLower().Replace(" ", "").RemoveFt();
        string title = track.Title.ToLower().Replace(" ", "").RemoveFt().RemoveSquareBrackets();

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

    static Dictionary<Track, string> SkipExisting(List<Track> tracks, string dir, FileConditions necessaryCond, SkipMode mode, bool useCache)
    {
        var existing = new Dictionary<Track, string>();
        List<string> musicFiles;
        List<TagLib.File> musicIndex;
        bool useTags = mode == SkipMode.Tag || mode == SkipMode.TagPrecise;
        bool precise = mode == SkipMode.NamePrecise || mode == SkipMode.TagPrecise;

        if (useCache && MusicCache.TryGetValue(dir, out var cached))
        {
            musicFiles = cached.musicFiles;
            musicIndex = cached.musicIndex;
        }
        else
        {
            var files = System.IO.Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            musicFiles = files.Where(filename => Utils.IsMusicFile(filename)).ToList();
            musicIndex = useTags ? BuildMusicIndex(musicFiles) : new List<TagLib.File>();
            if (useCache)
                MusicCache[dir] = (musicFiles, musicIndex);
        }

        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].IsNotAudio)
                continue;
            bool exists;
            string? path;
            if (useTags)
                exists = TrackExistsInCollection(tracks[i], necessaryCond, musicIndex, out path, precise);
            else
                exists = TrackExistsInCollection(tracks[i], necessaryCond, musicFiles, out path, precise);

            if (exists)
            {
                existing.TryAdd(tracks[i], path);
                tracks[i] = new Track(tracks[i]) { TrackState = Track.State.Exists, DownloadPath = path };
            }
        }

        return existing;
    }

    static Dictionary<Track, string> SkipExistingM3u(List<Track> tracks)
    {
        var existing = new Dictionary<Track, string>();

        for (int i = 0; i < tracks.Count; i++)
        {
            if (!m3uEditor.HasFail(tracks[i], out _))
            {
                existing.TryAdd(tracks[i], "");
                tracks[i] = new Track(tracks[i]) { TrackState = Track.State.Exists, DownloadPath = "" };
            }
        }

        return existing;
    }

    static List<TagLib.File> BuildMusicIndex(List<string> musicFiles)
    {
        var musicIndex = new List<TagLib.File>();
        foreach (var p in musicFiles)
        {
            try { musicIndex.Add(TagLib.File.Create(p)); }
            catch { continue; }
        }
        return musicIndex;
    }

    static Dictionary<string, (List<string> musicFiles, List<TagLib.File> musicIndex)> MusicCache = new();

    static List<string> ParseConfig(string path)
    {
        var lines = File.ReadAllLines(path);
        var res = new List<string>();
        foreach (var line in lines)
        {
            string l = line.Trim();
            if (l == "" || l.StartsWith('#'))
                continue;

            int i = l.IndexOfAny(new char[] { ' ', '=' });

            if (i < 0) continue;

            var x = l.Split(l[i], 2, StringSplitOptions.TrimEntries);
            string opt = x[0];
            string arg = x[1];

            if (opt == "") continue;

            if (arg.StartsWith('='))
                arg = arg.Substring(1).TrimStart();

            if (arg.Length > 0 && arg[0] == '"' && arg[arg.Length - 1] == '"')
                arg = arg.Substring(1, arg.Length - 2);

            if (arg == "false") continue;

            if (!opt.StartsWith('-'))
            {
                if (opt.Length == 1)
                    opt = '-' + opt;
                else
                    opt = "--" + opt;
            }

            res.Add(opt);

            if (arg.Length > 0 && arg != "true")
                res.Add(arg);
        }
        return res;
    }

    static void ParseConditions(FileConditions cond, string input)
    {
        var tr = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;
        string[] conditions = input.Split(';', tr);
        foreach (string condition in conditions)
        {
            string[] parts = condition.Split(new string[] { ">=", "<=", "=", ">", "<" }, 2, tr);
            string field = parts[0].Replace("-", "").Trim().ToLower();
            string value = parts.Length > 1 ? parts[1].Trim() : "true";

            switch (field)
            {
                case "sr":
                case "samplerate":
                    UpdateMinMax(value, condition, ref cond.MinSampleRate, ref cond.MaxSampleRate);
                    break;
                case "br":
                case "bitrate":
                    UpdateMinMax(value, condition, ref cond.MinBitrate, ref cond.MaxBitrate);
                    break;
                case "bd":
                case "bitdepth":
                    UpdateMinMax(value, condition, ref cond.MinBitDepth, ref cond.MaxBitDepth);
                    break;
                case "t":
                case "tol":
                case "lentol":
                case "lengthtol":
                case "tolerance":
                case "lengthtolerance":
                    cond.LengthTolerance = int.Parse(value);
                    break;
                case "f":
                case "format":
                case "formats":
                    cond.Formats = value.Split(',', tr);
                    break;
                case "banned":
                case "bannedusers":
                    cond.BannedUsers = value.Split(',', tr);
                    break;
                case "dangerwords":
                    cond.DangerWords = value.Split(',', tr);
                    break;
                case "stricttitle":
                    cond.StrictTitle = bool.Parse(value);
                    break;
                case "strictartist":
                    cond.StrictArtist = bool.Parse(value);
                    break;
                case "strictalbum":
                    cond.StrictAlbum = bool.Parse(value);
                    break;
                case "acceptnolen":
                case "acceptnolength":
                    cond.AcceptNoLength = bool.Parse(value);
                    break;
                case "strict":
                case "acceptmissing":
                case "acceptmissingprops":
                    cond.AcceptMissingProps = bool.Parse(value);
                    break;
                default:
                    throw new ArgumentException($"Unknown condition '{condition}'");
            }
        }
    }


    static void UpdateMinMax(string value, string condition, ref int min, ref int max)
    {
        if (condition.Contains(">="))
            min = int.Parse(value);
        else if (condition.Contains("<="))
            max = int.Parse(value);
        else if (condition.Contains('>'))
            min = int.Parse(value) + 1;
        else if (condition.Contains('<'))
            max = int.Parse(value) - 1;
        else if (condition.Contains('='))
            min = max = int.Parse(value);
    }


    static Track ParseTrackArg(string input, bool isAlbum)
    {
        input = input.Trim();
        var track = new Track();
        var keys = new string[] { "title", "artist", "duration", "length", "album", "artist-maybe-wrong" };

        track.IsAlbum = isAlbum;

        var parts = input.Split(',');
        var other = "";
        var lastkeyval = true;

        for (int i = 0; i < parts.Length; i++)
        {
            var x = parts[i];
            bool keyval = false;

            if (x.Contains('='))
            {
                var lr = x.Split('=', 2, StringSplitOptions.TrimEntries);
                if (lr.Length == 2 && lr[1] != "" && keys.Contains(lr[0]))
                {
                    keyval = true;
                    switch (lr[0])
                    {
                        case "title":
                            track.Title = lr[1];
                            break;
                        case "artist":
                            track.Artist = lr[1];
                            break;
                        case "duration":
                        case "length":
                            track.Length = int.Parse(lr[1]);
                            break;
                        case "album":
                            track.Album = lr[1];
                            break;
                        case "artist-maybe-wrong":
                            if (lr[1] == "true")
                                track.ArtistMaybeWrong = true;
                            break;
                    }
                }
            }

            if (!keyval)
            {
                if (!lastkeyval)
                    other += ',';
                other += x;
                lastkeyval = false;
            }
            else
            {
                lastkeyval = true;
            }
        }

        string artist = "", album = "", title = "";
        string splitBy = other.Contains(" -- ") ? " -- " : " - ";
        parts = other.Split(splitBy, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 || parts.Length > 3)
        {
            if (isAlbum) 
                album = other.Trim();
            else 
                title = other.Trim();
        }
        else if (parts.Length == 2)
        {
            artist = parts[0];

            if (isAlbum)
                album = parts[1];
            else
                title = parts[1];
        }
        else if (parts.Length == 3)
        {
            artist = parts[0];
            album = parts[1];
            title = parts[2];
        }

        if (track.Artist == "")
            track.Artist = artist;
        if (track.Album == "")
            track.Album = album;
        if (track.Title == "")
            track.Title = title;

        if (track.Title == "" && track.Album == "" && track.Artist == "")
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

    static string DisplayString(Track t, Soulseek.File? file=null, SearchResponse? response=null, FileConditions? nec=null, 
        FileConditions? pref=null, bool fullpath=false, string customPath="", bool infoFirst=false, bool showUser=true, bool showSpeed=false)
    {
        if (file == null)
            return t.ToString();

        string sampleRate = file.SampleRate.HasValue ? $"{(file.SampleRate.Value/1000.0).Normalize()}kHz" : "";
        string bitRate = file.BitRate.HasValue ? $"{file.BitRate}kbps" : "";
        string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string user = showUser && response?.Username != null ? response.Username + "\\" : "";
        string speed = showSpeed && response?.Username != null ? $"({response.UploadSpeed / 1024.0 / 1024.0:F2}MB/s) " : "";
        string fname = fullpath ? file.Filename : (showUser ? "..\\" : "") + (customPath == "" ? GetFileNameSlsk(file.Filename) : customPath);
        string length = Utils.IsMusicFile(file.Filename) ? (file.Length ?? -1).ToString() + "s" : "";
        string displayText;
        if (!infoFirst)
        {
            string info = string.Join('/', new string[] { length, sampleRate+bitRate, fileSize }.Where(value => value!=""));
            displayText = $"{speed}{user}{fname} [{info}]";
        }
        else
        {
            string info = string.Join('/', new string[] { length.PadRight(4), (sampleRate+bitRate).PadRight(8), fileSize.PadLeft(6) });
            displayText = $"[{info}] {speed}{user}{fname}";
        }

        string necStr = nec != null ? $"nec:{nec.GetNotSatisfiedName(file, t, response)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, t, response)}" : "";
        string cond = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }

    static void PrintTracks(List<Track> tracks, int number = int.MaxValue, bool fullInfo=false, bool pathsOnly=false, bool showAncestors=false, bool infoFirst=false, bool showUser=true)
    {
        number = Math.Min(tracks.Count, number);

        string ancestor = "";

        if (showAncestors)
            ancestor = Utils.GreatestCommonPath(tracks.SelectMany(x => x.Downloads.Select(y => y.Value.Item2.Filename)));

        if (pathsOnly) 
        {
            for (int i = 0; i < number; i++)
            {
                foreach (var x in tracks[i].Downloads)
                {
                    if (ancestor == "")
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, infoFirst: infoFirst, showUser: showUser));
                    else
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst, showUser: showUser));
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
                    Console.WriteLine($"  Title:              {tracks[i].Title}");
                    Console.WriteLine($"  Artist:             {tracks[i].Artist}");
                    if (!tracks[i].IsAlbum)
                        Console.WriteLine($"  Length:             {tracks[i].Length}s");
                    if (!string.IsNullOrEmpty(tracks[i].Album))
                        Console.WriteLine($"  Album:              {tracks[i].Album}");
                    if (!string.IsNullOrEmpty(tracks[i].URI))
                        Console.WriteLine($"  URL/ID:             {tracks[i].URI}");
                    if (!string.IsNullOrEmpty(tracks[i].Other))
                        Console.WriteLine($"  Other:              {tracks[i].Other}");
                    if (tracks[i].ArtistMaybeWrong)
                        Console.WriteLine($"  Artist maybe wrong: {tracks[i].ArtistMaybeWrong}");    
                    if (tracks[i].Downloads != null) {
                        Console.WriteLine($"  Shares:             {tracks[i].Downloads.Count}");
                        foreach (var x in tracks[i].Downloads) {
                            if (ancestor == "")
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, infoFirst: infoFirst, showUser: showUser));
                            else
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst, showUser: showUser));
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
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, infoFirst: infoFirst, showUser: showUser));
                        else
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst, showUser: showUser));
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

    public static async Task WaitForLogin()
    {
        while (true)
        {
            WriteLine($"Wait for login, state: {client.State}", debugOnly: true);
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


public enum FailureReasons
{
    None,
    InvalidSearchString,
    OutOfDownloadRetries,
    NoSuitableFileFound,
    AllDownloadsFailed
}


public class TrackLists
{
    public enum ListType
    {
        Normal,
        Album,
        Aggregate
    }

    public List<(List<List<Track>> list, ListType type, Track source)> lists = new();

    public TrackLists() { }

    public TrackLists(List<(List<List<Track>> list, ListType type, Track source)> lists)
    {
        foreach (var (list, type, source) in lists)
        {
            var newList = new List<List<Track>>();
            foreach (var innerList in list)
            {
                var innerNewList = new List<Track>(innerList);
                newList.Add(innerNewList);
            }
            this.lists.Add((newList, type, source));
        }
    }

    public static TrackLists FromFlatList(List<Track> flatList, bool aggregate, bool album)
    {
        var res = new TrackLists();
        for (int i = 0; i < flatList.Count; i++)
        {
            if (aggregate)
            {
                res.AddEntry(ListType.Aggregate, flatList[i]);
            }
            else if (album || (flatList[i].Album != "" && flatList[i].Title == ""))
            {
                res.AddEntry(ListType.Album, new Track(flatList[i]) { IsAlbum = true });
            }
            else
            {
                res.AddEntry(ListType.Normal);
                while (i < flatList.Count && (flatList[i].Album == "" || flatList[i].Title != ""))
                {
                    res.AddTrackToLast(flatList[i]);
                    i++;
                }
                if (i < flatList.Count)
                    i--;
            }
        }
        return res;
    }

    public void AddEntry(List<List<Track>>? list=null, ListType? type=null, Track? source=null)
    {
        type ??= ListType.Normal;
        source ??= new Track();
        list ??= new List<List<Track>>();
        lists.Add(((List<List<Track>> list, ListType type, Track source))(list, type, source));
    }

    public void AddEntry(List<Track> tracks, ListType? type = null, Track? source = null)
    {
        var list = new List<List<Track>>() { tracks };
        AddEntry(list, type, source);
    }

    public void AddEntry(Track track, ListType? type = null, Track? source = null)
    {
        var list = new List<List<Track>>() { new List<Track>() { track } };
        AddEntry(list, type, source);
    }

    public void AddEntry(ListType? type = null, Track? source = null)
    {
        var list = new List<List<Track>>() { new List<Track>() };
        AddEntry(list, type, source);
    }

    public void AddTrackToLast(Track track)
    {
        int i = lists.Count - 1;
        int j = lists[i].list.Count - 1;
        lists[i].list[j].Add(track);
    }

    public void Reverse()
    {
        lists.Reverse();
        foreach (var (list, type, source) in lists)
        {
            foreach (var ls in list)
            {
                ls.Reverse();
            }
        }
    }

    public List<Track> CombinedTrackList(bool addSourceTracks=false)
    {
        var res = new List<Track>();

        foreach (var (list, type, source) in lists)
        {
            if (addSourceTracks)
                res.Add(source);
            foreach (var t in list[0])
                res.Add(t);
        }

        return res;
    }

    public List<Track> Flattened()
    {
        var res = new List<Track>();

        foreach (var (list, type, source) in lists)
        {
            if (type == ListType.Album || type == ListType.Aggregate)
            {
                res.Add(source);
            }
            else
            {
                foreach (var t in list[0])
                    res.Add(t);
            }
        }

        return res;
    }

    public void SetList(List<List<Track>> list, int index)
    {
        var (_, type, source) = lists[index];
        lists[index] = (list, type, source);
    }

    public void SetType(ListType type, int index)
    {
        var (list, _, source) = lists[index];
        lists[index] = (list, type, source);
    }

    public void SetSource(Track source, int index)
    {
        var (list, type, _) = lists[index];
        lists[index] = (list, type, source);
    }
}


public struct Track
{
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string URI = "";
    public int Length = -1;
    public bool ArtistMaybeWrong = false;
    public bool IsAlbum = false;
    public int MinAlbumTrackCount = -1;
    public int MaxAlbumTrackCount = -1;
    public bool IsNotAudio = false;
    public string FailureReason = "";
    public string DownloadPath = "";
    public string Other = "";
    public int CsvRow = -1; 
    public State TrackState = State.Initial;

    public SlDictionary? Downloads = null;

    public enum State
    {
        Initial,
        Downloaded, 
        Failed,
        Exists,
        NotFoundLastTime
    };

    public Track() { }

    public Track(Track other)
    {
        Title = other.Title;
        Artist = other.Artist;
        Album = other.Album;
        Length = other.Length;
        URI = other.URI;
        ArtistMaybeWrong = other.ArtistMaybeWrong;
        Downloads = other.Downloads;
        IsAlbum = other.IsAlbum;
        IsNotAudio = other.IsNotAudio;
        TrackState = other.TrackState;
        FailureReason = other.FailureReason;
        DownloadPath = other.DownloadPath;
        Other = other.Other;
        MinAlbumTrackCount = other.MinAlbumTrackCount;
        MaxAlbumTrackCount = other.MaxAlbumTrackCount;
        CsvRow = other.CsvRow;
    }

    public override readonly string ToString()
    {
        return ToString(false);
    }

    public readonly string ToString(bool noInfo = false)
    {
        if (IsNotAudio && Downloads != null && !Downloads.IsEmpty)
            return $"{Program.GetFileNameSlsk(Downloads.First().Value.Item2.Filename)}";

        string str = Artist;
        if (!IsAlbum && Title == "" && Downloads != null && !Downloads.IsEmpty)
        {
            str = $"{Program.GetFileNameSlsk(Downloads.First().Value.Item2.Filename)}";
        }
        else if (Title != "" || Album != "")
        {
            if (str != "")
                str += " - ";
            if (IsAlbum)
                str += Album;
            else if (Title != "")
                str += Title;
            if (!noInfo)
            {
                if (Length > 0)
                    str += $" ({Length}s)";
                if (IsAlbum)
                    str += " (album)";
            }
        }
        else if (!noInfo)
        {
            str += " (artist)";
        }

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

        return string.Equals(a.Title, b.Title, comparer)
            && string.Equals(a.Artist, b.Artist, comparer)
            && string.Equals(a.Album, b.Album, comparer);
    }

    public int GetHashCode(Track a)
    {
        unchecked
        {
            int hash = 17;
            string trackTitle = _ignoreCase ? a.Title.ToLower() : a.Title;
            string artistName = _ignoreCase ? a.Artist.ToLower() : a.Artist;
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
    public TrackLists trackLists;
    public string path;
    public string outputFolder;
    public int offset = 0;
    public string option = "fails";
    public bool m3uListLabels = false;
    public Dictionary<string, string> fails;

    public M3UEditor(string m3uPath, string outputFolder, TrackLists trackLists, int offset = 0, string option="fails")
    {
        this.trackLists = trackLists;
        this.outputFolder = Path.GetFullPath(outputFolder);
        this.offset = offset;
        this.option = option;
        path = Path.GetFullPath(m3uPath);

        var lines = ReadAllLines();
        fails = lines.Where(x => x.StartsWith("# Failed: "))
            .Select(line =>
            {
                var lastBracketIndex = line.LastIndexOf('[');
                lastBracketIndex = lastBracketIndex == -1 ? line.Length : lastBracketIndex;
                var key = line.Substring("# Failed: ".Length, lastBracketIndex - "# Failed: ".Length).Trim();
                var value = lastBracketIndex != line.Length ? line.Substring(lastBracketIndex + 1).Trim().TrimEnd(']') : "";
                return new { Key = key, Reason = value };
            })
            .ToSafeDictionary(pair => pair.Key, pair => pair.Reason);
    }

    public void Update()
    {
        if (option != "fails" && option != "all")
            return;

        bool needUpdate = false;

        lock (trackLists)
        {
            var lines = ReadAllLines().ToList();
            int index = offset;

            void updateLine(string newLine)
            {
                while (index >= lines.Count) lines.Add("");
                if (newLine != lines[index]) needUpdate = true;
                lines[index] = newLine;
            }

            foreach (var (list, type, source) in trackLists.lists)
            {
                if (source.TrackState == Track.State.Failed)
                {
                    updateLine(TrackToLine(source, source.FailureReason));
                    fails.TryAdd(source.ToString().Trim(), source.FailureReason.Trim());
                    index++;
                }
                else
                {
                    if (m3uListLabels)
                    {
                        string end = type == TrackLists.ListType.Normal ? "" : $" {source.ToString(noInfo: true)}";
                        updateLine($"# {Enum.GetName(typeof(TrackLists.ListType), type)} download{end}");
                        index++;
                    }
                    for (int k = 0; k < list.Count; k++)
                    {
                        for (int j = 0; j < list[k].Count; j++)
                        {
                            var track = list[k][j];
                            if (track.IsNotAudio)
                            {
                                continue;
                            }
                            else if (track.TrackState == Track.State.Failed || track.TrackState == Track.State.NotFoundLastTime ||
                                (option == "all" && (track.TrackState == Track.State.Downloaded || (track.TrackState == Track.State.Exists && k == 0))))
                            {
                                string reason = track.TrackState == Track.State.NotFoundLastTime ? nameof(FailureReasons.NoSuitableFileFound) : track.FailureReason;
                                updateLine(TrackToLine(track, reason));
                                if (track.TrackState == Track.State.Failed)
                                    fails.TryAdd(track.ToString().Trim(), reason.Trim());
                                if (type != TrackLists.ListType.Normal)
                                    index++;
                            }
                            else if (option == "fails" && track.TrackState == Track.State.Downloaded && index < lines.Count && lines[index].StartsWith($"# Failed: {track}"))
                            {
                                lines[index] = "";
                                needUpdate = true;
                            }

                            if (type == TrackLists.ListType.Normal)
                                index++;
                        }
                    }
                }
            }

            if (needUpdate)
            {
                if (!File.Exists(path))
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, string.Join("\n", lines).TrimEnd('\n') + "\n");
            }
        }
    }

    public string TrackToLine(Track track, string failureReason="")
    {
        if (failureReason != "")
            return $"# Failed: {track} [{failureReason}]";
        if (track.DownloadPath != "")
            return Path.GetRelativePath(Path.GetDirectoryName(path), track.DownloadPath).Replace("\\", "/");
        return $"# {track}";
    }

    public bool HasFail(Track track, out string? reason)
    {
        reason = null;
        var key = track.ToString().Trim();
        if (key == "")
            return false;
        return fails.TryGetValue(key, out reason);
    }

    public string ReadAllText()
    {
        if (!File.Exists(path))
            return "";
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var streamReader = new StreamReader(fileStream);
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
                int releaseCount = this.maxCount - this.semaphore.CurrentCount;
                if (releaseCount > 0)
                    this.semaphore.Release(releaseCount);

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

