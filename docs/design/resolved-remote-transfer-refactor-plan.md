# Sockseek v4 resolved remote transfer refactor

Status: implemented and verified

Target: Sockseek v4, before user-browse download work

Scope: exact peer-file identity, search-result composition, resolved directory
transfer plans, direct file/directory jobs, and shared download execution

Source review: 2026-08-11

Implementation progress (2026-08-12):

- Migration stages 1-9 are implemented. Production direct links no longer
  fabricate Soulseek search objects; exact file identity, search evidence,
  directory snapshots/plans, lifecycle bases, remote subtypes, shared runners,
  settings applicability, API payloads, persistence, and CLI presentation use
  the model below.
- Neutral/default mechanisms have unqualified names (`PlacementPlanner`,
  `FilePlacement`, `NameFormatVariableProvider`, and `NameFormatContext`). Only
  semantic specializations use qualifiers such as `Album*`, `Song*`, or
  `Music*`. `ExtractionMode.General` remains an input-intent value, not a type or
  service naming convention.
- `DirectoryDownloadJob` owns all current children for generic progress,
  cancellation, snapshot, and terminal traversal. An immutable attempt still
  owns exactly one planned child per entry; a specialization can explicitly add
  post-plan work (currently album art) without an album branch in generic
  orchestration.
- Search candidates now compose `FileMetadataDto`, including bit depth, instead
  of publishing flat duplicate file-fact fields. The future browse projection
  can reuse that leaf without reusing search identity or evidence.
- The directory allocation benchmark measured approximately 1.21 KiB per
  ordinary entry/child graph (122.25 KiB at 100, 1,207.65 KiB at 1,000, and
  12,087.95 KiB at 10,000 on 64-bit .NET 10). Admission conservatively budgets
  1.5 KiB plus strings/attributes per entry, with limits of 20,000 files, 2 TiB
  known bytes, and 128 MiB estimated retained memory. The default policy is
  applied when an attempt is created, before a pre-resolved job can be
  registered or any child becomes visible.
- Read-only `FileCandidate` convenience properties remain as projections of its
  single authoritative target/evidence composition; the obsolete constructor,
  duplicate transfer authority, old album booleans, and `PeerUsername.Normalize`
  use in exact identity paths are removed.
- Soulseek links expose `generic-file`/`generic-directory` auto-profile modes before
  extraction. Explicit incompatible remote name formats fail at submission;
  incompatible inherited formats fall back to empty ordinary placement only
  after a matching auto profile has had the opportunity to replace them.
- Focused Core, Server, Persistence, OpenAPI, and CLI tests have been added. The
  clean solution build and all four full test suites pass: Core 737,
  Persistence 52, Server 125, and CLI 257 (1,171 total). `git diff --check` is
  clean.

## Intentional behavior and compatibility changes

This refactor is not behavior-neutral for unqualified Soulseek links. The old
extractor treated every direct file as a music track and every trailing-slash
directory as an album. The new default reflects Soulseek's general file-sharing
semantics:

| Input | Before this refactor | After this refactor |
| --- | --- | --- |
| `slsk://peer/path/file.ext` with no requested mode | A `SongJob` containing fabricated search objects | An exact `RemoteFileJob`; no search, music fallback, tag-derived naming, playlist/index work, or album handling |
| `slsk://peer/path/folder/` with no requested mode | An `AlbumJob` | A `RemoteDirectoryJob` that retrieves the directory once, applies admission limits, and preserves the returned tree without inferring an album |
| File link with explicit `Song` intent (`--song`) | A preselected `SongJob` represented as a fake search result | A preselected `SongJob` with an exact target and no fake search evidence; music behavior remains enabled |
| Directory link with explicit `Song` intent | The trailing slash won and produced an `AlbumJob` | Rejected because a directory cannot represent one music track |
| Link with explicit `Album` intent (`--album`) | An `AlbumJob` using the supplied path as the selected directory | Still an `AlbumJob` using that path as the selected directory; album validation, naming, art, and finalization remain enabled |

For the default interpretation, the trailing slash is therefore significant: a
path without it is an exact file target, while a path with it is an exact
directory source. The new `ExtractionMode.General` value is the explicit form
of the same ordinary remote interpretation. There is currently no separate `--general` CLI
switch; the mode is available through the settings/API contracts used by remote
submission flows. It is not a global reinterpretation switch: non-Soulseek
extractors retain their source-defined behavior, and ambiguous string input
rejects `General` rather than silently treating a search query as a remote file.

Ordinary remote output also has deliberately different semantics from music
output. With an empty `NameFormat`, an exact file keeps its remote leaf name and
a directory keeps `<selection root>/<relative tree>/<remote leaf>`. A configured
format may use common structural variables, but music-only query/tag variables
are rejected when explicitly supplied for that remote submission. An incompatible
format inherited from global, default, or selected-profile configuration instead
falls back to the empty ordinary format: a file keeps its leaf name and a
directory keeps its tree. Auto profiles run before this fallback, and Soulseek
links expose `download-mode == "generic-file"` or `"generic-directory"` before
extraction, so a matching auto profile can replace a global music format with a
compatible structural format. Explicit search, fallback, playlist/index,
album-art, and incomplete-album settings on an ordinary remote submission are
rejected rather than ignored; other unrelated inherited music defaults are not
projected into its executor. Common transfer, output-parent, structural naming,
and generic on-complete behavior still apply.

Peer identity handling changes at the same protocol boundary. Usernames are now
validated but retain their exact spelling; the old code trimmed, NFC-normalized,
and uppercased them. Remote paths likewise retain their supplied spelling, and
the migrated exact-identity/directory paths use ordinal rather than
case-insensitive comparison. Chat, peer-policy, upload, and direct-link paths now
follow Soulseek/slskd-style exact username handling. This is a compatibility
change for configurations that relied on the former normalization; for example,
a blocked username must now use the exact spelling.

The public job contract also adds `remote-file` and `remote-directory` payload
kinds and exposes shared file/directory lifecycle state. Search candidates now
contain one composed `FileMetadataDto` instead of duplicating those metadata
fields at the candidate's top level. API consumers must handle those new
discriminators and the candidate shape; persisted records now encode the new job
kinds and shared lifecycle state.

