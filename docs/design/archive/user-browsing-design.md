# Sockseek v4 user browsing

Status: implemented and release-gate verified

Target: Sockseek v4
Scope: remote user profiles, remote share browsing, and downloads selected from a browse

Source and implementation review: 2026-08-20

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
  it once at startup. Missing or invalid images produce a warning and advertise no
  picture without preventing daemon startup; removing the option also advertises
  no picture.
- `sockseek user shares <username>` performs or reuses one browse, reports live
  transfer/index progress, and opens a filesystem-style share browser on a TTY.
- When an `AlbumJob` retrieves a peer directory for folder completion, track-count
  validation, or strict-quality validation, it reuses that peer's completed browse
  response if it is less than five minutes old. Its browse is likewise reusable by
  `sockseek user shares` and ordinary peer-directory retrieval, so these operations
  do not independently browse the same peer while the shared response is fresh.
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
- Browse-selected files follow the ordinary remote-transfer skip policy. An
  existing planned destination is reported as already existing and preserved by
  default; `--no-skip-existing` explicitly permits replacement.
- Informational share and selection byte totals saturate at the public 64-bit
  maximum if their exact mathematical sum is larger. Exact per-file sizes remain
  unchanged, and aggregate overflow never rejects an otherwise valid browse or
  directory download.
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

- Initial browse acquisition uses Soulseek.NET's ordinary materializing
  `BrowseAsync`, matching slskd. Sockseek does not reject a valid browse because of
  an estimated aggregate size, but a peer with an enormous share can temporarily
  spike daemon memory or terminate the process before the disk-backed artifact is
  available. A dependency-provided streaming API is deferred; Sockseek does not
  carry a Soulseek.NET fork for it.
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
4. After Soulseek.NET acquisition, browses use disk-backed artifacts and paging;
   Sockseek never rejects an otherwise valid browse merely because of its estimated
   aggregate size. Whole-response memory safety during acquisition is not promised.
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
3. Validate individual values before retaining them and write browse rows to disk.
   A malformed-input or operational failure SHOULD remain local to the browse or
   profile request. Soulseek.NET's whole-response materialization is the explicit
   exception: process-wide memory exhaustion remains possible for an enormous peer.
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

### What to improve around

- slskd's `/users/{username}/browse` calls Soulseek.NET `BrowseAsync` and returns a
  complete materialized `BrowseResponse`. Sockseek deliberately matches that
  acquisition behavior for now to avoid a dependency fork, while improving the
  downstream boundary with a daemon-side disk artifact, paging, and compact
  selection IDs. This does not bound Soulseek.NET's read, decompression, or object
  graph.
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
  of large-peer memory spikes describe the accepted acquisition risk.

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
RemoteUserProfileService   PeerBrowseService ------ PeerBrowseArtifactStore
                                  |                (ephemeral SQLite artifacts)
Searcher.RetrieveDirectory -------+
                                  |
DaemonSoulseekRuntime
        |
ordinary Soulseek.NET profile + materialized browse calls
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
`Searcher.RetrieveDirectory` and the public user browser share the same peer-browse
acquisition and artifact. Retrieving a directory therefore reuses a completed
artifact while it is fresh instead of issuing another whole-user browse.

### Ownership

`DaemonSoulseekRuntime` continues to own the connected `SoulseekClientManager` and
shared `PeerAccessPolicy`. A daemon-lifetime `RemoteUserProfileService` coordinates
the bounded profile subrequests and their short-lived caches. A daemon-lifetime
`PeerBrowseService` is the sole owner of outbound whole-user browse acquisition,
single-flight, artifact lifetime, and shared cancellation. Neither service creates
another Soulseek client or logs in independently.

`Searcher.RetrieveDirectory` MUST NOT call `ISoulseekClient.BrowseAsync` directly.
It acquires the user's current generation from `PeerBrowseService`, queries the
requested subtree through a short artifact lease, copies the exact results into an
owned `PeerDirectorySnapshot`, and releases the lease. Public browsing, album
folder completion, ordinary peer-directory downloads, and future explicit music
actions therefore cannot grow separate whole-user browse caches or retrieve the
same fresh shares independently.

