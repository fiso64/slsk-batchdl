# Plan: Core / CLI Split (Step 3)

## Overview

Split the monolithic `slsk-batchdl` console app into two projects:

- **`slsk-batchdl.Core`** — class library containing the engine, services, models, extractors, and
  settings. Zero knowledge of the console, CLI args, or JSON output formatting.
- **`slsk-batchdl.Cli`** — console app referencing Core. Owns arg parsing, config loading,
  interactive mode, and progress display.

The CLI binary name (`sldl`) and observable behaviour stay identical after the split. This is a
pure structural refactor. The `IProgressReporter` interface is also replaced with a C# event
system on `DownloadEngine` as part of this step, which is necessary for the server/GUI work
that follows.

---

## 1. Solution structure

```
slsk-batchdl/                       (solution root — unchanged)
├── slsk-batchdl.sln
├── slsk-batchdl.Core/              (new)
│   └── slsk-batchdl.Core.csproj
├── slsk-batchdl.Cli/               (renamed from slsk-batchdl/)
│   └── slsk-batchdl.Cli.csproj
├── slsk-batchdl.Core.Tests/        (Core-only tests)
├── slsk-batchdl.Cli.Tests/         (CLI/config tests)
└── slsk-batchdl.HelpGenerator/     (unchanged)
```

**`slsk-batchdl.Core.csproj`**
- `OutputType`: ClassLibrary
- `AssemblyName`: `Sldl.Core`
- `RootNamespace`: `Sldl.Core`
- NuGet packages: Soulseek, SpotifyAPI.Web, Google.Apis.YouTube.v3, HtmlAgilityPack,
  SmallestCSVParser, TagLibSharp, YoutubeExplode, SpotifyAPI.Web.Auth

**`slsk-batchdl.Cli.csproj`**
- `OutputType`: Exe
- `AssemblyName`: `sldl` (unchanged — binary name stays `sldl`)
- `RootNamespace`: `Sldl.Cli`
- NuGet packages: Goblinfactory.Konsole (console progress bars, CLI-only)
- `<ProjectReference>` to `slsk-batchdl.Core`
- Publish profiles, trimming config, HelpGenerator target all stay here

---

## 2. File assignment

### Core (`slsk-batchdl.Core/`)

| Source location | Notes |
|---|---|
| `DownloadEngine.cs` | Remove `IProgressReporter` field; add `Events` property |
| `Enums.cs` | No changes needed |
| `Jobs/*.cs` | All job types — no changes needed |
| `Models/*.cs` | All models — no changes needed |
| `Extractors/*.cs` | All extractors — no changes needed |
| `Settings/DownloadSettings.cs` | — |
| `Settings/EngineSettings.cs` | — |
| `Settings/ExtractionSettings.cs` | — |
| `Settings/OutputSettings.cs` | — |
| `Settings/PreprocessSettings.cs` | — |
| `Settings/SearchSettings.cs` | — |
| `Settings/SkipSettings.cs` | — |
| `Settings/TransferSettings.cs` | — |
| `Settings/BandcampSettings.cs` | — |
| `Settings/CsvSettings.cs` | — |
| `Settings/SpotifySettings.cs` | — |
| `Settings/YouTubeSettings.cs` | — |
| `Settings/YtDlpSettings.cs` | — |
| `Services/Downloader.cs` | Replace `IProgressReporter` param with `EngineEvents` |
| `Services/Searcher.cs` | Replace `IProgressReporter` param with `EngineEvents` |
| `Services/FileManager.cs` | — |
| `Services/JobContext.cs` | — |
| `Services/JobPreparer.cs` | — |
| `Services/M3uEditor.cs` | — |
| `Services/OnCompleteExecutor.cs` | — |
| `Services/Preprocessor.cs` | — |
| `Services/ResultSorter.cs` | — |
| `Services/SoulseekClientManager.cs` | — |
| `Services/TrackSkipper.cs` | — |
| `Services/ConditionParser.cs` | — |
| `Utilities/Logger.cs` | Remove `Printing` dependency (see §5) |
| `Utilities/IntervalProgressReporter.cs` | Update to use `EngineEvents` or `Logger` directly |
| `Utilities/RateLimitedSemaphore.cs` | — |
| `Utilities/TrackTemplateParser.cs` | — |
| `Utilities/Utils.cs` | — |
| *(new)* `Utilities/EngineEvents.cs` | Replaces `IProgressReporter` |