This refactor establishes one Sockseek-owned representation and execution path for
files selected from Soulseek. Album search, direct `slsk://` inputs, and user
browsing may discover files differently, but after discovery they must converge on
the same exact target and transfer primitives.

The refactor preserves explicit song and album runtime behavior while making the
ordinary remote-download path first-class. It is a prerequisite for the download portion of
[`user-browsing-design.md`](user-browsing-design.md), not an implementation of
remote profiles or browse acquisition.

## Problem statement

The current types combine concerns that no longer have the same lifecycle:

- `FileCandidate` is both an exact peer file and a search response. Constructing a
  direct `slsk://` target requires fake `SearchResponse` and `Soulseek.File`
  values, including sentinel sizes and speeds.
- `AlbumFolder` is both a peer directory snapshot and album-search evidence. It
  contains exact directory/file data alongside search counts, audio lengths,
  quality coverage, and sorter state.
- `RetrieveFolderJob` requires an `AlbumFolder`, although retrieving an exact peer
  directory is not inherently an album operation.
- `SongJob` combines song discovery with a resolved file transfer.
- `AlbumJob` combines album discovery, candidate selection, optional directory
  retrieval, resolved child transfers, retry across peers, and album finalization.
- `FileManager`, output settings, and completion paths contain music assumptions
  and concrete `SongJob`/`AlbumJob` checks. Reusing them unchanged for arbitrary
  files would either apply music behavior accidentally or accumulate branches that
  silently ignore most configured settings.
- The flags `AllowBrowseResolvedTarget`,
  `SkipResolvedTargetTrackCountVerification`, and
  `ResolvedTargetNeedsInitialFolderRetrieval` encode different workflows inside
  one album-shaped state machine.

User browsing would make this worse if it added unrelated remote-file models and
a second directory executor. The opposite extreme is also harmful: one concrete
job with a `music` flag would mix search, arbitrary-tree preservation, tag naming,
album images, fallback providers, and incompatible settings.

The durable model needs two layers. Abstract file/directory jobs own lifecycle and
observable state. Semantic subclasses own how a target or plan is resolved and
finalized. Exact targets, immutable plans, and transfer runners are shared below
both layers.

## Goals and acceptance criteria

The refactor is complete when:

1. Exact peer-file downloads can be represented without a search response,
   `SongQuery`, or Soulseek.NET object in the durable Core model.
2. `FileCandidate` composes an exact peer-file target with search-only evidence
   rather than owning both concepts implicitly.
3. Album candidate search evidence is separated from its peer-directory identity
   and snapshot; exact folder retrieval no longer requires an album model.
4. `SongJob` and exact `RemoteFileJob` share a small `FileDownloadJob` lifecycle
   base and the same exact-peer transfer runner without sharing discovery or
   finalization policy.
5. `AlbumJob` and `RemoteDirectoryJob` share a
   `DirectoryDownloadJob` lifecycle base. Full directory retrieval is part of the
   job when its explicit source requires it; an already-resolved source performs
   no second retrieval.
6. Every resolved directory attempt owns an immutable `DirectoryTransferPlan`.
   An album may resolve another candidate on a later attempt, but an individual
   plan is never mutated or retargeted.
7. Neutral transfer mechanics and music-focused planning, settings, and
   completion remain separate services. Shared runners never branch on a
   semantic-mode flag.
8. Existing song/album discovery, manual selection, retries, output placement,
   album art, playlists, tags, incomplete-album handling, cancellation, live
   monitoring, and persistence behavior remain covered by regression tests.
9. Ordinary direct Soulseek file and directory links no longer need fake search
   candidates or album-only control flags.
10. The user-browse implementation can map leased artifact rows into the shared
   target/plan types without depending on album search models.
11. A future explicit "download as track/album" action can reuse the same peer
    target or directory snapshot without teaching an ordinary remote job about
    music.

## Non-goals

- Do not make a peer directory imply an album.
- Do not merge album search DTOs with hierarchical browse DTOs.
- Do not add profile or browse acquisition APIs in this refactor.
- Do not replace the existing job engine, transfer queue, connection, workflow
  snapshots, or persistence subsystem.
- Do not put queries, album candidates, tags, images, or fallback providers on the
  common job bases.
- Do not split every existing settings class merely to achieve structural purity;
  introduce typed execution-policy projections and explicit applicability first.
- Do not persist disposable browse artifacts in the historical job database.
- Do not expose exact peer wire paths from the future browse listing API.

## Design rules

1. Search evidence wraps exact peer targets; exact targets never depend on search
   evidence.
2. A transfer plan is immutable and self-contained before its transfer attempt is
   registered. Exactness belongs to the target/plan, not necessarily to the
   higher-level job before resolution.
3. Job inheritance models stable lifecycle and cardinality only: one output file
   versus a directory-shaped set of files. Semantic behavior stays in subtype
   data and collaborating services, not virtual methods on mutable job entities.
4. Resolution is explicit state, not a collection of nullable targets and boolean
   flags. A resolved-plan source and a peer-directory source are different union
   cases and are persisted as such.
5. `AlbumJob : DirectoryDownloadJob` and `SongJob : FileDownloadJob` are valid
   because the abstract bases do not promise a resolved peer target at
   construction. Ordinary remote jobs are sibling semantic subtypes.
6. Shared execution is implemented by composition. Executors accept exact targets,
   plans, destinations, and child state; they do not branch on a synthetic
   semantic mode.
7. Ordinary remote and music interpretation is selected at the
   request/extraction boundary by choosing a job subtype. It is never stored on
   `PeerFileTarget` or `DirectoryTransferPlan`.
8. Public DTO reuse follows shared semantics, not matching property names. Search
   candidates and browse entries remain different resource projections.
9. Soulseek identity is exact wire data. Username and path validation must not
   trim, case-fold, Unicode-normalize, or substitute display spelling for wire
   spelling. Comparison behavior is defined explicitly at each protocol boundary.
10. No Core transfer value contains `SearchResponse`, `Soulseek.File`, API DTOs, or
   Server types.
11. Neutral/default mechanisms use unqualified names such as `PlacementPlanner`.
   Qualifiers belong on semantic specializations such as music, album, or song;
   `General*` is not used as a prefix for the ordinary mechanism.