Every acquisition has the same generation, browse ID, lifecycle, and artifact
regardless of whether an API request or a download job initiated it. An API request
that joins job-initiated work receives that existing ID rather than a wrapper
resource, and retained job-initiated generations may therefore appear in the
bounded browse list.

An immutable `LocalUserProfile` is loaded once from daemon settings and supplied
to both the client manager's fallback user-info resolver and
`SoulseekSharingAdapter`; neither reopens the configured picture per peer request.

Core owns remote-path parsing, materialized browse adaptation, artifact writing, shared
`PeerFileIdentity`/`PeerFileTarget`/`DirectoryTransferPlan` values, and exact
transfer runners. It also owns the abstract `FileDownloadJob`/
`DirectoryDownloadJob` lifecycle bases and concrete remote
`RemoteFileJob`/`RemoteDirectoryJob` subtypes. Server owns authorization, HTTP
resources, live projections, artifact-to-plan resolution, and creation of jobs.
API owns wire DTOs and clients. CLI owns presentation and interaction.

`PeerBrowseArtifactStore` is not a repository for domain history. Each successful
browse is an immutable SQLite file plus small metadata. A staging file is private
to its writer, atomically promoted on success, and deleted on cancellation or
failure. Terminal resources are evicted by a fixed age, terminal-resource count
target, and global artifact byte budget;
restart cleanup removes abandoned staging files. These values are internal safe
defaults, not public configuration. The initial defaults are 24 hours, 4,096
terminal resources, and 2 GiB. These are best-effort retention targets, never
admission or browse-validity limits: active resources are never target-evicted,
and a terminal transition preserves that generation while older terminal/unleased
resources are considered for eviction. Cleanup failures are rate-limited and
retried by later maintenance rather than failing otherwise valid work.
Removing the registry resource also retires its small live-state projection and
stream sequence, so daemon memory does not retain one entry per historical browse.

### Concurrency and reuse

The acquisition key is the configured local Soulseek account plus the peer's exact
wire username, compared ordinally. A successful artifact is fresh for five minutes
from completion: its age MUST be strictly less than `TimeSpan.FromMinutes(5)`, so
it becomes stale at the exact five-minute boundary. Freshness controls automatic
reuse; the longer fixed retention period controls how long a generation remains
addressable by browse ID. A stale artifact may remain readable by its ID but is
never returned by default acquisition.

`refresh` means "do not satisfy this request from an already completed artifact."
It does not demand a second browse after work already in flight. Acquisition follows
this table:

| State for the acquisition key | Ordinary request | `refresh=true` |
|---|---|---|
| No artifact, or only stale artifacts | Start a browse | Start a browse |
| Fresh completed artifact | Return it immediately | Start a browse |
| Browse queued or running | Join its browse ID | Join its browse ID |
| Previous artifact exists while a refresh runs | Join the refresh | Join the refresh |
| Previous refresh failed or was cancelled | Reuse the previous artifact only if still fresh; otherwise start | Start a browse |

- At most one network browse per acquisition key runs at a time. Every concurrent
  caller, including `RetrieveDirectory`, receives the same browse ID.
- A refresh produces a new immutable generation. The previous artifact remains
  readable by its existing ID while the refresh runs and afterward until ordinary
  eviction. Only successful atomic promotion makes the new generation the default.
- A caller joined to a failed or cancelled refresh observes that terminal result;
  it is never silently redirected to older data. Failed and cancelled staging
  artifacts are deleted and never replace the last successful generation.
- Client and job cancellation detaches only that waiter. It does not cancel the
  daemon-owned acquisition. The explicit browse-cancel operation is global and
  cancels the shared acquisition for every waiter.
- Page reads and download resolution hold short internal leases. Closing a GUI tab
  merely unsubscribes, and no client owns a completed resource or must release it.
- Global network-browse concurrency is deliberately small and fixed. Accepted
  browse resources wait in a compact FIFO coordination queue until a network slot
  is available; queue depth is not a validity rule and never rejects a browse.
