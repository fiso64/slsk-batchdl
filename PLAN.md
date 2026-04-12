# Plan: Job Architecture Redesign

## Overview

The current job hierarchy has accumulated structural debt: 10+ job types across two parallel
hierarchies (`QueryJob`/`DownloadJob`), with `SongJob` sitting outside the hierarchy entirely as an
internal engine object. The new design collapses this into 6 clean types with a single unambiguous
lifecycle model.

---

## 1. New job hierarchy

```
Job (abstract)
├── ExtractJob         { string Input; InputType? InputType; Job? Result }
├── JobList            { List<Job> Jobs }          ← ItemName inherited from Job
├── SongJob            { SongQuery Query; FileCandidate? ResolvedTarget; ... }
├── AlbumJob           { AlbumQuery Query; AlbumFolder? ResolvedTarget; ... }
├── AggregateJob       { SongQuery Query; List<SongJob> Songs }
└── AlbumAggregateJob  { AlbumQuery Query }
```

**`ExtractJob`** holds an input string (URL, file path, soulseek URI, etc.) and an optional
`InputType`. The engine runs the appropriate extractor on it, sets `Result` to whatever the
extractor returned, then processes `Result` as a child. The `ExtractJob` itself remains in the
tree (state `Done`/`Failed`) as a historical record of what was extracted and from where.

**`JobList`** is the universal grouping container. It holds any mix of `Job` subtypes. `ItemName`
(inherited from `Job`) carries the playlist name, artist name, filename, etc. The old
`SongListQueryJob`, `AlbumListJob`, and `JobQueue` all collapse into this. The engine's root
`Queue` is a persistent `JobList` initialized at construction.

**`SongJob`** is the unified song type. No more separate `SongQueryJob`/`SongDownloadJob`/internal
`SongJob`. If `ResolvedTarget` is null, the engine searches; if set, it skips to download. The
download progress fields (`Candidates`, `ChosenCandidate`, `DownloadPath`, `BytesTransferred`,
`FileSize`, `LastActivityTime`) live here.

**`AlbumJob`** mirrors `SongJob` for albums. `ResolvedTarget: AlbumFolder?` replaces the
`AlbumQueryJob`/`AlbumDownloadJob` split. `SelectAlbumVersion` callback receives an `AlbumJob`
with `Results` populated and sets `job.ResolvedTarget`. `AlbumFolder.Files` is `List<SongJob>`;
`AlbumFile` is removed entirely.

**`AggregateJob`** and **`AlbumAggregateJob`** stay conceptually unchanged.

**`Job` base class** carries only fields that are genuinely universal:
- `Id`, `State`, `Config`, `FailureReason` — lifecycle fields present on every job
- `ItemName`, `ItemNumber`, `LineNumber` — identity/provenance, used by all job types
- `CanBeSkipped` / `CanBeSkippedOverride` — skip-check policy, defaulted by subclass
- `ExtractorCond`, `ExtractorPrefCond`, `EnablesIndexByDefault` — extractor hints consumed and
  cleared by `JobPreparer`; kept on the base because any job type can be produced by an extractor
- `QueryTrack: SongQuery?` — virtual, returns `null` on the base; overridden by leaf types that
  have a meaningful query (`SongJob`, `AlbumJob`, etc.); `ExtractJob` and `JobList` do not override

---

## 2. Lifecycle model

Every job has a single `State: JobState`. The same enum covers both phases:

```
Pending → Extracting → Searching → Downloading → Done
                                 ↘ Failed
                                 ↘ Skipped
```

For a `SongJob` with `ResolvedTarget` pre-set, `Searching` is skipped.
For an `ExtractJob`, `Searching` and `Downloading` are skipped — it only uses `Extracting` → `Done`.
For a `JobList`, `State` is derived: `Done` when all children are done, `Failed` if all failed.

`TrackState` is removed. All state tracking moves to `JobState` on `SongJob`/`AlbumJob` directly.
`AlbumFile` is removed — per-file state is carried by `SongJob.State` directly.

---

## 3. `ExtractJob` and the extraction pipeline

