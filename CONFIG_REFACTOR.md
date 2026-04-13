# Plan: Config Refactor

## Motivation

`Config.cs` is currently ~1600 lines and does everything:

- **Field storage** — 130+ public fields, all mutable, all strung together in one class
- **CLI argument parsing** — an 800-line `switch/case` in `ProcessArgs()`
- **Config file parsing** — `ParseConfig()` reads `.conf` files and converts them to arg lists
- **Profile management** — `configProfiles`, `appliedProfiles`, `UpdateProfiles()`, `ProfileConditionSatisfied()`, expression parser
- **Condition parsing** — `ParseConditions()` is a 110-line switch/case that belongs nowhere near Config
- **Post-processing** — `PostProcessArgs()` expands variables, resolves defaults, sets derived fields
- **Computed properties** — `DoNotDownload`, `NeedLogin`, `DeleteAlbumOnFail`, etc.
- **Error reporting** — `InputError()` / `InputWarning()` live here for no good reason
- **`Copy()`** — required because everything is mutable and `JobPreparer` needs per-job snapshots

Consequences:
- Adding a new option means editing the switch/case in `ProcessArgs`, the field list, `Copy()`,
  and possibly `PostProcessArgs`. No single source of truth.
- GUI consumers cannot construct a `Config` programmatically without faking a `string[]` of CLI args.
  The old GUI was literally forced to build an argv string and pass it to the engine.
- Untestable in isolation — the constructor parses files, validates input, and throws on errors.
- `defaultConfig` is engine-level but carries per-submission fields. The two concerns are fused.

---

## Design Principle: Library vs. Frontend Boundary

**Settings objects are pure library data.** They have no knowledge of CLI flags, option names,
or how they were populated. The CLI and GUI are both consumers of the library; they know how to
translate user input into settings, but the settings themselves don't know where they came from.

- **Library (`sldl.core`):** `EngineSettings`, `DownloadSettings`, `CliSettings`, `ConfigManager`,
  `DownloadEngine`, `IProgressReporter` — everything a frontend needs to drive downloads.
- **CLI frontend (`sldl`):** thin layer — reads `argv`, calls `ConfigManager.BindCli()`, wires up
  `CliSettings`, runs `engine.RunAsync()`. Can optionally delegate to a running server via `--url`.
- **GUI/server frontend:** ASP.NET Core process, imports `sldl.core`, hosts `DownloadEngine` and
  exposes an HTTP API + web UI. The CLI attaches to this when `--url` is set.
- **Engine interfaces (`IProgressReporter`, `SelectAlbumVersion` callback):** the engine's only UI surface.

`ConfigManager` belongs in the library — config file loading, profile management, and
auto-profile evaluation are needed by all frontends. `CliSettings` also lives in the library
(GUI frontends simply ignore it).

Corollary: `[Option]` / `[Positional]` attributes on settings classes are a violation of this
principle — they couple library types to frontend concerns. Settings classes carry no such annotations.

---

## Goals

1. **Decouple CLI parsing from configuration data.** Settings classes are pure data; the binding
   logic lives in `ConfigManager` (CLI layer).
2. **Separate engine-level from per-submission settings.** This enables clean dynamic job
   enqueueing from a GUI without faking argv.
3. **Explicit binding table.** Option-to-field mappings live in one place (`ConfigManager.ApplyTokens`),
   not scattered across attributes on settings classes. Every flag and its transformation is
   readable in a single switch/case. Adding an option means one edit here and one on the settings class.
4. **Extract profile management** into a `ConfigManager` that owns config file loading, profile
   merging, and profile condition evaluation. Config classes have no knowledge of profiles.
5. **Extract `ParseConditions`** into `ConditionParser` — a standalone static utility.
6. **Null strings throughout.** Replace `""` defaults with `null`. `null` = not set,
   `""` = explicitly set to empty (almost never valid).
7. **Enable the channel-based engine API.** After this refactor, `DownloadEngine` takes
   `EngineSettings` at construction and accepts `(ExtractJob, DownloadSettings)` submissions
   at any time — no more `defaultConfig` field, no more argv injection from the GUI.

---

## Proposed structure

### `EngineSettings`

Stable for the lifetime of the engine. Set once at construction. No CLI annotations.

