# Sockseek v4 user browsing

Status: design only; not implemented

Target: Sockseek v4
Scope: remote user profiles, remote share browsing, and downloads selected from a browse

Source review: 2026-08-11

This design adds the last user-browsing item in `TODO.md`: viewing a Soulseek
user's description, picture, status, statistics, and shared files. It defines the
Core, daemon, HTTP API, live client, and CLI together so the eventual WebUI can use
the same public surface rather than requiring a second implementation.

The design uses RFC 2119 meanings for MUST, SHOULD, and MAY.

## User-facing changes

This is the consolidated changelog for the feature. User-visible behavior is
specified here; later sections explain how it works.

### Added

- `sockseek user profile <username>` shows a remote user's description, online
  status, shared file/directory counts, upload speed, upload slots, queue
  length, and free-slot state when the peer supplies them. Unavailable fields say
  `unknown`; an offline user is not presented as an application error.
- Profile pictures can be rendered with `--picture auto|sixel|pixels|none`.
  `auto` uses sixel only after positive capability detection, otherwise a bounded
  ANSI half-block mosaic. Redirected output, `--no-color`, and `--json` emit no
  image escape sequences. The default is `auto` for a terminal and `none`
  otherwise.
- `user-picture = <path>` in the default daemon config, or
  `--user-picture <path>` at startup, sets the local Soulseek profile picture next
  to the existing `user-description` option. The daemon validates and normalizes
  it once at startup; removing the option advertises no picture.
- `sockseek user shares <username>` performs or reuses one browse, reports live
  transfer/parse progress, and opens a filesystem-style share browser on a TTY.
- The share browser lists the current directory's child directories and files in
  one directory-first view. It does not project directories to albums or album
  aggregates. Three organizational roots therefore appear as three folders to
  enter, not three music candidates to accept or reject.
- Arrow keys navigate, `/` filters the current listing, `Enter` opens a directory,
  Backspace goes to its parent, and Space toggles the highlighted file or complete
  directory subtree in a download cart. `D` reviews and submits the cart; `?`
  always shows help. Directory rows show recursive public file/byte counts before
  selection.
- The cart can contain directory subtrees and individual files across several
  branches. A single submission creates one visible workflow with an independently
  observable remote-directory child for each canonical selection root.
- `sockseek user shares-page <browse-id>` is the non-interactive, page-oriented
  companion. It accepts `--parent`, `--query`, `--cursor`, and `--limit`; `--json`
  prints the exact directory page DTO. `--files <directory-id>` selects a file page
  instead. This avoids ever dumping an unbounded user's complete share graph.
- `sockseek user shares-download <browse-id> --folder <directory-id>` submits a
  scriptable download. `--folder` is repeatable, `--file` is repeatable, and
  folder selection includes its complete public subtree. It accepts transfer,
  output-parent, and structural name-format options and prints the resulting workflow
  ID.
- `sockseek user shares-cancel <browse-id>` explicitly cancels an active daemon
  browse. Interrupting a waiting CLI detaches by default because another client
  may have joined the same single-flight resource.
- Usernames displayed by chat and search are allowed to advertise profile and
  share actions to a future WebUI. Chat and search do not own profile or browse
  state.
- The daemon exposes a composite profile resource, a separate binary picture
  resource, asynchronous browse resources, paged directory/file resources, and a
  browse-to-download command. Typed API clients cover every resource.
- The live client exposes only browse lifecycle and summary changes. Directory and
  file contents remain paged HTTP data, so a GUI never has to replicate a large
  peer tree over the live stream.

### Deliberately unchanged or deferred

- Local description/picture changes are daemon-lifetime configuration and require
  restart; a live profile-mutation API remains out of scope.
- It does not add buddies, bookmarks, private user notes, or a durable history of
  previously browsed users. Those are separate product choices.
- It does not expose peer IP addresses or ports. Sockseek needs those internally;
  a profile UI does not.
- Version one does not add "download this browsed directory as an album." Browse
  selection uses the ordinary remote interpretation. The shared target/snapshot and job hierarchy
  leave room for a later explicit music-album action without inferring albums from
  directory shape or retrieving the peer twice.
- It does not make browse results part of the historical database or backup
  contract. They are bounded, disposable derived data.
- It does not add public tuning knobs for parser limits, cache eviction, or browse
  concurrency. Safe defaults are part of the implementation and may be adjusted
  from measurements.

## Goals and acceptance criteria

The implementation is complete only when:

1. A CLI user can inspect a profile, including a fun but safe terminal rendering
   of its picture.
2. A CLI user can browse a peer once, inspect directories and files without
   unbounded output, select several folders or file subsets, and start downloads.
3. The same typed API is sufficient for a multi-panel GUI with multiple concurrent
   user tabs.
4. Large valid browses remain usable through streaming, disk-backed artifacts, and
   paging; size alone never makes an otherwise valid browse fail.
5. All outbound user operations reuse the daemon's single Soulseek session and
   existing peer/operator policy seams.
6. Browse-selected downloads materialize the shared `PeerFileIdentity`,
   `PeerFileTarget`, and `DirectoryTransferPlan` model, create the ordinary remote
   job subtypes, use the existing job engine, and appear like other workflows in
   CLI and future GUI monitoring.

## Decision rules

When implementation details are ambiguous, apply these rules in order:

1. Keep one `DaemonSoulseekRuntime` and one underlying Soulseek connection.
2. Treat every profile string, picture byte, browse count, directory, filename,
   attribute, and compressed byte as untrusted peer input.
3. Validate framing and individual values before retaining them, and stream
   aggregate browse data to disk. A malformed-input or operational failure MUST be
   local to the browse or profile request and MUST leave the daemon usable.
4. Keep small changing state live; keep large collections page-oriented.
5. Use immutable IDs and opaque cursors at public boundaries. Never require a GUI
   to echo remote paths or an entire selected file list.
6. Reuse the remote-transfer identity, plan, lifecycle bases, semantic job
   subtypes, and runners; do not create a browse-only domain model or second
   transfer engine.
7. Cache only when it prevents network work or repeated parsing. Cached browse
   data is disposable and rebuildable.
8. Prefer a small, understandable behavior over options for every internal bound.

## Lessons from slskd

slskd is the closest mature reference, and its evolution gives useful positive
and negative guidance.

### What to carry forward

- Profile information, status, statistics, and pictures are distinct Soulseek
  operations and may succeed independently.
- Recursive directory selection and file-level selection are useful, especially
  for shares that group a large collection below a few roots.