- Profile subrequests use bounded single-flight caches with short fixed lifetimes.
  A profile refresh bypasses freshness but still joins an identical in-flight call.
- A transition away from an observed logged-in Soulseek session cancels active
  profile calls and browses with a stable `connection-lost` failure. Normal
  connecting and connected-before-login startup states do not cancel the request
  that is establishing the session. Completed artifacts remain readable until
  eviction, but starting downloads still requires a connected daemon.
- Profile cache keys and peer-browse acquisition keys include the configured local
  Soulseek account. Changing accounts never reuses the previous account's profile
  or default browse artifact.

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
    UserProfilePresence Presence,
    UserProfileSectionDto Status,
    UserProfileSectionDto Info,
    UserProfileSectionDto Statistics,
    UserProfileSectionDto PictureSection,
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

`UserProfilePresence` is `online`, `away`, `offline`, or `unknown`. `offline` is a valid
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
`PictureSection` unavailable and leave `Picture` null; the rest of the profile
still succeeds. A peer with no configured picture has an available picture
section and a null `Picture`, so absence is distinguishable from validation or
transport failure.

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
bytes are never advertised. Missing, unreadable, invalid, oversized, or unsafe
input is rejected with a clear startup warning while the daemon continues without
a picture; null means no picture. Normalized bytes stay in memory until restart;
allowed peers receive them in `UserInfo`, while denied peers receive no profile.

Soulseek.NET materializes the complete user-info response, including its picture
byte array, before Sockseek's validation runs. Matching the no-fork browse decision,
version one accepts that dependency-level allocation risk. Sockseek still validates
and bounds pictures before caching, rendering, or returning them.

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
fixed maximum height, leaves transparent pixels on the terminal's default colors,
and uses `▀` with foreground/background 24-bit color for two image rows per cell.
It restores terminal attributes in `finally`. `sixel` preserves transparent pixels
instead of painting them. `sixel` is emitted only when explicitly
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
`waiting-for-peer`, `receiving`, `indexing`, and `ready`. Progress is
monotonic within a phase. Soulseek often cannot supply a trustworthy total, so the
expected byte count is nullable and clients MUST support indeterminate progress.

The resource remains queryable through every terminal state until its fixed
retention expires or earlier count/disk-target eviction. At the expiry instant new reads
return `browse-expired`, even if an existing reader lease temporarily postpones
physical file deletion. Queued and running resources do not age out; their public
`ExpiresAt` is null, and the terminal transition starts a fresh retention window.
A failed or cancelled refresh does not delete the user's prior successful artifact.

### HTTP resources

```http
POST   /api/users/{username}/browses
GET    /api/user-browses?username={username}&state={state}&cursor={cursor}&limit={limit}
GET    /api/user-browses/{browseId}
GET    /api/user-browses/{browseId}/snapshot
POST   /api/user-browses/{browseId}/cancel

GET    /api/user-browses/{browseId}/directories?parentId={id}&query={q}&recursive={bool}&cursor={cursor}&limit={limit}
GET    /api/user-browses/{browseId}/directories/{directoryId}
GET    /api/user-browses/{browseId}/directories/{directoryId}/files?query={q}&cursor={cursor}&limit={limit}

POST   /api/user-browses/{browseId}/downloads
```

`POST .../browses` accepts `{ "refresh": false }`. It returns `202` for queued or
running work and `200` for a reusable completed resource. Joining a single-flight
request returns the existing ID. `refresh=true` bypasses only a completed artifact;
it joins any generation already queued or running for the acquisition key.

The list endpoint is bounded and intended to restore a GUI's recent tabs. It is
not durable browse history. Browse resources have no per-client ownership and are
removed by ordinary retention/eviction rather than a client release operation.
Cancel is idempotent, affects only running work, and is explicitly global because
the acquisition may be shared by API clients and download jobs.

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

The cursor authenticates the artifact generation, collection/filter identity, and
last immutable ID. The daemon recovers the corresponding sort value from the
immutable artifact instead of copying an arbitrarily long peer path into the HTTP
cursor. A cursor is invalid across artifacts or filters and produces `400
invalid-cursor`, not a best-effort result from different data.

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
    ShareVisibility Visibility,
    FileMetadataDto File);
