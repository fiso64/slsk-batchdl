# Sockseek v4 Sharing and Uploads

**Status:** Proposed design and implementation plan
**Target:** v4.0
**Research date:** 2026-07-29
**Sockseek baseline:** `sharing` at `9e92444dabfafb2ec65dc4f7c52cdf262e4c1ed1`
**Primary reference:** [`persistence-design.md`](archive/persistence-design.md)
**slskd source reviewed:** `e42a525d700d6dc343f316447803138b8ea2fbe3`
**Soulseek.NET 10.0.2 source reviewed:** `52fc3e4267114d8cd9492cb4d7438b3eca0267bf`

This document defines the first releasable sharing and upload architecture for
Sockseek v4. It is intentionally narrower than slskd, but it leaves clean seams
for later queue policy, moderation, relay, and large-library work.

Normative terms such as **MUST**, **SHOULD**, and **MAY** are used in the RFC 2119
sense.

---

## 1. Decision summary

The recommended implementation is built around these decisions:

1. **Sharing and uploads are daemon-lifetime services, not `DownloadEngine`
   features.** They use the same Soulseek session as downloads, but they must
   continue to exist when no download workflow is active.
2. **The published share catalog is a rebuildable, disk-backed index in its own
   SQLite database.** It is not part of Sockseek's historical persistence
   database or persistence writer.
3. **A scan builds a new catalog generation and publishes it atomically.** A
   generation contains the SQLite index and either its protocol-ready browse
   artifact or a typed unsupported-oversize browse marker. The previous
   generation remains available for search, browse, and upload resolution until
   the new generation is complete and validated.
4. **An alias is optional in configuration, but every published root has an
   effective alias.** `[Alias]/absolute/path` uses the explicit alias;
   `/absolute/path` derives the alias from the final directory name. Only the
   effective alias and relative path are exposed to peers.
5. **Filesystem paths received from peers are never resolved by string
   concatenation.** Uploads resolve an exact catalog entry to a canonical local
   file and revalidate it when opening the stream.
6. **Uploads use a global slot limit and a strict per-user round-robin queue.**
   Files remain FIFO within each user's queue, and at most one upload per
   normalized username is active. The latter is also a hard Soulseek.NET 10.0.2
   constraint, so enforcing it in Sockseek prevents an invisible second queue
   inside the library.
7. **Exact username and exact IP deny entries are v4.0 requirements.** Username
   checks apply to every resolver. Endpoint-bearing callbacks also check IP
   directly; incoming search resolves the endpoint before sending a non-empty
   response when IP policy is configured. CIDR, country, DNS, managed-list, and
   regular-expression matching are explicitly outside the first release.
8. **The running upload coordinator is authoritative for active queue and
   transfer state.** Durable transfer history is an asynchronous projection,
   consistent with the persistence architecture already implemented in v4.
9. **Upload transfers reuse the direction-neutral transfer model.** Uploads have
   `Direction = Upload`, but no download `JobId` or `WorkflowId`; those ownership
   fields must become nullable in Core and API contracts.
10. **Large-library behavior is a release contract, not a future optimization.**
    The scanner, catalog readers, browse path, API, and scheduler must satisfy the
    boundedness and measured performance gates in sections 21 and 22.
11. **Administrative mutation of share roots is configuration-only for v4.0.**
    Sockseek must not expose unauthenticated APIs that reveal or mutate local
    filesystem paths.
12. **Comparable user-facing defaults follow slskd unless Sockseek has a recorded
    architectural or safety reason to diverge.** v4.0 therefore defaults to ten
    upload slots, unlimited upload speed, CPU-count scan workers, no periodic
    rescan, and 10/500/500 incoming-search throttling. The disk-only catalog is
    an intentional architectural exception, not an overlooked parity gap.
13. **The public API follows pull request #194's replicated-state model.**
    Sharing and upload runtime state extend the existing revisioned daemon
    singleton; uploads remain ordinary transfer resources. HTTP supplies bounded
    snapshots, commands, details, and paginated live-queue/history resources,
    while SignalR supplies typed small-state/active-resource deltas.
    `SockseekApiClient`, `SockseekLiveClient`, and `DaemonClientStore` form the
    reusable client surface for both CLI and GUI consumers.
14. **Full browse uses Soulseek.NET's `RawBrowseResponse` stream path.** The
    browse artifact is built in a bounded streaming pass and served from an
    immutable generation file. Building a complete `BrowseResponse` object graph
    or `byte[]` is not an acceptable v4.0 implementation.
15. **Network-supplied excluded search phrases are part of serving correctness.**
    Sockseek tracks the list supplied by the Soulseek server and applies it,
    along with the request's own exclusions, before sending search results.
16. **Operator commands are authorization boundaries.** Scan and transfer
    cancellation use the same named operator policy as the rest of the daemon
    API. Until v4 authentication lands, non-loopback API exposure remains an
    explicitly insecure deployment and must not be made safer-looking by this
    feature.

### Release recommendation

Ship a useful, conservative baseline rather than attempting slskd parity:

- disk-backed full catalog and browse-artifact builds with atomic generation
  publication;
- alias-less and explicitly aliased roots;
- search, browse, and directory responses;
- resumable upload streams;
- global slots and global speed cap;
- strict per-user fair queuing and queued-size limits;
- exact username and exact IP blacklists;
- active upload state and durable upload history;
- manual cancellation, manual rescan, and optional periodic rescan;
- remote daemon control through `sockseek share ... --remote` and
  `sockseek transfers ... --remote`;
- explicit health, scan diagnostics, and release performance evidence.

Defer user groups, priorities, daily/weekly quotas, relay, a selectable memory
cache mode, managed blocklists, and runtime policy editing.

---

## 2. Scope

### 2.1 Required for the first sharing release

The first release MUST provide:

- One or more absolute local share roots.
- Optional explicit aliases and deterministic derived aliases.
- No exposure of the absolute local root when no explicit alias is supplied.
- Excluded subtrees.
- Case-insensitive regular-expression file/path filters by default.
- A persistent catalog that survives daemon restarts.
- An initial scan, manual rescan, cancellation, and optional periodic rescan.
- Concurrent search, browse, and upload resolution while a rescan is running.
- Search responses sourced from the published catalog.
- Browse responses containing explicit directory entries, including empty
  directories where the protocol requires them.
- Exact remote-path-to-local-file resolution.
- Queued and direct upload handling through Soulseek.NET.
- Resume from a peer-provided byte offset when the offset is valid.
- A global upload slot limit.
- A global upload bandwidth limit, expressed in KiB/s.
- Fair queueing across users.
- Per-user queued file and byte limits.
- Exact username blacklist entries.
- Exact IPv4 and IPv6 blacklist entries.
- Active upload state in v4's daemon live-state stream.
- Durable upload and upload-attempt history.
- Startup reconciliation of uploads that were active when the process stopped.
- Bounded memory behavior and explicit response/request concurrency limits.
- Measured release qualification against the scale fixtures in section 21.
- Path traversal, link, and share-boundary protections.
- Hard regex execution timeouts and remote-path collision detection.
- Soulseek-server excluded-phrase filtering for outgoing search results.
- Daemon-backed `share` and `transfers` CLI commands that support `--remote`.

### 2.2 Optimizations investigated against slskd

The following two optimizations are not current slskd behavior and are not
independently mandatory for Sockseek v4.0:

- **Prior-generation metadata reuse:** slskd resolves file information and calls
  its metadata factory for every discovered file on each scan.
- **Incremental/changed-subtree scans:** slskd updates rows in place and prunes
  stale timestamps, but it still enumerates every configured root and rebuilds
  its filename index. Open issue #1773 requests scanning one configured directory
  without rescanning all shares.
They MAY be implemented when useful. A specific optimization becomes mandatory
if the baseline implementation cannot satisfy a release performance or
bounded-memory gate without it.

File-backed browse is different: current slskd writes `browse.cache` and serves
it through Soulseek.NET's `RawBrowseResponse`. Its cache build still constructs
the complete directory/file object graph and then a complete serialized
`byte[]`, which is the source of the large-library failure mode. Sockseek MUST
retain the file-backed serving idea but replace the unbounded build with a
streaming generation-local serializer.

### 2.3 Desirable but not a v4.0 release requirement

- Reusing metadata from an unchanged file in the prior catalog generation.
- Incremental scans of one root or changed subtree.
- Live reload of the global speed limit.
- A local-admin API for changing share configuration after authentication and
  authorization are in place.
- Per-root metadata extraction settings.
- More detailed scan-error browsing.

### 2.4 Explicitly out of scope

The following slskd features are **not** proposed for Sockseek v4.0:

- Controller/agent relay and remote share repositories.
- A user-selectable memory-versus-disk share-cache mode; v4.0 deliberately uses
  one disk-backed generation model.
- User-defined groups.
- Privileged-user and leecher groups.
- Per-group priority, slot, speed, or queue-strategy settings.
- Configurable FIFO versus round-robin policy; v4.0 uses one fair policy.
- Daily and weekly file/byte/failure quotas.
- Automatic leecher classification.
- Username regular-expression blacklists.
- CIDR/range, country, ASN, hostname, or DNS-derived IP blacklists.
- Managed external blocklists.
- Locked/private files or artificial-scarcity features.
- FileSystemWatcher/inotify-based automatic share-tree change detection. Current
  slskd does not implement this; issue #1772 is an open request for it.
- Multi-host sharing under one Soulseek identity.
- Automatic requeue of interrupted uploads after restart.
- Remote file deletion or arbitrary filesystem browsing APIs.
- Unbounded upload-history loading at startup.
- A claim of multi-million-file full-browse support unless the release
  qualification proves a safe response path.
- Zero-byte-file sharing until a separate interoperability test settles the
  contract. Soulseek.NET 10.0.2's XML documentation says sizes below one byte are
  rejected, while its stream-overload guard rejects only negative sizes; v4.0
  must not infer support from that inconsistency.

The architecture below deliberately permits several of these later without
putting them in the first release's critical path.

---

## 3. Current v4 context

Sockseek's `TODO.md` places sharing/uploads in the v4.0 roadmap alongside chats,
user browsing, and the Web UI. The roadmap also establishes a useful transport
rule: SignalR carries replicated live state, HTTP carries snapshots and
historical/recovery queries, and activity is separate from authoritative state.
Pull request #193 is the integration branch for that work. Pull request #194
and `API-IMPROVEMENTS-DISCUSSION.md` establish the corresponding public API
philosophy: a normal monitor reconstructs authoritative state from a bounded
snapshot plus ordered typed deltas; activity events are supplemental; retained
history is paginated; and local CLI, remote CLI, and GUI consumers should use one
reusable client/reducer implementation.

The repository already contains most of the architectural prerequisites:

- Immutable Core change records in
  `Sockseek.Core/Events/CoreChanges.cs`.
- Immutable snapshots in
  `Sockseek.Core/Snapshots/CoreSnapshots.cs`.
- A daemon live-state projection in
  `Sockseek.Server/EngineStateStore.cs`.
- Snapshot/delta contracts in
  `Sockseek.Api/Contracts/LiveState.cs`.
- A persistence boundary and one-writer design in `Sockseek.Persistence`.
- Direction-neutral persisted transfers and nullable persisted `JobId` and
  `WorkflowId` fields.
- A daemon-owned `SoulseekClientManager` created by `EngineSupervisor`.

There are, however, several concrete blockers.

### 3.1 Transfer ownership is still download-shaped in Core and API

`TransferSnapshot` currently requires non-null `JobId` and `WorkflowId`, as does
`TransferIdentityFieldsDto`. An incoming upload belongs to the daemon and a
remote user, not to a download job.

Before adding uploads:

```csharp
public sealed record TransferSnapshot(
    Guid Id,
    long Revision,
    TransferSnapshotDirection Direction,
    Guid? JobId,
    Guid? WorkflowId,
    // ...
);

public sealed record TransferIdentityFieldsDto(
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? CandidateKey);
```

The persistence entity and `TransferPersistenceMutation` already use nullable
ownership, so the database shape does not need to be made less strict.

### 3.2 Core transfer changes are named for downloads

The current events include `DownloadStartedChange`,
`DownloadProgressedChange`, and `DownloadStateChangedChange`, while terminal
changes are already more generic.

Do not add a parallel copy of every transfer event for uploads. Normalize the
state-bearing contract:

```text
TransferRegisteredChange
TransferProgressedChange
TransferStateChangedChange
TransferAttemptStartedChange
TransferAttemptCompletedChange
TransferAttemptFailedChange
TransferAttemptCancelledChange
TransferCompletedChange
TransferFailedChange
TransferCancelledChange
```

A compatibility layer can continue publishing download-specific activity events
where the CLI expects them. State projection and persistence should consume the
generic changes.

### 3.3 `EngineStateStore` is attached to `DownloadEngine`

`EngineStateStore.AttachEngine(DownloadEngine)` subscribes directly to one
runtime and scopes transfers through workflow IDs. This cannot represent an
upload that has no workflow.

Recommended change:

- Rename/generalize it to `DaemonStateStore`, or introduce a daemon-level
  projection coordinator that owns the existing store plus sharing/upload
  projections.
- Accept Core changes from multiple daemon runtimes.
- Store upload transfers in the daemon scope even when `WorkflowId` is null.
- Keep workflow streams filtered to transfers whose `WorkflowId` matches.

Do not create a fake upload workflow merely to satisfy the current DTO shape.

### 3.4 Soulseek client options are assembled once

`SoulseekClientManager.CreateClientOptions()` currently supplies only a static
`userInfoResolver`. Sharing requires the browse, directory-contents, incoming
search, upload enqueue, and place-in-queue resolver hooks to be present when the
client is created.

The construction path should therefore accept a daemon-lifetime inbound request
router before it constructs `SoulseekClientOptions`:

```csharp
public interface ISoulseekInboundRequestRouter
{
    Task<UserInfo> ResolveUserInfoAsync(string username, IPEndPoint endpoint);
    Task<BrowseResponse> ResolveBrowseAsync(string username, IPEndPoint endpoint);
    Task<IEnumerable<Directory>> ResolveDirectoryAsync(
        string username,
        IPEndPoint endpoint,
        int token,
        string directory);
    Task<SearchResponse?> ResolveSearchAsync(
        string username,
        int token,
        SearchQuery query);
    Task EnqueueUploadAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath);
    Task<int?> ResolveQueuePositionAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath);
}
```

These are the exact resolver shapes in Sockseek's pinned Soulseek.NET 10.0.2
package. The library calls the incoming upload delegate `EnqueueDownload`
because the peer is asking to download from us; Sockseek should keep the clearer
`EnqueueUploadAsync` domain name at the adapter boundary.

The callbacks do not receive cancellation tokens. The adapter must therefore
use bounded admission channels, explicit deadlines, cancellable SQLite commands,
and a final deadline check before committing an upload admission. Timing out the
callback while allowing detached work to keep accumulating or later create a
transfer is not acceptable.

`SoulseekClientOptionsPatch` can replace resolver delegates and the maximum
upload speed, but not the maximum concurrent upload count. Resolver wiring
should still occur before the first connection so reconnects do not change
behavior. Upload slots are a session-construction setting in v4.0.

### 3.5 Remove manual share-count settings

`EngineSettings.SharedFiles`, `EngineSettings.SharedFolders`, and
`EngineSettings.NoModifyShareCount` are placeholders for a client that does not
have a real catalog. Once sharing exists, retaining them would create two
conflicting authorities for the same protocol values.

The sharing implementation PR MUST therefore remove:

- `SharedFiles`, `SharedFolders`, and `NoModifyShareCount` from `EngineSettings`
  and profile copying;
- `--shared-files`, `--shared-folders`, `--nmsc`, and
  `--no-modify-share-count` from configuration parsing, generated help, README
  documentation, and tests; and
- the conditional manual-count branch in `SoulseekClientManager`.

For v4.0, the published catalog is the only source of shared file and directory
counts. Counts MUST be sent after login, after successful catalog publication,
and after reconnect. If no valid catalog exists, the daemon publishes zero
counts. A failed or cancelled rescan leaves the prior published counts intact.
`UserDescription` remains a Soulseek identity setting, not a sharing setting.

### 3.6 Roadmap boundaries

The remaining v4 roadmap makes three naming and dependency boundaries important:

- This feature serves **our local shares**. Future user browsing retrieves a
  remote user's description, picture, and shares and should live under
  `/api/users/{username}/...`, not reuse the local catalog or `/api/sharing`
  resource.
- A future Web UI may inspect our own published tree through a paged
  `/api/sharing/catalog/...` peer-view projection backed by
  `IShareCatalogReader`. It must expose remote aliases/paths only and is distinct
  from both sensitive root configuration and remote-user browsing; no new
  catalog model is required.
- `PeerAccessPolicy` is daemon-wide and may later be reused by chats. Its
  configuration is therefore named `peer-blocked-user`/`peer-blocked-ip`, not
  `sharing-blocked-*`.
- Read-only status and operator mutations have different security consequences.
  Scan start/cancel and transfer cancellation must pass a named operator
  authorization policy so the v4 authentication work can secure them without
  rewriting endpoint handlers or `AvailableActions`.

v4.0 catalog readers expose only the public published catalog and therefore do
not carry a speculative visibility argument. A future locked/private-share
design must introduce an explicit request authorization context together with
its artifact strategy; adding a nominal enum now would not answer whether that
feature needs per-policy artifacts, filtered artifacts, or a different serving
path. The catalog reader is internal, so its contract can evolve when those
requirements are known.

---

## 4. What slskd does

This section records the useful parts of slskd's design without treating it as a
specification Sockseek must copy. The review is based on current `master` as of
2026-07-29 and the linked public issues.

### 4.1 Share configuration and public-path behavior

slskd supports:

- Any number of absolute share directories.
- Optional aliases using `[Alias]absolute/path` syntax.
- Alias-less entries containing only an absolute directory.
- Excluded directories.
- Regular-expression filters, case-insensitive by default.
- A share cache stored in memory or on disk.
- Configurable scan workers.
- Optional timed full rescans through cache retention.
- Search, full browse, and directory-contents resolvers backed by the share
  service.
- Share-count publication after initialization.

When an alias is omitted, slskd derives it from the shared directory's final
name: for example, `D:\Music` becomes the remote root `Music`. Peers see the
alias and relative path, not `D:\Music` or another absolute host path. Aliases
must be unique, non-empty, and contain no path separator.

Sockseek should implement the same privacy-preserving behavior:

```text
/mnt/storage/MEDIA/MUSIC       -> MUSIC\...
[Music]/mnt/storage/MEDIA/MUSIC -> Music\...
/srv/slsk-test                 -> slsk-test\...
```

The explicit form is useful when the final directory name is private, unstable,
or collides with another root. The alias-less form should be the common concise
case.

slskd's browse implementation explicitly emits directory records rather than
inferring directories only from files. This matters because empty directories
and directories containing only subdirectories otherwise appear incorrectly to
some clients.

### 4.2 Share scanner and repository

The current slskd scanner:

- Rejects concurrent scans with a semaphore.
- Compiles filters once for the scan.
- Skips hidden and system entries.
- Enumerates all directories under all configured roots up front.
- Applies exclusions and deduplicates the resulting directory set.
- Uses a bounded channel for per-directory file work.
- Uses multiple metadata workers.
- Stores files and directories in a SQLite repository.
- Timestamps records touched by a scan and prunes stale records at the end.
- Rebuilds its SQLite FTS5 filename index and vacuums after a successful scan.
- Does not prune stale records when the scan is cancelled.

The SQLite repository contains scan metadata, explicit directories, files, and
an FTS filename index. It enables WAL and uses backup/restore operations.

slskd's `memory` mode is not a separate object/dictionary implementation: it is
a shared in-memory SQLite database kept alive by an open connection. slskd also
maintains an on-disk backup and restores that backup into the in-memory database
at startup. Its `disk` mode opens the working SQLite database from a file. This
means supporting both modes includes startup restore, backup, keepalive, and
qualification behavior in addition to choosing a connection string.

The separation of scanner, repository, and serving service is sound. Sockseek
should preserve it, while changing two details:

1. do not materialize every directory path in a whole-tree `HashSet`; use a
   bounded streaming traversal because root overlap is rejected up front; and
2. build an unpublished generation rather than mutating the only published
   catalog during a scan.

### 4.3 What slskd does not currently optimize

The distinction matters for v4 scope:

- **No prior-row metadata reuse:** every discovered file is passed through file
  information resolution and `SoulseekFileFactory.Create(...)` during a scan.
- **No changed-subtree scan:** timestamp upserts make the database update
  incremental internally, but filesystem discovery remains a full traversal.
  Issue #1773 explicitly requests a way to rescan one shared directory.
- **No share-tree watcher:** automatic freshness is periodic full rescan through
  cache retention. Issue #1772 requests live add/delete/rename detection.

slskd does have a file-backed serialized browse cache. `CacheBrowseResponse`
calls `ShareService.BrowseAsync`, constructs a complete `BrowseResponse`, calls
`ToByteArray()`, and writes the result to `browse.cache`.
`BrowseResponseResolver` then opens that file and returns `RawBrowseResponse`.
The serving half is appropriately streaming; the build half still holds both a
whole-tree object graph and a whole response byte array. Sockseek should improve
that build path rather than incorrectly treating file-backed browse as absent.

### 4.4 Soulseek.NET integration

slskd wires daemon services into Soulseek.NET through resolver delegates for:

- User information.
- Full browse response.
- Directory contents.
- Incoming search response.
- Enqueueing a requested upload.
- Place-in-queue queries.

It initializes the share service before connecting, then publishes share counts.
Search responses include files plus current upload availability information,
including whether a slot is free and a queue-length forecast.

For incoming search, Soulseek.NET 10.0.2 supplies a username and token but not an
endpoint. slskd performs a cheap cached username decision before catalog work,
then resolves the user's endpoint and applies IP policy before sending a
non-empty response. Sockseek should use the same two-stage shape when exact IP
rules are configured.

Incoming search handling has explicit concurrency and circuit-breaker limits.
Upload enqueue handling serializes requests per user and bounds global request
processing, which prevents a single peer or request storm from exhausting the
application.