- A virtualized tree, filtering, collapse/expand, and direct profile/browse actions
  from usernames materially improve a WebUI.
- Browse acquisition needs an explicit progress resource rather than making a long
  request look frozen.

### What not to copy

- slskd's `/users/{username}/browse` returns a complete materialized
  `BrowseResponse`. Its browser then creates the complete tree and historically
  stored it in `localStorage`; it now uses IndexedDB and virtualized rendering.
  Moving storage and windowing the DOM improves the UI, but it does not bound the
  server's read, decompression, or object graph.
- slskd's status tracker is removed shortly after completion, while the browser
  polls it frequently. Reported failures include repeated `404` responses and a
  browse that appears stuck at zero.
- slskd recursively gathers selected files into JavaScript arrays and imposes a
  5,000-file selection cap because the selection crossed browser-storage and API
  boundaries as file data. Sockseek instead sends compact artifact IDs and resolves
  them inside the daemon.
- slskd exposes peer endpoints in its user view. Sockseek does not need to make
  network coordinates public.
- Soulseek.NET's current browse path buffers the framed message, decompresses into
  another byte array, and materializes lists of every directory and file. Reports
  of large-peer memory spikes show why UI virtualization alone is not the safety
  boundary.

These lessons are evidence, not a compatibility target. Sockseek's resource and
DTO shapes intentionally differ.

## Architecture

```text
CLI / future WebUI
        |
typed SockseekApiClient + SockseekLiveClient
        |
Server endpoints ---- DaemonClientStore (small live projections)
        |                         |
UserBrowsingService ------ UserBrowseArtifactStore
        |                 (ephemeral SQLite artifacts)
        |
DaemonSoulseekRuntime
        |
bounded profile calls + streaming browse ingress
        |
one Soulseek.NET client/session

browse selection -> server-side resolver -> DirectoryTransferPlan
                                                |
                                                v
                                      JobList -> existing job engine
```

### Prerequisite transfer refactor

The prerequisite
[`resolved-remote-transfer-refactor-plan.md`](resolved-remote-transfer-refactor-plan.md)
is now implemented and verified. It established the shared exact target,
immutable directory plan, file/directory lifecycle bases, and remote job
subtypes. The browse feature does not introduce
another peer-file target, directory candidate, placement planner, or transfer
runner.

The dependency is deliberately below the public resources. Album search, a user
browse artifact, and a direct Soulseek link have different discovery and identity
contracts, but all three converge on `PeerFileIdentity`/`PeerFileTarget` after the
server has resolved an exact file. Album orchestration and ordinary directory jobs
share directory lifecycle state while retaining different planners/finalizers.
Album orchestration produces a `DirectoryTransferPlan` only after candidate
selection; a browse submission produces one directly from leased artifact rows.

### Ownership

`DaemonSoulseekRuntime` continues to own the connected `SoulseekClientManager` and
shared `PeerAccessPolicy`. A new daemon-lifetime `UserBrowsingService` coordinates
profile calls, browse single-flight, cache/artifact lifetime, and cancellation. It
MUST NOT create another Soulseek client or log in independently.

An immutable `LocalUserProfile` is loaded once from daemon settings and supplied
to both the client manager's fallback user-info resolver and
`SoulseekSharingAdapter`; neither reopens the configured picture per peer request.

Core owns remote-path parsing, streaming browse decoding, artifact writing, shared
`PeerFileIdentity`/`PeerFileTarget`/`DirectoryTransferPlan` values, and exact
transfer runners. It also owns the abstract `FileDownloadJob`/
`DirectoryDownloadJob` lifecycle bases and concrete remote
`RemoteFileJob`/`RemoteDirectoryJob` subtypes. Server owns authorization, HTTP
resources, live projections, artifact-to-plan resolution, and creation of jobs.
API owns wire DTOs and clients. CLI owns presentation and interaction.

`UserBrowseArtifactStore` is not a repository for domain history. Each successful
browse is an immutable SQLite file plus small metadata. A staging file is private
to its writer, atomically promoted on success, and deleted on cancellation or
failure. Completed artifacts are evicted by a fixed age and global byte budget;
restart cleanup removes abandoned staging files. These values are internal safe
defaults, not public configuration.

### Concurrency and reuse

- At most one network browse per exact wire username runs at a time. Concurrent
  callers receive the same browse ID.
- A fresh completed artifact for that user is reused unless `refresh=true`.
- A refresh creates a new immutable artifact. Existing readers may finish against
  the previous one until their lease ends; new default lookups use the new one.
- Global network-browse concurrency is deliberately small and fixed. Accepted
  browse resources wait in a compact FIFO coordination queue until a network slot
  is available; queue depth is not a validity rule and never rejects a browse.
- Profile subrequests use bounded single-flight caches with short fixed lifetimes.
  A profile refresh bypasses freshness but still joins an identical in-flight call.
- Soulseek reconnect/logoff cancels active profile calls and browses with a stable
  `connection-lost` failure. Completed artifacts remain readable until eviction,
  but starting downloads still requires a connected daemon.
- Cache/single-flight keys include the configured local Soulseek account. Changing
  accounts never reuses the previous account's profile or default browse artifact.

## Identity and access policy

Soulseek usernames are opaque, case-sensitive protocol identities. This matches
slskd's pass-through API and ordinal dictionaries, Soulseek.NET's exact wait and
connection keys, and Nicotine+'s user/watch/browse maps. Sockseek MUST send the
exact API/CLI spelling on every server and peer request and use
`StringComparer.Ordinal` for browse single-flight, caches, artifacts, and response
correlation. Wrong case is a different/non-existent user; Sockseek never retries
with guessed casing or aliases two spellings.

The API boundary validates a non-empty, well-formed Unicode scalar sequence and
rejects control characters. It MUST NOT trim, case-fold, or apply
Unicode normalization; spaces and NFC/NFD distinctions are preserved. Display
escaping is presentation only. Invalid input produces `400 invalid-username`
before Soulseek is contacted.

The prerequisite transfer refactor replaced the former normalizing username path
with `PeerUsername.Validate`/`PeerIdentityValidator.ValidateUsername`. Those
validators preserve exact wire spelling. Shared peer policy, chat, uploads,
search, direct links, and transfer targets use the same ordinal identity; display
sanitization remains a separate presentation concern.

Every outbound profile, picture, browse, and browse-selected download checks the
shared `PeerAccessPolicy`. A blocked peer produces `404 user-not-found`; this
avoids disclosing policy membership and matches the treatment of inaccessible
peer resources.