## Stable conceptual model

Four concerns vary independently and must not be collapsed into one mode enum:

| Job | Interpretation | Initial resolution | Specialized behavior |
| --- | --- | --- | --- |
| `RemoteFileJob` | Remote file | Exact `PeerFileTarget` | Preserve file identity and explicit destination |
| `SongJob` | Music track | Query/candidates or explicit target | Ranking, fallback, music naming and metadata |
| `RemoteDirectoryJob` | Remote directory/tree | Peer directory identity or resolved plan | Preserve the selected tree by default, or apply structural name formatting; resolve collisions deterministically |
| `AlbumJob` | Music album | Album query/candidates or explicit directory | Album validation, roles, images, organization and retry |

The two abstract bases provide shared observable shape:

```text
Job
├─ FileDownloadJob
│  ├─ RemoteFileJob       ordinary exact peer file
│  └─ SongJob             music-focused discovery/fallback
└─ DirectoryDownloadJob
   ├─ RemoteDirectoryJob  ordinary remote directory or resolved tree
   └─ AlbumJob            music-focused directory discovery
```

This is not a template-method hierarchy. Jobs remain state containers. Resolution,
planning, transfer, placement, and finalization are services selected by the
orchestrator for the concrete subtype. Consequently, adding another semantic
download later does not require adding another branch to the shared transfer
runner.

## Core model

### Exact peer-file target

Add a Sockseek-owned immutable value. The exact namespace and record/class choice
may follow existing Core conventions, but its semantic shape is:

```csharp
public sealed record PeerFileIdentity(
    string Username,
    string Filename);

public sealed record PeerFileTarget(
    PeerFileIdentity Identity,
    long? Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<FileAttributeSnapshot>? Attributes);
```

`PeerFileIdentity` is the only equality/deduplication key: `Username` and
`Filename` retain the exact bounded protocol spelling. Target metadata does not
participate in remote identity. Construction copies attribute data into
Sockseek-owned immutable values. Unknown metadata uses nullable fields or the
established protocol sentinel only at the adapter boundary; the target does not
manufacture a search response to carry it.

Identity/target validation is shared by search projection, direct-link extraction,
album folder retrieval, persisted candidate restoration, and later browse-artifact
materialization. It validates well-formed bounded input without changing wire
identity. Safe display and filesystem components are derived separately.

The existing `PeerUsername.Normalize` is not used for outbound identity because
it trims, normalizes, and uppercases. The username correction already required by
the user-browse design should introduce or rename a validator whose return value
preserves the input exactly, then migrate chat, peer policy, and uploads with
focused compatibility tests.

### Search candidate

Refactor `FileCandidate` to compose the exact target:

```csharp
public sealed class FileCandidate
{
    public PeerFileTarget Target { get; }
    public SearchPeerSnapshot Peer { get; }
    public FileSearchEvidence Evidence { get; }
}
```

`SearchPeerSnapshot` owns response-time facts used for presentation and sorting,
such as free-slot state, upload speed, and response file count.
`FileSearchEvidence` owns any sequence/revision/ranking inputs that are not facts
about the remote file itself.

Temporary delegating properties such as `Username`, `Filename`, `Size`, and
`Length` may remain during migration to keep individual commits reviewable. They
must delegate to `Target`; there must be one authoritative value. Persistence and
API projection restore the same composition rather than reconstructing Soulseek.NET
objects.

### Peer directory and album evidence

Split the current `AlbumFolder` responsibilities:

```csharp
public sealed record PeerDirectoryIdentity(
    string Username,
    string FolderPath);

public sealed record PeerDirectorySnapshot(
    PeerDirectoryIdentity Identity,
    IReadOnlyList<PeerFileTarget> Files,
    bool IsComplete);

public sealed class AlbumDirectoryCandidate
{
    public PeerDirectorySnapshot Directory { get; }
    public IReadOnlyList<AlbumFileMatch> Matches { get; }
    public AlbumSearchEvidence Evidence { get; }
}
```

`AlbumSearchEvidence` contains the existing search-result file/audio counts,
sorted audio lengths, representative filename, quality coverage, and aggregate
sort entry. `AlbumFileMatch` associates a `SongQuery` or lazy query inference with
one target/candidate; that query is not placed on `PeerFileTarget`.

During a staged migration, `AlbumFolder` may be retained as a compatibility
facade over `PeerDirectorySnapshot` plus `AlbumSearchEvidence`. The final Core
model must make it impossible for a generic directory transfer to depend on
album-search evidence.

Refactor `RetrieveFolderJob` to require `PeerDirectoryIdentity` and produce a
`PeerDirectorySnapshot`. Album search retrieval associates that snapshot back with
its `AlbumDirectoryCandidate`; the retrieval job itself has no album query,
ranking, or conditions. Search-scoped API actions may keep their album-candidate
reference at the Server boundary while using the generic Core retrieval target.

### Directory transfer plan

An immutable plan describes the resolved selection and its logical relative tree;
it does not decide music naming or the final local destination:

```csharp
public sealed record DirectoryTransferEntry(
    PeerFileTarget Target,
    IReadOnlyList<string> RelativeDirectoryComponents);

public sealed record DirectoryTransferPlan(
    string DisplayRoot,
    IReadOnlyList<DirectoryTransferEntry> Entries);
```

The real implementation also records a stable target identity used for duplicate
detection. A plan:

- contains files from one exact peer identity;
- preserves every exact wire filename in its targets;
- contains only server/Core-derived logical relative components;
- rejects rooted paths, empty components, `.`/`..`, controls, and containment
  escapes before workflow registration;
- has deterministic ordering and duplicate handling, but leaves filesystem
  sanitization and collision suffixes to the selected placement planner;
- owns copied values and remains usable after a search snapshot or browse artifact
  is evicted; and
- is admitted against measured file-count, byte, and engine-memory limits before
  any child job becomes visible.

`DisplayRoot` is presentation and logical tree intent, not protocol identity or a
pre-approved local path. A synthetic browse root is valid because each entry still
contains one exact wire filename. `PlacementPlanner` preserves this tree by
default, or renders the configured shared name format from structural variables.
Music planning uses the same format engine with additional query/tag
variables. Neither changes the plan or target.

