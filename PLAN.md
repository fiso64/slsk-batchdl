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
inside a `JobList` that was produced by that extractor.

---

## 4. `album` / `aggregate` flags

Most extractors are faithful mappers and do not interpret `album`/`aggregate` — the upgrade is a
user-level semantic decision applied after extraction. Exception: the String extractor reads
`config.album` at extraction time and returns `AlbumJob` directly when set, since it is building
a query from scratch and can choose the right type immediately.

For all other extractors, `UpgradeToAlbumMode` is an engine-level transform. It is a standalone
function `UpgradeToAlbumMode(Job job, bool album, bool aggregate) → Job` that handles any shape:

- `JobList` → upgrades each child in-place (e.g. Spotify/YouTube playlist `SongJob`s → `AlbumJob`s)
- bare `SongJob` → returns `AlbumJob` / `AggregateJob` / `AlbumAggregateJob` as appropriate
- bare `AlbumJob` → returns `AlbumAggregateJob` if aggregate, otherwise unchanged
- anything else → returned as-is

In practice `UpgradeToAlbumMode` is only ever called on `JobList` results (playlist extractors).
The generic signature handles edge cases cleanly without special-casing.

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

`SongJob` currently lives outside the `Job` hierarchy (it's `INotifyPropertyChanged` but not `Job`).
It becomes a full `Job` subclass. This means:

- `ItemNumber`, `LineNumber`, `FailureReason`, `Config` all move onto the base `Job` — no more
  separate mirroring on `SongJob`
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

Both become engine callbacks, no longer baked into the engine logic:

```csharp
// Called after album search completes. Return chosen folder or null to skip.
public Func<AlbumJob, Task<AlbumFolder?>>? SelectAlbumVersion { get; set; }
```

The `interactiveMode` config flag is consumed by the CLI which wires in `InteractiveModeManager`
as the `SelectAlbumVersion` implementation. Fast-search is an engine-internal optimization that
fires early candidates at the searcher — no public API change needed.

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
The current behaviour where profiles accumulate sequentially across jobs is accidental and is
deliberately removed.

**Editors and skippers:**
`JobList`-level editors and skippers are shared across all children in the list (same as current
`SongListQueryJob` model). Each `ExtractJob` may get its own editor if it has a distinct
`indexFilePath` after profile resolution.

---

## Concurrency

The engine's main loop becomes a recursive tree-walker:

```
ProcessJob(Job job):
    ExtractJob ej  →  ej.Result = extractor.GetTracks(ej.Input)
                      ProcessJob(ej.Result)               // recurses — uniform, no special-casing

    JobList jl     →  foreach job in jl.Jobs: ProcessJob(job)   // fan-out point

    AlbumJob aj    →  SearchAlbum(aj) → DownloadAlbum(aj)

    SongJob sj     →  SearchAndDownload(sj)
```

Concurrency applies **uniformly** at every `ProcessJob` call — there is no distinction between
the root `JobList`, a nested `JobList` inside an `ExtractJob.Result`, or any other level. The
`JobList` branch is the fan-out point regardless of how deep in the tree it sits. An
`ExtractJob.Result` that is a `JobList` gets the same concurrent fan-out as the root.

When concurrency is enabled, the `JobList` branch becomes `await Task.WhenAll(...)` over its
children, limited by semaphores. Three independent semaphores are envisioned:
- **Extractor concurrency** — limits simultaneous `ExtractJob` runs (network API calls)
- **Album job concurrency** — limits simultaneous album search+download cycles
- **Song job concurrency** — limits simultaneous song search+download cycles (already exists as `concurrentProcesses`)

These semaphores count across the entire tree, not per-list, so the total resource usage is bounded
globally. For now the loop remains sequential (step 11 adds the fan-out).

**CLI rendering:** The existing `CliProgressReporter` already handles concurrent song downloads
(progress bars per `SongJob`, updated in-place via Konsole) and concurrent album searches
(`_jobBars` per job). The infrastructure is largely there. However, rendering multiple
simultaneously active albums (each with its own block of per-song progress bars) will likely
have issues: bars from different albums may interleave unexpectedly, and many active bars can
break when they overflow the visible terminal area. Fixing this is a separate concern from the
structural refactor and is deferred to a later step alongside step 11.

The current `parallelAlbumSearch` feature (search-only parallelism) is removed (see breaking
changes) — it is superseded by the full parallel search+download the new architecture enables.

---

## Breaking changes

| Change | Notes |
|--------|-------|
| `{state}` name-format / on-complete variable for songs: `"Downloaded"` → `"Done"` | `JobState` replaces `TrackState` on `SongJob` |
| `--parallel-album-search` / `parallelAlbumSearch` removed | Superseded by full parallel search+download (step 11); CLI rendering deferred |

Index file format is **unchanged** — `JobState` values 0–4 are aligned with the old `TrackState`
values so existing index files remain readable.

---

## Current code status

Steps 1–10 are complete with the following caveats:
- `ExtractJob` class exists but lacks the `Result: Job?` field and the engine does not yet process
  `ExtractJob` nodes recursively — it still calls `extractor.GetTracks` directly and adds results
  to `Queue`. The full `ExtractJob`-based pipeline (CLI submits `ExtractJob`, engine recurses) is
  part of step 11.
- `ListExtractor` still eagerly resolves each line by calling sub-extractors directly, rather than
  returning a `JobList` of `ExtractJob`s. This is intentional — deferred to step 11.

---

## Implementation order

| Step | Description |
|------|-------------|
| 1 | ✓ Introduce `ExtractJob` and `JobList` types. |
| 2 | ✓ Collapse `SongJob` into `Job` hierarchy. Remove `SongQueryJob`, `SongDownloadJob`. |
| 3 | ✓ Collapse `AlbumQueryJob` + `AlbumDownloadJob` → `AlbumJob` with `ResolvedTarget`. Remove `AlbumFile`. |
| 4 | ✓ Rename `AggregateQueryJob` → `AggregateJob`, `AlbumAggregateQueryJob` → `AlbumAggregateJob`. Remove `QueryJob`, `DownloadJob`. |
| 5 | ✓ Collapse `SongListQueryJob` + `AlbumListJob` → `JobList`. Remove `JobQueue`. |
| 6 | ✓ `IExtractor.GetTracks` → `Job` (singular). Engine: `Queue.Jobs.Add(result)`. Remove `parallelAlbumSearch`. |
| 7 | ✓ Update `JobPreparer` to tree-walk instead of flat loop. |
| 8 | ✓ Remove `TrackState`. Extend `JobState` with `Skipped`, `Extracting`, `Searching`, `Downloading`. |
| 9 | ✓ Update `IProgressReporter` signatures. |
| 10 | ✓ Update `Preprocessor`, `TrackSkipper`, `FileManager`, `OnCompleteExecutor` for new types. |
| 11 | Add `ExtractJob.Result`, recursive engine processing, and concurrency. | <-- Stop and discuss first!

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
  type. The current sequential accumulation across jobs is accidental and removed.
- **`ExtractJob.Result` preserves history** — the engine sets `Result` after extraction and recurses
  into it; the `ExtractJob` itself stays in the tree as a historical record. No queue replacement.
- **`IExtractor.GetTracks` returns `Job`** — the extractor decides the shape (single `AlbumJob`,
  `JobList` of `SongJob`s, `JobList` of `ExtractJob`s, etc.). The engine does not wrap the result.
- **Concurrency is tree-wide, not list-local** — three semaphores (extractor / album / song) count
  across the whole tree. `JobList` is the fan-out point; semaphores are acquired inside the leaf
  processors (`ProcessAlbumJob`, `ProcessSongJob`), not held by the list itself.
- **CLI interactive mode is compatible with album concurrency** — album search runs concurrently;
  interactive selection is a sequential gate (FIFO queue of completed searches awaiting user input);
  download resumes concurrently after selection. The user works through prompts one at a time while
  searching and downloading proceed in parallel around them.
- **`parallelAlbumSearch` is removed** — superseded by full parallel search+download (step 11).
  CLI rendering of concurrent albums is deferred; the existing bar-per-job infrastructure handles
  it partially but bar interleaving and overflow need separate work.
- **`SelectAlbumVersion`** callback: `Func<AlbumJob, Task<AlbumFolder?>>` — `JobContext` not
  exposed to the consumer.
- **`TrackState` is removed** — merged into `JobState` on the job itself. Index file format
  unchanged (values 0–4 preserved).
- **`AlbumFile` is removed** — `AlbumFolder.Files` becomes `List<SongJob>`; `IsNotAudio` is a
  computed property on `SongJob` based on the file extension of `ResolvedTarget?.Filename`.
  `DownloadAlbumFile` merges with `DownloadSong`.
- **`InteractiveModeManager`** is wired in by the CLI as the `SelectAlbumVersion` implementation.
  The engine has no direct dependency on it.