One pinned-library edge must not be copied blindly:
`PeerMessageHandler` disposes `RawBrowseResponse.Stream` only after a successful
write, not from a `finally`. Section 8.6 therefore prefers an upstream/forked
disposal fix, retains an exact-EOF self-expiring wrapper for Sockseek-owned
resources until it is proven unnecessary, and relies on a separately verified
connection timeout for a network write already in progress.

### 4.5 Upload service, scheduler, and governor

slskd keeps three responsibilities separate:

1. `UploadService` validates a requested file, enforces user/group limits,
   creates durable transfer records, starts Soulseek.NET uploads, and maps
   terminal failures.
2. `UploadQueue` schedules queued files according to group priority and either
   FIFO or round-robin behavior.
3. `UploadGovernor` enforces global and group token-bucket bandwidth limits.

The upload queue can estimate a file's queue position and forecast where a new
request would land. slskd notes in its own implementation that its group
round-robin estimate is not perfectly fair because only ready uploads are
eligible; faster peers can rotate through the queue more quickly.

For Sockseek, the useful lesson is the separation—not the full group model.
Sockseek can implement strict user-round-robin directly and avoid several layers
of policy in v4.0. It should also avoid launching one long-lived task per queued
upload as slskd currently does. Soulseek.NET enforces one concurrent upload per
username internally; Sockseek's scheduler must model that constraint itself so
work never waits in a hidden library queue after consuming a Sockseek slot.

### 4.6 Blacklist behavior

slskd's built-in blacklisted group can match explicit usernames, username
patterns, and CIDRs. Blacklisted peers are prevented from receiving search
results, browsing files, retrieving directory contents, and enqueueing new
uploads; private/chat messages are also ignored. Existing active or queued
transfers are not automatically cancelled when a user is newly blacklisted.

Sockseek v4.0 adopts only the common, deterministic subset needed by sharing:

- exact username entries;
- exact IPv4 or IPv6 address entries;
- no regex, CIDR, country, ASN, hostname, or managed blocklist;
- enforcement for search, browse, directory contents, and new upload admission.

The policy belongs in a daemon-wide peer access component so chats can reuse it
later without moving configuration or changing semantics.

### 4.7 Configuration breadth deliberately not copied

slskd additionally exposes:

- Per-group slots, speed, priority, and FIFO/round-robin strategy.
- Privileged, default, leecher, and user-defined groups.
- Per-user queued, daily, and weekly file/byte/failure limits.
- Managed blacklist files and CIDR matching.
- Upload transfer retention.

This breadth is useful evidence about real operator needs, but it also shows how
quickly upload policy becomes its own product area. Sockseek's first release
stops after common global controls, per-user queued limits, and exact
username/IP deny entries.

### 4.8 Lessons from slskd issues and pull requests

Several public issues are particularly relevant:

- **Share scanning at scale:** issue #610 reports an out-of-memory failure while
  scanning roughly 700,000 files. A bounded work channel is not sufficient if
  all directory names are first accumulated in memory.
- **Whole-catalog memory is risky:** issue #1593 discusses a memory-backed cache
  with hundreds of thousands of files. Issue #1765 reports a 4.3-million-file
  disk catalog succeeding while `browse.cache` construction still exhausts
  memory. A disk index and file-backed serving are not sufficient if artifact
  generation first materializes the whole catalog as an object graph or byte
  array.
- **Unbounded history loads are risky:** issue #1291 shows startup OOM behavior
  while iterating a large transfer query. Active state must be bounded, and
  history must be paginated.
- **Scan visibility matters:** issue #443 asks for real-time scan status and
  errors. Long scans need progress, cancellation, and a clear distinction
  between the currently published catalog and a build in progress.
- **Live share watching is not current behavior:** issue #1772 remains open.
  Existing slskd configuration-file watching and issue #1050 are not evidence of
  share-tree automatic change detection and should not be used as a parity
  justification.
- **Per-root rescans are not current behavior:** issue #1773 remains open and
  describes the current need to rescan every configured share.
- **Slots and speed are common expectations:** issue #127 led to work for upload
  slot and bandwidth limiting. These are baseline controls, not exotic policy.
- **Connectivity is part of upload health:** slskd support discussions and the
  Soulseek FAQ repeatedly surface closed listening ports as a reason peers
  cannot browse or download. Sockseek must expose listener health clearly.

These lessons lead directly to bounded traversal, disk-backed storage, atomic
catalog-and-artifact publication, no startup history preload, explicit scan
state, streaming browse-artifact construction, and operational health warnings.

---

## 5. Target architecture

### 5.1 Runtime ownership

Introduce a daemon-lifetime Soulseek feature host:

```text
ServerHostedService / DaemonRuntime
  |
  +-- SoulseekSession
  |     +-- SoulseekClientManager
  |     +-- connection/readiness/reconnect state
  |
  +-- DownloadEngineSupervisor
  |     +-- download workflows
  |
  +-- SharingRuntime
  |     +-- ShareCatalogManager
  |     +-- ShareScanCoordinator
  |     +-- SoulseekBrowseArtifactBuilder
  |     +-- SoulseekSharingAdapter
  |     +-- PeerAccessPolicy
  |
  +-- UploadRuntime
        +-- UploadCoordinator
        +-- UploadScheduler
        +-- UploadAdmissionPolicy
```

`SoulseekSession` owns the one `ISoulseekClient` and its reconnect lifecycle.
Downloads, sharing, and uploads consume that session. No feature owns or disposes
the client independently.

A minimal implementation can preserve most of `SoulseekClientManager`, but
`EngineSupervisor` SHOULD stop being the conceptual owner of the Soulseek
session. Sharing must not disappear because a download engine is restarted.
Conversely, a resolver failure must be caught and mapped to a safe empty/denied
protocol response; it must not fault the session or restart unrelated download,
chat, or future user-browse features.

Use one coordinated daemon runtime/hosted-service lifecycle. `SharingRuntime`,
`UploadRuntime`, and the download supervisor are child components, not
independently ordered `IHostedService` instances whose startup/disposal order is
left to dependency-injection registration.

### 5.2 Component responsibilities

#### `ShareCatalogManager`

- Opens the current catalog generation at startup.
- Provides short-lived read leases.
- Atomically switches to a newly published generation.
- Retains the previous generation until all leases drain.
- Keeps a browse stream lease alive until its wrapper reaches exact EOF,
  is disposed, or enforces the browse idle deadline—not merely until the
  resolver returns.
- Exposes immutable catalog metadata and health.

#### `ShareScanCoordinator`

- Allows one active scan.
- Builds a new catalog generation through a bounded pipeline.
- Builds and validates the generation's browse artifact.
- Publishes only after validation.
- Reports coalescible progress and ordered terminal changes.
- Leaves the current generation untouched on failure or cancellation.

#### `IShareCatalogReader`

- Searches files with a hard result limit.
- Lists full browse content or one directory.
- Resolves an exact remote path to a catalog file record.
- Returns counts and generation metadata.

#### `ISoulseekBrowseArtifactBuilder` / `SoulseekBrowseArtifactBuilder`

- Reads one completed staging catalog in deterministic directory/file order.
- Writes the Soulseek browse wire format through a streaming compressor to a
  temporary file without constructing a complete `BrowseResponse` or `byte[]`.
- Records wire-format version, byte length, hash, counts, and build diagnostics.
- Validates the artifact before it can be included in a published manifest.
- Has decompressed-payload and parse-equivalence contract tests against
  Soulseek.NET for representative small fixtures. Valid zlib encoders need not
  produce identical compressed bytes.

The contract is Core-owned and accepts protocol-neutral catalog rows. The
implementation sits under Core's Soulseek adapter because the artifact is a
versioned Soulseek wire representation. Persistence owns the catalog rows,
manifest fields, temporary-file durability, and atomic generation publication;
it MUST NOT acquire Soulseek serialization knowledge.

#### `SoulseekSharingAdapter`

- Maps Soulseek.NET resolver calls into bounded sharing/upload operations.
- Does not perform database writes directly.
- Converts Core-owned records to Soulseek.NET response objects.
- Applies request timeouts and concurrency limits.
- Returns a lease-owning `RawBrowseResponse` for full browse.
- Catches domain/infrastructure failures at every callback boundary and maps
  them to the pinned library's empty/denied behavior.

#### `PeerAccessPolicy`

- Holds normalized exact username and exact IP deny sets.
- Provides allocation-free or low-allocation checks on hostile request paths.
- Applies username policy as soon as a username is known and IP policy whenever
  a peer endpoint is known.
- Contains no protocol, filesystem, persistence, or scheduling behavior.

#### `UploadAdmissionPolicy`

- Rejects requests when no upload-serving catalog/session is available.
- Rejects blacklisted peers, unknown files, invalid paths, duplicates, and
  configured per-user queued-limit violations.
- Has no scheduling loop and does not open file streams.

#### `UploadScheduler`

- Owns the authoritative active and queued upload set.
- Enforces global slots.
- Implements strict user-round-robin scheduling.
- Enforces at most one active upload per normalized username.
- Provides free-slot, queue-length, and estimated-position snapshots.

#### `UploadCoordinator`

- Creates transfer identity at admission and attempt 1 only at dispatch.
- Opens and validates files.
- Calls Soulseek.NET upload APIs.
- Maps progress, cancellation, and failure to generic Core transfer changes.
- Never waits for persistence before serving the peer.

### 5.3 Dependency rule

Core runtime code MUST NOT depend on EF entities, SQLite connections, API DTOs,
or mutable Soulseek.NET transfer objects.

Conversely, persistence code MUST NOT depend on Soulseek.NET response types or
reimplement protocol serialization. The protocol adapter streams neutral catalog
rows into a persistence-supplied temporary output and returns artifact metadata
for the generation manifest.

The boundary is:

```text
Soulseek.NET callback
    -> immutable request record
    -> sharing/upload domain service
    -> immutable Core changes/snapshots
    -> live-state and persistence adapters
```

Peer callback records have fixed maximum username, query, and remote-path sizes
before they enter keyed gates, logs, queue dictionaries, or SQLite. The global
request gate is acquired before creating a per-user gate, and empty per-user
gates are removed with reference-counted cleanup. This keeps attacker-controlled
key cardinality bounded.

---

## 6. Configuration model

Sharing, uploads, and peer access are daemon settings, not per-download profile
settings. They MUST NOT be copied into each submitted job.

### 6.1 Typed settings and settled defaults

```csharp
public sealed class SharingSettings
{
    public List<ShareRootSettings> Roots { get; set; } = [];
    public List<string> ExcludedDirectories { get; set; } = [];
    public List<string> Filters { get; set; } = [];
    public bool ScanOnStart { get; set; } = true;
    public TimeSpan? RescanInterval { get; set; } = null;
    public int ScanWorkers { get; set; } = Environment.ProcessorCount;
    public int SearchResponseFileLimit { get; set; } = 500;
    public int IncomingSearchConcurrency { get; set; } = 10;
    public int IncomingSearchQueueCapacity { get; set; } = 500;
}

public sealed class ShareRootSettings
{
    public required string LocalPath { get; set; }
    public string? Alias { get; set; }

    // Computed after path normalization and validation; never configured twice.
    public required string EffectiveAlias { get; init; }
}

public sealed class UploadSettings
{
    public int Slots { get; set; } = 10;
    public int? SpeedLimitKiBPerSecond { get; set; } = null;
    public int? MaximumQueuedFilesPerUser { get; set; } = null;
    public int? MaximumQueuedMegabytesPerUser { get; set; } = null;
}

public sealed class PeerAccessSettings
{
    public List<string> BlockedUsernames { get; set; } = [];
    public List<string> BlockedIpAddresses { get; set; } = [];
}
```

These defaults are decisions, not Phase 0 placeholders:

| Behavior | Sockseek v4.0 default | slskd default/equivalent | Decision |
|---|---:|---:|---|
| Sharing roots | empty | empty | Match; sharing is inactive until at least one root exists. |
| Share filters | empty | empty | Match. |
| Startup scan | enabled | enabled unless `--no-share-scan` | Match. |
| Periodic rescan | disabled (`null`) | disabled/empty retention | Match. |
| Scan workers | `Environment.ProcessorCount` | `Environment.ProcessorCount` | Match; validate 1–128. |
| Incoming search concurrency | 10 | 10 | Match. |
| Incoming search queue/circuit breaker | 500 | 500 | Match. |
| Search response file limit | 500 | 500 | Match. |
| Global upload slots | 10 | 10 | Match. |
| Global upload speed | unlimited | effectively unlimited (`int.MaxValue`) | Match semantically; omitted/null is Sockseek's single unlimited representation. |
| Per-user queued file/byte limits | unlimited (`null`) | unlimited when unset | Match as a policy default. |
| Blacklists | empty | empty | Match. |
| Catalog storage | disk-backed generation | memory | Deliberate divergence; see section 8.2. |

There is no separate `sharing-enabled` option: the presence of at least one
valid `share` activates sharing. There is no separate `uploads-enabled` option:
when the daemon has a published catalog, a connected/listening Soulseek session,
and at least one upload slot, it serves uploads. This matches slskd's effective
model and avoids contradictory states.

Audio metadata extraction is required behavior, not a user switch. Following
symbolic links/reparse points is prohibited in v4.0, not configurable. Incoming
upload callback concurrency and pending-callback capacity are implementation
safety limits, initially fixed to 10 and 500 respectively; they should become
public tuning options only if release measurements show a legitimate need.

Every configured regular expression is constructed with a fixed, non-infinite
match timeout. Patterns compatible with `RegexOptions.NonBacktracking` SHOULD
use it; other valid .NET patterns may use the backtracking engine with the same
hard timeout. A timeout during scanning fails the staging generation because
treating an indeterminate filter result as “not filtered” could publish private
content. Cancellation tokens alone do not interrupt a synchronous regex match.

The configurable per-user limits default to unlimited for parity, but this does
not make process memory unbounded. The scheduler MUST enforce an independent
hard global safety ceiling of 100,000 accepted queued uploads in v4.0, matching
the mandatory qualification fixture. Hitting that ceiling rejects new admission
with a generic capacity response and health/metric signal. It is an
implementation envelope, not a user quota, and may be raised only with the
corresponding scheduler and memory qualification.

### 6.2 Share value syntax and alias derivation

Each share item has one of two forms:

```text
/absolute/local/path
[Public Alias]/absolute/local/path
```

Examples:

```ini
share = /mnt/storage/MEDIA/MUSIC
share = + /srv/slsk-test
share = + [Live Sets]/srv/media/live
```

For an alias-less item, configuration binding MUST:

1. expand allowed variables and canonicalize the absolute local path;
2. remove trailing directory separators;
3. derive `EffectiveAlias` from the final directory segment;
4. validate the derived alias with exactly the same rules as an explicit alias;
5. reject the configuration if no safe final segment exists; and
6. publish remote paths beginning only with `EffectiveAlias`.

Thus `/mnt/storage/MEDIA/MUSIC/Artist/Track.flac` is exposed as
`MUSIC\Artist\Track.flac`, never as `mnt\storage\MEDIA\MUSIC\...` and never as
an absolute path.

### 6.3 List-valued config syntax

The sharing PR MUST use Sockseek's existing ordered list-operation semantics.
There will be no prerequisite configuration-language PR.

An unprefixed value replaces the list inherited from lower-precedence settings;
a value beginning with `+ ` appends to that list. Repeated keys are permitted
because each line is an ordered operation, but repetition alone does not change
the replace/append rule.

```ini
share = /mnt/storage/MEDIA/MUSIC
share = + /srv/slsk-test
share = + [Live Sets]/srv/media/live

share-exclude = /mnt/storage/MEDIA/MUSIC/private
share-exclude = + /mnt/storage/MEDIA/MUSIC/incoming

share-filter = \.part$
share-filter = + Thumbs\.db$

    peer-blocked-user = spammer
    peer-blocked-user = + another-user
    peer-blocked-ip = 203.0.113.10
    peer-blocked-ip = + 2001:db8::10
```

This is the same model already used by `on-complete` and `regex`: replacement
is necessary for profile/command-line layering, while explicit `+ ` preserves
inherited entries. Comma-separated values are not accepted for these new
sharing options.

Sockseek already has legacy collection-valued options with different syntax,
including comma-separated `banned-users`. This PR does not migrate or reinterpret
those options. `banned-users` remains a per-download source-selection filter: it
prevents selecting files offered by named remote users. It is distinct from the
daemon-global inbound sharing policy introduced here, which prevents blocked
peers from searching, browsing, or downloading our shares. To keep those two
concepts distinguishable in flat config and CLI help, this feature uses the
singular daemon-wide names `peer-blocked-user` and `peer-blocked-ip`; it does
not add `blacklist-users`. The `peer-` prefix also remains correct if chats
later reuse the same deny policy.

A separate configuration-language design SHOULD evaluate structured YAML and a
consistent migration for every collection-valued option. That work is outside
this feature and is not a prerequisite for v4.0 sharing.

### 6.4 Configuration and CLI examples

Representative configuration, with defaults shown as comments:

```ini
share = /mnt/storage/MEDIA/MUSIC
share = + [Live Sets]/srv/media/live
share-scan-on-start = true
# share-rescan-interval omitted: no periodic rescan
# share-scan-workers omitted: Environment.ProcessorCount
# share-search-concurrency omitted: 10
# share-search-queue-capacity omitted: 500
# share-search-result-limit omitted: 500

# upload-slots omitted: 10
# upload-speed-limit-kib omitted: unlimited
# upload-max-queued-files-per-user omitted: unlimited policy limit
# upload-max-queued-mib-per-user omitted: unlimited policy limit
```

Equivalent startup CLI options MUST use the same replacement and append
semantics:

```text
--share /mnt/storage/MEDIA/MUSIC
--share "+ /srv/slsk-test"
--share-filter "+ \.part$"
--peer-blocked-user bad-user
--peer-blocked-ip 203.0.113.10
--upload-slots 10
```

`--upload-speed-limit-kib`, when present, accepts a positive KiB/s value. Omission
means unlimited; zero and negative values are invalid. Per-user byte limits use
MiB in configuration and API contracts, matching Soulseek/slskd terminology.

Do not put roots or blacklists in per-download named/automatic profiles.

### 6.5 Validation rules

Configuration validation MUST enforce:

- A root is absolute and exists when a scan starts.
- A filesystem volume/mount root is rejected, with or without an explicit alias.
- An explicit or derived alias is non-empty, is not `.` or `..`, contains
  neither `/` nor `\`, and contains no NUL or control character.
- Aliases are unique using the same case-sensitivity rules as remote path lookup.
- Derivation uses the normalized final directory segment, not an arbitrary
  substring of the original configuration text.
- An exclusion is inside exactly one configured root.
- Overlapping roots are rejected in v4.0.
- Every regular expression compiles at configuration load time with the fixed
  finite match timeout.
- Every blacklist username is non-empty after protocol-name normalization.
- Every blacklist IP parses through `IPAddress.TryParse`; hostnames and CIDR
  notation are rejected.
- IPv4-mapped IPv6 values are normalized consistently so the same endpoint
  cannot evade an exact match through representation differences.
- Scan worker count, request limits, result limits, configured queue limits, and
  slots have safe upper bounds.
- Root, exclusion, filter, blocked-user, and blocked-IP collection counts and
  encoded value lengths have documented implementation ceilings; blank and
  duplicate normalized entries are rejected.
- Rescan interval is null/off or at least one minute and fits the platform timer
  range. An elapsed interval coalesces behind the one active scan rather than
  accumulating timer work.
- Scan workers validate in the range 1–128. Upload slots validate in the range
  1–1,024; the upper bound is an implementation safety envelope and may be
  changed only with connection, scheduler, snapshot, and shutdown measurements.
- Upload speed is either omitted/null for unlimited or a positive KiB/s value;
  zero and negative values are rejected, and checked conversion to the pinned
  library's bytes-per-second field must fit its `int` range.
- Per-user queue limits are either omitted/null for unlimited policy behavior or
  positive values; checked MiB-to-byte conversion must fit `long`. The
  independent 100,000-entry global safety ceiling always applies.

Settings that affect Soulseek client construction MAY require daemon restart in
v4.0. Documentation must identify which values are restart-required.

---

## 7. Remote path and filesystem model

Path handling is the main security boundary.

### 7.1 Namespaces

Maintain three distinct representations:

1. **Configured local root** — canonical absolute host path.
2. **Relative catalog path** — platform-neutral path relative to the root.
3. **Remote path** — `Alias\relative\path\file.ext`, using Soulseek separators.

Never expose a local root or absolute local path in Soulseek responses.

### 7.2 Catalog identity

A file is uniquely identified inside one generation by:

```text
(root_id, normalized_relative_path)
```

The remote lookup key is:

```text
normalized_remote_path = alias + "\\" + normalized_relative_path
```

The catalog stores both the display form and a comparison key. Remote paths
MUST normalize separators to `\` and normalize each valid Unicode segment to
NFC (never compatibility-normalize with NFKC). Display spelling/casing remains
exactly as scanned; only the lookup key is normalized.

SQLite's built-in `NOCASE` collation is ASCII-only and MUST NOT define this
identity. `RemotePathKey` is the Core value object for Sockseek's normative
remote-path comparison rule. Its v1 algorithm applies separator and per-segment
NFC normalization, simple invariant uppercase to each Unicode scalar with the
pinned runtime's `Rune.ToUpperInvariant`, UTF-8 encoding, and binary byte
comparison. Scan collision detection, alias uniqueness, SQLite exact lookup,
incoming resolution, and upload duplicate keys MUST all use this exact value
object. The design does not claim universal equivalence with
`StringComparer.OrdinalIgnoreCase`; tests establish consistency and stable
golden vectors for Sockseek's own rule instead.

The key algorithm is versioned in the catalog metadata and generation manifest.
A runtime or Unicode-data change that alters any accepted golden vector requires
a `remote_path_key_version` bump and catalog rebuild before publication. FTS
keeps a separate textual search projection; its tokenizer never defines exact
path identity.

Each local path segment must be representable as exactly one remote segment.
On Unix, a filename can contain `\`; such an entry is skipped as unsupported
because publishing it would turn one local segment into multiple ambiguous
Soulseek segments. NUL/control characters and segments that cannot be encoded by
the pinned library are handled the same way.

Case-sensitive filesystems can contain two entries, such as `Track.flac` and
`track.flac`, that collapse to the same remote comparison key. Any file/file,
directory/directory, or file/directory collision after separator, case, and
Unicode policy normalization fails validation of the staging generation with
bounded `RemotePathCollision` samples. Sockseek must not pick a winner based on
filesystem enumeration order.

### 7.3 Resolution rules

An incoming remote path MUST be rejected when it:

- Is empty or rooted.
- Contains NUL.
- Contains `.` or `..` segments.
- Contains an empty alias.
- Cannot be normalized unambiguously.
- Does not exactly match one published catalog record.

On an exact catalog match, the upload coordinator obtains the configured root
and stored relative path. It then:

1. Combines them using platform APIs.
2. Canonicalizes the result.
3. Verifies the result remains under the canonical root.
4. Opens the file without following an unexpected link outside the root.
5. Validates the already-open handle's final target and stable identity against
   the catalog record.
6. Revalidates length and last-write time against that same open handle.

The request path is never appended directly to a local root.

### 7.4 Symbolic links and reparse points

v4.0 has a fixed no-follow policy; there is no `FollowSymbolicLinks` option.

- Directory traversal skips symbolic-link/reparse-point directories.
- A file that is itself a symbolic link is skipped unless safe link handling is
  explicitly implemented and tested.
- Enabling links later must still verify the final target remains beneath the
  configured root.

Path checks performed before `FileStream` construction are not sufficient: a
local entry can be replaced with a link between the check and open. Phase 0 MUST
select and contract-test a handle-based safe-open strategy on every supported
OS—for example, an OS no-follow/beneath primitive, or opening then validating
the final handle target plus volume/file identity before returning any bytes.
If a platform cannot implement one of those fail-closed strategies, uploads are
unsupported on that platform; size and timestamp checks are not a security
substitute.

The same rule applies per filesystem. UNC/SMB, NFS, FUSE, container bind mounts,
and other filesystems are supported only when the Phase 0 capability probe and
safe-open contract can obtain the required final target and stable identity.
Otherwise configuration/scan reports a typed unsupported-filesystem error for
that root; it does not silently weaken validation.

Phase 0 produces a supported-filesystem matrix rather than a single
"Windows/Unix" assertion. Each tested row records OS/runtime, filesystem and
mount type, local/container context, safe-open primitive, final-target primitive,
stable-identity primitive, link/reparse behavior, rename-and-replacement result,
attribute reliability, and one of `Supported`, `Conditional`, or `Unsupported`
with its reason. Qualification covers native filesystems on every supported OS
and explicitly exercises Docker bind mounts, SMB/NFS, and representative FUSE
or WSL mounts where the product claims support. Absence from the matrix means
uploads are unsupported until qualified; browse/search may remain available
only if their weaker read requirements are documented separately.

### 7.5 File changes after scanning

The published catalog is a snapshot, while the filesystem can change.

At upload open:

- Missing file: fail as unavailable/not shared.
- Current size differs: fail; do not silently serve different bytes under stale
  metadata.
- Resume offset is greater than current length: reject.
- Last-write time differs but size is equal: conservative v4.0 behavior is to
  reject and request a rescan.
- Handle identity differs, or final-target containment cannot be proven: fail
  before the stream is exposed to Soulseek.NET.

A failed open marks the catalog as potentially stale and increments a health
counter; it does not mutate the catalog in place.

Zero-byte and non-regular files are not published in v4.0. Catalog file and
directory counts use 64-bit values internally, but publication validates that
the values passed to Soulseek.NET's `SetSharedCountsAsync(int, int)` fit the
protocol/library range.

---

## 8. Share catalog storage

### 8.1 Separate database from historical persistence

The share index MUST live under the daemon data directory, for example:

```text
data/
  sockseek.sqlite3                 # durable job/transfer history
  sharing/
    current.json                   # atomic generation manifest
    share-index-<generation>.sqlite3
    browse-<generation>.bin        # protocol-ready RawBrowseResponse payload
