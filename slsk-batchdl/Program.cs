using AngleSharp.Dom;
using Konsole;
using Soulseek;
using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using TagLib;

using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using File = System.IO.File;
using Directory = System.IO.Directory;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;


// todo: Why does it use so much CPU and memory? Why does album searching take so long? (way longer than searchTimeout).
//
// todo: Investigate spotify locale issues. Spotify-made playlist title language changes when the app is rerun.
//
// todo: --get-parents: When not downloading albums, --get-parents will make the program retrieve and download
//       all parent folders for every track (parent of parent if parent is a disc folder).
//       Implementation: handle it under one download so that downloads for other tracks may continue
//       simultaneously. However the downloads of a parent folder must continue after fails, unless that fail
//       is the original track itself (jump to the next track + parent folder in that case).
//       The result should be RefreshOrPrinted in the same progress bar: E.g if
//       all parent tracks have been successfully downloaded, the progress bar should read
//       "Succeeded (10/10 parent files): original track filepath", if 1 or more of the files failed to download
//       "Succeeded with fails (02/10 parent files): original track filepath, (Failed: track1.mp3, track2.mp3)"
//       Note that the success file count should always be 1 or more since the original track should always be a success,
//       otherwise it should select another source for that track and download the files from the new parent.
//       The original track should always be the first to be downloaded regardless of its order in the parent folder.
//       Maybe create a new function GetParent(SlResponse, SlFile, ProgressBar, bool notThisFile=true) that will request the parent folder and download the files
//       while properly updating the progress bar. The original track can be downloaded in SearchAndDownload (skipping to the next as usual on fail)
//       and if successful, run GetParent to dl all files from the parent that arent the original track. The default resulting save file paths should be the same
//       as the parent, e.g if parent folder is someuser\music\artist\album and one of the tracks is someuser\music\artist\disc 1\track.mp3 then we save
//       it as outputFolder/album/disc 1/track.mp3.
//       Note: It's possible that the parent folder downloads will contain a track that also appears in the track list later. We can check for this in the following
//       way: Save a list/dict of all downloaded tracks together with the their key = username + "\\" + filename (check if one of the existing dicts/bags I define
//       below can already be used for that as well). Then when performing a search for a particular track, we check if one of the results has been downloaded already
//       AND satisfies the preferred conditions. If that is the case, we skip the track and refresh the progress bar with "Succeeded". If all results only satisfy nec
//       conditions and a downloaded file also satisfies nec conditions, do the same.
//
// todo: --get-all
//
// todo: make --interactive work for non-albums as well, allowing the user to specify the desired file and to 
//       skip downloading a track. Important for --get-parents to avoid large numbers of unwanted files. When --get-parents is active,
//       interactive should also print the list of files in the parent folder that are about to be downloaded, and there should be an
//       additional option "Only Source Track [t]" which will make it only download the original track and not all the files in the parent.
//
// todo: --pref-users <list>

public enum FailureReasons
{
    None,
    InvalidSearchString,
    OutOfDownloadRetries,
    NoSuitableFileFound,
    AllDownloadsFailed
}

static class Program
{
    static SoulseekClient? client = null;
    static TrackLists trackLists = new();
    static ConcurrentDictionary<Track, SearchInfo> searches = new();
    static ConcurrentDictionary<string, DownloadWrapper> downloads = new();
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
    static bool albumArtOnly = false;
    static bool interactiveMode = false;
    static bool albumIgnoreFails = false;
    static int albumTrackCount = -1;
    static char albumTrackCountIneq = '=';
    static string albumCommonPath = "";
    static string regexReplacePattern = "";
    static string regexPatternToReplace = "";
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
    static string ytdlpArgument = "";
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

    static string inputType = "";