## Job model

### File download lifecycle

Extract only stable one-file state into an abstract base:

```csharp
public abstract class FileDownloadJob : Job
{
    public string? DownloadPath { get; protected set; }
    public long BytesTransferred { get; protected set; }
    public long? FileSize { get; protected set; }
}

public sealed class RemoteFileJob : FileDownloadJob
{
    public PeerFileTarget Target { get; }
    public RelativeOutputPath OutputPath { get; }
}

public class SongJob : FileDownloadJob, IUpgradeable
{
    public SongQuery Query { get; }
    public IReadOnlyList<FileCandidate>? Candidates { get; }
    public PeerFileTarget? ResolvedPeerTarget { get; }
}
```

`FileDownloadJob` owns no query, target, candidate list, source-provider enum, or
placement rules. It merely makes progress/output state consistent for snapshots,
persistence, cancellation, and completion. `RemoteFileJob` requires its exact
target and relative output intent. It has no Soulseek search, YouTube fallback,
tag naming, or music conditions.

`SongJob` remains the song discovery/orchestration job. Once it chooses a
`FileCandidate`, it passes `candidate.Target` to the same exact-file runner. It
does not need to create a nested public `RemoteFileJob` merely to share code. A
preselected music-track download stores a `PeerFileTarget` directly rather than a
fabricated `FileCandidate`; candidate evidence remains optional search data.

### Directory download lifecycle

The abstract base represents a directory-shaped download across resolution,
transfer, and completion:

```csharp
public abstract class DirectoryDownloadJob : Job
{
    public DirectoryExecutionState DirectoryState { get; }
    public DirectoryTransferAttempt? ActiveAttempt { get; }
    public IReadOnlyList<FileDownloadJob> FileJobs { get; }
    public string? DownloadPath { get; protected set; }
}

public sealed class RemoteDirectoryJob : DirectoryDownloadJob
{
    public RemoteDirectorySource Source { get; }
}

public class AlbumJob : DirectoryDownloadJob, IUpgradeable
{
    public AlbumQuery Query { get; }
    public IReadOnlyList<AlbumDirectoryCandidate> Results { get; }
}
```

The base owns controlled directory-phase transitions and active child/output
state. It does not know how to search, retrieve, plan, or finalize.
`DirectoryExecutionState` is an explicit discriminated state such as
`Unresolved`, `Resolving`, `Planned(attemptNumber)`, and
`Transferring(attemptNumber)`, rather than a nullable plan plus boolean flags. A
`DirectoryTransferAttempt` owns one immutable plan and its materialized file
children; state and snapshots reference its number rather than serializing the
plan twice. The normal `JobLifecycleState` remains the public
pending/running/awaiting-selection/terminal lifecycle; directory state is a
subordinate concern and maps to existing activity phases such as `Searching`,
`RetrievingFolder`, and `Downloading`.

`RemoteDirectorySource` is immutable and has two cases:

```csharp
public abstract record RemoteDirectorySource
{
    public sealed record PeerDirectory(PeerDirectoryIdentity Directory)
        : RemoteDirectorySource;

    public sealed record Resolved(DirectoryTransferPlan Plan)
        : RemoteDirectorySource;
}
```

A peer-directory source performs one full directory retrieval before planning. A
resolved source starts in the planned state and cannot invoke retrieval. This is
how browse-selected downloads retain a structural no-second-browse guarantee
without narrowing every directory job to transfer-only behavior.

For a resolved source, the source plan becomes attempt one; it is not copied into
a second mutable job field. Persistence stores one plan payload and references it
from source/attempt state so maximum-size remote jobs do not pay twice in memory
or on disk.

`AlbumJob` now legitimately derives from `DirectoryDownloadJob`. Candidate
selection converts the chosen `PeerDirectorySnapshot` and album-approved files
into a plan. Album-specific services remain responsible for:

- searching and manual candidate selection;
- retrieving/validating candidate folder contents when required;
- selecting audio, ancillary, and image files;
- retrying another album directory after stale or failed candidates;
- track-count and quality conditions;
- album art, tag/output organization, playlists, on-complete behavior, and
  incomplete-album policy.

The current album control flags are removed through explicit state/policy:

| Current field | Replacement |
| --- | --- |
| `ResolvedTargetNeedsInitialFolderRetrieval` | Unresolved directory state plus a `PeerDirectoryIdentity`/incomplete snapshot |
| `AllowBrowseResolvedTarget` | Snapshot completeness and an album resolution policy describing whether completion is permitted |
| `SkipResolvedTargetTrackCountVerification` | Album-only validation requirement such as `Standard` or `UserAccepted` |
| Mutable `ResolvedTarget` as transfer state | Active directory attempt associated with the selected album candidate |

These replacements remain album resolution data where appropriate; they do not
become properties on `DirectoryDownloadJob` or `RemoteDirectoryJob`.

An album retry creates a new immutable plan with a monotonically increasing
attempt number. Children are associated with the attempt that created them; stale
children cannot be mistaken for the active candidate. Whether failed attempt
children remain in the live tree or only in activity/history is decided once for
all directory subtypes and covered by snapshot/persistence tests.

`RemoteDirectoryJob` uses `RemoteFileJob` children and neutral planning.
`AlbumJob` uses `SongJob` children and music planning. The shared base exposes
them as `FileDownloadJob` values, allowing generic cancellation/progress traversal
without erasing subtype-specific payloads.

The album executor delegates exact file transfer and common directory-group
mechanics after its semantic decisions. Shared runners consume targets, plans,
resolved destinations, and explicit child state; they do not require the child to
be a particular semantic subtype.

### Direct Soulseek links

Interpretation is explicit at extraction/submission, not inferred from file type
or folder shape:

| Input intent | File link | Directory link |
| --- | --- | --- |
| Ordinary remote (Soulseek default) | `RemoteFileJob(target)` | `RemoteDirectoryJob(RemoteDirectorySource.PeerDirectory)` |
| Music track | preselected `SongJob(target)` | not applicable |
| Music album | `AlbumJob` treating the supplied path as its selected directory | preselected `AlbumJob(directory)` |