```

Reasons for a separate store:

- The catalog is rebuildable, while transfer/job history is durable user data.
- A full scan is a high-volume bulk write and FTS rebuild.
- Catalog writes must not delay terminal transfer persistence.
- Catalog schema and maintenance cadence are different.
- Corruption can be recovered by rebuilding without touching history.

`Sockseek.Persistence` can still contain the implementation, but the catalog
must not use the historical persistence writer or EF `SockseekDbContext`.
Direct `Microsoft.Data.Sqlite` is appropriate here because the workload is
bulk insertion, exact lookup, and FTS5.

The sharing directory, manifest, catalog, browse artifact, and temporary files
contain absolute local paths or a complete public file listing. Create them with
daemon-owner-only permissions/ACLs and never broaden permissions during atomic
replacement. This is a local confidentiality boundary even though these files
are rebuildable.

### 8.2 Storage-mode decision: disk-only in v4.0

slskd defaults its share cache to `memory`, and its documentation correctly
notes the tradeoff: warm lookups can be faster and require less ongoing disk I/O,
but large shares consume more memory; `disk` lowers memory use at the cost of
more CPU and disk activity. Sockseek should not copy that default mechanically
because the catalog lifecycle is different.

In current slskd, memory mode is a shared in-memory SQLite database plus a
separate on-disk backup used for restart restoration. It requires a keepalive
connection, backup after successful scans, restore at startup, and qualification
of both working and backup databases. Sockseek's proposed catalog already uses
immutable SQLite generation files, atomic manifest publication, retained prior
generations, and read leases. Adding memory mode would therefore require:

- simultaneously holding old and staging in-memory generations during scans;
- a second backup/restore path in addition to generation publication;
- different crash-recovery and corruption behavior;
- a doubled release matrix for startup, scan, publication, browse, and query
  tests; and
- substantially higher peak RSS precisely where the release requirements focus
  on large-library boundedness.

A disk-backed SQLite generation also benefits from the operating system page
cache. Frequently read index/database pages can remain memory-resident without
requiring Sockseek to own a second persistence mode, while cold or very large
catalog pages remain evictable under memory pressure. Disk storage does not make
whole-browse allocation safe by itself, so browse boundedness remains a separate
release gate.

**Decision:** v4.0 exposes no `share-cache-storage-mode` option and always uses
disk-backed catalog generations. This is a justified default divergence from
slskd. It matters operationally—users should expect a small amount of catalog
I/O—but it does not change peer-visible behavior or configuration portability.

A memory mode may be proposed later only with benchmark evidence that the
qualified disk implementation fails an agreed latency/CPU requirement and that
memory mode provides a material improvement while still satisfying the same
 1M-row startup, scan, browse, crash-recovery, and peak-memory gates. It must be
an optimization behind `IShareCatalogReader`/generation abstractions, not a
second semantic model. Disk remains the default unless a future design and
migration explicitly changes it.

### 8.3 Generation publication

Do not update the only readable catalog in place.

A scan creates `share-index-<new-generation>.sqlite3` and
`browse-<new-generation>.bin`, writes all rows, builds indexes and the streaming
browse artifact, runs validation, and closes the writers. Publication then:

1. Opens the new database read-only and runs `PRAGMA quick_check`.
2. Validates schema version, `RemotePathKey` algorithm version, and
   configured-root fingerprint.
3. Validates counts and required metadata.
4. Validates the browse artifact's wire-format version, framing, counts, length,
   and hash.
5. Writes `current.json.tmp`, flushes file contents, atomically replaces
   `current.json`, and flushes parent-directory metadata where the platform
   requires it for crash durability.
6. Switches `ShareCatalogManager` to the new read handle.
7. Publishes `ShareCatalogPublishedChange`.
8. Updates Soulseek shared counts.
9. Disposes and later deletes old catalog/artifact generations after
   outstanding query and browse-stream leases drain, retaining at least one
   rollback generation.

On failure or cancellation, delete the incomplete generation and retain the
old manifest and reader.

At startup, validate the manifest before opening it, remove only recognized
unreferenced staging files inside the owned sharing directory, and retry cleanup
of retired generations. Never glob-delete unknown files. Scan/database/artifact
writes periodically check a documented minimum free-space reserve and abort the
staging generation before consuming it; disk-full cleanup failure degrades health
but never invalidates the published generation. Phase 0 sets and records the
portable reserve rule.

This is a stronger consistency model than timestamping rows in the published
catalog and pruning at the end. Peers observe either the old complete catalog or
the new complete catalog, never a partial scan.

### 8.4 Proposed schema

Each generation database can be self-contained:

```sql
CREATE TABLE catalog_metadata (
    schema_version       INTEGER NOT NULL,
    remote_path_key_version INTEGER NOT NULL,
    generation_id       TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    settings_hash       TEXT NOT NULL,
    directory_count     INTEGER NOT NULL,
    file_count          INTEGER NOT NULL,
    total_bytes         INTEGER NOT NULL,
    browse_status       TEXT NOT NULL,
    browse_wire_version INTEGER,
    browse_length_bytes INTEGER,
    browse_sha256       TEXT
);

CREATE TABLE roots (
    root_id              INTEGER PRIMARY KEY,
    alias                TEXT NOT NULL,
    local_path           TEXT NOT NULL,
    comparison_alias     BLOB NOT NULL UNIQUE
);

CREATE TABLE directories (
    directory_id         INTEGER PRIMARY KEY,
    root_id              INTEGER NOT NULL,
    relative_path        TEXT NOT NULL,
    remote_path          TEXT NOT NULL,
    comparison_path      BLOB NOT NULL UNIQUE,
    FOREIGN KEY (root_id) REFERENCES roots(root_id)
);

CREATE TABLE files (
    file_id              INTEGER PRIMARY KEY,
    root_id              INTEGER NOT NULL,
    directory_id         INTEGER NOT NULL,
    relative_path        TEXT NOT NULL,
    remote_path          TEXT NOT NULL,
    comparison_path      BLOB NOT NULL UNIQUE,
    search_text          TEXT NOT NULL,
    size_bytes           INTEGER NOT NULL,
    modified_at_utc      TEXT,
    file_identity        BLOB NOT NULL,
    protocol_code        INTEGER NOT NULL,
    extension            TEXT,
    media_length_seconds INTEGER,
    bitrate_kbps         INTEGER,
    sample_rate_hz       INTEGER,
    bit_depth            INTEGER,
    attributes_json      TEXT,
    FOREIGN KEY (root_id) REFERENCES roots(root_id),
    FOREIGN KEY (directory_id) REFERENCES directories(directory_id)
);

CREATE INDEX idx_files_directory ON files(directory_id, remote_path);
CREATE INDEX idx_files_root_relative ON files(root_id, relative_path);

CREATE VIRTUAL TABLE file_search USING fts5(
    search_text,
    content='files',
    content_rowid='file_id',
    tokenize='unicode61 remove_diacritics 2'
);
```

Exact SQL and FTS synchronization can differ, but these invariants matter:

- Exact remote-path lookup is indexed and does not use FTS.
- Catalog metadata and the manifest carry the same supported
  `remote_path_key_version`; a mismatch makes the generation unavailable and
  requests a rebuild.
- Directory listing is indexed.
- Search never scans the whole files table.
- Local paths are confined to the catalog implementation and admin diagnostics.
- Counts are read from committed metadata, not recomputed on every user-info
  request.
- Stable file identity is opaque to higher layers but sufficient for the
  platform safe-open check.
- Protocol code, extension, and ordered attributes are reproducible without
  reopening the media file while serving search, directory, or browse requests.
- Every wire count/length uses checked arithmetic and fits the pinned protocol
  field before publication. Every remote string has a documented encoded-byte
  ceiling and is proven encodable; no serializer cast may wrap a large count or
  UTF-8 byte length.
- Browse metadata has a database/manifest constraint: `Ready` requires
  version/positive length/hash and an artifact; `UnavailableOversize` requires
  those fields/artifact to be absent. No other artifact failure is publishable.

### 8.5 SQLite settings

Recommended build-time settings:

- WAL is optional for the staging database because no readers use it during
  build; a large transaction or bounded batch transactions are sufficient. If
  WAL is used, checkpoint and close it before validation/publication so a
  generation never depends on orphanable `-wal`/`-shm` sidecars.
- Use a conservative `synchronous` setting. The index is rebuildable, but an OS
  crash must not result in publishing corrupt data.
- Set `busy_timeout` explicitly.
- Use prepared statements and batched transactions.
- Run `ANALYZE` after building indexes.
- Run `quick_check` before publication.
- Open published generations read-only.

Avoid vacuuming every scan unless measurements show it is needed; a generation
is freshly built and old files are deleted wholesale.

### 8.6 Browse artifact format and lifetime

Soulseek.NET 10.0.2 already supports a stream-backed full browse through
`RawBrowseResponse(long length, Stream stream)`. The artifact is one complete
framed peer message equivalent to `BrowseResponse.ToByteArray()`: outer length,
peer message code, and a zlib-compatible compressed browse payload. The
decompressed payload and framing must match; compressed bytes need not be
byte-for-byte identical across valid compressor implementations.

The builder MUST:

1. Reserve/write the framing fields and known directory count.
2. Read directories and their files from the staging database in one
   deterministic indexed order.
3. Encode strings and attributes with the pinned library's wire semantics into
   a streaming zlib-compatible compressor.
4. Finalize the outer length, hash the finished artifact, and record the
   serializer/wire version.
5. Validate small fixtures by parsing them with Soulseek.NET and validate large
   fixtures with an independent streaming structural reader.

An artifact that exceeds protocol framing, configured disk safety, or another
hard serving limit does not make the SQLite catalog corrupt. At mandatory
qualification sizes it is a release failure. At a larger unsupported size,
Sockseek may publish search/directory/upload capability with full browse marked
`UnavailableOversize` and health degraded; it must not attempt the object-graph
fallback.

Each browse request opens a new read-only artifact stream wrapped with both a
generation lease and the browse-concurrency permit. Both are released only when
the exact declared length has been read, the stream is disposed, or a documented
idle deadline expires Sockseek's wrapper. Retired generation count/bytes and
long-held stream leases are bounded operational resources: once their safety
envelope is reached, new scans are rejected or publication is deferred with
explicit health rather than allowing disk use to grow on every rescan.

Opening a published artifact revalidates its physical length against the
manifest. Startup/generation validation verifies its hash. The returned stream is
an exact-length wrapper that throws on premature EOF and never yields bytes past
the recorded length. This is required because the pinned library streams the
declared length and does not itself treat an early zero-byte read as terminal.
Soulseek.NET 10.0.2 disposes `RawBrowseResponse.Stream` only after
`connection.WriteAsync` succeeds, not in a `finally`. The preferred correction
is an upstream release, or a narrowly pinned fork while an upstream change is
pending, that disposes the raw stream from a `finally`; a forced-write-failure
contract test must prove it. Until such a version is pinned, Sockseek also uses
a self-expiring exact-length wrapper. The wrapper guarantees that Sockseek-owned
generation leases, permits, and file handles are released by its deadline and
that future reads fail. It does **not** claim to cancel a network
`connection.WriteAsync` already stalled after its last read. The pinned
library's separately verified connection inactivity timeout bounds that
remaining network operation.

---

## 9. Scan pipeline

### 9.1 State machine

```text
Idle
  -> Preparing
  -> Enumerating
  -> ReadingMetadata
  -> FinalizingIndex
  -> Validating
  -> Publishing
  -> Completed

Any nonterminal state -> Cancelling -> Cancelled
Any nonterminal state -> Failed
```

The old catalog remains `Ready` throughout. Scan status and published-catalog
status are separate fields.

### 9.2 Bounded pipeline

The scanner MUST NOT first materialize all directories or files.

Recommended pipeline:

```text
single filesystem enumerator
  -> bounded FileCandidate channel
  -> N metadata workers
  -> bounded CatalogRecord channel
  -> single batched SQLite writer
```

The enumerator performs iterative depth-first traversal, so memory is bounded by
path depth and the channel capacities rather than the total directory count. It
emits a directory record as soon as a directory is entered, preserving empty
directories.

Metadata workers:

- Read size and timestamps.
- For candidate audio files, attempt to parse the required protocol metadata;
  there is no user switch that disables extraction.
- Catch malformed/inaccessible-file failures.
- Emit a basic file record without optional metadata when safe.
- Never retain an open stream after producing the record.

The SQLite writer is the only writer to the generation database. It batches
records in transactions and publishes progress after commits.

### 9.3 Filtering and exclusions

Order of operations:

1. Canonicalize root.
2. Apply the fixed v4 hidden/system policy below.
3. Skip excluded subtrees before enumerating children.
4. Apply filters to the normalized remote relative path.
5. Apply symbolic-link policy.
6. Read file metadata.

The explicitly configured root itself is always eligible even when its final
segment is hidden; naming it is an operator decision. For descendants:

- On Windows, an entry with either the `Hidden` or `System` attribute is skipped.
- On Unix, an entry whose name starts with `.` is skipped. If reliable
  hidden/system attributes are also exposed, either attribute also causes a
  skip.
- Skipping a directory skips its complete subtree.
- If attributes required by the host policy cannot be read, treat the entry as
  inaccessible, skip it, and retain only a bounded diagnostic sample.

This behavior is not configurable in v4. Filesystems whose attributes or name
semantics cannot support the rule reliably must be marked conditional or
unsupported in the Phase 0 filesystem matrix rather than silently including
possibly private content.

Filters use compiled, case-insensitive regular expressions by default, with the
finite execution policy from section 6.1. A matching directory filter skips the
subtree; a matching file filter skips the file. A `RegexMatchTimeoutException`
fails the generation with a stable configuration/filter code.

Network-supplied excluded search phrases are not scan filters and do not alter
the catalog. They are session state applied at response time so reconnects and
server updates take effect without a full rescan.

### 9.4 Error policy

The scanner should continue past expected per-entry errors:

- Access denied.
- File disappeared during enumeration.
- Invalid media metadata.
- Unsupported path or filename.
- Transient I/O error below a configured retry threshold.

The live scan state stores counters and a bounded sample of errors, not every
error forever. The terminal scan result records:

- directories visited;
- files indexed;
- bytes indexed;
- files filtered;
- directories excluded;
- entries skipped by attributes/link policy;
- metadata failures;
- I/O failures;
- elapsed time;
- whether a new generation was published.

A root-level failure or database failure fails the generation and prevents
publication.

Remote comparison-key collisions also fail publication. Disk-full, browse
artifact framing/validation failure at a supported size, and a settings change
during a scan fail the staging generation; they never partially update the
current manifest. Cleanup of large staging, WAL, journal, and temporary artifact
files is retried at startup when immediate deletion is not possible.

### 9.5 Cancellation

Cancellation MUST:

- Stop accepting new filesystem work.
- Complete channels in dependency order.
- Await workers with a bounded shutdown timeout.
- Roll back/close the staging database.
- Delete the staging generation when possible.
- Leave the current catalog and shared counts unchanged.
- Publish an ordered terminal scan change.

### 9.6 Scan triggers

Support:

- Initial scan when no valid catalog exists.
- Startup background full rescan when a catalog exists and `ScanOnStart` is true.
- Manual full rescan API/CLI action.
- Optional periodic full rescan.

If a scan is already active, a new request returns the existing scan identity
rather than starting a second scan. A `force` request MAY cancel and replace it
later, but is not required in v4.0.

Current slskd does not implement a share-tree `FileSystemWatcher`/inotify update
path; issue #1772 requests that feature. Sockseek therefore does not require
automatic change detection in v4.0. Do not add a watcher opportunistically under
this feature without a separate design covering event loss, overflow, rename
coalescing, network mounts, symlinks, debounce, reconciliation scans, and watch
resource limits.

### 9.7 Optional scan optimizations

Prior-generation metadata reuse and changed-subtree scans are optional, not
parity requirements. If implemented:

- metadata reuse must compare at least normalized root identity, relative path,
  size, and last-write timestamp, with a documented policy for timestamp
  precision and filesystems that do not provide stable values;
- reused rows must still be copied into the staging generation rather than read
  across generations after publication;
- an incremental scan must have a full-reconciliation fallback and may not make
  watcher events authoritative;
- cancellation/failure must retain the currently published generation; and
- benchmarks must prove that the extra comparison/index complexity is beneficial.

## 10. Search and browse serving

### 10.1 Incoming search

Incoming search is hostile/untrusted input and can arrive at high frequency.
The adapter MUST have:

- A bounded global concurrency limit.
- A bounded waiting queue/circuit breaker.
- A hard result limit.
- A request timeout/cancellation token.
- No unbounded wildcard or table scan.

Search flow:

1. Validate the already parsed `SearchQuery` term/exclusion counts and total
   encoded size.
2. Apply exact username policy and any fresh cached endpoint decision.
3. Reject empty/invalid/filtered search requests.
4. Acquire a catalog read lease.
5. Query FTS5 with a hard limit and bounded over-fetch.
6. Apply request exclusions and the current Soulseek-server excluded phrases as
   a final ordinal-case-insensitive substring check on remote paths.
7. If results remain and an exact IP deny set is configured but no fresh
   endpoint is cached, resolve the requester endpoint within the same bounded
   request deadline; deny/fail closed if it cannot be resolved.
8. Map rows to Soulseek files.
9. Attach an immutable upload-capacity snapshot: current speed, whether this user
   could start now, and a bounded non-negative protocol queue signal. Use the
   requester forecast when cheaply available; otherwise use the global waiting
   count rather than running an unbounded position calculation.
10. Return null/empty quickly when overloaded rather than accumulating work.

FTS terms are quoted/escaped as data and the `MATCH` expression is passed as a
SQLite parameter, never interpolated into SQL. Positive terms use AND semantics;
request exclusions use NOT as an index prefilter and are checked again as
substrings to preserve Soulseek behavior. Term count, term length, exclusion
count, and bounded over-fetch prevent a query with many exclusions from turning
post-filtering into an unbounded scan.

The server-supplied phrase set is an immutable, atomically replaced session
resource with hard count, per-phrase, and total encoded-byte limits. It is
deduplicated and compiled into a bounded matcher off the callback path. Never
truncate an oversized/malformed set: that could publish a forbidden result.
Instead mark search serving unavailable with
`ExcludedPhraseSetInvalid` until a valid replacement arrives, while browse and
uploads continue. Do not clear the last valid set merely on disconnect; retaining
it can over-filter but cannot expose a phrase the prior server prohibited.
Phase 0 must settle whether a new login supplies an authoritative empty/non-empty
set before incoming searches can arrive; fresh-process search readiness remains
fail-closed until that ordering is proven.

### 10.2 Full browse

A full browse can be much larger than a search response. It must not turn the
disk-backed index into a whole-catalog object graph or byte array.

The pinned library contract is already known:

- resolver type:
  `Func<string, IPEndPoint, Task<BrowseResponse>>`;
- stream response:
  `RawBrowseResponse(long length, Stream stream)`; and
- the peer handler writes the supplied stream and disposes it after a successful
  write, but not unconditionally when the write throws in 10.0.2.

Sockseek therefore always serves the current generation's validated browse
artifact. The response stream owns its generation lease and a bounded global
browse-stream permit until exact EOF, disposal, or wrapper self-expiration. If
the requester is denied, the gate is saturated, the artifact is
unavailable/oversize, or a safe stream cannot be opened, return the pinned
library's empty/no-response behavior
without starting object construction.

slskd validates the usefulness of a file-backed cache, but its current cache
builder first constructs every `Directory`/`File` and then calls
`BrowseResponse.ToByteArray()`. Issue #1765 demonstrates why Sockseek's
streaming artifact build is mandatory even though the serving mechanism is
similar.

### 10.3 Directory contents

Directory-content requests are naturally bounded by one directory but still need
an upper safety limit. They use exact normalized directory lookup and an indexed
query ordered by remote filename.

Unknown directories return an empty/not-found response without probing the host
filesystem.

The preflight limit is based on both item count and encoded byte estimate. This
matters for a single directory containing a small number of extremely long
names. Soulseek.NET 10.0.2 serializes this response to a `byte[]`, so rejection
must happen before constructing a proportional `Directory`/file collection.

### 10.4 User information

`UserInfoResolver` uses the same immutable scheduler snapshot as search:

- `UploadSlots` is the configured global slot count, subject to the pinned
  protocol range.
- `QueueLength` is the requesting user's ahead-count forecast when cheaply
  available, otherwise the same bounded global-waiting fallback used by search;
  the wire field is non-nullable.
- `HasFreeUploadSlot` is true only if that user could start immediately, taking
  the global slot count, their existing active upload, and queued predecessors
  into account.
- `UserDescription` remains an identity setting. A future local profile-picture
  setting belongs with identity, not the share catalog.

Denied peers receive a non-revealing zero-capacity response or no response,
according to the pinned callback behavior established by contract tests.

### 10.5 Catalog availability

Resolver behavior:

- No configured share roots: return no files and reject uploads.
- No valid catalog and initial scan running: return empty/unavailable and expose
  `Initializing` health.
- Valid old catalog and rescan running: continue serving the old generation.
- Scan failed: continue serving the old generation and expose `Stale/Degraded`.
- Current settings fingerprint does not match the manifest: do not serve the
  prior catalog, even when `ScanOnStart` is false; treat it as unavailable and
  perform the mandatory initial build.
- Catalog file unreadable: mark sharing unavailable and request rebuild; do not
  fall back to direct filesystem traversal in a peer request.
- Browse artifact unavailable/oversize: keep indexed search, directory, and
  upload service available, but reject full browse deterministically and expose
  degraded browse capability.

---

## 11. Upload admission and queue behavior

### 11.1 Request flow

```text
Soulseek.NET enqueue callback
  -> bounded request gate
  -> normalize peer username and observed endpoint
  -> exact username/IP blacklist check
  -> bounded/ref-counted keyed per-user gate
  -> normalize and exact-resolve catalog path
  -> validate upload settings and outstanding limits
  -> coalesce/reject duplicate
  -> create TransferSnapshot(Direction=Upload)
  -> enqueue in UploadScheduler
  -> persist asynchronously
  -> complete the void enqueue callback, or throw the mapped rejection