### CLI (`slsk-batchdl.Cli/`)

| Source location | Notes |
|---|---|
| `Program.cs` | Subscribe reporters to `engine.Events` instead of passing as ctor arg |
| `Help.cs` | — |
| `Help.Content.cs` | Generated file — stays here |
| `Settings/CliSettings.cs` | — |
| `Settings/ConfigFile.cs` | — |
| `Services/ConfigManager.cs` | — |
| `Services/ConsoleInputManager.cs` | — |
| `Services/InteractiveModeManager.cs` | — |
| `Services/DiagnosticService.cs` | Calls `JsonPrinter` — CLI presentation concern |
| `Utilities/CliProgressReporter.cs` | Rewritten: subscribes to `EngineEvents` |
| `Utilities/JsonStreamProgressReporter.cs` | Rewritten: subscribes to `EngineEvents` |
| `Utilities/JsonPrinter.cs` | — |
| `Utilities/Printing.cs` | — |
| `Properties/PublishProfiles/*` | — |

### Tests

- `slsk-batchdl.Core.Tests` references only `slsk-batchdl.Core`.
- `slsk-batchdl.Cli.Tests` references `slsk-batchdl.Cli` and `slsk-batchdl.Core`.
- `<Compile Include>` links for `MockSoulseekClient*.cs` removed from Core's csproj;
  the mock files live in `slsk-batchdl.Core.Tests/TestClients` and are only needed there.

---

## 3. Namespace renaming

All namespaces are renamed with a `Sldl.Core.` prefix in Core and `Sldl.Cli.` in CLI. Files
currently in the global namespace get explicit namespace declarations.

| Current namespace | New namespace |
|---|---|
| `Jobs` | `Sldl.Core.Jobs` |
| `Models` | `Sldl.Core.Models` |
| `Enums` | `Sldl.Core` |
| `Settings` | `Sldl.Core.Settings` (Core files) / `Sldl.Cli.Settings` (CliSettings, ConfigFile) |
| `Services` | `Sldl.Core.Services` (Core files) / `Sldl.Cli.Services` (ConfigManager, etc.) |
| `Utilities` | `Sldl.Core` (Core utilities) / `Sldl.Cli` (Printing, CliProgressReporter, etc.) |
| `Extractors` | `Sldl.Core.Extractors` |
| *(global)* `DownloadEngine`, `Searcher`, `Downloader`, `Utils`, `Logger`, etc. | `Sldl.Core` |
| *(global)* `Program`, `Printing`, `JsonPrinter`, etc. | `Sldl.Cli` |

The rename is mechanical and can be done with IDE rename-refactoring or global find-and-replace
on namespace declarations. All `using` directives update in the same pass.

---

## 4. Replacing `IProgressReporter` with `EngineEvents`

`IProgressReporter` is removed entirely. `DownloadEngine` gains a public `EngineEvents Events`
property. `Searcher` and `Downloader` take `EngineEvents` in their constructors instead of
`IProgressReporter`.

### `EngineEvents` class