The new `ExtractionMode.General` input value selects this ordinary remote
interpretation alongside the song and album interpretations. Other extractors
keep their source-defined defaults; changing the default for Soulseek links does
not change the interpretation of Spotify, CSV, or MusicBrainz inputs.

Extend the existing `ExtractionMode` with `General` rather than introducing an
overlapping mode type. `ExtractionMode` answers how an input is interpreted;
`DownloadBehaviorPolicy` continues to answer automatic versus manual candidate
selection. Neither is copied into the target or transfer plan.

An exact file never needs fake search evidence. An ordinary directory link performs
folder retrieval inside its `RemoteDirectoryJob`. A preselected album may also
retrieve its directory, but then applies album validation and finalization. The
same peer identity or snapshot can therefore support both user intents without a
`music` flag on the target, plan, or runner.

## Execution refactor

### Exact-file runner

Extract the protocol transfer portion of `SongDownloadExecutor` into a component
which accepts:

- a `PeerFileTarget`;
- the owning job/transfer state;
- an already resolved local destination;
- cancellation and retry policy; and
- progress/attempt callbacks already used by the job engine.

It owns Soulseek download invocation, reconnect waiting, unknown-error retry,
stale-target classification, transfer registration, progress, and terminal
transfer completion. It does not search, rank candidates, invoke YouTube, infer
queries, tag files, or run album policy.

`SongDownloadExecutor` retains discovery and fallback, then calls this runner for
each selected Soulseek candidate. `RemoteFileJob` calls it directly with no
search/fallback policy.

### Directory runner

Extract only mechanics genuinely shared by resolved directory transfers:

- bounded deterministic child materialization;
- parent/child registration;
- linked cancellation and single-child cancellation behavior;
- concurrency through the existing engine limits;
- aggregate progress and terminal outcome derivation; and
- cleanup of unfinished children.

Album candidate choice, filtering, images, retries across directories, and final
album organization remain outside this runner. Placement is resolved before an
exact-file call; the runner does not derive a destination by checking whether its
parent is an album.

There is no second Soulseek client, transfer queue, connection, retry subsystem,
or live-state channel.

### Planning, placement, and settings

Ordinary remote and music downloads deliberately use different planners around
the shared runner:

```text
RemoteDirectoryResolutionCoordinator
  RemoteDirectorySource.PeerDirectory -> retrieve snapshot
  RemoteDirectorySource.Resolved      -> reuse plan
  PlacementPlanner          -> contained deterministic destinations

AlbumResolutionCoordinator
  query/candidate selection -> optional snapshot retrieval
  AlbumTransferPlanner      -> file roles and accepted plan entries
  MusicPlacementFinalizer   -> naming, tags, images, playlists, index

both -> DirectoryTransferRunner -> ExactPeerFileTransferRunner
```

`PlacementPlanner` preserves the plan's logical tree when `NameFormat` is
empty. When configured, it renders one relative destination per file using the
shared name-format engine and shared placement variables such as peer username,
filename, final output extension (`ext`), relative path, folder name,
item/default folder, input/extractor, and output/runtime context. It then sanitizes every rendered
component, applies stable collision suffixes, and proves containment beneath the
resolved output parent.

Music jobs use the same parser, conditional/fallback syntax, escaping, and the
same shared provider, while adding source-query and downloaded-tag variables such as artist,
title, album, track, and disc. A variable registry declares which job semantics
support each variable and whether it is available before or after transfer.
Referencing a variable unsupported by the selected semantic job fails validation
before workflow registration. A supported music variable whose tag value is
missing may still evaluate empty so the existing fallback syntax continues to
work.

`AlbumTransferPlanner` and the existing music finalizers may stage and rename
files according to those query/tag values. The exact-file runner receives a
resolved destination and cannot distinguish those callers.

The current `FileManagerContext.FromSongJob` and monolithic variable dictionary
are split into a common naming context plus a structural provider and a
music-enriched provider.
The `FileManager` type check (`job is AlbumJob`) is removed from shared path
construction. Album-specific organization may remain in a music-named component;
the template engine and generic containment/sanitization move to job-agnostic
utilities.

The existing `DownloadSettings` object may remain the configuration aggregate
during this refactor, but executors receive typed projections with an explicit
applicability matrix:

| Concern | Shared transfer | Remote file/tree | Music track/album |
| --- | --- | --- | --- |
| Output parent, cancellation, peer retry/timeouts | Yes | Yes | Yes |
| Safe components, containment, collision policy | Utility | Default tree or formatted relative path | Used for staging where applicable |
| Name-format parser and common structural variables | Utility | Yes | Yes |
| Query/tag-derived name-format variables | No | No | Yes |
| Search ranking, file/folder conditions, preprocessing | No | No | Yes |
| YouTube/other fallback | No | No | Track only |
| Tag-based post-transfer organization | No | No | Yes |
| Album images and incomplete-album action | No | No | Album only |
| Playlist and music index | No | No | Yes |
| On-complete | Common job hook with generic context | Yes | Yes, with music context extension |

`Output.NameFormat` is valid for ordinary remote submissions. Its structural
variables are validated against the neutral provider. A music-only variable in an
explicit API/CLI override produces `400 invalid-name-format-variable` rather than
an empty path component. If the incompatible format is only inherited, the
remote policy uses empty `NameFormat` and therefore ordinary filename/tree
placement. Auto-profile conditions distinguish `generic-file` and
`generic-directory` before this projection and may supply a valid structural
format. Other explicit API/CLI overrides that are invalid for an ordinary remote
submission are likewise rejected rather than silently ignored. Unrelated album
defaults in a global profile are not projected into an ordinary remote executor.
This prevents accidental music behavior without forcing existing music-oriented
global naming to block ordinary transfers.

`DownloadBehaviorPolicy` continues to govern discovery/manual selection where it
is meaningful. Already-resolved remote jobs are automatic; do not add remote
file/directory policy fields merely because they share a base class.

## API, snapshots, and persistence

Search and browse resources remain separate projections because their identities
and paging semantics differ:

- a file search candidate has a username/full filename reference, peer
  availability, and search evidence;
- a browse file has artifact-local IDs and safe display metadata while its exact
  wire filename remains server-side; and
- a browse directory has parent IDs, synthetic/visibility state, and recursive
  aggregates that do not exist on an album candidate.

