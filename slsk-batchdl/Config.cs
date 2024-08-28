
using Enums;
using Data;
using System.Text;
using System.Text.RegularExpressions;


static class Config
{
    public static FileConditions necessaryCond = new();

    public static FileConditions preferredCond = new()
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

    public static string parentFolder = Directory.GetCurrentDirectory();
    public static string input = "";
    public static string outputFolder = "";
    public static string m3uFilePath = "";
    public static string musicDir = "";
    public static string folderName = "";
    public static string defaultFolderName = "";
    public static string spotifyId = "";
    public static string spotifySecret = "";
    public static string ytKey = "";
    public static string username = "";
    public static string password = "";
    public static string artistCol = "";
    public static string albumCol = "";
    public static string trackCol = "";
    public static string ytIdCol = "";
    public static string descCol = "";
    public static string trackCountCol = "";
    public static string lengthCol = "";
    public static string timeUnit = "s";
    public static string nameFormat = "";
    public static string invalidReplaceStr = " ";
    public static string ytdlpArgument = "";
    public static string onComplete = "";
    public static string confPath = "";
    public static string profile = "";
    public static bool aggregate = false;
    public static bool album = false;
    public static bool albumArtOnly = false;
    public static bool interactiveMode = false;
    public static bool albumIgnoreFails = false;
    public static bool setAlbumMinTrackCount = true;
    public static bool setAlbumMaxTrackCount = false;
    public static bool skipNotFound = false;
    public static bool desperateSearch = false;
    public static bool noRemoveSpecialChars = false;
    public static bool artistMaybeWrong = false;
    public static bool fastSearch = false;
    public static bool ytParse = false;
    public static bool removeFt = false;
    public static bool removeBrackets = false;
    public static bool reverse = false;
    public static bool useYtdlp = false;
    public static bool skipExisting = false;
    public static bool removeTracksFromSource = false;
    public static bool getDeleted = false;
    public static bool deletedOnly = false;
    public static bool removeSingleCharacterSearchTerms = false;
    public static bool relax = false;
    public static bool debugInfo = false;
    public static bool noModifyShareCount = false;
    public static bool useRandomLogin = false;
    public static bool noBrowseFolder = false;
    public static bool skipExistingPrefCond = false;
    public static int downrankOn = -1;
    public static int ignoreOn = -2;
    public static int minAlbumTrackCount = -1;
    public static int maxAlbumTrackCount = -1;
    public static int fastSearchDelay = 300;
    public static int minSharesAggregate = 2;
    public static int maxTracks = int.MaxValue;
    public static int offset = 0;
    public static int maxStaleTime = 50000;
    public static int updateDelay = 100;
    public static int searchTimeout = 6000;
    public static int concurrentProcesses = 2;
    public static int unknownErrorRetries = 2;
    public static int maxRetriesPerTrack = 30;
    public static int listenPort = 49998;
    public static int searchesPerTime = 34;
    public static int searchRenewTime = 220;
    public static double fastSearchMinUpSpeed = 1.0;
    public static Track regexToReplace = new();
    public static Track regexReplaceBy = new();
    public static AlbumArtOption albumArtOption = AlbumArtOption.Default;
    public static M3uOption m3uOption = M3uOption.Index;
    public static DisplayMode displayMode = DisplayMode.Single;
    public static InputType inputType = InputType.None;
    public static SkipMode skipMode = SkipMode.M3u;
    public static SkipMode skipModeMusicDir = SkipMode.Name;
    public static PrintOption printOption = PrintOption.None;

    static readonly Dictionary<string, (List<string> args, string? cond)> profiles = new();
    static readonly HashSet<string> appliedProfiles = new();
    static bool hasConfiguredM3uMode = false;
    static bool confPathChanged = false;
    static string[] arguments;

    public static bool HasAutoProfiles { get; private set; } = false;
    public static bool DoNotDownload => (printOption & (PrintOption.Results | PrintOption.Tracks)) != 0;
    public static bool PrintTracks => (printOption & PrintOption.Tracks) != 0;
    public static bool PrintResults => (printOption & PrintOption.Results) != 0;
    public static bool PrintTracksFull => (printOption & PrintOption.Tracks) != 0 && (printOption & PrintOption.Full) != 0;
    public static bool PrintResultsFull => (printOption & PrintOption.Results) != 0 && (printOption & PrintOption.Full) != 0;


