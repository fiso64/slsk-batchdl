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

## Goals

1. **Decouple CLI parsing from configuration data.** Config classes are pure data; the parser is
   a separate utility.
2. **Separate engine-level from per-submission settings.** This enables clean dynamic job
   enqueueing from a GUI without faking argv.
3. **Attribute-based CLI parser.** Options are declared on the config fields themselves; no
   separate maintenance of a switch/case.
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

Stable for the lifetime of the engine. Set once at construction.

```csharp
public class EngineSettings
{
    public string?           Username             { get; init; }
    public string?           Password             { get; init; }
    public bool              UseRandomLogin       { get; init; }
    public int?              ListenPort           { get; init; } = 49998;
    public int               ConnectTimeout       { get; init; } = 20_000;
    public int               ConcurrentSearches   { get; init; } = 2;
    public int               ConcurrentExtractors { get; init; } = 2;
    public int               SearchesPerTime      { get; init; } = 34;
    public int               SearchRenewTime      { get; init; } = 220;
    public int               SharedFiles          { get; init; }
    public int               SharedFolders        { get; init; }
    public string?           UserDescription      { get; init; }
    public bool              NoModifyShareCount   { get; init; }
    public Logger.LogLevel   LogLevel             { get; init; } = Logger.LogLevel.Info;
    public string?           LogFilePath          { get; init; }
    public bool              NoProgress           { get; init; }
    public bool              ProgressJson         { get; init; }
    public string?           MockFilesDir         { get; init; }
    public bool              MockFilesReadTags    { get; init; } = true;
    public bool              MockFilesSlow        { get; init; }
}
```

### `DownloadSettings`

Per-submission. Passed to `engine.EnqueueAsync(job, settings)`. Composed of domain-oriented
sub-objects so that services receive only what they need.

```
DownloadSettings
├── OutputSettings        — ParentDir, NameFormat, InvalidReplaceStr, WritePlaylist,
│                           WriteIndex, IndexFilePath, M3uFilePath, FailedAlbumPath,
│                           OnComplete, PrintOption
├── SearchSettings        — NecessaryCond, PreferredCond, NecessaryFolderCond,
│                           PreferredFolderCond, SearchTimeout, MaxStaleTime,
│                           MaxRetriesPerTrack, UnknownErrorRetries, DownrankOn,
│                           IgnoreOn, FastSearch, FastSearchDelay, FastSearchMinUpSpeed,
│                           DesperateSearch, NoRemoveSpecialChars,
│                           RemoveSingleCharTerms, NoBrowseFolder, Relax, NoIncompleteExt
├── SkipSettings          — SkipExisting, SkipNotFound, SkipMode, SkipMusicDir,
│                           SkipModeMusicDir, SkipCheckCond, SkipCheckPrefCond
├── PreprocessSettings    — RemoveFt, RemoveBrackets, ExtractArtist, ArtistMaybeWrong,
│                           ParseTitleTemplate, Regex
├── AlbumSettings         — Album, Aggregate, AlbumArtOnly, AlbumArtOption,
│                           InteractiveMode, MinAlbumTrackCount, MaxAlbumTrackCount,
│                           SetAlbumMinTrackCount, SetAlbumMaxTrackCount,
│                           AlbumTrackCountMaxRetries
├── ExtractionSettings    — Input, InputType, MaxTracks, Offset, Reverse, GetDeleted,
│                           DeletedOnly, RemoveTracksFromSource,
│                           MinSharesAggregate, AggregateLengthTol
├── SpotifySettings       — ClientId, ClientSecret, Token, Refresh
├── YouTubeSettings       — ApiKey, YtdlpArgument, UseYtdlp, YtParse
└── CsvSettings           — ArtistCol, AlbumCol, TitleCol, YtIdCol, DescCol,
                            TrackCountCol, LengthCol, TimeUnit, HtmlFromFile
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

**On immutability:** `init`-only properties require a builder/construction pattern rather than
the current mutation-based `ProcessArgs`. During construction (CLI parsing, profile application),
a mutable intermediate is used; the final settings object is frozen at the end. The profile
replay pattern (`UpdateProfiles`) reconstructs from scratch — this is compatible with `init`
properties since it already rebuilds from defaults every time.

---

## Attribute-based CLI parser

Options are declared on config fields; the parser is driven by reflection. No more switch/case.

```csharp
// On any sub-config class:
[Option("--album", "-a", HelpText = "Download as album")]
public bool Album { get; init; }

[Option("--path", "-p", HelpText = "Output directory")]
public string? ParentDir { get; init; }

[Option("--max-tracks", "-n")]
public int MaxTracks { get; init; } = int.MaxValue;