```csharp
public class EngineSettings
{
    public string?           Username             { get; set; }
    public string?           Password             { get; set; }
    public bool              UseRandomLogin       { get; set; }
    public int?              ListenPort           { get; set; } = 49998;
    public int               ConnectTimeout       { get; set; } = 20_000;
    public int               ConcurrentSearches   { get; set; } = 2;
    public int               ConcurrentExtractors { get; set; } = 2;
    public int               SearchesPerTime      { get; set; } = 34;
    public int               SearchRenewTime      { get; set; } = 220;
    public int               SharedFiles          { get; set; }
    public int               SharedFolders        { get; set; }
    public string?           UserDescription      { get; set; }
    public bool              NoModifyShareCount   { get; set; }
    public Logger.LogLevel   LogLevel             { get; set; } = Logger.LogLevel.Info;
    public string?           LogFilePath          { get; set; }
    public string?           MockFilesDir         { get; set; }
    public bool              MockFilesReadTags    { get; set; } = true;
    public bool              MockFilesSlow        { get; set; }
}
```

### `DownloadSettings`

Per-submission. Passed to `engine.EnqueueAsync(job, settings)`. Composed of domain-oriented
sub-objects so that services receive only what they need.

```
DownloadSettings
├── [top-level]           — PrintOption, IsAggregate (mode flags consumed by the engine)
├── OutputSettings        — ParentDir, NameFormat, InvalidReplaceStr, WritePlaylist,
│                           WriteIndex, IndexFilePath, M3uFilePath, FailedAlbumPath,
│                           OnComplete
├── SearchSettings        — NecessaryCond, PreferredCond, NecessaryFolderCond,
│                           PreferredFolderCond, SearchTimeout, MaxStaleTime,
│                           IsAggregate, MinSharesAggregate, AggregateLengthTol,
│                           ArtistMaybeWrong, DownrankOn, IgnoreOn,
│                           FastSearch, FastSearchDelay, FastSearchMinUpSpeed,
│                           DesperateSearch, NoRemoveSpecialChars,
│                           RemoveSingleCharTerms, NoBrowseFolder, Relax
├── SkipSettings          — SkipExisting, SkipNotFound, SkipMode, SkipMusicDir,
│                           SkipModeMusicDir, SkipCheckCond, SkipCheckPrefCond
├── PreprocessSettings    — RemoveFt, RemoveBrackets, ExtractArtist,
│                           ParseTitleTemplate, Regex
├── AlbumSettings         — IsAlbum, AlbumArtOnly, AlbumArtOption,
│                           MinAlbumTrackCount, MaxAlbumTrackCount,
│                           SetAlbumMinTrackCount, SetAlbumMaxTrackCount,
│                           AlbumTrackCountMaxRetries
├── ExtractionSettings    — Input, InputType, MaxTracks, Offset, Reverse,
│                           RemoveTracksFromSource
├── TransferSettings      — MaxRetriesPerTrack, UnknownErrorRetries, NoIncompleteExt
├── SpotifySettings       — ClientId, ClientSecret, Token, Refresh
├── YouTubeSettings       — ApiKey, YtParse, GetDeleted, DeletedOnly
├── YtDlpSettings         — UseYtdlp, YtdlpArgument
├── CsvSettings           — ArtistCol, AlbumCol, TitleCol, YtIdCol, DescCol,
│                           TrackCountCol, LengthCol, TimeUnit
└── BandcampSettings      — HtmlFromFile
```

`Job.Config` holds one `DownloadSettings`. Callees receive the sub-object they care about:

```csharp
Preprocessor.PreprocessSong(song, job.Config.Preprocess);
FileManager.GetDownloadPath(candidate, job.Config.Output);
ResultSorter.Sort(candidates, job.Config.Search);
TrackSkipper.Check(song, job.Config.Skip);
```

Computed properties that span sub-objects (`NeedLogin`, `DoNotDownload`, `PrintTracks`, etc.)
live on `DownloadSettings` itself.

### `CliSettings`

Frontend-only presentation settings. Lives in the library so all frontends can consume it;
GUI frontends simply ignore the fields they don't need.

```csharp
public class CliSettings
{
    public bool InteractiveMode { get; set; }  // wire up SelectAlbumVersion callback
    public bool NoProgress      { get; set; }  // use NullProgressReporter
    public bool ProgressJson    { get; set; }  // use JsonProgressReporter
}
```

---

## Token binding: `ConfigManager.ApplyTokens`

The binding table lives in `ConfigManager.ApplyTokens` — a single explicit method that maps
token streams to settings mutations. This is the old `Config.ProcessArgs` switch/case, living
in the right place.