```csharp
namespace Sldl.Core;

public class EngineEvents
{
    // ── Extraction ───────────────────────────────────────────────────────────
    public event Action<ExtractJob>?        ExtractionStarted;
    public event Action<ExtractJob, Job>?   ExtractionCompleted;
    public event Action<ExtractJob, string>? ExtractionFailed;

    // ── Job-level ────────────────────────────────────────────────────────────
    public event Action<Job>?                 JobStarted;
    public event Action<Job, AlbumFolder>?    AlbumDownloadStarted;
    public event Action<Job>?                 AlbumDownloadCompleted;
    public event Action<Job>?                 JobFolderRetrieving;
    public event Action<Job, bool, int>?      JobCompleted;    // job, found, lockedFileCount
    public event Action<Job, string>?         JobStatus;       // job, short status label

    // ── Song-level ───────────────────────────────────────────────────────────
    public event Action<SongJob>?             SongSearching;
    public event Action<SongJob>?             SongNotFound;
    public event Action<SongJob>?             SongFailed;
    public event Action<SongJob>?             StateChanged;
    public event Action<SongJob>?             OnCompleteStart;
    public event Action<SongJob>?             OnCompleteEnd;

    // ── Download ─────────────────────────────────────────────────────────────
    public event Action<SongJob, FileCandidate>?         DownloadStarted;
    public event Action<SongJob, long, long>?            DownloadProgress; // transferred, total
    public event Action<SongJob, TransferStates>?        DownloadStateChanged; // raw state, not string

    // ── List / overall ───────────────────────────────────────────────────────
    public event Action<IEnumerable<SongJob>>?           TrackListReady;
    public event Action<JobList, int, int, int>?         ListProgress;    // list, done, failed, total
    public event Action<int, int, int>?                  OverallProgress; // done, failed, total
}
```

**Why `Action<>` not `EventHandler<T>`:** The sender is always `DownloadEngine` and is never
needed by subscribers. `Action<>` is terser and avoids allocating `EventArgs` subclasses.

**`DownloadStateChanged` carries `TransferStates` not a string:** Subscribers format it as they
need. `CliProgressReporter` keeps its `GetStateLabel` helper locally. `JsonStreamProgressReporter`
can either serialize the enum name or map it too.

**`JobStatus` keeps a string:** The labels ("deleting files", "moved to ...", "cancelled") are
already short ad-hoc strings that carry no machine-readable structure — passing them as strings
is fine.

### `DownloadEngine` changes

```csharp
public class DownloadEngine
{
    public EngineEvents Events { get; } = new();

    // Remove: IProgressReporter field, constructor parameter, ProgressReporter property

    // Internal use — fire events through Events:
    // e.g. Events.ExtractionStarted?.Invoke(ej);
    //      Events.SongSearching?.Invoke(song);
    // etc.
}
```

`Searcher` and `Downloader` constructors change `IProgressReporter progressReporter` →
`EngineEvents events`. All internal `progressReporter.Report*` calls change to
`events.EventName?.Invoke(...)`.

### CLI-side reporters become event subscribers

`CliProgressReporter` and `JsonStreamProgressReporter` are no longer instantiated before the
engine and injected. Instead, they subscribe after construction:

```csharp
// Program.cs
var engine = new DownloadEngine(engineSettings, clientManager);

if (cliSettings.ProgressJson)
    new JsonStreamProgressReporter(Console.Out).Attach(engine.Events);
else
    new CliProgressReporter(cliSettings).Attach(engine.Events);
```

Each reporter gains an `Attach(EngineEvents events)` method that wires up all its handlers.

### `IntervalProgressReporter`

This class is used internally by `DownloadEngine` to log periodic summary lines via
`Logger.Info`. It does not implement `IProgressReporter` and is not affected by the removal —
it stays as-is.

---

## 5. Logger refactoring

`Logger` stays in Core but must not depend on `Printing` (which is CLI-only).

**Problem:** `Logger.AddConsole` currently calls `Printing.WriteLine`, and the `Log` method has
a special branch for console outputs that calls `Printing.WriteLine` for coloured output.

**Fix:** Replace both `Printing.WriteLine` calls in `Logger` with direct `Console` calls. The
`Logger.AddConsole` overload registers an output action that writes to `Console.Out` directly,
using `Console.ForegroundColor` for colour. `Printing`'s concurrency locking is not needed here
because Logger already holds its own `_lockObject` around output writes.

The `IsConsoleOutput` / `UseConsoleColors` flags on `OutputConfig` remain; the branch that was
`Printing.WriteLine(logEntry, color)` becomes:

```csharp
Console.ForegroundColor = targetColor.Value;
Console.WriteLine(logEntry);
Console.ResetColor();
```

`Program.cs` still calls `Logger.AddConsole()` exactly as today. No call-site changes needed.

---