`IExtractor.GetTracks` returns a single `Job`. The extractor is the authority on shape:
- Playlist extractor → `JobList` of `SongJob`s
- Album extractor → `AlbumJob` directly (no wrapping)
- list.txt extractor → `JobList` of `ExtractJob`s (one per line, no double-wrapping)
- Soulseek URI extractor → `SongJob` with `ResolvedTarget` pre-set
- String extractor → `AlbumJob` or `JobList([SongJob])`

The CLI always submits an `ExtractJob` into `Queue`. The engine has one generic rule: when it
encounters an `ExtractJob`, it runs the extractor, sets `Result`, and processes `Result` as a
child. This recurses naturally — a list.txt produces a `JobList` of `ExtractJob`s, each of which
the engine then resolves in turn:

```
Queue → [
  ExtractJob("list.txt") → Result = JobList [
    ExtractJob("https://open.spotify.com/playlist/...") → Result = JobList("My Playlist") [SongJob, ...]
    ExtractJob("album://Pink Floyd - The Wall")          → Result = AlbumJob(query)
    ExtractJob("slsk://user/Music/song.mp3")             → Result = SongJob(resolved)
  ]
]
```

`ExtractJob` nodes stay in the tree with `Result` set — they are never removed or replaced.

`RemoveTrackFromSource` stays on `IExtractor`. The engine calls it after a `SongJob` completes
inside a `JobList` that was produced by that extractor. The extractor reference is threaded as a
parameter through `ProcessJob` — it is not stored as an engine field (which would be a data race
under concurrent fan-out).

---

## 4. `album` / `aggregate` flags

Most extractors are faithful mappers and do not interpret `album`/`aggregate` — the upgrade is a
user-level semantic decision applied after extraction. Exception: the String extractor reads
`config.album` at extraction time and returns `AlbumJob` directly when set, since it is building
a query from scratch and can choose the right type immediately.

For all other extractors, `UpgradeToAlbumMode` is a method on `JobList`, called by the engine
after extraction. It upgrades each child in-place (e.g. Spotify/YouTube playlist `SongJob`s →
`AlbumJob`s). For bare (non-list) results, the engine wraps in a temporary `JobList`, upgrades,
then unwraps.

---

## 5. `JobList` as the queue

`JobQueue` is removed. The engine holds a persistent root `JobList` initialized at construction:

```csharp
public JobList Queue { get; } = new();
```

`JobPreparer` walks the tree to set up `Config`, `JobContext`, editors, and skippers per job — the
tree walk replaces the flat loop.

For `JobList`s, config and editors are shared across children (same as the current `SongListQueryJob`
model). Each `ExtractJob` gets its own config slice (profiles may change per source).

---

## 6. `SongJob` as a unified type

`SongJob` becomes a full `Job` subclass:

- `ItemNumber`, `LineNumber`, `FailureReason`, `Config` all live on the base `Job`
- `SongJob.Other` (YouTube display metadata) stays as-is
- `SongJob.Candidates` (search results) stays; `ResolvedTarget` = `ChosenCandidate` after selection

Consumers who currently watch `SongJob` directly (GUI, progress reporter) continue to do so — the
object is still publicly accessible as a child of the `JobList`. No change in observability.

---

## 7. `AlbumJob` unified type

`AlbumQueryJob` + `AlbumDownloadJob` collapse into one `AlbumJob`:

```csharp
public class AlbumJob : Job
{
    public AlbumQuery        Query          { get; set; }
    public List<AlbumFolder> Results        { get; set; } = new();  // populated by search
    public AlbumFolder?      ResolvedTarget { get; set; }           // set after SelectAlbumVersion
    public string?           DownloadPath   { get; set; }           // set after download
}

public class AlbumFolder
{
    public string         Username   { get; set; }
    public string         FolderPath { get; set; }
    public List<SongJob>  Files      { get; set; } = new();   // was List<AlbumFile>
}
```

`AlbumFile` is removed. Per-file state (download path, bytes transferred, state) is carried
directly by `SongJob`. `SongJob.IsNotAudio` becomes a computed property based on the file
extension of `ResolvedTarget?.Filename`.

`SelectAlbumVersion` callback: `Func<AlbumJob, Task<AlbumFolder?>>` — receives the job with
`Results` populated, returns the chosen folder (or null to skip). Engine sets
`job.ResolvedTarget = chosen`.