    static void PrintHelp()
    {
        // undocumented options:
        // --artist-col, --title-col, --album-col, --length-col, --yt-desc-col, --yt-id-col
        // --remove-brackets, --spotify, --csv, --string, --youtube, --random-login
        // --danger-words, --pref-danger-words, --no-modify-share-count, --yt-dlp-argument, --album, --album-art-only
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
                            "\n  -p --path <path>               Download folder" +
                            "\n  -f --folder <name>             Subfolder name. Set to '.' to output directly to the" +
                            "\n                                 download folder (default: playlist/csv name)" +
                            "\n  -n --number <maxtracks>        Download the first n tracks of a playlist" +
                            "\n  -o --offset <offset>           Skip a specified number of tracks" +
                            "\n  -r --reverse                   Download tracks in reverse order" +
                            "\n  --nf --name-format <format>    Name format for downloaded tracks, e.g \"{artist} - {title}\"" +
                            "\n  --fs --fast-search             Begin downloading as soon as a file satisfying the preferred" +
                            "\n                                 conditions is found. Increases chance to download bad files." +
                            "\n  --m3u <option>                 Create an m3u8 playlist file" +
                            "\n                                 'none': Do not create a playlist file" +
                            "\n                                 'fails' (default): Write only failed downloads to the m3u" +
                            "\n                                 'all': Write successes + fails as comments" +
                            "\n" +
                            "\n  --spotify-id <id>              spotify client ID" +
                            "\n  --spotify-secret <secret>      spotify client secret" +
                            "\n  --remove-from-playlist         Remove downloaded tracks from playlist (spotify only)" +
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
                            "\n  --min-samplerate <rate>        Minimum file sample rate" +
                            "\n  --max-samplerate <rate>        Maximum file sample rate" +
                            "\n  --min-bitdepth <depth>         Minimum bit depth" +
                            "\n  --max-bitdepth <depth>         Maximum bit depth" +
                            "\n  --strict-title                 Only download if filename contains track title" +
                            "\n  --strict-artist                Only download if filepath contains track artist" +
                            "\n  --banned-users <list>          Comma-separated list of users to ignore" +
                            "\n" +
                            "\n  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)" +
                            "\n  --pref-length-tol <sec>        Preferred length tolerance in seconds (default: 2)" +
                            "\n  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)" +
                            "\n  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2200)" +
                            "\n  --pref-min-samplerate <rate>   Preferred minimum sample rate" +
                            "\n  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 96000)" +
                            "\n  --pref-min-bitdepth <depth>    Preferred minimum bit depth" +
                            "\n  --pref-max-bitdepth <depth>    Preferred maximum bit depth" +
                            "\n  --pref-strict-artist           Prefer download if filepath contains track artist" +
                            "\n  --pref-banned-users <list>     Comma-separated list of users to deprioritize" +
                            "\n" +
                            "\n  --strict                       Skip files with missing properties instead of accepting by" +
                            "\n                                 default; if --min-bitrate is set, ignores any files with" +
                            "\n                                 unknown bitrate." +
                            "\n" +
                            "\n  -a --aggregate                 Instead of downloading a single track matching the input," +
                            "\n                                 find and download all distinct songs associated with the" +
                            "\n                                 provided artist, album, or track title." +
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
                            "\n                                 a '+' or '-' after the number for the inequalities >= and <=" +
                            "\n  --album-ignore-fails           When downloading an album and one of the files fails, do not" +
                            "\n                                 skip to the next source and do not delete all successfully" +
                            "\n                                 downloaded files" +
                            "\n  --album-art <option>           When downloading albums, optionally retrieve album images" +
                            "\n                                 from another location:" +
                            "\n                                 'default': Download from the same folder as the music" +
                            "\n                                 'largest': Download from the folder with the largest image" +
                            "\n                                 'most': Download from the folder containing the most images" +
                            "\n --album-art-only                Only download album art for the provided album" +
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
                            "\n                                 artist/album/title only, then filtering. (slower search)" +
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
        int idx = Array.LastIndexOf(args, "--config");
        int idx2 = Array.LastIndexOf(args, "--conf");
        idx = idx > -1 ? idx : idx2;
        if (idx != -1)
        {
            confPath = args[idx + 1];
            confPathChanged = true;
        }

        if ((File.Exists(confPath) || confPathChanged) && confPath != "none")
        {
            if (confPathChanged && !File.Exists(confPath))
            {
                confPath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath);
            }
            string confArgs = System.IO.File.ReadAllText(confPath);
            List<string> finalArgs = new List<string>();
            finalArgs.AddRange(ParseCommand(confArgs));
            finalArgs.AddRange(args);
            args = finalArgs.ToArray();
        }