## 6. `MockSoulseekClient` cleanup

The main project's csproj currently links mock files from the Tests folder:
```xml
<Compile Include="../slsk-batchdl.Core.Tests/TestClients/MockSoulseekClient*.cs" ... />
```

This link is removed from `slsk-batchdl.Core.csproj`. The files already live in
`slsk-batchdl.Core.Tests/` and only need to be compiled there. If any Core internals need to be
accessed from tests, add `[assembly: InternalsVisibleTo("slsk-batchdl.Core.Tests")]` to Core.

---

## 7. Implementation order

| Step | Description |
|------|-------------|
| 7.1 | Create `slsk-batchdl.Core/` folder and `.csproj`. Move Core files. Add to solution. |
| 7.2 | Rename `slsk-batchdl/` folder → `slsk-batchdl.Cli/`. Update `.csproj` (name, assembly, add Core reference, remove NuGet packages now in Core). Update solution. |
| 7.3 | Rename all namespaces (IDE tooling or global find-and-replace). Verify build. |
| 7.4 | Add `EngineEvents.cs` to Core. Remove `IProgressReporter.cs`. Update `DownloadEngine`, `Searcher`, `Downloader`. |
| 7.5 | Rewrite `CliProgressReporter` and `JsonStreamProgressReporter` as event subscribers. Update `Program.cs`. |
| 7.6 | Refactor `Logger.AddConsole` / `Logger.Log` to remove `Printing` dependency. |
| 7.7 | Remove `MockSoulseekClient` compile links from Core csproj. Update Tests project reference to Core. |
| 7.8 | Full build + test run. Smoke-test the CLI binary. |

Steps 7.1–7.3 are mechanical and can be done together. Steps 7.4–7.6 are the substantive
changes and should be done and verified independently.

---

## Open questions deferred to later steps

- **`SearchJob`**: Explicit search-then-choose job type for GUI. Not needed for Step 3.
- **`SelectAlbumVersion` in daemon/thin-client mode**: Interactive album selection over
  the network. Not needed until the Server project exists.
- **Server project (`slsk-batchdl.Server`)**: ASP.NET Core Web API wrapping `DownloadEngine`
  as a background service, with SignalR for real-time updates. Belongs to Step 4.
- **Daemon mode in CLI** (`sldl daemon`): CLI boots the Server project. Step 4.
- **Remote CLI mode** (`sldl --remote localhost:5000 ...`): CLI as thin HTTP client. Step 4.

---

## Current checkpoint — 2026-04-16

Step 3 is now structurally complete.

### Completed

- `slsk-batchdl.Core/` and `slsk-batchdl.Cli/` projects exist and are included in the solution.
- Core package references live in `slsk-batchdl.Core.csproj`; CLI keeps CLI-only packages and
  references Core.
- Source files are split into their intended Core/CLI folders.
- Core namespaces are under `Sldl.Core.*`; CLI namespaces are under `Sldl.Cli`.
- `EngineEvents` exists in Core and replaces progress reporter injection.
- `DownloadEngine`, `Searcher`, and `Downloader` use `EngineEvents`.
- `CliProgressReporter` and `JsonStreamProgressReporter` attach to `engine.Events`.
- `Logger` no longer depends directly on CLI `Printing`.
- Core no longer links `MockSoulseekClient` files from the test project.
- Tests are split into `slsk-batchdl.Core.Tests` and `slsk-batchdl.Cli.Tests`; Core tests reference
  only Core, while CLI/config tests reference the CLI project.
- Test namespace/import fallout was cleaned up, including a test-only `GlobalUsings.cs`.
- Search-setting application was moved into `JobPreparer.ApplySearchSettings` so both the engine
  and tests use the same preparation helper.
- Stale code comments mentioning removed progress reporter types were cleaned up.
- `--mock-files-dir` now uses a Core-owned `LocalFilesSoulseekClient`, so the diagnostic CLI
  path remains available without compiling test mocks into Core.

### Verification status

- `dotnet build slsk-batchdl.sln --no-restore` succeeds with existing warnings only.
- `dotnet test slsk-batchdl.Core.Tests/slsk-batchdl.Core.Tests.csproj --no-build` passes:
  223 passed, 0 failed.
