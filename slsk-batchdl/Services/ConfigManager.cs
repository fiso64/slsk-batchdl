using System.Text.RegularExpressions;
using Enums;
using Models;
using Jobs;
using Settings;

namespace Services;

/// Owns config file loading, CLI binding, and profile management.
/// Config classes themselves have no knowledge of profiles, files, or argv.
///
/// Typical call flow (CLI):
///   var file   = ConfigManager.Load(explicitConfPath);
///   var (eng, dl, cli) = ConfigManager.Bind(file, args, profileName);
///   // per-job, inside the engine loop:
///   dl = ConfigManager.UpdateProfiles(dl, eng, cli, file, args, job);
public static partial class ConfigManager
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// Discovers and parses the config file.
    /// Pass explicitPath = "none" to skip loading entirely.
    public static ConfigFile Load(string? explicitPath = null)
    {
        string path = ResolveConfigPath(explicitPath);
        if (path == "none" || !File.Exists(path))
            return new ConfigFile(path, new Dictionary<string, ProfileEntry>());
        return ParseConfigFile(path);
    }

    /// Pre-scan argv for --config/-c / --no-config and return the path to use.
    /// This mirrors Config.SetConfigPath so that Load() can be called before full parsing.
    public static string ExtractConfigPath(IReadOnlyList<string> args)
    {
        int noConf = FindLastFlag(args, "--nc", "--no-config");
        if (noConf != -1 && !IsExplicitFalse(args, noConf))
            return "none";

        int conf = FindLastFlag(args, "-c", "--config");
        if (conf != -1 && conf + 1 < args.Count)
        {
            string p = Utils.ExpandVariables(args[conf + 1]);
            string adjacent = Path.Join(AppDomain.CurrentDomain.BaseDirectory, p);
            return File.Exists(adjacent) ? adjacent : p;
        }

        foreach (var candidate in DefaultConfigPaths())
            if (File.Exists(candidate))
                return candidate;

        return "";
    }

    /// Creates fresh settings, applies the config file's [default] profile,
    /// any named profile, and finally cliArgs — in that order.
    /// Returns the three top-level settings objects.
    public static (EngineSettings Engine, DownloadSettings Download, CliSettings Cli)
        Bind(ConfigFile file, IReadOnlyList<string> cliArgs, string? profileName = null)
    {
        var engine = new EngineSettings();
        var dl     = new DownloadSettings();
        var cli    = new CliSettings();

        // Apply default profile
        if (file.Profiles.TryGetValue("default", out var def))
            ApplyTokens(def.Tokens, engine, dl, cli);

        // Apply named profiles (comma-separated)
        if (!string.IsNullOrEmpty(profileName))
        {
            foreach (var name in profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == "default") continue;
                if (file.Profiles.TryGetValue(name, out var prof))
                    ApplyTokens(prof.Tokens, engine, dl, cli);
                else
                    Logger.Warn($"Warning: No profile '{name}' found in config");
            }
        }

        // Apply CLI args
        ApplyTokens(NormalizeArgs(cliArgs), engine, dl, cli);

        PostProcess(engine, dl);

        return (engine, dl, cli);
    }

    /// Re-evaluates auto-profiles against the job. Returns the same instance if
    /// nothing changed; returns a freshly-bound instance if profiles changed.
    public static DownloadSettings UpdateProfiles(
        DownloadSettings current,
        ConfigFile file,
        IReadOnlyList<string> cliArgs,
        string? profileName,
        Job job,
        CliSettings? cli = null)
    {
        if (current.PrintOption != PrintOption.None)
            return current;
        if (!file.HasAutoProfiles)
            return current;

        // Determine which auto-profiles currently apply
        var toApply = new List<(string Name, ProfileEntry Entry)>();
        bool needUpdate = false;

        foreach (var (name, entry) in file.Profiles)
        {
            if (name == "default" || entry.Condition == null) continue;

            bool satisfied  = ProfileConditionSatisfied(entry.Condition, current, job, cli);
            bool wasApplied = current.AppliedAutoProfiles.Contains(name);

            if (satisfied != wasApplied) needUpdate = true;
            if (satisfied) toApply.Add((name, entry));
        }

        if (!needUpdate) return current;

        // Reconstruct from scratch: default → applicable auto-profiles → named profile → CLI
        var engine2 = new EngineSettings();
        var dl2     = new DownloadSettings();
        var cli2    = new CliSettings();

        if (file.Profiles.TryGetValue("default", out var def))
            ApplyTokens(def.Tokens, engine2, dl2, cli2);

        foreach (var (name, entry) in toApply)
        {
            job.AddPrintLine($"Applying auto profile: {name}");
            ApplyTokens(entry.Tokens, engine2, dl2, cli2);
        }

        if (!string.IsNullOrEmpty(profileName))
        {
            foreach (var name in profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == "default") continue;
                if (file.Profiles.TryGetValue(name, out var prof))
                    ApplyTokens(prof.Tokens, engine2, dl2, cli2);
            }
        }

        ApplyTokens(NormalizeArgs(cliArgs), engine2, dl2, cli2);

        dl2.AppliedAutoProfiles = [..toApply.Select(x => x.Name)];

        PostProcess(engine2, dl2);

        return dl2;
    }

    public static bool ProfileConditionSatisfied(string cond, DownloadSettings settings, Job? job = null, CliSettings? cli = null)
    {
        var tokens = new Queue<string>(CondTokenRegex().Split(cond).Where(t => !string.IsNullOrWhiteSpace(t)));

        bool ParseExpression()
        {
            bool left = ParseAndExpression();
            while (tokens.Count > 0 && tokens.Peek() == "||")
            {
                tokens.Dequeue();
                left = left || ParseAndExpression();
            }
            return left;
        }

        bool ParseAndExpression()
        {
            bool left = ParsePrimary();
            while (tokens.Count > 0 && tokens.Peek() == "&&")
            {
                tokens.Dequeue();
                left = left && ParsePrimary();
            }
            return left;
        }

        bool ParsePrimary()
        {
            string tok = tokens.Dequeue();
            if (tok == "(") { var r = ParseExpression(); tokens.Dequeue(); return r; }
            if (tok == "!") return !ParsePrimary();
            if (tok.StartsWith('"')) throw new Exception($"Input error: Invalid token at this position: {tok}");

            if (tokens.Count > 0 && (tokens.Peek() == "==" || tokens.Peek() == "!="))
            {
                string op  = tokens.Dequeue();
                string val = tokens.Dequeue().Trim('"').ToLower();
                string cur = GetVarValue(tok, settings, job, cli).ToString()!.ToLower();
                return op == "==" ? cur == val : cur != val;
            }

            return (bool)GetVarValue(tok, settings, job, cli);
        }

        return ParseExpression();
    }

    /// Determines whether an index file should be written for this submission.
    /// Mirrors the old Config.WillWriteIndex logic.
    public static bool WillWriteIndex(DownloadSettings dl, JobList? queue = null)
    {
        if (dl.DoNotDownload) return false;
        if (!dl.Output.HasConfiguredIndex && queue != null && !queue.Jobs.Any(x => x.EnablesIndexByDefault))
            return false;
        return dl.Output.WriteIndex;
    }

    public static IReadOnlyList<string> GetProfileNames(ConfigFile file)
        => file.Profiles.Keys.Where(k => k != "default").OrderBy(k => k).ToList();

    // ── Token application ─────────────────────────────────────────────────────

    /// Maps one token list to settings mutations. Every supported flag has an explicit case.
    private static void ApplyTokens(
        IList<string> tokens,
        EngineSettings engine,
        DownloadSettings dl,
        CliSettings cli)
    {
        var tr = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;

        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];

            // Positional: bare URL/path sets input
            if (!t.StartsWith('-'))
            {
                if (dl.Extraction.Input != null)
                    throw new Exception($"Input error: Invalid argument '{t}'. Input is already set to '{dl.Extraction.Input}'");
                dl.Extraction.Input = t;
                continue;
            }

            switch (t)
            {
                // ── Meta (already consumed before ApplyTokens) ────────────────
                case "-c": case "--config":   i++; break;
                case "--nc": case "--no-config": break;
                case "--profile":             i++; break;

                // ── EngineSettings ────────────────────────────────────────────
                case "--user": case "--username":
                    engine.Username = Next(tokens, ref i, t); break;
                case "--pass": case "--password":
                    engine.Password = Next(tokens, ref i, t); break;
                case "-l": case "--login":
                {
                    var parts = Next(tokens, ref i, t).Split(';', 2);
                    engine.Username = parts[0];
                    engine.Password = parts.Length > 1 ? parts[1] : "";
                    break;
                }
                case "--rl": case "--random-login":
                    engine.UseRandomLogin = ParseBoolFlag(tokens, ref i); break;
                case "--lp": case "--port": case "--listen-port":
                    engine.ListenPort = ParseInt(Next(tokens, ref i, t), t); break;
                case "--no-listen":
                    engine.ListenPort = null; break;
                case "--cp": case "--concurrent-searches":
                    engine.ConcurrentSearches = ParseInt(Next(tokens, ref i, t), t); break;
                case "--concurrent-extractors":
                    engine.ConcurrentExtractors = ParseInt(Next(tokens, ref i, t), t); break;
                case "--spt": case "--searches-per-time":
                    engine.SearchesPerTime = ParseInt(Next(tokens, ref i, t), t); break;
                case "--srt": case "--searches-renew-time":
                    engine.SearchRenewTime = ParseInt(Next(tokens, ref i, t), t); break;
                case "--nmsc": case "--no-modify-share-count":
                    engine.NoModifyShareCount = ParseBoolFlag(tokens, ref i); break;
                case "-v": case "--verbose": case "--debug":
                    engine.LogLevel = Logger.LogLevel.Debug; break;
                case "--lf": case "--log-file":
                    engine.LogFilePath = Next(tokens, ref i, t); break;
                case "--cto": case "--connect-timeout":
                    engine.ConnectTimeout = ParseInt(Next(tokens, ref i, t), t); break;
                case "--user-description":
                    engine.UserDescription = Next(tokens, ref i, t); break;
                case "--shared-files":
                    engine.SharedFiles = ParseInt(Next(tokens, ref i, t), t); break;
                case "--shared-folders":
                    engine.SharedFolders = ParseInt(Next(tokens, ref i, t), t); break;
                case "--mock-files-dir":
                    engine.MockFilesDir = Next(tokens, ref i, t); break;
                case "--mock-files-no-read-tags":
                    engine.MockFilesReadTags = false; break;
                case "--mock-files-slow":
                    engine.MockFilesSlow = ParseBoolFlag(tokens, ref i); break;

                // ── CliSettings ───────────────────────────────────────────────
                case "-t": case "--interactive":
                    cli.InteractiveMode = ParseBoolFlag(tokens, ref i); break;
                case "--np": case "--no-progress":
                    cli.NoProgress = true; break;
                case "--progress":
                    cli.NoProgress = false; break;
                case "--progress-json":
                    cli.ProgressJson = ParseBoolFlag(tokens, ref i); break;

                // ── OutputSettings ────────────────────────────────────────────
                case "-p": case "--path": case "--parent":
                    dl.Output.ParentDir = Next(tokens, ref i, t); break;
                case "--nf": case "--name-format":
                    dl.Output.NameFormat = Next(tokens, ref i, t); break;
                case "--irs": case "--invalid-replace-str":
                    dl.Output.InvalidReplaceStr = Next(tokens, ref i, t); break;
                case "--wp": case "--write-playlist":
                    dl.Output.WritePlaylist = ParseBoolFlag(tokens, ref i); break;
                case "--nwp": case "--no-write-playlist":
                    dl.Output.WritePlaylist = false; break;
                case "--pp": case "--playlist-path":
                    dl.Output.M3uFilePath = Next(tokens, ref i, t); break;
                case "--wi": case "--write-index":
                    dl.Output.WriteIndex = ParseBoolFlag(tokens, ref i);
                    dl.Output.HasConfiguredIndex = true; break;
                case "--nwi": case "--no-write-index":
                    dl.Output.WriteIndex = false;
                    dl.Output.HasConfiguredIndex = true; break;
                case "--ip": case "--index-path":
                    dl.Output.IndexFilePath = Next(tokens, ref i, t);
                    dl.Output.HasConfiguredIndex = true; break;
                case "--failed-album-path":
                    dl.Output.FailedAlbumPath = Next(tokens, ref i, t); break;
                case "--oc": case "--on-complete":
                {
                    string val = Next(tokens, ref i, t);
                    if (val.TrimStart().StartsWith("+ "))
                    {
                        dl.Output.OnComplete ??= [];
                        dl.Output.OnComplete.Add(val.TrimStart()[2..]);
                    }
                    else
                    {
                        dl.Output.OnComplete = [val];
                    }
                    break;
                }
                case "--print":
                    dl.PrintOption = ParsePrintOption(Next(tokens, ref i, t), t); break;
                case "--pt": case "--print-tracks":
                    dl.PrintOption = PrintOption.Tracks; break;
                case "--ptf": case "--print-tracks-full":
                    dl.PrintOption = PrintOption.Tracks | PrintOption.Full; break;
                case "--pr": case "--print-results":
                    dl.PrintOption = PrintOption.Results; break;
                case "--prf": case "--print-results-full":
                    dl.PrintOption = PrintOption.Results | PrintOption.Full; break;
                case "--pl": case "--print-link":
                    dl.PrintOption = PrintOption.Link; break;
                case "--pj": case "--print-json":
                    dl.PrintOption = PrintOption.Json; break;
                case "--pjf": case "--print-json-full":
                    dl.PrintOption = PrintOption.Json | PrintOption.Full; break;

                // ── ExtractionSettings ────────────────────────────────────────
                case "-i": case "--input":
                {
                    string val = Next(tokens, ref i, t);
                    if (dl.Extraction.Input != null)
                        throw new Exception($"Input error: Invalid argument '{val}'. Input is already set to '{dl.Extraction.Input}'");
                    dl.Extraction.Input = val;
                    break;
                }
                case "--it": case "--input-type":
                    dl.Extraction.InputType = Enum.Parse<InputType>(Next(tokens, ref i, t).Replace("-", ""), ignoreCase: true); break;
                case "-n": case "--number":
                    dl.Extraction.MaxTracks = ParseInt(Next(tokens, ref i, t), t); break;
                case "-o": case "--offset":
                    dl.Extraction.Offset = ParseInt(Next(tokens, ref i, t), t); break;
                case "-r": case "--reverse":
                    dl.Extraction.Reverse = ParseBoolFlag(tokens, ref i); break;
                case "--gd": case "--get-deleted":
                    dl.YouTube.GetDeleted = ParseBoolFlag(tokens, ref i); break;
                case "--do": case "--deleted-only":
                    dl.YouTube.DeletedOnly = ParseBoolFlag(tokens, ref i); break;
                case "--rfp": case "--rfs": case "--remove-from-source": case "--remove-from-playlist":
                    dl.Extraction.RemoveTracksFromSource = ParseBoolFlag(tokens, ref i); break;
                case "--msa": case "--min-shares-aggregate":
                    dl.Search.MinSharesAggregate = ParseInt(Next(tokens, ref i, t), t); break;
                case "--alt": case "--aggregate-length-tol":
                    dl.Search.AggregateLengthTol = ParseInt(Next(tokens, ref i, t), t); break;

                // ── AlbumSettings ─────────────────────────────────────────────
                case "-a": case "--album":
                    dl.Extraction.IsAlbum = ParseBoolFlag(tokens, ref i); break;
                case "-g": case "--aggregate":
                    dl.Search.IsAggregate = ParseBoolFlag(tokens, ref i); break;
                case "--aa": case "--album-art":
                    dl.Output.AlbumArtOption = Next(tokens, ref i, t).ToLower().Trim() switch
                    {
                        "default" => AlbumArtOption.Default,
                        "largest" => AlbumArtOption.Largest,
                        "most"    => AlbumArtOption.Most,
                        var s     => throw new Exception($"Input error: Invalid album art option '{s}'"),
                    };
                    break;
                case "--aao": case "--aa-only": case "--album-art-only":
                    dl.Output.AlbumArtOnly = ParseBoolFlag(tokens, ref i);
                    if (dl.Output.AlbumArtOnly)
                    {
                        if (dl.Output.AlbumArtOption == AlbumArtOption.Default)
                            dl.Output.AlbumArtOption = AlbumArtOption.Largest;
                        dl.Search.PreferredCond = new FileConditions();
                        dl.Search.NecessaryCond = new FileConditions();
                    }
                    break;
                case "--matc": case "--min-album-track-count":
                    dl.Search.NecessaryFolderCond.MinTrackCount = ParseInt(Next(tokens, ref i, t), t); break;
                case "--Matc": case "--max-album-track-count":
                    dl.Search.NecessaryFolderCond.MaxTrackCount = ParseInt(Next(tokens, ref i, t), t); break;
                case "--eMtc": case "--extract-max-track-count":
                    dl.Extraction.SetAlbumMaxTrackCount = ParseBoolFlag(tokens, ref i); break;
                case "--emtc": case "--extract-min-track-count":
                    dl.Extraction.SetAlbumMinTrackCount = ParseBoolFlag(tokens, ref i); break;
                case "--album-track-count-max-retries":
                    dl.Transfer.AlbumTrackCountMaxRetries = ParseInt(Next(tokens, ref i, t), t); break;
                case "--atc": case "--album-track-count":
                {
                    string a = Next(tokens, ref i, t);
                    if (a == "-1")
                        dl.Search.NecessaryFolderCond.MinTrackCount = dl.Search.NecessaryFolderCond.MaxTrackCount = -1;
                    else if (a.EndsWith('-'))
                        dl.Search.NecessaryFolderCond.MaxTrackCount = ParseInt(a[..^1], t);
                    else if (a.EndsWith('+'))
                        dl.Search.NecessaryFolderCond.MinTrackCount = ParseInt(a[..^1], t);
                    else
                        dl.Search.NecessaryFolderCond.MinTrackCount = dl.Search.NecessaryFolderCond.MaxTrackCount = ParseInt(a, t);
                    break;
                }

                // ── PreprocessSettings ────────────────────────────────────────
                case "--rft": case "--remove-ft":
                    dl.Preprocess.RemoveFt = ParseBoolFlag(tokens, ref i); break;
                case "--rb": case "--remove-brackets":
                    dl.Preprocess.RemoveBrackets = ParseBoolFlag(tokens, ref i); break;
                case "--amw": case "--artist-maybe-wrong":
                    dl.Search.ArtistMaybeWrong = ParseBoolFlag(tokens, ref i); break;
                case "--ea": case "--extract-artist":
                    dl.Preprocess.ExtractArtist = ParseBoolFlag(tokens, ref i); break;
                case "--parse-title":
                    dl.Preprocess.ParseTitleTemplate = Next(tokens, ref i, t); break;
                case "--re": case "--regex":
                    ApplyRegex(Next(tokens, ref i, t), dl.Preprocess); break;

                // ── SearchSettings ────────────────────────────────────────────
                case "--st": case "--search-time": case "--search-timeout":
                    dl.Search.SearchTimeout = ParseInt(Next(tokens, ref i, t), t); break;
                case "--Mst": case "--stale-time": case "--max-stale-time":
                    dl.Search.MaxStaleTime = ParseInt(Next(tokens, ref i, t), t); break;
                case "--Mr": case "--retries": case "--max-retries":
                    dl.Transfer.MaxRetriesPerTrack = ParseInt(Next(tokens, ref i, t), t); break;
                case "--uer": case "--unknown-error-retries":
                    dl.Transfer.UnknownErrorRetries = ParseInt(Next(tokens, ref i, t), t); break;
                case "--fs": case "--fast-search":
                    dl.Search.FastSearch = ParseBoolFlag(tokens, ref i); break;
                case "--fsd": case "--fast-search-delay":
                    dl.Search.FastSearchDelay = ParseInt(Next(tokens, ref i, t), t); break;
                case "--fsmus": case "--fast-search-min-up-speed":
                    dl.Search.FastSearchMinUpSpeed = ParseDouble(Next(tokens, ref i, t), t); break;
                case "-d": case "--desperate":
                    dl.Search.DesperateSearch = ParseBoolFlag(tokens, ref i); break;
                case "--nrsc": case "--no-remove-special-chars":
                    dl.Search.NoRemoveSpecialChars = ParseBoolFlag(tokens, ref i); break;
                case "--rsc": case "--remove-special-chars":
                    dl.Search.NoRemoveSpecialChars = false; break;
                case "--nbf": case "--no-browse-folder":
                    dl.Search.NoBrowseFolder = true; break;
                case "--bf": case "--browse-folder":
                    dl.Search.NoBrowseFolder = false; break;
                case "--nie": case "--no-incomplete-ext":
                    dl.Transfer.NoIncompleteExt = ParseBoolFlag(tokens, ref i); break;
                case "--rf": case "--relax": case "--relax-filtering":
                    dl.Search.Relax = ParseBoolFlag(tokens, ref i); break;
                case "--ftd": case "--fails-to-downrank":
                    dl.Search.DownrankOn = -ParseInt(Next(tokens, ref i, t), t); break;
                case "--fti": case "--fails-to-ignore":
                    dl.Search.IgnoreOn = -ParseInt(Next(tokens, ref i, t), t); break;

                // ── Necessary condition shorthands ────────────────────────────
                case "--af": case "--format":
                    dl.Search.NecessaryCond.Formats = [..Next(tokens, ref i, t).ToLower().Split(',', tr).Select(x => x.TrimStart('.'))]; break;
                case "--lt": case "--tolerance": case "--length-tol": case "--length-tolerance":
                    dl.Search.NecessaryCond.LengthTolerance = ParseInt(Next(tokens, ref i, t), t); break;
                case "--mbr": case "--min-bitrate":
                    dl.Search.NecessaryCond.MinBitrate    = ParseInt(Next(tokens, ref i, t), t); break;
                case "--Mbr": case "--max-bitrate":
                    dl.Search.NecessaryCond.MaxBitrate    = ParseInt(Next(tokens, ref i, t), t); break;
                case "--msr": case "--min-samplerate":
                    dl.Search.NecessaryCond.MinSampleRate = ParseInt(Next(tokens, ref i, t), t); break;
                case "--Msr": case "--max-samplerate":
                    dl.Search.NecessaryCond.MaxSampleRate = ParseInt(Next(tokens, ref i, t), t); break;
                case "--mbd": case "--min-bitdepth":
                    dl.Search.NecessaryCond.MinBitDepth   = ParseInt(Next(tokens, ref i, t), t); break;
                case "--Mbd": case "--max-bitdepth":
                    dl.Search.NecessaryCond.MaxBitDepth   = ParseInt(Next(tokens, ref i, t), t); break;
                case "--stt": case "--strict-title":
                    dl.Search.NecessaryCond.StrictTitle   = ParseNullableBool(tokens, ref i); break;
                case "--sar": case "--strict-artist":
                    dl.Search.NecessaryCond.StrictArtist  = ParseNullableBool(tokens, ref i); break;
                case "--sal": case "--strict-album":
                    dl.Search.NecessaryCond.StrictAlbum   = ParseNullableBool(tokens, ref i); break;
                case "--anl": case "--accept-no-length":
                    dl.Search.NecessaryCond.AcceptNoLength = ParseNullableBool(tokens, ref i); break;
                case "--bu": case "--banned-users":
                    dl.Search.NecessaryCond.BannedUsers   = Next(tokens, ref i, t).Split(',', tr); break;
                case "--sc": case "--strict": case "--strict-conditions":
                {
                    bool val = ParseBoolFlag(tokens, ref i);
                    dl.Search.NecessaryCond.AcceptMissingProps = !val;
                    dl.Search.PreferredCond.AcceptMissingProps = !val;
                    break;
                }
                case "--cond": case "--conditions":
                {
                    var fc = new FolderConditions();
                    dl.Search.NecessaryCond.AddConditions(ConditionParser.ParseFileConditions(Next(tokens, ref i, t), fc));
                    dl.Search.NecessaryFolderCond.AddConditions(fc);
                    break;
                }

                // ── Preferred condition shorthands ────────────────────────────
                case "--paf": case "--pf": case "--pref-format":
                    dl.Search.PreferredCond.Formats = [..Next(tokens, ref i, t).ToLower().Split(',', tr).Select(x => x.TrimStart('.'))]; break;
                case "--plt": case "--pref-tolerance": case "--pref-length-tol": case "--pref-length-tolerance":
                    dl.Search.PreferredCond.LengthTolerance = ParseInt(Next(tokens, ref i, t), t); break;
                case "--pmbr": case "--pref-min-bitrate":
                    dl.Search.PreferredCond.MinBitrate    = ParseInt(Next(tokens, ref i, t), t); break;
                case "--pMbr": case "--pref-max-bitrate":
                    dl.Search.PreferredCond.MaxBitrate    = ParseInt(Next(tokens, ref i, t), t); break;
                case "--pmsr": case "--pref-min-samplerate":
                    dl.Search.PreferredCond.MinSampleRate = ParseInt(Next(tokens, ref i, t), t); break;
                case "--pMsr": case "--pref-max-samplerate":
                    dl.Search.PreferredCond.MaxSampleRate = ParseInt(Next(tokens, ref i, t), t); break;
                case "--pmbd": case "--pref-min-bitdepth":
                    dl.Search.PreferredCond.MinBitDepth   = ParseInt(Next(tokens, ref i, t), t); break;
                case "--pMbd": case "--pref-max-bitdepth":
                    dl.Search.PreferredCond.MaxBitDepth   = ParseInt(Next(tokens, ref i, t), t); break;
                case "--pst": case "--pstt": case "--pref-strict-title":
                    dl.Search.PreferredCond.StrictTitle   = ParseNullableBool(tokens, ref i); break;
                case "--psar": case "--pref-strict-artist":
                    dl.Search.PreferredCond.StrictArtist  = ParseNullableBool(tokens, ref i); break;
                case "--psal": case "--pref-strict-album":
                    dl.Search.PreferredCond.StrictAlbum   = ParseNullableBool(tokens, ref i); break;
                case "--panl": case "--pref-accept-no-length":
                    dl.Search.PreferredCond.AcceptNoLength = ParseNullableBool(tokens, ref i); break;
                case "--pbu": case "--pref-banned-users":
                    dl.Search.PreferredCond.BannedUsers   = Next(tokens, ref i, t).Split(',', tr); break;
                case "--pc": case "--pref": case "--preferred-conditions":
                {
                    var fc = new FolderConditions();
                    dl.Search.PreferredCond.AddConditions(ConditionParser.ParseFileConditions(Next(tokens, ref i, t), fc));
                    dl.Search.PreferredFolderCond.AddConditions(fc);
                    break;
                }

                // ── SkipSettings ──────────────────────────────────────────────
                case "--se": case "--skip-existing":
                    dl.Skip.SkipExisting = ParseBoolFlag(tokens, ref i); break;
                case "--nse": case "--no-skip-existing":
                    dl.Skip.SkipExisting = false; break;
                case "--snf": case "--skip-not-found":
                    dl.Skip.SkipNotFound = ParseBoolFlag(tokens, ref i); break;
                case "--smd": case "--skip-music-dir":
                    dl.Skip.SkipMusicDir = Next(tokens, ref i, t); break;
                case "--smod": case "--skip-mode-output-dir":
                    dl.Skip.SkipMode = ParseSkipMode(Next(tokens, ref i, t), t, allowIndex: true); break;
                case "--smmd": case "--skip-mode-music-dir":
                    dl.Skip.SkipModeMusicDir = ParseSkipMode(Next(tokens, ref i, t), t, allowIndex: false); break;
                case "--scc": case "--skip-check-cond":
                    dl.Skip.SkipCheckCond = ParseBoolFlag(tokens, ref i); break;
                case "--scpc": case "--skip-check-pref-cond":
                    dl.Skip.SkipCheckPrefCond = ParseBoolFlag(tokens, ref i); break;

                // ── SpotifySettings ───────────────────────────────────────────
                case "--si": case "--spotify-id":
                    dl.Spotify.ClientId = Next(tokens, ref i, t); break;
                case "--ss": case "--spotify-secret":
                    dl.Spotify.ClientSecret = Next(tokens, ref i, t); break;
                case "--stk": case "--spotify-token":
                    dl.Spotify.Token = Next(tokens, ref i, t); break;
                case "--str": case "--spotify-refresh":
                    dl.Spotify.Refresh = Next(tokens, ref i, t); break;

                // ── YouTubeSettings ───────────────────────────────────────────
                case "--yk": case "--youtube-key":
                    dl.YouTube.ApiKey = Next(tokens, ref i, t); break;
                case "--yp": case "--yt-parse":
                    dl.Csv.YtParse = ParseBoolFlag(tokens, ref i); break;
                case "--yd": case "--yt-dlp":
                    dl.YtDlp.UseYtdlp = ParseBoolFlag(tokens, ref i); break;
                case "--yda": case "--yt-dlp-argument":
                    dl.YtDlp.YtdlpArgument = Next(tokens, ref i, t); break;

                // ── CsvSettings ───────────────────────────────────────────────
                case "--ac": case "--artist-col":
                    dl.Csv.ArtistCol = Next(tokens, ref i, t); break;
                case "--tc": case "--track-col": case "--title-col":
                    dl.Csv.TitleCol = Next(tokens, ref i, t); break;
                case "--alc": case "--album-col":
                    dl.Csv.AlbumCol = Next(tokens, ref i, t); break;
                case "--ydc": case "--yt-desc-col":
                    dl.Csv.DescCol = Next(tokens, ref i, t); break;
                case "--atcc": case "--album-track-count-col":
                    dl.Csv.TrackCountCol = Next(tokens, ref i, t); break;
                case "--yic": case "--yt-id-col":
                    dl.Csv.YtIdCol = Next(tokens, ref i, t); break;
                case "--lc": case "--length-col":
                    dl.Csv.LengthCol = Next(tokens, ref i, t); break;
                case "--tf": case "--time-format":
                    dl.Csv.TimeUnit = Next(tokens, ref i, t); break;
                case "--from-html":
                    dl.Bandcamp.HtmlFromFile = Next(tokens, ref i, t); break;

                default:
                    throw new Exception($"Input error: Unknown argument: {t}");
            }
        }
    }

    // ── Post-processing ───────────────────────────────────────────────────────

    private static void PostProcess(EngineSettings engine, DownloadSettings dl)
    {
        // Enforce IgnoreOn ≤ DownrankOn
        dl.Search.IgnoreOn = Math.Min(dl.Search.IgnoreOn, dl.Search.DownrankOn);

        // --deleted-only implies --get-deleted
        if (dl.YouTube.DeletedOnly)
            dl.YouTube.GetDeleted = true;

        // AlbumArtOption default when AlbumArtOnly
        if (dl.Output.AlbumArtOnly && dl.Output.AlbumArtOption == AlbumArtOption.Default)
            dl.Output.AlbumArtOption = AlbumArtOption.Largest;

        // Path expansion
        if (string.IsNullOrWhiteSpace(dl.Output.ParentDir))
            dl.Output.ParentDir = Directory.GetCurrentDirectory();

        dl.Output.ParentDir   = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.ParentDir));
        dl.Output.NameFormat  = dl.Output.NameFormat?.Trim();

        if (dl.Output.M3uFilePath != null)
            dl.Output.M3uFilePath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.M3uFilePath));
        if (dl.Output.IndexFilePath != null)
            dl.Output.IndexFilePath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.IndexFilePath));
        if (dl.Skip.SkipMusicDir != null)
            dl.Skip.SkipMusicDir = Utils.GetFullPath(Utils.ExpandVariables(dl.Skip.SkipMusicDir));
        if (engine.LogFilePath != null)
            engine.LogFilePath = Utils.GetFullPath(Utils.ExpandVariables(engine.LogFilePath));
        if (engine.MockFilesDir != null)
            engine.MockFilesDir = Utils.GetFullPath(Utils.ExpandVariables(engine.MockFilesDir));

        if (dl.Output.FailedAlbumPath == null)
            dl.Output.FailedAlbumPath = Path.Join(dl.Output.ParentDir, "failed");
        else if (dl.Output.FailedAlbumPath is not ("disable" or "delete"))
            dl.Output.FailedAlbumPath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.FailedAlbumPath));
    }

    // ── Config file parsing ───────────────────────────────────────────────────

    private static ConfigFile ParseConfigFile(string path)
    {
        var profiles = new Dictionary<string, ProfileEntry>();
        var curProfile = "default";
        bool hasAutoProfiles = false;

        foreach (var (line, lineNum) in File.ReadAllLines(path).Select((l, n) => (l.Trim(), n)))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                curProfile = line[1..^1];
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
                throw new Exception($"Input error: Error parsing config '{path}' at line {lineNum}");

            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            string key = parts[0];
            string val = parts[1];
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                val = val[1..^1];

            if (!profiles.ContainsKey(curProfile))
                profiles[curProfile] = new ProfileEntry([], null);

            if (key == "profile-cond")
            {
                if (curProfile != "default")
                {
                    profiles[curProfile] = profiles[curProfile] with { Condition = val };
                    hasAutoProfiles = true;
                }
            }
            else
            {
                string flag = key.Length == 1 ? $"-{key}" : $"--{key}";
                profiles[curProfile].Tokens.Add(flag);
                profiles[curProfile].Tokens.Add(val);
            }
        }

        return new ConfigFile(path, profiles, hasAutoProfiles);
    }

    private static string ResolveConfigPath(string? explicit_)
    {
        if (!string.IsNullOrEmpty(explicit_)) return explicit_;
        foreach (var p in DefaultConfigPaths())
            if (File.Exists(p)) return p;
        return "";
    }

    private static IEnumerable<string> DefaultConfigPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sldl", "sldl.conf");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sldl", "sldl.conf");
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
            yield return Path.Combine(xdg, "sldl", "sldl.conf");
        yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sldl.conf");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Normalize argv: expand --arg=val into --arg val, and -abc into -a -b -c.
    private static List<string> NormalizeArgs(IReadOnlyList<string> args)
    {
        var result = new List<string>(args.Count);
        foreach (var arg in args)
        {
            if (arg.Length > 2 && arg[0] == '-')
            {
                if (arg[1] == '-')
                {
                    if (arg.Contains('='))
                    {
                        var eq = arg.IndexOf('=');
                        result.Add(arg[..eq]);
                        result.Add(arg[(eq + 1)..]);
                        continue;
                    }
                }
                else if (!arg.Contains(' '))
                {
                    foreach (char c in arg[1..])
                        result.Add($"-{c}");
                    continue;
                }
            }
            result.Add(arg);
        }
        return result;
    }

    private static void ApplyRegex(string raw, PreprocessSettings pre)
    {
        string s = raw.Replace("\\;", "<<semicol>>");
        bool append = s.TrimStart().StartsWith("+ ");
        if (append) s = s.TrimStart()[2..];

        string applyTo = "TAL";
        if (s.Length > 2 && s[1] == ':' && s[0] is 'T' or 'A' or 'L')
        {
            applyTo = s[0].ToString();
            s = s[2..];
        }

        var parts  = s.Split(';');
        string pat = parts[0].Replace("<<semicol>>", ";");
        string rep = (parts.Length > 1 ? parts[1] : "").Replace("<<semicol>>", ";");

        var toReplace = new RegexFields
        {
            Title  = applyTo.Contains('T') ? pat : "",
            Artist = applyTo.Contains('A') ? pat : "",
            Album  = applyTo.Contains('L') ? pat : "",
        };
        var replaceBy = new RegexFields
        {
            Title  = applyTo.Contains('T') ? rep : "",
            Artist = applyTo.Contains('A') ? rep : "",
            Album  = applyTo.Contains('L') ? rep : "",
        };

        if (!append) pre.Regex = null;
        pre.Regex ??= [];
        pre.Regex.Add((toReplace, replaceBy));
    }

    private static PrintOption ParsePrintOption(string s, string flag) => s.ToLower().Trim() switch
    {
        "none"          => PrintOption.None,
        "tracks"        => PrintOption.Tracks,
        "results"       => PrintOption.Results,
        "tracks-full"   => PrintOption.Tracks | PrintOption.Full,
        "results-full"  => PrintOption.Results | PrintOption.Full,
        "link"          => PrintOption.Link,
        "json"          => PrintOption.Json,
        "json-all"      => PrintOption.Json | PrintOption.Full,
        "index"         => PrintOption.Index,
        "index-failed"  => PrintOption.Index | PrintOption.IndexFailed,
        _ => throw new Exception($"Input error: Invalid print option '{s}' for '{flag}'"),
    };

    private static object GetVarValue(string var, DownloadSettings settings, Job? job, CliSettings? cli = null)
    {
        static string toKebab(string s) =>
            string.Concat(s.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLower(c) : char.ToLower(c).ToString()));

        string mode = job != null
            ? toKebab(job.GetType().Name.Replace("Job", ""))
            : settings.Extraction.IsAlbum && settings.Search.IsAggregate ? "album-aggregate"
            : settings.Extraction.IsAlbum        ? "album"
            : settings.Search.IsAggregate   ? "aggregate"
            : "normal";

        return var switch
        {
            "input-type"    => settings.Extraction.InputType.ToString().ToLower(),
            "download-mode" => mode,
            "interactive"   => cli?.InteractiveMode ?? false,
            "album"         => settings.Extraction.IsAlbum,
            "aggregate"     => settings.Search.IsAggregate,
            _ => throw new Exception($"Input error: Unrecognized profile condition variable '{var}'"),
        };
    }

    private static string Next(IList<string> tokens, ref int i, string flag)
    {
        if (++i >= tokens.Count)
            throw new Exception($"Input error: Option '{flag}' requires a parameter");
        return tokens[i];
    }

    private static double ParseDouble(string s, string flag)
    {
        if (!double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
            throw new Exception($"Input error: Option '{flag}' requires a numeric parameter, got '{s}'");
        return v;
    }

    private static SkipMode ParseSkipMode(string s, string flag, bool allowIndex)
    {
        return s.ToLower().Trim() switch
        {
            "name"  => SkipMode.Name,
            "tag"   => SkipMode.Tag,
            "index" when allowIndex => SkipMode.Index,
            _ => throw new Exception($"Input error: Invalid skip mode '{s}' for '{flag}'"),
        };
    }

    private static int ParseInt(string s, string flag)
    {
        if (!int.TryParse(s.Replace("_", ""), out int v))
            throw new Exception($"Input error: Option '{flag}' requires an integer parameter, got '{s}'");
        return v;
    }

    /// Reads an optional bool literal from the next token; defaults to true (bare flag).
    private static bool ParseBoolFlag(IList<string> tokens, ref int i)
    {
        if (i + 1 < tokens.Count && tokens[i + 1] is "true" or "false" or "True" or "False")
            return bool.Parse(tokens[++i]);
        return true;
    }

    private static bool? ParseNullableBool(IList<string> tokens, ref int i)
        => ParseBoolFlag(tokens, ref i);

    private static int FindLastFlag(IReadOnlyList<string> args, params string[] names)
    {
        for (int i = args.Count - 1; i >= 0; i--)
            if (names.Contains(args[i])) return i;
        return -1;
    }

    private static bool IsExplicitFalse(IReadOnlyList<string> args, int idx)
        => idx + 1 < args.Count && args[idx + 1] == "false";

    [GeneratedRegex(@"(\s+|\(|\)|&&|\|\||==|!=|!|\"".*?\"")")]
    private static partial Regex CondTokenRegex();
}