`M3uEditor.NotifyJobDownloadPath` push pattern stays — engine calls it after `DownloadPath` is set.

---

## 8. Interactive mode and fast-search

`SelectAlbumVersion` is an engine callback:

```csharp
// Called after album search completes. Return chosen folder or null to skip.
public Func<AlbumJob, Task<AlbumFolder?>>? SelectAlbumVersion { get; set; }
```

The `interactiveMode` config flag is consumed by the CLI. When set, the CLI wires in
`InteractiveModeManager` as the `SelectAlbumVersion` implementation — the engine has no direct
dependency on `InteractiveModeManager`. Filter state (`filterStr`) and retrieved-folder tracking
live in the CLI-side closure, not in the engine.

**Fast-search** is a song-level optimization: when a highly-ranked candidate arrives during a
song search (free slot, sufficient upload speed, preferred conditions met), the engine starts a
provisional download immediately without waiting for the full search to complete. The search
continues concurrently in the background so that if the fast download fails, the full candidate
list is available as fallback. Fast-search is entirely within `SearchAndDownloadSong` and has no
interaction with album-level logic or the `SelectAlbumVersion` callback.

The implementation lives in `SearchAndDownloadSong`. The search is started as a `Task` (not
awaited immediately) with an `onFastSearchCandidate` callback. When the callback fires, the engine
launches a provisional download `Task`. Both tasks run concurrently. The coordinator awaits
whichever finishes first:

- Fast download wins → cancel the search via `searchCts`, return the result.
- Search finishes first → sort all candidates, try them in order as today.
- Fast download fails while search still running → wait for search to finish, try remaining candidates.

`searchCts` is a `CancellationTokenSource` created inside `SearchAndDownloadSong`, linked to
`appCts`. It is cancelled by the coordinator when the fast download succeeds. `Downloader.DownloadFile`
has no knowledge of `searchCts` — cancellation of the search is the coordinator's responsibility,
not the downloader's.

---

## 9. `AggregateJob` processing

`AggregateJob` searches and populates `Songs: List<SongJob>`. Each `SongJob` in `Songs` has
`ResolvedTarget` pre-set (aggregate results are already resolved files). The engine processes them
as a batch download without a second search round — same as today, just cleaner typing.

---

## 10. State enums

`TrackState` is removed. `JobState` is extended:

```csharp
public enum JobState
{
    Pending          = 0,
    Done             = 1,   // aligned with TrackState.Downloaded    = 1
    Failed           = 2,   // aligned with TrackState.Failed        = 2
    AlreadyExists    = 3,   // aligned with TrackState.AlreadyExists = 3
    NotFoundLastTime = 4,   // aligned with TrackState.NotFoundLastTime = 4
    Skipped          = 5,   // new
    Extracting       = 6,   // new — ExtractJob running
    Searching        = 7,   // new — explicit search phase
    Downloading      = 8,   // new — explicit download phase
}
```

Values 0–4 are intentionally preserved from the current `JobState`/`TrackState` alignment so that
existing index files remain readable without migration. Values 5–8 are new and only used at runtime
(never written to index files).

---

## 11. `IProgressReporter`

The structured events become the primary API (machine-readable, sufficient for both CLI and GUI):

```csharp
// Extraction
void ReportExtractionStarted(ExtractJob job);
void ReportExtractionCompleted(ExtractJob job, Job result);

// Job-level (album / aggregate searches)
void ReportJobStarted(Job job, bool parallel);
void ReportJobCompleted(Job job, bool found, int lockedFiles);
void ReportJobFolderRetrieving(Job job);

// Song-level
void ReportSearchStart(SongJob song);
void ReportSearchResult(SongJob song, int resultCount, FileCandidate? chosen = null);
void ReportDownloadStart(SongJob song, FileCandidate candidate);
void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes);
void ReportStateChanged(SongJob song);

// List-level
void ReportListProgress(JobList list, int done, int total);
void ReportOverallProgress(int downloaded, int failed, int total);
```

---

## 12. `JobContext` and `JobPreparer`

`JobContext` stays engine-internal (editors, skippers). `JobPreparer` changes from a flat loop over
`Queue.Jobs` to a tree walk, with config flowing top-down:

**Config inheritance:**
- Root config starts from CLI args / config file
- `ExtractJob` gets a copy of the root config; its `inputType` is set from the resolved extractor
- `JobList` inherits config from its parent `ExtractJob` (or root if not under an `ExtractJob`)
- Leaf jobs (`SongJob`, `AlbumJob`, etc.) inherit config from their parent `JobList`

**Profile re-evaluation at leaves:**
Profile conditions are evaluated independently per leaf job at processing time, using:
- `input-type` — injected from the nearest ancestor `ExtractJob`'s resolved `inputType`
- `download-mode` — derived from the leaf job's concrete type (`SongJob` → `"normal"`,
  `AlbumJob` → `"album"`, etc.)

This means siblings are independent — a profile applied to one leaf does not affect its siblings.

**Editors and skippers:**
`JobList`-level editors and skippers are shared across all children in the list. Each `ExtractJob`
may get its own editor if it has a distinct `indexFilePath` after profile resolution.

---

## Concurrency

The engine's main loop is a recursive tree-walker:

```
ProcessJob(Job job, IExtractor? extractor):
    ExtractJob ej  →  ex = resolve extractor
                      ej.Result = ex.GetTracks(ej.Input)
                      ProcessJob(ej.Result, ex)          // recurses — uniform, no special-casing

    JobList jl     →  Task.WhenAll(jl.Jobs.Select(child => ProcessJob(child, extractor)))

    leaf job       →  skip checks → source search → download
```

`JobList` is the fan-out point. Concurrency applies uniformly at every level — root list, nested
list inside an `ExtractJob.Result`, or any other depth. All semaphores are tree-wide (not
per-list). The extractor reference is threaded as a parameter so concurrent `ExtractJob` runs
don't race on a shared field.

**Search concurrency** — two independent limiters compose in series on every search:

1. **`RateLimitedSemaphore`** (inside `Searcher`) — limits *rate*: N searches per time window.
   Config: `--searches-per-time` / `--search-renew-time`.
2. **`SemaphoreSlim concurrencySemaphore`** (inside `Searcher`) — limits *concurrency*: at most
   N searches in-flight simultaneously across the whole tree. Acquired around the full duration
   of each search. Default: 2. Config: `--concurrent-searches`.

`SearchDirectLinkAlbum` (peer browse, not a keyword search) does not consume a concurrency slot.

**Extractor concurrency** — `SemaphoreSlim` inside the engine limits simultaneous `ExtractJob`
runs to avoid API rate limits. Default: 2. Config: `--concurrent-extractors`.

**Download concurrency** — unlimited. Downloads are p2p and each peer serialises uploads anyway;
there is no meaningful reason to cap simultaneous downloads at the engine level. The old
`--concurrent-processes` / `--concurrent-downloads` flag is removed.

**CLI rendering** — the existing `CliProgressReporter` handles concurrent song downloads and
concurrent album searches. However, rendering multiple simultaneously active albums (each with its
own block of per-song progress bars) will likely have interleaving and overflow issues. Fixing
this is deferred (see §Next steps).

---

## Cancellation

Every job gets a `CancellationTokenSource` set by the engine when it starts processing that job.
The CTS is tree-linked: each job's CTS is linked to both `appCts` (engine-wide) and its parent
job's CTS, so that cancelling any ancestor propagates to all descendants automatically.

```csharp
public class Job
{
    // Set by the engine immediately before processing begins.
    // Linked to appCts and the parent job's token (if any).
    // Cancelling this job cancels all its descendants; engine-wide cancel propagates here too.
    public CancellationTokenSource? Cts { get; internal set; }
    public void Cancel() => Cts?.Cancel();
}
```

`ProcessJob` accepts a `parentToken` parameter (default: `CancellationToken.None`) and sets:
```csharp
job.Cts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token, parentToken);
```

`JobList` passes `jl.Cts.Token` as `parentToken` to each child in the `Task.WhenAll` fan-out,
so cancelling a list cancels all its songs.

**`ExtractJob` does not propagate to its `Result`.** When the engine recurses into `ej.Result`
after extraction, it passes `parentToken` (not `ej.Cts.Token`). Reason: the `ExtractJob` is
`Done` by the time its `Result` starts processing. Cancelling a `Done` job should have no effect
on the still-running `Result`. The `Result` can be cancelled independently by targeting the
`JobList` or individual leaf jobs directly.