```csharp
// Excerpt — the full table covers all ~100 flags
private static void ApplyTokens(IList<string> tokens, EngineSettings eng,
    DownloadSettings dl, CliSettings cli)
{
    for (int i = 0; i < tokens.Count; i++)
    {
        switch (tokens[i])
        {
            case "--username": case "--user":
                eng.Username = Next(tokens, ref i); break;

            case "--format": case "--af":
                dl.Search.NecessaryCond.Formats = NextArray(tokens, ref i); break;

            case "--pref-format": case "--pf":
                dl.Search.PreferredCond.Formats = NextArray(tokens, ref i); break;

            case "--fails-to-downrank": case "--ftd":
                dl.Search.DownrankOn = -NextInt(tokens, ref i); break;

            case "--cond": case "--conditions":
                var fc = new FolderConditions();
                dl.Search.NecessaryCond.AddConditions(ConditionParser.ParseFileConditions(Next(tokens, ref i), fc));
                dl.Search.NecessaryFolderCond.AddConditions(fc);
                break;

            // ... all other flags
        }
    }
}
```

**No reflection. No attributes on settings classes.** Every flag and its transformation is
visible in one place. The "cost" vs. the attribute approach is one extra line when adding a new
option (the switch case), which is a fair trade for having settings classes that are clean
library objects, and for eliminating the problems that arise when library types (like
`FileConditions`) need CLI annotations.

**Config file format stays the same.** `ParseConfigFile()` converts `key = val` lines to
`--key val` token pairs, which flow through the same `ApplyTokens`. Profile sections are
labelled groups of those tokens.

**[DEFERRED]**: Help text and option reference generation. Currently sourced from `README.md`
via `slsk-batchdl.HelpGenerator`. With the explicit binding table, help strings could be
registered alongside each case (e.g. a parallel help dictionary), or kept in the README.
Not a blocker for the refactor.

---

## `ConfigManager`

Owns config file loading, token application, profile merging, and profile condition evaluation.
The binding table (`ApplyTokens`) lives here — it is the CLI layer's knowledge of how flags
map to settings fields.

```csharp
public static class ConfigManager
{
    // Discovers and parses the config file.
    public static ConfigFile Load(string? explicitPath = null);

    // Creates fresh settings, applies default profile + named profile + cliArgs in order.
    public static (EngineSettings Engine, DownloadSettings Download, CliSettings Cli)
        Bind(ConfigFile file, IReadOnlyList<string> cliArgs, string? profileName = null);

    // Re-evaluates auto-profiles against the given job.
    // Reconstructs from scratch (default → auto-profiles → named → CLI) if anything changed.
    public static DownloadSettings UpdateProfiles(
        DownloadSettings current, ConfigFile file, IReadOnlyList<string> cliArgs,
        string? profileName, Job job);

    // Evaluates a profile-cond expression against variables derived from the job and settings.
    public static bool ProfileConditionSatisfied(string cond, DownloadSettings settings, Job? job);
}
```

**Token merging model:** All config sources reduce to ordered token lists. Later tokens win
for scalar fields; appendable fields (`--on-complete + ...`, `--regex + ...`) accumulate.
Profile replay: defaults → auto-profiles → named profile → CLI args.

**`ConfigFile`** is a plain data record:
```csharp
public record ConfigFile(string Path, Dictionary<string, ProfileEntry> Profiles, bool HasAutoProfiles);
public record ProfileEntry(List<string> Tokens, string? Condition);
```

---

## `ConditionParser`

Standalone static utility for parsing composite condition strings.

```csharp
// Services/ConditionParser.cs
public static class ConditionParser
{
    public static FileConditions   ParseFileConditions(string input, FolderConditions? folderOut = null);
    public static FolderConditions ParseFolderConditions(string input);
}
```

---

## `Job.Config` and `JobPreparer`

`Job.Config` currently holds a `Config` instance. After the refactor it holds `DownloadSettings`.
`JobPreparer.PrepareJob` currently does `job.Config = parentConfig.Copy()`. With mutable settings,
sharing a reference isn't safe — but since `ConfigManager.UpdateProfiles` rebuilds from scratch
when profiles change, and unchanged jobs receive the same reference as their parent, `Copy()` can
be replaced by passing the same reference, with a fresh `DownloadSettings` created only when
`UpdateProfiles` detects a profile change.

`EngineSettings` is not on `Job` at all — it belongs to `DownloadEngine` only.

---

## Enabling the channel-based engine API

After this refactor:

```csharp
// Construction — engine-level settings only
new DownloadEngine(engineSettings, clientManager, progressReporter);

// Submit a job at any time, with its own per-submission settings
engine.EnqueueAsync(new ExtractJob(url), downloadSettings);
```