        if (args.Contains("--strict"))
        {
            preferredCond.AcceptMissingProps = false;
            necessaryCond.AcceptMissingProps = false;
            preferredCond.MaxBitrate = -1;
            necessaryCond.MaxBitrate = -1;
            preferredCond.MinBitrate = -1;
            necessaryCond.MinBitrate = -1;
            preferredCond.MaxSampleRate = -1;
            necessaryCond.MaxSampleRate = -1;
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
                    case "--conf":
                    case "--config":
                        confPath = args[++i];
                        break;
                    case "-f":
                    case "--folder":
                        folderName = args[++i];
                        break;
                    case "--md":
                    case "--music-dir":
                        musicDir = args[++i];
                        break;
                    case "-a":
                    case "--aggregate":
                        aggregate = true;
                        break;
                    case "--mua":
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
                    case "--se":
                    case "--skip-existing":
                        skipExisting = true;
                        break;
                    case "--snf":
                    case "--skip-not-found":
                        skipNotFound = true;
                        break;
                    case "--rfp":
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
                    case "--regex":
                        string s = args[++i].Replace("\\;", "<<semicol>>");
                        var parts = s.Split(";").ToArray();
                        regexPatternToReplace = parts[0];
                        if (parts.Length > 1)
                            regexReplacePattern = parts[1];
                        regexPatternToReplace = regexPatternToReplace.Replace("<<semicol>>", ";");
                        regexReplacePattern = regexReplacePattern.Replace("<<semicol>>", ";");
                        break;
                    case "-r":
                    case "--reverse":
                        reverse = true;
                        break;
                    case "--m3u":
                    case "--m3u8":
                        m3uOption = args[++i];
                        break;
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
                        if (a.Last() == '+' || a.Last() == '-')
                        {
                            albumTrackCountIneq = a.Last();
                            a = a.Substring(0, a.Length - 1);
                        }
                        albumTrackCount = int.Parse(a);
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
                                throw new ArgumentException($"Invalid album art download mode \'{args[i]}\'");
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
                    case "--int":
                    case "--interactive":
                        interactiveMode = true;
                        break;
                    case "--pref-f":
                    case "--pref-af":
                    case "--pref-format":
                        preferredCond.Formats = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "--pref-t":
                    case "--pref-tol":
                    case "--pref-length-tol":
                        preferredCond.LengthTolerance = int.Parse(args[++i]);
                        break;
                    case "--pref-min-br":
                    case "--pref-min-bitrate":
                        preferredCond.MinBitrate = int.Parse(args[++i]);
                        break;
                    case "--pref-max-br":
                    case "--pref-max-bitrate":
                        preferredCond.MaxBitrate = int.Parse(args[++i]);
                        break;
                    case "--pref-max-sr":
                    case "--pref-max-samplerate":
                        preferredCond.MaxSampleRate = int.Parse(args[++i]);
                        break;
                    case "--pref-min-sr":
                    case "--pref-min-samplerate":
                        preferredCond.MinSampleRate = int.Parse(args[++i]);
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
                    case "--pref-banned":
                    case "--pref-banned-users":
                        preferredCond.BannedUsers = args[++i].Split(',');
                        break;
                    case "--pref-min-bd":
                    case "--pref-min-bitdepth":
                        preferredCond.MinBitDepth = int.Parse(args[++i]);
                        break;
                    case "--pref-max-bd":
                    case "--pref-max-bitdepth":
                        preferredCond.MaxBitDepth = int.Parse(args[++i]);
                        break;
                    case "--af":
                    case "--format":
                        necessaryCond.Formats = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "--tol":
                    case "--length-tol":
                        necessaryCond.LengthTolerance = int.Parse(args[++i]);
                        break;
                    case "--min-br":
                    case "--min-bitrate":
                        necessaryCond.MinBitrate = int.Parse(args[++i]);
                        break;
                    case "--max-br":
                    case "--max-bitrate":
                        necessaryCond.MaxBitrate = int.Parse(args[++i]);
                        break;
                    case "--max-sr":
                    case "--max-samplerate":
                        necessaryCond.MaxSampleRate = int.Parse(args[++i]);
                        break;
                    case "--min-sr":
                    case "--min-samplerate":
                        necessaryCond.MinSampleRate = int.Parse(args[++i]);
                        break;
                    case "--min-bd":
                    case "--min-bitdepth":
                        necessaryCond.MinBitDepth = int.Parse(args[++i]);
                        break;
                    case "--max-bd":
                    case "--max-bitdepth":
                        necessaryCond.MaxBitDepth = int.Parse(args[++i]);
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
                    case "--banned":
                    case "--banned-users":
                        necessaryCond.BannedUsers = args[++i].Split(',');
                        break;
                    case "--cond":
                    case "--conditions":
                        ParseConditions(necessaryCond, args[++i]);
                        break;
                    case "--pref":
                    case "--pref-cond":
                    case "--preferred":
                        ParseConditions(preferredCond, args[++i]);
                        break;
                    case "--nmsc":
                    case "--no-modify-share-count":
                        noModifyShareCount = true;
                        break;
                    case "--seut":
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
                    case "--sm":
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
                    case "--debug":
                        debugInfo = true;
                        break;
                    case "--strict":
                        preferredCond.AcceptMissingProps = false;
                        necessaryCond.AcceptMissingProps = false;
                        break;
                    case "--yda":
                    case "--yt-dlp-argument":
                        ytdlpArgument = args[++i];
                        break;
                    case "--al":
                    case "--album":
                        album = true;
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

        if (input == "")
            throw new ArgumentException($"No input provided");

        if (ytKey != "")
            YouTube.apiKey = ytKey;

        if (debugDisableDownload)
            maxConcurrentProcesses = 1;

        if (inputType == "youtube" || (inputType == "" && input.StartsWith("http") && input.Contains("youtu")))
        {
            WriteLine("Youtube download", debugOnly: true);
            await YoutubeInput();
        }
        else if (inputType == "spotify" || (inputType == "" && (input.StartsWith("http") && input.Contains("spotify")) || input == "spotify-likes"))
        {
            WriteLine("Spotify download", debugOnly: true);
            await SpotifyInput();
        }
        else if (inputType == "csv" || (inputType == "" && Path.GetExtension(input).Equals(".csv", StringComparison.OrdinalIgnoreCase)))
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
        folderName = String.Join('/', folderName.Split("/").Select(x => ReplaceInvalidChars(x, " ").Trim()));
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
        inputType = "youtube";

        string name;
        List<Track>? deleted = null;
        List<Track> tracks;

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

        YouTube.StopService();
        trackLists.AddEntry(tracks);

        if (album || aggregate)
            trackLists = TrackLists.FromFlatList(trackLists.Flattened().ToList(), aggregate, album);

        defaultFolderName = ReplaceInvalidChars(name, " ");
    }


    static async Task SpotifyInput()
    {
        int max = reverse ? int.MaxValue : maxTracks;
        int off = reverse ? 0 : offset;

        spotifyUrl = input;
        inputType = "spotify";

        string? playlistName;
        bool usedDefaultId = false;
        bool login = spotifyUrl == "spotify-likes" || removeTracksFromSource;
        List<Track> tracks;

        static void readSpotifyCreds()
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

        trackLists.AddEntry(tracks);
        if (album || aggregate)
            trackLists = TrackLists.FromFlatList(trackLists.Flattened().ToList(), aggregate, album);

        defaultFolderName = ReplaceInvalidChars(playlistName, " ");
    }


    static async Task CsvInput()
    {
        int max = reverse ? int.MaxValue : maxTracks;
        int off = reverse ? 0 : offset;

        csvPath = input;
        inputType = "csv";

        if (!System.IO.File.Exists(csvPath))
            throw new FileNotFoundException("CSV file not found");

        var tracks = await ParseCsvIntoTrackInfo(csvPath, artistCol, trackCol, lengthCol, albumCol, descCol, ytIdCol, timeUnit, ytParse);
        tracks = tracks.Skip(off).Take(max).ToList();
        trackLists = TrackLists.FromFlatList(tracks, aggregate, album);
        defaultFolderName = Path.GetFileNameWithoutExtension(csvPath);
    }