```

`FileMetadataDto` is presentation-safe leaf metadata, not an identity. The
resolved-transfer refactor also composes it into search candidate DTOs so search
and browsing use the same public representation for file facts without equating
their resource identities. Search candidates retain username/full-filename
references and peer evidence; browse files retain artifact-local IDs and expose
only a safe leaf name.

Directory and file IDs are artifact-local immutable integers. File visibility is
carried on the file row itself rather than inferred from a possibly mixed
synthetic directory, so clients can display locked files without making them
selectable. Public APIs do not
return the exact wire path because it is both untrusted and unnecessary for
selection; the artifact retains it internally for protocol requests and job
materialization.

The artifact minimally contains:

- `metadata`: schema version, browse/user/generation, timestamps, counts, and
  completion marker;
- `directories`: ID, parent ID, normalized display components, exact wire path,
  visibility, and direct/recursive aggregates;
- `files`: ID, directory ID, visibility, safe display name, exact wire filename,
  size, and validated Soulseek attributes;
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
valid Windows path. Parsing therefore has three representations:

- wire identity: the exact well-formed string used to request/download from the
  peer, including control characters and the peer's original separator spelling;
- tree identity: an ordinal, separator-normalized key used only to relate browse
  rows; and
- display identity: a terminal-safe projection which renders controls visibly
  (C0 controls use Unicode control pictures and other unsafe display scalars use
  `<U+...>` markers) without changing the retained wire identity.

Empty components, `.`/`..`, separator-only paths, duplicate identities after
normalization, and a filename outside its declared directory are rejected or
quarantined according to one deterministic parser rule. They never become local
filesystem paths. Case is preserved; equality uses the protocol-compatible
comparison already used by Sockseek remote candidates. Locked shares remain
visible as locked but cannot be submitted for download.

Soulseek.NET recognizes one historical Soulseek NS file-size encoding: a 32-bit
unsigned size sign-extended into the 64-bit field. Sockseek consumes the library's
decoded value; other negative sizes remain malformed at Sockseek's artifact
boundary. This matches the package used by both Sockseek and the reviewed slskd
version.

String decoding likewise matches the pinned library: strict UTF-8 is attempted
first, with ISO-8859-1 fallback for legacy peer bytes. The resulting text still
passes Sockseek's well-formed-scalar and structural path validation before it
becomes an artifact row. Control characters are retained in wire identity and
escaped only at display or local-filesystem boundaries.

Artifact aggregates are computed after ingestion in SQL. Counts remain exact;
informational byte totals use saturating addition so aggregate overflow cannot
invalidate otherwise valid rows.
Filter queries are case-insensitive ordinal/display-normalized substring matches
over directory name/path. Version one does not promise full-text ranking.

## Materialized browse ingress

Sockseek uses the ordinary Soulseek.NET 10.0.2 package and calls `BrowseAsync`, as
slskd does. The library receives, decompresses, and materializes the complete public
and locked `BrowseResponse` before returning it. Sockseek accepts the resulting
temporary memory spike and possible process termination for an enormous response
rather than maintaining a private networking fork.

Sockseek adds no guessed aggregate row or byte admission limit. Once `BrowseAsync`
returns, the adapter validates each retained path/file value and writes public and
locked rows into the private SQLite staging artifact in bounded transactions.
Disk-full, malformed retained values, and other artifact failures fail that browse;
aggregate size by itself does not. On success the library object graph becomes
collectable, while all clients page and select from the artifact instead of copying
the complete graph into HTTP, live state, CLI state, or request JSON.

One directory path, filename, or extension is a retained API/storage value rather
than aggregate browse data. Its encoded form is limited to 1 MiB at Sockseek's
artifact boundary. This is an individual-value representation boundary, not an
aggregate browse limit; the number of otherwise valid values remains unrestricted.

The adapter contains an explicit TODO to monitor Soulseek.NET and adopt a public,
cancellable streaming browse API when upstream provides one. The intended upgrade
removes whole-response materialization without introducing a Sockseek-owned fork.

The daemon injects `PeerBrowseService` into `Searcher.RetrieveDirectory`; it
acquires with `refresh=false`, then performs an indexed subtree query against the
artifact. Two directory retrievals, or a directory retrieval and public user
browse, reuse the same successful generation during its five-minute freshness
period. The returned `PeerDirectorySnapshot` owns its exact target data and has no
lifetime dependency on the artifact. Cancelling the calling job stops its wait but
leaves shared acquisition running.

One-shot execution has no daemon-lifetime cache to share. It uses
`OneShotPeerDirectorySource`, which still causes Soulseek.NET to materialize the
complete peer browse but retains only the requested public subtree after the call
returns. Daemon execution should use the shared artifact-backed service instead.

`GetDirectoryContentsAsync` MAY explicitly refresh a known selectable directory
for a future feature, but cannot discover an unknown peer tree and is not a
substitute for the initial whole-user browse.

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
The fingerprint includes the URL browse generation as well as the body, so the
same key cannot alias selections from two artifacts.
The daemon retains the most recent 4,096 completed keys for up to 24 hours;
active submissions are never evicted. Retrying after that bounded window is a new
submission, so durable automation should also retain the returned workflow ID.
The idempotency lookup precedes artifact resolution: once a request succeeds, an
identical retry returns its original response even if the ephemeral browse artifact
has since expired or been evicted.

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
skipped, and is returned with a successful submission. Before confirmation, the
interactive CLI computes the directly observable file, byte, and locked-branch
totals from the immutable row aggregates already loaded for its selection cart.
The server's submission response remains authoritative for canonicalization and
policy checks. The artifact lease can end after job construction because the job
owns its target data; eviction never changes an already submitted workflow.

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
    [--picture auto|sixel|pixels|none]
    [--remote <url>] [--json]
```