Every endpoint in this design, including reads, calls
`OperatorMutationPolicy.RequireOperator()`. It is a pass-through in the current
self-hosted trust model and the seam for later authentication. Artifact IDs are
unguessable, but possession of an ID is not authorization.

## Profile model

### Composite behavior

The service requests user info, status, and statistics concurrently after the
policy check. It does not make the CLI or WebUI coordinate three endpoints.
Individual sections retain their own state so one peer timeout does not erase
successful data from the others.

```csharp
public sealed record UserProfileDto(
    string Username,
    UserPresence Presence,
    UserProfileSectionDto Status,
    UserProfileSectionDto Info,
    UserProfileSectionDto Statistics,
    string? Description,
    long? SharedFileCount,
    long? SharedDirectoryCount,
    long? AverageUploadSpeed,
    int? UploadCount,
    int? UploadSlots,
    int? QueueLength,
    bool? HasFreeUploadSlot,
    UserPictureRefDto? Picture,
    DateTimeOffset ObservedAt);

public sealed record UserProfileSectionDto(
    ResourceSectionState State, // available, unavailable, timed-out
    string? Reason);

public sealed record UserPictureRefDto(
    string Url,
    string MediaType,
    int ByteLength,
    string ETag);
```

`UserPresence` is `online`, `away`, `offline`, or `unknown`. `offline` is a valid
profile response. `unknown` means status could not be established. Numeric values
MUST be range-checked; invalid peer values become `null`, not wrapped or negative
public values.

Descriptions are plain text. The server normalizes newlines, removes forbidden
controls, enforces a scalar/UTF-8 limit, and never interprets markup. The CLI wraps
the result; the WebUI will render it as text content.

### Pictures

Picture bytes have a strict byte limit before retention. The server recognizes a
small allow-list of formats by signature and structural probe, reports the real
media type. Unknown, truncated, oversized, or absurd-dimension images make only
`Picture` unavailable; the rest of the profile still succeeds.

The outbound picture is configured alongside the existing description:

```ini
[default]
user-description = Hello from Sockseek
user-picture = /absolute/path/to/profile-picture.jpg
```

`EngineSettings.UserPicturePath` is daemon-lifetime and forbidden in named or
automatic download profiles. Startup expands the path using normal config path
rules, requires a readable regular file, and reads through a byte-limited stream.
Core's bounded image decoder, which the CLI also uses, applies orientation and first
frame, strips metadata, bounds dimensions/pixels/work, and encodes a static JPEG at
a fixed internal size/quality for broad Soulseek-client compatibility. Original
bytes are never advertised. Invalid or oversized input is a clear startup config
error; null means no picture. Normalized bytes stay in memory until restart;
allowed peers receive them in `UserInfo`, while denied peers receive no profile.

Soulseek.NET currently materializes the complete user-info response, including its
picture byte array. The narrow dependency patch required for browse framing MUST
also cap user-info message, description, and picture lengths while framing/parsing,
before allocating from peer-declared lengths. Post-parse validation alone is not a
memory boundary.

The API does not put base64 into JSON:

```http
GET /api/users/{username}/profile?refresh=false
GET /api/users/{username}/picture
```

The picture response supports `If-None-Match`, sets an exact `Content-Type`,
`Content-Length`, a restrictive cache policy, and `X-Content-Type-Options: nosniff`.
It never serves SVG or HTML. A bounded byte-budget cache may retain validated
picture bytes briefly; a miss repeats `GetUserInfoAsync`, subject to single-flight.

CLI decoding runs through a decoder configured with input-byte, dimension, pixel,
frame, and elapsed-work bounds. Animation uses only its first frame. Decode failure
prints a short `picture unavailable` note only when human-readable output was
requested.

For `pixels`, the renderer scales without upsampling to the terminal width and a
fixed maximum height, composites transparency over the current background, and
uses `▀` with foreground/background 24-bit color for two image rows per cell. It
restores terminal attributes in `finally`. `sixel` is emitted only when explicitly
selected or capability detection positively identifies support; `auto` falls back
to pixels without blocking on an indefinite terminal probe.

## Browse lifecycle

A browse is an asynchronous resource because the peer controls connection delay,
payload size, and transfer duration.

```csharp
public sealed record UserBrowseDto(
    Guid BrowseId,
    string Username,
    UserBrowseState State,
    UserBrowsePhase Phase,
    long CompressedBytesReceived,
    long? CompressedBytesExpected,
    long DecompressedBytesRead,
    long DirectoryCount,
    long FileCount,
    long TotalFileBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    ApiErrorDto? Failure,
    long Revision);
```

States are `queued`, `running`, `complete`, `failed`, and `cancelled`. Phases are
`waiting-for-peer`, `receiving`, `decoding`, `indexing`, and `ready`. Progress is
monotonic within a phase. Soulseek often cannot supply a trustworthy total, so the
expected byte count is nullable and clients MUST support indeterminate progress.

The resource remains queryable through every terminal state until its fixed
retention expires. A failed or cancelled refresh does not delete the user's prior
successful artifact.

### HTTP resources

```http
POST   /api/users/{username}/browses
GET    /api/user-browses?username={username}&state={state}&cursor={cursor}&limit={limit}
GET    /api/user-browses/{browseId}
POST   /api/user-browses/{browseId}/cancel
DELETE /api/user-browses/{browseId}

GET    /api/user-browses/{browseId}/directories?parentId={id}&query={q}&recursive={bool}&cursor={cursor}&limit={limit}
GET    /api/user-browses/{browseId}/directories/{directoryId}
GET    /api/user-browses/{browseId}/directories/{directoryId}/files?query={q}&cursor={cursor}&limit={limit}

POST   /api/user-browses/{browseId}/downloads/preview
POST   /api/user-browses/{browseId}/downloads
```

`POST .../browses` accepts `{ "refresh": false }`. It returns `202` for queued or
running work and `200` for a reusable completed resource. Joining a single-flight
request returns the existing ID. `refresh=true` starts a new generation unless an
identical refresh is already running.

The list endpoint is bounded and intended to restore a GUI's recent tabs. It is
not durable browse history. `DELETE` releases the caller-visible browse and marks
it eligible for physical eviction; leased readers and jobs already created are
not invalidated. Cancel is idempotent and affects only running work.

Collection endpoints require a completed artifact and return
`409 browse-not-ready` while it is active. An expired/removed ID returns
`410 browse-expired`, allowing a client to offer Refresh instead of pretending the
user has no shares.

