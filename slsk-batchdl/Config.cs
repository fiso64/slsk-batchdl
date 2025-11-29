using Enums;
using Models;
using System.Text;
using System.Text.RegularExpressions;

// undocumented options
// --login, --random-login, --no-modify-share-count, --unknown-error-retries
// --invalid-replace-str, --cond, --pref
// --fast-search-delay, --fast-search-min-up-speed
// --min-album-track-count, --max-album-track-count, --extract-max-track-count, --extract-min-track-count 
// --skip-mode-music-dir, --skip-mode-output-dir, --album-parallel-search-count, --album-track-count-max-retries
// --remove-brackets, --no-remove-special-chars, --parse-title, --extract-artist, --from-html

public class Config
{
    public FileConditions necessaryCond = new()
    {
        Formats = new string[] { "mp3", "flac", "ogg", "m4a", "opus", "wav", "aac", "alac" },
    };

    public FileConditions preferredCond = new()
    {
        Formats = new string[] { "mp3" },
        LengthTolerance = 3,
        MinBitrate = 200,
        MaxBitrate = 2500,
        MaxSampleRate = 48000,
        StrictTitle = true,
        StrictAlbum = true,
    };

    public string parentDir = Directory.GetCurrentDirectory();
    public string input = "";
    public string m3uFilePath = "";
    public string indexFilePath = "";
    public string skipMusicDir = "";
    public string spotifyId = "";
    public string spotifySecret = "";
    public string spotifyToken = "";
    public string spotifyRefresh = "";
    public string ytKey = "";
    public string username = "";
    public string password = "";
    public string artistCol = "";
    public string albumCol = "";
    public string titleCol = "";
    public string ytIdCol = "";
    public string descCol = "";
    public string trackCountCol = "";
    public string lengthCol = "";
    public string timeUnit = "s";
    public string nameFormat = "";
    public string invalidReplaceStr = " ";
    public string ytdlpArgument = "";
    public string confPath = "";
    public string profile = "";
    public string failedAlbumPath = "";
    public string logFilePath = "";
    public string mockFilesDir = "";
    public string parseTitleTemplate = "";
    public string htmlFromFile = "";
    public string userDescription = "";
    public bool aggregate = false;
    public bool album = false;
    public bool albumArtOnly = false;
    public bool interactiveMode = false;
    public bool setAlbumMinTrackCount = true;
    public bool setAlbumMaxTrackCount = false;
    public bool skipNotFound = false;
    public bool desperateSearch = false;
    public bool noRemoveSpecialChars = false;
    public bool artistMaybeWrong = false;
    public bool fastSearch = false;
    public bool ytParse = false;
    public bool removeFt = false;
    public bool removeBrackets = false;
    public bool reverse = false;
    public bool useYtdlp = false;
    public bool removeTracksFromSource = false;
    public bool getDeleted = false;
    public bool deletedOnly = false;
    public bool removeSingleCharacterSearchTerms = false;
    public bool relax = false;
    public bool noModifyShareCount = false;
    public bool useRandomLogin = false;
    public bool noBrowseFolder = false;
    public bool skipCheckCond = false;
    public bool skipCheckPrefCond = false;
    public bool noProgress = false;
    public bool writePlaylist = false;
    public bool skipExisting = true;
    public bool writeIndex = true;
    public bool parallelAlbumSearch = false;
    public bool mockFilesReadTags = true;
    public bool extractArtist = false;
    public bool noIncompleteExt = false;
    public int downrankOn = -1;
    public int ignoreOn = -2;
    public int minAlbumTrackCount = -1;
    public int maxAlbumTrackCount = -1;
    public int albumTrackCountMaxRetries = 5;
    public int fastSearchDelay = 300;
    public int minSharesAggregate = 2;
    public int maxTracks = int.MaxValue;
    public int offset = 0;
    public int maxStaleTime = 30000;
    public int searchTimeout = 6000;
    public int concurrentProcesses = 2;
    public int unknownErrorRetries = 2;
    public int maxRetriesPerTrack = 30;
    public int listenPort = 49998;
    public int searchesPerTime = 34;
    public int searchRenewTime = 220;
    public int aggregateLengthTol = 3;
    public int parallelAlbumSearchProcesses = 5;
    public int connectTimeout = 20000;
    public double fastSearchMinUpSpeed = 1.0;
    public List<string>? onComplete = null;
    public List<(Track, Track)>? regex = null;
    public Logger.LogLevel logLevel = Logger.LogLevel.Info;
    public AlbumArtOption albumArtOption = AlbumArtOption.Default;
    public InputType inputType = InputType.None;
    public SkipMode skipMode = SkipMode.Index;
    public SkipMode skipModeMusicDir = SkipMode.Name;
    public PrintOption printOption = PrintOption.None;

