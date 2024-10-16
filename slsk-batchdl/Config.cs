﻿
using AngleSharp.Css;
using Enums;
using Models;
using System.Text;
using System.Text.RegularExpressions;


public class Config
{
    public FileConditions necessaryCond = new();

    public FileConditions preferredCond = new()
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
    public string onComplete = "";
    public string confPath = "";
    public string profile = "";
    public string failedAlbumPath = "";
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
    public bool debugInfo = false;
    public bool noModifyShareCount = false;
    public bool useRandomLogin = false;
    public bool noBrowseFolder = false;
    public bool skipCheckCond = false;
    public bool skipCheckPrefCond = false;
    public bool noProgress = false;
    public bool writePlaylist = false;
    public bool skipExisting = true;
    public bool writeIndex = true;
    public int downrankOn = -1;
    public int ignoreOn = -2;
    public int minAlbumTrackCount = -1;
    public int maxAlbumTrackCount = -1;
    public int fastSearchDelay = 300;
    public int minSharesAggregate = 2;
    public int maxTracks = int.MaxValue;
    public int offset = 0;
    public int maxStaleTime = 50000;
    public int updateDelay = 100;
    public int searchTimeout = 6000;
    public int concurrentProcesses = 2;
    public int unknownErrorRetries = 2;
    public int maxRetriesPerTrack = 30;
    public int listenPort = 49998;
    public int searchesPerTime = 34;
    public int searchRenewTime = 220;
    public int aggregateLengthTol = 3;
    public double fastSearchMinUpSpeed = 1.0;
    public Track regexToReplace = new();
    public Track regexReplaceBy = new();
    public AlbumArtOption albumArtOption = AlbumArtOption.Default;
    public InputType inputType = InputType.None;
    public SkipMode skipMode = SkipMode.Index;
    public SkipMode skipModeMusicDir = SkipMode.Name;
    public PrintOption printOption = PrintOption.None;

    public bool HasAutoProfiles { get; private set; } = false;
    public bool DoNotDownload => (printOption & (PrintOption.Results | PrintOption.Tracks)) != 0;
    public bool PrintTracks => (printOption & PrintOption.Tracks) != 0;
    public bool PrintResults => (printOption & PrintOption.Results) != 0;
    public bool PrintTracksFull => (printOption & PrintOption.Tracks) != 0 && (printOption & PrintOption.Full) != 0;
    public bool PrintResultsFull => (printOption & PrintOption.Results) != 0 && (printOption & PrintOption.Full) != 0;
    public bool DeleteAlbumOnFail => failedAlbumPath == "delete";
    public bool IgnoreAlbumFail => failedAlbumPath == "disable";

    readonly Dictionary<string, (List<string> args, string? cond)> configProfiles = new();
    readonly HashSet<string> appliedProfiles = new();
    bool hasConfiguredIndex = false;
    bool confPathChanged = false;
    string[] arguments;
    FileConditions? undoTempConds = null;
    FileConditions? undoTempPrefConds = null;

    private static Config Instance = new();

    public static Config I { get { return Instance; } }

    private Config() { }

    private Config(Dictionary<string, (List<string> args, string? cond)> cfg, string[] args) 
    { 
        configProfiles = cfg;
        arguments = args;
    }


    public void LoadAndParse(string[] args)
    {
        int helpIdx = Array.FindLastIndex(args, x => x == "--help" || x == "-h");
        if (args.Length == 0 || helpIdx >= 0)
        {
            string option = helpIdx + 1 < args.Length ? args[helpIdx + 1] : "";
            Help.PrintHelp(option);
            Environment.Exit(0);
        }

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
            profile = arguments[profileIndex + 1];
            if (profile == "help")
            {
                ListProfiles();
                Environment.Exit(0);
            }
        }

        ApplyProfiles(profile);