All collections use a server-capped `limit`, stable ordering, opaque cursor, and:

```csharp
public sealed record PageDto<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);
```

The cursor encodes artifact generation, sort key, and last immutable ID and is
authenticated by the server. It is invalid across artifacts and produces
`400 invalid-cursor`, not a best-effort result from different data.

With neither `parentId` nor `query`, the directory endpoint returns roots. A
`parentId` returns direct children. `query` filters that level by default;
`recursive=true` instead searches descendant display paths below the supplied
parent, or the whole artifact when no parent is supplied. The file endpoint also
accepts `query` for direct filenames. Filtering never changes stable page sort.

## Browse data model

The artifact is a flat indexed representation, not a recursively materialized
object graph.

```csharp
public sealed record BrowseDirectoryEntryDto(
    long DirectoryId,
    long? ParentId,
    string Name,
    string DisplayPath,
    ShareVisibility Visibility,
    bool IsSynthetic,
    long DirectDirectoryCount,
    long DirectFileCount,
    long RecursiveFileCount,
    long RecursiveFileBytes,
    long LockedDescendantCount,
    bool HasChildren);

public sealed record FileMetadataDto(
    string Name,
    long Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<FileAttributeDto>? Attributes);

public sealed record BrowseFileEntryDto(
    long FileId,
    long DirectoryId,
    FileMetadataDto File);
```

`FileMetadataDto` is presentation-safe leaf metadata, not an identity. The
resolved-transfer refactor also composes it into search candidate DTOs so search
and browsing use the same public representation for file facts without equating
their resource identities. Search candidates retain username/full-filename
references and peer evidence; browse files retain artifact-local IDs and expose
only a safe leaf name.

Directory and file IDs are artifact-local immutable integers. Public APIs do not
return the exact wire path because it is both untrusted and unnecessary for
selection; the artifact retains it internally for protocol requests and job
materialization.

The artifact minimally contains:

- `metadata`: schema version, browse/user/generation, timestamps, counts, and
  completion marker;
- `directories`: ID, parent ID, normalized display components, exact wire path,
  visibility, and direct/recursive aggregates;
- `files`: ID, directory ID, safe display name, exact wire filename, size, and
  validated Soulseek attributes;
- indexes for parent/name traversal, directory filtering, and file paging.

Each Soulseek directory is a first-class item. No album inference, similarity
grouping, or cross-directory coalescing occurs. Missing ancestors are synthesized
so a peer with `Collection A\...`, `Collection B\...`, and `Collection C\...` has
the natural three-root hierarchy even if it did not send rows for those parents.
A synthetic directory is selectable as the union of its public descendant files;
it is never sent to the peer as an invented exact-directory request.

`RecursiveFileCount` and `RecursiveFileBytes` count downloadable public files.
`LockedDescendantCount` lets clients disclose that locked branches will not be
included. A locked directory cannot be selected directly; a public or synthetic
ancestor selection skips locked branches and reports that fact before submission.
`ShareVisibility` is `public`, `locked`, or `mixed`; `mixed` is used for structural
parents whose descendants have both visibilities.

### Remote path rules

Soulseek paths normally use backslashes, but peer input is not assumed to be a
valid Windows path. Parsing therefore has two representations:

- wire identity: the exact validated string used to request/download from the peer;
- display identity: normalized separators, NFC text, removed forbidden controls,
  and explicit replacement markers for unsafe display scalars.

Empty components, `.`/`..`, separator-only paths, duplicate identities after
normalization, and a filename outside its declared directory are rejected or
quarantined according to one deterministic parser rule. They never become local
filesystem paths. Case is preserved; equality uses the protocol-compatible
comparison already used by Sockseek remote candidates. Locked shares remain
visible as locked but cannot be submitted for download.

Artifact aggregates are computed after ingestion in SQL and checked for overflow.
Filter queries are case-insensitive ordinal/display-normalized substring matches
over directory name/path. Version one does not promise full-text ranking.

## Streaming browse ingress

This feature MUST NOT call Soulseek.NET `BrowseAsync` and then copy its complete
`BrowseResponse` into an artifact. The pinned library's implementation first
accumulates the peer message, creates byte-array copies during framing and zlib
decompression, then creates lists and objects for all rows. That defeats every
downstream paging guarantee.

The implementation therefore includes a narrow, reviewable compatibility patch
at the pinned Soulseek.NET boundary:

Because the required framing and message-handler hooks are internal, the patch is
carried as a commit-pinned Sockseek build of Soulseek.NET rather than reflection or
a second peer socket. It preserves the existing public surface for all other
operations, retains upstream license notices, and is proposed upstream. Dependency
updates MUST rebase and rerun the protocol/memory fixtures; the exact fork commit
is recorded beside the package pin.

1. Only incoming browse-response framing is redirected to a streaming sink.
2. The announced length, compressed bytes, elapsed receive time, and idle periods
   are checked while reading; data never accumulates in `List<byte>`.
3. A streaming zlib decoder feeds a row parser. Framing lengths, counts, integer
   arithmetic, string Unicode, and attributes are validated as they are read, but
   total decompressed bytes, directory/file rows, and staging growth are not
   treated as validity limits.
4. Rows are inserted in bounded transactions into the private staging artifact.
   No in-memory directory graph or complete file list is constructed.
5. The parser requires an exact end-of-message and rejects trailing, truncated,
   malformed, or overflowing input with a stable failure. Disk-full and other
   storage failures are operational failures of this browse, not evidence that the
   peer's share was too large.
6. Cancellation closes the peer operation, disposes streams/statements, and
   removes staging data.

Representation bounds MUST come from the Soulseek frame or the receiving API and
be tested at their actual boundary. Sockseek MUST NOT add guessed aggregate row,
decompressed-byte, or staging-byte ceilings. Memory qualification measures that
streaming memory stays bounded as a valid artifact grows; storage consumption is
managed by artifact eviction and ordinary filesystem availability.

The patch has a deletion condition: remove it when the pinned Soulseek.NET version
offers an equivalent streaming, cancellable browse response API. Until
then, the adapter is isolated and accompanied by protocol fixture tests so a
library update cannot silently restore full buffering.

`GetDirectoryContentsAsync` MAY refresh a known selectable directory for a future
feature, but cannot discover an unknown peer tree and is not a substitute for the
initial streaming browse.

## Live state