```

The callback must complete quickly. It must not wait for a free slot, the full
upload, or a persistence commit. Username policy is checked before catalog work;
IP policy is checked whenever Soulseek.NET provides or Sockseek has resolved the
peer endpoint. Admission is rechecked immediately before a queued upload starts
so a stale endpoint or future policy reload cannot bypass it.

The callback has no library cancellation token. Every admitted request carries
an internal deadline through its queued work; after that deadline it may only
complete as an overload/timeout rejection and MUST NOT register a transfer.

### 11.2 Blacklist behavior

The daemon-wide access policy uses two normalized hash sets:

- usernames compared with the protocol's case-insensitive username semantics;
- exact `IPAddress` values, including normalized IPv4-mapped IPv6 handling.

For a matching peer, Sockseek MUST:

- return no incoming search results;
- deny full browse;
- deny directory contents;
- reject a new upload request before a transfer is registered.

No peer-facing response should reveal whether the username, IP, catalog, or
another policy caused the denial. Metrics use low-cardinality reason codes; logs
must not repeatedly emit one warning per hostile request.

v4.0 does not include runtime blacklist mutation. On a future live reload,
active transfers should continue, queued transfers should be revalidated before
start, and the behavior must be documented and tested.

### 11.3 Duplicate requests

Use `(normalized username, RemotePathKey)` as the active duplicate key.

Recommended behavior:

- If the same user requests the same file while it is queued or active, find
  the existing nonterminal transfer and do not create another transfer, consume
  counters, or add a queue entry.
- Soulseek.NET's `EnqueueDownload` callback returns only `Task`, not a transfer
  or queue-state value. The adapter therefore completes that callback
  successfully for a duplicate. The library's subsequent
  `PlaceInQueueResolver` call reports the existing entry's position.
- The domain admission result may carry `DuplicateOf(TransferId)` internally so
  the adapter and telemetry know what happened, but it is not a new peer
  response abstraction. Record coalescing through a counter or low-volume
  activity event.
- Once terminal, a new request creates a new transfer ID.

This makes retries idempotent and avoids quota/queue inflation.

### 11.4 Per-user queued limits

v4.0 supports optional maximum queued files and maximum queued MiB per user.
Both are unset/unlimited by default, matching slskd. For policy compatibility,
“queued” here means all accepted nonterminal uploads for the user, including an
active upload. Runtime `QueuedFiles`/`QueuedBytes` metrics and DTO fields still
mean waiting entries only; use `OutstandingFiles`/`OutstandingBytes` for the
policy counters so the two meanings are never mixed in code.

Check outstanding file and original-size byte counters atomically with
admission. When a configured limit would be exceeded, reject before creating a
new transfer with the standard
`Too many files` or `Too many megabytes` protocol reason where the pinned library
permits it. The rejection is an activity/metric, not a durable transfer.

The independent 100,000-entry global scheduler safety ceiling applies even when
policy limits are unset. Reaching it returns a generic capacity failure and does
not create a transfer. This preserves bounded memory without changing the
user-facing default into an arbitrary per-user quota. Daily/weekly quotas and
failure quotas are deferred.

### 11.5 Strict user-round-robin scheduler

Use a simple, deterministic structure:

```text
Dictionary<User, FIFO<UploadId>> perUserQueues
Deque<User> readyUsers
Set<UploadId> active
Set<User> activeUsers
SortedSet<(RequestedAtUtc, UploadId)> waitingByAdmission
```

`waitingByAdmission` is a secondary index for the bounded generic live-transfer
page, not the scheduling order. It adds/removes one key per waiting entry and
supports keyset paging without copying or sorting the per-user queues. The
monotonic queue revision changes with every admission/removal/state transition.

Enqueue:

- Append to the user's FIFO.
- If it was empty and the user is not active, append the user once to
  `readyUsers`.

Start next:

1. Pop one user from the front.
2. Pop that user's oldest ready upload.
3. Mark the user active; do not put the user back in `readyUsers`.
4. Start the selected upload if a global slot is available.
5. When that upload terminalizes, clear the active-user marker and append the
   user to the back if they still have queued work.

All mutations occur under one scheduler lock or actor loop. This gives strict
fairness among eligible users with ready work while retaining FIFO order per
user and mirroring Soulseek.NET's fixed
`MaximumConcurrentUploadsPerUser == 1`.

Do not use one global FIFO: a user queueing thousands of files would block every
later user.

Do not call `UploadAsync` for every waiting entry and let `SlotAwaiter` or the
library's per-user/global semaphores become the real queue. Only a scheduler
grant creates the one task that invokes Soulseek.NET. This keeps task count
proportional to active slots, makes cancellation authoritative, and prevents a
Sockseek slot from being consumed while the library waits on another queue.

### 11.6 Queue position

Queue position is an estimate because active transfers complete at unpredictable
times. The domain value is `AheadCount`: zero means the upload is next eligible
for a slot, which avoids mixing zero-based protocol values with one-based UI
positions. It is a revisioned forecast from the current round-robin ring and
per-user FIFO state, not a promise about the order in which concurrent active
uploads will finish.

Expose:

- `QueuedFiles` globally.
- `ActiveSlots` and `TotalSlots`.
- `AheadCount` for an existing file.
- Forecast `AheadCount` for a new request from a user.

When non-null, the API may display `AheadCount + 1` as a one-based position. The
adapter maps to Soulseek's place/queue fields only after real-client tests
establish their zero/one-based semantics. Do not promise an exact start time.

Position calculation has a hard inspected-entry/time budget and runs as one
bounded scheduler query. It must not clone the queue or hold the scheduler actor
while traversing 100,000 entries. A target deeper than the calculation envelope
returns no estimate (`null` at the Soulseek resolver and a stable
`EstimateUnavailableLargeQueue` reason in API detail); it does not degrade
scheduling or invent a position.

`SearchResponse.QueueLength` is non-nullable. When a cheap requester forecast is
unavailable, populate it from the O(1) global waiting count (validated to fit the
wire `int`) and calculate `HasFreeUploadSlot` from the same scheduler revision.
This is an availability signal, not the selected-transfer queue estimate.

### 11.7 Slots

`UploadSettings.Slots` is the global number of concurrent active uploads and
defaults to 10, matching slskd. Changing slots MAY require restart in v4.0.
The same value is supplied as Soulseek.NET's constructor-only
`MaximumConcurrentUploads`; Sockseek's scheduler never grants more, so work
cannot stack behind the library's global semaphore.
The scheduler starts as many queued
uploads as needed whenever:

- a file is enqueued;
- an active upload terminates;
- the runtime starts;
- slots increase in a future live-reload implementation.

### 11.8 Bandwidth limit

Use Soulseek.NET's global maximum upload-speed facility when available in the
pinned package. This avoids implementing a second transfer stream layer.

Requirements:

- Unit is KiB/s.
- Omitted/null means unlimited; zero is invalid.
- The effective value is visible in daemon state and user-info/search responses.
- If the library option is constructor-only, configuration changes require
  restart in v4.0.
- Per-user and per-group limits are deferred.

Soulseek.NET 10.0.2 exposes `MaximumUploadSpeed` in
`SoulseekClientOptionsPatch`, so speed changes need not be assumed
constructor-only. Phase 0 must measure its aggregate behavior. If it cannot
reliably enforce the cap, the sharing/upload feature implements a measured
shared governor through `TransferOptions.Governor` before release; this is not a
deferrable follow-up while the setting remains part of v4.0.

### 11.9 Catalog publication while uploads are queued

Queue entries do not retain catalog leases; otherwise one slow peer could pin an
old generation indefinitely. Admission captures the `RemotePathKey`,
expected size, expected last-write time when available, and expected file
identity for diagnostics. Immediately before a scheduler grant invokes
Soulseek.NET, the coordinator resolves that remote path against the **current**
published generation and requires the same safe-open
identity/size/available-time contract.

- If the path is no longer published, fail the queued transfer as
  `FileNoLongerShared`.
- If it now names a different file, fail as `FileChanged`; never substitute the
  new bytes into an already accepted request.
- An upload whose validated stream is already open may finish across a catalog
  publication. Publication does not revoke active file handles.

This policy makes removals take effect for waiting work, avoids stale catalog
leases, and gives generation cleanup a finite owner set.

---

## 12. Upload transfer lifecycle

### 12.1 Normalized state machine

Sockseek should own stable API states rather than exposing every Soulseek.NET
internal flag:

```text
Queued
  -> Initializing
  -> InProgress
  -> Completed

Queued/Initializing/InProgress -> Cancelled
Queued/Initializing/InProgress -> Failed
Queued/Initializing/InProgress -> Interrupted   (daemon shutdown/crash)
```

Validation happens before transfer registration. `Requested`, `Validating`, and
`Rejected` are admission activity/result concepts, not transfer lifecycle
states. If future audit requirements need them, persist a separate bounded
admission-event record. This prevents nominal transfer states that no accepted
transfer can actually expose.

### 12.2 Identity

An accepted upload has:

- Stable `TransferId`.
- `Direction = Upload`.
- `Source = SoulseekPeer`.
- `Username` and `RemotePath`.
- `JobId = null`.
- `WorkflowId = null`.
- No public local absolute path.
- Zero attempts while the accepted transfer is only queued; at most one attempt
  after it is dispatched to the protocol upload operation.

Do not encode direction in the ID.

### 12.3 Attempts

Use the existing transfer-attempt model:

- An accepted upload has `AttemptCount = 0` while queued. A queued transfer that
  is cancelled, invalidated, or interrupted before dispatch legitimately
  terminates with no attempt.
- Attempt 1 is created immediately before the scheduler-dispatched
  Soulseek.NET upload operation begins. v4 never creates attempt 2 for the same
  accepted upload.
- A resume offset within the same Soulseek.NET operation remains the same
  attempt.
- Sockseek does not automatically retry a failed upload; the peer must request
  again, which creates a new transfer.

This keeps upload semantics honest and avoids daemon-initiated retries that the
remote peer did not request. Persistence and API projections must therefore
allow a terminal accepted transfer with an empty attempt collection; they must
not synthesize attempt 1 during reconciliation.

### 12.4 Stream creation and resume

Soulseek.NET should receive a stream factory so the file is opened only when the
transfer is ready to start.

The factory:

1. Re-resolves the accepted `RemotePathKey` in the current catalog.
2. Requires the expected file identity/size and performs the handle-based
   safe-open procedure from section 7.
3. Validates `0 <= startOffset <= fileLength`.
4. Opens a read-only stream with asynchronous/sequential options appropriate to
   the platform.
5. Seeks to `startOffset`.
6. Wraps it as an exact-length stream for `fileLength - startOffset`: premature
   EOF throws instead of allowing Soulseek.NET's fixed-length write loop to spin,
   and growth cannot append unadvertised bytes.
7. Returns the stream without allowing writes or path substitution.

Because the factory positions and bounds the remaining stream itself, the
corresponding `TransferOptions` MUST set
`SeekInputStreamAutomatically = false` and
`DisposeInputStreamOnCompletion = true`. The pinned default is automatic seeking;
leaving it enabled would apply the peer's absolute offset a second time inside
the remaining-length wrapper.

If validation fails, fail the transfer with a stable reason such as
`FileUnavailable`, `FileChanged`, or `InvalidOffset`.

A local process can still modify an already-open regular file on platforms
without mandatory read locking. v4.0 does not copy every upload into a private
snapshot. Recheck size and last-write time through the open handle at terminal
completion; if they changed, record `FileChangedDuringTransfer`, mark catalog
health potentially stale, and do not describe the transfer as content-verified.
Some bytes may already have reached the peer, so operator documentation must
recommend rescanning and avoiding writes to actively shared files.

### 12.5 Progress

Progress is coalescible latest-value state:

- bytes transferred;
- total bytes;
- current speed when available;
- last progress time.

Do not persist every callback. Reuse the persistence coalescing interval and
terminal-priority behavior already designed for downloads.

### 12.6 Cancellation

Terminal arbitration is one atomic compare-and-transition owned by
`UploadCoordinator`; library callbacks, peer disconnect, API cancellation,
shutdown timeout, and persistence callbacks cannot each terminalize
independently. Cancellation sources should distinguish:

- User/API cancel.
- Peer cancel/disconnect.
- Daemon shutdown.
- Catalog/file invalidation.

Cancellation removes queued entries immediately. Active cancellation calls the
Soulseek.NET cancellation path and terminalizes only after the operation exits or
a shutdown timeout expires. A late library callback after forced shutdown is
observed for diagnostics but loses terminal arbitration and cannot release a
slot/accounting twice.

### 12.7 Restart reconciliation

On startup, any persisted upload transfer/attempt without a terminal state is
marked `Interrupted` with an end timestamp.

Do **not** requeue it. The old peer connection and protocol request no longer
exist. A new peer request creates a new transfer.

On graceful shutdown, accepted queued and active uploads also terminalize as
`Interrupted` with `CancellationSource = DaemonShutdown`; they are not reported
as user cancellations. Crash reconciliation produces the same public outcome
with a distinct diagnostic source when useful.

---

## 13. Core changes and snapshots

### 13.1 New sharing snapshots

```csharp
public sealed record ShareCatalogSnapshot(
    Guid GenerationId,
    long Revision,
    ShareCatalogLifecycleState State,
    long DirectoryCount,
    long FileCount,
    long TotalBytes,
    ShareBrowseAvailability BrowseAvailability,
    long? BrowseArtifactBytes,
    DateTimeOffset? PublishedAtUtc,
    string? FailureCode,
    string? FailureMessage);

public sealed record ShareScanSnapshot(
    Guid ScanId,
    long Revision,
    ShareScanPhase Phase,
    int RootsCompleted,
    int RootCount,
    long DirectoriesSeen,
    long FilesSeen,
    long FilesIndexed,
    long FilesFiltered,
    long ErrorCount,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureMessage);
```

Do not include configured local paths in the general live-state contract.

### 13.2 New sharing changes

```text
ShareScanStartedChange                 ordered
ShareScanProgressedChange              coalescible by scan ID
ShareScanCompletedChange               ordered
ShareScanFailedChange                  ordered
ShareScanCancelledChange               ordered
ShareCatalogPublishedChange            ordered
ShareCatalogHealthChangedChange        coalescible
```

A completed scan and catalog publication can be one composite ordered change if
publication is guaranteed in the same operation. Keep failure/cancellation
terminal and ordered.

### 13.3 Generic transfer changes

Refactor state-bearing transfer changes to accept any direction. Download-only
context such as `JobSnapshot Song` must not be required for upload transfer
publication.

Activity payloads can remain specialized:

```text
UploadRequestedActivity
UploadRejectedActivity
UploadStartedActivity
UploadFailedActivity
ShareScanErrorActivity
```

These are best effort and not required to rebuild state.

### 13.4 Sequence ownership

Sharing/upload changes must use the same daemon runtime/epoch and monotonic
sequence space as other daemon state. Do not create an unrelated SignalR stream
whose ordering cannot be reconciled with transfer updates.

---

## 14. Live-state and API design

The sharing/upload API MUST follow the state-replication model established by
pull request #194. A normal monitor or GUI reconstructs small operational state
and active resources from one bounded daemon snapshot plus subsequent ordered
deltas. Potentially large collections use revisioned, paginated HTTP queries;
commands, details, and paginated history complement the stream. Activity events
must never be required to derive current state.

### 14.1 Extend the existing revisioned daemon component

Do not add independently revisioned top-level `Sharing` and `Uploads` fields to
`StateSnapshotDto` and `StateDeltaDto`. Both runtime summaries are small daemon
singletons and should be nested in the existing `DaemonStateDto`:

```csharp
public sealed record DaemonStateDto(
    long Revision,
    SoulseekClientStatusDto SoulseekClient,
    int RestartCount,
    DateTimeOffset? SearchRateLimitResetsAtUtc,
    SharingStateDto Sharing,
    UploadRuntimeStateDto Uploads);

public sealed record SharingStateDto(
    bool Configured,
    ShareCatalogStateDto Catalog,
    bool SearchServingReady,
    string? SearchUnavailableReasonCode,
    ShareScanStateDto? ActiveScan,
    ShareScanStateDto? LastScan);

public sealed record UploadRuntimeStateDto(
    bool Configured,
    bool AcceptingUploads,
    int Slots,
    int ActiveSlots,
    int QueuedFiles,
    long QueuedBytes,
    long QueueRevision,
    int? SpeedLimitKiBPerSecond,
    bool PeerListenerReady,
    UploadHealthState Health,
    string? UnavailableReasonCode);
```

`StateSnapshotDto` continues to contain one daemon component and the existing
bounded resource collections. `StateDeltaDto` continues to replace
`DaemonStateDto` by daemon revision. The reducer therefore gains fields, not a
new singleton-revision mechanism or a second synchronization protocol.

Queue summary changes are latest-value coalescible through the existing
`StateUpdateCoalescer`. Every queue mutation updates the authoritative
`QueuedFiles`, `QueuedBytes`, `QueueRevision`, and, when applicable,
`AcceptingUploads`, but ordinary queue churn does not force an immediate
SignalR flush. During a burst, the next bounded coalescer interval publishes the
latest daemon component rather than one outbound daemon delta per mutation.
Ordered terminal changes for replicated active transfers retain the existing
prompt-flush behavior. The scheduler and peer callback always consult
authoritative runtime state; bounded UI lag in this summary cannot admit work.

`Configured` means at least one valid share root is configured. It does not mean
that a catalog is ready. `AcceptingUploads` is the direct answer to whether a new
request can currently be admitted. `PeerListenerReady`, catalog lifecycle, and
health/reason fields describe why it may not be. Do not expose an ambiguous
`Enabled` property when v4.0 has no sharing/upload enable switches.
`SearchServingReady` additionally reflects connection/excluded-phrase readiness;
it can be false while the same catalog remains browsable and upload-capable.

`ActiveScan` is null after the terminal transition. `LastScan` retains the most
recent terminal resource so one-shot status can explain the last failure or
cancellation without conflating scan outcome with catalog health. The server
keeps only the active scan plus a small fixed ring of recent terminal scan
resources for `/scans/{id}`; evicted IDs return `404`, and scan history is not
silently added to durable transfer persistence.

A scan resource should be stable and actionable:

```csharp
public sealed record ShareScanStateDto(
    Guid ScanId,
    long Revision,
    ShareScanPhase Phase,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long DirectoriesDiscovered,
    long FilesDiscovered,
    long BytesDiscovered,
    int ErrorCount,
    IReadOnlyList<ShareScanErrorSampleDto> ErrorSamples,
    IReadOnlyList<ResourceActionDto> AvailableActions);
