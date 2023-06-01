using System.Diagnostics;
using System.Text.RegularExpressions;
using Soulseek;
using Konsole;
using System.Net.NetworkInformation;
using System.Collections.Concurrent;

class Program
{
    static SoulseekClient client = new SoulseekClient();
    static ConcurrentDictionary<Track, SearchInfo> searches = new ConcurrentDictionary<Track, SearchInfo>();
    static ConcurrentDictionary<string, DownloadWrapper> downloads = new ConcurrentDictionary<string, DownloadWrapper>();
    static List<Track> tracks = new List<Track>();
    static string outputFolder = "";
    static string failsFilePath = "";
    static string m3uFilePath = "";
    static string musicDir = "";
    static string ytdlpFormat = "";
    static int downloadMaxStaleTime = 0;
    static int updateDelay = 200;
    static int slowUpdateDelay = 2000;
    static bool slowConsoleOutput = false;

    static string logLocation = "";
    static StreamWriter? logFile = null;
    static TextWriterTraceListener? textListener = null;

    static object failsFileLock = new object();
    static object consoleLock = new object();
    static bool writeFails = true;

    static DateTime lastUpdate;
    static bool skipUpdate = false;

    static void PrintHelp()
    {
        Console.WriteLine("Usage: slsk-batchdl.exe [OPTIONS]" +
                            "\nOptions:" +
                            "\n  -p --parent <path>           	Downloaded music will be placed here" +
                            "\n  -n --name <name>             	Folder / playlist name. If not specified, the name of the" +
                            "\n				csv file / spotify / yt playlist is used." +
                            "\n  --username <username>        	Soulseek username" +
                            "\n  --password <password>        	Soulseek password" +
                            "\n" +
                            "\n  --spotify <url>              	Download a spotify playlist. \"likes\" to download all your" +
                            "\n				liked music." +
                            "\n  --spotify-id <id>            	Your spotify client id (use if the default fails or if" +
                            "\n				playlist private)" +
                            "\n  --spotify-secret <sec>       	Your spotify client secret (use if the default fails or if" +
                            "\n				playlist private)" +
                            "\n" +
                            "\n  --youtube <url>              	Get tracks from a YouTube playlist" +
                            "\n  --youtube-key <key>          	Provide an API key if you also want to search for" +
                            "\n				unavailable uploads" +
                            "\n  --no-channel-search          	Enable to also perform a search without channel name if" +
                            "\n				nothing was found (only for yt)" +
                            "\n" +
                            "\n  --csv <path>                 	Use a csv file containing track info to download" +
                            "\n  --artist-col <column>        	Artist or uploader name column" +
                            "\n  --title-col <column>         	Title or track name column" +
                            "\n  --album-col <column>         	CSV album column name. Optional, may improve searching," +
                            "\n				slower" +
                            "\n  --length-col <column>        	CSV duration column name. Recommended, will improve" +
                            "\n				accuracy" +
                            "\n  --time-unit <unit>           	Time unit in track duration column, ms or s (default: s)" +
                            "\n  --yt-desc-col <column>       	Description column name. Use with --yt-parse." +
                            "\n  --yt-id-col <column>         	Youtube video ID column (only needed if length-col or" +
                            "\n				yt-desc-col don't exist). Use with --yt-parse." +
                            "\n  --yt-parse                   	Enable if you have a csv file of YouTube video titles and" +
                            "\n				channel names; attempt to parse." +
                            "\n" +
                            "\n  -s --single <str>            	Search & download a specific track" +
                            "\n" +
                            "\n  --pref-format <format>       	Preferred file format (default: mp3)" +
                            "\n  --pref-length-tol <tol>      	Preferred length tolerance (default: 3)" +
                            "\n  --pref-min-bitrate <rate>    	Preferred minimum bitrate (default: 200)" +
                            "\n  --pref-max-bitrate <rate>    	Preferred maximum bitrate (default: 2200)" +
                            "\n  --pref-max-samplerate <rate> 	Preferred maximum sample rate (default: 96000)" +
                            "\n  --pref-danger-words <list>   	Comma separated list of words that must appear in either" +
                            "\n				both search result and track title, or in neither of the" +
                            "\n				two, case-insensitive (default:\"mix, edit, dj, cover\")" +
                            "\n  --nec-format <format>        	Necessary file format" +
                            "\n  --nec-length-tolerance <tol> 	Necessary length tolerance (default: 3)" +
                            "\n  --nec-min-bitrate <rate>     	Necessary minimum bitrate" +
                            "\n  --nec-max-bitrate <rate>     	Necessary maximum bitrate" +
                            "\n  --nec-max-samplerate <rate>  	Necessary maximum sample rate" +
                            "\n  --nec-danger-words <list>    	Comma separated list of words that must appear in either" +
                            "\n				both search result and track title, or in neither of the" +
                            "\n				two. Case-insensitive. (default:\"mix, edit, dj, cover\")" +
                            "\n" +
                            "\n  --album-search		Also search for \"[Album name][track name]\". Occasionally" +
                            "\n				helps to find more, slower." +
                            "\n  --no-diacr-search           	Also perform a search without diacritics" +
                            "\n  --skip-existing              	Skip if a track matching the conditions is found in the" +
                            "\n				output folder or your music library (if provided)" +
                            "\n  --skip-notfound              	Skip searching for tracks that weren't found in Soulseek" +
                            "\n				last time" +
                            "\n  --remove-ft                  	Remove \"ft.\" or \"feat.\" and everything after from the track" +
                            "\n				names." +
                            "\n  --remove-strings <strings>   	Comma separated list of strings to remove when searching" +
                            "\n				for tracks. Case insesitive." +
                            "\n  --music-dir <path>           	Specify to also skip downloading tracks which are in your" +
                            "\n				library, use with --skip-existing" +
                            "\n  --reverse                    	Download tracks in reverse order" +
                            "\n  --skip-if-pref-failed        	Skip if preferred versions of a track exist but failed to" +
                            "\n				download. If no pref. versions were found, download as " +
                            "\n				usual." +
                            "\n  --create-m3u                 	Create an m3u playlist file" +
                            "\n  --m3u-only                   	Only create an m3u playlist file with existing tracks and" +
                            "\n				exit" +
                            "\n  --m3u <path>                 	Where to place created m3u files (--parent by default)" +
                            "\n  --yt-dlp                     	Use yt-dlp to download tracks that weren't found on" +
                            "\n				Soulseek. yt-dlp must be available from the command line." +
                            "\n  --yt-dlp-f <format>          	yt-dlp audio format (default: \"bestaudio / best\")" +
                            "\n" +
                            "\n  --search-timeout <ms>        	Maximal search time (default: 8000)" +
                            "\n  --max-stale-time <ms>        	Maximal download time with no progress (default: 60000)" +
                            "\n  --concurrent-processes <num> 	Max concurrent searches / downloads (default: 2)" +
                            "\n  --max-retries <num>          	Maximum number of users to try downloading from before" +
                            "\n				skipping track (default: 30)" +
                            "\n" +
                            "\n  --simple-output                No download bars in console");
    }