- `dotnet test slsk-batchdl.Cli.Tests/slsk-batchdl.Cli.Tests.csproj --no-build` passes:
  82 passed, 0 failed.
- CLI smoke test succeeds:
  `dotnet slsk-batchdl.Cli/bin/Debug/net10.0/sldl.dll --help`
  prints `Usage: sldl <input> [OPTIONS]`, confirming the output assembly name remains `sldl`.
- `--mock-files-dir` smoke test succeeds against a temporary local music folder and downloads
  the expected files through the CLI.
- `Dockerfile` and `publish.bat` now publish `slsk-batchdl.Cli/slsk-batchdl.Cli.csproj`; no
  runnable files still reference the removed `slsk-batchdl/slsk-batchdl.csproj` path.
- The large move/rename diff has had a focused pre-commit review for stale namespaces, old project
  paths, removed progress reporter references, and accidental Core dependencies on CLI/test code.

### Remaining follow-up

- Review the Docker image package choices: the projects target `net10.0`, but the Dockerfile
  still installs `dotnet6-sdk` / `dotnet6-runtime`.

---

## Next step: Typed profiles and job settings resolver

The current `ConfigManager` still treats profiles as CLI token lists. Before the GUI rewrite,
decouple profiles from CLI arguments so Core can apply profiles without knowing about argv,
short flags, or CLI-only settings.

### Goal

- Core owns typed profile application.
- CLI only parses `sldl.conf` and command-line args into typed profiles.
- GUI can build the same typed profiles directly from UI state.
- Auto-profiles still work per job, but no longer require rebinding raw CLI tokens.

### Proposed Core model

Add Core profile and patch types:

```csharp
public sealed record SettingsProfile
{
    public string Name { get; init; } = "";
    public string? Condition { get; init; }
    public EngineSettingsPatch Engine { get; init; } = new();
    public DownloadSettingsPatch Download { get; init; } = new();
}
```

Patch objects represent only explicitly configured values, e.g. nullable scalars and nullable
collections:

```csharp
public sealed record DownloadSettingsPatch
{
    public SearchSettingsPatch Search { get; init; } = new();
    public OutputSettingsPatch Output { get; init; } = new();
    public ExtractionSettingsPatch Extraction { get; init; } = new();
}
```

Add a `SettingsPatchApplier` that applies patches to cloned settings instances.

### Job settings resolver

Replace the static `JobPreparer.ApplyProfiles` hook with an instance-level resolver:

```csharp
public interface IJobSettingsResolver
{
    DownloadSettings Resolve(DownloadSettings inherited, Job job);
}
```

`JobPreparer` should call the resolver while assigning `job.Config`. The default resolver returns
the inherited settings unchanged. The CLI creates a profile-backed resolver and passes it to the
engine/preparer.

### Auto-profile conditions

Move auto-profile condition evaluation to Core, but do not reference `CliSettings` from Core.
Instead, support an optional profile context:

```csharp
public sealed class ProfileContext
{
    public Dictionary<string, object> Values { get; } = new();
}
```

Condition variables are resolved from:

- Core settings/job state, such as `album`, `aggregate`, `input-type`, job type, etc.
- Extra context values supplied by the host.

The CLI can provide:

```csharp
context.Values["interactive"] = cli.InteractiveMode;
context.Values["progress-json"] = cli.ProgressJson;
context.Values["no-progress"] = cli.NoProgress;
```

The GUI can provide GUI-specific values later, or omit CLI-only values. Unknown condition
variables should fail loudly rather than silently evaluating to false.

### Migration shape

1. Add Core patch/profile/resolver types.
2. Change CLI config-file parsing so profiles become `SettingsProfile` objects instead of token
   lists.
3. Change CLI argument parsing so command-line options produce a special `<cli>` profile plus
   `CliSettings`.
4. Apply profiles in this order: built-in defaults, config default profile, explicit named
   profiles, matching auto-profiles, CLI profile.
5. Remove token rebinding from auto-profile application.