Human output keeps unavailable fields compact and clearly separates `offline`
from `unknown`. In `auto` mode, the CLI actively queries terminal capabilities and
uses sixel only when support is reported; it falls back to the portable pixel
renderer when probing is unavailable or times out. `--picture sixel` remains an
explicit override for terminals or intervening multiplexers that do not answer the
probe. JSON is exactly `UserProfileDto`; picture content is represented by its URL
and metadata and is never fetched merely to produce JSON.

### Interactive shares

```text
sockseek user shares <username> [--refresh]
    [transfer/output-parent/profile options] [--remote <url>]
```

The command posts the browse, subscribes to its live resource when available, and
falls back to bounded HTTP polling with backoff. Ctrl+C during acquisition cancels
the CLI wait and prints the browse ID; it does not cancel the shared daemon browse
because another client or download job may be observing the same single-flight
resource. The explicit `shares-cancel` command globally cancels it for every
waiter. Once complete, the browser requests only visible directory and file pages.

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

Before `D` submits, the CLI summarizes the cart's file and byte totals and locked
branches skipped from the immutable browse data it has already loaded. If
submission fails, it leaves the cart intact and shows the operational or validation
error. On confirmation and success it prints the server's authoritative resolution
summary and root workflow ID, then returns; normal daemon monitoring owns
subsequent progress.

### Scriptable shares

```text
sockseek user shares-page <browse-id>
    [--parent <directory-id> | --files <directory-id>] [--query <text>]
    [--cursor <opaque>] [--limit <n>] [--remote <url>] [--json]

sockseek user shares-download <browse-id>
    (--folder <directory-id> | --file <file-id>)...
    [transfer/output-parent/profile options]
    [--request-id <guid>] [--remote <url>] [--json]

sockseek user shares-cancel <browse-id> [--remote <url>] [--json]
```