Introduce one presentation-only leaf component for facts both projections expose:

```csharp
public sealed record FileMetadataDto(
    string Name,
    long Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<FileAttributeDto>? Attributes);
```

`FileCandidateDto` composes this metadata alongside its candidate reference, full
remote filename, and peer/search evidence. The future `BrowseFileEntryDto`
composes it alongside artifact-local file/directory IDs. `Name` is a safe leaf
display value and is never used as protocol identity. This reuse must not cause
browse responses to expose exact wire paths or search responses to lose their
stable candidate references. Directory DTOs do not inherit from one another: an
album candidate and a hierarchical artifact row have different identities and
fields.

Add explicit payload/snapshot discriminators for the concrete `RemoteFileJob` and
`RemoteDirectoryJob`. Abstract bases are not protocol job kinds. Instead, compose
small `FileDownloadStateDto` and `DirectoryDownloadStateDto` components into both
remote and music payloads so progress, directory phase/attempt, counts,
output-relative placement, and terminal metadata have one representation without
flattening semantic fields into a nullable union. `SongJobPayloadDto` keeps query,
candidate, and source information; `AlbumJobPayloadDto` keeps album results and
selection evidence; remote payloads contain neither.

Exact wire paths follow the same redaction rules as existing transfer snapshots
and must not leak merely because a target is now a shared Core value.

The refactor must update:

- `CoreSnapshotFactory` and transfer snapshots;
- `EngineStateStore`, server payload mapping, available actions, and live deltas;
- job request mapping and intent-to-concrete-job selection;
- typed settings projection/applicability validation;
- persistence serialization/restoration for jobs that are durable today;
- CLI job status presentation and JSON discriminators; and
- OpenAPI contract assertions.

This is v4 contract work. Any public DTO rename or shape change is made atomically
with typed clients and tests; do not publish old and new job representations in
parallel indefinitely.

## Migration sequence

Each stage is independently reviewable. Stages 1-7 preserve current runtime
behavior; stage 8 contains the intentional, explicitly tested Soulseek-link
interpretation change.

1. **Characterize current behavior.** Add missing regression tests for direct
   candidates, album selection/retry, cancellation, output placement, snapshots,
   and persistence before changing models.
2. **Introduce exact target values.** Add `PeerFileTarget` and conversion at
   Soulseek.NET/search/persistence boundaries. Refactor `FileCandidate` to compose
   it while temporarily retaining delegating properties.
3. **Separate directory evidence.** Introduce `PeerDirectoryIdentity`,
   `PeerDirectorySnapshot`, and `AlbumSearchEvidence`; adapt
   `AlbumFolder`/`AlbumFile`, `RetrieveFolderJob`, and search projections.
4. **Extract the file lifecycle and runner.** Add abstract `FileDownloadJob`, move
   common progress/output state from `SongJob`, route resolved song candidates
   through the exact runner, then add `RemoteFileJob`.
5. **Introduce neutral transfer plans and placement seams.** Map a selected album
   directory into an immutable `DirectoryTransferPlan`; separate job-agnostic safe
   path utilities, neutral tree placement, and music placement/finalization.
6. **Extract the directory lifecycle.** Add abstract `DirectoryDownloadJob` and
   explicit resolution/attempt transitions, then make `AlbumJob` derive from it
   without changing album behavior.
7. **Add the remote directory subtype.** Add `RemoteDirectorySource` and
   `RemoteDirectoryJob`; route peer-directory sources through generic retrieval
   and resolved-plan sources directly into shared transfer mechanics.
8. **Migrate direct links and contracts.** Remove fake search responses and album
   control flags from ordinary `slsk://` handling, add explicit remote versus
   music interpretation, and update dispatch, snapshots, persistence, actions,
   typed clients, settings validation, and CLI presentation atomically.
9. **Remove compatibility scaffolding.** Delete obsolete constructors, duplicate
   authoritative fields, and superseded flags only after all callers and tests use
   the new model.
10. **Begin user-browse downloads.** Map artifact selections directly to
    `PeerFileTarget` and `DirectoryTransferPlan`, then construct
    `RemoteDirectoryJob(RemoteDirectorySource.Resolved)`; do not add another target or
    transfer hierarchy.

## Verification plan

Add focused fixtures rather than concentrating the refactor in existing broad
end-to-end classes:

- `Sockseek.Core.Tests/PeerFileTargetTests.cs` for exact identity, construction,
  copying, and adapter equivalence;
- `Sockseek.Core.Tests/DirectoryTransferPlanTests.cs` for invariants, relative
  structure, copying, and admission;
- `Sockseek.Core.Tests/ExactFileTransferRunnerTests.cs` for shared network transfer
  outcomes and the resolved-song/remote-file parity matrix;
- `Sockseek.Core.Tests/FileDownloadJobTests.cs` and
  `DirectoryDownloadLifecycleTests.cs` for common state transitions, attempt
  ownership, child lifecycle, cancellation, progress, and aggregate outcomes;
- `Sockseek.Core.Tests/RemoteDirectoryJobTests.cs` for both source variants and
  retrieval behavior;
- `Sockseek.Core.Tests/PlacementPlannerTests.cs` for containment,
  sanitization, stable collisions, default tree preservation, and structural
  name-format rendering;
- focused additions to `NameFormatTests.cs` for variable-provider capability and
  remote/music validation; and
- focused additions to `ExtractorTests2`, `DownloadEventsTests`,
  `DownloadFallbackTests`, `StaleDownloadTests`, and `EndToEndTests` for direct
  links and album behavior preservation.

Server, Persistence, and CLI coverage belongs with the existing
`EngineStateStoreTests`, `EngineSupervisorTests`, `EnginePersistenceAdapterTests`,
`PersistenceDaemonTests`, `OpenApiContractTests`, backend-parity tests, and status
presenter tests. Test helpers should create Sockseek-owned targets directly; only
adapter tests should need Soulseek.NET response/file objects.

### Core value tests

- Construct `PeerFileIdentity`/`PeerFileTarget` from live Soulseek search data,
  persisted data,
  direct links, album folder retrieval, and synthetic browse-artifact fixtures;
  assert equivalent exact targets without Soulseek.NET objects.