[Option("--spotify-id", "--si")]   // declared on SpotifySettings
public string? ClientId { get; init; }

// Input accepts both --input/-i and as the bare positional argument.
[Option("--input", "-i")]
[Positional]   // first bare non-flag token maps here if --input/-i not already set
public string? Input { get; init; }
```

The binder handles:
- `--arg=val` → split on first `=`
- `-abc` → expand to `-a -b -c`
- Bool flags: bare `--flag` = true, `--flag false` = false, `--flag true` = true
- Enum fields: attribute carries the string→enum mapping or a converter delegate
- Nested sub-objects: binder walks the `DownloadSettings` object graph; each sub-config
  class has `[Option]` attributes on its own properties
- Positional: a token that does not start with `-` and has no preceding flag awaiting a
  parameter fills the `[Positional]`-annotated field. If the field is already set
  (via `--input`/`-i`), emitting a positional token is an error.

A `[CustomParser]` attribute or explicit pre-processing handles the ~8 non-trivial cases:
- `--login user;pass` → splits into `Username` + `Password`
- `--album-track-count N+` / `N-` shorthand
- `--regex T:pattern;replacement` with target-field prefix and append mode
- `--on-complete + cmd` append mode

**Validation is separate from binding.** The binder only produces a settings object; it does not
enforce business rules. `DownloadSettings` has a `Validate()` method called by `ConfigManager`
after binding is complete:

```csharp
public IEnumerable<string> Validate()
{
    if (Extraction.Input == null && (Output.PrintOption & PrintOption.Index) == 0)
        yield return "No input provided";
    // other cross-field validations
}
```

This keeps the binder general-purpose and makes validation testable independently.

**Config file format stays the same.** `ParseConfig()` converts `key = val` lines to
`--key val` token pairs, which flow through the same binder. Profile sections are labelled
groups of those tokens.

**[DEFERRED]**: **Help text.** `[Option]` attributes carry a `HelpText` string, which becomes the authoritative
source for the option reference table. `slsk-batchdl.HelpGenerator` currently reads `README.md`
and generates `Help.Content.cs`; after this refactor it should read attribute metadata instead,
so adding an option without documentation becomes impossible. The README's narrative sections
(examples, cross-references, grouped explanations) stay hand-written — only the option table
is generated.

---

## `ConfigManager`

Owns everything related to loading and merging configuration. The config classes themselves have
no knowledge of profiles, files, or argv.

```csharp
public static class ConfigManager
{
    // Discovers and loads the config file. Returns parsed profile dict.
    public static ConfigFile Load(string? explicitPath = null);

    // Applies the named profiles (and default) from a loaded config file
    // to a raw token list, then binds to settings objects.
    public static (EngineSettings engine, DownloadSettings download)
        Bind(ConfigFile file, string[] cliArgs, string? profileName = null);

    // Re-evaluates auto-profiles against the given job; returns new settings
    // if any profile changed, or the same instance if nothing changed.
    public static DownloadSettings UpdateProfiles(
        DownloadSettings current, ConfigFile file, string[] cliArgs, Job job);

    // Evaluates a profile-cond expression string against variables derived from
    // the job and current settings. Extracted from Config.ProfileConditionSatisfied.
    public static bool ProfileConditionSatisfied(string cond, DownloadSettings settings, Job? job);
}
```

**Token merging model:** All config sources (default profile, named profiles, CLI args) are
reduced to ordered token lists. Merging is left-to-right override — later tokens win for scalar
fields, and appendable fields (`--on-complete + ...`, `--regex + ...`) accumulate. The profile
replay in `UpdateProfiles` replays: defaults → auto-profiles → named profile → CLI args.

**`ConfigFile`** is a plain data object:
```csharp
public record ConfigFile(
    string Path,
    IReadOnlyDictionary<string, ProfileEntry> Profiles
);

public record ProfileEntry(IReadOnlyList<string> Tokens, string? Condition);
```

---

## `ConditionParser`

`ParseConditions` is currently a static method on `Config` for no reason. Move it:

```csharp
// Services/ConditionParser.cs
public static class ConditionParser
{
    public static FileConditions   ParseFileConditions(string input, FolderConditions? folderOut = null);
    public static FolderConditions ParseFolderConditions(string input);
}
```

Callers (`List.cs` extractor, `ConfigManager`) import `ConditionParser` directly.

---

## `Job.Config` and `JobPreparer`

`Job.Config` currently holds a `Config` instance. After the refactor it holds `DownloadSettings`.
`JobPreparer.PrepareJob` currently does `job.Config = parentConfig.Copy()` — `Copy()` disappears;
since `DownloadSettings` is immutable (`init` properties), "copying" is just passing the same
reference. A new instance is only created when profile re-evaluation produces a change (via
`ConfigManager.UpdateProfiles`).

`EngineSettings` is not on `Job` at all — it belongs to `DownloadEngine` only.

---

## Enabling the channel-based engine API

After this refactor:

```csharp
// Construction — engine-level settings only
new DownloadEngine(EngineSettings, clientManager, progressReporter);