Add a `UserBrowse` live scope to the existing live protocol and
`DaemonClientStore`. Its identity is `browseId`, and its snapshot/delta contains
only the fields in `UserBrowseDto`. Revisions and snapshot-barrier semantics match
the daemon, workflow, and chat scopes already designed.

Typical flow:

1. GUI posts a browse and receives its resource.
2. It subscribes to `UserBrowse(browseId)` before rendering progress.
3. Snapshot/deltas advance lifecycle and counters.
4. On `complete`, the GUI fetches root directories and later children/files by
   page as they become visible.
5. Leaving the tab unsubscribes. It does not cancel the network browse unless the
   user explicitly chooses Cancel.

Slow consumers coalesce progress revisions for a browse. Terminal state is never
dropped. Directory rows, file rows, descriptions, and pictures are prohibited
from browse deltas. A WebUI may keep a small page cache and virtualize its tree,
but this is an optimization rather than the daemon's safety boundary.

## Selecting and downloading shares

### Request contract

```csharp
public sealed record StartUserShareDownloadsRequestDto(
    Guid RequestId,
    IReadOnlyList<UserShareSelectionDto> Selections,
    SubmissionOptionsDto? Options = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UserShareDirectorySelectionDto), "directory")]
[JsonDerivedType(typeof(UserShareFileSelectionDto), "file")]
public abstract record UserShareSelectionDto;

public sealed record UserShareDirectorySelectionDto(long DirectoryId)
    : UserShareSelectionDto;

public sealed record UserShareFileSelectionDto(long FileId)
    : UserShareSelectionDto;

public sealed record StartUserShareDownloadsResponseDto(
    JobSummaryDto Workflow,
    UserShareResolutionSummaryDto Resolution);
```

A directory selection always means its complete public subtree. This matches a
normal file browser and makes an organizational/synthetic root useful. Direct-only
selection is expressed by selecting its individual files, not a subtle recursive
flag. File selections are exact.

An empty request is invalid. All IDs must belong to the URL artifact; clients cannot supply
usernames, paths, sizes, speeds, or attributes. `RequestId` is an idempotency key:
repeating the same authenticated request returns the same submitted workflow,
while reusing it with different content returns `409 idempotency-conflict`.

This endpoint always selects ordinary remote interpretation in version one. Its
`SubmissionOptionsDto` is projected to the refactor's typed remote-transfer policy:
transfer settings, output parent, safe-component replacement, shared name format,
and the generic on-complete hook may apply. The format may use shared structural
file and relative-path variables; a music-only query/tag variable
in an explicit request produces `400 invalid-name-format-variable`. An
incompatible inherited format falls back to ordinary filename/tree placement;
`generic-file` and `generic-directory` auto profiles run first and may replace it
with a structural format. Explicit per-request search,
preprocessing, fallback, album-art, playlist/index, or incomplete-album overrides
produce `400 invalid-remote-transfer-option`. A selected/global profile may
contain those settings for music jobs, but they are not projected into ordinary remote
execution.

Before submission the server:

1. leases the completed artifact and rechecks operator/peer policy and connection;
2. resolves compact IDs from the artifact with indexed, disk-backed queries;
3. rejects missing/invalid IDs and direct selection of a locked directory/file;
4. expands directory subtrees, including synthetic roots, while excluding locked
   branches and recording their count in the resolution summary;
5. canonicalizes selections to an antichain: a selected ancestor directory covers
   selected descendants and files, and repeated IDs collapse;
6. groups remaining standalone files by their actual containing directory;
7. enumerates expanded rows progressively and copies exact immutable artifact
   values into shared `PeerFileIdentity` and
   `PeerFileTarget` instances, and server-derived relative path components into a
   `DirectoryTransferPlan`.

`UserShareResolutionSummaryDto` reports canonical directory roots, standalone
files, total public files/bytes, redundant selections removed, and locked branches
skipped. This summary is also computed for the CLI review screen before its final
confirmation by `POST .../downloads/preview`, which accepts the same selections
and options without an idempotency key and creates no workflow. Because artifacts
are immutable, a later identical submission resolves the same rows; policy,
connection and idempotency are still rechecked. The artifact lease can
end after job construction because the job owns its target data; eviction never
changes an already submitted workflow.

### Remote directory jobs over resolved plans

The prerequisite transfer refactor supplies the shared model, lifecycle bases,
semantic subtypes, planners, and runners. `AlbumJob` and `RemoteDirectoryJob` both
derive from abstract `DirectoryDownloadJob`, but browse selections create the
remote subtype. A peer directory is not evidence of an album, and routing it
through album planning would incorrectly invite track-count rules, images, tag
organization, playlists, and album-shaped presentation.

Browse submission uses the remote subtypes with an already-resolved source:

```csharp
public sealed class RemoteDirectoryJob : DirectoryDownloadJob
{
    public RemoteDirectorySource Source { get; }
}

public sealed class RemoteFileJob : FileDownloadJob
{
    public PeerFileTarget Target { get; }
    public RelativeOutputPath OutputPath { get; }
}

var job = new RemoteDirectoryJob(
    new RemoteDirectorySource.Resolved(plan));
```

The root remains a normal `JobList` named `Shares from <username>`. Each canonical
directory selection becomes one `RemoteDirectoryJob`; standalone files from the
same containing directory share one such job. A synthetic selection root is valid
because each plan entry retains a real exact wire filename.

The `RemoteDirectorySource.Resolved` union case starts the job in its planned directory
state and has no transition that invokes folder retrieval. Thus browse-selected
downloads perform no search, directory retrieval, album discovery, or second
browse, even though a different `RemoteDirectoryJob(RemoteDirectorySource.PeerDirectory)` used by
a direct folder link may retrieve contents as part of its lifecycle.

`SongJob : FileDownloadJob` and `AlbumJob : DirectoryDownloadJob` use the same
exact-peer runner and generic parent/child mechanics after their own discovery and
planning phases. The default `PlacementPlanner` preserves the selected relative tree;
music-specific placement and finalization remain outside shared runners.

No second downloader, queue, or connection is created. The remote job kinds
participate in the existing workflow snapshots, activity log, cancellation,
retry, concurrency, skip-existing, and terminal-state rules. Cancelling a
directory cancels its file children; cancelling one file or sibling directory
does not cancel the others.