- Assert target metadata changes do not change `PeerFileIdentity` equality or
  duplicate detection.
- Preserve case, leading/trailing spaces, and NFC/NFD distinctions in valid
  usernames and paths. Reject empty, control-containing, ill-formed, and
  over-byte-limit identities without trimming or normalization.
- Preserve unknown size/metadata without inventing search speeds, response counts,
  or free-slot values.
- Prove copied attribute collections and transfer plans cannot change when their
  source lists mutate.
- Reject a plan with mixed peers, duplicate exact targets, rooted relative paths,
  empty or traversal components, or overflowed totals. Its
  admission validator rejects more entries/bytes than policy allows.
- Verify deterministic entry order. Separately verify the default placement
  planner's collision suffixes for case, separator, Unicode-normalization, and
  sanitized-name collisions.
- Verify a synthetic display root never becomes a wire request; every entry still
  downloads through its own exact filename.

### Search and album regression tests

- File search ranking, free-slot preference, speed, raw result projection, result
  revision, persistence restoration, and candidate references are unchanged after
  `FileCandidate` composition.
- Album folder grouping, lazy query inference, search counts, audio lengths,
  quality coverage, sorting, full-folder retrieval, and manual selection are
  unchanged after evidence separation.
- `RetrieveFolderJob` accepts a generic `PeerDirectoryIdentity`, returns a complete
  `PeerDirectorySnapshot`, and carries no album query/evidence. Search-scoped
  retrieval still updates the canonical album candidate selected by the caller.
- A normal `AlbumJob` searches once, selects a candidate, produces a plan, and
  downloads the same files and output layout as before.
- `AlbumJob` derives from `DirectoryDownloadJob`; its search/retrieval/planning
  transitions use the shared directory state without exposing music fields on
  the base.
- A preselected/retrieved album skips search according to existing semantics and
  does not retrieve twice.
- Standard, user-accepted, incomplete-snapshot, and completion-forbidden album
  cases reproduce the old flag behaviors through explicit state/policy; the
  superseded boolean properties are absent.
- Stale or failed album candidates still fall back to the next folder within retry
  policy; a `RemoteDirectoryJob` never changes to another peer identity.
- Each album candidate attempt gets a new immutable plan and attempt number;
  children and terminal evidence from an earlier attempt cannot become active for
  the next candidate.
- Track-count/quality rejection, album-art-only, optional art failure, ancillary
  files, tag naming, playlist/index updates, on-complete commands, incomplete-album
  actions, skip-existing, and final rename failures retain current outcomes.
- Album and aggregate-album child job state, cancellation source, progress, and
  terminal outcome remain identical in Core, Server, and CLI projections.

### Exact-file runner tests

- Run the same target through a resolved `SongJob` and `RemoteFileJob`; assert
  identical Soulseek request identity, bytes/progress, retry classification,
  local containment, and transfer history.
- `RemoteFileJob` never invokes search, result sorting, query preprocessing, YouTube
  fallback, tag inference, or album handlers.
- Cover success, already exists, peer queued, stale file, remote rejection,
  disconnect/reconnect, timeout, unknown-error retry exhaustion, parent
  cancellation, direct child cancellation, and daemon shutdown.
- A cancellation or failure completes transfer handles and removes live progress
  without affecting unrelated sibling jobs.

### Directory job tests

- `RemoteDirectoryJob(RemoteDirectorySource.PeerDirectory)` retrieves exactly once, transitions
  through resolving/planned/transferring, and uses the returned complete snapshot.
- `RemoteDirectoryJob(RemoteDirectorySource.Resolved)` begins planned and cannot invoke
  directory retrieval, including after restart or retry.
- A resolved source and its first attempt share one owned plan; snapshot and
  persistence fixtures prove the entry collection is not duplicated.
- A plan with nested relative paths is preserved by default placement below the
  chosen output root and never escapes it; music placement remains free to choose
  its documented name format.
- Empty `NameFormat` preserves the selection tree. A configured format
  renders shared structural file and relative-path variables, preserves the final
  output extension (`ext`) without duplication, and still passes through
  sanitization, collision, and containment checks.
- Remote jobs reject query/tag-only variables before registration. Music jobs
  accept them, and missing supported tag values continue to exercise conditional
  fallback syntax rather than becoming unsupported-variable errors.
- An explicitly submitted tag-only format fails before registration. An inherited
  tag-only format falls back to empty ordinary placement, while a matching
  `generic-file`/`generic-directory` auto profile can override it with a structural
  format before fallback is evaluated.
- Multiple files use existing concurrency limits; the directory does not create a
  second queue or bypass transfer admission.
- Cancelling the directory cancels unfinished children. Cancelling one file leaves
  siblings running. Partial failure produces the documented aggregate outcome.
- File-count/byte/memory admission rejects atomically before workflow registration.
- A plan remains executable after its source search snapshot or browse artifact is
  deleted.
- `RemoteDirectoryJob` does not apply music search, track filters, album images,
  tag formats, playlists, or incomplete-album behavior.
- Explicit incompatible music overrides on an ordinary remote submission fail
  validation; inherited global music defaults never reach the neutral planner.
  An inherited incompatible `NameFormat` is specifically projected to empty
  ordinary placement. Common transfer, output-parent, and valid common-variable
  name formats apply to both paths.
- Snapshot, live delta, persistence round-trip in every directory phase, restart
  restoration, action availability, history paging, and CLI rendering cover
  `RemoteFileJob`, `RemoteDirectoryJob`, and the shared state components used by
  song/album payloads.

### Direct-link tests

- A default direct Soulseek file link creates `RemoteFileJob`, preserves exact
  username/path, performs no search, and downloads once.
- A trailing-slash directory link performs exactly one directory resolution,
  creates a plan inside `RemoteDirectoryJob`, and performs no album inference or
  second retrieval.
- Explicit music-track intent creates a preselected `SongJob` without fabricated
  search evidence. Explicit album intent creates album orchestration and retains
  current album behavior.
- Soulseek's ordinary remote default does not change source-defined interpretation for
  Spotify, CSV, YouTube, Bandcamp, or MusicBrainz inputs.
- Malformed username/path inputs fail before Soulseek is contacted and create no
  workflow.

### Remote/music separation tests

