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
├── ExtractJob         { string Input; InputType? Type }
├── JobList            { List<Job> Jobs; string? Name }
├── SongJob            { SongQuery Query; FileCandidate? ResolvedTarget; ... }
├── AlbumJob           { AlbumQuery Query; AlbumFolder? ResolvedTarget; ... }
├── AggregateJob       { SongQuery Query; List<SongJob>? Results }
└── AlbumAggregateJob  { AlbumQuery Query }
```

**`ExtractJob`** is a new type. It holds an input string (URL, file path, soulseek URI, etc.) and
an optional `InputType`. The engine runs the appropriate extractor on it and replaces it in the
queue with a `JobList` of the extracted jobs.

**`JobList`** is the universal grouping container. It holds any mix of `Job` subtypes. It has a
`Name` (playlist name, artist name, filename, etc.) and accumulates completion state from its
children. The old `SongListQueryJob`, `AlbumListJob`, `JobQueue` all collapse into this.

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

```
User submits: ExtractJob("list.txt")

Engine:
  → runs ListExtractor on "list.txt"
  → produces JobList [
        ExtractJob("https://open.spotify.com/playlist/..."),
        ExtractJob("album://Pink Floyd - The Wall"),
        ExtractJob("slsk://user/Music/song.mp3"),
    ]

  → runs SpotifyExtractor on first ExtractJob
  → produces JobList("My Playlist") [SongJob, SongJob, ...]

  → runs StringExtractor on second
  → produces AlbumJob(query)

  → runs SoulseekExtractor on third
  → produces SongJob(resolved)   ← ResolvedTarget pre-set, no search needed
```

The engine processes `ExtractJob`s concurrently (within a `JobList`) — while Spotify is fetching,
Bandcamp can fetch in parallel. This is the main concurrency improvement this design enables.

`IExtractor.GetTracks` returns `List<Job>` (was `List<QueryJob>`). Extractors can return any mix —
`SongJob`, `AlbumJob`, `JobList`, even nested `ExtractJob`s. The engine wraps the result in a
`JobList` and attaches it to the parent.

`RemoveTrackFromSource` stays on `IExtractor`. The engine calls it after a `SongJob` completes
inside a `JobList` that was produced by that extractor.

---

## 4. `album` / `aggregate` flags

Extractors are faithful mappers — they return whatever they found, typed correctly. They do not
interpret `album`/`aggregate`. The upgrade is a user-level semantic decision, not an extraction
concern.

`UpgradeToAlbumMode` moves into the engine as a post-extraction transform applied to the `JobList`
result of each `ExtractJob`, before its children are processed:

```
ExtractJob resolved → JobList [SongJob, SongJob, ...] → engine upgrades → JobList [AlbumJob, AlbumJob, ...]
```

One place, no extractor coupling. Extractors that already return albums (Bandcamp, MusicBrainz)
are unaffected — upgrading `AlbumJob → AlbumJob` is a no-op.

---

## 5. `JobList` as the queue

`JobQueue` is removed. The engine holds a single root `JobList`. `JobPreparer` walks the tree to
set up `Config`, `JobContext`, editors, and skippers per job — the tree walk replaces the flat loop.

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

`AggregateJob` searches and populates `Results: List<SongJob>`. Each `SongJob` in `Results` has
`ResolvedTarget` pre-set (aggregate results are already resolved files). The engine processes them
as a batch download without a second search round — same as today, just cleaner typing.

---

## 10. State enums

`TrackState` is removed. `JobState` is extended:

```csharp
public enum JobState
{
    Pending     = 0,
    Extracting  = 1,   // new — ExtractJob running
    Searching   = 2,
    Downloading = 3,
    Done        = 4,
    Failed      = 5,
    Skipped     = 6,
    AlreadyExists = 7, // was TrackState.AlreadyExists
    NotFoundLastTime = 8,  // was TrackState.NotFoundLastTime
}
```

`AlbumFile` is removed — its state is carried by `SongJob.State` directly.

---

## 11. `IProgressReporter`

The structured events become the primary API (machine-readable, sufficient for both CLI and GUI):

```csharp
// Extraction
void ReportExtractionStarted(ExtractJob job);
void ReportExtractionCompleted(ExtractJob job, JobList result);

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
`JobQueue.Jobs` to a tree walk, with config flowing top-down:

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