Output is always rooted at the resolved output parent. With an empty name format,
it preserves `<selected-folder>/<descendant-folders>/<filename>`; a configured
format may instead render the relative destination from shared structural
variables. In both cases, every component is sanitized
independently, containment is checked after joining, and normalization collisions
receive a stable suffix rather than overwriting. Transfer/output-parent/profile
resolution applies; music discovery, track filters, tag-derived naming, album
image, playlist, and incomplete-album settings do not apply to these job kinds.
Only query/tag-derived format variables are music-specific. An explicit
incompatible music override in this request is rejected rather than silently
ignored; unrelated music defaults inherited from a profile are not projected into
the remote executor. CLI help states this instead of presenting music options as
share behavior.

Expanded rows are read progressively from the artifact and copied into the normal
directory-job representation. IDs stay compact at the API boundary, and the
review response gives exact totals for confirmation; those totals are
informational, not admission limits.

The existing search-scoped `StartFolderDownload` endpoint is not reused as the
public contract: it requires a search job and accepts folder-shaped client data.
The dedicated browse endpoint resolves compact IDs server-side, then submits the
new remote jobs through the same engine path as every other workflow.

## CLI behavior

Remote user commands follow the direct dispatcher pattern established by chat and
always use `SockseekApiClient`; they do not instantiate Soulseek.NET in the CLI.
`--remote`, operator credentials when added, cancellation, error formatting, and
`--json` conventions remain shared.

### Profile

```text
sockseek user profile <username> [--refresh]
    [--picture auto|sixel|pixels|none] [--image-width <cells>]
    [--remote <url>] [--json]
```

Human output keeps unavailable fields compact and clearly separates `offline`
from `unknown`. `--image-width` is capped by the renderer and affects only display.
JSON is exactly `UserProfileDto`; picture content is represented by its URL and
metadata and is never fetched merely to produce JSON.

### Interactive shares

```text
sockseek user shares <username> [--refresh] [--filter <text>]
    [transfer/output-parent/profile options] [--remote <url>]
```

The command posts the browse, subscribes to its live resource when available, and
falls back to bounded HTTP polling with backoff. Ctrl+C during acquisition cancels
the CLI wait and prints the browse ID; it does not cancel the shared daemon browse
because another client may be observing the same single-flight resource. The
explicit `shares-cancel` command cancels it. Once complete, the browser requests
only visible directory and file pages.

`InteractiveShareBrowser` is a new filesystem interaction, not an adapter over
`InteractiveModeManager`. Its screen has a breadcrumb, a directory-first entry
table, a selected-cart summary, and a compact key legend. Directories show public
recursive file/byte counts and locked descendants; files show size and type. Pages
are fetched as the viewport approaches their end, and row objects are discarded
under a small page-cache budget.

Selection is an antichain of artifact IDs. Space on a directory selects its whole
public subtree and removes selections it contains. A row covered by an already
selected ancestor shows a covered marker; to choose only part of that ancestor,
the user first toggles the ancestor off, enters it, and selects the desired
children/files. This avoids hidden exclusion rules and guarantees compact request
state. Space on a file selects only that file. Locked rows remain inspectable but
cannot be selected.

The implementation MAY reuse low-level terminal pieces such as key dispatch,
width-aware columns, safe text rendering, filter editing, and help overlays. It
MUST NOT reuse the album candidate projection, one-candidate card flow,
album-accept/reject semantics, `d:<indices>` grammar, or album renderer. Existing
interactive search behavior is left unchanged; extracting a generic primitive is
optional and must have regression tests if done.

Before `D` submits, the CLI calls the preview endpoint and shows canonical
roots/files/bytes, redundant entries, locked branches skipped, and the output
root. If submission fails, it leaves the cart intact and shows the operational or
validation error. On confirmation and success it prints the root
workflow ID and returns; normal daemon monitoring owns subsequent progress.

### Scriptable shares

```text
sockseek user shares-page <browse-id>
    [--parent <directory-id> | --files <directory-id>] [--query <text>]
    [--cursor <opaque>] [--limit <n>] [--remote <url>] [--json]

sockseek user shares-download <browse-id>
    (--folder <directory-id> | --file <file-id>)...
    [--preview] [transfer/output-parent/profile options]
    [--request-id <guid>] [--remote <url>] [--json]

sockseek user shares-cancel <browse-id> [--remote <url>] [--json]
```

`shares --json` starts or reuses a browse, waits for a terminal state, and prints
only `UserBrowseDto`. It never silently changes into a full graph dump. Scripts use
the returned browse ID with `shares-page`, then submit stable IDs with
`shares-download`. Omitting `--request-id` generates one for that invocation.
Directory IDs always mean complete public subtrees. `--preview` returns only the
resolution summary and does not require/generate a request ID.
`shares-cancel` is idempotent and returns the resulting browse resource.

Non-TTY invocation without `--json` or explicit selection/page arguments is a
usage error, not an attempt to read interactive keys from redirected stdin.

## API client surface

`ISockseekApiClient` and `SockseekApiClient` add typed, cancellation-aware methods
matching each HTTP resource:

```csharp
Task<UserProfileDto> GetUserProfileAsync(...);
Task<UserPictureResponse> GetUserPictureAsync(...);
Task<UserBrowseDto> StartUserBrowseAsync(...);
Task<PageDto<UserBrowseDto>> GetUserBrowsesAsync(...);
Task<UserBrowseDto> GetUserBrowseAsync(...);
Task CancelUserBrowseAsync(...);
Task DeleteUserBrowseAsync(...);
Task<PageDto<BrowseDirectoryEntryDto>> GetUserShareDirectoriesAsync(...);
Task<BrowseDirectoryEntryDto> GetUserShareDirectoryAsync(...);
Task<PageDto<BrowseFileEntryDto>> GetUserShareFilesAsync(...);
Task<UserShareResolutionSummaryDto> PreviewUserShareDownloadsAsync(...);
Task<StartUserShareDownloadsResponseDto> StartUserShareDownloadsAsync(...);
```

The picture client exposes a response/stream plus media metadata and never reads
the entire body unless its caller elects to do so within the advertised bound.
All new DTOs are registered in source-generated JSON metadata. The live client
adds subscribe/unsubscribe and snapshot/delta types for `UserBrowse` only.

## Errors and recovery

Errors use the existing `ApiErrorDto` envelope and add these stable codes:

| HTTP | Code | Meaning / client action |
|---:|---|---|
| 400 | `invalid-username` | Fix the username before retrying. |
| 400 | `invalid-selection` | Fix empty, mixed, or malformed selection IDs. |
| 400 | `invalid-remote-transfer-option` | Remove music/search-only overrides from a share transfer submission. |
| 400 | `invalid-name-format-variable` | Remove variables unavailable to remote downloads or choose music interpretation. |
| 400 | `invalid-cursor` | Restart paging from the first page. |
| 404 | `user-not-found` | User is inaccessible or denied by peer policy. |
| 409 | `browse-not-ready` | Wait on the browse resource. |
| 409 | `idempotency-conflict` | Use a new request ID for different content. |
| 410 | `browse-expired` | Start/refresh the user's browse. |
| 502 | `peer-response-invalid` | Peer data was malformed; refresh may retry. |
| 503 | `soulseek-unavailable` | Connect the daemon or retry after reconnect. |
| 504 | `peer-timeout` | Peer did not respond within the bounded phase. |

Profile section timeouts normally produce a partial `200` profile. A total service
or policy failure uses the envelope. Browse failures are recorded on the resource
and the initial/start request still returns its ID if work was accepted.

Retries never append to a failed staging artifact. A refresh is a new generation
and may coexist with readers of the last good one. Clients SHOULD retain their
navigation path by display components but MUST discard IDs when generations
change.

## Security and privacy

- Treat descriptions and names as text, never terminal markup, HTML, format
  strings, glob expressions, SQL, or local paths.
- Escape terminal controls including OSC, CSI, and bidi formatting controls before
  display. JSON retains safe Unicode text but never raw invalid scalars.
- Parameterize every artifact query. Cursors contain no raw SQL or remote path.
- Validate image formats and resource bounds before browser/terminal decoding; do
  not proxy arbitrary peer-declared media types.
- Do not log descriptions, picture bytes, filenames, directory names, IPs, or
  browse filter text at normal levels. Diagnostics use browse ID, phase, bounded
  counts, duration, and stable error code.
- Do not make the artifact directory web-addressable. Files are opened by internal
  ID under a daemon-controlled root with restrictive local permissions.
- Recheck symlink/root containment rules only when converting candidates to local
  download paths; remote display paths are never joined directly to an output
  root.
- Apply response compression carefully: already compressed picture data is not
  recompressed, and paged JSON is bounded before compression.

## Observability

Keep the surface small and free of peer labels:

- counters for browses started, reused, completed, cancelled, and failed by stable
  reason;
- gauges for active/queued browses and artifact count/bytes;
- histograms for receive bytes, decompressed bytes, rows, and duration;
- structured lifecycle logs keyed by browse ID and exact wire-username hash, not
  raw username or paths;
- one warning when an artifact is evicted for budget and one error for cleanup
  failure, both rate-limited.

Progress events are coalesced and rate-limited before they reach the live store.
Per-row events and metrics are prohibited.

## Verification plan

### Core and protocol fixtures

- Valid public/locked, empty, Unicode, mixed-separator, and missing-parent browses.
- Truncated or invalid zlib, invalid lengths/counts, trailing bytes, integer
  overflow, malformed Unicode, and invalid attributes.
- Actual wire/representation bounds at one below, exactly at, and one above their
  value; no tests encode guessed total-row or total-byte validity limits.
- A highly compressed, very large valid browse succeeds and remains pageable.
- Cancellation/timeout at framing, receive, decompression, parsing, transaction,
  indexing, and atomic promotion.
- Assert bounded peak memory for synthetic browses of increasing size; the
  threshold is independent of total row count.
- Assert no complete `BrowseResponse`, complete byte array, or directory graph is
  retained by the production path.
- Library-upgrade compatibility fixtures captured without personally identifying
  real peer data.

### Artifact and API

- Atomic staging promotion, restart cleanup, age/byte eviction, reader leases, and
  reuse/refresh generations.
- Stable paging under concurrent clients; invalid and cross-generation cursors.
- Directory/file counts, synthesized parents, filtering, locked visibility, and
  exact wire identity retained internally.
- Profile partial success, offline/unknown distinction, description sanitization,
  picture validation/ETag/304, image bombs, local load/normalize, and denied peers.
- Exact case, wrong case, leading/trailing spaces, and NFC/NFD-distinct usernames
  across API, wire mocks, cache/single-flight, peer policy, chat, and uploads.
- Operator/peer policy on every read/mutation, including an artifact ID learned
  before policy changed.
- Live subscribe race: snapshot barrier plus deltas cannot miss terminal state;
  slow consumers remain bounded.

### Selection and jobs

- Directory subtree, synthetic root, individual file, several branches, antichain
  canonicalization, locked/stale IDs, and large valid expansions.
- Artifact rows map to the same `PeerFileIdentity` equality and `PeerFileTarget`
  metadata used by search/direct links, while `DirectoryTransferPlan` contains
  only exact targets plus server-derived logical relative components.
- Search and browse projections compose the same `FileMetadataDto` leaf facts;
  browse pages never expose the target username or exact wire filename.
- Same idempotency key/same body returns one workflow; changed body conflicts.
- Preview creates no workflow; submission is one `JobList` with independent
  `RemoteDirectoryJob(RemoteDirectorySource.Resolved)` children and nested
  `RemoteFileJob` leaves, with no browse/search afterward.
- The resolved source cannot call directory retrieval, while a peer-directory
  source fixture proves the shared subtype can retrieve exactly once for direct
  links without weakening the browse invariant.
- Artifact deletion immediately after submission does not affect downloads.
- Root versus child cancellation, partial failure, retry, skip-existing, output
  tree preservation, sanitization, collision handling, and containment.
- Empty name format preserves the selected tree. Shared username, filename,
  output-extension (`ext`), foldername, and relative-path variables render
  deterministically;
  music-only query/tag variables fail before workflow creation.
- Music search, track filtering, tag-derived naming, album images, playlists, and
  incomplete-album behavior are absent from ordinary share jobs.
- Explicit music-only submission overrides are rejected; inherited music defaults
  are not projected into remote execution.

### CLI and GUI readiness

- Profile human/JSON, redirected/no-color output, capability-positive sixel,
  fallback pixels, decoder failure, narrow/wide terminals, and style restoration.
- Browse progress with/without live connection, reconnect, Ctrl+C, expired browse,
  paging, filters, parent navigation, multi-select cart, and submission summary.
- Existing interactive search behavior is unchanged; any reused terminal primitive
  has focused regression coverage.
- Headless API test models two simultaneous GUI tabs, each paging a large artifact
  while live progress for another browse remains bounded.

### Release gates

The feature MUST NOT ship until:

1. The resolved remote-transfer refactor passes its release gates; browse code has
   not introduced replacement target, plan, job, or runner types.