The CTS hierarchy in practice:

```
appCts                                     (engine-wide — engine.Cancel())
  └── rootExtractJob.Cts                   (linked to appCts only)
  └── resultJobList.Cts                    (linked to appCts — sibling of ExtractJob, not child)
        └── song1.Cts                      (linked to resultJobList.Cts — cancel list cancels song)
        └── song2.Cts                      (independent of song1 — cancel one doesn't affect other)
        └── nestedList.Cts                 (linked to resultJobList.Cts)
              └── song3.Cts               (linked to nestedList.Cts)
                    └── searchCts         (per search cycle — cancelled on fast-search win)
```

The `cancelOnFail` pattern in album downloads (cancel remaining file downloads when one fails) is
implemented via the album's local `CancellationTokenSource` passed to `RunAlbumDownloads`, which
is itself linked to `job.Cts` so that user-initiated album cancellation also propagates.

**'c' keypress — deferred:**
The correct future implementation: the CLI owns keyboard input and, on 'c', presents a numbered
list of all currently running jobs (name + state) and lets the user select which one to cancel.
The engine exposes `job.Cancel()` and `engine.Cancel()`; it does not listen for console keys.
`SongJob`s that are part of an `AlbumJob` should not be listed individually for the cancel prompt.

---

## Breaking changes

| Change | Notes |
|--------|-------|
| `{state}` name-format / on-complete variable for songs: `"Downloaded"` → `"Done"` | `JobState` replaces `TrackState` on `SongJob` |
| `--parallel-album-search` / `parallelAlbumSearch` removed | Superseded by full parallel search+download |
| `--concurrent-processes` / `--concurrent-downloads` removed | Downloads are now unlimited; `--concurrent-searches` replaces the search-limiting role |

Index file format is **unchanged** — `JobState` values 0–4 are aligned with the old `TrackState`
values so existing index files remain readable.

---

## Implementation order

| Step | Description |
|------|-------------|
| 1  | ✓ Introduce `ExtractJob` and `JobList` types. |
| 2  | ✓ Collapse `SongJob` into `Job` hierarchy. Remove `SongQueryJob`, `SongDownloadJob`. |
| 3  | ✓ Collapse `AlbumQueryJob` + `AlbumDownloadJob` → `AlbumJob` with `ResolvedTarget`. Remove `AlbumFile`. |
| 4  | ✓ Rename `AggregateQueryJob` → `AggregateJob`, `AlbumAggregateQueryJob` → `AlbumAggregateJob`. Remove `QueryJob`, `DownloadJob`. |
| 5  | ✓ Collapse `SongListQueryJob` + `AlbumListJob` → `JobList`. Remove `JobQueue`. |
| 6  | ✓ `IExtractor.GetTracks` → `Job` (singular). Engine: `Queue.Jobs.Add(result)`. Remove `parallelAlbumSearch`. |
| 7  | ✓ Update `JobPreparer` to tree-walk instead of flat loop. |
| 8  | ✓ Remove `TrackState`. Extend `JobState` with `Skipped`, `Extracting`, `Searching`, `Downloading`. |
| 9  | ✓ Update `IProgressReporter` signatures. |
| 10 | ✓ Update `Preprocessor`, `TrackSkipper`, `FileManager`, `OnCompleteExecutor` for new types. |
| 11 | ✓ `ExtractJob.Result`, recursive `ProcessJob` tree-walker, `Task.WhenAll` fan-out, search/extractor semaphores, `InteractiveModeManager` moved to CLI, `--concurrent-processes` removed. |

---

## Next steps

These are the remaining known todos, roughly in priority order:

### CLI rendering for concurrent jobs ✓
The existing `CliProgressReporter` was written for sequential album processing. With true
concurrent fan-out, progress bars from different albums interleave and can overflow the terminal.
This needs to be addressed before concurrent mode is usable in practice.

### ~~Per-job cancellation (`job.Cts`)~~ ✓
`Job.Cts` and `Job.Cancel()` are implemented. Each job's CTS is tree-linked: `ProcessJob` accepts
a `parentToken` parameter and sets `job.Cts = CreateLinkedTokenSource(appCts.Token, parentToken)`.
`JobList` passes `jl.Cts.Token` to each child in the fan-out. `ExtractJob` passes `parentToken`
(not its own token) when recursing into its `Result` — the `Result` is a sibling in the hierarchy,
not a child. Remaining step:
- The CLI wires a keyboard handler that presents a numbered list of running jobs and calls
  `job.Cancel()` on the chosen one.