        ProcessArgs(arguments);
    }


    void SetConfigPath(string[] args)
    {
        int idx = Array.FindLastIndex(args, x => x == "-c" || x == "--config");

        if (idx != -1)
        {
            confPath = Utils.ExpandUser(args[idx + 1]);
            confPathChanged = true;

            if(File.Exists(Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath)))
                confPath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, confPath);
        }

        if (!confPathChanged)
        {
            var configPaths = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sldl", "sldl.conf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sldl", "sldl.conf"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sldl.conf"),
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


    public void PostProcessArgs() // must be run after extracting tracklist
    {
        if (DoNotDownload || debugInfo)
            concurrentProcesses = 1;

        ignoreOn = Math.Min(ignoreOn, downrankOn);

        if (DoNotDownload)
        {
            writeIndex = false;   
        }
        else if (!hasConfiguredIndex && Program.trackLists != null && !Program.trackLists.lists.Any(x => x.enablesIndexByDefault))
        {
            writeIndex = false;
        }    

        if (albumArtOnly && albumArtOption == AlbumArtOption.Default)
            albumArtOption = AlbumArtOption.Largest;
        
        nameFormat = nameFormat.Trim();

        confPath        = Utils.GetFullPath(Utils.ExpandUser(confPath));
        parentDir       = Utils.GetFullPath(Utils.ExpandUser(parentDir));
        m3uFilePath     = Utils.GetFullPath(Utils.ExpandUser(m3uFilePath));
        indexFilePath   = Utils.GetFullPath(Utils.ExpandUser(indexFilePath));
        skipMusicDir    = Utils.GetFullPath(Utils.ExpandUser(skipMusicDir));
        failedAlbumPath = Utils.GetFullPath(Utils.ExpandUser(failedAlbumPath));

        if (failedAlbumPath.Length == 0)
            failedAlbumPath = Path.Join(parentDir, "failed");
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
                throw new ArgumentException($"Error parsing config '{path}' at line {i}");

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


    public static bool UpdateProfiles(TrackListEntry tle)
    {
        if (I.DoNotDownload)
            return false;
        if (!I.HasAutoProfiles)
            return false;

        bool needUpdate = false;
        var toApply = new List<(string name, List<string> args)>();

        foreach ((var key, var val) in I.configProfiles)
        {
            if (key == "default" || val.cond == null)
                continue;

            bool condSatisfied = I.ProfileConditionSatisfied(val.cond, tle);
            bool alreadyApplied = I.appliedProfiles.Contains(key);
            
            if (condSatisfied && !alreadyApplied)
                needUpdate = true;
            if (!condSatisfied && alreadyApplied)
                needUpdate = true;
            
            if (condSatisfied)
                toApply.Add((key, val.args));
        }

        if (!needUpdate)
            return false;

        // this means that auto profiles can't change --profile and --config
        var profile = I.profile;
        Instance = new Config(I.configProfiles, I.arguments);
        I.ApplyDefaultConfig();
        I.ApplyProfiles(profile);

        foreach (var (name, args) in toApply)
        {
            Console.WriteLine($"Applying auto profile: {name}");
            I.ProcessArgs(args);
            I.appliedProfiles.Add(name);
        }

        I.ProcessArgs(I.arguments);
        I.PostProcessArgs();

        return true;
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
                    Console.WriteLine($"Error: No profile '{name}' found in config");
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
            _ => throw new ArgumentException($"Unrecognized profile condition variable {var}")
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


    public void AddTemporaryConditions(FileConditions? cond, FileConditions? prefCond)
    {
        if (cond != null)
            undoTempConds = necessaryCond.AddConditions(cond);
        if (prefCond != null)
            undoTempPrefConds = preferredCond.AddConditions(prefCond);
    }


    public void RestoreConditions()
    {
        if (undoTempConds != null)
            necessaryCond.AddConditions(undoTempConds);
        if (undoTempPrefConds != null)
            preferredCond.AddConditions(undoTempPrefConds);
    }


    public static FileConditions ParseConditions(string input)
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
                case "strictconditions":
                case "acceptmissing":
                case "acceptmissingprops":
                    cond.AcceptMissingProps = bool.Parse(value);
                    break;
                default:
                    throw new ArgumentException($"Unknown condition '{condition}'");
            }
        }

        return cond;
    }


    void ProcessArgs(IReadOnlyList<string> args)
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

        void setNullableFlag(ref bool? flag, ref int i, bool trueVal = true)
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
                            "list" => InputType.List,
                            _ => throw new ArgumentException($"Invalid input type '{args[i]}'"),
                        };
                        break;
                    case "-p":
                    case "--path":
                    case "--parent":
                        parentDir = args[++i];
                        break;
                    case "-c":
                    case "--config":
                        confPath = args[++i];
                        break;
                    case "--smd":
                    case "--skip-music-dir":
                        skipMusicDir = args[++i];
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
                    case "--stk":
                    case "--spotify-token":
                        spotifyToken = args[++i];
                        break;
                    case "--str":
                    case "--spotify-refresh":
                        spotifyRefresh = args[++i];
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
                    case "--title-col":
                        titleCol = args[++i];
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
                    case "--nse":
                    case "--no-skip-existing":
                        setFlag(ref skipExisting, ref i, false);
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
                    case "--wp":
                    case "--write-playlist":
                        setFlag(ref writePlaylist, ref i);
                        break;
                    case "--pp":
                    case "--playlist-path":
                        m3uFilePath = args[++i];
                        break;
                    case "--nwi":
                    case "--no-write-index":
                        hasConfiguredIndex = true;
                        setFlag(ref writeIndex, ref i, false);
                        break;
                    case "--ip":
                    case "--index-path":
                        hasConfiguredIndex = true;
                        indexFilePath = args[++i];
                        break;
                    case "--lp":
                    case "--port":
                    case "--listen-port":
                        listenPort = int.Parse(args[++i]);
                        break;
                    case "--st":
                    case "--search-time":
                    case "--search-timeout":
                        searchTimeout = int.Parse(args[++i]);
                        break;
                    case "--Mst":
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
                    case "--Mr":
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
                    case "--fap":
                    case "--failed-album-path":
                        failedAlbumPath = args[++i];
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
                        setNullableFlag(ref preferredCond.StrictTitle, ref i);
                        break;
                    case "--psa":
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
                        setNullableFlag(ref necessaryCond.StrictTitle, ref i);
                        break;
                    case "--sa":
                    case "--strict-artist":
                        setNullableFlag(ref necessaryCond.StrictArtist, ref i);
                        break;
                    case "--sal":
                    case "--strict-album":
                        setNullableFlag(ref necessaryCond.StrictAlbum, ref i);
                        break;
                    case "--bu":
                    case "--banned-users":
                        necessaryCond.BannedUsers = args[++i].Split(',');
                        break;
                    case "--anl":
                    case "--accept-no-length":
                        setNullableFlag(ref necessaryCond.AcceptNoLength, ref i);
                        break;
                    case "--cond":
                    case "--conditions":
                        necessaryCond.AddConditions(ParseConditions(args[++i]));
                        break;
                    case "--pc":
                    case "--pref":
                    case "--preferred-conditions":
                        preferredCond.AddConditions(ParseConditions(args[++i]));
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
                    case "--smod":
                    case "--skip-mode-output-dir":
                        skipMode = args[++i].ToLower().Trim() switch
                        {
                            "name" => SkipMode.Name,
                            "tag" => SkipMode.Tag,
                            "index" => SkipMode.Index,
                            _ => throw new ArgumentException($"Invalid output dir skip mode '{args[i]}'"),
                        };
                        break;
                    case "--smmd":
                    case "--skip-mode-music-dir":
                        skipModeMusicDir = args[++i].ToLower().Trim() switch
                        {
                            "name" => SkipMode.Name,
                            "tag" => SkipMode.Tag,
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
                        setNullableFlag(ref preferredCond.AcceptMissingProps, ref i, false);
                        setNullableFlag(ref necessaryCond.AcceptMissingProps, ref i, false);
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
                        aggregateLengthTol = int.Parse(args[++i]);
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
}