// Submit a job at any time, with its own per-submission settings
engine.EnqueueAsync(new ExtractJob(url), downloadSettings);
```

`RunAsync` loops over a `Channel<(ExtractJob, DownloadSettings)>`. The CLI writes one item and
completes the writer. The GUI writes items as the user queues downloads.

This is the direct payoff of separating `EngineSettings` from `DownloadSettings` — the engine
no longer needs a `defaultConfig` field, and every submission is self-contained.

---

## Null string migration

**Why:** `null` = not set. `""` = explicitly set to empty (almost never a valid case for config
strings). Current code uses `""` as "not set", leading to defensive `Length > 0` and `!= ""`
checks everywhere. After migration, `== null` / `!= null` is the idiom.

**Risk:** `== ""` and `.Length == 0` checks will not become compile errors when the field type
changes to `string?`. They will silently evaluate to `false` for `null` values (since null ≠ ""),
causing "not set" checks to stop working.

**Mitigation — before changing any field type, run these greps and treat output as a fix-list:**

```
== ""
!= ""
\.Length == 0
\.Length > 0
string\.IsNullOrEmpty    ← already correct, no change needed
string\.IsNullOrWhitespace  ← already correct
```

Scope is bounded: config fields are read in extractors, `FileManager`, `JobPreparer`,
`Preprocessor`, `DownloadEngine`, and the CLI layer. The highest-traffic fields to audit first:
`parentDir`, `nameFormat`, `indexFilePath`, `skipMusicDir`, `failedAlbumPath`.

Enable `<Nullable>enable</Nullable>` in the project as part of this refactor. It catches
unguarded dereferences of `string?` fields (the compile-time half of the risk). Combine with the
grep checklist above to cover the silent-semantic half.

---

## What disappears

| Current | Replaced by |
|---------|-------------|
| `Config` class (1600 lines) | `EngineSettings` + `DownloadSettings` + sub-objects |
| `Config(string[] args)` constructor | `ConfigManager.Bind(file, args)` |
| `Config.Copy()` | Not needed — `DownloadSettings` is immutable; profile re-eval via `ConfigManager.UpdateProfiles` |
| `Config.ProcessArgs()` (800-line switch) | Attribute-based binder driven by reflection |
| `Config.ParseConfig()` | `ConfigManager.Load()` |
| `Config.UpdateProfiles()` / `ApplyProfiles()` | `ConfigManager.UpdateProfiles()` |
| `Config.ProfileConditionSatisfied()` + expression parser | `ConfigManager.ProfileConditionSatisfied()` |
| `Config.ParseConditions()` | `ConditionParser.ParseFileConditions()` |
| `Config.PostProcessArgs()` | Path expansion moves to `ConfigManager.Bind()`; derived-field logic moves to computed properties on `DownloadSettings` |
| `Config.InputError()` / `InputWarning()` | Throw site + Logger.Warn at the call site, or a shared `ParseException` type |
| `DownloadEngine.defaultConfig` | `EngineSettings` at construction; `DownloadSettings` per submission |

---

## Implementation order

| Step | Description |
|------|-------------|
| 1 | Define `EngineSettings`, `DownloadSettings`, sub-config classes as data-only (`init` properties). No logic yet. Wire nullable annotations. |
| 2 | Run null-migration grep checklist. Fix all `== ""`/`.Length` patterns on fields being migrated. Enable `<Nullable>enable</Nullable>`. |
| 3 | Write `ConditionParser` as a standalone static class. Replace `Config.ParseConditions` calls. |
| 4 | Write the attribute-based binder. Start with the common cases (string, int, bool, enum). Handle the ~8 special-case args explicitly in the CLI layer. |
| 5 | Write `ConfigManager` (Load, Bind, UpdateProfiles, ProfileConditionSatisfied). Port profile logic from `Config`. |
| 6 | Update `Job.Config` to `DownloadSettings`. Update `JobPreparer` — drop `Copy()`, pass immutable references, call `ConfigManager.UpdateProfiles` instead of `config.UpdateProfiles`. |
| 7 | Update all callsites (extractors, `Preprocessor`, `FileManager`, `Searcher`, `Downloader`, `DownloadEngine`). |
| 8 | Add `Channel<(ExtractJob, DownloadSettings)>` to `DownloadEngine`. Expose `EnqueueAsync`. Update `RunAsync` to loop over the channel. |
| 9 | Delete `Config.cs`. |