    static async Task StringInput()
    {
        searchStr = input;
        inputType = "string";
        var music = ParseTrackArg(searchStr);
        bool isAlbum = false;

        if (album)
        {
            trackLists.AddEntry(TrackLists.ListType.Album, new Track(music) { TrackIsAlbum = true });
        }
        else if (!aggregate && music.TrackTitle != "")
        {
            trackLists.AddEntry(music);
        }
        else if (aggregate)
        {
            trackLists.AddEntry(TrackLists.ListType.Aggregate, music);
        }
        else if (music.TrackTitle == "" && music.Album != "")
        {
            isAlbum = true;
            music.TrackIsAlbum = true;
            trackLists.AddEntry(TrackLists.ListType.Album, music);
        }
        else
        {
            throw new ArgumentException("Need track title or album");
        }

        if (aggregate || isAlbum || album)
            defaultFolderName = ReplaceInvalidChars(music.ToString(true), " ").Trim();
        else
            defaultFolderName = ".";
    }


    static async Task MainLoop()
    {
        for (int i = 0; i < trackLists.lists.Count; i++)
        {
            var (list, type, source) = trackLists.lists[i];

            List<Track> existing = new List<Track>();
            List<Track> notFound = new List<Track>();

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
                    if (list.Count == 0 || list[0].Count == 0)
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

            if (skipExisting)
            {
                existing = DoSkipExisting(list[0], print: i==0, useCache: trackLists.lists.Count > 1);
                foreach (var tracks in list.Skip(1)) DoSkipExisting(tracks, false, useCache: trackLists.lists.Count > 1);
            }

            m3uEditor.Update();

            if (!interactiveMode)
            {
                PrintTracksTbd(list[0].Where(t => t.TrackState == Track.State.Initial).ToList(), existing, notFound, type);
            }

            if (debugPrintTracks || list.Count == 0 || list[0].Count == 0)
            {
                if (i < trackLists.lists.Count - 1) Console.WriteLine();
                continue;
            }

            if (type == TrackLists.ListType.Normal)
            {
                await TracksDownloadNormal(list[0]);
            }
            else if (type == TrackLists.ListType.Album)
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
        if (tracks.Count > 0)
        {
            bool showAll = type != TrackLists.ListType.Normal || debugPrintTracks;
            PrintTracks(tracks, showAll ? int.MaxValue : 10, debugPrintTracksFull, infoFirst: debugPrintTracks);
            if (debugPrintTracksFull && (existing.Count > 0 || notFound.Count > 0))
                Console.WriteLine("\n-----------------------------------------------\n");
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
        if (!(musicDir != "" && outputFolder.StartsWith(musicDir, StringComparison.OrdinalIgnoreCase)) && System.IO.Directory.Exists(outputFolder))
        {
            var d = SkipExisting(tracks, outputFolder, necessaryCond, useTagsCheckExisting, preciseSkip, useCache);
            d.ToList().ForEach(x => existing.TryAdd(x.Key, x.Value));
        }
        if (musicDir != "" && System.IO.Directory.Exists(musicDir))
        {
            if (print) Console.WriteLine($"Checking if tracks exist in library..");
            var d = SkipExisting(tracks, musicDir, necessaryCond, useTagsCheckExisting, preciseSkip, useCache);
            d.ToList().ForEach(x => existing.TryAdd(x.Key, x.Value));
        }
        else if (musicDir != "" && !System.IO.Directory.Exists(musicDir))
            if (print) Console.WriteLine($"Musid dir does not exist: {musicDir}");

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
            int tries = 2;
        retry:
            await WaitForLogin();

            try
            {
                WriteLine($"Search and download {track}", debugOnly: true);
                var savedFilePath = await SearchAndDownload(track);
                lock (trackLists) { tracks[index] = new Track(track) { TrackState=Track.State.Downloaded, DownloadPath=savedFilePath }; }

                if (removeTracksFromSource && !string.IsNullOrEmpty(spotifyUrl))
                    spotifyClient.RemoveTrackFromPlaylist(playlistUri, track.URI);
            }
            catch (Exception ex)
            {
                WriteLine($"Exception thrown: {ex}", debugOnly: true);
                if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                {
                    goto retry;
                }
                else if (ex is SearchAndDownloadException)
                {
                    lock (trackLists) { tracks[index] = new Track(track) { TrackState = Track.State.Failed, FailureReason = ex.Message }; }
                }
                else
                {
                    WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                    if (tries-- > 0)
                        goto retry;
                }
            }
            finally { semaphore.Release(); }

            m3uEditor.Update();
        });

        await Task.WhenAll(downloadTasks);
    }