```

The daemon revision is authoritative for live-state replacement. The scan
revision is useful for the scan detail resource, idempotent command responses,
and client-side suppression of stale detail requests.

### 14.2 Keep uploads in the existing transfer model

Active uploads remain rows in the existing replicated `Transfers` collection.
Queued uploads remain the same generic transfer resource and use the same detail,
cancel, and history contracts, but the complete waiting queue is queried through
a generic paginated live-transfer endpoint. It is not copied into every daemon
snapshot: the independent 100,000-entry queue safety ceiling makes that
operationally bounded but unsuitable for browser reconnects. This distinction
does not create a parallel upload-resource hierarchy.

At minimum, ownership becomes nullable:

```csharp
public sealed record TransferIdentityFieldsDto(
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? CandidateKey);
```

The existing typed component/delta pattern should be retained. Add cohesive
scheduling and runtime fields instead of repeatedly replacing the whole transfer:

```csharp
public sealed record TransferSchedulingFieldsDto(
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc);

public sealed record TransferQueueEstimateDto(
    int? AheadCount,
    long QueueRevision,
    DateTimeOffset CalculatedAtUtc,
    string? UnavailableReasonCode);

public sealed record TransferProgressFieldsDto(
    long BytesTransferred,
    long TotalBytes,
    long? BytesPerSecond,
    DateTimeOffset? LastProgressAtUtc);

public sealed record TransferDeltaDto(
    Guid TransferId,
    long Revision,
    TransferStateDto? Added = null,
    TransferStatusFieldsDto? Status = null,
    TransferSchedulingFieldsDto? Scheduling = null,
    TransferProgressFieldsDto? Progress = null);
```

The transfer status component MUST adopt the existing
`IReadOnlyList<ResourceActionDto> AvailableActions` convention, including a
cancel action only while the transfer is cancellable. Queue position is useful,
but it changes for many rows whenever the round-robin scheduler advances. Expose
`TransferQueueEstimateDto` from transfer detail or another bounded explicit query
for a selected queued transfer; do not generate O(queue length) live deltas after
every dequeue. The estimate is tied to `QueueRevision` and `CalculatedAtUtc`.
v4.0 MUST NOT promise an estimated start time; peer behavior, resumes,
cancellations, and speed limits make it misleading.

The concrete `TransferStatusFieldsDto` extension must also carry typed terminal
outcome, failure reason, and cancellation source plus `AvailableActions`.
Do not encode cancellation/rejection reasons into the existing free-form
`State` string or infer them from exception messages.

New finite state introduced by this feature MUST use string-serialized enums,
including `ShareCatalogLifecycleState`, `ShareScanPhase`, and
`UploadHealthState`. Extensible diagnostics use stable reason/error codes plus a
human-readable message. This PR need not convert every existing transfer string
to an enum, but it must not add new unstructured lifecycle strings.

### 14.3 Scope and bounded-state rules

- Daemon stream: all active uploads and the complete small sharing/upload runtime
  summary through `DaemonStateDto`; queue count/bytes/revision are summary state,
  not one replicated row per waiting upload.
- Paged live-transfer query: queued uploads ordered by stable admission time and
  transfer ID. Keyset continuation remains usable while the queue changes; every
  page reports the traversal's origin revision and the revision observed while
  producing that page.
- Workflow stream: no daemon-owned uploads; only transfers whose non-null
  `WorkflowId` matches the workflow.
- Historical transfer endpoints: both directions, filterable by direction and
  other existing transfer-history filters.
- Terminal upload removal: follows the existing bounded-active-state rule.
  Clients observe the terminal state before removal, and retained history remains
  available through HTTP.
- A queued transfer that never became active is observed through its command
  response/detail and paged live view. The server does not emit an add/remove
  delta for every waiting row.
- Activity events: upload rejection, scan error, and other transient notices are
  best effort and never required to rebuild the displayed state.

### 14.4 Client and reducer responsibilities

The implementation is incomplete if only server DTOs and endpoints are added.
The reusable client layer introduced by pull request #194 must be extended at the
same time:

- `DaemonClientStore` applies `DaemonStateDto` replacement by daemon revision and
  applies transfer identity/status/scheduling/progress components by transfer
  revision. It remains the authoritative in-memory live-state reducer.
- `SockseekLiveClient` owns reconnect, epoch/sequence recovery, snapshot reload,
  command forwarding, and bounded history/detail loading through its
  `SockseekApiClient`.
- `SockseekApiClient` exposes the HTTP contracts directly for callers that do not
  need live replication.
- Local CLI, remote CLI, and a future GUI consume these clients; they must not
  implement independent JSON models, state inference, or reconnect reducers.

The public client surface should include equivalents of:

```csharp
SharingStateDto? GetSharing();
UploadRuntimeStateDto? GetUploadRuntime();
IReadOnlyList<TransferStateDto> GetActiveTransfers();

Task<SharingStateDto> GetSharingAsync(CancellationToken cancellationToken);
Task<StartShareScanResponseDto> StartShareScanAsync(CancellationToken cancellationToken);
Task<ShareScanStateDto?> GetShareScanAsync(Guid scanId, CancellationToken cancellationToken);
Task CancelShareScanAsync(Guid scanId, CancellationToken cancellationToken);
Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken);
Task<LiveTransferPageDto> LoadLiveTransferPageAsync(
    LiveTransferFilter? filter = null,
    string? cursor = null,
    int limit = 100,
    LiveTransferConsistency consistency = LiveTransferConsistency.BestEffort,
    CancellationToken cancellationToken = default);
Task<CursorPage<TransferHistoryDto>> LoadTransferHistoryPageAsync(
    TransferHistoryFilter? filter = null,
    string? cursor = null,
    int limit = 100,
    CancellationToken cancellationToken = default);
```

The runtime page contract is explicit:

```csharp
public enum LiveTransferConsistency
{
    BestEffort,
    Strict
}

public sealed record LiveTransferPageDto(
    IReadOnlyList<TransferStateDto> Items,
    string? NextCursor,
    long OriginQueueRevision,
    long ObservedQueueRevision,
    bool QueueChanged);
```

The generic historical `CursorPage<T>` does not gain runtime queue semantics.

`DaemonClientStore` MUST NOT preload or retain all queued or historical
transfers. Live queue pages, history pages, and attempt details remain explicit
HTTP data owned by the requesting view or command. `SockseekLiveClient` may
provide the façade, but it must not turn either large collection into replicated
state.

The current `/api/transfers` collection is a historical persistence query. Keep
that pagination contract. `GET /api/transfers/{transferId}` is resolved with a
live-first overlay: the authoritative runtime row (including queue estimate and
actions) wins while present, and retained history is the fallback after live
removal. A newly accepted upload remains inspectable/cancellable even if its
asynchronous persistence mutation has not committed or persistence is degraded.
The response identifies whether it is `Live`, `Historical`, or a merged
point-in-time view so clients do not mistake persistence lag for disappearance.

### 14.5 HTTP resources and commands

Sharing adds these bounded resources and commands:

```text
GET  /api/sharing
POST /api/sharing/scans
GET  /api/sharing/scans/{scanId}
POST /api/sharing/scans/{scanId}/cancel
```

The transfer resources already introduced by the v4 API are extended, not
replaced or duplicated:

```text
GET  /api/transfers?direction=upload&...
GET  /api/transfers/live?direction=upload&state=queued&cursor=...&limit=...&consistency=bestEffort
GET  /api/transfers/{transferId}
POST /api/transfers/{transferId}/cancel
```

`/api/transfers` keeps its historical persistence pagination. The `/live`
collection is runtime-owned, direction-neutral, cursor-paged, and hard-limited;
it does not merge persistence rows. Its cursor binds the filter and stable
admission key `(RequestedAtUtc, TransferId)`.

The default `bestEffort` traversal continues across queue mutations. Each page
returns `OriginQueueRevision`, `ObservedQueueRevision`, and
`QueueChanged = OriginQueueRevision != ObservedQueueRevision`; a changing queue
can therefore cause a row to be skipped or observed on a later traversal, but it
cannot starve a GUI by continually forcing page one. A client that needs a
transactionally consistent traversal explicitly requests `consistency=strict`.
Only then does the cursor bind the origin revision for enforcement, and a
revision mismatch returns `409 QueueRevisionChanged`. Cursors always remain
opaque, integrity-protected, and bound to their filter, consistency mode, and
sort key.

Use explicit POST action routes for cancellation, consistent with existing job
and workflow cancellation endpoints. Cancelling a scan or transfer does not
delete its status/history resource.

Starting a scan has a typed, idempotent result:

```csharp
public enum StartShareScanResult
{
    Started,
    AlreadyRunning
}

public sealed record StartShareScanResponseDto(
    StartShareScanResult Result,
    ShareScanStateDto Scan);
```

Recommended status behavior:

- `202 Accepted`: a new scan was started.
- `200 OK`: a scan was already running and its current resource is returned.
- `400 Bad Request`: malformed command input.
- `404 Not Found`: unknown scan or transfer ID.
- `409 Conflict`: the resource exists but is no longer cancellable, or a strict
  live-transfer traversal returns `QueueRevisionChanged`.
- `503 Service Unavailable`: sharing infrastructure is unavailable.
- `401 Unauthorized`/`403 Forbidden`: the configured operator policy rejects a
  mutation command.

Cancellation is idempotent with respect to repeated requests: once the final
resource state is visible, another request returns that state or a stable
non-cancellable result rather than creating another transition.

Extend the existing API error response compatibly with a machine-readable code:

```csharp
public sealed record ApiErrorDto(
    string Error,
    string? Code = null);
```

GUI and CLI code use the stable code and HTTP status for control flow; the human
message is for display and diagnostics. Do not require clients to parse English
error strings.

`GET /api/sharing` is a bounded explicit status/detail query for one-shot CLI,
diagnostics, and clients not using SignalR. It returns the same public state
semantics as the daemon projection and must not become a separately computed
source of truth.

### 14.6 Discoverable actions

Use `ResourceActionDto` consistently with pull request #194. At minimum:

- A running scan advertises its cancel action.
- A queued or active transfer advertises its cancel action.
- Terminal and otherwise non-cancellable resources do not advertise cancel.

A normal GUI should render or enable commands from `AvailableActions`, not from a
hard-coded table of lifecycle states. The server still validates every command;
a published action is a resource-capability/discoverability aid, not
authorization. Because replicated daemon state is shared across callers, it
MUST NOT imply caller-specific permission. Mutation endpoints independently
enforce the operator policy and return `403` when the authenticated caller lacks
it; a future identity-capabilities endpoint may let clients hide commands
without making the state reducer caller-specific.

### 14.7 Public path and configuration privacy

`TransferStatusFieldsDto.LocalPath` currently exists for downloads. For uploads:

- Return null or a deliberately redacted display value.
- Never return a host absolute path in unauthenticated live state or history.
- Configured local roots are not returned by `GET /api/sharing`.
- Blacklist entries are not returned by ordinary remote status APIs.
- Scan error samples and failure messages are mapped to safe remote aliases and
  stable codes; raw `IOException.Message` values are not copied into public DTOs
  because they commonly contain absolute paths.
- An authenticated local-admin diagnostics/configuration surface can be designed
  later.

Share aliases and peer-visible remote paths are safe to expose; local roots are
not. v4.0 is therefore GUI-ready for monitoring and operations, not for editing
sensitive share-root or blacklist configuration.

### 14.8 CLI and remote-daemon semantics

Sharing and transfer inspection operate on daemon-owned state. Both command
families MUST use the same `SockseekLiveClient`/`SockseekApiClient` contracts and
work through the existing remote backend:

```text
sockseek share status --remote http://127.0.0.1:5030
sockseek share scan --remote http://127.0.0.1:5030
sockseek share scan --cancel --remote http://127.0.0.1:5030
sockseek transfers --direction upload --remote http://127.0.0.1:5030
sockseek transfer cancel <id> --remote http://127.0.0.1:5030
```

`sockseek transfers` is a daemon query/control command. It has no meaningful
one-shot local mode and MUST reject execution without a running daemon target
(or a future explicitly configured implicit daemon URL).

`sockseek share` is also primarily daemon control because status, publication,
scan cancellation, and the active generation belong to the running daemon. Do
not silently start a temporary local daemon. A future offline command such as
`share validate` or `share build-offline` would be a separately named operation
with separate locking rules.

CLI output should distinguish:

- Published generation, effective aliases, and aggregate counts.
- Active scan and progress.
- Last scan error.
- Listener reachability warning.
- Upload slots, queue, speed cap, and whether new uploads are accepted.
- Blacklist entry counts, without exposing the entries through ordinary remote
  status output.

The commands preserve pagination for transfer history and use server-provided
`AvailableActions` rather than independently inferring cancellability.

### 14.9 Protocol compatibility, serialization, and OpenAPI

Adding daemon sharing/upload fields, nullable transfer ownership, new transfer
components, and new delta payloads changes the live contract. The implementation
MUST make an explicit compatibility decision:

- If the feature lands before v4's live protocol is frozen and all v4 clients are
  released together, it may remain part of protocol version 4.
- If an already supported protocol-4 client is expected to interoperate with a
  server lacking or adding these semantics, increment `LiveProtocol.Version`.

In either case, update and test:

- `SockseekApiJsonContext` source-generated serialization registrations;
- OpenAPI schemas and examples;
- snapshot and delta JSON round trips;
- reducer behavior for duplicate, stale, overlapping, and gap-recovery deltas;
- local/remote CLI parity;
- generated or non-.NET client usability;
- serialized-traffic and bounded-snapshot tests.

Unknown optional fields may be tolerated where the current JSON contract permits
it, but version negotiation—not accidental deserialization behavior—is the
compatibility boundary.

### 14.10 GUI-readiness boundary

With this contract, a GUI can safely:

- display catalog lifecycle, counts, scan progress, bounded errors, and health;
- start and cancel scans;
- display active and queued uploads, selected-transfer queue estimates, slots,
  throughput, listener
  readiness, and rejection/unavailable reasons;
- cancel eligible transfers through advertised actions;
- page combined or direction-filtered transfer history and attempt details;
- recover after reconnect, daemon restart, epoch change, and sequence gaps using
  the same reducer as the CLI.

v4.0 intentionally does not make a GUI capable of reading or mutating absolute
share roots, blacklist entries, or upload policy configuration. Those operations
require a separately designed authenticated local-admin boundary.

### 14.11 Authentication and authorization integration

The daemon currently defaults to loopback and does not yet implement the
username/password Web UI authentication listed in `TODO.md`. Sharing must not
invent a second auth scheme, but it must leave a real enforcement point:

- all scan start/cancel and transfer-cancel handlers call one named operator
  authorization policy before resource lookup/mutation so unauthorized callers
  cannot use status differences to enumerate command targets;
- `ResourceActionDto` action IDs remain stable across the later authentication
  implementation;
- remote CLI clients can attach the same future credentials as other API calls;
  and
- documentation states plainly that binding the current unauthenticated daemon
  to a non-loopback address gives network clients operator control.

The final v4.0 release must apply the roadmap's authentication policy to these
commands. Feature work may be merged earlier under the existing loopback trust
model, but tests must prove authorization failures once the policy is present.

## 15. Persistence integration

### 15.1 Catalog versus durable history

These are separate persistence concerns:

| Concern | Authority | Store | Recovery |
|---|---|---|---|
| Published share catalog | Catalog generation | Separate SQLite index | Rebuildable |
| Active upload queue | `UploadScheduler` memory | None authoritative | Interrupted on restart |
| Active upload projection | Runtime snapshots | Memory | Rebuilt from runtime |
| Upload history | Runtime changes | `sockseek.sqlite3` | Durable |

Do not route catalog rows through `PersistenceCoordinator`.

### 15.2 Transfer persistence changes

The transfer entity already allows null `JobId` and `WorkflowId`. Required work:

- Make Core/API ownership nullable.
- Map generic transfer changes rather than download-only changes.
- Persist `Direction = Upload` and peer identity.
- Ensure attempt writes do not require a job row.
- Reconcile unfinished upload transfers and attempts as `Interrupted` at startup.
- Add/verify indexes for `(direction, started_at)`, `(username, direction,
  started_at)`, and active/terminal query patterns.
- Keep all history queries paginated.

### 15.3 Outcome taxonomy

Keep three concepts separate rather than putting rejection, failure, and
cancellation into one string:

Admission rejection codes:

```text
PeerDenied
FileNotShared
UploadsUnavailable
OutstandingFileLimitExceeded
OutstandingByteLimitExceeded
GlobalCapacityExceeded
RequestOverloaded
RequestTimedOut
```

Accepted-transfer failure codes:

```text
FileUnavailable
FileNoLongerShared
FileChanged
FileChangedDuringTransfer
InvalidOffset
PeerDisconnected
ConnectionFailed
TransferTimedOut
Unknown
```

Cancellation source:

```text
User
Peer
DaemonShutdown
CatalogInvalidation
```

Detailed exception type/message can remain redacted diagnostics. API clients
use typed terminal outcome plus the relevant stable reason/source.

### 15.4 Persistence non-interference

Upload progress and peer callbacks MUST continue when historical persistence is
slow or degraded. Terminal mutations keep the same priority guarantees as
terminal download mutations. A persistence outage may degrade history, but it
must not hold an upload slot or block the network callback path.

“Durable history” means durable when the bounded persistence projection accepts
and commits the mutations. The existing persistence design deliberately
coalesces and may evict projections during a prolonged outage. Sharing does not
weaken that boundary or block admission to preserve audit completeness:

- terminal mutations contain enough state to upsert a complete transfer and,
  when dispatch created one, its attempt even if an earlier structural mutation
  has not committed;
- dropped/evicted upload projections increment existing incomplete-history
  health counters and are visible operationally;
- startup reconciliation applies to unfinished rows that actually reached the
  database; and
- documentation must not claim lossless upload audit history through arbitrary
  persistence outages.

---

## 16. Startup, reconnect, and shutdown

### 16.1 Startup order

Recommended order:

1. Start historical persistence and perform transfer reconciliation when
   available. A degraded/disabled history projection does not prevent catalog
   or session startup.
2. Validate sharing/upload settings.
3. Open and validate the current catalog manifest/generation.
4. Construct sharing and upload runtimes.
5. Construct Soulseek.NET options with all resolver delegates.
6. Start the Soulseek session and listener.
7. Login.
8. Publish current share counts and dynamic user info.
9. If no valid catalog exists, complete the initial scan before advertising
   nonzero shares; if a valid catalog exists, rescan in the background.
10. Start periodic scan scheduling.

A valid prior catalog should allow fast reconnect without waiting for a full
rescan.

Manifest validity includes the settings fingerprint, catalog schema, browse
wire/serializer version, and required artifact hash. A package/serializer
upgrade that changes wire compatibility makes the prior browse artifact
unavailable and triggers rebuild; it is never served optimistically.

### 16.2 Reconnect

On Soulseek reconnect:

- Resolver services remain alive.
- Republish current shared file/folder counts.
- Reapply effective upload-speed/listener options as supported.
- Retain the last valid excluded-phrase matcher and follow the Phase 0
  authoritative-set readiness rule before resuming search responses.
- Do not duplicate queued uploads merely because the server session changed.
- Active peer transfers follow Soulseek.NET's connection behavior; failures are
  terminalized normally.

### 16.3 Graceful shutdown

Shutdown order:

1. Stop accepting new scans and upload requests.
2. Cancel a staging scan.
3. Terminalize queued uploads immediately as `Interrupted` with daemon-shutdown
   source.
4. Allow active uploads to finish within the server shutdown timeout, or cancel
   them when the remaining budget is exhausted.
5. Publish exactly one terminal or interrupted runtime state per accepted upload
   and enqueue its idempotent terminal upsert to the bounded history projection.
6. Drain terminal persistence mutations within its own configured limit.
7. Dispose published catalog leases/readers.
8. Disconnect and dispose the Soulseek session.

The shutdown budget must be shared explicitly; nested components must not each
consume the full 30 seconds.

---

## 17. Failure behavior and health

### 17.1 Health states

Sharing:

```text
Unconfigured
Initializing
Ready
Stale                 (scan failed; old catalog served)
Unavailable           (configured, but no readable catalog)
Stopping
```

`Scanning` is the active operation in `ActiveScan`, not catalog health. A
catalog can be `Ready` or `Stale` while a new scan is running. Keeping these
axes orthogonal follows the roadmap's distinction between replicated state and
activity and avoids composite-state growth such as
`ScanningWithStaleCatalog`.

Uploads:

```text
Unconfigured
Ready
Degraded              (listener warning, persistence degradation, stale files)
Unavailable           (configured, but client/session/catalog not ready)
Stopping
```

### 17.2 Operational warnings

Emit clear warnings when:

- Shares are configured, but the peer listener is not ready.
- No share root exists or is readable.
- A configured alias duplicates another alias.
- The catalog settings hash does not match current roots/filters.
- A scan fails and an older generation remains active.
- A file repeatedly fails revalidation at upload time.
- A browse response exceeds the safe memory/size limit.
- A browse artifact is unavailable, oversize, or pinned by a long-lived stream
  lease.
- Catalog staging approaches the free-space reserve, or orphan/retired-generation
  cleanup cannot restore the expected disk envelope.
- Incoming search or upload circuit breakers drop requests.
- Search serving is waiting for an authoritative excluded-phrase set or rejected
  an invalid/oversized replacement.
- Persistence is degraded while uploads continue.

### 17.3 Metrics

At minimum:

```text
sockseek_share_catalog_files
sockseek_share_catalog_directories
sockseek_share_catalog_bytes
sockseek_share_scan_duration_seconds
sockseek_share_scan_files_total
sockseek_share_scan_errors_total
sockseek_share_requests_dropped_total{type=search|browse|upload}