    public static bool ParseArgsAndReadConfig(string[] args)
    {
        args = args.SelectMany(arg =>
        {
            if (arg.Length > 3 && arg.StartsWith("--") && arg.Contains('='))
            {
                var parts = arg.Split('=', 2);
                return new[] { parts[0], parts[1] };
            }
            return new[] { arg };
        }).ToArray();

        SetConfigPath(args);

        if (confPath != "none" && (confPathChanged || File.Exists(confPath)))
        {
            if (File.Exists(Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath)))
                confPath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath);
            ParseConfig(confPath);
        }

        args = args.SelectMany(arg =>
        {
            if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && !arg.Contains(' '))
                return arg[1..].Select(c => $"-{c}");
            return new[] { arg };
        }).ToArray();

        arguments = args;

        int profileIndex = Array.FindLastIndex(args, x => x == "--profile");

        if (profileIndex != -1 && profileIndex < args.Length - 1)
        {
            profile = args[profileIndex + 1];
            if (profile == "help")
            {
                ListProfiles();
                return false;
            }
        }

        if (profiles.ContainsKey("default"))
        {
            ProcessArgs(profiles["default"].args);
            appliedProfiles.Add("default");
        }

        if (HasAutoProfiles)
        {
            ProcessArgs(args);
            ApplyAutoProfiles();
        }

        ApplyProfile(profile);

        ProcessArgs(args);

        return true;
    }


    static void SetConfigPath(string[] args)
    {
        int idx = Array.LastIndexOf(args, "-c");
        int idx2 = Array.LastIndexOf(args, "--config");
        idx = idx > idx2 ? idx : idx2;
        if (idx != -1)
        {
            confPath = Utils.ExpandUser(args[idx + 1]);
        }

        if (confPath.Length > 0)
        {
            confPathChanged = true;
        }

        if (!confPathChanged)
        {
            var configPaths = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sldl", "sldl.conf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sldl", "sldl.conf"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sldl.conf"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "slsk-batchdl.conf"),
            };

            foreach (var path in configPaths)
            {
                if (File.Exists(path))
                {
                    confPath = path;
                    break;
                }
            }
        }
    }


    public static void PostProcessArgs() // must be run after Program.trackLists has been assigned
    {
        if (DoNotDownload || debugInfo)
            concurrentProcesses = 1;

        ignoreOn = Math.Min(ignoreOn, downrankOn);

        if (DoNotDownload)
            m3uOption = M3uOption.None;
        else if (!hasConfiguredM3uMode)
        {
            if (inputType == InputType.String)
                m3uOption = M3uOption.None;
            else if (!aggregate && !(skipExisting && (skipMode == SkipMode.M3u || skipMode == SkipMode.M3uCond))
                && Program.trackLists != null && !Program.trackLists.Flattened(true, false, true).Skip(1).Any())
            {
                m3uOption = M3uOption.None;
            }
        }

        parentFolder = Utils.ExpandUser(parentFolder);
        m3uFilePath = Utils.ExpandUser(m3uFilePath);
        musicDir = Utils.ExpandUser(musicDir);

        if (folderName.Length == 0)
            folderName = defaultFolderName;
        if (folderName == ".")
            folderName = "";

        folderName = folderName.Replace('\\', '/');
        folderName = string.Join('/', folderName.Split('/').Select(x => x.ReplaceInvalidChars(invalidReplaceStr).Trim()));
        folderName = folderName.Replace('/', Path.DirectorySeparatorChar);

        outputFolder = Path.Join(parentFolder, folderName);

        if (m3uFilePath.Length == 0)
            m3uFilePath = Path.Join(outputFolder, (folderName.Length == 0 ? "playlist" : folderName) + ".m3u8");
    }


    static void ParseConfig(string path)
    {
        var lines = File.ReadAllLines(path);
        var curProfile = "default";

        for (int i = 0; i < lines.Length; i++)
        { 
            string l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith('#'))
                continue;

            if (l.StartsWith('[') && l.EndsWith(']'))
            {
                curProfile = l[1..^1];
                continue;
            }

            int idx = l.IndexOf('=');
            if (idx <= 0 || idx == l.Length - 1)
                throw new ArgumentException($"Error parsing config '{path}' at line {i}");

            var x = l.Split('=', 2, StringSplitOptions.TrimEntries);
            string key = x[0];
            string val = x[1];

            if (val[0] == '"' && val[^1] == '"')
                val = val[1..^1];

            if (!profiles.ContainsKey(curProfile))
                profiles[curProfile] = (new List<string>(), null);

            if (key == "profile-cond" && curProfile != "default")
            {
                var a = profiles[curProfile].args;
                profiles[curProfile] = (a, val);
                HasAutoProfiles = true;
            }
            else
            {
                if (key.Length == 1)
                    key = '-' + key;
                else
                    key = "--" + key;

                profiles[curProfile].args.Add(key);
                profiles[curProfile].args.Add(val);
            }
        }
    }


    public static void UpdateArgs(TrackListEntry tle)
    {
        if (DoNotDownload)
            return;
        if (!HasAutoProfiles)
            return;

        var newProfiles = ApplyAutoProfiles(tle);

        if (newProfiles.Count > 0)
        {
            //appliedProfiles.Clear();
            appliedProfiles.Union(newProfiles);
            ApplyProfile(profile);
            ProcessArgs(arguments);
            PostProcessArgs();
        }
    }


    static void ApplyProfile(string name)
    {
        if (name.Length > 0 && name != "default")
        {
            if (profiles.ContainsKey(name))
            {
                ProcessArgs(profiles[name].args);
                appliedProfiles.Add(name);
            }
            else
                Console.WriteLine($"Error: No profile '{name}' found in config");
        }
    }


    static HashSet<string> ApplyAutoProfiles(TrackListEntry? tle = null)
    {
        var applied = new HashSet<string>();

        if (!HasAutoProfiles)
            return applied;

        foreach ((var key, var val) in profiles)
        {
            if (key == "default" || appliedProfiles.Contains(key))
                continue;
            if (key != profile && val.cond != null && ProfileConditionSatisfied(val.cond, tle))
            {
                Console.WriteLine($"Applying auto profile: {key}");
                ProcessArgs(val.args);
                appliedProfiles.Add(key);
                applied.Add(key);
            }
        }

        return applied;
    }


    static object GetVarValue(string var, TrackListEntry? tle = null)
    {
        static string toKebab(string input)
        {
            return string.Concat(input.Select((x, i) 
                => char.IsUpper(x) && i > 0 ? "-" + char.ToLower(x).ToString() : char.ToLower(x).ToString()));
        }

        return var switch
        {
            "input-type" => inputType.ToString().ToLower(),
            "download-mode" => tle != null ? toKebab(tle.source.Type.ToString())
                : album && aggregate ? "album-aggregate" : album ? "album" : aggregate ? "aggregate" : "normal",
            "interactive" => interactiveMode,
            "album" => album,
            "aggregate" => aggregate,
            _ => throw new ArgumentException($"Unrecognized profile condition variable {var}")
        };
    }


    public static bool ProfileConditionSatisfied(string cond, TrackListEntry? tle = null)
    {
        var tokens = new Queue<string>(Regex.Split(cond, @"(\s+|\(|\)|&&|\|\||==|!=|!|\"".*?\"")").Where(t => !string.IsNullOrWhiteSpace(t)));

        bool ParseExpression()
        {
            var left = ParseAndExpression();

            while (tokens.Count > 0)
            {
                string token = tokens.Peek();
                if (token == "||")
                {
                    tokens.Dequeue();
                    var right = ParseAndExpression();
                    left = left || right;
                }
                else
                    break;
            }

            return left;
        }

        bool ParseAndExpression()
        {
            var left = ParsePrimaryExpression();

            while (tokens.Count > 0)
            {
                string token = tokens.Peek();
                if (token == "&&")
                {
                    tokens.Dequeue();
                    var right = ParsePrimaryExpression();
                    left = left && right;
                }
                else
                    break;
            }

            return left;
        }

        bool ParsePrimaryExpression()
        {
            string token = tokens.Dequeue();

            if (token == "(")
            {
                var result = ParseExpression();
                tokens.Dequeue();
                return result;
            }

            if (token == "!")
                return !ParsePrimaryExpression();

            if (token.StartsWith("\""))
                throw new ArgumentException("Invalid token at this position: " + token);

            if (tokens.Count > 0 && (tokens.Peek() == "==" || tokens.Peek() == "!="))
            {
                string op = tokens.Dequeue();
                string value = tokens.Dequeue().Trim('"').ToLower();
                string varValue = GetVarValue(token, tle).ToString().ToLower();
                return op == "==" ? varValue == value : varValue != value;
            }

            return (bool)GetVarValue(token, tle);
        }

        return ParseExpression();
    }


    static void ListProfiles()
    {
        Console.WriteLine("Available profiles:");
        foreach ((var key, var val) in profiles)
        {
            if (key == "default")
                continue;
            Console.WriteLine($"  [{key}]");
            if (val.cond != null)
                Console.WriteLine($"    profile-cond = {val.cond}");
            for (int i = 0; i < val.args.Count; i += 2)
            {
                Console.WriteLine($"    {val.args[i].TrimStart('-')} = {val.args[i + 1]}");
            }
            Console.WriteLine();
        }
    }


    static void ParseConditions(FileConditions cond, string input)
    {
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


    static void ProcessArgs(IReadOnlyList<string> args)
    {
        void setFlag(ref bool flag, ref int i, bool trueVal = true)
        {
            if (i >= args.Count - 1 || args[i + 1].StartsWith('-'))
                flag = trueVal;
            else if (args[i + 1] == "false")
            {
                flag = !trueVal;
                i++;
            }
            else if (args[i + 1] == "true")
            {
                flag = trueVal;
                i++;
            }
            else
                flag = trueVal;
        }

        bool inputSet = false;

        for (int i = 0; i < args.Count; i++)
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
                        setFlag(ref aggregate, ref i);
                        break;
                    case "--msa":
                    case "--min-shares-aggregate":
                        minSharesAggregate = int.Parse(args[++i]);
                        break;
                    case "--rf":
                    case "--relax":
                    case "--relax-filtering":
                        setFlag(ref relax, ref i);
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
                        var login = args[++i].Split(';', 2);
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
                        setFlag(ref useRandomLogin, ref i);
                        break;
                    case "--ac":
                    case "--artist-col":
                        artistCol = args[++i];
                        break;
                    case "--tc":
                    case "--track-col":
                        trackCol = args[++i];
                        break;
                    case "--alc":
                    case "--album-col":
                        albumCol = args[++i];
                        break;
                    case "--ydc":
                    case "--yt-desc-col":
                        descCol = args[++i];
                        break;
                    case "--atcc":
                    case "--album-track-count-col":
                        trackCountCol = args[++i];
                        break;
                    case "--yic":
                    case "--yt-id-col":
                        ytIdCol = args[++i];
                        break;
                    case "--lc":
                    case "--length-col":
                        lengthCol = args[++i];
                        break;
                    case "--tf":
                    case "--time-format":
                        timeUnit = args[++i];
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
                    case "--irs":
                    case "--invalid-replace-str":
                        invalidReplaceStr = args[++i];
                        break;
                    case "--print":
                        printOption = args[++i].ToLower().Trim() switch
                        {
                            "none" => PrintOption.None,
                            "tracks" => PrintOption.Tracks,
                            "results" => PrintOption.Results,
                            "tracks-full" => PrintOption.Tracks | PrintOption.Full,
                            "results-full" => PrintOption.Results | PrintOption.Full,
                            _ => throw new ArgumentException($"Invalid print option '{args[i]}'"),
                        };
                        break;
                    case "--pt":
                    case "--print-tracks":
                        printOption = PrintOption.Tracks;
                        break;
                    case "--ptf":
                    case "--print-tracks-full":
                        printOption = PrintOption.Tracks | PrintOption.Full;
                        break;
                    case "--pr":
                    case "--print-results":
                        printOption = PrintOption.Results;
                        break;
                    case "--prf":
                    case "--print-results-full":
                        printOption = PrintOption.Results | PrintOption.Full;
                        break;
                    case "--yp":
                    case "--yt-parse":
                        setFlag(ref ytParse, ref i);
                        break;
                    case "--yd":
                    case "--yt-dlp":
                        setFlag(ref useYtdlp, ref i);
                        break;
                    case "-s":
                    case "--se":
                    case "--skip-existing":
                        setFlag(ref skipExisting, ref i);
                        break;
                    case "--snf":
                    case "--skip-not-found":
                        setFlag(ref skipNotFound, ref i);
                        break;
                    case "--rfp":
                    case "--rfs":
                    case "--remove-from-source":
                    case "--remove-from-playlist":
                        setFlag(ref removeTracksFromSource, ref i);
                        break;
                    case "--rft":
                    case "--remove-ft":
                        setFlag(ref removeFt, ref i);
                        break;
                    case "--rb":
                    case "--remove-brackets":
                        setFlag(ref removeBrackets, ref i);
                        break;
                    case "--gd":
                    case "--get-deleted":
                        setFlag(ref getDeleted, ref i);
                        break;
                    case "--do":
                    case "--deleted-only":
                        setFlag(ref getDeleted, ref i);
                        setFlag(ref deletedOnly, ref i);
                        break;
                    case "--re":
                    case "--regex":
                        string s = args[++i].Replace("\\;", "<<semicol>>");
                        string applyTo = "TAL";

                        if (s.Length > 2 && s[1] == ':' && (s[0] == 'T' || s[0] == 'A' || s[0] == 'L'))
                        {
                            applyTo = s[0].ToString();
                            s = s[2..];
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
                        setFlag(ref reverse, ref i);
                        break;
                    case "--m3u":
                    case "--m3u8":
                        hasConfiguredM3uMode = true;
                        m3uOption = args[++i].ToLower().Trim() switch
                        {
                            "none" => M3uOption.None,
                            "index" => M3uOption.Index,
                            "all" => M3uOption.All,
                            _ => throw new ArgumentException($"Invalid m3u option '{args[i]}'"),
                        };
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
                        maxStaleTime = int.Parse(args[++i]);
                        break;
                    case "--cp":
                    case "--cd":
                    case "--processes":
                    case "--concurrent-processes":
                    case "--concurrent-downloads":
                        concurrentProcesses = int.Parse(args[++i]);
                        break;
                    case "--spt":
                    case "--searches-per-time":
                        searchesPerTime = int.Parse(args[++i]);
                        break;
                    case "--srt":
                    case "--searches-renew-time":
                        searchRenewTime = int.Parse(args[++i]);
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
                            maxAlbumTrackCount = int.Parse(a[..^1]);
                        }
                        else if (a.Last() == '+')
                        {
                            minAlbumTrackCount = int.Parse(a[..^1]);
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
                        setFlag(ref setAlbumMaxTrackCount, ref i);
                        break;
                    case "--emtc":
                    case "--extract-min-track-count":
                        setFlag(ref setAlbumMinTrackCount, ref i);
                        break;
                    case "--aa":
                    case "--album-art":
                        albumArtOption = args[++i].ToLower().Trim() switch
                        {
                            "default" => AlbumArtOption.Default,
                            "largest" => AlbumArtOption.Largest,
                            "most" => AlbumArtOption.Most,
                            "most-largest" => AlbumArtOption.MostLargest,
                            _ => throw new ArgumentException($"Invalid album art download mode '{args[i]}'"),
                        };
                        break;
                    case "--aao":
                    case "--aa-only":
                    case "--album-art-only":
                        setFlag(ref albumArtOnly, ref i);
                        if (albumArtOption == AlbumArtOption.Default)
                            albumArtOption = AlbumArtOption.Largest;
                        preferredCond = new FileConditions();
                        necessaryCond = new FileConditions();
                        break;
                    case "--aif":
                    case "--album-ignore-fails":
                        setFlag(ref albumIgnoreFails, ref i);
                        break;
                    case "-t":
                    case "--interactive":
                        setFlag(ref interactiveMode, ref i);
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
                    case "--pst":
                    case "--pstt":
                    case "--pref-strict-title":
                        setFlag(ref preferredCond.StrictTitle, ref i);
                        break;
                    case "--psa":
                    case "--pref-strict-artist":
                        setFlag(ref preferredCond.StrictArtist, ref i);
                        break;
                    case "--psal":
                    case "--pref-strict-album":
                        setFlag(ref preferredCond.StrictAlbum, ref i);
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
                    case "--stt":
                    case "--strict-title":
                        setFlag(ref necessaryCond.StrictTitle, ref i);
                        break;
                    case "--sa":
                    case "--strict-artist":
                        setFlag(ref necessaryCond.StrictArtist, ref i);
                        break;
                    case "--sal":
                    case "--strict-album":
                        setFlag(ref necessaryCond.StrictAlbum, ref i);
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
                        setFlag(ref noModifyShareCount, ref i);
                        break;
                    case "-d":
                    case "--desperate":
                        setFlag(ref desperateSearch, ref i);
                        break;
                    case "--dm":
                    case "--display":
                    case "--display-mode":
                        displayMode = args[++i].ToLower().Trim() switch
                        {
                            "single" => DisplayMode.Single,
                            "double" => DisplayMode.Double,
                            "simple" => DisplayMode.Simple,
                            _ => throw new ArgumentException($"Invalid display mode '{args[i]}'"),
                        };
                        break;
                    case "--sm":
                    case "--skip-mode":
                        skipMode = args[++i].ToLower().Trim() switch
                        {
                            "name" => SkipMode.Name,
                            "name-cond" => SkipMode.NameCond,
                            "tag" => SkipMode.Tag,
                            "tag-cond" => SkipMode.TagCond,
                            "m3u" => SkipMode.M3u,
                            "m3u-cond" => SkipMode.M3uCond,
                            _ => throw new ArgumentException($"Invalid skip mode '{args[i]}'"),
                        };
                        break;
                    case "--smmd":
                    case "--skip-mode-music-dir":
                        skipModeMusicDir = args[++i].ToLower().Trim() switch
                        {
                            "name" => SkipMode.Name,
                            "name-cond" => SkipMode.NameCond,
                            "tag" => SkipMode.Tag,
                            "tag-cond" => SkipMode.TagCond,
                            _ => throw new ArgumentException($"Invalid music dir skip mode '{args[i]}'"),
                        };
                        break;
                    case "--nrsc":
                    case "--no-remove-special-chars":
                        setFlag(ref noRemoveSpecialChars, ref i);
                        break;
                    case "--amw":
                    case "--artist-maybe-wrong":
                        setFlag(ref artistMaybeWrong, ref i);
                        break;
                    case "--fs":
                    case "--fast-search":
                        setFlag(ref fastSearch, ref i);
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
                        setFlag(ref debugInfo, ref i);
                        break;
                    case "--sc":
                    case "--strict":
                    case "--strict-conditions":
                        setFlag(ref preferredCond.AcceptMissingProps, ref i, false);
                        setFlag(ref necessaryCond.AcceptMissingProps, ref i, false);
                        break;
                    case "--yda":
                    case "--yt-dlp-argument":
                        ytdlpArgument = args[++i];
                        break;
                    case "-a":
                    case "--album":
                        setFlag(ref album, ref i);
                        break;
                    case "--oc":
                    case "--on-complete":
                        onComplete = args[++i];
                        break;
                    case "--ftd":
                    case "--fails-to-downrank":
                        downrankOn = -int.Parse(args[++i]);
                        break;
                    case "--fti":
                    case "--fails-to-ignore":
                        ignoreOn = -int.Parse(args[++i]);
                        break;
                    case "--uer":
                    case "--unknown-error-retries":
                        unknownErrorRetries = int.Parse(args[++i]);
                        break;
                    case "--profile":
                        profile = args[++i];
                        break;
                    case "--nbf":
                    case "--no-browse-folder":
                        setFlag(ref noBrowseFolder, ref i);
                        break;
                    case "--sepc":
                    case "--skip-existing-pref-cond":
                        setFlag(ref skipExistingPrefCond, ref i);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }
            else
            {
                if (!inputSet)
                {
                    input = args[i].Trim();
                    inputSet = true;
                }
                else
                    throw new ArgumentException($"Invalid argument \'{args[i]}\'. Input is already set to \'{input}\'");
            }
        }
    }
}