using AngleSharp.Dom;
using Konsole;
using Newtonsoft.Json.Linq;
using Soulseek;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TagLib.Matroska;
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
    static string noRegexSearch = "";
    static string timeUnit = "s";
    static string displayStyle = "single";
    static string input = "";
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
    static bool removeBrackets = false;
    static bool reverse = false;
    static bool useYtdlp = false;
    static bool skipExisting = false;
    static bool skipIfPrefFailed = false;
    static bool createM3u = false;
    static bool m3uOnly = false;
    static bool useTagsCheckExisting = false;
    static bool removeTracksFromSource = false;
    static int maxTracks = int.MaxValue;
    static int minUsersAggregate = 1;
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
        DangerWords = new string[] { "mix", "dj ", " edit", "cover" },
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
        DangerWords = new string[] { "mix", "dj ", " edit", "cover" },
        BannedUsers = { },
        AcceptNoLength = true,
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
    static bool debugPrintTracksFull = false;

    static string inputType = "";

    static void PrintHelp()
    {
        // additional options: --m3u-only, --yt-dlp-f, --skip-if-pref-failed, --slow-output,
        // --no-modify-share-count, --max-retries, --max-results-per-user, --album-search
        // --artist-col, --title-col, --album-col, --length-col, --yt-desc-col, --yt-id-col
        Console.WriteLine("Usage: slsk-batchdl -i <input> [OPTIONS]" +
                            "\n" +
                            "\n  -i --input <input>             <input> is one of the following:" +
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
                            "\n                                 Search string for the track, album, or artist to search for:" +
                            "\n                                 Can either be any typical search text like \"{artist} - {title}\"" +
                            "\n                                 or a comma-separated list like" +
                            "\n                                 \"title=Song Name,artist=Artist Name,length=215\". Allowed" +
                            "\n                                 properties are; title, artist, album, length (in seconds)." +
                            "\n" +
                            "\nOptions:" +
                            "\n  --user <username>              Soulseek username" +
                            "\n  --pass <password>              Soulseek password" +
                            "\n" +
                            "\n  --spotify                      Input is a spotify url (override automatic parsing)" +
                            "\n  --spotify-id <id>              spotify client ID (required for private playlists)" +
                            "\n  --spotify-secret <secret>      spotify client secret (required for private playlists)" +
                            "\n" +
                            "\n  --youtube                      Input is a youtube url (override automatic parsing)" +
                            "\n  --youtube-key <key>            Youtube data API key" +
                            "\n" +
                            "\n  --csv                          Input is a path to a local CSV (override automatic parsing)" +
                            "\n  --time-format <format>         Time format in Length column of the csv file (e.g h:m:s.ms" +
                            "\n                                 for durations like 1:04:35.123). Default: s" +
                            "\n  --yt-parse                     Enable if the csv file contains YouTube video titles and" +
                            "\n                                 channel names; attempt to parse them into proper title and" +
                            "\n                                 artist. If the the csv contains an \"ID\", \"URL\", or" +
                            "\n                                 \"Description\" column then they will be used for parsing too" +
                            "\n" +
                            "\n  --string                       Input is a search string (override automatic parsing)" +
                            "\n  -a --aggregate                 Instead of downloading a single track matching the search" +
                            "\n                                 string, find and download all distinct songs associated with" +
                            "\n                                 the provided artist, album, or track title. Search string must" +
                            "\n                                 be a list of properties." +
                            "\n --min-users-aggregate <num>     Minimum number of users sharing a track before it is" +
                            "\n                                 downloaded in aggregate mode. Setting it to 2 or more will" +
                            "\n                                 significantly reduce false positives, but may introduce false" +
                            "\n                                 negatives. Default: 1" +
                            "\n" +
                            "\n  -p --path <path>               Download folder" +
                            "\n  -f --folder <name>             Subfolder name (default: playlist/csv name)" +
                            "\n  -n --number <maxtracks>        Download the first n tracks of a playlist" +
                            "\n  -o --offset <offset>           Skip a specified number of tracks" +
                            "\n  --reverse                      Download tracks in reverse order" +
                            "\n  --remove-from-playlist         Remove downloaded tracks from playlist (for spotify only)" +
                            "\n  --name-format <format>         Name format for downloaded tracks, e.g \"{artist} - {title}\"" +
                            "\n  --m3u                          Create an m3u8 playlist file" +
                            "\n" +
                            "\n  --format <format>              Accepted file format(s), comma-separated" +
                            "\n  --length-tol <tol>             Length tolerance in seconds (default: 3)" +
                            "\n  --min-bitrate <rate>           Minimum file bitrate" +
                            "\n  --max-bitrate <rate>           Maximum file bitrate" +
                            "\n  --max-samplerate <rate>        Maximum file sample rate" +
                            "\n  --strict-title                 Only download if filename contains track title" +
                            "\n  --strict-artist                Only download if filepath contains track artist" +
                            "\n  --banned-users <list>          Comma-separated list of users to ignore" +
                            "\n  --danger-words <list>          Comma-separated list of words that must appear in either" +
                            "\n                                 both search result and track title or in neither of the" +
                            "\n                                 two. Case-insensitive. (default:\"mix, edit, dj, cover\")" +
                            "\n  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)" +
                            "\n  --pref-length-tol <tol>        Preferred length tolerance in seconds (default: 3)" +
                            "\n  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)" +
                            "\n  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2200)" +
                            "\n  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 96000)" +
                            "\n  --pref-strict-title            Prefer download if filename contains track title" +
                            "\n  --pref-strict-artist           Prefer download if filepath contains track artist" +
                            "\n  --pref-banned-users <list>     Comma-separated list of users to deprioritize" +
                            "\n  --pref-danger-words <list>     Comma-separated list of words that should appear in either" +
                            "\n                                 both search result and track title or in neither of the" +
                            "\n                                 two." +
                            "\n" +
                            "\n  -s --skip-existing                Skip if a track matching file conditions is found in the" +
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
                            "\n                                 track names before searching." +
                            "\n  --remove-brackets              Remove text in square brackets from track names before" +
                            "\n                                 searching." +
                            "\n  --no-artist-search             Perform a search without artist name if nothing was" +
                            "\n                                 found. Only use for sources such as youtube or soundcloud" +
                            "\n                                 where the \"artist\" could just be an uploader." +
                            "\n  --artist-search                Also try to find track by searching for the artist only" +
                            "\n  --no-regex-search <reg>        Also perform a search without a regex pattern" +
                            "\n  --no-diacr-search              Also perform a search without diacritics" +
                            "\n  -d --desperate                 Equivalent to enabling all additional searches. Slower." +
                            "\n  --yt-dlp                       Use yt-dlp to download tracks that weren't found on" +
                            "\n                                 Soulseek. yt-dlp must be available from the command line." +
                            "\n" +
                            "\n  --config <path>                Specify config file location" +
                            "\n  --search-timeout <ms>          Max search time in ms (default: 6000)" +
                            "\n  --max-stale-time <ms>          Max download time without progress in ms (default: 50000)" +
                            "\n  --concurrent-processes <num>   Max concurrent searches & downloads (default: 2)" +
                            "\n  --display <option>             Changes how searches and downloads are displayed." +
                            "\n                                 single (default): Show transfer state and percentage." +
                            "\n                                 double: Also show a progress bar. " +
                            "\n                                 simple: No download bar" +
                            "\n" +
                            "\n  --print <option>               Only print tracks or results instead of downloading." +
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
                case "--parent":
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
                    else if (opt == "tracks-full") {
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

        if (input == "")
            throw new ArgumentException($"Must provide an -i argument.");

        if (inputType=="youtube" || (inputType == "" && input.Contains("http") && input.Contains("youtu"))) {
            ytUrl = input;
            inputType = "youtube";
        }
        else if (inputType == "spotify" || (inputType == "" && (input.Contains("http") && input.Contains("spotify")) || input == "spotify-likes")) {
            spotifyUrl = input;
            inputType = "spotify";
        }
        else if (inputType == "csv" || (inputType == "" && Path.GetExtension(input).Equals(".csv", StringComparison.OrdinalIgnoreCase))) {
            csvPath = input;
            inputType = "csv";
        }
        else {
            searchStr = input;
            inputType = "string";
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
                folderName = ReplaceInvalidChars(name, " ");

            YouTube.StopService();
        }
        else if (csvPath != "")
        {
            if (!System.IO.File.Exists(csvPath))
                throw new Exception("CSV file not found");

            Console.WriteLine("Parsing CSV track info");
            tracks = await ParseCsvIntoTrackInfo(csvPath, artistCol, trackCol, lengthCol, albumCol, descCol, ytIdCol, timeUnit, ytParse);
            tracks = tracks.Skip(off).Take(max).ToList();

            if (folderName == "")
                folderName = Path.GetFileNameWithoutExtension(csvPath);
        }
        else if (searchStr != "" && !aggregate)
        {
            tracks.Add(ParseTrackArg(searchStr));
            writeFails = false;
        }
        else if (searchStr != "" && aggregate)
        {
            writeFails = false;
            if (folderName == "")
                folderName = ReplaceInvalidChars(searchStr, " ");
            var music = ParseTrackArg(searchStr);
            await WaitForInternetConnection();
            await client.ConnectAsync(username, password);
            if (!noModifyShareCount)
                await client.SetSharedCountsAsync(10, 50);

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

        if (removeBrackets)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                Track track = tracks[i];
                track.TrackTitle = track.TrackTitle.RemoveSquareBrackets();
                tracks[i] = track;
            }
        }

        folderName = ReplaceInvalidChars(folderName, " ");

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

        if ((skipExisting || m3uOnly))
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

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new Exception("No soulseek username or password");

        await WaitForInternetConnection();
        if (!aggregate) {
            await client.ConnectAsync(username, password);
            if (!noModifyShareCount)
                await client.SetSharedCountsAsync(10, 50);
        }

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

                    if (removeTracksFromSource && !string.IsNullOrEmpty(spotifyUrl))
                        spotifyClient.RemoveTrackFromPlaylist(playlistUri, track.URI);

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


    static async Task<string> SearchAndDownload(Track track)
    {
        Console.ResetColor();
        ProgressBar? progress = GetProgressBar(displayStyle);
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        var cts = new CancellationTokenSource();
        var saveFilePath = "";
        bool attemptedDownloadPref = false;
        Task? downloadTask = null;
        object downloadingLocker = new object();
        bool downloading = false;
        bool notFound = false;

        if (track.Downloads != null) {
            results = track.Downloads;
            goto downloads;
        }

        RefreshOrPrint(progress, 0, $"Searching: {track}", true);

        var title = track.ArtistName != "" ? $"{track.ArtistName} - {track.TrackTitle}" : $"{track.TrackTitle}";
        string searchText = $"{title}";
        var removeChars = new string[] { " ", "_", "-" };

        if (track.TrackTitle.Replace(removeChars, "").ReplaceInvalidChars("") == "")
        {
            RefreshOrPrint(progress, 0, $"Track title only contains invalid characters: {title}, not searching", true);
            WriteLineOutputFile($"{title} [Track title has only invalid chars]");
            return "";
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

                    var f = r.Files.First();
                    if (cond.FileSatisfies(f, track, r) && r.HasFreeUploadSlot && r.UploadSpeed / 1000000 >= 1)
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
            var fileResponses = OrderedResults(results, track);

            if (debugDisableDownload)
            {
                foreach (var x in fileResponses)
                    Console.WriteLine(DisplayString(track, x.file, x.response,
                        (printResultsFull ? necessaryCond : null), preferredCond, printResultsFull));
                WriteLine($"Total: {fileResponses.Count()}\n", ConsoleColor.Yellow);
                return "";
            }

            foreach (var x in fileResponses)
            {
                bool pref = preferredCond.FileSatisfies(x.file, track, x.response);
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


    static async Task<List<Track>> GetUniqueRelatedTracks(Track track)
    {
        var results = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>();
        var opts = new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                searchTimeout: searchTimeout,
                responseFilter: (response) => {
                    return response.UploadSpeed > 0
                            && necessaryCond.BannedUsersSatisfies(response);
                },
                fileFilter: (file) => {
                    return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track, null);
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
            search = track.ArtistName + " - " + search;

        await RunSearches(search, opts, handler, cts.Token);

        string artistName = track.ArtistName.Trim();
        string trackName = track.TrackTitle.Trim();
        string albumName = track.Album.Trim();
        
        var inferTrack = ((SearchResponse r, Soulseek.File f) x) => {
            Track t = new Track(track);
            t.Length = x.f.Length ?? -1;

            string aname = artistName, tname = trackName, alname = albumName;

            string fpath = GetAsPathSlsk(x.f.Filename);            
            string fname = GetFileNameWithoutExtSlsk(x.f.Filename).Replace(" — ", " - ").Trim();

            var updateIfHelps = (ref string cont, ref string str, string newCont, string newStr) => { 
                if (!cont.Trim().ContainsIgnoreCase(search.Trim()) && newCont.Trim().ContainsIgnoreCase(newStr.Trim())) {
                    cont = newCont.Trim();
                    str = newStr.Trim();
                }
            };

            fname = fname.Replace("_", " ").Trim();
            aname = aname.Replace("_", " ").Trim();
            alname = alname.Replace("_", " ").Trim();

            if (aname != "")
                updateIfHelps(ref fname, ref aname, fname.ReplaceInvalidChars(""), aname.ReplaceInvalidChars(""));
            if (tname != "")
                updateIfHelps(ref fname, ref tname, fname.ReplaceInvalidChars(""), tname.ReplaceInvalidChars(""));
            if (alname != "")
                updateIfHelps(ref fname, ref alname, fname.ReplaceInvalidChars(""), alname.ReplaceInvalidChars(""));

            bool maybeRemix = aname != "" && (fname.ContainsIgnoreCase($"{aname} edit") || fname.ContainsIgnoreCase($"{aname} remix"));

            var trackNumReg = @"^\s*((\d-)\d{2,3}|\d{2,3}\.?)\s*$";
            var trackNumRegWs = @"^\s*((\d-)\d{2,3}|\d{2,3}\.?)\s+$";
            var trackNumStart = @"^(?:(?:[0-9]-)?\d{2,3}[. -])(?=.+\S)";
            var trackNumMiddle = @"(?<=- )\s*((\d-)\d{2,3}|\d{2,3}\.?)\s+";

            if (Regex.Match(fname, trackNumStart).Success || Regex.Match(fname, trackNumMiddle).Success) {
                fname = Regex.Replace(fname, trackNumStart, "").Trim();
                fname = Regex.Replace(fname, trackNumMiddle, "").Trim();
                if (fname.StartsWith("- ")) fname = fname.Substring(2).Trim();
            }

            string[] parts = fname.Split(new string[] { " - " }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 1) {
                if (maybeRemix)
                    t.ArtistMaybeWrong = true;
                t.TrackTitle = parts[0];
            }
            else if (parts.Length == 2) {
                bool hasTitle = tname != "" && parts[1].ContainsIgnoreCase(tname);
                bool hasArtist = aname != "" && (parts[0].ContainsIgnoreCase(aname)
                    || parts[1].ContainsIgnoreCase(aname + " remix") || parts[1].ContainsIgnoreCase(aname + " edit"));

                if (!hasArtist && !hasTitle) {
                    t.ArtistMaybeWrong = true;
                }

                t.ArtistName = parts[0];
                t.TrackTitle = parts[1];
            }
            else if (parts.Length == 3) {
                bool hasTitle = tname != "" && parts[2].ContainsIgnoreCase(tname);
                if (hasTitle)
                    t.TrackTitle = parts[2];

                int artistPos = -1;
                if (aname != "") {
                    if (parts[0].ContainsIgnoreCase(aname))
                        artistPos = 0;
                    else if (parts[1].ContainsIgnoreCase(aname))
                        artistPos = 1;
                    else
                        t.ArtistMaybeWrong = true;
                }
                int albumPos = -1;
                if (alname != "") {
                    if (parts[0].ContainsIgnoreCase(alname))
                        albumPos = 0;
                    else if (parts[1].ContainsIgnoreCase(alname))
                        albumPos = 1;
                }
                if (artistPos >= 0 && artistPos == albumPos) {
                    artistPos = 0;
                    albumPos = 1;
                }
                if (artistPos == -1) {
                    if (aname != "" && parts[2].ContainsIgnoreCase(aname + " remix") || parts[2].ContainsIgnoreCase(aname + " edit")) {
                        artistPos = 0;
                        albumPos = 1;
                    }
                }

                if (artistPos == -1 && albumPos == -1) {
                    t.ArtistMaybeWrong = true;
                }

                t.ArtistName = parts[artistPos];
                t.TrackTitle = parts[2];
            }

            if (t.TrackTitle == "") {
                t.TrackTitle = fname;
                t.ArtistMaybeWrong = true;
            }

            return t;
        };

        var fileResponses = OrderedResults(results, track);

        var equivalentFiles = fileResponses
            .GroupBy(inferTrack, new TrackStringComparer())
            .Where(group => group.Select(x => x.Item1.Username).Distinct().Count() >= minUsersAggregate)
            .SelectMany(group => {
                var sortedTracks = group.OrderBy(t => t.Item2.Length).Where(x => x.Item2.Length != null).ToList();
                var groups = new List<(Track, List<(SearchResponse, Soulseek.File)>)>();
                var noLengthGroup = group.Where(x => x.Item2.Length == null);
                for (int i = 0; i < sortedTracks.Count;) {
                    var subGroup = new List<(SearchResponse, Soulseek.File)> { sortedTracks[i] };
                    int j = i + 1;
                    while (j < sortedTracks.Count) { 
                        int l1 = (int)sortedTracks[j].Item2.Length;
                        int l2 = (int)sortedTracks[i].Item2.Length;
                        if (Math.Abs(l1 - l2) <= necessaryCond.LengthTolerance) {
                            subGroup.Add(sortedTracks[j]);
                            j++;
                        }
                        else break;
                    }
                    groups.Add((group.Key, subGroup));
                    i = j;
                }

                if (noLengthGroup.Count() > 0) {
                    if (groups.Count() > 0 && !preferredCond.AcceptNoLength)
                        groups.First().Item2.AddRange(noLengthGroup);
                    else
                        groups.Add((group.Key, noLengthGroup.ToList()));
                }

                return groups.Where(subGroup => subGroup.Item2.Select(x => x.Item1.Username).Distinct().Count() >= minUsersAggregate)
                    .Select(subGroup => (subGroup.Item1, OrderedResults(subGroup.Item2
                        .Select(item => new KeyValuePair<string, (SearchResponse, Soulseek.File)>(subGroup.Item1.ToString(), item)), subGroup.Item1)));
            });


        var tracks = equivalentFiles.Select(kvp => {
                kvp.Item1.Downloads = new ConcurrentDictionary<string, (SearchResponse, Soulseek.File)>(
                    kvp.Item2.ToDictionary(item => { return item.response.Username + "\\" + item.file.Filename; }, item => item));
                return kvp.Item1; })
            .ToList();

        return tracks;
    }


    static IOrderedEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(IEnumerable<KeyValuePair<string, (SearchResponse, Soulseek.File)>> results, Track track)
    {
        var random = new Random();
        return results
                .Select(kvp => (response: kvp.Value.Item1, file: kvp.Value.Item2))
                .OrderByDescending(x => x.file.Length != null || preferredCond.AcceptNoLength)
                .ThenByDescending(x => preferredCond.BannedUsersSatisfies(x.response))
                .ThenByDescending(x => preferredCond.StrictTitleSatisfies(x.file.Filename, track.TrackTitle))
                .ThenByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FormatSatisfies(x.file.Filename))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track, x.response))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 700)
                .ThenByDescending(x => necessaryCond.FileSatisfies(x.file, track, x.response)) 
                .ThenByDescending(x => x.file.Filename.ToLower().Contains(track.Album.ToLower()))
                .ThenByDescending(x => x.response.UploadSpeed / 300)
                .ThenByDescending(x => (x.file.BitRate ?? 0) / 70)
                .ThenBy(x => random.Next());
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
        public string[] BannedUsers = { };
        public string StrictStringRegexRemove = "";
        public bool StricStringDiacrRemove = false;
        public bool AcceptNoLength = false;

        public FileConditions() { }

        public FileConditions(FileConditions other)
        {
            other.Formats.CopyTo(Formats, 0);
            LengthTolerance = other.LengthTolerance;
            MinBitrate = other.MinBitrate;
            MaxBitrate = other.MaxBitrate;
            MaxSampleRate = other.MaxSampleRate;
            DangerWords = other.DangerWords.ToArray();
            BannedUsers = other.BannedUsers.ToArray();
        }

        public bool FileSatisfies(Soulseek.File file, Track track, SearchResponse? response)
        {
            return DangerWordSatisfies(file.Filename, track.TrackTitle) && FormatSatisfies(file.Filename) && LengthToleranceSatisfies(file, track.Length) 
                && BitrateSatisfies(file) && SampleRateSatisfies(file) && StrictTitleSatisfies(file.Filename, track.TrackTitle)
                && StrictArtistSatisfies(file.Filename, track.ArtistName) && BannedUsersSatisfies(response);
        }

        public bool FileSatisfies(TagLib.File file, Track track)
        {
            return DangerWordSatisfies(file.Name, track.TrackTitle) && FormatSatisfies(file.Name) && LengthToleranceSatisfies(file, track.Length) 
                && BitrateSatisfies(file) && SampleRateSatisfies(file) && StrictTitleSatisfies(file.Name, track.TrackTitle) 
                && StrictArtistSatisfies(file.Name, track.ArtistName);
        }

        public bool DangerWordSatisfies(string fname, string tname)
        {
            if (tname == "")
                return true;

            fname = GetFileNameWithoutExtSlsk(fname).Replace(" — ", " - ");
            fname = fname.Split('-', StringSplitOptions.RemoveEmptyEntries).Last().ToLower();
            tname = tname.Replace(" — ", " - ").Split('-', StringSplitOptions.RemoveEmptyEntries).Last().ToLower();

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
            if (!StrictTitle || tname == "")
                return true;

            fname = noPath ? GetFileNameWithoutExtSlsk(fname) : fname;
            return StrictString(fname, tname, StrictStringRegexRemove, StricStringDiacrRemove);
        }

        public bool StrictArtistSatisfies(string fname, string aname)
        {
            if (!StrictArtist || aname == "")
                return true;

            return StrictString(fname, aname, StrictStringRegexRemove, StricStringDiacrRemove);
        }

        public static bool StrictString(string fname, string tname, string regexRemove = "", bool diacrRemove = false)
        {
            if (string.IsNullOrEmpty(tname))
                return true;

            var seps = new string[] { " ", "_", "-" };
            fname = ReplaceInvalidChars(fname.Replace(seps, ""), "");
            fname = regexRemove != "" ? Regex.Replace(fname, regexRemove, "") : fname;
            fname = diacrRemove ? fname.RemoveDiacritics() : fname;
            tname = ReplaceInvalidChars(tname.Replace(seps, ""), "");
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
            if (!BannedUsersSatisfies(response))
                return "BannedUsers fails";
            return "Satisfied";                               
        }

        public string GetNotSatisfiedName(TagLib.File file, Track track)
        {
            if (!DangerWordSatisfies(file.Name, track.TrackTitle))
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
        string? lengthCol = "", string? albumCol = "", string? descCol = "", string? ytIdCol = "", string timeUnit = "", bool ytParse = false)
    {
        if (timeUnit == "") timeUnit = "s";
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

            var regex = new Regex($"{Regex.Escape(d)}(?=(?:[^\"']*\"[^\"']*\")*[^\"']*$)"); // thank you, ChatGPT.

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                var values = regex.Split(line);
                if (!values.Any(t => !string.IsNullOrEmpty(t.Trim())))
                    continue;

                var desc = "";

                var track = new Track();
                if (artistIndex >= 0) track.ArtistName = values[artistIndex].Trim('"').Split(',').First().Trim(' ');
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
        string searchName = track.TrackTitle.Replace(ignore, "");
        searchName = searchName.ReplaceInvalidChars("").RemoveFt().RemoveSquareBrackets();
        searchName = string.IsNullOrEmpty(searchName) ? track.TrackTitle : searchName;

        filename = Path.GetFileNameWithoutExtension(filename);
        filename = filename.ReplaceInvalidChars("");
        filename = filename.Replace(ignore, "");

        if (filename.Contains(searchName, StringComparison.OrdinalIgnoreCase))
            return true;
        else if ((track.ArtistMaybeWrong || string.IsNullOrEmpty(track.ArtistName)) && track.TrackTitle.Count(c => c == '-') == 1)
        {
            searchName = track.TrackTitle.Split('-', StringSplitOptions.RemoveEmptyEntries)[1].Replace(ignore, "");
            searchName = searchName.ReplaceInvalidChars("").RemoveFt().RemoveSquareBrackets();
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
        string artist = track.ArtistName.ToLower().Replace(" ", "").RemoveFt();
        string title = track.TrackTitle.ToLower().Replace(" ", "").RemoveFt().RemoveSquareBrackets();

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
            string fileTitle = f.Tag.Title.ToLower().Replace(" ", "").RemoveFt().RemoveSquareBrackets();

            if (precise && !conditions.FileSatisfies(f, track))
                continue;

            bool durCheck = conditions.LengthToleranceSatisfies(f, track.Length);
            bool check1 = (artist.Contains(fileArtist) || title.Contains(fileArtist)) && (!precise || durCheck);
            bool check2 = !precise && fileTitle.Length >= 6 && durCheck;

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

    static string ReplaceInvalidChars(this string str, string replaceStr)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
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
        string displayText = $"{response?.Username ?? ""}{fname} [{file.Length}s{sampleRate}{bitRate}/{fileSize}]";

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
                Console.WriteLine($"  Album:              {tracks[i].Album}");
                Console.WriteLine($"  Length:             {tracks[i].Length}s");
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
    public TrackStringComparer() { }

    public bool Equals(Track a, Track b) {
        if (a.Equals(b))
            return true;

        return a.TrackTitle.Equals(b.TrackTitle)
            && a.ArtistName.Equals(b.ArtistName)
            && a.Album.Equals(b.Album);
    }
    public int GetHashCode(Track a)
    {
        unchecked {
            int hash = 17;
            hash = hash * 23 + a.TrackTitle?.GetHashCode() ?? 0;
            hash = hash * 23 + a.ArtistName?.GetHashCode() ?? 0;
            hash = hash * 23 + a.Album?.GetHashCode() ?? 0;
            return hash;
        }
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