`shares --json` starts or reuses a browse, waits for a terminal state, and prints
only `UserBrowseDto`. It never silently changes into a full graph dump. Scripts use
the returned browse ID with `shares-page`, then submit stable IDs with
`shares-download`. Omitting `--request-id` generates one for that invocation.
Directory IDs always mean complete public subtrees.
`shares-cancel` is idempotent and returns the resulting browse resource. It is a
global operation on shared acquisition, not a way to detach only the invoking CLI.

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
Task<UserBrowseDto> CancelUserBrowseAsync(...);
Task<PageDto<BrowseDirectoryEntryDto>> GetUserShareDirectoriesAsync(...);
Task<BrowseDirectoryEntryDto> GetUserShareDirectoryAsync(...);
Task<PageDto<BrowseFileEntryDto>> GetUserShareFilesAsync(...);
Task<StartUserShareDownloadsResponseDto> StartUserShareDownloadsAsync(...);
```

The picture client exposes a response/stream plus media metadata and never reads
the entire body unless its caller elects to do so within the advertised bound.
All new DTOs are registered in source-generated JSON metadata. The live client
adds subscribe/unsubscribe and snapshot/delta types for `UserBrowse` only.

## Errors and recovery

Errors use the existing `ApiErrorDto` envelope and add these stable codes:

| HTTP/resource | Code | Meaning / client action |
|---:|---|---|
| 400 | `invalid-username` | Fix the username before retrying. |
| 400 | `invalid-request` | Fix a page limit, query, or state filter. |
| 400 | `invalid-selection` | Fix empty, mixed, or malformed selection IDs. |
| 400 | `invalid-remote-transfer-option` | Remove music/search-only overrides from a share transfer submission. |
| 400 | `invalid-name-format-variable` | Remove variables unavailable to remote downloads or choose music interpretation. |
| 400 | `invalid-cursor` | Restart paging from the first page. |
| 404 | `user-not-found` | User is inaccessible or denied by peer policy. |
| 404 | `picture-unavailable` | The user has no valid, currently available profile picture. |
| 404 | `directory-not-found` | Refresh navigation from a known directory page. |
| 409 | `browse-not-ready` | Wait on the browse resource. |
| 409 | `idempotency-conflict` | Use a new request ID for different content. |
| 410 | `browse-expired` | Start/refresh the user's browse. |
| 502 | `peer-response-invalid` | Peer data was malformed; refresh may retry. |
| 503 | `soulseek-unavailable` | Connect the daemon or retry after reconnect. |
| terminal browse | `peer-timeout` | The peer did not respond within a bounded transport phase. |
| terminal browse | `connection-lost` | Reconnect, then start or refresh the browse. |
| terminal browse | `browse-cancelled` | The shared browse was explicitly cancelled. |
| terminal browse | `peer-io-failed` / `browse-failed` | Retry; the acquisition failed without publishing a partial artifact. |

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

- Verify the production adapter calls ordinary Soulseek.NET `BrowseAsync` and writes
  both public and locked directories, files, and attributes to the row sink.
- Valid empty, Unicode, mixed-separator, and missing-parent artifacts.
- Structurally invalid path components, malformed Unicode, negative values,
  duplicate identities, and invalid attributes fail at Sockseek's retained-value
  boundary. Control-bearing paths remain browseable and downloadable with safe
  display and local placement projections. No tests encode a guessed total-row or
  total-byte validity limit.
- A large materialized fixture remains pageable after acquisition. Peak-memory
  growth during Soulseek.NET acquisition is an accepted limitation, not a release
  gate.
- Cancellation/timeout during package receive, artifact transactions, indexing,
  and atomic promotion.
- Library-upgrade interoperability fixtures captured without personally identifying
  real peer data.

### Artifact and API

- Atomic staging promotion, restart cleanup, age/count/byte eviction, reader leases, and
  reuse/refresh generations.
- TimeProvider-driven boundary tests prove reuse one tick before five minutes from
  successful completion and a new acquisition at the exact five-minute boundary.
  Freshness and retention are tested independently.
- The full acquisition state table is covered, including ordinary and refresh
  callers joining the same in-flight generation, refresh failure preserving but
  not silently substituting the previous generation, and account-key isolation.
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
- Submission returns its resolution summary and creates one `JobList` with independent
  `RemoteDirectoryJob(RemoteDirectorySource.Resolved)` children and nested
  `RemoteFileJob` leaves, with no browse/search afterward.
- The resolved source cannot call directory retrieval. Peer-directory sources,
  album folder completion, and the user-browser share `PeerBrowseService`: no
  artifact causes one wire browse, concurrent callers single-flight, a fresh
  artifact causes none, and explicit refresh causes one new generation.
- Cancelling a directory or album job detaches its wait without cancelling an
  acquisition observed by another job or API client; explicit browse cancellation
  produces one shared terminal cancellation.
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
2. The production path uses the ordinary Soulseek.NET package and its materializing
   `BrowseAsync`; no vendored fork or private protocol implementation remains.
3. A large fixture can be paged and multi-selected without sending the whole graph
   through HTTP, live state, CLI memory, or request JSON.
4. Existing workflow monitoring observes browse-selected downloads without a
   special compatibility path.
5. A public/locked materialized response passes through the production adapter, and
   retained-value/artifact failures are tested with intentionally malformed local
   fixtures.
6. Documentation and CLI help contain the user-facing commands above and explain
   that browse caches are ephemeral.

## Delivery sequence

1. Complete
   [`resolved-remote-transfer-refactor-plan.md`](resolved-remote-transfer-refactor-plan.md),
   including exact identity, abstract lifecycle state, remote/music policy
   separation, album/direct-link regressions, shared runners, concrete job
   payloads, and progressive directory execution.
2. Add materialized browse adaptation and the artifact schema/store; record the
   accepted dependency memory tradeoff and upstream-streaming TODO.
3. Add local profile loading, remote profile service, picture validation,
   `PeerBrowseService`, and artifact lifecycle; route `Searcher.RetrieveDirectory`
   through it and centralize whole-user `BrowseAsync` acquisition there.
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

- [x] One shared Soulseek runtime and peer policy are reused.
- [x] Profile data is composite, partial, bounded, and excludes peer endpoints.
- [x] Local pictures are normalized for peers; remote pictures are safely rendered.
- [x] Browse ingress materializes through Soulseek.NET, then writes a disk artifact
  without aggregate size-based refusal.
- [x] Successful browses become immutable disposable SQLite artifacts.
- [x] Public browsing and directory retrieval share one five-minute-fresh
  peer-browse acquisition and artifact path.
- [x] Browse lifecycle is durable for its short resource lifetime and live-visible.
- [x] Directories/files are paged; large collections never enter live deltas.
- [x] Multiple folder/file selections resolve from artifact IDs on the server.
- [x] The resolved remote-transfer refactor and its regression gates are complete.
- [x] Downloads are `JobList`/`RemoteDirectoryJob` workflows with no
  second browse or music-policy leakage.
- [x] The share UI is filesystem-shaped and album interactive behavior is unchanged.
- [x] Typed clients make the same surface usable by CLI and future WebUI.
- [x] Retained-value, cancellation, policy, paging, and job tests pass; acquisition
  memory growth is the documented dependency limitation.

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
  That revision references Soulseek.NET 10.0.2, the same ordinary package version
  used by Sockseek.
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
  the source corresponding to Sockseek's package version at review time, especially
  [`ISoulseekClient`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/ISoulseekClient.cs),
  [`WaitKey`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Common/WaitKey.cs),
  [`BrowseResponseFactory`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Messaging/Messages/Peer/BrowseResponseFactory.cs),
  and
  [`MessageConnection`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Network/MessageConnection.cs).
- Nicotine+ commit
  [`d08f755b749e781b087705ed61822f64531e5d8c`](https://github.com/nicotine-plus/nicotine-plus/tree/d08f755b749e781b087705ed61822f64531e5d8c), especially its exact-key
  [`users.py`](https://github.com/nicotine-plus/nicotine-plus/blob/d08f755b749e781b087705ed61822f64531e5d8c/pynicotine/users.py) and [`userbrowse.py`](https://github.com/nicotine-plus/nicotine-plus/blob/d08f755b749e781b087705ed61822f64531e5d8c/pynicotine/userbrowse.py).

These SHAs make the source claims reproducible. Re-check the integration points and
whether upstream now provides a public streaming browse API when either dependency
is updated.