## Current code status

The codebase currently has a partial refactor in place:
- `QueryJob`/`DownloadJob` split exists but is being replaced
- `AlbumQueryJob`, `AlbumDownloadJob`, `SongListQueryJob`, `SongQueryJob`, `SongDownloadJob`,
  `AlbumListJob`, `AggregateQueryJob`, `AlbumAggregateQueryJob` all exist but will be removed
- `SongJob` exists as a non-`Job` class — needs to become a `Job` subclass
- `JobQueue` exists — will be replaced by `JobList` as the root container
- `IExtractor.GetTracks` returns `List<QueryJob>` — will change to `List<Job>`

---

## Implementation order

| Step | Description |
|------|-------------|
| 1 | Introduce `ExtractJob` and `JobList`. Keep old types alongside. Update engine to handle them. |
| 2 | Collapse `SongJob` into `Job` hierarchy. Remove `SongQueryJob`, `SongDownloadJob`. |
| 3 | Collapse `AlbumQueryJob` + `AlbumDownloadJob` → `AlbumJob` with `ResolvedTarget`. |
| 4 | Collapse `AggregateQueryJob` → `AggregateJob`, `AlbumAggregateQueryJob` → `AlbumAggregateJob`. |
| 5 | Collapse `SongListQueryJob` + `AlbumListJob` → `JobList`. Remove `JobQueue`. |
| 6 | Update `IExtractor.GetTracks` → `List<Job>`. Update all extractors. |
| 7 | Update `JobPreparer` to tree-walk instead of flat loop. |
| 8 | Remove `TrackState`. Extend `JobState`. |
| 9 | Update `IProgressReporter` signatures. |
| 10 | Update `Preprocessor`, `TrackSkipper`, `FileManager`, `OnCompleteExecutor` for new types. |
| 11 | Enable concurrent `ExtractJob` processing within a `JobList`. |

Steps 1–5 are the structural core and should be done together in one pass. Steps 6–10 are
cleanup. Step 11 is the concurrency payoff and can be a separate commit.

---

## Resolved decisions

- **`SongJob` is public** — consumers hold references and observe it directly. It is not
  engine-internal.
- **`ResolvedTarget` replaces the query/download split** — same object throughout, phase determined
  by whether `ResolvedTarget` is set.
- **`UpgradeToAlbumMode` stays, moves to engine** — applied as a post-extraction transform on the
  `JobList` result of each `ExtractJob`. Extractors are faithful mappers and do not interpret
  `album`/`aggregate` flags.
- **`JobQueue` is replaced by `JobList`** — the root `JobList` is the engine's queue.
- **Profile resolution is per-leaf, siblings are independent** — profiles are re-evaluated at each
  leaf job using `input-type` from the ancestor `ExtractJob` and `download-mode` from the leaf
  type. The current sequential accumulation across jobs is accidental and removed.
- **`ExtractJob` enables concurrent extraction** — the engine can run multiple extractors in
  parallel when they are siblings in a `JobList`.
- **`SelectAlbumVersion`** callback: `Func<AlbumJob, Task<AlbumFolder?>>` — `JobContext` not
  exposed to the consumer.
- **`TrackState` is removed** — merged into `JobState` on the job itself.
- **`AlbumFile` is removed** — `AlbumFolder.Files` becomes `List<SongJob>`; `IsNotAudio` is a
  computed property on `SongJob` based on the file extension of `ResolvedTarget?.Filename`.
  `DownloadAlbumFile` merges with `DownloadSong`.
- **`InteractiveModeManager`** is wired in by the CLI as the `SelectAlbumVersion` implementation.
  The engine has no direct dependency on it.