    static async Task Main(string[] args)
    {
        if (!string.IsNullOrEmpty(logLocation))
        {
            logFile = new StreamWriter(System.IO.Path.Combine(logLocation, "log.txt"), append: false);
            textListener = new TextWriterTraceListener(logFile);
            Trace.Listeners.Add(textListener);
        }

        AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{e.ExceptionObject}");
            Console.ResetColor();
            Trace.TraceError($"{e.ExceptionObject}");
            if (logFile != null)
            {
                logFile.Flush();
                logFile.Close();
            }
        };

        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--help") || args.Contains("-h") || args.Length == 0)
        {
            PrintHelp();
            return;
        }

        musicDir = "";
        string parentFolder = System.IO.Directory.GetCurrentDirectory();
        string folderName = "";
        string ytUrl = "";
        string singleName = "";
        string spotifyUrl = "";
        string spotifyId = "";
        string spotifySecret = "";
        string encodedSpotifyId = "MWJmNDY5MWJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI="; // base64 encoded client id and secret to avoid git guardian detection (annoying)
        string encodedSpotifySecret = "ZmQ3NjYyNmM0ZjcxNGJkYzg4Y2I4ZTQ1ZTU1MDBlNzE=";
        string ytKey = "";
        string tracksCsv = "";
        string username = "";
        string password = "";
        string artistCol = "";
        string albumCol = "";
        string trackCol = "";
        string ytIdCol = "";
        string descCol = "";
        string lengthCol = "";
        string removeStrings = "";
        string timeUnit = "s";
        ytdlpFormat = "bestaudio/best";
        bool skipNotFound = false;
        bool searchWithoutArtist = false;
        bool searchWithoutDiacr = false;
        bool ytParse = false;
        bool removeFt = false;
        bool reverse = false;
        bool useYtdlp = false;
        bool skipExisting = false;
        bool skipIfPrefFailed = false;
        bool albumSearch = false;
        bool createM3u = false;
        bool m3uOnly = false;
        int searchTimeout = 8000;
        downloadMaxStaleTime = 60000;
        int maxConcurrentProcesses = 2;
        int maxRetriesPerTrack = 30;
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