    public bool HasAutoProfiles { get; private set; } = false;
    public bool NeedLogin => !PrintTracks && (printOption & PrintOption.Index) == 0;
    public bool RequiresInput => (printOption & PrintOption.Index) == 0;
    public bool DoNotDownload => printOption != PrintOption.None;
    public bool PrintTracks => (printOption & PrintOption.Tracks) != 0;
    public bool PrintResults => (printOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
    public bool PrintFull => (printOption & PrintOption.Full) != 0;
    public bool NonVerbosePrint => (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
    public bool DeleteAlbumOnFail => failedAlbumPath == "delete";
    public bool IgnoreAlbumFail => failedAlbumPath == "disable";
    public bool HasOnComplete => onComplete != null && onComplete.Any(x => !string.IsNullOrWhiteSpace(x));

    private Dictionary<string, (List<string> args, string? cond)> configProfiles;
    private HashSet<string> appliedProfiles;
    private string[] arguments;
    bool hasConfiguredIndex = false;
    bool confPathChanged = false;

    public Config() { }

    public Config(string[] args)
    {
        configProfiles = new Dictionary<string, (List<string> args, string? cond)>();
        appliedProfiles = new HashSet<string>();
        arguments = args;

        arguments = args.SelectMany(arg =>
        {
            if (arg.Length > 2 && arg[0] == '-')
            {
                if (arg[1] == '-')
                {
                    if (arg.Length > 3 && arg.Contains('='))
                        return arg.Split('=', 2); // --arg=val becomes --arg val
                }
                else if (!arg.Contains(' '))
                {
                    return arg[1..].Select(c => $"-{c}"); // -abc becomes -a -b -c
                }
            }
            return new[] { arg };
        }).ToArray();

        SetConfigPath(arguments);

        if (confPath != "none" && (confPathChanged || File.Exists(confPath)))
        {
            ParseConfig(confPath);
            ApplyDefaultConfig();
        }

        int profileIndex = Array.FindLastIndex(arguments, x => x == "--profile");

        if (profileIndex != -1)
        {
            if (profileIndex + 1 >= arguments.Length)
                InputError("Option requires parameter");

            profile = arguments[profileIndex + 1];
            if (profile == "help" || profile == "list")
            {
                ListProfiles();
                Environment.Exit(0);
            }
        }

        ApplyProfiles(profile);

        ProcessArgs(arguments);

        if (input.Length == 0 && RequiresInput)
            InputError($"No input provided");
    }

    public Config Copy() // deep copies all fields except configProfiles and arguments
    {
        var copy = (Config)this.MemberwiseClone();

        copy.necessaryCond = new FileConditions(necessaryCond);
        copy.preferredCond = new FileConditions(preferredCond);

        if (regex != null)
        {
            copy.regex = new List<(Track, Track)>();
            foreach (var (x, y) in regex)
                copy.regex.Add((new Track(x), new Track(y)));
        }

        copy.appliedProfiles = new HashSet<string>(appliedProfiles);

        copy.configProfiles = configProfiles;
        copy.arguments = arguments;

        return copy;
    }

    void SetConfigPath(string[] args)
    {
        int idx1 = Array.FindLastIndex(args, x => x == "--nc" || x == "--no-config");

        if (idx1 != -1 && !(idx1 < args.Length - 1 && args[idx1 + 1] == "false"))
        {
            confPath = "none";
            confPathChanged = true;
            return;
        }

        int idx2 = Array.FindLastIndex(args, x => x == "-c" || x == "--config");

        if (idx2 != -1)
        {
            confPathChanged = true;

            if (confPath == "none")
                return;

            confPath = Utils.ExpandVariables(args[idx2 + 1]);
            if (File.Exists(Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath)))
                confPath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath);
        }

        if (!confPathChanged)
        {
            var configPaths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sldl", "sldl.conf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sldl", "sldl.conf"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sldl.conf")
            };