### ~~`Searching` and `Downloading` states not yet set~~ ✓
`song.State = JobState.Searching` is set before the search block in `SearchAndDownloadSong`, and
`song.State = JobState.Downloading` is set before each `DownloadFile` call.

### ~~`ReportExtractionStarted` / `ReportExtractionCompleted` not yet called~~ ✓
Added to `IProgressReporter` (no-op in all implementations); called at the start of extraction
and immediately after `ej.State = Done` in the `ExtractJob` branch of `ProcessJob`.

### ~~`ReportJobStarted(parallel: false)` hardcoded~~ ✓
`parallel` parameter removed entirely from `ProcessJob`, `ProcessLeafJob`, and `ReportJobStarted`
— the distinction was meaningless since all fan-outs use `Task.WhenAll`.

---

## Resolved decisions

- **`SongJob` is public** — consumers hold references and observe it directly. It is not
  engine-internal.
- **`ResolvedTarget` replaces the query/download split** — same object throughout, phase determined
  by whether `ResolvedTarget` is set.
- **`UpgradeToAlbumMode` is a method on `JobList`, called by the engine** — applied after the
  extractor result is added to `Queue`. Extractors are faithful mappers and do not interpret
  `album`/`aggregate` flags.
- **`DownloadEngine.Queue` is a persistent `JobList`** — initialized at construction, never
  replaced. Extractors return `Job` (singular), the engine adds it with `Queue.Jobs.Add(result)`.
  This enables the persistent-service use case (enqueue new downloads at any time) and gives the
  concurrency tree-walker a stable root fan-out point.
- **Profile resolution is per-leaf, siblings are independent** — profiles are re-evaluated at each
  leaf job using `input-type` from the ancestor `ExtractJob` and `download-mode` from the leaf
  type.
- **`ExtractJob.Result` preserves history** — the engine sets `Result` after extraction and recurses
  into it; the `ExtractJob` itself stays in the tree as a historical record. No queue replacement.
- **`IExtractor.GetTracks` returns `Job`** — the extractor decides the shape (single `AlbumJob`,
  `JobList` of `SongJob`s, `JobList` of `ExtractJob`s, etc.). The engine does not wrap the result.
- **Concurrency is tree-wide, not list-local** — semaphores count across the whole tree. `JobList`
  is the fan-out point; semaphores are acquired inside the leaf processors and inside `Searcher`,
  not held by the list itself.
- **`SelectAlbumVersion`** callback: `Func<AlbumJob, Task<AlbumFolder?>>` — `JobContext` not
  exposed to the consumer. `InteractiveModeManager` is wired by the CLI, not the engine.
- **`TrackState` is removed** — merged into `JobState` on the job itself. Index file format
  unchanged (values 0–4 preserved).
- **`AlbumFile` is removed** — `AlbumFolder.Files` becomes `List<SongJob>`; `IsNotAudio` is a
  computed property on `SongJob` based on the file extension of `ResolvedTarget?.Filename`.
- **Fast-search is engine-internal, coordinator-owned** — `SearchAndDownloadSong` runs search and
  provisional download concurrently. `searchCts` is created and cancelled inside this function.
  `Downloader.DownloadFile` has no knowledge of `searchCts`.
- **Search concurrency uses two composing limiters** — both live inside `Searcher`. Rate limiter:
  `RateLimitedSemaphore`. Concurrency limiter: `SemaphoreSlim concurrencySemaphore`. Both wrap
  `RunSearches`. `SearchDirectLinkAlbum` is exempt. Download concurrency is unlimited.
- **`--concurrent-processes` removed** — its search-limiting role is superseded by
  `--concurrent-searches`. Downloads have no engine-level concurrency cap.
- **`extractor` is not a field** — threaded as a `ProcessJob` parameter to avoid data races under
  concurrent fan-out.
- **`_contexts` is `ConcurrentDictionary`** — written from concurrent tasks during fan-out.