    static async Task TracksDownloadAlbum(List<List<Track>> list, bool imagesOnly) // bad
    {
        var dlFiles = new ConcurrentDictionary<string, char>();
        var dlAdditionalImages = new ConcurrentDictionary<string, char>();
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

        while (list.Count > 0)
        {
            albumDlFailed = false;
            tracks = interactiveMode ? InteractiveModeAlbum(list) : list[0];
            mainLoopCts = new CancellationTokenSource();
            albumCommonPath = Utils.GreatestCommonPath(tracks.SelectMany(x => x.Downloads.Select(y => y.Value.Item2.Filename)), dirsep: '\\');
            SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);
            var copy = new List<Track>(tracks);

            try
            {
                var downloadTasks = copy.Select(async (track, index) =>
                {
                    if (track.TrackState == Track.State.Exists || track.TrackState == Track.State.NotFoundLastTime)
                        return;
                    await semaphore.WaitAsync(mainLoopCts.Token);
                    int tries = 2;
                retry:
                    await WaitForLogin();
                    mainLoopCts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var savedFilePath = await SearchAndDownload(track);
                        dlFiles.TryAdd(savedFilePath, char.MinValue);
                        lock (trackLists)
                        {
                            tracks[index] = new Track(track) { TrackState = Track.State.Downloaded, DownloadPath = savedFilePath };
                            if (downloadingImages)
                            {
                                dlAdditionalImages.TryAdd(savedFilePath, char.MinValue);
                                ReplaceTrack(listRef, track, tracks[index]); // shitty shortcut
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                        {
                            goto retry;
                        }
                        else if (ex is SearchAndDownloadException)
                        {
                            lock (trackLists)
                            {
                                tracks[index] = new Track(track) { TrackState = Track.State.Failed, FailureReason = ex.Message };
                                if (downloadingImages)
                                    ReplaceTrack(listRef, track, tracks[index]); // shitty shortcut
                            }
                        }
                        else
                        {
                            WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                            if (tries-- > 0)
                                goto retry;
                        }

                        if (!albumIgnoreFails)
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
                            throw new OperationCanceledException();
                        }
                    }
                    finally { semaphore.Release(); }
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

            if (!downloadingImages && !albumDlFailed && albumArtOption != "")
            {
                prepareImageDownload();
                continue;
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
            Console.WriteLine($"User: {response.Username} ({((float)response.UploadSpeed / (1024 * 1024)):F3}MB/s)");
            PrintTracks(tracks.Where(t => t.TrackState == Track.State.Initial).ToList(), pathsOnly: true, showAncestors: true);
            Console.WriteLine();
            Console.WriteLine($"Folder {aidx + 1}/{list.Count} [Up/Down/Enter/Esc]");
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

        void responseHandler(SearchResponse r)
        {
            if (r.Files.Count > 0)
            {
                foreach (var file in r.Files)
                    results.TryAdd(r.Username + "\\" + file.Filename, (r, file));

                if (fastSearch)
                {
                    var f = r.Files.First();
                    if (r.HasFreeUploadSlot && r.UploadSpeed / 1024 / 1024 >= 1 && preferredCond.FileSatisfies(f, track, r))
                    {
                        lock (downloadingLocker)
                        {
                            if (!downloading)
                            {
                                downloading = true;
                                saveFilePath = GetSavePath(f.Filename);
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

        lock (downloadingLocker) { }
        searches.TryRemove(track, out _);

        if (!downloading && results.IsEmpty && !useYtdlp)
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

        if (debugDisableDownload && results.IsEmpty)
        {
            WriteLine($"No results", ConsoleColor.Yellow);
            return "";
        }
        else if (!downloading && !results.IsEmpty)
        {
            var random = new Random();
            var fileResponses = OrderedResults(results, track, badUsers, true);

            if (debugDisableDownload)
            {
                foreach (var (response, file) in fileResponses) {
                    Console.WriteLine(DisplayString(track, file, response,
                        (printResultsFull ? necessaryCond : null), (printResultsFull ? preferredCond : null), printResultsFull, infoFirst: true));
                }
                WriteLine($"Total: {fileResponses.Count()}\n", ConsoleColor.Yellow);
                return "";
            }

            var newBadUsers = new ConcurrentBag<string>();
            var ignoredResults = new ConcurrentDictionary<string, (SlResponse, SlFile)>();
            foreach (var (response, file) in fileResponses)
            {
                if (newBadUsers.Contains(response.Username))
                {
                    ignoredResults.TryAdd(response.Username + "\\" + file.Filename, (response, file));
                    continue;
                }
                saveFilePath = GetSavePath(file.Filename);
                try
                {
                    downloading = true;
                    await DownloadFile(response, file, saveFilePath, track, progress);
                    break;
                }
                catch (Exception e)
                {
                    downloading = false;
                    if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
                        throw;
                    newBadUsers.Add(response.Username);
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
                    foreach (var (length, id, title) in ytResults)
                    {
                        if (necessaryCond.LengthToleranceSatisfies(length, track.Length))
                        {
                            string saveFilePathNoExt = GetSavePathNoExt(title);
                            downloading = true;
                            RefreshOrPrint(progress, 0, $"yt-dlp download: {track}", true);
                            saveFilePath = await YouTube.YtdlpDownload(id, saveFilePathNoExt, ytdlpArgument);
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


    public class SearchAndDownloadException : Exception
    {
        public SearchAndDownloadException(string text = "") : base(text) { }
    }


    static async Task<List<List<Track>>> GetAlbumDownloads(Track track)
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

        var groupedLists = OrderedResults(results, track, albumMode: true)
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

        bool countIsGood(int count, int wantedCount)
        {
            if (wantedCount == -1)
                return true;
            if (albumTrackCountIneq == '+')
                return count >= wantedCount;
            else if (albumTrackCountIneq == '-')
                return count <= wantedCount;
            else
                return count == wantedCount;
        }

        var result = musicFolders
            .Where(x => countIsGood(x.Item2.Count(rf => Utils.IsMusicFile(rf.file.Filename)), albumTrackCount))
            .Select(ls => ls.Item2.Select(x => {
                var t = new Track
                {
                    ArtistName = track.ArtistName,
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
            .GroupBy(/*(Func<(SlResponse r, SlFile f), Track>)*/inferTrack, new TrackStringComparer(ignoreCase: true))
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
        Track track, IEnumerable<string>? ignoreUsers=null, bool useInfer=false, bool useLevenshtein=true, bool albumMode=false)
    {
        bool useBracketCheck = true;
        if (albumMode)
        {
            useBracketCheck = false;
            useLevenshtein = false;
            useInfer = false;
            preferredCond.StrictTitle = false; // bad!
            necessaryCond.StrictTitle = false; // bad!
        }

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

        (Track, int) infTrack((SearchResponse response, Soulseek.File file) x)
        {
            string key = $"{x.response.Username}\\{x.file.Filename}";
            if (result != null && result.ContainsKey(key))
                return result[key];
            return (new Track(), 0);
        }

        bool bracketCheck((SearchResponse response, Soulseek.File file) x)
        {
            Track inferredTrack = infTrack(x).Item1;
            string t1 = track.TrackTitle.RemoveFt().Replace('[', '(');
            string t2 = inferredTrack.TrackTitle.RemoveFt().Replace('[', '(');
            return track.ArtistMaybeWrong || t1.Contains('(') || !t2.Contains('(');
        }

        int levenshtein((SearchResponse response, Soulseek.File file) x)
        {
            Track inferredTrack = infTrack(x).Item1;
            string t1 = track.TrackTitle.ReplaceInvalidChars("").Replace(" ", "").Replace("_", "").RemoveFt().ToLower();
            string t2 = inferredTrack.TrackTitle.ReplaceInvalidChars("").Replace(" ", "").Replace("_", "").RemoveFt().ToLower();
            return Utils.Levenshtein(t1, t2);
        }

        // giga sort algorithm. I have no idea which parts meaningully improve it and which parts are useless.
        var random = new Random();
        return results.Select(kvp => (response: kvp.Value.Item1, file: kvp.Value.Item2))
                .OrderByDescending(x => !ignoreUsers?.Contains(x.response.Username))
                .ThenByDescending(x => necessaryCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => (x.file.Length != null && x.file.Length > 0) || preferredCond.AcceptNoLength)
                .ThenByDescending(x => preferredCond.BannedUsersSatisfies(x.response))
                .ThenByDescending(x => !useBracketCheck || bracketCheck(x)) // deprioritize result if it contains ( or [ and the track title doesn't (avoid remixes)
                .ThenByDescending(x => preferredCond.StrictTitleSatisfies(x.file.Filename, track.TrackTitle))
                .ThenByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FormatSatisfies(x.file.Filename))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 1024 / 650)
                .ThenByDescending(x => albumMode || FileConditions.StrictString(x.file.Filename, track.TrackTitle, ignoreCase: true))
                .ThenByDescending(x => !albumMode || FileConditions.StrictString(GetDirectoryNameSlsk(x.file.Filename), track.Album, ignoreCase: true))
                .ThenByDescending(x => FileConditions.StrictString(x.file.Filename, track.ArtistName, ignoreCase: true))
                .ThenByDescending(x => !useLevenshtein || levenshtein(x) <= 5) // sorts by the distance between the track title and the (inferred) track title of the search result
                .ThenByDescending(x => x.response.UploadSpeed / 1024 / 300)
                .ThenByDescending(x => (x.file.BitRate ?? 0) / 70)
                .ThenByDescending(x => useInfer ? infTrack(x).Item2 : 0) // sorts by the number of occurences of this track
                .ThenByDescending(x => random.Next());
    }


    static async Task RunSearches(Track track, SlDictionary results, Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions, 
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

        if (results.IsEmpty && track.ArtistMaybeWrong && title)
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
                    searchTasks.Add(Search($"{track.ArtistName} {track.Album}", opts, responseHandler, ct, onSearch));
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
                    searchTasks.Add(Search($"{track.ArtistName} {track.TrackTitle}", opts, responseHandler, ct, onSearch));
                }
            }

            await Task.WhenAll(searchTasks);

            if (results.IsEmpty)
            {
                var track2 = track.ArtistMaybeWrong ? InferTrack(track.TrackTitle, new Track()) : track;

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
                if (track2.TrackTitle.Length > 3 && artist)
                {
                    var cond = new FileConditions(necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
                    var opts = getSearchOptions(Math.Min(searchTimeout, 5000), cond, preferredCond);
                    searchTasks.Add(Search($"{track2.TrackTitle}", opts, responseHandler, ct, onSearch));
                }
                if (track2.ArtistName.Length > 3 && title)
                {
                    var cond = new FileConditions(necessaryCond)
                    {
                        StrictTitle = !track.ArtistMaybeWrong,
                        StrictArtist = !track.ArtistMaybeWrong,
                        LengthTolerance = -1
                    };
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
        string old;
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
                str = str.Replace(s, string.Concat("*", s.AsSpan(1)), StringComparison.OrdinalIgnoreCase);
        }

        return str.Trim();
    }


    public static Track InferTrack(string filename, Track defaultTrack)
    {
        Track t = new Track(defaultTrack);
        filename = GetFileNameWithoutExtSlsk(filename).Replace(" — ", " - ").Replace("_", " ").RemoveConsecutiveWs().Trim();

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

        await WaitForLogin();
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        string origPath = filePath;
        filePath += ".incomplete";

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
            using var cts = new CancellationTokenSource();
            using var outputStream = new FileStream(filePath, FileMode.Create);
            downloads.TryAdd(file.Filename, new DownloadWrapper(origPath, response, file, track, cts, progress));
            await client.DownloadAsync(response.Username, file.Filename, () => Task.FromResult((Stream)outputStream), file.Size, options: transferOptions, cancellationToken: cts.Token);
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
            if (!skipUpdate)
            {
                try
                {
                    if (client.State.HasFlag(SoulseekClientStates.LoggedIn))
                    {
                        foreach (var (key, val) in searches) // shouldn't this give "collection was modified" errors? whatever..
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
        public bool AcceptNoLength = false;
        public bool AcceptMissingProps = true;

        public FileConditions() { }

        public FileConditions(FileConditions other)
        {
            Array.Resize(ref Formats, other.Formats.Length);
            Array.Copy(other.Formats, Formats, other.Formats.Length);
            LengthTolerance = other.LengthTolerance;
            MinBitrate = other.MinBitrate;
            MaxBitrate = other.MaxBitrate;
            MinSampleRate = other.MinSampleRate;
            MaxSampleRate = other.MaxSampleRate;
            DangerWords = other.DangerWords.ToArray();
            BannedUsers = other.BannedUsers.ToArray();
            AcceptNoLength = other.AcceptNoLength;
            StrictArtist = other.StrictArtist;
            StrictTitle = other.StrictTitle;
            MinBitDepth = other.MinBitDepth;
            MaxBitDepth = other.MaxBitDepth;
        }

        public bool FileSatisfies(Soulseek.File file, Track track, SearchResponse? response)
        {
            return DangerWordSatisfies(file.Filename, track.TrackTitle, track.ArtistName) && FormatSatisfies(file.Filename) 
                && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file) 
                && StrictTitleSatisfies(file.Filename, track.TrackTitle) && StrictArtistSatisfies(file.Filename, track.ArtistName) 
                && StrictAlbumSatisfies(file.Filename, track.Album) && BannedUsersSatisfies(response) && BitDepthSatisfies(file);
        }

        public bool FileSatisfies(TagLib.File file, Track track)
        {
            return DangerWordSatisfies(file.Name, track.TrackTitle, track.ArtistName) && FormatSatisfies(file.Name) 
                && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file) 
                && StrictTitleSatisfies(file.Name, track.TrackTitle) && StrictArtistSatisfies(file.Name, track.ArtistName)
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

            return StrictString(fname, aname, StrictStringRegexRemove, StrictStringDiacrRemove, ignoreCase: true);
        }

        public bool StrictAlbumSatisfies(string fname, string alname)
        {
            if (!StrictAlbum || alname == "")
                return true;

            return StrictString(GetDirectoryNameSlsk(fname), alname, StrictStringRegexRemove, StrictStringDiacrRemove, ignoreCase: true);
        }

        public static bool StrictString(string fname, string tname, string regexRemove = "", bool diacrRemove = true, bool ignoreCase = true)
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
            if (!BitDepthSatisfies(file))
                return "BitDepth fails";
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
            if (!BitDepthSatisfies(file))
                return "BitDepth fails";
            return "Satisfied";
        }
    }


    static async Task<List<Track>> ParseCsvIntoTrackInfo(string path, string artistCol = "", string trackCol = "",
        string lengthCol = "", string albumCol = "", string descCol = "", string ytIdCol = "", string timeUnit = "s", bool ytParse = false)
    {
        var tracks = new List<Track>();
        using var sr = new StreamReader(path, System.Text.Encoding.UTF8);
        var parser = new SmallestCSV.SmallestCSVParser(sr);

        var header = parser.ReadNextRow();
        while (header == null || header.Count == 0 || !header.Any(t => t.Trim() != ""))
            header = parser.ReadNextRow();

        string[] cols = { artistCol, albumCol, trackCol, lengthCol, descCol, ytIdCol };
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
        int artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex;
        (artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex) = (indices[0], indices[1], indices[2], indices[3], indices[4], indices[5]);

        while (true)
        {
            var values = parser.ReadNextRow();
            if (values == null)
                break;
            if (!values.Any(t => t.Trim() != ""))
                continue;
            while (values.Count < foundCount)
                values.Add("");

            var desc = "";

            var track = new Track();
            if (artistIndex >= 0) track.ArtistName = values[artistIndex];
            if (trackIndex >= 0) track.TrackTitle = values[trackIndex];
            if (albumIndex >= 0) track.Album = values[albumIndex];
            if (descIndex >= 0) desc = values[descIndex];
            if (ytIdIndex >= 0) track.URI = values[ytIdIndex];
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
                track = await YouTube.ParseTrackInfo(track.TrackTitle, track.ArtistName, track.URI, track.Length, true, desc);

            if (track.TrackTitle != "" || track.ArtistName != "" || track.Album != "")
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

    static string ApplyNamingFormat(string filepath)
    {
        if (nameFormat == "" || !Utils.IsMusicFile(filepath))
            return filepath;

        string add = Path.GetRelativePath(outputFolder, Path.GetDirectoryName(filepath));
        string newFilePath = NamingFormat(filepath, nameFormat);
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
            foreach (Match match in matches.Cast<Match>())
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

    static Dictionary<Track, string> SkipExisting(List<Track> tracks, string dir, FileConditions necessaryCond, bool useTags, bool precise, bool useCache)
    {
        var existing = new Dictionary<Track, string>();
        List<string> musicFiles;
        List<TagLib.File> musicIndex;

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
        FileConditions? pref=null, bool fullpath=false, string customPath="", bool infoFirst=false)
    {
        if (file == null)
            return t.ToString();

        string sampleRate = file.SampleRate.HasValue ? $"{file.SampleRate}Hz" : "";
        string bitRate = file.BitRate.HasValue ? $"{file.BitRate}kbps" : "";
        string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string fname = fullpath ? "\\" + file.Filename : "\\..\\" + (customPath == "" ? GetFileNameSlsk(file.Filename) : customPath);
        string length = Utils.IsMusicFile(file.Filename) ? (file.Length ?? -1).ToString() + "s" : "";
        string displayText;
        if (!infoFirst)
        {
            string info = string.Join('/', new string[] { length, sampleRate+bitRate, fileSize }.Where(value => value!=""));
            displayText = $"{response?.Username ?? ""}{fname} [{info}]";
        }
        else
        {
            string info = string.Join('/', new string[] { length.PadRight(4), (sampleRate+bitRate).PadRight(8), fileSize.PadLeft(6) });
            displayText = $"[{info}] {response?.Username ?? ""}{fname}";
        }

        string necStr = nec != null ? $"nec:{nec.GetNotSatisfiedName(file, t, response)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, t, response)}" : "";
        string cond = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }

    static void PrintTracks(List<Track> tracks, int number = int.MaxValue, bool fullInfo=false, bool pathsOnly=false, bool showAncestors=false, bool infoFirst=false)
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
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, infoFirst: infoFirst));
                    else
                        Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst));
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
                    Console.WriteLine($"  Title:              {tracks[i].TrackTitle}");
                    Console.WriteLine($"  Artist:             {tracks[i].ArtistName}");
                    if (!tracks[i].TrackIsAlbum)
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
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, infoFirst: infoFirst));
                            else
                                Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst));
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
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, infoFirst: infoFirst));
                        else
                            Console.WriteLine("    " + DisplayString(tracks[i], x.Value.Item2, x.Value.Item1, customPath: x.Value.Item2.Filename.Replace(ancestor, ""), infoFirst: infoFirst));
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
            else if (album || (flatList[i].Album != "" && flatList[i].TrackTitle == ""))
            {
                res.AddEntry(ListType.Album, new Track(flatList[i]) { TrackIsAlbum = true });
            }
            else
            {
                res.AddEntry(ListType.Normal);
                while (i < flatList.Count && (flatList[i].Album == "" || flatList[i].TrackTitle != ""))
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
    public string TrackTitle = "";
    public string ArtistName = "";
    public string Album = "";
    public string URI = "";
    public int Length = -1;
    public bool ArtistMaybeWrong = false;
    public bool TrackIsAlbum = false;
    public bool IsNotAudio = false;
    public SlDictionary? Downloads = null;
    public State TrackState = State.Initial;
    public string FailureReason = "";
    public string DownloadPath = "";

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
        TrackTitle = other.TrackTitle;
        ArtistName = other.ArtistName;
        Album = other.Album;
        Length = other.Length;
        URI = other.URI;
        ArtistMaybeWrong = other.ArtistMaybeWrong;
        Downloads = other.Downloads;
        TrackIsAlbum = other.TrackIsAlbum;
        IsNotAudio = other.IsNotAudio;
        TrackState = other.TrackState;
        FailureReason = other.FailureReason;
        DownloadPath = other.DownloadPath;
    }

    public override readonly string ToString()
    {
        return ToString(false);
    }

    public readonly string ToString(bool noInfo = false)
    {
        if (IsNotAudio && Downloads != null && !Downloads.IsEmpty)
            return $"{Program.GetFileNameSlsk(Downloads.First().Value.Item2.Filename)}";

        string str = ArtistName;
        if (!TrackIsAlbum && TrackTitle == "" && Downloads != null && !Downloads.IsEmpty)
        {
            str = $"{Program.GetFileNameSlsk(Downloads.First().Value.Item2.Filename)}";
        }
        else if (TrackTitle != "" || Album != "")
        {
            if (str != "")
                str += " - ";
            if (TrackTitle != "")
                str += TrackTitle;
            else if (TrackIsAlbum)
                str += Album;
            if (!noInfo)
            {
                if (Length > 0)
                    str += $" ({Length}s)";
                if (TrackIsAlbum)
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
        m3uListLabels = false;/*trackLists.lists.Any(x => x.type != TrackLists.ListType.Normal);*/
        fails = ReadAllLines()
            .Where(x => x.StartsWith("# Failed: "))
            .Select(line =>
            {
                var lastBracketIndex = line.LastIndexOf('[');
                lastBracketIndex = lastBracketIndex == -1 ? line.Length : lastBracketIndex;
                var key = line.Substring("# Failed: ".Length, lastBracketIndex - "# Failed: ".Length).Trim();
                var value = lastBracketIndex != line.Length ? line.Substring(lastBracketIndex + 1).Trim().TrimEnd(']') : "";
                return new { Key = key, Value = value };
            })
            .ToSafeDictionary(pair => pair.Key, pair => pair.Value);
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
                            if (track.TrackState == Track.State.Downloaded && !Utils.IsMusicFile(track.DownloadPath))
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