2. The streaming ingress path has adversarial fixtures and measured peak-memory
   evidence; calling materializing `BrowseAsync` is a release blocker.
3. A large fixture can be paged and multi-selected without sending the whole graph
   through HTTP, live state, CLI memory, or request JSON.
4. Existing workflow monitoring observes browse-selected downloads without a
   special compatibility path.
5. slskd interoperability is smoke-tested against public and locked share shapes,
   while failure behavior is tested with intentionally malformed local fixtures.
6. Documentation and CLI help contain the user-facing commands above and explain
   that browse caches are ephemeral.

## Delivery sequence

1. Complete
   [`resolved-remote-transfer-refactor-plan.md`](resolved-remote-transfer-refactor-plan.md),
   including exact identity, abstract lifecycle state, remote/music policy
   separation, album/direct-link regressions, shared runners, concrete job
   payloads, and progressive directory execution.
2. Add streaming browse ingress, protocol fixtures, artifact schema/store, and source
   patch deletion note. No public endpoint uses it until the memory gate passes.
3. Add local profile loading, remote profile service, picture validation, browse
   coordination, and artifact lifecycle.
4. Add API DTOs/endpoints/typed clients and live `UserBrowse` scope.
5. Add browse selection resolution that materializes shared exact targets/plans,
   then submit `RemoteDirectoryJob(RemoteDirectorySource.Resolved)` with
   `RemoteFileJob` leaves through the existing engine.
6. Add the filesystem-style share browser, profile/share CLI commands, and image
   renderers; reuse only terminal primitives that remain genuinely generic.
7. Run the verification/release gates and update `TODO.md` only when the complete
   user-facing slice is implemented.

Each step should be independently reviewable. Schema and wire versions are bumped
with their normal compatibility rules; this design does not require a migration of
historical user data.

## Authoritative checklist

Feature boxes remain unchecked because this file is a design, not an
implementation claim. The completed prerequisite is marked separately.

- [ ] One shared Soulseek runtime and peer policy are reused.
- [ ] Profile data is composite, partial, bounded, and excludes peer endpoints.
- [ ] Local pictures are normalized for peers; remote pictures are safely rendered.
- [ ] Browse ingress streams to disk without aggregate size-based refusal.
- [ ] Successful browses become immutable disposable SQLite artifacts.
- [ ] Browse lifecycle is durable for its short resource lifetime and live-visible.
- [ ] Directories/files are paged; large collections never enter live deltas.
- [ ] Multiple folder/file selections resolve from artifact IDs on the server.
- [x] The resolved remote-transfer refactor and its regression gates are complete.
- [ ] Downloads are `JobList`/`RemoteDirectoryJob` workflows with no
  second browse or music-policy leakage.
- [ ] The share UI is filesystem-shaped and album interactive behavior is unchanged.
- [ ] Typed clients make the same surface usable by CLI and future WebUI.
- [ ] Adversarial, memory, cancellation, policy, paging, and job tests pass.

## Source record

The design was checked against:

- Sockseek branch `user-browsing`, commit
  `05529fac50285e509c652cceb0c400aa97eef732`.
- slskd commit
  [`c80e3f45201d8095f8952786ee69009d2793d91f`](https://github.com/slskd/slskd/tree/c80e3f45201d8095f8952786ee69009d2793d91f),
  [user API controller](https://github.com/slskd/slskd/blob/c80e3f45201d8095f8952786ee69009d2793d91f/src/slskd/Users/API/Controllers/UsersController.cs),
  [user service](https://github.com/slskd/slskd/blob/c80e3f45201d8095f8952786ee69009d2793d91f/src/slskd/Users/UserService.cs),
  [profile configuration](https://github.com/slskd/slskd/blob/c80e3f45201d8095f8952786ee69009d2793d91f/docs/config.md#other),
  [outbound user-info resolver](https://github.com/slskd/slskd/blob/c80e3f45201d8095f8952786ee69009d2793d91f/src/slskd/Application.cs#L1909-L1970),
  [browse UI](https://github.com/slskd/slskd/blob/c80e3f45201d8095f8952786ee69009d2793d91f/src/web/src/components/Browse/Browse.jsx),
  [directory tree](https://github.com/slskd/slskd/blob/c80e3f45201d8095f8952786ee69009d2793d91f/src/web/src/components/Browse/DirectoryTree.jsx),
  [selection UI](https://github.com/slskd/slskd/blob/c80e3f45201d8095f8952786ee69009d2793d91f/src/web/src/components/Browse/Selection.jsx).
- slskd reports and changes:
  [large browse/localStorage failure #317](https://github.com/slskd/slskd/issues/317),
  [large browse rendering #1153](https://github.com/slskd/slskd/issues/1153),
  [browse memory exhaustion #1372](https://github.com/slskd/slskd/issues/1372),
  [multiple users/tabs #1373](https://github.com/slskd/slskd/issues/1373),
  [recursive selection #1788](https://github.com/slskd/slskd/pull/1788),
  [virtualized tree #1789](https://github.com/slskd/slskd/pull/1789), and
  [username profile/browse actions #1298](https://github.com/slskd/slskd/pull/1298).
- Soulseek.NET commit
  [`52fc3e4267114d8cd9492cb4d7438b3eca0267bf`](https://github.com/jpdillingham/Soulseek.NET/tree/52fc3e4267114d8cd9492cb4d7438b3eca0267bf),
  the version pinned by Sockseek at review time, especially
  [`ISoulseekClient`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/ISoulseekClient.cs),
  [`WaitKey`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Common/WaitKey.cs),
  [`BrowseResponseFactory`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Messaging/Messages/Peer/BrowseResponseFactory.cs),
  and
  [`MessageConnection`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Network/MessageConnection.cs).
- Nicotine+ commit
  [`d08f755b749e781b087705ed61822f64531e5d8c`](https://github.com/nicotine-plus/nicotine-plus/tree/d08f755b749e781b087705ed61822f64531e5d8c), especially its exact-key
  [`users.py`](https://github.com/nicotine-plus/nicotine-plus/blob/d08f755b749e781b087705ed61822f64531e5d8c/pynicotine/users.py) and [`userbrowse.py`](https://github.com/nicotine-plus/nicotine-plus/blob/d08f755b749e781b087705ed61822f64531e5d8c/pynicotine/userbrowse.py).

These SHAs make the source claims reproducible. Re-check the integration points and
the streaming-ingress deletion condition when either dependency is updated.