            string? xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdgConfigHome))
            {
                configPaths.Add(Path.Combine(xdgConfigHome, "sldl", "sldl.conf"));
            }

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


    public void PostProcessArgs(TrackLists trackLists) // must be run after extracting tracklist
    {
        if (DoNotDownload)
            concurrentProcesses = 1;

        ignoreOn = Math.Min(ignoreOn, downrankOn);

        writeIndex = WillWriteIndex(trackLists);

        if (albumArtOnly && albumArtOption == AlbumArtOption.Default)
            albumArtOption = AlbumArtOption.Largest;

        nameFormat = nameFormat.Trim();

        if (string.IsNullOrWhiteSpace(parentDir))
            parentDir = Directory.GetCurrentDirectory();

        confPath = Utils.GetFullPath(Utils.ExpandVariables(confPath));
        parentDir = Utils.GetFullPath(Utils.ExpandVariables(parentDir));
        m3uFilePath = Utils.GetFullPath(Utils.ExpandVariables(m3uFilePath));
        indexFilePath = Utils.GetFullPath(Utils.ExpandVariables(indexFilePath));
        skipMusicDir = Utils.GetFullPath(Utils.ExpandVariables(skipMusicDir));
        logFilePath = Utils.GetFullPath(Utils.ExpandVariables(logFilePath));
        mockFilesDir = Utils.GetFullPath(Utils.ExpandVariables(mockFilesDir));

        if (failedAlbumPath.Length == 0)
            failedAlbumPath = Path.Join(parentDir, "failed");
        else if (failedAlbumPath != "disable" && failedAlbumPath != "delete")
            failedAlbumPath = Utils.GetFullPath(Utils.ExpandVariables(failedAlbumPath));
    }


    public bool WillWriteIndex(TrackLists? trackLists)
    {
        if (DoNotDownload)
        {
            return false;
        }
        else if (!hasConfiguredIndex && trackLists != null && !trackLists.lists.Any(x => x.enablesIndexByDefault))
        {
            return false;
        }
        return writeIndex;
    }


    void ParseConfig(string path)
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
                InputError($"Error parsing config '{path}' at line {i}");

            var x = l.Split('=', 2, StringSplitOptions.TrimEntries);
            string key = x[0];
            string val = x[1];

            if (val[0] == '"' && val[^1] == '"')
                val = val[1..^1];

            if (!configProfiles.ContainsKey(curProfile))
                configProfiles[curProfile] = (new List<string>(), null);

            if (key == "profile-cond" && curProfile != "default")
            {
                var a = configProfiles[curProfile].args;
                configProfiles[curProfile] = (a, val);
                HasAutoProfiles = true;
            }
            else
            {
                if (key.Length == 1)
                    key = '-' + key;
                else
                    key = "--" + key;

                configProfiles[curProfile].args.Add(key);
                configProfiles[curProfile].args.Add(val);
            }
        }
    }


    public bool NeedUpdateProfiles(TrackListEntry tle)
    {
        if (DoNotDownload)
            return false;
        if (!HasAutoProfiles)
            return false;

        foreach ((var key, var val) in configProfiles)
        {
            if (key == "default" || val.cond == null)
                continue;

            bool condSatisfied = ProfileConditionSatisfied(val.cond, tle);
            bool alreadyApplied = appliedProfiles.Contains(key);

            if (condSatisfied && !alreadyApplied)
                return true;
            if (!condSatisfied && alreadyApplied)
                return true;
        }

        return false;
    }


    public bool NeedUpdateProfiles(TrackListEntry tle, out List<(string name, List<string> args)>? toApply)
    {
        toApply = null;

        if (DoNotDownload)
            return false;
        if (!HasAutoProfiles)
            return false;

        bool needUpdate = false;
        toApply = new List<(string name, List<string> args)>();

        foreach ((var key, var val) in configProfiles)
        {
            if (key == "default" || val.cond == null)
                continue;

            bool condSatisfied = ProfileConditionSatisfied(val.cond, tle);
            bool alreadyApplied = appliedProfiles.Contains(key);

            if (condSatisfied && !alreadyApplied)
                needUpdate = true;
            if (!condSatisfied && alreadyApplied)
                needUpdate = true;

            if (condSatisfied)
                toApply.Add((key, val.args));
        }

        return needUpdate;
    }


    public Config UpdateProfiles(TrackListEntry tle, TrackLists trackLists)
    {
        if (!NeedUpdateProfiles(tle, out var toApply))
            return this;

        var newConfig = this.Copy();

        // don't forget to reset appendable config vars
        newConfig.onComplete = null;
        newConfig.regex = null;

        newConfig.appliedProfiles.Clear();

        newConfig.ApplyDefaultConfig();

        foreach (var (name, args) in toApply)
        {
            tle.AddPrintLine($"Applying auto profile: {name}");
            newConfig.ProcessArgs(args);
            newConfig.appliedProfiles.Add(name);
        }

        newConfig.ApplyProfiles(newConfig.profile);

        newConfig.ProcessArgs(newConfig.arguments);

        newConfig.PostProcessArgs(trackLists);

        return newConfig;
    }


    void ApplyDefaultConfig()
    {
        if (configProfiles.ContainsKey("default"))
        {
            ProcessArgs(configProfiles["default"].args);
            appliedProfiles.Add("default");
        }
    }


    void ApplyProfiles(string names)
    {
        foreach (var name in names.Split(','))
        {
            if (name.Length > 0 && name != "default")
            {
                if (configProfiles.ContainsKey(name))
                {
                    ProcessArgs(configProfiles[name].args);
                    appliedProfiles.Add(name);
                }
                else
                    InputWarning($"No profile '{name}' found in config");
            }
        }
    }


    object GetVarValue(string var, TrackListEntry? tle = null)
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
            _ => InputError<object>($"Unrecognized profile condition variable {var}")
        };
    }


    public bool ProfileConditionSatisfied(string cond, TrackListEntry? tle = null)
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
                InputError("Invalid token at this position: " + token);

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


    void ListProfiles()
    {
        Console.WriteLine("Available profiles:");
        foreach ((var key, var val) in configProfiles)
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


    public static FileConditions ParseConditions(string input, Track? track = null)
    {
        static void UpdateMinMax(string value, string condition, ref int? min, ref int? max)
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

        static void UpdateMinMax2(string value, string condition, ref int min, ref int max)
        {
            int? nullableMin = min;
            int? nullableMax = max;
            UpdateMinMax(value, condition, ref nullableMin, ref nullableMax);
            min = nullableMin ?? min;
            max = nullableMax ?? max;
        }

        var cond = new FileConditions();

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
                    cond.Formats = value.Split(',', tr).Select(x => x.TrimStart('.')).ToArray();
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
                case "strictconditions":
                case "acceptmissing":
                case "acceptmissingprops":
                    cond.AcceptMissingProps = bool.Parse(value);
                    break;
                case "albumtrackcount":
                    if (track != null)
                        UpdateMinMax2(value, condition, ref track.MinAlbumTrackCount, ref track.MaxAlbumTrackCount);
                    break;
                default:
                    InputError($"Unknown condition '{condition}'");
                    break;
            }
        }

        return cond;
    }


    void ProcessArgs(IReadOnlyList<string> args)
    {
        bool getFlag(ref int i, bool trueVal = true)
        {
            if (i >= args.Count - 1 || args[i + 1].StartsWith('-'))
            {
                return trueVal;
            }
            else if (args[i + 1] == "false")
            {
                i++;
                return !trueVal;
            }
            else if (args[i + 1] == "true")
            {
                i++;
                return trueVal;
            }
            else
            {
                return trueVal;
            }
        }

        void setFlag(ref bool flag, ref int i, bool trueVal = true)
        {
            flag = getFlag(ref i, trueVal);
        }

        void setNullableFlag(ref bool? flag, ref int i, bool trueVal = true)
        {
            flag = getFlag(ref i, trueVal);
        }

        string GetParameter(ref int i)
        {
            i++;
            if (i < 0 || i >= args.Count)
                InputError("Option requires parameter");
            return args[i];
        }

        int getIntParameter(ref int i)
        {
            var value = GetParameter(ref i);
            if (!int.TryParse(value.Replace("_", ""), out var res))
                InputError($"Option requires integer parameter: {value}");
            return res;
        }

        double getDoubleParameter(ref int i)
        {
            var value = GetParameter(ref i);
            if (!double.TryParse(value.Replace("_", ""), out var res))
                InputError($"Option requires double parameter: {value}");
            return res;
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
                        input = GetParameter(ref i);
                        break;
                    case "--it":
                    case "--input-type":
                        inputType = GetParameter(ref i).ToLower().Trim() switch
                        {
                            "none" => InputType.None,
                            "csv" => InputType.CSV,
                            "youtube" => InputType.YouTube,
                            "spotify" => InputType.Spotify,
                            "bandcamp" => InputType.Bandcamp,
                            "string" => InputType.String,
                            "list" => InputType.List,
                            _ => InputError<InputType>($"Invalid input type '{args[i]}'"),
                        };
                        break;
                    case "-p":
                    case "--path":
                    case "--parent":
                        parentDir = GetParameter(ref i);
                        break;
                    case "-c":
                    case "--config":
                        confPath = GetParameter(ref i);
                        break;
                    case "--nc":
                    case "--no-config":
                        confPath = "none";
                        break;
                    case "--smd":
                    case "--skip-music-dir":
                        skipMusicDir = GetParameter(ref i);
                        break;
                    case "-g":
                    case "--aggregate":
                        setFlag(ref aggregate, ref i);
                        break;
                    case "--msa":
                    case "--min-shares-aggregate":
                        minSharesAggregate = getIntParameter(ref i);
                        break;
                    case "--rf":
                    case "--relax":
                    case "--relax-filtering":
                        setFlag(ref relax, ref i);
                        break;
                    case "--si":
                    case "--spotify-id":
                        spotifyId = GetParameter(ref i);
                        break;
                    case "--ss":
                    case "--spotify-secret":
                        spotifySecret = GetParameter(ref i);
                        break;
                    case "--stk":
                    case "--spotify-token":
                        spotifyToken = GetParameter(ref i);
                        break;
                    case "--str":
                    case "--spotify-refresh":
                        spotifyRefresh = GetParameter(ref i);
                        break;
                    case "--yk":
                    case "--youtube-key":
                        ytKey = GetParameter(ref i);
                        break;
                    case "-l":
                    case "--login":
                        var login = GetParameter(ref i).Split(';', 2);
                        username = login[0];
                        password = login[1];
                        break;
                    case "--user":
                    case "--username":
                        username = GetParameter(ref i);
                        break;
                    case "--pass":
                    case "--password":
                        password = GetParameter(ref i);
                        break;
                    case "--rl":
                    case "--random-login":
                        setFlag(ref useRandomLogin, ref i);
                        break;
                    case "--ac":
                    case "--artist-col":
                        artistCol = GetParameter(ref i);
                        break;
                    case "--tc":
                    case "--track-col":
                    case "--title-col":
                        titleCol = GetParameter(ref i);
                        break;
                    case "--alc":
                    case "--album-col":
                        albumCol = GetParameter(ref i);
                        break;
                    case "--ydc":
                    case "--yt-desc-col":
                        descCol = GetParameter(ref i);
                        break;
                    case "--atcc":
                    case "--album-track-count-col":
                        trackCountCol = GetParameter(ref i);
                        break;
                    case "--yic":
                    case "--yt-id-col":
                        ytIdCol = GetParameter(ref i);
                        break;
                    case "--lc":
                    case "--length-col":
                        lengthCol = GetParameter(ref i);
                        break;
                    case "--tf":
                    case "--time-format":
                        timeUnit = GetParameter(ref i);
                        break;
                    case "-n":
                    case "--number":
                        maxTracks = getIntParameter(ref i);
                        break;
                    case "-o":
                    case "--offset":
                        offset = getIntParameter(ref i);
                        break;
                    case "--nf":
                    case "--name-format":
                        nameFormat = GetParameter(ref i);
                        break;
                    case "--irs":
                    case "--invalid-replace-str":
                        invalidReplaceStr = GetParameter(ref i);
                        break;
                    case "--print":
                        printOption = GetParameter(ref i).ToLower().Trim() switch
                        {
                            "none" => PrintOption.None,
                            "tracks" => PrintOption.Tracks,
                            "results" => PrintOption.Results,
                            "tracks-full" => PrintOption.Tracks | PrintOption.Full,
                            "results-full" => PrintOption.Results | PrintOption.Full,
                            "link" => PrintOption.Link,
                            "json" => PrintOption.Json,
                            "json-all" => PrintOption.Json | PrintOption.Full,
                            "index" => PrintOption.Index,
                            "index-failed" => PrintOption.Index | PrintOption.IndexFailed,
                            _ => InputError<PrintOption>($"Invalid print option '{args[i]}'"),
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
                    case "--pl":
                    case "--print-link":
                        printOption = PrintOption.Link;
                        break;
                    case "--pj":
                    case "--print-json":
                        printOption = PrintOption.Json;
                        break;
                    case "--pjf":
                    case "--print-json-full":
                        printOption = PrintOption.Json | PrintOption.Full;
                        break;
                    case "--yp":
                    case "--yt-parse":
                        setFlag(ref ytParse, ref i);
                        break;
                    case "--yd":
                    case "--yt-dlp":
                        setFlag(ref useYtdlp, ref i);
                        break;
                    case "--nse":
                    case "--no-skip-existing":
                        setFlag(ref skipExisting, ref i, false);
                        break;
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
                        string s = GetParameter(ref i).Replace("\\;", "<<semicol>>");
                        bool append = false;

                        if (s.TrimStart().StartsWith("+ "))
                        {
                            append = true;
                            s = s.TrimStart().Substring(2);
                        }

                        regex = regex ?? new();
                        var regexToReplace = new Track();
                        var regexReplaceBy = new Track();

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

                        if (!append) regex.Clear();
                        regex.Add((regexToReplace, regexReplaceBy));
                        break;
                    case "-r":
                    case "--reverse":
                        setFlag(ref reverse, ref i);
                        break;
                    case "--wp":
                    case "--write-playlist":
                        setFlag(ref writePlaylist, ref i);
                        break;
                    case "--nwp":
                    case "--no-write-playlist":
                        setFlag(ref writePlaylist, ref i, false);
                        break;
                    case "--pp":
                    case "--playlist-path":
                        m3uFilePath = GetParameter(ref i);
                        break;
                    case "--nwi":
                    case "--no-write-index":
                        hasConfiguredIndex = true;
                        setFlag(ref writeIndex, ref i, false);
                        break;
                    case "--wi":
                    case "--write-index":
                        hasConfiguredIndex = true;
                        setFlag(ref writeIndex, ref i);
                        break;
                    case "--ip":
                    case "--index-path":
                        hasConfiguredIndex = true;
                        indexFilePath = GetParameter(ref i);
                        break;
                    case "--lp":
                    case "--port":
                    case "--listen-port":
                        listenPort = getIntParameter(ref i);
                        break;
                    case "--st":
                    case "--search-time":
                    case "--search-timeout":
                        searchTimeout = getIntParameter(ref i);
                        break;
                    case "--Mst":
                    case "--stale-time":
                    case "--max-stale-time":
                        maxStaleTime = getIntParameter(ref i);
                        break;
                    case "--cp":
                    case "--cd":
                    case "--processes":
                    case "--concurrent-processes":
                    case "--concurrent-downloads":
                        concurrentProcesses = getIntParameter(ref i);
                        break;
                    case "--spt":
                    case "--searches-per-time":
                        searchesPerTime = getIntParameter(ref i);
                        break;
                    case "--srt":
                    case "--searches-renew-time":
                        searchRenewTime = getIntParameter(ref i);
                        break;
                    case "--Mr":
                    case "--retries":
                    case "--max-retries":
                        maxRetriesPerTrack = getIntParameter(ref i);
                        break;
                    case "--atc":
                    case "--album-track-count":
                        string a = GetParameter(ref i);
                        if (a == "-1")
                        {
                            minAlbumTrackCount = -1;
                            maxAlbumTrackCount = -1;
                        }
                        else if (a.Last() == '-')
                        {
                            if (!int.TryParse(a[..^1], out int res))
                                InputError("Option requires integer parameter");
                            maxAlbumTrackCount = res;
                        }
                        else if (a.Last() == '+')
                        {
                            if (!int.TryParse(a[..^1], out int res))
                                InputError("Option requires integer parameter");
                            minAlbumTrackCount = res;
                        }
                        else
                        {
                            if (!int.TryParse(a, out int res))
                                InputError("Option requires integer parameter");
                            minAlbumTrackCount = res;
                            maxAlbumTrackCount = minAlbumTrackCount;
                        }
                        break;
                    case "--matc":
                    case "--min-album-track-count":
                        minAlbumTrackCount = getIntParameter(ref i);
                        break;
                    case "--Matc":
                    case "--max-album-track-count":
                        maxAlbumTrackCount = getIntParameter(ref i);
                        break;
                    case "--eMtc":
                    case "--extract-max-track-count":
                        setFlag(ref setAlbumMaxTrackCount, ref i);
                        break;
                    case "--emtc":
                    case "--extract-min-track-count":
                        setFlag(ref setAlbumMinTrackCount, ref i);
                        break;
                    case "--album-track-count-max-retries":
                        albumTrackCountMaxRetries = getIntParameter(ref i);
                        break;
                    case "--aa":
                    case "--album-art":
                        albumArtOption = GetParameter(ref i).ToLower().Trim() switch
                        {
                            "default" => AlbumArtOption.Default,
                            "largest" => AlbumArtOption.Largest,
                            "most" => AlbumArtOption.Most,
                            _ => InputError<AlbumArtOption>($"Invalid album art download mode '{args[i]}'"),
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
                    case "--fap":
                    case "--failed-album-path":
                        failedAlbumPath = GetParameter(ref i);
                        break;
                    case "-t":
                    case "--interactive":
                        setFlag(ref interactiveMode, ref i);
                        break;
                    case "--pf":
                    case "--paf":
                    case "--pref-format":
                        preferredCond.Formats = GetParameter(ref i).ToLower().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim().TrimStart('.')).ToArray();
                        break;
                    case "--plt":
                    case "--pref-tolerance":
                    case "--pref-length-tol":
                    case "--pref-length-tolerance":
                        preferredCond.LengthTolerance = getIntParameter(ref i);
                        break;
                    case "--pmbr":
                    case "--pref-min-bitrate":
                        preferredCond.MinBitrate = getIntParameter(ref i);
                        break;
                    case "--pMbr":
                    case "--pref-max-bitrate":
                        preferredCond.MaxBitrate = getIntParameter(ref i);
                        break;
                    case "--pmsr":
                    case "--pref-min-samplerate":
                        preferredCond.MinSampleRate = getIntParameter(ref i);
                        break;
                    case "--pMsr":
                    case "--pref-max-samplerate":
                        preferredCond.MaxSampleRate = getIntParameter(ref i);
                        break;
                    case "--pmbd":
                    case "--pref-min-bitdepth":
                        preferredCond.MinBitDepth = getIntParameter(ref i);
                        break;
                    case "--pMbd":
                    case "--pref-max-bitdepth":
                        preferredCond.MaxBitDepth = getIntParameter(ref i);
                        break;
                    case "--pst":
                    case "--pstt":
                    case "--pref-strict-title":
                        setNullableFlag(ref preferredCond.StrictTitle, ref i);
                        break;
                    case "--psar":
                    case "--pref-strict-artist":
                        setNullableFlag(ref preferredCond.StrictArtist, ref i);
                        break;
                    case "--psal":
                    case "--pref-strict-album":
                        setNullableFlag(ref preferredCond.StrictAlbum, ref i);
                        break;
                    case "--panl":
                    case "--pref-accept-no-length":
                        setNullableFlag(ref preferredCond.AcceptNoLength, ref i);
                        break;
                    case "--pbu":
                    case "--pref-banned-users":
                        preferredCond.BannedUsers = GetParameter(ref i).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "--af":
                    case "--format":
                        necessaryCond.Formats = GetParameter(ref i).ToLower().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim().TrimStart('.')).ToArray();
                        break;
                    case "--lt":
                    case "--tolerance":
                    case "--length-tol":
                    case "--length-tolerance":
                        necessaryCond.LengthTolerance = getIntParameter(ref i);
                        break;
                    case "--mbr":
                    case "--min-bitrate":
                        necessaryCond.MinBitrate = getIntParameter(ref i);
                        break;
                    case "--Mbr":
                    case "--max-bitrate":
                        necessaryCond.MaxBitrate = getIntParameter(ref i);
                        break;
                    case "--msr":
                    case "--min-samplerate":
                        necessaryCond.MinSampleRate = getIntParameter(ref i);
                        break;
                    case "--Msr":
                    case "--max-samplerate":
                        necessaryCond.MaxSampleRate = getIntParameter(ref i);
                        break;
                    case "--mbd":
                    case "--min-bitdepth":
                        necessaryCond.MinBitDepth = getIntParameter(ref i);
                        break;
                    case "--Mbd":
                    case "--max-bitdepth":
                        necessaryCond.MaxBitDepth = getIntParameter(ref i);
                        break;
                    case "--stt":
                    case "--strict-title":
                        setNullableFlag(ref necessaryCond.StrictTitle, ref i);
                        break;
                    case "--sar":
                    case "--strict-artist":
                        setNullableFlag(ref necessaryCond.StrictArtist, ref i);
                        break;
                    case "--sal":
                    case "--strict-album":
                        setNullableFlag(ref necessaryCond.StrictAlbum, ref i);
                        break;
                    case "--bu":
                    case "--banned-users":
                        necessaryCond.BannedUsers = GetParameter(ref i).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "--anl":
                    case "--accept-no-length":
                        setNullableFlag(ref necessaryCond.AcceptNoLength, ref i);
                        break;
                    case "--cond":
                    case "--conditions":
                        necessaryCond.AddConditions(ParseConditions(GetParameter(ref i)));
                        break;
                    case "--pc":
                    case "--pref":
                    case "--preferred-conditions":
                        preferredCond.AddConditions(ParseConditions(GetParameter(ref i)));
                        break;
                    case "--nmsc":
                    case "--no-modify-share-count":
                        setFlag(ref noModifyShareCount, ref i);
                        break;
                    case "-d":
                    case "--desperate":
                        setFlag(ref desperateSearch, ref i);
                        break;
                    case "--np":
                    case "--no-progress":
                        setFlag(ref noProgress, ref i);
                        break;
                    case "--progress":
                        setFlag(ref noProgress, ref i, false);
                        break;
                    case "--smod":
                    case "--skip-mode-output-dir":
                        skipMode = GetParameter(ref i).ToLower().Trim() switch
                        {
                            "name" => SkipMode.Name,
                            "tag" => SkipMode.Tag,
                            "index" => SkipMode.Index,
                            _ => InputError<SkipMode>($"Invalid output dir skip mode '{args[i]}'"),
                        };
                        break;
                    case "--smmd":
                    case "--skip-mode-music-dir":
                        skipModeMusicDir = GetParameter(ref i).ToLower().Trim() switch
                        {
                            "name" => SkipMode.Name,
                            "tag" => SkipMode.Tag,
                            _ => InputError<SkipMode>($"Invalid music dir skip mode '{args[i]}'"),
                        };
                        break;
                    case "--nrsc":
                    case "--no-remove-special-chars":
                        setFlag(ref noRemoveSpecialChars, ref i);
                        break;
                    case "--rsc":
                    case "--remove-special-chars":
                        setFlag(ref noRemoveSpecialChars, ref i, false);
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
                        fastSearchDelay = getIntParameter(ref i);
                        break;
                    case "--fsmus":
                    case "--fast-search-min-up-speed":
                        fastSearchMinUpSpeed = getDoubleParameter(ref i);
                        break;
                    case "-v":
                    case "--verbose":
                    case "--debug":
                        if (getFlag(ref i))
                        {
                            logLevel = Logger.LogLevel.Debug;
                            Logger.SetConsoleLogLevel(logLevel);
                        }
                        break;
                    case "--lf":
                    case "--log-file":
                        logFilePath = GetParameter(ref i);
                        if (logFilePath.Length > 0)
                            Logger.AddOrReplaceFile(Utils.GetFullPath(Utils.ExpandVariables(logFilePath)));
                        break;
                    case "--sc":
                    case "--strict":
                    case "--strict-conditions":
                        setNullableFlag(ref preferredCond.AcceptMissingProps, ref i, false);
                        setNullableFlag(ref necessaryCond.AcceptMissingProps, ref i, false);
                        break;
                    case "--yda":
                    case "--yt-dlp-argument":
                        ytdlpArgument = GetParameter(ref i);
                        break;
                    case "-a":
                    case "--album":
                        setFlag(ref album, ref i);
                        break;
                    case "--oc":
                    case "--on-complete":
                        var onCompleteStr = GetParameter(ref i);
                        onComplete = onComplete ?? new List<string>();
                        if (onCompleteStr.TrimStart().StartsWith("+ "))
                        {
                            onComplete.Add(onCompleteStr.TrimStart().Substring(2));
                        }
                        else
                        {
                            onComplete.Clear();
                            onComplete.Add(onCompleteStr);
                        }
                        break;
                    case "--ftd":
                    case "--fails-to-downrank":
                        downrankOn = -getIntParameter(ref i);
                        break;
                    case "--fti":
                    case "--fails-to-ignore":
                        ignoreOn = -getIntParameter(ref i);
                        break;
                    case "--uer":
                    case "--unknown-error-retries":
                        unknownErrorRetries = getIntParameter(ref i);
                        break;
                    case "--profile":
                        profile = GetParameter(ref i);
                        break;
                    case "--nbf":
                    case "--no-browse-folder":
                        setFlag(ref noBrowseFolder, ref i);
                        break;
                    case "--bf":
                    case "--browse-folder":
                        setFlag(ref noBrowseFolder, ref i, false);
                        break;
                    case "--scc":
                    case "--skip-check-cond":
                        setFlag(ref skipCheckCond, ref i);
                        break;
                    case "--scpc":
                    case "--skip-check-pref-cond":
                        setFlag(ref skipCheckPrefCond, ref i);
                        break;
                    case "--alt":
                    case "--aggregate-length-tol":
                        aggregateLengthTol = getIntParameter(ref i);
                        break;
                    case "--aps":
                    case "--album-parallel-search":
                        setFlag(ref parallelAlbumSearch, ref i);
                        break;
                    case "--apsc":
                    case "--album-parallel-search-count":
                        parallelAlbumSearchProcesses = getIntParameter(ref i);
                        break;
                    case "--mock-files-dir":
                        mockFilesDir = GetParameter(ref i);
                        break;
                    case "--mock-files-no-read-tags":
                        setFlag(ref mockFilesReadTags, ref i, false);
                        break;
                    case "--parse-title":
                        parseTitleTemplate = GetParameter(ref i);
                        break;
                    case "--from-html":
                        htmlFromFile = GetParameter(ref i);
                        break;
                    case "--ea":
                    case "--extract-artist":
                        setFlag(ref extractArtist, ref i);
                        break;
                    case "--nie":
                    case "--no-incomplete-ext":
                        setFlag(ref noIncompleteExt, ref i);
                        break;
                    case "--cto":
                    case "--connect-timeout":
                        connectTimeout = getIntParameter(ref i);
                        break;
                    case "--user-description":
                        userDescription = GetParameter(ref i);
                        break;
                    default:
                        InputError($"Unknown argument: {args[i]}");
                        break;
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
                    InputError($"Invalid argument \'{args[i]}\'. Input is already set to \'{input}\'");
            }
        }
    }


    public static string[] GetArgsArray(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (c == '\"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }
            }
            else
            {
                currentArg.Append(c);
            }
        }

        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }

        return args.ToArray();
    }


    public static void InputWarning(string message)
    {
        Logger.Warn($"Warning: {message}");
    }


    public static void InputError(string message)
    {
        Logger.Fatal($"Input error: {message}");
        Environment.Exit(1);
    }


    public static T InputError<T>(string message)
    {
        InputError(message);
        return default;
    }

    public Logger.LogLevel GetConsoleLogLevel()
    {
        if (NonVerbosePrint)
            return Logger.LogLevel.Error;
        return logLevel;
    }
}