`RunAsync` loops over a `Channel<(ExtractJob, DownloadSettings)>`. The CLI writes one item and
completes the writer. A server frontend writes items as they arrive over HTTP.

---

## Null string migration

**Why:** `null` = not set. `""` = explicitly set to empty (almost never a valid case for config
strings). Current code uses `""` as "not set", leading to defensive `Length > 0` and `!= ""`
checks everywhere. After migration, `== null` / `!= null` is the idiom.

**Risk:** `== ""` and `.Length == 0` checks will not become compile errors when the field type
changes to `string?`. They will silently evaluate to `false` for `null` values.

**Mitigation — before changing any field type, run these greps and treat output as a fix-list:**

```
== ""
!= ""
\.Length == 0
\.Length > 0
string\.IsNullOrEmpty    ← already correct, no change needed
string\.IsNullOrWhitespace  ← already correct
```

---

## What disappears

| Current | Replaced by |
|---------|-------------|
| `Config` class (1600 lines) | `EngineSettings` + `DownloadSettings` + sub-objects |
| `Config(string[] args)` constructor | `ConfigManager.Bind(file, args)` |
| `Config.Copy()` | Pass same reference; `ConfigManager.UpdateProfiles` rebuilds when needed |
| `Config.ProcessArgs()` (800-line switch) | `ConfigManager.ApplyTokens` — same pattern, right place |
| `Config.ParseConfig()` | `ConfigManager.Load()` |
| `Config.UpdateProfiles()` / `ApplyProfiles()` | `ConfigManager.UpdateProfiles()` |
| `Config.ProfileConditionSatisfied()` + expression parser | `ConfigManager.ProfileConditionSatisfied()` |
| `Config.ParseConditions()` | `ConditionParser.ParseFileConditions()` |
| `Config.PostProcessArgs()` | Path expansion + constraint enforcement in `ConfigManager.PostProcess()` |
| `Config.InputError()` / `InputWarning()` | Throw at call site; `Logger.Warn` for warnings |
| `DownloadEngine.defaultConfig` | `EngineSettings` at construction; `DownloadSettings` per submission |
| `[Option]` / `[Positional]` attributes on settings | Removed — settings are clean library objects |
| `OptionAttribute.cs`, `CommandLineBinder.cs` | Removed — binding is explicit in `ApplyTokens` |

---

## Implementation order

| Step | Description |
|------|-------------|
| ~~1~~ | ~~Define `EngineSettings`, `DownloadSettings`, sub-config classes as data-only. No logic yet.~~ ✅ |
| ~~2~~ | ~~Run null-migration grep checklist. Fix all `== ""`/`.Length` patterns.~~ ✅ |
| ~~3~~ | ~~Write `ConditionParser`. Replace `Config.ParseConditions` calls.~~ ✅ |
| ~~4~~ | ~~**Clean up attribute experiment.** Remove `[Option]`/`[Positional]` attributes from all settings classes. Delete `OptionAttribute.cs` and `CommandLineBinder.cs`. Rewrite `ConfigManager.ApplyTokens` as a clean explicit switch/case covering all flags. Remove the 12 binder tests that tested the now-deleted infrastructure.~~ ✅ |
| ~~5~~ | ~~`ConfigManager` is already written. Verify `ApplyTokens` is complete and correct. Add tests for the binding.~~ ✅ |
| ~~6~~ | ~~Update `Job.Config` to `DownloadSettings`. Update `JobPreparer` — drop `Copy()`, pass references, call `ConfigManager.UpdateProfiles`. Add `WillWriteIndex` to `ConfigManager`. Thread `CliSettings?` through `UpdateProfiles`/`ProfileConditionSatisfied`/`GetVarValue` to fix `interactive` profile condition (was hard-coded `false`). Remove now-redundant `interactiveMode` mutation from `InteractiveModeManager`.~~ ✅ ⚠️ **Build broken** — `DownloadEngine` and `OnCompleteExecutor` still reference old `Config` fields; resolved in step 7. |
| ~~7~~ | ~~Update all callsites (extractors, `Preprocessor`, `FileManager`, `Searcher`, `Downloader`, `DownloadEngine`). Use Python rename script for the ~170 mechanical field renames. Fix null string patterns found during migration. **Resolves step 6 build breakage.**~~ ✅ |
| ~~8~~ | ~~Add `Channel<(ExtractJob, DownloadSettings)>` to `DownloadEngine`. Expose `Enqueue`/`CompleteEnqueue`. Update `RunAsync`.~~ ✅ |
| ~~9~~ | ~~Delete `Config.cs` and `ConfigTests.cs` (old tests for the deleted class).~~ ✅ |