sockseek_upload_active
sockseek_upload_queued
sockseek_upload_queued_bytes
sockseek_upload_bytes_total
sockseek_upload_completed_total{outcome=...}
sockseek_upload_rejected_total{reason=...}
sockseek_upload_duplicates_coalesced_total
sockseek_upload_queue_wait_seconds
sockseek_upload_queue_summary_deltas_total
```

Avoid high-cardinality labels such as username, path, or transfer ID.

---

## 18. Security and abuse resistance

The feature exposes local files to untrusted internet peers. The release must be
reviewed as a security feature, not only a transfer feature.

Required controls:

- Exact catalog lookup; no raw filesystem lookup from peer input.
- Canonical containment checks at scan time and handle-based final-target/file
  identity checks after open.
- Symbolic-link/reparse replacement escape prevention across the scan-to-open
  race.
- No absolute local paths in peer/API responses or ordinary logs.
- Bounded incoming search and upload request queues.
- Hard search and directory response limits.
- Per-user queue file/byte limits.
- Exact username and exact IP deny checks before serving or admitting work.
- Duplicate request coalescing.
- Finite regex execution time, parameterized/escaped FTS input, and bounded
  term/exclusion counts.
- Safe handling of malformed Unicode and path separators.
- Rejection of remote comparison-key collisions and unrepresentable local
  segments.
- Read-only file streams.
- Owner-only permissions/ACLs on catalog, artifact, manifest, and staging files.
- No execution or metadata side effects.
- No unauthenticated configuration mutation.
- Redacted diagnostics by default.
- Soulseek-server excluded phrases applied before outgoing search responses.

A root can contain private data by operator mistake. Startup output should print
effective aliases and counts, but local paths only at debug level or in a local
console confirmation—not in remote live state. Alias derivation must never make
an ancestor directory visible.

Blacklists are abuse controls, not an authentication boundary. Exact IP entries
may be ineffective when a peer reconnects from another address, and they must
not be described as CIDR/range blocking.

---

## 19. Suggested project and file layout

```text
Sockseek.Core/
  IO/
    ExactLengthReadStream.cs        # throws on early EOF; caps declared length
  Sharing/
    ShareCatalogSnapshots.cs
    ShareScanChanges.cs
    SharePath.cs
    ShareCatalogContracts.cs
    PeerAccessPolicy.cs
  Transfers/
    TransferChanges.cs             # direction-neutral state changes
    Uploads/
      UploadCoordinator.cs
      UploadScheduler.cs
      UploadAdmissionPolicy.cs
      UploadSnapshots.cs
      SafeSharedFileOpener.cs
  Settings/
    SharingSettings.cs
    UploadSettings.cs
    PeerAccessSettings.cs
  Soulseek/
    SoulseekSession.cs             # extracted/shared lifecycle
    SoulseekInboundRequestRouter.cs
    ISoulseekBrowseArtifactBuilder.cs
    SoulseekBrowseArtifactBuilder.cs

Sockseek.Persistence/
  Sharing/
    SqliteShareCatalogReader.cs
    SqliteShareCatalogBuilder.cs
    ShareCatalogManifest.cs
    ShareCatalogManager.cs
    ShareCatalogSchema.cs
  Write/
    TransferPersistenceAdapter.cs  # extended for uploads
  Read/
    TransferHistoryReader.cs       # direction filters/pagination

Sockseek.Server/
  DaemonStateStore.cs              # generalized EngineStateStore
  SoulseekFeatureHost.cs           # one coordinated hosted-service lifecycle
  Endpoints/
    SharingEndpoints.cs
    TransferEndpoints.cs

Sockseek.Api/
  Contracts/
    Sharing.cs
    LiveState.cs                   # daemon component + transfer component extensions
    ServerTransfers.cs
  Client/
    SockseekApiClient.cs
    SockseekLiveClient.cs
    DaemonClientStore.cs

Sockseek.Core.Tests/
  Sharing/
  Uploads/

Sockseek.Persistence.Tests/
  Sharing/
  UploadPersistenceTests.cs

Sockseek.Server.Tests/
  SharingEndpointTests.cs
  UploadLiveStateTests.cs
  SharingClientContractTests.cs
  SharingOpenApiTests.cs