- Feed the same `PeerFileTarget` to `RemoteFileJob` and a preselected `SongJob`.
  Assert one identical Soulseek wire request and transfer outcome, while only the
  song path receives music naming/fallback/finalization context.
- Feed one complete `PeerDirectorySnapshot` to an ordinary remote-directory request
  and an explicit preselected-album request. Neither retrieves again. The remote
  planner preserves every selected entry/tree component; the album planner applies
  documented file roles, conditions, and music finalization.
- A directory name that resembles an album still creates `RemoteDirectoryJob`
  under ordinary remote intent. Only explicit music-album intent creates `AlbumJob`.
- Common transfer/output-parent overrides yield the same effective runner policy
  for remote and music jobs. The same common-variable name format renders through
  the shared engine for both. Music-only variables/overrides are accepted for the
  applicable semantic job and rejected for remote jobs.
- Remote completion never writes playlists/music indexes or runs incomplete-album
  actions. Music regression fixtures prove those behaviors remain active.
- Adding a test-only third semantic `FileDownloadJob` or `DirectoryDownloadJob`
  subtype can reuse the exact runner without extending a semantic-mode enum or
  modifying neutral/music placement services.

### Architecture and contract tests

- Core exact targets/plans have no references to Soulseek.NET response/file types,
  API DTOs, Server, CLI, or persistence entities.
- `FileDownloadJob` contains no query, peer target, search candidate, tag, or
  fallback-provider field. `DirectoryDownloadJob` contains no album query,
  candidate evidence, tree-placement policy, or image setting.
- The abstract bases expose state transitions but no virtual
  `Resolve`/`Plan`/`Finalize` template methods and reference no concrete semantic
  subtype.
- `RemoteFileJob` has no missing-target construction path.
- `RemoteDirectoryJob` has exactly the peer-identity and resolved-plan source
  cases; no null source, boolean retrieval flag, or implicit third mode exists.
- `RetrieveFolderJob` depends on `PeerDirectoryIdentity`/`PeerDirectorySnapshot`,
  not `AlbumFolder`.
- `SongJob : FileDownloadJob`, `RemoteFileJob : FileDownloadJob`,
  `AlbumJob : DirectoryDownloadJob`, and
  `RemoteDirectoryJob : DirectoryDownloadJob`. No semantic subtype inherits from
  another semantic subtype.
- Generic runners and path utilities contain no album/browse, semantic-mode, or
  concrete job-type switch. Concrete orchestration dispatch is allowed only before
  entering shared runners.
- The name-format parser and shared provider are reused directly; music composes
  query/tag enrichment rather than redefining shared names. Providers declare
  applicability and phase. The common provider has no `SongQuery`, TagLib, album, or concrete-job
  dependency.
- OpenAPI contains one supported discriminator/payload for each concrete job kind;
  common file/directory state is composed consistently and typed clients
  deserialize every subtype exhaustively.
- Search DTOs retain candidate references and peer evidence; browse DTOs retain
  opaque artifact IDs and do not expose wire paths.
- Existing architecture dependency tests and all Core, Server, Persistence, and
  CLI suites pass after removal of compatibility scaffolding.

### Performance checks

- Guard direct-file execution with resolved-song/remote-file parity tests and an
  architecture check that both enter the same exact runner. The originally
  proposed synthetic before/after network benchmark was dropped during
  implementation: the extraction moved the existing protocol loop rather than
  adding a wrapper, and a mocked or variable peer transfer would measure the
  harness/network instead of the refactor. Future client-independent work inside
  the runner should add a microbenchmark at that seam.
- Measure directory plan and child-job retained bytes at representative and
  maximum admitted counts; use the measurement to set the fixed job-memory bound
  consumed by user browsing.
- Confirm progress/live events remain coalesced and bounded for a maximum admitted
  directory job.

### Verification results (2026-08-12)

- `dotnet build Sockseek.sln --no-restore -v:q` succeeds with no errors.
- Full suites pass: Core 735, Persistence 52, Server 122, and CLI 254 (1,163
  total). This includes the exact-runner failure/cancellation matrix, album
  retry/art/finalization regressions, remote settings/direct-link behavior,
  payload/OpenAPI contracts, persistence, and CLI parity/presentation.
- Architecture tests reject semantic dependencies in shared runners, `General*`
  prefixes on neutral Core mechanisms, parallel directory source modes, and
  normalized exact identities. Production direct links contain no fabricated
  `SearchResponse` or `Soulseek.File` values.
- The directory allocation benchmark reports 122.25 KiB at 100 entries,
  1,207.65 KiB at 1,000, and 12,087.95 KiB at 10,000 on 64-bit .NET 10. The
  estimator and admission policy deliberately remain conservative relative to
  those measurements.
- Directory progress is derived from owned children without a second plan graph;
  the existing coalescer, bounded live dispatcher, and non-blocking bounded
  persistence tests cover slow consumers independently of directory size.
- `git diff --check` is clean after generated OpenAPI and typed-contract updates.
- A combined solution run with project/test parallelization also passes. Its
  regression coverage waits for asynchronous server job registration before
  inspecting concrete runtime type and proves a handled fatal login cannot leave
  an unobserved readiness-broadcast fault for the finalizer thread.

## Release gates

The refactor is ready for user-browse download work only when:

1. Existing song and album regression suites pass without compatibility-only fake
   `SearchResponse` or `Soulseek.File` construction in production direct-link code.
2. Remote file/directory jobs and music jobs use the existing
   transfer/runtime/persistence path and are fully observable through current
   workflow clients.
3. The abstract lifecycle bases contain no semantic policy; music behavior is
   absent from remote jobs and remains present for song/album jobs.
4. Username/path exactness tests pass across search, direct links, chat, peer
   policy, uploads, and the target factories used by future browsing.
5. The directory job admission budget is based on measured retained memory.
6. Remote versus music settings applicability and direct-link interpretation are
   enforced by contract tests rather than undocumented ignored options.
7. Remote name formatting supports shared placement variables through the shared
   engine, rejects music-only variables clearly, and remains containment-safe.
8. The updated user-browsing design references these shared targets, plans,
   lifecycle bases, runners, and remote job subtypes rather than defining parallel
   equivalents.
