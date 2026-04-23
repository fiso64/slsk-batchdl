using Sldl.Core;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Sldl.Cli;

/// Owns config file loading and CLI token binding. Core owns typed profile application.
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

    public static string? ExtractProfileName(IReadOnlyList<string> args)
    {
        int idx = FindLastFlag(args, "--profile");
        return idx != -1 && idx + 1 < args.Count ? args[idx + 1] : null;
    }

    /// Creates fresh settings, applies the config file's [default] profile,
    /// any named profile, and finally cliArgs — in that order.
    /// Returns the top-level settings objects.
    public static (EngineSettings Engine, DownloadSettings Download, CliSettings Cli)
        Bind(ConfigFile file, IReadOnlyList<string> cliArgs, string? profileName = null)
    {
        var (engine, download, cli, _) = BindAll(file, cliArgs, profileName);
        return (engine, download, cli);
    }

    public static (EngineSettings Engine, DownloadSettings Download, CliSettings Cli, DaemonSettings Daemon)
        BindAll(ConfigFile file, IReadOnlyList<string> cliArgs, string? profileName = null)
    {
        var engine = new EngineSettings();
        var dl     = new DownloadSettings();
        var cli    = new CliSettings();
        var daemon = new DaemonSettings();
        profileName ??= ExtractProfileName(cliArgs);

        if (file.Profiles.TryGetValue("default", out var def))
            ApplyProfile(def, engine, dl, cli, daemon);

        foreach (var prof in GetNamedProfiles(file, profileName))
            ApplyProfile(prof, engine, dl, cli, daemon);

        ApplyTokens(NormalizeArgs(cliArgs), engine, dl, cli, daemon);

        PostProcess(engine, dl);

        return (engine, dl, cli, daemon);
    }

    public static IJobSettingsResolver CreateJobSettingsResolver(
        ConfigFile file,
        IReadOnlyList<string> cliArgs,
        CliSettings cli,
        string? profileName = null)
    {
        profileName ??= ExtractProfileName(cliArgs);
        var context = CreateProfileContext(cli);

        var catalog = CreateProfileCatalog(file);
        var namedProfiles = catalog.ResolveNamedProfiles(SplitProfileNames(profileName), msg => Logger.Warn(msg));

        var cliProfile = ParseTokensAsProfile("<cli>", NormalizeArgs(cliArgs)).Profile;

        return new ProfileJobSettingsResolver(
            new DownloadSettings(),
            catalog.DefaultProfile,
            catalog.AutoProfiles,
            namedProfiles,
            cliProfile,
            context,
            normalize: PostProcessDownload,
            warn: msg => Logger.Warn(msg));
    }

    public static ProfileCatalog CreateProfileCatalog(ConfigFile file)
    {
        var defaultProfile = file.Profiles.TryGetValue("default", out var def)
            ? ToSettingsProfile(def)
            : null;

        var autoProfiles = file.Profiles
            .Where(x => x.Key != "default" && x.Value.Condition != null)
            .Select(x => ToSettingsProfile(x.Value))
            .ToList();

        var namedProfiles = file.Profiles
            .Where(x => x.Key != "default")
            .Select(x => ToSettingsProfile(x.Value))
            .ToList();

        return new ProfileCatalog
        {
            DefaultProfile = defaultProfile,
            AutoProfiles = autoProfiles,
            NamedProfiles = namedProfiles,
        };
    }

    public static void ApplyAutoProfileCliSettings(ConfigFile file, DownloadSettings root, CliSettings cli, Job? job = null)
    {
        // Client settings can themselves affect profile context, e.g. one profile
        // enables interactive mode and another condition depends on interactive.
        // Resolve to a small fixed point so later matching sees those client-side values.
        const int maxPasses = 8;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            var before = (cli.InteractiveMode, cli.NoProgress, cli.ProgressJson);
            var context = CreateProfileContext(cli);

            foreach (var profile in file.Profiles
                         .Where(x => x.Key != "default" && x.Value.Condition != null)
                         .Select(x => ToProfileEntry(x.Value))
                         .Where(p => p.Condition != null && ProfileConditionEvaluator.Satisfied(p.Condition, root, job, context)))
            {
                profile.Cli.ApplyTo(cli);
            }

            var after = (cli.InteractiveMode, cli.NoProgress, cli.ProgressJson);
            if (after.Equals(before))
                return;
        }

        Logger.Warn("Warning: Client profile settings did not stabilize after repeated auto-profile passes");
    }

    public static IReadOnlyList<string> GetProfileNames(ConfigFile file)
        => file.Profiles.Keys.Where(k => k != "default").OrderBy(k => k).ToList();

    private static IEnumerable<string> SplitProfileNames(string? profileName)
        => string.IsNullOrWhiteSpace(profileName)
            ? []
            : profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static ProfileContext CreateProfileContext(CliSettings cli)
    {
        var context = new ProfileContext();
        context.Values["interactive"] = cli.InteractiveMode;
        context.Values["progress-json"] = cli.ProgressJson;
        context.Values["no-progress"] = cli.NoProgress;
        return context;
    }

    private static IEnumerable<ProfileEntry> GetNamedProfiles(ConfigFile file, string? profileName)
    {
        if (string.IsNullOrEmpty(profileName))
            yield break;

        foreach (var name in profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (name == "default") continue;
            if (file.Profiles.TryGetValue(name, out var prof))
                yield return prof;
            else
                Logger.Warn($"Warning: No profile '{name}' found in config");
        }
    }

    private static void ApplyProfile(ProfileEntry profile, EngineSettings engine, DownloadSettings dl, CliSettings cli, DaemonSettings daemon)
    {
        var effective = ToProfileEntry(profile);
        SettingsPatchApplier.Apply(effective.Profile, engine, dl);
        effective.Cli.ApplyTo(cli);
        effective.Daemon.ApplyTo(daemon);
    }

    private static SettingsProfile ToSettingsProfile(ProfileEntry profile)
        => ToProfileEntry(profile).Profile;

    private static ProfileEntry ToProfileEntry(ProfileEntry profile)
    {
        if (profile.Tokens.Count == 0)
            return profile;

        var parsed = ParseTokensAsProfile(profile.Profile.Name, profile.Tokens);
        return parsed with { Profile = parsed.Profile with { Condition = profile.Condition } };
    }

    // ── Token application ─────────────────────────────────────────────────────

    /// Maps one token list to a typed profile, then applies that profile.
    private static void ApplyTokens(
        IList<string> tokens,
        EngineSettings engine,
        DownloadSettings dl,
        CliSettings cli,
        DaemonSettings daemon)
    {
        ApplyProfile(ParseTokensAsProfile("<tokens>", tokens), engine, dl, cli, daemon);
    }

    private static ProfileEntry ParseTokensAsProfile(string name, IList<string> tokens)
    {
        var entry = new ProfileEntry(
            new SettingsProfile { Name = name },
            new CliSettingsPatch(),
            new DaemonSettingsPatch(),
            []);

        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];

            if (!t.StartsWith('-'))
            {
                AddProfileOption(entry, "--input", t);
                continue;
            }

            switch (t)
            {
                case "-c": case "--config": case "--profile":
                    i++;
                    break;
                case "--nc": case "--no-config":
                    if (i + 1 < tokens.Count && IsBoolLiteral(tokens[i + 1]))
                        i++;
                    break;
                default:
                    if (IsValuelessOption(t))
                    {
                        AddProfileOption(entry, t, "true");
                    }
                    else if (IsBoolOption(t))
                    {
                        string value = "true";
                        if (i + 1 < tokens.Count && IsBoolLiteral(tokens[i + 1]))
                            value = tokens[++i];
                        AddProfileOption(entry, t, value);
                    }
                    else
                    {
                        AddProfileOption(entry, t, Next(tokens, ref i, t));
                    }
                    break;
            }
        }

        return entry;
    }

    // ── Post-processing ───────────────────────────────────────────────────────

    private static void PostProcess(EngineSettings engine, DownloadSettings dl)
    {
        PostProcessDownload(dl);

        if (engine.LogFilePath != null)
            engine.LogFilePath = Utils.GetFullPath(Utils.ExpandVariables(engine.LogFilePath));
        if (engine.MockFilesDir != null)
            engine.MockFilesDir = Utils.GetFullPath(Utils.ExpandVariables(engine.MockFilesDir));
    }

    private static void PostProcessDownload(DownloadSettings dl)
    {
        NormalizeDownload(dl);

        if (string.IsNullOrWhiteSpace(dl.Output.ParentDir))
            dl.Output.ParentDir = Directory.GetCurrentDirectory();

        dl.Output.ParentDir = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.ParentDir));
        dl.Output.NameFormat = dl.Output.NameFormat.Trim();

        if (dl.Output.M3uFilePath != null)
            dl.Output.M3uFilePath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.M3uFilePath));
        if (dl.Output.IndexFilePath != null)
            dl.Output.IndexFilePath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.IndexFilePath));
        if (dl.Skip.SkipMusicDir != null)
            dl.Skip.SkipMusicDir = Utils.GetFullPath(Utils.ExpandVariables(dl.Skip.SkipMusicDir));

        if (dl.Output.FailedAlbumPath == null)
            dl.Output.FailedAlbumPath = Path.Join(dl.Output.ParentDir, "failed");
        else if (dl.Output.FailedAlbumPath is not ("disable" or "delete"))
            dl.Output.FailedAlbumPath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.FailedAlbumPath));
    }

    private static void NormalizeDownload(DownloadSettings dl) => SettingsNormalizer.Normalize(dl);

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
                profiles[curProfile] = new ProfileEntry(
                    new SettingsProfile { Name = curProfile },
                    new CliSettingsPatch(),
                    new DaemonSettingsPatch(),
                    []);

            if (key == "profile-cond")
            {
                if (curProfile != "default")
                {
                    profiles[curProfile] = profiles[curProfile] with
                    {
                        Profile = profiles[curProfile].Profile with { Condition = val }
                    };
                    hasAutoProfiles = true;
                }
            }
            else
            {
                string flag = key.Length == 1 ? $"-{key}" : $"--{key}";
                AddProfileOption(profiles[curProfile], flag, val);
            }
        }

        return new ConfigFile(path, profiles, hasAutoProfiles);
    }

    private static void AddProfileOption(ProfileEntry entry, string flag, string value)
    {
        var tr = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;

        void Engine(Action<EngineSettings> action) => entry.Profile.Engine.Add(action);
        void Download(Action<DownloadSettings> action) => entry.Profile.Download.Add(action);
        void Cli(Action<CliSettings> action) => entry.Cli.Add(action);
        void Daemon(Action<DaemonSettings> action) => entry.Daemon.Add(action);

        bool Bool() => bool.Parse(value);
        bool? NullableBool() => Bool();
        int Int() => ParseInt(value, flag);
        double Double() => ParseDouble(value, flag);

        switch (flag)
        {
            // ── Meta ─────────────────────────────────────────────────────────
            case "-c": case "--config":
            case "--nc": case "--no-config":
            case "--profile":
                break;

            // ── EngineSettings ───────────────────────────────────────────────
            case "--user": case "--username":
                Engine(e => e.Username = value); break;
            case "--pass": case "--password":
                Engine(e => e.Password = value); break;
            case "-l": case "--login":
                Engine(e =>
                {
                    var parts = value.Split(';', 2);
                    e.Username = parts[0];
                    e.Password = parts.Length > 1 ? parts[1] : "";
                });
                break;
            case "--rl": case "--random-login":
                Engine(e => e.UseRandomLogin = Bool()); break;
            case "--lp": case "--port": case "--listen-port":
                Engine(e => e.ListenPort = Int()); break;
            case "--no-listen":
                Engine(e => e.ListenPort = null); break;
            case "--cp": case "--concurrent-searches":
                Engine(e => e.ConcurrentSearches = Int()); break;
            case "--concurrent-extractors":
                Engine(e => e.ConcurrentExtractors = Int()); break;
            case "--spt": case "--searches-per-time":
                Engine(e => e.SearchesPerTime = Int()); break;
            case "--srt": case "--searches-renew-time":
                Engine(e => e.SearchRenewTime = Int()); break;
            case "--nmsc": case "--no-modify-share-count":
                Engine(e => e.NoModifyShareCount = Bool()); break;
            case "-v": case "--verbose": case "--debug":
                Engine(e => e.LogLevel = Logger.LogLevel.Debug); break;
            case "--lf": case "--log-file":
                Engine(e => e.LogFilePath = value); break;
            case "--cto": case "--connect-timeout":
                Engine(e => e.ConnectTimeout = Int()); break;
            case "--user-description":
                Engine(e => e.UserDescription = value); break;
            case "--shared-files":
                Engine(e => e.SharedFiles = Int()); break;
            case "--shared-folders":
                Engine(e => e.SharedFolders = Int()); break;
            case "--mock-files-dir":
                Engine(e => e.MockFilesDir = value); break;
            case "--mock-files-no-read-tags":
                Engine(e => e.MockFilesReadTags = false); break;
            case "--mock-files-slow":
                Engine(e => e.MockFilesSlow = Bool()); break;

            // ── CliSettings ──────────────────────────────────────────────────
            case "-t": case "--interactive":
                Cli(c => c.InteractiveMode = Bool()); break;
            case "--np": case "--no-progress":
                Cli(c => c.NoProgress = Bool()); break;
            case "--progress":
                Cli(c => c.NoProgress = !Bool()); break;
            case "--progress-json":
                Cli(c => c.ProgressJson = Bool()); break;
            case "--server-ip": case "--daemon-ip": case "--api-ip":
                Daemon(d => d.ListenIp = value); break;
            case "--server-port": case "--daemon-port": case "--api-port":
                Daemon(d => d.ListenPort = Int()); break;

            // ── OutputSettings ───────────────────────────────────────────────
            case "-p": case "--path": case "--parent":
                Download(d => d.Output.ParentDir = value); break;
            case "--nf": case "--name-format":
                Download(d => d.Output.NameFormat = value); break;
            case "--irs": case "--invalid-replace-str":
                Download(d => d.Output.InvalidReplaceStr = value); break;
            case "--wp": case "--write-playlist":
                Download(d => d.Output.WritePlaylist = Bool()); break;
            case "--nwp": case "--no-write-playlist":
                Download(d => d.Output.WritePlaylist = false); break;
            case "--pp": case "--playlist-path":
                Download(d => d.Output.M3uFilePath = value); break;
            case "--wi": case "--write-index":
                Download(d => { d.Output.WriteIndex = Bool(); d.Output.HasConfiguredIndex = true; }); break;
            case "--nwi": case "--no-write-index":
                Download(d => { d.Output.WriteIndex = false; d.Output.HasConfiguredIndex = true; }); break;
            case "--ip": case "--index-path":
                Download(d => { d.Output.IndexFilePath = value; d.Output.HasConfiguredIndex = true; }); break;
            case "--failed-album-path":
                Download(d => d.Output.FailedAlbumPath = value); break;
            case "--oc": case "--on-complete":
                Download(d =>
                {
                    if (value.TrimStart().StartsWith("+ "))
                    {
                        d.Output.OnComplete ??= [];
                        d.Output.OnComplete.Add(value.TrimStart()[2..]);
                    }
                    else
                    {
                        d.Output.OnComplete = [value];
                    }
                });
                break;
            case "--print":
                Download(d => d.PrintOption = ParsePrintOption(value, flag)); break;
            case "--pt": case "--print-tracks":
                Download(d => d.PrintOption = PrintOption.Tracks); break;
            case "--ptf": case "--print-tracks-full":
                Download(d => d.PrintOption = PrintOption.Tracks | PrintOption.Full); break;
            case "--pr": case "--print-results":
                Download(d => d.PrintOption = PrintOption.Results); break;
            case "--prf": case "--print-results-full":
                Download(d => d.PrintOption = PrintOption.Results | PrintOption.Full); break;
            case "--pl": case "--print-link":
                Download(d => d.PrintOption = PrintOption.Link); break;
            case "--pj": case "--print-json":
                Download(d => d.PrintOption = PrintOption.Json); break;
            case "--pjf": case "--print-json-full":
                Download(d => d.PrintOption = PrintOption.Json | PrintOption.Full); break;

            // ── Extraction / album settings ──────────────────────────────────
            case "-i": case "--input":
                Download(d =>
                {
                    if (d.Extraction.Input != null)
                        throw new Exception($"Input error: Invalid argument '{value}'. Input is already set to '{d.Extraction.Input}'");
                    d.Extraction.Input = value;
                });
                break;
            case "--it": case "--input-type":
                Download(d => d.Extraction.InputType = Enum.Parse<InputType>(value.Replace("-", ""), ignoreCase: true)); break;
            case "-n": case "--number":
                Download(d => d.Extraction.MaxTracks = Int()); break;
            case "-o": case "--offset":
                Download(d => d.Extraction.Offset = Int()); break;
            case "-r": case "--reverse":
                Download(d => d.Extraction.Reverse = Bool()); break;
            case "--gd": case "--get-deleted":
                Download(d => d.YouTube.GetDeleted = Bool()); break;
            case "--do": case "--deleted-only":
                Download(d => d.YouTube.DeletedOnly = Bool()); break;
            case "--rfp": case "--rfs": case "--remove-from-source": case "--remove-from-playlist":
                Download(d => d.Extraction.RemoveTracksFromSource = Bool()); break;
            case "--msa": case "--min-shares-aggregate":
                Download(d => d.Search.MinSharesAggregate = Int()); break;
            case "--alt": case "--aggregate-length-tol":
                Download(d => d.Search.AggregateLengthTol = Int()); break;
            case "-a": case "--album":
                Download(d => d.Extraction.IsAlbum = Bool()); break;
            case "-g": case "--aggregate":
                Download(d => d.Search.IsAggregate = Bool()); break;
            case "--aa": case "--album-art":
                Download(d => d.Output.AlbumArtOption = value.ToLower().Trim() switch
                {
                    "default" => AlbumArtOption.Default,
                    "largest" => AlbumArtOption.Largest,
                    "most" => AlbumArtOption.Most,
                    var s => throw new Exception($"Input error: Invalid album art option '{s}'"),
                });
                break;
            case "--aao": case "--aa-only": case "--album-art-only":
                Download(d =>
                {
                    d.Output.AlbumArtOnly = Bool();
                    if (d.Output.AlbumArtOnly)
                    {
                        if (d.Output.AlbumArtOption == AlbumArtOption.Default)
                            d.Output.AlbumArtOption = AlbumArtOption.Largest;
                        d.Search.PreferredCond = new FileConditions();
                        d.Search.NecessaryCond = new FileConditions();
                    }
                });
                break;
            case "--matc": case "--min-album-track-count":
                Download(d => d.Search.NecessaryFolderCond.MinTrackCount = Int()); break;
            case "--Matc": case "--max-album-track-count":
                Download(d => d.Search.NecessaryFolderCond.MaxTrackCount = Int()); break;
            case "--eMtc": case "--extract-max-track-count":
                Download(d => d.Extraction.SetAlbumMaxTrackCount = Bool()); break;
            case "--emtc": case "--extract-min-track-count":
                Download(d => d.Extraction.SetAlbumMinTrackCount = Bool()); break;
            case "--album-track-count-max-retries":
                Download(d => d.Transfer.AlbumTrackCountMaxRetries = Int()); break;
            case "--atc": case "--album-track-count":
                Download(d =>
                {
                    if (value == "-1")
                        d.Search.NecessaryFolderCond.MinTrackCount = d.Search.NecessaryFolderCond.MaxTrackCount = -1;
                    else if (value.EndsWith('-'))
                        d.Search.NecessaryFolderCond.MaxTrackCount = ParseInt(value[..^1], flag);
                    else if (value.EndsWith('+'))
                        d.Search.NecessaryFolderCond.MinTrackCount = ParseInt(value[..^1], flag);
                    else
                        d.Search.NecessaryFolderCond.MinTrackCount = d.Search.NecessaryFolderCond.MaxTrackCount = Int();
                });
                break;

            // ── Preprocess / search settings ─────────────────────────────────
            case "--rft": case "--remove-ft":
                Download(d => d.Preprocess.RemoveFt = Bool()); break;
            case "--rb": case "--remove-brackets":
                Download(d => d.Preprocess.RemoveBrackets = Bool()); break;
            case "--amw": case "--artist-maybe-wrong":
                Download(d => d.Search.ArtistMaybeWrong = Bool()); break;
            case "--ea": case "--extract-artist":
                Download(d => d.Preprocess.ExtractArtist = Bool()); break;
            case "--parse-title":
                Download(d => d.Preprocess.ParseTitleTemplate = value); break;
            case "--re": case "--regex":
                Download(d => ApplyRegex(value, d.Preprocess)); break;
            case "--st": case "--search-time": case "--search-timeout":
                Download(d => d.Search.SearchTimeout = Int()); break;
            case "--Mst": case "--stale-time": case "--max-stale-time":
                Download(d => d.Search.MaxStaleTime = Int()); break;
            case "--Mr": case "--retries": case "--max-retries":
                Download(d => d.Transfer.MaxRetriesPerTrack = Int()); break;
            case "--uer": case "--unknown-error-retries":
                Download(d => d.Transfer.UnknownErrorRetries = Int()); break;
            case "--fs": case "--fast-search":
                Download(d => d.Search.FastSearch = Bool()); break;
            case "--fsd": case "--fast-search-delay":
                Download(d => d.Search.FastSearchDelay = Int()); break;
            case "--fsmus": case "--fast-search-min-up-speed":
                Download(d => d.Search.FastSearchMinUpSpeed = Double()); break;
            case "-d": case "--desperate":
                Download(d => d.Search.DesperateSearch = Bool()); break;
            case "--nrsc": case "--no-remove-special-chars":
                Download(d => d.Search.NoRemoveSpecialChars = Bool()); break;
            case "--rsc": case "--remove-special-chars":
                Download(d => d.Search.NoRemoveSpecialChars = false); break;
            case "--nbf": case "--no-browse-folder":
                Download(d => d.Search.NoBrowseFolder = true); break;
            case "--bf": case "--browse-folder":
                Download(d => d.Search.NoBrowseFolder = false); break;
            case "--nie": case "--no-incomplete-ext":
                Download(d => d.Transfer.NoIncompleteExt = Bool()); break;
            case "--rf": case "--relax": case "--relax-filtering":
                Download(d => d.Search.Relax = Bool()); break;
            case "--ftd": case "--fails-to-downrank":
                Download(d => d.Search.DownrankOn = -Int()); break;
            case "--fti": case "--fails-to-ignore":
                Download(d => d.Search.IgnoreOn = -Int()); break;

            // ── Necessary condition shorthands ───────────────────────────────
            case "--af": case "--format":
                Download(d => d.Search.NecessaryCond.Formats = [.. value.ToLower().Split(',', tr).Select(x => x.TrimStart('.'))]); break;
            case "--lt": case "--tolerance": case "--length-tol": case "--length-tolerance":
                Download(d => d.Search.NecessaryCond.LengthTolerance = Int()); break;
            case "--mbr": case "--min-bitrate":
                Download(d => d.Search.NecessaryCond.MinBitrate = Int()); break;
            case "--Mbr": case "--max-bitrate":
                Download(d => d.Search.NecessaryCond.MaxBitrate = Int()); break;
            case "--msr": case "--min-samplerate":
                Download(d => d.Search.NecessaryCond.MinSampleRate = Int()); break;
            case "--Msr": case "--max-samplerate":
                Download(d => d.Search.NecessaryCond.MaxSampleRate = Int()); break;
            case "--mbd": case "--min-bitdepth":
                Download(d => d.Search.NecessaryCond.MinBitDepth = Int()); break;
            case "--Mbd": case "--max-bitdepth":
                Download(d => d.Search.NecessaryCond.MaxBitDepth = Int()); break;
            case "--stt": case "--strict-title":
                Download(d => d.Search.NecessaryCond.StrictTitle = NullableBool()); break;
            case "--sar": case "--strict-artist":
                Download(d => d.Search.NecessaryCond.StrictArtist = NullableBool()); break;
            case "--sal": case "--strict-album":
                Download(d => d.Search.NecessaryCond.StrictAlbum = NullableBool()); break;
            case "--anl": case "--accept-no-length":
                Download(d => d.Search.NecessaryCond.AcceptNoLength = NullableBool()); break;
            case "--bu": case "--banned-users":
                Download(d => d.Search.NecessaryCond.BannedUsers = value.Split(',', tr)); break;
            case "--sc": case "--strict": case "--strict-conditions":
                Download(d =>
                {
                    bool val = Bool();
                    d.Search.NecessaryCond.AcceptMissingProps = !val;
                    d.Search.PreferredCond.AcceptMissingProps = !val;
                });
                break;
            case "--cond": case "--conditions":
                Download(d =>
                {
                    var fc = new FolderConditions();
                    d.Search.NecessaryCond.AddConditions(ConditionParser.ParseFileConditions(value, fc));
                    d.Search.NecessaryFolderCond.AddConditions(fc);
                });
                break;

            // ── Preferred condition shorthands ───────────────────────────────
            case "--paf": case "--pf": case "--pref-format":
                Download(d => d.Search.PreferredCond.Formats = [.. value.ToLower().Split(',', tr).Select(x => x.TrimStart('.'))]); break;
            case "--plt": case "--pref-tolerance": case "--pref-length-tol": case "--pref-length-tolerance":
                Download(d => d.Search.PreferredCond.LengthTolerance = Int()); break;
            case "--pmbr": case "--pref-min-bitrate":
                Download(d => d.Search.PreferredCond.MinBitrate = Int()); break;
            case "--pMbr": case "--pref-max-bitrate":
                Download(d => d.Search.PreferredCond.MaxBitrate = Int()); break;
            case "--pmsr": case "--pref-min-samplerate":
                Download(d => d.Search.PreferredCond.MinSampleRate = Int()); break;
            case "--pMsr": case "--pref-max-samplerate":
                Download(d => d.Search.PreferredCond.MaxSampleRate = Int()); break;
            case "--pmbd": case "--pref-min-bitdepth":
                Download(d => d.Search.PreferredCond.MinBitDepth = Int()); break;
            case "--pMbd": case "--pref-max-bitdepth":
                Download(d => d.Search.PreferredCond.MaxBitDepth = Int()); break;
            case "--pst": case "--pstt": case "--pref-strict-title":
                Download(d => d.Search.PreferredCond.StrictTitle = NullableBool()); break;
            case "--psar": case "--pref-strict-artist":
                Download(d => d.Search.PreferredCond.StrictArtist = NullableBool()); break;
            case "--psal": case "--pref-strict-album":
                Download(d => d.Search.PreferredCond.StrictAlbum = NullableBool()); break;
            case "--panl": case "--pref-accept-no-length":
                Download(d => d.Search.PreferredCond.AcceptNoLength = NullableBool()); break;
            case "--pbu": case "--pref-banned-users":
                Download(d => d.Search.PreferredCond.BannedUsers = value.Split(',', tr)); break;
            case "--pc": case "--pref": case "--preferred-conditions":
                Download(d =>
                {
                    var fc = new FolderConditions();
                    d.Search.PreferredCond.AddConditions(ConditionParser.ParseFileConditions(value, fc));
                    d.Search.PreferredFolderCond.AddConditions(fc);
                });
                break;

            // ── Skip and provider settings ───────────────────────────────────
            case "--se": case "--skip-existing":
                Download(d => d.Skip.SkipExisting = Bool()); break;
            case "--nse": case "--no-skip-existing":
                Download(d => d.Skip.SkipExisting = false); break;
            case "--snf": case "--skip-not-found":
                Download(d => d.Skip.SkipNotFound = Bool()); break;
            case "--smd": case "--skip-music-dir":
                Download(d => d.Skip.SkipMusicDir = value); break;
            case "--smod": case "--skip-mode-output-dir":
                Download(d => d.Skip.SkipMode = ParseSkipMode(value, flag, allowIndex: true)); break;
            case "--smmd": case "--skip-mode-music-dir":
                Download(d => d.Skip.SkipModeMusicDir = ParseSkipMode(value, flag, allowIndex: false)); break;
            case "--scc": case "--skip-check-cond":
                Download(d => d.Skip.SkipCheckCond = Bool()); break;
            case "--scpc": case "--skip-check-pref-cond":
                Download(d => d.Skip.SkipCheckPrefCond = Bool()); break;
            case "--si": case "--spotify-id":
                Download(d => d.Spotify.ClientId = value); break;
            case "--ss": case "--spotify-secret":
                Download(d => d.Spotify.ClientSecret = value); break;
            case "--stk": case "--spotify-token":
                Download(d => d.Spotify.Token = value); break;
            case "--str": case "--spotify-refresh":
                Download(d => d.Spotify.Refresh = value); break;
            case "--yk": case "--youtube-key":
                Download(d => d.YouTube.ApiKey = value); break;
            case "--yp": case "--yt-parse":
                Download(d => d.Csv.YtParse = Bool()); break;
            case "--yd": case "--yt-dlp":
                Download(d => d.YtDlp.UseYtdlp = Bool()); break;
            case "--yda": case "--yt-dlp-argument":
                Download(d => d.YtDlp.YtdlpArgument = value); break;
            case "--ac": case "--artist-col":
                Download(d => d.Csv.ArtistCol = value); break;
            case "--tc": case "--track-col": case "--title-col":
                Download(d => d.Csv.TitleCol = value); break;
            case "--alc": case "--album-col":
                Download(d => d.Csv.AlbumCol = value); break;
            case "--ydc": case "--yt-desc-col":
                Download(d => d.Csv.DescCol = value); break;
            case "--atcc": case "--album-track-count-col":
                Download(d => d.Csv.TrackCountCol = value); break;
            case "--yic": case "--yt-id-col":
                Download(d => d.Csv.YtIdCol = value); break;
            case "--lc": case "--length-col":
                Download(d => d.Csv.LengthCol = value); break;
            case "--tf": case "--time-format":
                Download(d => d.Csv.TimeUnit = value); break;
            case "--from-html":
                Download(d => d.Bandcamp.HtmlFromFile = value); break;

            default:
                throw new Exception($"Input error: Unknown argument: {flag}");
        }
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

    private static bool IsBoolLiteral(string value) =>
        value is "true" or "false" or "True" or "False";

    private static bool IsValuelessOption(string flag) => flag switch
    {
        "--no-listen"
        or "-v" or "--verbose" or "--debug"
        or "--mock-files-no-read-tags"
        or "--np" or "--no-progress"
        or "--progress"
        or "--nwp" or "--no-write-playlist"
        or "--nwi" or "--no-write-index"
        or "--pt" or "--print-tracks"
        or "--ptf" or "--print-tracks-full"
        or "--pr" or "--print-results"
        or "--prf" or "--print-results-full"
        or "--pl" or "--print-link"
        or "--pj" or "--print-json"
        or "--pjf" or "--print-json-full"
        or "--rsc" or "--remove-special-chars"
        or "--nbf" or "--no-browse-folder"
        or "--bf" or "--browse-folder"
        or "--nse" or "--no-skip-existing" => true,
        _ => false,
    };

    private static bool IsBoolOption(string flag) => flag switch
    {
        "--rl" or "--random-login"
        or "--nmsc" or "--no-modify-share-count"
        or "--mock-files-slow"
        or "-t" or "--interactive"
        or "--progress-json"
        or "--wp" or "--write-playlist"
        or "--wi" or "--write-index"
        or "-r" or "--reverse"
        or "--gd" or "--get-deleted"
        or "--do" or "--deleted-only"
        or "--rfp" or "--rfs" or "--remove-from-source" or "--remove-from-playlist"
        or "-a" or "--album"
        or "-g" or "--aggregate"
        or "--aao" or "--aa-only" or "--album-art-only"
        or "--eMtc" or "--extract-max-track-count"
        or "--emtc" or "--extract-min-track-count"
        or "--rft" or "--remove-ft"
        or "--rb" or "--remove-brackets"
        or "--amw" or "--artist-maybe-wrong"
        or "--ea" or "--extract-artist"
        or "--fs" or "--fast-search"
        or "-d" or "--desperate"
        or "--nrsc" or "--no-remove-special-chars"
        or "--nie" or "--no-incomplete-ext"
        or "--rf" or "--relax" or "--relax-filtering"
        or "--stt" or "--strict-title"
        or "--sar" or "--strict-artist"
        or "--sal" or "--strict-album"
        or "--anl" or "--accept-no-length"
        or "--sc" or "--strict" or "--strict-conditions"
        or "--pst" or "--pstt" or "--pref-strict-title"
        or "--psar" or "--pref-strict-artist"
        or "--psal" or "--pref-strict-album"
        or "--panl" or "--pref-accept-no-length"
        or "--se" or "--skip-existing"
        or "--snf" or "--skip-not-found"
        or "--scc" or "--skip-check-cond"
        or "--scpc" or "--skip-check-pref-cond"
        or "--yp" or "--yt-parse"
        or "--yd" or "--yt-dlp" => true,
        _ => false,
    };

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

    private static int FindLastFlag(IReadOnlyList<string> args, params string[] names)
    {
        for (int i = args.Count - 1; i >= 0; i--)
            if (names.Contains(args[i])) return i;
        return -1;
    }

    private static bool IsExplicitFalse(IReadOnlyList<string> args, int idx)
        => idx + 1 < args.Count && args[idx + 1] == "false";

}