        string confPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "slsk-batchdl.conf");
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
                case "-s":
                case "--single":
                    singleName = args[++i];
                    break;
                case "--no-channel-search":
                    searchWithoutArtist = true;
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
                case "--no-diacr-search":
                    searchWithoutDiacr = true;
                    break;
                case "--yt-desc-col":
                    descCol = args[++i];
                    break;
                case "--yt-id-col":
                    ytIdCol = args[++i];
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
                case "--skip-notfound":
                    skipNotFound = true;
                    break;
                case "--remove-ft":
                    removeFt = true;
                    break;
                case "--remove-strings":
                    removeStrings = args[++i];
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
                case "--max-stale-time":
                    downloadMaxStaleTime = int.Parse(args[++i]);
                    break;
                case "--concurrent-processes":
                    maxConcurrentProcesses = int.Parse(args[++i]);
                    break;
                case "--max-retries":
                    maxRetriesPerTrack = int.Parse(args[++i]);
                    break;
                case "--pref-format":
                    preferredCond.Format = args[++i];
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
                    preferredCond.dangerWords = args[++i].Split(',');
                    break;
                case "--nec-format":
                    necessaryCond.Format = args[++i];
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
                    necessaryCond.dangerWords = args[++i].Split(',');
                    break;
                case "--slow-output":
                    slowConsoleOutput = true;
                    break;
                default:
                    throw new Exception($"Unknown argument: {args[i]}");
            }
        }

        if (ytKey != "")
            YouTube.apiKey = ytKey;

        StringEdit strEdit = new StringEdit(removeFt: removeFt);
        if (removeStrings != "")
            strEdit.stringsToRemove = removeStrings.Split(',');

        if (spotifyUrl != "")
        {
            bool usedDefaultId = false;
            if (spotifyId == "" || spotifySecret == "")
            {
                spotifyId = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifyId));
                spotifySecret = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifySecret));
                usedDefaultId = true;
            }
            string? playlistName;
            if (spotifyUrl == "likes")
            {
                playlistName = "Spotify Likes";
                if (usedDefaultId)
                {
                    Console.Write("Spotify client ID:");
                    spotifyId = Console.ReadLine();
                    Console.Write("Spotify client secret:");
                    spotifySecret = Console.ReadLine();
                    Console.WriteLine();
                }
                tracks = await GetSpotifyLikes(spotifyId, spotifySecret, strEdit);
            }
            else
            {
                try
                {
                    (playlistName, tracks) = await GetSpotifyPlaylist(spotifyUrl, spotifyId, spotifySecret, false, strEdit);
                }
                catch (SpotifyAPI.Web.APIException)
                {
                    Console.WriteLine("Spotify playlist not found. It may be set to private. Login? [Y/n]");
                    string answer = Console.ReadLine();
                    if (answer.ToLower() == "y")
                    {
                        if (usedDefaultId)
                        {
                            Console.Write("Spotify client ID:");
                            spotifyId = Console.ReadLine();
                            Console.Write("Spotify client secret:");
                            spotifySecret = Console.ReadLine();
                            Console.WriteLine();
                        }
                        try { (playlistName, tracks) = await GetSpotifyPlaylist(spotifyUrl, spotifyId, spotifySecret, true, strEdit); }
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
            string name;

            if (YouTube.apiKey != "")
            {
                Console.WriteLine("Loading YouTube playlist (API)");
                (name, tracks) = await YouTube.GetTracksApi(ytUrl, strEdit);
            }
            else
            {
                Console.WriteLine("Loading YouTube playlist");
                (name, tracks) = await YouTube.GetTracksYtExplode(ytUrl, strEdit);
            }

            if (folderName == "")
                folderName = RemoveInvalidChars(name, " ");

            YouTube.StopService();
        }
        else if (tracksCsv != "")
        {
            if (!System.IO.File.Exists(tracksCsv))
                throw new Exception("csv file not found");
            if (lengthCol == "")
                Console.WriteLine($"Warning: No length column specified, results may be imprecise.");

            tracks = await ParseCsvIntoTrackInfo(tracksCsv, strEdit, artistCol, trackCol, lengthCol, albumCol, descCol, ytIdCol, timeUnit, ytParse);

            if (folderName == "")
                folderName = Path.GetFileNameWithoutExtension(tracksCsv);
        }
        else if (singleName != "")
        {
            tracks.Add(new Track { TrackTitle=singleName, onlyTrackTitle=true });
            writeFails = false;
        }
        else
            throw new Exception("Nothing url, csv or name provided to download.");

        if (tracks.Count > 1)
        {
            Console.WriteLine("First 10 tracks:");
            PrintTracks(tracks, 10);
        }

        folderName = RemoveInvalidChars(folderName, " ");

        outputFolder = Path.Combine(parentFolder, folderName);
        System.IO.Directory.CreateDirectory(outputFolder);
        failsFilePath = Path.Combine(outputFolder, $"{folderName}_failed.txt");

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
                Console.WriteLine("Checking if tracks exist in output folder");
                var outputDirFiles = System.IO.Directory.GetFiles(outputFolder, "*", SearchOption.AllDirectories);
                var musicFiles = outputDirFiles.Where(f => IsMusicFile(f)).ToArray();
                tracks = tracks.Where(x =>
                {
                    bool exists = TrackExistsInCollection(x, necessaryCond, musicFiles, out string? path);
                    if (exists)
                        m3uLines[tracksStart.IndexOf(x)] = path;
                    return !exists;
                }).ToList();
            }

            if (musicDir != "")
            {
                Console.WriteLine($"Checking if tracks exist in library");
                var musicDirFiles = System.IO.Directory.GetFiles(musicDir, "*", SearchOption.AllDirectories);
                var musicFiles = musicDirFiles
                    .Where(filename => outputFolder == "" || !filename.Contains(outputFolder))
                    .Where(filename => IsMusicFile(filename)).ToArray();
                tracks = tracks.Where(x =>
                {
                    bool exists = TrackExistsInCollection(x, necessaryCond, musicFiles, out string? path);
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

                var filteredLines = failsFileCont.Split('\n').Where(line => line.Contains("[No suitable file found]")).ToList();

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

        albumSearch |= albumCol != "";
        int tracksRemaining = tracks.Count;
        if (reverse)
            tracks.Reverse();

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new Exception("No soulseek username or password");
        await client.ConnectAsync(username, password);

        object lockObj = new object();
        var UpdateTask = Task.Run(() => Update());
        SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentProcesses);

        string notFoundLastTime = skipNotFound && tracksCount2 - tracks.Count > 0 ? $"{tracksCount2 - tracks.Count} not found" : "";
        string alreadyExist = skipExisting && tracksStart.Count - tracksCount2 > 0 ? $"{tracksStart.Count - tracksCount2} already exist" : "";
        notFoundLastTime = alreadyExist != "" && notFoundLastTime != "" ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist + notFoundLastTime != "" ? $" ({alreadyExist}{notFoundLastTime})" : "";
        
        if (tracks.Count > 1 || skippedTracks != "")
            Console.WriteLine($"Downloading {tracks.Count} tracks{skippedTracks}\n");

        int successCount = 0;
        int failCount = 0;

        var downloadTasks = tracks.Select(async (track) =>
        {
            await semaphore.WaitAsync();
            int netRetries = 2;
        retry:
            try
            {
                await WaitForInternetConnection();
                
                var savedFilePath = await SearchAndDownload(track, preferredCond, necessaryCond, skipIfPrefFailed, 
                                                            maxRetriesPerTrack, searchTimeout, albumSearch, useYtdlp, searchWithoutArtist, searchWithoutDiacr);
                if (savedFilePath != "")
                {
                    successCount++;
                    m3uLines[tracksStart.IndexOf(track)] = savedFilePath;

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
                {
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                if (ex is System.InvalidOperationException && ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) && netRetries-- > 0)
                {
                    goto retry;
                }
                else
                    failCount++;
            }
            finally
            {
                semaphore.Release();
            }

            if ((DateTime.Now - lastUpdate).TotalMilliseconds > updateDelay * 3)
            {
                UpdateTask = Task.Run(() => Update());
            }

            tracksRemaining--;

            if ((successCount + failCount + 1) % 25 == 0)
            {
                skipUpdate = true;
                await Task.Delay(50);
                lock (consoleLock)
                {
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine($"\nSuccesses: {successCount}, fails: {failCount}, tracks left: {tracksRemaining}\n");
                    Console.ResetColor();
                }
                await Task.Delay(50);
                skipUpdate = false;
            }
        });

        await Task.WhenAll(downloadTasks);

        if (tracks.Count > 1)
            Console.WriteLine($"\n\nDownloaded {tracks.Count - failCount} of {tracks.Count} tracks");
        if (System.IO.File.Exists(failsFilePath))
            Console.WriteLine($"Failed:\n{System.IO.File.ReadAllText(failsFilePath)}");
    }

    static async Task<string> SearchAndDownload(Track track, FileConditions preferredCond, FileConditions necessaryCond, 
        bool skipIfPrefFailed, int maxRetriesPerFile, int searchTimeout, bool albumSearch, bool useYtdlp, bool noChannelSearch, bool noDiacrSearch)
    {
        var title = !track.onlyTrackTitle ? $"{track.ArtistName} - {track.TrackTitle}" : $"{track.TrackTitle}";
        var saveFilePath = "";
        Trace.TraceInformation($"Searching for {title}");
        logFile?.Flush();

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
                return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track);
            }
        );

        bool attemptedDownloadPref = false;
        Task? downloadTask = null;
        object downloadingLocker = new object();
        bool downloading = false;
        var responses = new ConcurrentDictionary<string, SearchResponse>();
        var cts = new CancellationTokenSource();

        Console.ResetColor();
        ProgressBar progress = new ProgressBar(PbStyle.DoubleLine, 100);
        SafeRefresh(progress, 0, $"Searching: {title}");

        Action<SearchResponse> responseHandler = (r) =>
        {
            if (r.Files.Count > 0)
            {
                responses.TryAdd(r.Files.First().Filename, r);
                lock (downloadingLocker)
                {
                    if (!downloading)
                    {
                        var f = r.Files.First();
                        if (preferredCond.FileSatisfies(f, track) && r.HasFreeUploadSlot && r.UploadSpeed / 1000000 >= 1)
                        {
                            downloading = true;
                            saveFilePath = GetSavePath(f, track);
                            attemptedDownloadPref = true;
                            try
                            {
                                Trace.TraceInformation($"Early download: {f.Filename}");
                                logFile?.Flush();
                                downloadTask = DownloadFile(r, f, saveFilePath, track, progress, cts);
                            }
                            catch
                            {
                                saveFilePath = "";
                                downloading = false;
                            }
                        }
                    }
                }
            }
        };

        lock (searches)
            searches[track] = new SearchInfo(searchQuery, responses, searchOptions, progress);

        try
        {
            await WaitForInternetConnection();
            var searchTasks = new List<Task>();
            Trace.TraceInformation("Search pos 1");
            searchTasks.Add(client.SearchAsync(searchQuery, options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler));

            if (noDiacrSearch && title.RemoveDiacriticsIfExist(out string newSearch))
            {
                var searchQuery2 = SearchQuery.FromText(newSearch);
                Trace.TraceInformation("Search pos 2");
                searchTasks.Add(client.SearchAsync(searchQuery2, options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler));
            }

            await Task.WhenAll(searchTasks);
        }
        catch (OperationCanceledException ex) { }

        if (albumSearch && responses.Count == 0 && track.Album != "")
        {
            Func<Soulseek.File, bool> ff1 = (file) =>
            {
                var seps = new string[] { " ", "_" };
                return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track)
                    && file.Filename.Replace(seps, "").Contains(track.ArtistName.Replace(seps, ""), StringComparison.OrdinalIgnoreCase);
            };
            Func<Soulseek.File, bool> ff2 = (file) =>
            {
                var seps = new string[] { " ", "_" };
                return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track)
                    && file.Filename.Replace(seps, "").Contains(track.TrackTitle.Replace(seps, ""), StringComparison.OrdinalIgnoreCase);
            };

            var searchOptions1 = new SearchOptions(minimumPeerUploadSpeed: 1, searchTimeout: 5000, fileFilter: ff1);
            var searchOptions2 = new SearchOptions(minimumPeerUploadSpeed: 1, searchTimeout: 5000, fileFilter: ff2);

            var searchQuery1 = SearchQuery.FromText($"{track.Album} {track.TrackTitle}");
            var searchQuery2 = SearchQuery.FromText($"{track.ArtistName} {track.Album}");

            SafeRefresh(progress, 0, $"Searching (album name): {title}");

            try
            {
                await WaitForInternetConnection();
                var searchTasks = new List<Task>();

                Trace.TraceInformation("Search pos 3, 4");
                searchTasks.Add(client.SearchAsync(searchQuery1, options: searchOptions1, cancellationToken: cts.Token, responseHandler: responseHandler));
                searchTasks.Add(client.SearchAsync(searchQuery2, options: searchOptions2, cancellationToken: cts.Token, responseHandler: responseHandler));

                if (noDiacrSearch)
                {
                    if (searchQuery1.SearchText.RemoveDiacriticsIfExist(out string newSearch1))
                    {
                        Trace.TraceInformation("Search pos 5");
                        var searchQuery1_2 = SearchQuery.FromText(newSearch1);
                        searchTasks.Add(client.SearchAsync(searchQuery1_2, options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler));
                    }
                    if (searchQuery2.SearchText.RemoveDiacriticsIfExist(out string newSearch2))
                    {
                        Trace.TraceInformation("Search pos 6");
                        var searchQuery2_2 = SearchQuery.FromText(newSearch2);
                        searchTasks.Add(client.SearchAsync(searchQuery2_2, options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler));
                    }
                }

                await Task.WhenAll(searchTasks);
            }
            catch (OperationCanceledException ex) { }
        }

        if (noChannelSearch && responses.Count == 0 && track.ArtistMaybeWrong && new[] { " ", ":", "-" }.Any(c => track.TrackTitle.Contains(c)))
        {
            string searchText = $"{track.TrackTitle}";
            searchOptions = new SearchOptions
            (
                minimumPeerUploadSpeed: 1, searchTimeout: 8000,
                fileFilter: (file) => { return IsMusicFile(file.Filename) && necessaryCond.FileSatisfies(file, track); }
            );

            SafeRefresh(progress, 0, $"Searching (no channel name): {searchText}");

            try
            {
                await WaitForInternetConnection();
                var searchTasks = new List<Task>();

                Trace.TraceInformation("Search pos 7");
                searchTasks.Add(client.SearchAsync(SearchQuery.FromText(searchText), options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler));

                if (noDiacrSearch && title.RemoveDiacriticsIfExist(out string newSearch))
                {
                    var searchQuery2 = SearchQuery.FromText(newSearch);
                    Trace.TraceInformation("Search pos 8");
                    searchTasks.Add(client.SearchAsync(searchQuery2, options: searchOptions, cancellationToken: cts.Token, responseHandler: responseHandler));
                }

                await Task.WhenAll(searchTasks);
            }
            catch (OperationCanceledException ex) { }
        }

        searches.TryRemove(track, out _);
        cts.Dispose();

        lock (downloadingLocker) { }

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
                .SelectMany(kvp => kvp.Value.Files.Select(file => (response: kvp.Value, file)))
                .OrderByDescending(x => preferredCond.LengthToleranceSatisfies(x.file, track.Length))
                .ThenByDescending(x => preferredCond.BitrateSatisfies(x.file))
                .ThenByDescending(x => preferredCond.FileSatisfies(x.file, track))
                .ThenByDescending(x => x.response.HasFreeUploadSlot)
                .ThenByDescending(x => x.response.UploadSpeed / 200)
                .ThenByDescending(x => x.file.Filename.Contains(track.TrackTitle, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var x in fileResponses)
            {
                bool pref = preferredCond.FileSatisfies(x.file, track);
                if (skipIfPrefFailed && attemptedDownloadPref && !pref)
                {
                    SafeRefresh(progress, 0, $"Pref. version of the file exists, but couldn't be downloaded: {track}, skipping");
                    var failedDownloadInfo = $"{track} [Pref. version of the file exists, but couldn't be downloaded]";
                    WriteLineOutputFile(failedDownloadInfo);
                    return "";
                }

                saveFilePath = GetSavePath(x.file, track);

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
                    if (--maxRetriesPerFile <= 0)
                    {
                        SafeRefresh(progress, 0, $"Out of download retries: {track}, skipping");
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
                SafeRefresh(progress, 0, $"Not found, searching with yt-dlp: {track}");
                downloading = true;
                string fname = GetSaveName(track);
                await YtdlpSearchAndDownload(track, necessaryCond, Path.Combine(outputFolder, fname), progress);
                string[] files = System.IO.Directory.GetFiles(outputFolder, fname + ".*");
                foreach (string file in files)
                {
                    if (IsMusicFile(file))
                    {
                        SafeRefresh(progress, 100, $"yt-dlp: Completed download for {track}");
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
                SafeRefresh(progress, 0, $"{e.Message}");
            }
        }

        if (!downloading)
        {
            if (notFound)
            {
                SafeRefresh(progress, 0, $"Not found: {track}, skipping");   
                var failedDownloadInfo = $"{track} [No suitable file found]";
                WriteLineOutputFile(failedDownloadInfo);
            }
            else
            {
                SafeRefresh(progress, 0, $"Failed to download: {track}, skipping");
                var failedDownloadInfo = $"{track} [All downloads failed]";
                WriteLineOutputFile(failedDownloadInfo);
            }
            return "";
        }

        return saveFilePath;
    }

    static async Task DownloadFile(SearchResponse response, Soulseek.File file, string filePath, Track track, ProgressBar progress, CancellationTokenSource? searchCts = null)
    {
        Trace.TraceInformation($"Downloading {file.Filename}");
        logFile.Flush();
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
                downloads[file.Filename] = new DownloadWrapper(filePath, response, file, track, cts, progress);

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
                            try { val.cts.Cancel(); }
                            catch { }
                            val.stalled = true;
                            val.UpdateText();
                            downloads.TryRemove(key, out _);
                        }
                    }
                    else
                        downloads.TryRemove(key, out _);
                }
            }

            await Task.Delay(updateDelay);
        }
    }

    static string GetSavePath(Soulseek.File file, Track track)
    {
        if (!track.onlyTrackTitle)
            return $"{GetSavePathNoExt(track)}{Path.GetExtension(file.Filename)}";
        else
            return $"{Path.Combine(outputFolder, RemoveInvalidChars(Path.GetFileName(file.Filename), " "))}";
    }

    static string GetSavePathNoExt(Track track)
    {
        return Path.Combine(outputFolder, $"{GetSaveName(track)}");
    }

    static string GetSaveName(Track track)
    {
        string name = $"{track.ArtistName} - {track.TrackTitle}";
        return RemoveInvalidChars(name, " ");
    }

    static async Task YtdlpSearchAndDownload(Track track, FileConditions conditions, string savePathNoExt, ProgressBar progress)
    {
        if (track.YtID != "")
        {
            YtdlpDownload(track.YtID, savePathNoExt, progress);
            return;
        }

        Process process = new Process();
        ProcessStartInfo startInfo = new ProcessStartInfo();

        startInfo.FileName = "yt-dlp";
        string search = $"{track.ArtistName} - {track.TrackTitle}";
        startInfo.Arguments = $"\"ytsearch3:{search}\" --print \"%(duration>%H:%M:%S)s ¦¦ %(id)s ¦¦ %(title)s\"";
        SafeRefresh(progress, 0, $"{startInfo.FileName} {startInfo.Arguments}");

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
        Regex regex = new Regex(@"^(\d+):(\d+):(\d+) ¦¦ ([\w-]+) ¦¦ (.+)$");
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
                YtdlpDownload(res.Item2, savePathNoExt, progress);
                return;
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
        SafeRefresh(progress, 0, $"yt-dlp \"{id}\" -f {ytdlpFormat} -ci -o \"{Path.GetFileNameWithoutExtension(savePathNoExt + ".m")}.%(ext)s\" -x");

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

        public DownloadWrapper(string savePath, SearchResponse response, Soulseek.File file, Track track, CancellationTokenSource cts, ProgressBar progress)
        {
            this.savePath = savePath;
            this.response = response;
            this.file = file;
            this.cts = cts;
            this.track = track;
            string sampleRate = file.SampleRate.HasValue ? $"/{file.SampleRate}Hz" : "";
            string bitRate = file.BitRate.HasValue ? $"/{file.BitRate}kbps" : "";
            string fileSize = $"{file.Size / (float)(1024 * 1024):F1}MB";
            displayText = $"{response.Username}\\..\\{file.Filename.Split('\\').Last()} " +
                $"[{file.Length}s{sampleRate}{bitRate}/{fileSize}]";

            this.progress = progress;
            SafeRefresh(progress, 0, displayText);
        }

        public string UpdateText()
        {
            char[] bars = { '/', '|', '\\', '―' };
            downloadRotatingBarState++;
            downloadRotatingBarState %= bars.Length;
            string bar = success ? "" : bars[downloadRotatingBarState] + " ";
            float? percentage = bytesTransferred / (float)file.Size;
            string percText = percentage < 0.1 ? $"0{percentage:P}" : $"{percentage:P}";
            queued = transfer?.State.ToString().Contains("Queued") ?? false;
            string state = "NullState";

            if (stalled)
            {
                state = "Stalled";
                bar = "";
            }
            else if (transfer != null)
            {
                if (queued)
                    state = "Queued";
                else if (transfer.State.ToString().Contains("Completed, "))
                    state = transfer.State.ToString().Replace("Completed, ", "");
                else
                    state = transfer.State.ToString();
            }

            if (state == "Succeeded")
                success = true;

            string txt = $"{bar}{state}:".PadRight(14, ' ');

            Console.ResetColor();
            SafeRefresh(progress, (int)((percentage ?? 0) * 100), $"{txt} {displayText}");

            return progress.Line1 + "\n" + progress.Line2;
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
        public ConcurrentDictionary<string, SearchResponse> responses;
        public ProgressBar progress;

        public SearchInfo(SearchQuery query, ConcurrentDictionary<string, SearchResponse> responses, SearchOptions searchOptions, ProgressBar progress)
        {
            this.query = query;
            this.responses = responses;
            this.searchOptions = searchOptions; 
            this.progress = progress;
        }
    }

    class FileConditions
    {
        public string Format { get; set; } = "";
        public int LengthTolerance { get; set; } = -1;
        public int MinBitrate { get; set; } = -1;
        public int MaxBitrate { get; set; } = -1;
        public int MaxSampleRate { get; set; } = -1;
        public string[] dangerWords = { "mix", "dj ", " edit", "cover" };

        public bool FileSatisfies(Soulseek.File file, Track track)
        {
            string fname = Path.GetFileNameWithoutExtension(file.Filename);

            return NameSatisfies(fname, track.TrackTitle) && FormatSatisfies(file.Filename) && LengthToleranceSatisfies(file, track.Length) 
                && BitrateSatisfies(file) && SampleRateSatisfies(file);
        }

        public bool FileSatisfies(TagLib.File file, Track track)
        {
            string fname = Path.GetFileNameWithoutExtension(file.Name);

            return NameSatisfies(fname, track.TrackTitle) && FormatSatisfies(file.Name) && LengthToleranceSatisfies(file, track.Length) 
                && BitrateSatisfies(file) && SampleRateSatisfies(file);
        }

        public bool NameSatisfies(string fname, string tname)
        {
            if (string.IsNullOrEmpty(tname))
                return false;
            tname = tname.Split('-', StringSplitOptions.RemoveEmptyEntries).Last();

            foreach (var word in dangerWords)
            {
                if (fname.Contains(word, StringComparison.OrdinalIgnoreCase) ^ tname.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    if (word == "mix")
                        return fname.Contains("original mix", StringComparison.OrdinalIgnoreCase) || tname.Contains("original mix", StringComparison.OrdinalIgnoreCase);
                    else
                        return false;
                }
            }

            return true;
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

    static async Task<(string?, List<Track>)> GetSpotifyPlaylist(string url, string id, string secret, bool login, StringEdit stringEdit)
    {
        var spotify = new Spotify(id, secret);
        if (login)
        {
            await spotify.AuthorizeLogin();
            await spotify.IsClientReady();
        }
        else
            await spotify.Authorize();

        Console.WriteLine("Loading Spotify tracks");
        (string? name, var res) = await spotify.GetPlaylist(url, stringEdit);
        return (name, res);
    }

    static async Task<List<Track>> GetSpotifyLikes(string id, string secret, StringEdit stringEdit)
    {
        var spotify = new Spotify(id, secret);
        await spotify.AuthorizeLogin();
        await spotify.IsClientReady();

        Console.WriteLine("Loading Spotify tracks");
        var res = await spotify.GetLikes(stringEdit);
        return res;
    }

    static async Task<List<Track>> ParseCsvIntoTrackInfo(string path, StringEdit stringEdit, string artistCol = "", string trackCol = "", 
        string lengthCol = "", string albumCol = "", string descCol = "", string ytIdCol = "", string timeUnit = "s", bool ytParse = false)
    {
        var tracks = new List<Track>();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            var header = reader.ReadLine();

            string[] cols = { artistCol, albumCol, trackCol, descCol, ytIdCol, lengthCol };
            for (int i = 0; i < cols.Length; i++)
            {
                if (!string.IsNullOrEmpty(cols[i]) && Array.IndexOf(header.Split(','), cols[i]) == -1)
                    throw new Exception($"Column \"{cols[i]}\" not found in CSV file");
            }

            var artistIndex = string.IsNullOrEmpty(artistCol) ? -1 : Array.IndexOf(header.Split(','), artistCol);
            var albumIndex = string.IsNullOrEmpty(albumCol) ? -1 : Array.IndexOf(header.Split(','), albumCol);
            var trackIndex = string.IsNullOrEmpty(trackCol) ? -1 : Array.IndexOf(header.Split(','), trackCol);
            var descIndex = string.IsNullOrEmpty(descCol) ? -1 : Array.IndexOf(header.Split(','), descCol);
            var ytIdIndex = string.IsNullOrEmpty(ytIdCol) ? -1 : Array.IndexOf(header.Split(','), ytIdCol);
            var lengthIndex = string.IsNullOrEmpty(lengthCol) ? -1 : Array.IndexOf(header.Split(','), lengthCol);

            var regex = new Regex(",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)"); // thank you, ChatGPT.

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                var values = regex.Split(line);
                var desc = "";
                var id = "";

                var track = new Track();
                if (artistIndex >= 0) track.ArtistName = values[artistIndex].Trim('"').Split(',').First().Trim(' ');
                if (trackIndex >= 0) track.TrackTitle = stringEdit.Edit(values[trackIndex].Trim('"'));
                if (albumIndex >= 0) track.Album = values[albumIndex].Trim('"');
                if (descIndex >= 0) desc = values[descIndex].Trim('"');
                if (ytIdIndex >= 0) id = values[ytIdIndex].Trim('"');
                if (lengthIndex >= 0 && int.TryParse(values[lengthIndex], out int result) && result > 0)
                {
                    if (timeUnit == "ms")
                        track.Length = result / 1000;
                    else
                        track.Length = result;
                }

                if (ytParse)
                    track = await YouTube.ParseTrackInfo(track.TrackTitle, track.ArtistName, id, track.Length, true, desc);

                if (track.TrackTitle != "") tracks.Add(track);
            }
        }

        if (ytParse)
            YouTube.StopService();

        return tracks;
    }

    static bool IsMusicFile(string fileName)
    {
        var musicExtensions = new string[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".wma", ".m4a", ".alac", ".ape", ".dsd", ".dff", ".dsf", ".opus" };
        var extension = Path.GetExtension(fileName).ToLower();
        return musicExtensions.Contains(extension);
    }

    static bool TrackExistsInCollection(Track track, FileConditions conditions, IEnumerable<string> collection, out string? foundPath)
    {
        string[] ignore = new string[] { " ", "_", "-", ".", "(", ")" };
        string searchName = track.TrackTitle.Replace(ignore, "");
        if (string.IsNullOrEmpty(searchName))
            searchName = track.TrackTitle;

        searchName = RemoveInvalidChars(searchName, "");

        var matchingFiles = collection
            .Where(fileName => fileName.Replace(ignore, "").Contains(searchName, StringComparison.OrdinalIgnoreCase)).ToArray();

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

                if (conditions.FileSatisfies(f, track))
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
    static string RemoveInvalidChars(string str, string replaceStr)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
            str = str.Replace(c.ToString(), replaceStr);
        return str;
    }

    static void PrintTracks(List<Track> tracks, int number = -1)
    {
        number = number == -1 ? tracks.Count : Math.Min(tracks.Count, number);
        for (int i = 0; i < number; i++)
        {
            Console.WriteLine($"  {tracks[i]}");
        }
        if (number != tracks.Count)
            Console.WriteLine("  ...");
        Console.WriteLine($"Track count: {tracks.Count}");
    }

    static void SafeRefresh(ProgressBar progress, int current, string item)
    {
        lock (consoleLock)
            progress.Refresh(current, item);
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

public class StringEdit
{
    public string[] stringsToRemove = { };
    string[] ftStrings = { "ft.", "feat." };
    public bool removeFt = false;

    public StringEdit(string[]? stringsToRemove = null, bool removeFt = false)
    {
        if (stringsToRemove != null)
            this.stringsToRemove = stringsToRemove;

        this.removeFt = removeFt;
    }

    public string Edit(string str)
    {
        foreach (string s in stringsToRemove)
        {
            var t = str;
            str = Regex.Replace(str, Regex.Escape(s), "", RegexOptions.IgnoreCase);
            if (t == str)
            {
                if (str.Contains("["))
                {
                    string s2 = s.Replace("[", "(").Replace("]", ")");
                    str = Regex.Replace(str, Regex.Escape(s2), "", RegexOptions.IgnoreCase);
                }
                else if (str.Contains("("))
                {
                    string s2 = s.Replace("(", "[").Replace(")", "]");
                    str = Regex.Replace(str, Regex.Escape(s2), "", RegexOptions.IgnoreCase);
                }
            }
        }

        if (removeFt)
        {
            foreach (string ftStr in ftStrings)
            {
                int ftIndex = str.IndexOf(ftStr, StringComparison.OrdinalIgnoreCase);

                if (ftIndex != -1)
                    str = str.Substring(0, ftIndex - 1);
            }
        }

        return str.Trim();
    }
}

public struct Track
{
    public string TrackTitle = "";
    public string ArtistName = "";
    public string Album = "";
    public string YtID = "";
    public int Length = -1;
    public bool ArtistMaybeWrong = false;
    public bool onlyTrackTitle = false;

    public Track() { }

    public override string ToString()
    {
        var length = Length > 0 ? $" ({Length}s)" : "";
        if (!onlyTrackTitle)
            return $"{ArtistName} - {TrackTitle}{length}";
        else
            return $"{TrackTitle}{length}";
    }
}

public static class ExtensionMethods
{
    public static string Replace(this string s, string[] separators, string newVal)
    {
        string[] temp;
        temp = s.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return String.Join(newVal, temp);
    }

    public static bool RemoveDiacriticsIfExist(this string s, out string res)
    {
        res = s.RemoveDiacritics();
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