```

Names may change, but the dependency boundaries should not.

---

## 20. Implementation sequence and phase stop conditions

A phase is not complete because its primary class exists. Each phase stops only
when its evidence and stop condition are satisfied.

### Phase 0 — Contract, protocol, and performance spike

Work:

1. Pin and inspect the exact Soulseek.NET version used by v4.
2. Record the already identified resolver signatures, `RawBrowseResponse`
   contract, one-upload-per-user constraint, queue callbacks, peer endpoint
   availability, upload resume semantics, cancellation behavior, and global
   speed-limit behavior.
3. Build a small real-client harness using at least two independent Soulseek
   clients.
4. Benchmark full-browse object construction and serialization with generated
   10k, 100k, and 1M-row catalogs.
5. Implement a small streaming browse serializer spike and prove that
   `RawBrowseResponse` serves it; prefer an upstream disposal fix, with a
   narrowly pinned fork permitted only when package timing requires it.
6. Select and spike the handle-based safe-open strategy and produce the
   OS/filesystem/mount support matrix from section 7.4.
7. Define the pinned release qualification host and capture matched raw
   filesystem-enumeration, metadata-reader, minimal loopback TCP, ungoverned
   Soulseek.NET upload, and SQLite baselines.
8. Measure staging/catalog/artifact disk envelopes and record the minimum
   free-space reserve/check cadence.
9. Observe excluded-phrase delivery ordering, empty-set semantics, and reconnect
   behavior with the pinned server/library and real-client harness.
10. Force raw-browse network failure before and during streaming and select the
    exact-EOF/dispose/self-expiring lease strategy plus a verified library
    disposal fix/upgrade when available. Separately measure the library
    connection-timeout bound for a write already stalled after a read.
11. Measure metadata-light and metadata-heavy scan phases plus database
    finalization, artifact serialization, hash/validation, and total
    scan-to-publication time. Record numeric scan and artifact ceilings before
    Phase 1; an unresolved `TBD` is a stop condition.

Stop condition:

- [ ] **P0-01** Every inbound resolver and upload callback has a contract test or
  a written source reference.
- [ ] **P0-02** One tested username canonicalization/comparison rule is used by
  every deny, duplicate, gate, counter, and scheduler key, and the adapter can
  obtain an IP address wherever exact-IP enforcement is promised.
- [ ] **P0-03** A browse-path decision is recorded with measured peak memory and
  serialization cost, and a streaming artifact is parsed successfully by the
  pinned library/real client.
- [ ] **P0-04** Resume at zero, middle, EOF, and invalid offsets is observed with
  real clients.
- [ ] **P0-05** The speed cap is either proven usable or an alternative governor
  is designed; the feature may not silently ignore the setting.
- [ ] **P0-06** No open library uncertainty can invalidate the architecture in
  later phases.
- [ ] **P0-07** The supported-filesystem matrix has a handle-based safe-open
  contract test for every `Supported`/`Conditional` row that defeats replacement
  by a symlink/reparse point between scan and open; unqualified capabilities fail
  the root closed.
- [ ] **P0-08** The free-space reserve rule covers catalog, SQLite temporary/WAL,
  and browse-artifact peak disk use on the qualification fixtures.
- [ ] **P0-09** Fresh-login and reconnect tests prove when excluded-phrase state
  is authoritative and when outgoing search must remain fail-closed.
- [ ] **P0-10** A failed/stalled `RawBrowseResponse` write releases its browse
  permit, generation lease, and file handle within the documented deadline;
  future reads fail, and the separately measured connection timeout bounds the
  remaining network operation.
- [ ] **P0-11** The qualification record contains numeric metadata-light,
  metadata-heavy, artifact-build, and total-publication duration ceilings used
  by `BUDGET-17` and `BUDGET-18`.

### Phase 1 — Settings, aliases, access policy, and path model

Work:

1. Add daemon settings for sharing, uploads, and peer access.
2. Implement `[Alias]path` and alias-less `path` parsing.
3. Implement derived alias, duplicate alias, overlap, exclusion, regex, and
   exact-IP validation.
4. Implement current list replace/append semantics for all new list options.
5. Implement canonical remote-path value objects and containment checks.
6. Implement `PeerAccessPolicy` with exact username/IP matching.
7. Remove manual shared-count settings and CLI aliases completely; catalog
   counts are the only authority.

Stop condition:

- [ ] **P1-01** Alias-less roots expose only the final directory name in every
  generated remote path.
- [ ] **P1-02** Explicit and derived alias collisions fail configuration with
  actionable diagnostics.
- [ ] **P1-03** Hostile path, Unicode, separator, volume-root, symlink/reparse,
  replacement-race, remote-collision, and overlap test corpora pass on Windows
  and Unix runners.
- [ ] **P1-04** CIDR, hostname, regex username, and malformed IP entries are
  rejected rather than interpreted approximately.
- [ ] **P1-05** List replacement and explicit `+ ` append behavior is tested
  across default config, named profiles where permitted, command-line settings,
  and remote daemon startup.
- [ ] **P1-06** `SharedFiles`, `SharedFolders`, `NoModifyShareCount`,
  `--shared-files`, `--shared-folders`, `--nmsc`, and
  `--no-modify-share-count` no longer exist in runtime settings, help, README,
  generated config documentation, or tests.
- [ ] **P1-07** Every share filter has a finite match timeout; timeout fails the
  staging scan and never publishes an unfiltered entry.
- [ ] **P1-08** `RemotePathKey` v1 is the one versioned identity rule used by
  aliases, scan collision detection, exact lookup, and upload duplicate keys;
  fixed hidden/system policy tests pass on supported OS runners.

### Phase 2 — Generation-based catalog and scanner

Work:

1. Implement manifest and catalog/browse-artifact generation lifecycle.
2. Implement schema creation and read-only catalog reader.
3. Implement a streaming, bounded directory/file pipeline and batched writer.
4. Add FTS search, exact lookup, directory listing, counts, remote-collision
   detection, and validation.
5. Implement startup restore, recognized-orphan/failed-build cleanup,
   old-generation retention, free-space checks, and atomic publication.
6. Add scan progress Core changes and bounded error samples.
7. Build the browse wire artifact with the bounded streaming serializer.
8. Add prior-generation metadata reuse only if Phase 0/2 measurements show it
   is needed to pass scan performance gates; slskd parity does not require it.

Stop condition:

- [ ] **P2-01** Generation N remains searchable, browsable, and resolvable while
  N+1 builds, fails, cancels, and publishes.
- [ ] **P2-02** A scan never stores all discovered directory or file paths in one
  collection.
- [ ] **P2-03** Process interruption at every manifest/publication step recovers
  to either the old or new complete generation, never a partial generation.
- [ ] **P2-04** A corrupted staging generation cannot become current.
- [ ] **P2-05** Catalog queries have verified indexes/query plans at the release
  fixture sizes.
- [ ] **P2-06** `PERF-01` through `PERF-13` pass.
- [ ] **P2-07** The catalog and browse artifact publish as one generation, and
  artifact output is protocol-equivalent without a whole-tree object/byte
  allocation.
- [ ] **P2-08** Catalog data has owner-only permissions, staging stops before the
  free-space reserve, and crash/orphan cleanup never deletes unknown files.
- [ ] **P2-09** `PERF-31`, `BUDGET-17`, and `BUDGET-18` pass with separately
  reported scan, database-finalization, artifact, validation, and publication
  timings.

### Phase 3 — Soulseek sharing integration

Work:

1. Introduce the daemon-lifetime Soulseek inbound router.
2. Wire user-info, search, browse, and directory resolvers.
3. Apply username/IP policy before expensive catalog/serialization work.
4. Add incoming search/browse throttling, timeouts, and result limits.
5. Track and apply Soulseek-server excluded search phrases.
6. Publish catalog counts after login and publication.
7. Add listener/catalog health.
8. Serve the generation artifact through lease-owning `RawBrowseResponse`
   streams.

Stop condition:

- [ ] **P3-01** Two real test clients can search, browse, and retrieve directory
  contents from explicit and alias-less roots.
- [ ] **P3-02** A peer cannot observe an absolute path or ancestor directory.
- [ ] **P3-03** Blacklisted usernames receive no share responses.
- [ ] **P3-04** Blacklisted exact IPs receive no share responses wherever the
  endpoint is available.
- [ ] **P3-05** An oversized browse is either served within the release envelope
  or rejected before proportional allocation.
- [ ] **P3-06** `PERF-14` through `PERF-18` pass.
- [ ] **P3-07** Server-supplied and request-supplied excluded phrases cannot
  appear in outgoing search results.
- [ ] **P3-08** Excluded-phrase state is atomically bounded; invalid/oversized or
  not-yet-authoritative state disables search serving without disabling browse
  or uploads.

### Phase 4 — Upload scheduler and coordinator

Work:

1. Implement admission policy and keyed per-user gates.
2. Implement strict user-round-robin scheduler.
3. Implement slot accounting and queue estimates.
4. Implement exact file revalidation and resume stream factory.
5. Wire Soulseek.NET upload callbacks, progress, terminal states, and
   cancellation.
6. Apply global upload speed limit.
7. Revalidate access policy immediately before dispatch.
8. Re-resolve queued paths against the current generation and enforce one active
   upload per normalized username.

Stop condition:

- [ ] **P4-01** Multiple peers demonstrate deterministic fair ordering and FIFO
  order within each user.
- [ ] **P4-02** Username/IP-denied requests never create a transfer record or
  consume queue limits.
- [ ] **P4-03** Valid resumes complete byte-for-byte and invalid offsets fail with
  stable protocol/domain outcomes.
- [ ] **P4-04** No requested remote path can escape a configured root, including
  after file replacement or link manipulation between scan and open.
- [ ] **P4-05** Slot and byte accounting returns to zero after every terminal,
  rejected, and cancelled path.
- [ ] **P4-06** `PERF-19` through `PERF-24`, `PERF-26`, and `PERF-28` pass.
- [ ] **P4-07** Catalog publication cannot cause a queued request to serve a
  removed or substituted file, and no queued entry pins an old generation.
- [ ] **P4-08** Duplicate callback and attempt-cardinality tests prove that a
  coalesced request creates nothing new, queued terminal transfers have zero
  attempts, and a dispatched transfer has exactly one.
- [ ] **P4-09** `PERF-32`, `BUDGET-11`, and `BUDGET-19` pass.

### Phase 5 — Live state, reducer, and persistence

Work:

1. Generalize `EngineStateStore` into a daemon state projection.
2. Extend the existing revisioned `DaemonStateDto` with sharing and upload
   runtime components; do not add a second singleton replication mechanism.
3. Add typed scan/catalog/upload-health DTOs, stable reason codes, and
   `AvailableActions`.
4. Add transfer scheduling/runtime components, put active daemon-scoped uploads
   in snapshots/deltas, and implement a revisioned, paginated runtime query over
   the waiting queue.
5. Extend `DaemonClientStore` and `SockseekLiveClient` so snapshot, delta,
   reconnect, gap recovery, and paged transfer queries work without
   GUI/CLI-specific reducers or whole-queue retention.
6. Extend persistence mapping for generic transfer changes.
7. Add upload restart reconciliation and direction indexes.
8. Ensure local paths and blacklist entries are redacted.
9. Update JSON source generation and make the explicit live-protocol version
   decision described in section 14.9.

Stop condition:

- [ ] **P5-01** A client reconstructs catalog, scan, upload runtime, and active
  upload state from one snapshot plus deltas without relying on activity events.
- [ ] **P5-02** Sharing/uploads use `DaemonStateDto` replacement by daemon
  revision; no independent top-level singleton revision/reducer exists.
- [ ] **P5-03** Duplicate, stale, overlapping, reordered-within-contract, and
  sequence-gap test cases converge through the same `DaemonClientStore` used by
  CLI and future GUI consumers.
- [ ] **P5-04** Scan and cancellable transfer resources advertise correct
  `AvailableActions`; terminal resources do not.
- [ ] **P5-05** Completed uploads and attempts remain queryable after restart.
- [ ] **P5-06** Interrupted active uploads reconcile exactly once and are not
  automatically requeued.
- [ ] **P5-07** A blocked persistence writer cannot stall peer callbacks,
  scheduling, progress, cancellation, live-state publication, or client gap
  recovery.
- [ ] **P5-08** Public JSON and logs at ordinary levels contain no absolute share
  roots or blacklist contents.
- [ ] **P5-09** Source-generated JSON and live-protocol compatibility tests cover
   every new snapshot/delta component and nullable transfer owner.
- [ ] **P5-10** A 100k-entry waiting queue changes only the small daemon queue
  summary; the runtime can produce bounded live pages and does not serialize the
  queue into a reconnect snapshot.
- [ ] **P5-11** `PERF-27`, `PERF-29`, `PERF-30`, and `BUDGET-16` pass, including
  best-effort paging under churn, strict revision rejection, and coalesced
  daemon-summary bursts.

### Phase 6 — HTTP API, reusable clients, remote CLI, and documentation

Work:

1. Add bounded sharing status, scan start/detail, and explicit POST cancel
   endpoints.
2. Extend the existing transfer detail/history endpoints for uploads, add the
   generic paged live-transfer collection, and add transfer cancellation; do not
   create duplicate upload-history endpoints.
3. Add typed scan-command results, stable machine-readable API error codes, and
   documented HTTP status behavior.
4. Extend `SockseekApiClient` and `SockseekLiveClient` with sharing commands,
   transfer cancellation, and bounded live/history/detail loaders.
5. Add daemon-backed `share`, `transfers`, and transfer-cancel CLI commands using
   those clients.
6. Require a running daemon target for transfer commands.
7. Add metrics and health details.
8. Add Docker volume, filesystem support matrix, permissions,
   listener/port-forwarding, blacklist, alias, and rescan documentation.
9. Document restart-required settings, GUI-readiness boundaries, and unsupported
   configuration mutation.

Stop condition:

- [ ] **P6-01** Every command works through `--remote` against a running daemon
  using the same public clients available to a GUI.
- [ ] **P6-02** `sockseek transfers` without a daemon target returns a clear usage
  error and never starts an ephemeral engine.
- [ ] **P6-03** Starting an already-running scan returns the typed
  `AlreadyRunning` result and the existing scan resource without duplication.
- [ ] **P6-04** Scan and transfer cancellation use explicit POST action routes,
  stable statuses/codes, and idempotent state transitions.
- [ ] **P6-05** A clean installation can be configured, scanned, queried,
  diagnosed, and cancelled without reading source code.
- [ ] **P6-06** Remote status exposes effective aliases/counts and operational
  state but not roots or blacklist entries.
- [ ] **P6-07** OpenAPI, source-generated JSON, help, client methods,
  documentation, and implementation agree.
- [ ] **P6-08** A thin test GUI/view-model can display state, reconnect, page
  live/history transfers, and invoke advertised actions without hard-coded
  lifecycle inference or direct HTTP/SignalR parsing.

### Phase 7 — Scale and release hardening

Work:

1. Run the full physical and synthetic scale matrix.
2. Measure peak RSS, retained managed heap, allocations, elapsed time, CPU, SQLite
   query plans, and request latency.
3. Fuzz path, regex, FTS, username, and IP input.
4. Test corrupted/truncated catalog recovery and abrupt kill at publication.
5. Test persistence degradation while uploads remain active.
6. Run cross-platform filesystem and real-client interoperability tests.
7. Save the release benchmark report as a CI/release artifact.

Stop condition:

- [ ] **P7-01** Every mandatory item in sections 21 and 22 passes.
- [ ] **P7-02** No stop-work condition in section 23 is open.
- [ ] **P7-03** Every final release-gate question in section 24 can be answered
  yes.

---

## 21. Test and performance qualification matrix

### 21.1 Unit and contract tests

- Share item parsing with explicit and derived aliases.
- Duplicate/invalid alias rejection.
- List replacement and `+ ` append behavior.
- Root overlap and exclusion validation.
- Exact username normalization and matching.
- Exact IPv4, IPv6, and IPv4-mapped IPv6 matching.
- Rejection of CIDR, hostname, country, and regex blacklist forms.
- Remote path normalization.
- `.`/`..`, rooted, mixed-separator, NUL, Unicode, and case tests.
- `RemotePathKey` v1 golden vectors, normalization/scalar-fold determinism, and
  fuzz tests proving scan, aliases, duplicate keys, and exact SQLite
  lookup/uniqueness use the same binary value rather than `NOCASE` or FTS.
- Catalog-key version mismatch/rebuild behavior across a simulated runtime or
  Unicode-data change.
- Remote comparison collisions on case-sensitive filesystems and local segments
  containing Soulseek separators.
- Checked protocol count/string-length boundaries and invalid Unicode encoding
  inputs.
- Symbolic-link/reparse escape tests.
- Handle-based replacement-race safe-open tests.
- Fixed Windows hidden/system and Unix dot-entry policy, including hidden
  directories, an explicitly configured hidden root, and attribute-read failure.
- Filter compilation, matching, and deterministic timeout failure.
- Strict round-robin scheduler ordering.
- FIFO ordering within a user.
- At most one active upload per normalized username.
- Duplicate upload coalescing completes the void enqueue callback, creates no
  transfer/counter, and lets `PlaceInQueueResolver` report the existing entry.
- Queued cancellation/invalidation creates zero attempts; dispatch creates
  exactly one, and no terminal path creates a second attempt.
- Per-user file and byte limits.
- Queue-position estimate calculation, revisioning, and bounded explicit queries.
- Deep 100k-queue estimates return typed unavailable within their work budget and
  never stall scheduler mutations.
- State-machine transition validity.
- Typed scan-command results and stable API error-code mapping.
- `AvailableActions` publication for cancellable and terminal resources.
- Daemon component replacement and stale-revision suppression.
- JSON round trips for nullable transfer ownership and every new enum/component.

### 21.2 Catalog integration tests

- Empty directory survives scan and appears in browse.
- Filtered and excluded files do not appear.
- Alias-less roots expose only the derived alias.
- File metadata is stored when valid and omitted safely when invalid.
- Exact lookup never uses a host path supplied by the caller.
- FTS query is escaped and result-limited.
- Request exclusions and Soulseek-server excluded phrases are applied with
  bounded over-fetch.
- Excluded-phrase login/reconnect ordering, atomic replacement, invalid/oversized
  fail-closed behavior, and last-valid-set retention.
- Old generation remains readable during scan.
- Failed/cancelled scan never publishes partial rows.
- Manifest swap survives process interruption at each publication step.
- Corrupt current generation falls back to a retained prior generation or
  requests rebuild without silently replacing user data.
- Outstanding leases can finish while a new generation publishes.
- Browse artifact is protocol-equivalent, hash-validated, atomically published,
  and constructed without whole-tree retention.
- Browse disconnect/stall before and during raw streaming releases every permit,
  generation lease, and file handle by the idle deadline; future reads fail and
  the separately configured library connection timeout bounds an already
  stalled write.
- Every filesystem/mount matrix row claiming upload support passes final-target,
  stable-identity, link, rename/replacement, and hidden-attribute tests.
- Optional metadata reuse, if implemented, has parity tests against a forced
  full metadata read.

### 21.3 Sharing and upload integration tests

- Search, browse, and directory contents from explicit and derived aliases.
- Username deny on every sharing resolver.
- Exact IP deny on every resolver where an endpoint is available.
- Immediate upload start when a slot is free.
- Queue when all slots are used.
- Fair alternation among three users.
- One prolific user cannot block a later user indefinitely.
- Queue cancellation frees limits and updates positions.
- Active cancellation frees a slot and starts the next upload.
- Resume at zero, middle, and exact EOF.
- Stream-factory resumes disable the library's automatic second seek and dispose
  the exact-length wrapper on success, cancellation, and failure.
- Reject negative/oversized offsets.
- File missing, replaced, linked, or changed after scan.
- File shrink/growth or in-place modification after safe-open causes bounded
  failure/stale health; premature EOF cannot spin a library write loop.
- Queued file removed/replaced by a new catalog generation.
- Peer disconnect and timeout.
- No configured roots or no published catalog.
- Search endpoint resolution and exact-IP denial before a non-empty response.
- Listener-disabled health warning.
- Speed cap behavior within documented tolerance.

### 21.4 Live-state, persistence, API, client, and remote CLI tests

- Upload has null job/workflow ownership.
- `DaemonStateDto` contains sharing and upload runtime components.
- No parallel top-level sharing/upload singleton revision mechanism exists.
- Daemon snapshot contains active uploads and the queue count/bytes/revision, but
  not one row per queued upload.
- The paged live-transfer query continues by stable admission key during churn,
  reports origin/observed revisions in best-effort mode, and rejects a revision
  change only when strict consistency was requested.
- A 100,000-mutation queue burst is latest-value coalesced and does not produce
  one outbound daemon delta per mutation; replicated terminal transfer changes
  remain ordered and prompt.
- Workflow snapshot excludes daemon uploads.
- Identity, status, scheduling, and progress deltas coalesce/apply by their
  documented revisions.
- Duplicate/stale deltas are ignored and sequence gaps trigger snapshot recovery.
- Terminal delta precedes live removal correctly.
- Scan and transfer `AvailableActions` match command eligibility.
- Upload and attempt history persist.
- Restart changes active upload/attempt to `Interrupted`.
- Existing transfer history query is direction-filtered and paginated.
- Queue-position detail is revisioned and does not emit queue-wide deltas when the
  scheduler advances.
- Live transfer detail works before persistence commit and while persistence is
  degraded, then falls back to historical detail after removal.
- Starting a concurrent scan returns `AlreadyRunning` and the existing resource.
- Scan and transfer cancellation use explicit POST actions and are idempotent.
- Stable API error codes distinguish unavailable, unknown, and non-cancellable
  resources without message parsing.
- Local absolute path and blacklist entries are absent from public JSON.
- Public scan error samples/messages cannot contain local absolute paths.
- Persistence queue saturation does not stall upload progress callbacks.
- `SockseekApiClient`, `SockseekLiveClient`, and `DaemonClientStore` cover every
  sharing/upload operation used by CLI and the test view-model.
- OpenAPI and source-generated JSON include every new contract.
- Every `share` and `transfers` operation works with `--remote`.
- Transfer commands fail clearly without a daemon target.
- Operator-policy authorization is enforced for every scan/transfer mutation.

### 21.5 Qualification fixtures

Two fixture classes are mandatory:

1. **Physical filesystem fixture:** at least 100,000 files and 10,000 directories,
   including empty directories, deep paths, large single directories, Unicode,
   filtered files, exclusions, and inaccessible-entry simulations where the
   platform permits them. Run a metadata-light variant and a metadata-heavy
   variant with representative valid, absent, and malformed audio metadata;
   each has a matched baseline that performs the same enumeration, stat, and
   metadata reads without catalog writes or artifact construction.
2. **Synthetic catalog fixture:** at least 1,000,000 files and 100,000 directories
   with realistic path/metadata distributions, plus one directory containing at
   least 10,000 files.

The scheduler fixture contains at least 100,000 queued uploads across at least
1,000 users and includes duplicate-key, per-user FIFO, admission-order, transfer
snapshot, and counter state. Transfer-history tests contain at least 100,000
transfer rows.

PR CI MAY use reduced deterministic fixtures for speed, but release qualification
MUST run the sizes above on a pinned, documented host. The report records CPU,
RAM, storage medium, filesystem, OS, .NET runtime, SQLite version, cold/warm
cache state, and exact Sockseek commit.

### 21.6 Structural performance requirements

These are mandatory on every platform and are not negotiable through faster
hardware:

- [ ] **PERF-01** Filesystem traversal is streaming and bounded; no collection
  retains all discovered directory or file paths.
- [ ] **PERF-02** Scanner channels and metadata workers have configured hard
  bounds, and observed queue depth never exceeds them.
- [ ] **PERF-03** Catalog writes are batched; transaction count scales with batch
  count, not file count.
- [ ] **PERF-04** Publication is an atomic manifest/handle switch and does not
  copy or deserialize the complete catalog.
- [ ] **PERF-05** Search uses an indexed plan and memory proportional to the
  configured result limit.
- [ ] **PERF-06** Exact remote-path resolution uses an indexed unique lookup.
- [ ] **PERF-07** Directory listing uses an indexed parent lookup and a hard item
  or serialized-size limit.
- [ ] **PERF-08** Active scan progress/error storage is bounded and independent
  of total file count.
- [ ] **PERF-09** The previous generation remains available without waiting for
  scanner transactions or publication locks.
- [ ] **PERF-10** Catalog startup opens metadata/current generation without
  reading all rows.
- [ ] **PERF-11** Transfer history is paginated and never preloaded wholesale.
- [ ] **PERF-12** Peer callback paths never synchronously wait for persistence.
- [ ] **PERF-13** Queue and blacklist checks are O(1) average-time operations and
  do not scan all users or rules.
- [ ] **PERF-14** Full browse is served only from the generation's stored
  protocol-ready artifact; resolver work is O(1) apart from opening/wrapping the
  stream.
- [ ] **PERF-15** An unavailable/oversize browse is rejected before allocating a
  proportional object graph or byte array.
- [ ] **PERF-16** Repeated browse requests share immutable file data/page cache
  and do not create a retained whole-tree cache per request or peer.
- [ ] **PERF-17** The pre-serialized browse artifact is produced in a bounded
  one-pass pipeline and published/deleted with its catalog generation.
- [ ] **PERF-18** Browse stream permits, generation leases, retired-generation
  bytes, and long-held response streams have enforced bounds.
- [ ] **PERF-19** Upload stream creation performs one exact catalog lookup and one
  bounded set of filesystem validations; it does not rescan directories.
- [ ] **PERF-20** Scheduler enqueue, cancel, and next-selection do not traverse
  all queued uploads in normal operation.
- [ ] **PERF-21** Progress publication/coalescing scales with active upload count,
  not protocol callback frequency.
- [ ] **PERF-22** A saturated incoming request gate rejects quickly and does not
  create unbounded tasks.
- [ ] **PERF-23** Unlimited upload throughput is compared with the same pinned
  Soulseek.NET upload harness without Sockseek scheduling/governance overhead.
  A minimal loopback TCP sender/receiver with matching file and buffer sizes is
  also recorded as the host/network ceiling; direct file copy is diagnostic
  only, not the acceptance baseline.
- [ ] **PERF-24** The configured speed cap is measured over a sustained interval
  and does not materially overshoot the configured aggregate cap.
- [ ] **PERF-25** Disk-catalog qualification records cold and warm lookup/search
  behavior, catalog read bytes, and CPU use. The implementation may not add an
  in-memory mirror merely to hide an unindexed query or whole-catalog read.
- [ ] **PERF-26** Queue-position estimates are calculated only for bounded
  explicit requests with a hard work budget; deep estimates return unavailable
  and scheduler advancement never emits O(queue length) transfer deltas.
- [ ] **PERF-27** Daemon snapshot size and `DaemonClientStore` retention scale
  with active transfers, not waiting-queue length. Queued rows exist only in
  bounded live pages owned by the requesting view.
- [ ] **PERF-28** Waiting upload count does not create one Task or one
  Soulseek.NET upload operation per entry; such work scales with active slots.
- [ ] **PERF-29** Live-transfer paging uses a stable indexed admission-order key
  and hard page limit; it does not copy or sort the complete scheduler queue.
  Default continuation survives revision churn, while strict traversal detects
  and reports it.
- [ ] **PERF-30** Queue-summary daemon changes use bounded latest-value
  coalescing, so outbound delta rate scales with coalescer flush intervals rather
  than queue-mutation rate; ordered terminal active-transfer changes remain
  prompt.
- [ ] **PERF-31** Qualification records discovery/metadata/indexing, database
  finalization, artifact construction, hash/validation, and total
  scan-to-publication durations separately.
- [ ] **PERF-32** Scheduler indexes, duplicate keys, counters, queued transfer
  snapshots, and API-page construction have measured peak and retained memory at
  the 100,000-entry fixture; the hard item ceiling is not the only memory bound.

### 21.7 Numeric release budgets

The following are initial v4.0 release budgets on the pinned qualification host.
They may be changed only through a reviewed benchmark record explaining the
hardware, workload, regression risk, and replacement threshold.

- [ ] **BUDGET-01** Scanning both 100k-file physical fixture variants completes
  without OOM, process termination, or sustained queue saturation.
- [ ] **BUDGET-02** Scanner peak RSS increase is at most 512 MiB and retained
  managed-heap increase after completion and forced collection is at most
  128 MiB over the ready-daemon baseline.
- [ ] **BUDGET-03** On the 1M-row warm synthetic catalog, exact path lookup p95 is
  at most 25 ms and p99 at most 75 ms over at least 10,000 lookups.
- [ ] **BUDGET-04** On the same catalog, a 500-result search has p95 at most
  250 ms and p99 at most 750 ms over at least 1,000 representative searches.
- [ ] **BUDGET-05** Listing a 10,000-file directory has p95 at most 500 ms before
  network serialization and stays within the configured response bound.
- [ ] **BUDGET-06** Search under a concurrent full scan has no failures and p95
  latency no worse than 3x the ready-catalog warm baseline.
- [ ] **BUDGET-07** Publishing a completed generation pauses new catalog lease
  acquisition for no more than 250 ms p99; existing leases continue.
- [ ] **BUDGET-08** A 100k-file full browse succeeds repeatedly through
  `RawBrowseResponse` without increasing retained managed heap by more than
  128 MiB after ten sequential requests.
- [ ] **BUDGET-09** A 1M-file full browse succeeds through the bounded artifact
  stream with peak RSS increase at most 512 MiB, unless its encoded wire length
  exceeds a documented protocol limit; such a rejection occurs before more than
  64 MiB attributable request allocation.
- [ ] **BUDGET-10** With the 100k-entry scheduler fixture, enqueue/cancel/select
  operations have p99 at most 10 ms over at least 100,000 mixed operations.
- [ ] **BUDGET-11** With no speed cap, Sockseek localhost upload throughput
  reaches at least 90% of the pinned ungoverned Soulseek.NET upload harness for
  the same file of at least 1 GiB and matching buffer sizes. The minimal
  loopback-TCP ceiling is reported for context.
- [ ] **BUDGET-12** With a speed cap, aggregate bytes sent over a 60-second steady
  interval are between 90% and 110% of the configured limit, excluding a
  documented startup burst window of at most two seconds.
- [ ] **BUDGET-13** Blocking the historical persistence writer for 60 seconds
  causes no upload disconnect or callback stall; critical, ordinary, progress,
  and degraded buffers never exceed their configured capacities (defaults
  512/2,048/512/1,024), peak managed heap rises by at most 64 MiB, and retained
  managed heap after recovery and forced collection rises by at most 16 MiB over
  the matched healthy-writer run.
- [ ] **BUDGET-14** After a clean restart, opening and validating an existing
  1M-row disk catalog reaches catalog-ready state within 5 seconds on the pinned
  host, without scanning and with peak RSS increase at most 256 MiB.
- [ ] **BUDGET-15** During a full 100k-file generation build, total sharing-store
  disk usage remains within the documented bound for current, staging, rollback,
  SQLite temporary/WAL files, and any browse artifact; the measured worst case
  and cleanup behavior are included in the qualification report.
- [ ] **BUDGET-16** With the same ten active uploads, growing the waiting queue
  from 100 to 100,000 entries changes daemon snapshot JSON by at most 64 KiB and
  retained `DaemonClientStore` heap by at most 1 MiB. A 100-row live queue page
  has p95 latency at most 250 ms on the pinned host.
- [ ] **BUDGET-17** On the 100k-file physical fixture, metadata-light
  scan-to-index time is at most 10 minutes and at least 50% of its matched
  baseline throughput; metadata-heavy time is at most 30 minutes and at least
  35% of its matched baseline. Total scan-to-publication is at most 15 and
  45 minutes respectively.
- [ ] **BUDGET-18** For the 1M-row synthetic generation after row insertion,
  database finalization takes at most 10 minutes, streaming artifact
  construction at most 10 minutes, hash/structural validation at most 5 minutes,
  and total finalization-to-publication at most 20 minutes.
- [ ] **BUDGET-19** With 100,000 queued uploads across 1,000 users, scheduler and
  live-transfer runtime state increase peak managed heap by at most 256 MiB,
  retained managed heap by at most 192 MiB, and peak RSS by at most 384 MiB over
  the empty-scheduler baseline. Repeated 100-row page requests leave no retained
  page-sized growth.

The numeric budgets are not a promise that every user receives these latencies.
They are a reproducible regression gate and a proof that the architecture is not
pathologically expensive.

---

## 22. Completion and release acceptance criteria

This section is the hard definition of done for v4 sharing and uploads. The
named `PERF-*`/`BUDGET-*` requirements in section 21 and named requirements in
this section are authoritative. Phase stop conditions in section 20 and the
final questions in section 24 are navigation/evidence checklists: if wording
ever conflicts, the authoritative requirement wins. A semantic change must
update its authoritative requirement first and then every reference to its ID;
do not maintain independent paraphrases as separate contracts.

### 22.1 Global completion rule

The feature is complete only when all of the following are true:

1. Every mandatory checkbox in sections 20 through 24 is satisfied.
2. Every mandatory condition has automated evidence, or a documented manual
   verification where automation is genuinely impractical.
3. The full solution builds in Release configuration on supported platforms.
4. The complete automated test suite passes without ignored sharing/upload
   tests.
5. Real file-backed catalog tests prove restart survival and atomic publication.
6. Real-client tests prove search, browse, queue, resume, cancellation, and
   blacklist behavior.
7. The release qualification report satisfies all structural and numeric
   performance gates.
8. No known blocker or stop-work condition remains unresolved.
9. No completion claim relies on work described as “later,” “follow-up,” or
   “good enough for now” when that work is mandatory here.

The following are **not** sufficient stopping points:

- Creating the catalog schema and completing one scan.
- Returning search results without full browse and directory contents.
- Uploading a file without queue fairness, resume, cancellation, or durable
  history.
- Supporting explicit aliases while alias-less roots leak or fail.
- Implementing username blacklists but not exact IP blacklists.
- Passing only small in-memory or mocked filesystem tests.
- Having a disk catalog/file-backed browse response while artifact construction
  can still exhaust process memory.
- Adding server endpoints without reusable client/reducer support, remote CLI parity, discoverable actions, and pagination.
- Serving old catalogs during successful scans but not during failure,
  cancellation, corruption, or abrupt restart.
- Documenting performance as “bounded” without recorded release measurements.

### 22.2 Architecture and ownership

- [ ] **ARCH-01** Sharing and uploads remain operational with no active download
  job or `DownloadEngine` instance.
- [ ] **ARCH-02** One daemon-owned Soulseek session serves downloads, sharing, and
  uploads without independent disposal races.
- [ ] **ARCH-03** Catalog storage is separate from historical persistence and its
  one-writer queue.
- [ ] **ARCH-04** Resolver callbacks depend on immutable request records and
  domain interfaces, not API DTOs, EF entities, or mutable Soulseek transfers.
- [ ] **ARCH-05** Core/API transfer ownership supports daemon-scoped uploads with
  nullable `JobId` and `WorkflowId`.
- [ ] **ARCH-06** Active runtime state is authoritative; persistence remains an
  asynchronous projection.
- [ ] **ARCH-07** v4.0 has one disk-backed generation implementation and no
  memory-storage option, backup/restore mode, or mode-dependent semantics.

### 22.3 Configuration, aliases, and access policy

- [ ] **CFG-01** Explicit `[Alias]path` and alias-less `path` forms are supported.
- [ ] **CFG-02** Alias-less roots derive only the normalized final directory name.
- [ ] **CFG-03** Peers and ordinary remote API clients never receive an absolute
  root or ancestor path.
- [ ] **CFG-04** Duplicate explicit/derived aliases, overlapping roots, invalid
  exclusions, volume roots, invalid/timeout-prone regex behavior, remote path
  collisions, and unsafe bounds fail fast or fail staging publication as
  specified.
- [ ] **CFG-05** Every new list option uses unprefixed replacement and explicit
  `+ ` append semantics consistent with existing `on-complete`/`regex` behavior.
  No comma-separated or alternate list grammar is accepted by this feature;
  legacy collection options remain unchanged.
- [ ] **CFG-06** `peer-blocked-user` and `peer-blocked-ip` parse,
  normalize, and enforce exact username and exact IPv4/IPv6 entries without
  being confused with the per-download `banned-users` source filter.
- [ ] **CFG-07** CIDR, country, ASN, hostname, managed-list, and username regex
  behavior is neither accepted accidentally nor claimed.
- [ ] **CFG-08** Manual share-count settings and their CLI options are removed;
  all advertised counts come from the published catalog.
- [ ] **CFG-09** Defaults match the settled table in section 6.1, including ten
  upload slots, unlimited speed, CPU-count scan workers, and no periodic rescan.
- [ ] **CFG-10** The fixed Windows hidden/system and Unix dot-entry policies are
  enforced recursively, explicitly configured hidden roots remain eligible, and
  the release does not expose an undocumented include-hidden switch.

### 22.4 Catalog and scan correctness

- [ ] **CAT-01** Search, browse, directory listing, and upload resolution use one
  immutable published generation per request lease.
- [ ] **CAT-02** A peer never observes a partially built generation.
- [ ] **CAT-03** Failed and cancelled scans retain the prior catalog and counts.
- [ ] **CAT-04** Abrupt termination during scan/publication recovers to a complete
  old or new catalog-plus-artifact generation.
- [ ] **CAT-05** Explicit empty directories are preserved.
- [ ] **CAT-06** Exact lookup, FTS search, directory listing, counts, and settings
  hash validation are covered by file-backed integration tests.
- [ ] **CAT-07** Catalog corruption has a documented fallback/rebuild path and
  never silently traverses the live filesystem for a peer request.
- [ ] **CAT-08** Scanner errors are bounded, observable, and classified without
  aborting for expected per-entry failures.
- [ ] **CAT-09** Optional metadata reuse/incremental behavior, if implemented, is
  demonstrably equivalent to a full build and has a full-reconciliation path.
- [ ] **CAT-10** The browse artifact is built with bounded memory, validated
  against its catalog/wire version, and Sockseek-owned stream resources release
  on exact EOF, disposal, and the self-expiring deadline. A stalled network write
  is separately bounded by the verified library connection timeout.
- [ ] **CAT-11** Startup removes only recognized orphan/staging files, catalog
  writes respect the documented free-space reserve, and a disk-full staging
  failure leaves the published generation usable.
- [ ] **CAT-12** `RemotePathKey` and its recorded algorithm version define
  alias/path identity consistently across scan collision detection, SQLite
  uniqueness, exact resolution, and duplicate admission; a key-version mismatch
  requires a rebuild.
- [ ] **CAT-13** Every filesystem/mount for which uploads are enabled has a
  published support-matrix row and passing safe-open, final-target,
  stable-identity, replacement-race, and attribute-policy evidence.

### 22.5 Protocol serving and blacklist behavior

- [ ] **SERVE-01** Search responses are bounded, indexed, escaped, and include a
  coherent upload-capacity snapshot.
- [ ] **SERVE-02** Full browse includes required directory entries and has a safe
  measured streaming, concurrency, lifetime, and size bound.
- [ ] **SERVE-03** Directory requests use exact indexed lookup and do not probe
  arbitrary host paths.
- [ ] **SERVE-04** Blacklisted usernames receive no search, browse, directory, or
  new-upload service.
- [ ] **SERVE-05** Blacklisted exact IPs receive the same denial wherever an
  endpoint is available.
- [ ] **SERVE-06** Denials do not disclose which blacklist or internal condition
  matched.
- [ ] **SERVE-07** Unconfigured, initializing, stale, scanning, and unavailable
  sharing states have deterministic protocol and health behavior.
- [ ] **SERVE-08** Request exclusions and current Soulseek-server excluded
  phrases are absent from every outgoing search response.
- [ ] **SERVE-09** Excluded-phrase state has hard bounds and explicit readiness;
  invalid, oversized, or not-yet-authoritative state fails search closed without
  disabling unrelated peer services.

### 22.6 Upload correctness and fairness

- [ ] **UP-01** Every accepted upload has one logical transfer and zero or one
  attempt with stable identity. The attempt is created only when the
  scheduler-dispatched Soulseek.NET operation begins.
- [ ] **UP-02** Strict user-round-robin fairness and FIFO-within-user ordering are
  deterministic under concurrency, with at most one active upload per username.
- [ ] **UP-03** A duplicate nonterminal request completes the void
  `EnqueueDownload` callback successfully, creates no transfer/attempt/counter,
  and is represented to the peer through the existing entry returned by
  `PlaceInQueueResolver`.
- [ ] **UP-04** Configured file/MiB limits and the global scheduler safety
  ceiling are checked atomically with admission.
- [ ] **UP-05** Slots cannot be leaked or oversubscribed.
- [ ] **UP-06** Resume at zero, middle, and EOF is correct; invalid offsets fail
  safely.
- [ ] **UP-07** File identity, containment, link policy, size, and offset are
  revalidated on the opened handle before any byte is exposed.
- [ ] **UP-08** Cancellation and disconnect produce exactly one terminal outcome
  and free all accounting.
- [ ] **UP-09** The global speed limit is enforced or the daemon refuses to claim
  it is configured.
- [ ] **UP-10** Active and queued transfers are not automatically requeued after
  restart.
- [ ] **UP-11** A queued transfer re-resolves against the current generation and
  cannot serve a removed or substituted file after publication.

### 22.7 Live state, persistence, API, clients, and CLI

- [ ] **STATE-01** The existing revisioned `DaemonStateDto` contains catalog,
  scan, queue, slot, listener, health, and upload-admission state.
- [ ] **STATE-02** No separate top-level sharing/upload singleton revision or
  reducer exists.
- [ ] **STATE-03** Daemon snapshots/deltas contain active uploads plus the
  queue summary/revision, while bounded live pages contain queued uploads;
  workflow streams exclude daemon-scoped uploads.
- [ ] **STATE-04** Transfer identity, status, scheduling, and progress components
  update independently with bounded latest-value coalescing where permitted.
- [ ] **STATE-05** Terminal upload state is observable before active-state
  removal, and history remains queryable.
- [ ] **STATE-06** Startup reconciles formerly active uploads/attempts to
  `Interrupted` exactly once.
- [ ] **STATE-07** Snapshot reload, daemon restart/epoch change, duplicate/stale
  deltas, and sequence-gap recovery converge in `DaemonClientStore`.
- [ ] **STATE-08** Sharing, scan, upload, and new diagnostic lifecycle fields use
  documented string-serialized enums or stable reason codes rather than new
  ad-hoc state strings.
- [ ] **STATE-09** Queue-summary daemon replacements are latest-value
  coalescible and do not emit one outbound delta per queue mutation; terminal
  replicated transfer changes remain ordered and promptly observable.
- [ ] **ACTION-01** Running scans and cancellable transfers expose correct
  `AvailableActions`; terminal/non-cancellable resources do not.
- [ ] **ACTION-02** CLI and test GUI/view-model consume advertised actions rather
  than maintaining their own lifecycle-to-command table.
- [ ] **API-01** Sharing status and scan control are exposed through bounded HTTP
  resources and explicit POST action routes.
- [ ] **API-02** Existing transfer detail/history resources support uploads,
  direction filters, pagination, and cancellation without a duplicate upload API.
- [ ] **API-03** Starting an already active scan returns a typed
  `AlreadyRunning` result and the existing scan resource.
- [ ] **API-04** HTTP statuses and stable machine-readable error codes cover
  malformed, unknown, non-cancellable, and unavailable cases; clients never
  parse human messages for control flow.
- [ ] **API-05** Ordinary API/SignalR payloads contain no absolute local paths or
  blacklist entries.
- [ ] **API-06** `GET /api/sharing` and live `DaemonStateDto` use the same public
  state semantics and cannot disagree because of separate computation.
- [ ] **API-07** `SockseekApiJsonContext`, OpenAPI, JSON round-trip fixtures, and
  the explicit `LiveProtocol.Version` decision cover every contract change.
- [ ] **API-08** A selected queued transfer can return a revisioned,
  point-in-time queue-position estimate or typed work-budget-unavailable result
  without requiring queue-wide live-state churn or promising an estimated start
  time.
- [ ] **API-09** Transfer detail is live-first and remains available before
  persistence commit; historical detail is the fallback after live removal.
- [ ] **API-10** Every scan/transfer mutation crosses the named operator
  authorization policy and returns stable unauthorized/forbidden behavior.
- [ ] **API-11** The generic live-transfer collection pages queued uploads with a
  hard limit and stable admission key without historical persistence. Default
  paging continues during churn and reports origin/observed revisions; explicit
  strict mode returns `QueueRevisionChanged` on mutation.
- [ ] **CLIENT-01** `SockseekApiClient`, `SockseekLiveClient`, and
  `DaemonClientStore` expose every operation/state required by CLI and a future
  operational GUI.
- [ ] **CLIENT-02** Queued live transfers, historical transfers, and attempts
  remain explicitly paged and are not retained wholesale by
  `DaemonClientStore`.
- [ ] **CLIENT-03** A thin GUI/view-model can display state, recover connections,
  page live/history transfers, and execute advertised commands without direct
  SignalR/JSON handling or a second reducer.
- [ ] **CLI-01** `sockseek share` works against a daemon through `--remote` using
  the reusable public clients.
- [ ] **CLI-02** `sockseek transfers` and transfer cancellation require a running
  daemon target and work through `--remote`.
- [ ] **CLI-03** CLI errors distinguish unavailable daemon, unconfigured
  sharing, active scan reuse, unknown transfer, and non-cancellable history by
  typed result/status/code.

### 22.8 Security, reliability, and operations

- [ ] **SEC-01** Path traversal, separator confusion, malformed Unicode, remote
  collisions, link escapes, handle replacement, and case behavior have
  cross-platform tests.
- [ ] **SEC-02** Incoming request gates, response sizes, queue sizes, and error
  samples are bounded.
- [ ] **SEC-03** Regex has finite execution time, and FTS inputs are treated as
  bounded data that cannot create an unbounded expression or query.
- [ ] **SEC-04** Catalog, artifact, manifest, and staging files are created with
  daemon-owner-only permissions/ACLs and retain them across publication.
- [ ] **REL-01** Persistence failure cannot block or cancel active peer transfers.
- [ ] **REL-02** Graceful shutdown stops admission, reconciles accepted work, and
  does not leave false active state.
- [ ] **REL-03** Listener/session reconnect republishes counts and restores
  resolvers without duplicating handlers.
- [ ] **OPS-01** Catalog health distinguishes unconfigured, initializing, ready,
  stale/degraded, unavailable, and stopping while scan operation state remains
  orthogonal; upload admission is exposed separately.
- [ ] **OPS-02** Metrics cover catalog size, scan timing/errors, dropped requests,
  active/queued uploads, queue wait, throughput, rejection reasons, and listener
  health without high-cardinality labels.
- [ ] **OPS-03** Docker/read-only mounts, permissions, listener/port forwarding,
  catalog location, rescan behavior, aliases, blacklists, and performance limits
  are documented.
- [ ] **OPS-04** Operators can cancel a scan and upload and can diagnose stale or
  unavailable catalogs without inspecting the database manually.

### 22.9 Performance release condition

- [ ] **PERF-GATE** Every mandatory `PERF-*` and `BUDGET-*` item in section 21
  passes on the pinned qualification host, and the report is attached to the
  release or pull request used for final approval.

---

## 23. Stop-work conditions

Implementation MUST pause and resolve the issue before proceeding if any of the
following occurs:

1. A peer-supplied remote path is used to construct or probe a host filesystem
   path without an exact catalog lookup.
2. Alias-less configuration exposes any ancestor or absolute path component.
3. A scan or browse-artifact implementation retains all file/directory records
   or the complete serialized payload in memory.
4. A partially built or failed catalog can become current.
5. Publication requires stopping existing catalog readers for an unbounded time.
6. Resolver/upload callbacks synchronously wait for persistence, a full scan, or
   a free upload slot.
7. Incoming request, scan, queue, progress, or diagnostic growth is unbounded.
8. A blacklisted request can create a transfer before access policy is checked.
9. Exact-IP policy cannot be enforced in a path where the documentation claims
   it is enforced, and the gap is not made explicit.
10. CIDR, hostname, regex, or country input is accidentally accepted as an exact
    blacklist entry.
11. File replacement, symlink, reparse point, or case behavior can escape a root
    after catalog publication.
12. The same accepted upload can finish with zero or more than one terminal
    outcome, or slot/queue accounting can leak.
13. An invalid resume offset can expose unrelated bytes or cause an unchecked
    seek/read.
14. A browse request can cause OOM before the size guard runs.
15. A history/status endpoint or CLI operation loads all catalog or transfer
    history into memory.
16. `sockseek transfers` starts a temporary local engine instead of querying a
    daemon.
17. Persistence failure stalls active uploads or causes unbounded buffering.
18. Tests involving SQLite locking, filesystem links, restart, publication,
    upload resume, or real protocol behavior pass only with mocks/in-memory
    substitutes.
19. Required tests are skipped, flaky, timing-dependent without deterministic
    synchronization, or absent from CI/release qualification.
20. A numeric performance gate is weakened without a reviewed benchmark record.
21. Sharing/upload runtime state is added as a parallel singleton replication
    protocol instead of extending the existing revisioned daemon component,
    without measured evidence that daemon replacement is inadequate.
22. CLI or GUI code parses raw SignalR/JSON independently, hard-codes lifecycle
    action eligibility, or parses human error text instead of using the shared
    clients, reducer, `AvailableActions`, statuses, and stable codes.
23. A live-contract change ships without source-generated JSON coverage, updated
    OpenAPI, reducer recovery tests, and an explicit `LiveProtocol.Version`
    compatibility decision.
24. The implementation adds share-tree watching while relying on watcher events
    as authoritative and without a full reconciliation design.
25. Documentation and implementation disagree about path privacy, blacklist
    scope, daemon/remote semantics, defaults, or supported browse size.
26. Manual shared file/folder counts remain configurable after catalog-derived
    counts are implemented.
27. A memory catalog mode is added without a separate design and the full
    disk-versus-memory qualification matrix described in section 8.2.
28. A browse response is built through `BrowseResponse`/`ToByteArray()` instead
    of the validated `RawBrowseResponse` generation artifact.
29. A regular expression can execute without a finite engine timeout, or a
    timeout is treated as a non-match and can publish the affected path.
30. A queued upload retains a catalog generation lease, survives removal from
    the current generation, or is dispatched into Soulseek.NET before Sockseek's
    scheduler grants it.
31. More than one upload per normalized username is counted active by Sockseek,
    allowing work to wait behind Soulseek.NET's private per-user semaphore.
32. A scan/transfer mutation endpoint bypasses the named operator authorization
    boundary, or non-loopback unauthenticated control is presented as secure.
33. Soulseek-server excluded phrases can appear in an outgoing search response.
34. The complete waiting upload queue is serialized into the daemon snapshot,
    retained by `DaemonClientStore`, or sorted/copied to produce one live page.
35. An excluded-phrase set is silently truncated/cleared, or search results are
    served before phrase state is authoritative for a fresh process.
36. A failed/stalled raw browse write can retain Sockseek-owned stream resources,
    a generation lease, browse permit, or retired artifact beyond the documented
    self-expiration deadline.

---

## 24. Final release gate

The sharing/upload implementation may stop and be released as complete only when
a reviewer can answer **yes** to every question below:

- [ ] Can an operator configure both `/absolute/path` and
  `[Alias]/absolute/path`, with no local-path disclosure?
- [ ] Can search, browse, directory contents, and upload resolution continue from
  generation N while N+1 builds, fails, cancels, or publishes?
- [ ] Can no peer request escape a configured root before or after file changes?
- [ ] Can case/Unicode/separator collisions never make two local entries share one
  remote lookup key?
- [ ] Can exact usernames and exact IPv4/IPv6 addresses be denied consistently
  without claiming CIDR/regex/country support?
- [ ] Can a blacklisted peer be rejected before expensive browse/catalog/upload
  work and before transfer registration?
- [ ] Is full browse built once through a bounded streaming serializer, served
  through a lease-owning `RawBrowseResponse`, and rejected safely when the
  artifact is unavailable/oversize?
- [ ] Can the disk-backed catalog meet cold-start and warm-query budgets without
  an in-memory mirror or a selectable storage mode?
- [ ] Can one user enqueue many files without starving other users?
- [ ] Can no user consume more than one active slot or create work hidden behind
  Soulseek.NET's private queue?
- [ ] Are the settled defaults from section 6.1 implemented exactly, and are all
  manual share-count settings and CLI options gone?
- [ ] Can every accepted upload be resumed, cancelled, observed live, and receive
  exactly one authoritative runtime terminal outcome, with committed history
  complete while persistence is healthy and explicit incomplete-history health
  during a prolonged projection outage?
- [ ] Can a rescan remove/replace a queued path without that request serving stale
  or substituted bytes?
- [ ] Can the historical persistence writer be unavailable without stalling peer
  service or growing memory without bound?
- [ ] Can catalog and upload history survive restart and reconcile interrupted
  work safely?
- [ ] Can transfer history be paged without loading it all into memory?
- [ ] Can a client reconstruct bounded operational sharing/upload state from one
  daemon snapshot plus ordered deltas, then page the live waiting queue without
  retaining all of it in the shared reducer?
- [ ] Are scan and transfer commands discoverable through `AvailableActions`,
  with no GUI-specific lifecycle inference?
- [ ] Can a GUI request a selected transfer's queue estimate without every
  scheduler advancement producing O(queue length) deltas?
- [ ] Do typed scan results, stable API error codes, OpenAPI, source-generated
  JSON, and the live-protocol version decision agree?
- [ ] Can a thin GUI/view-model reconnect, recover gaps, page live/history
  transfers, and cancel eligible resources without direct HTTP/SignalR parsing
  or a second state model?
- [ ] Can `sockseek share` and `sockseek transfers` operate through `--remote`,
  with transfer commands refusing meaningless standalone execution?
- [ ] Do all mutation commands cross the operator authorization policy, with the
  current loopback-only trust limitation documented until v4 authentication is
  complete?
- [ ] Can operators see scan, catalog, listener, queue, slot, speed, and degraded
  health without seeing private roots or blacklist entries?
- [ ] Do the 100k physical, 1M synthetic, 100k queue, and 100k history fixtures
  satisfy every structural and numeric performance gate?
- [ ] Are request exclusions and the current Soulseek-server excluded phrases
  absent from outgoing search responses?
- [ ] Do Release builds and all unit, integration, concurrency, restart,
  corruption, cross-platform, real-client, API, CLI, and load tests pass?
- [ ] Does documentation accurately state aliases, privacy, blacklists, scan
  freshness, unsupported features, performance limits, and failure behavior?

If any answer is **no**, sharing/uploads are not finished.

---

## 25. Follow-up roadmap

After the baseline is operating in real installations, consider features in this
order:

1. Prior-generation metadata reuse, if scans show metadata I/O is the dominant
   cost.
2. Incremental one-root or changed-subtree scans with full reconciliation.
3. Paged Web UI inspection of our published peer-view catalog.
4. Authenticated local-admin sharing configuration.
5. Per-user overrides.
6. User groups and priority/slot/speed policy.
7. Daily/weekly quotas.
8. Username-pattern, CIDR/range, country, and managed blacklist features as a
   separately designed moderation feature.
9. Share-tree watcher support only with overflow/reconciliation semantics.
10. Relay/multi-host sharing only as a separately designed feature.

Do not add group/relay policy before measurements show the baseline catalog,
scheduler, access policy, and transfer contracts are stable.

---

## 26. Open decisions to close during Phase 0

Source inspection closed several earlier uncertainties: resolver signatures do
not carry cancellation tokens; browse can be served through
`RawBrowseResponse`; per-user upload concurrency is fixed at one; and maximum
upload speed is patchable at runtime. The remaining Phase 0 decisions require
measurement or platform-specific proof:

1. The internal request deadlines and exact saturation/empty-response mapping
   for callbacks that have no cancellation token.
2. Username canonicalization/comparison, proven against server casing and at
   least two clients, then used identically by deny sets, duplicate detection,
   per-user gates, policy counters, and the scheduler.
3. The endpoint-cache freshness rule and bounded endpoint-resolution behavior
   used by incoming search when exact IP policy is configured.
4. The Windows and Unix handle-based safe-open implementations and exact stable
   file identity stored in the catalog.
5. The streaming browse serializer format version and validation strategy,
   proven compatible with `RawBrowseResponse` and at least two real clients.
6. Whether the library's aggregate upload-speed option satisfies `BUDGET-12`;
   otherwise the precise shared `TransferOptions.Governor` implementation.
7. The observed callback semantics for immediate versus queued uploads,
   place-in-queue responses, resume offsets, disconnects, and cancellation with
   at least two independent clients.
8. The minimum free-space reserve/check cadence for staging catalog, SQLite
   temporary/WAL data, and browse-artifact writes, demonstrated against the
   qualification fixtures and documented for operators.
9. Excluded-phrase event ordering and empty-set semantics on fresh login and
   reconnect, including the exact fail-closed search-readiness transition.
10. The browse stream idle deadline/self-expiration mechanics, unless a pinned
    Soulseek.NET upgrade proves raw stream disposal in a `finally` on every write
    outcome; separately, the connection-timeout bound for a write already
    stalled after its last read.
11. The measured scan and artifact-build timing ceilings and the completed
    OS/filesystem/container support matrix required by `P0-07`, `P0-11`,
    `BUDGET-17`, and `BUDGET-18`.

The following are **not** Phase 0 decisions: storage is disk-only; upload slots
default to 10; upload speed and per-user queue policy limits default to unlimited;
scan workers default to `Environment.ProcessorCount`; incoming search defaults
are 10/500/500; periodic rescan defaults off; remote-path identity is
`RemotePathKey` v1; symlink following is disabled; hidden/system behavior is the
fixed section 9.3 policy; full browse uses a streaming generation artifact; one
active upload per username is enforced; list syntax uses unprefixed replace plus
explicit `+ ` append; and manual share-count options are removed.

Each remaining decision must produce a small recorded decision, source or
benchmark evidence, and a contract test where applicable.

## 27. Source index

### Sockseek

- [v4 branch](https://github.com/fiso64/sockseek/tree/v4)
- [v4 TODO](https://raw.githubusercontent.com/fiso64/sockseek/refs/heads/v4/TODO.md)
- [Pull request #193](https://github.com/fiso64/sockseek/pull/193)
- [Pull request #194](https://github.com/fiso64/sockseek/pull/194)
- [API improvements discussion](https://raw.githubusercontent.com/fiso64/sockseek/refs/heads/v4/docs/temp/API-IMPROVEMENTS-DISCUSSION.md)
- [Persistence discussion](https://raw.githubusercontent.com/fiso64/sockseek/7e5a071f1ed7038661fbced6892e197ee3f69933/docs/temp/PERSISTENCE-DISCUSSION.md)

Relevant code at the reviewed commit:

- `Sockseek.Core/Events/CoreChanges.cs`
- `Sockseek.Core/Snapshots/CoreSnapshots.cs`
- `Sockseek.Core/Soulseek/SoulseekClientManager.cs`
- `Sockseek.Core/Settings/EngineSettings.cs`
- `Sockseek.Persistence/Entities/PersistenceEntities.cs`
- `Sockseek.Persistence/Write/PersistenceMutations.cs`
- `Sockseek.Server/EngineSupervisor.cs`
- `Sockseek.Server/EngineStateStore.cs`
- `Sockseek.Server/ServerOptions.cs`
- `Sockseek.Api/Contracts/LiveState.cs`

### slskd implementation and configuration

- [Configuration documentation](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/docs/config.md)
- [`Options.cs` defaults](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Core/Options.cs)
- [`Application.cs` resolver wiring and browse-cache build](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Application.cs)
- [`ShareService`](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Shares/ShareService.cs)
- [`ShareRepositoryFactory`](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Shares/ShareRepositoryFactory.cs)
- [`ShareScanner`](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Shares/ShareScanner.cs)
- [`SqliteShareRepository`](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Shares/SqliteShareRepository.cs)
- [`UploadService`](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Transfers/Uploads/UploadService.cs)
- [`UploadQueue`](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Transfers/Uploads/UploadQueue.cs)
- [`UploadGovernor`](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Transfers/Uploads/UploadGovernor.cs)

The links are pinned to the exact source reviewed on 2026-07-29 so later
upstream changes cannot silently alter the evidence for this design.

### slskd issues and discussions

- [#610 — OOM scanning an exceptionally large share](https://github.com/slskd/slskd/issues/610)
- [#1765 — browse response/cache OOM with a multi-million-file library](https://github.com/slskd/slskd/issues/1765)
- [#1593 — memory cache/browse warming OOM report](https://github.com/slskd/slskd/issues/1593)
- [#1291 — startup OOM with transfer history](https://github.com/slskd/slskd/issues/1291)
- [#443 — share scan progress and errors](https://github.com/slskd/slskd/issues/443)
- [#1050 — excessive watcher use in configuration/file-provider behavior](https://github.com/slskd/slskd/issues/1050)
- [#1772 — live/realtime share updating request](https://github.com/slskd/slskd/issues/1772)
- [#1773 — manual rescan of one shared directory request](https://github.com/slskd/slskd/issues/1773)
- [#127 — upload/download speed and slot limits](https://github.com/slskd/slskd/issues/127)
- [#346 — upload slot and bandwidth limiting work](https://github.com/slskd/slskd/pull/346)
- [Discussion #1543 — upload/listener configuration troubleshooting](https://github.com/slskd/slskd/discussions/1543)
- [Discussion #647 — locked folders and policy alternatives](https://github.com/slskd/slskd/discussions/647)

### Soulseek / Soulseek.NET

- [Soulseek.NET README and excluded-phrase requirement](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/README.md#excluded-search-phrases)
- [`SoulseekClientOptions` callback and concurrency contracts](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Options/SoulseekClientOptions.cs)
- [`SoulseekClientOptionsPatch` live-patch surface](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Options/SoulseekClientOptionsPatch.cs)
- [`RawBrowseResponse`](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/RawBrowseResponse.cs)
- [`PeerMessageHandler` raw-response streaming](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Messaging/Handlers/PeerMessageHandler.cs)
- [`SoulseekClient` upload semaphore ordering](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/SoulseekClient.cs)
- [`TransferOptions` slot/governor hooks](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Options/TransferOptions.cs)
- [Soulseek package 10.0.2](https://www.nuget.org/packages/Soulseek/10.0.2)
- [Soulseek FAQ — listening port](https://slsknet.org/news/faq-page)

---

## Bottom line

Sockseek should copy slskd's **separation of catalog, scheduler, governor, and
history**, but not its full policy surface.

The highest-risk design mistake would be to add resolver callbacks directly to
`DownloadEngine`, scan into a mutable in-memory dictionary, and persist every
upload callback synchronously. That would work for a demo and create avoidable
coupling, memory, and recovery problems.

The proposed first release instead gives Sockseek a daemon-owned, disk-backed,
atomically published share catalog with a bounded wire-artifact path, plus a
small fair upload runtime whose large waiting queue is paged rather than
replicated wholesale. It fits v4's existing immutable change, live-state, and
persistence boundaries, while leaving advanced slskd-style policy as an optional
layer rather than a prerequisite for basic sharing.
