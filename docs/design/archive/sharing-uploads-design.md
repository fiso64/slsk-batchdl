# Sockseek v4 sharing and uploads

**Status:** Implementation complete; interoperability and release-performance
qualification pending

**Target:** v4.0

**Last design correction:** 2026-08-06

**Sockseek baseline after branch rebase:**
`f10a61457cfec08d8af8f3766c3f899380ed2dce`

**slskd source reviewed:** `e42a525d700d6dc343f316447803138b8ea2fbe3`

**Soulseek.NET source reviewed:**
`52fc3e4267114d8cd9492cb4d7438b3eca0267bf` (package 10.0.2)

This is the authoritative design for Sockseek's first public sharing and upload
release. It describes the implementation after the self-hosting and
maintainability correction preserved in
[`sharing-uploads-design.correction.md`](sharing-uploads-design.correction.md).
That file is historical context, not an additional specification.

Sockseek targets self-hosted and homeserver use. Remote Soulseek peers and their
input are untrusted. The operator, daemon account, configured roots, mounted
storage, and normal local administration are inside the product trust boundary.
The design protects paths, privacy, protocol interoperability, and bounded
resources without trying to defend against a hostile local administrator or
blacklisting ordinary storage stacks.

Normative terms such as **MUST**, **SHOULD**, and **MAY** use their RFC 2119
meanings.

---

## 1. Decision rules

These rules control later sharing work as well as v4 maintenance:

1. Prefer ordinary .NET and Soulseek.NET behavior. Add a workaround only for a
   demonstrated defect, keep it narrow, and give it a deletion condition.
2. Validate and bound peer input. Do not treat every local mount or filesystem
   as hostile.
3. Fail one entry or request when possible. Reject a root or catalog only when
   continuing could escape its public namespace or publish an inconsistent
   generation.
4. Expose configuration for meaningful operator policy, not internal worker,
   buffer, and response constants.
5. Preserve one understandable behavior. Do not add strict/relaxed modes,
   capability matrices, or fallback ladders without observed need.
6. A release gate MUST protect interoperability, privacy, bounded resource use,
   or an observed regression. Measurements establish numeric thresholds;
   invented thresholds do not prove correctness.
7. Rebuild derived data instead of creating migration and durability machinery
   appropriate for irreplaceable data.

## 2. Scope

### 2.1 Included in v4

- One daemon-lifetime Soulseek session shared by downloads, sharing, uploads,
  and later chat and user-browse features.
- One or more absolute share roots, each with an effective public alias.
- Explicit directory exclusions and case-insensitive regex filters.
- Manual, startup, and optional periodic scans with progress and cancellation.
- A rebuildable disk-backed catalog and streaming full-browse artifact.
- Search, full browse, directory listing, user information, queue placement,
  resumable uploads, cancellation, and exact peer denial.
- Ten upload slots by default, an optional aggregate speed cap, FIFO within a
  user, and round-robin fairness between users.
- Live upload state, paged live queue, durable upload history, and one shared
  API/client transfer model for uploads and downloads.
- Remote CLI commands for status, scans, transfer history, and cancellation.

### 2.2 Deferred

- Private, buddy, group, locked, or paid shares.
- Per-user priorities, custom slot classes, quotas, and scheduled policy.
- CIDR, country, ASN, DNS, regex, or managed peer blocklists.
- Filesystem watchers and continuous catalog mutation.
- Runtime APIs that reveal or mutate local share roots.
- A selectable in-memory catalog mode.
- Upload retry policy. A later peer request creates a new transfer.
- A transactionally consistent live-queue traversal or start-time forecast.

The architecture leaves room for later visibility policy by keeping public-path
resolution and peer authorization outside protocol callbacks. v4 does not add a
pervasive speculative `ShareView` abstraction before those requirements exist.

## 3. Architecture

### 3.1 Ownership

Sharing is daemon infrastructure, not a `DownloadEngine` feature:

```text
EngineSupervisor / SharingRuntime
  ├─ SoulseekClientManager       one daemon-owned network session
  ├─ ShareScanCoordinator        one staging scan at a time
  │   ├─ ShareScanner            bounded discovery and metadata pipeline
  │   └─ ShareCatalogManager     generation publication and leases
  ├─ SoulseekSharingAdapter      bounded protocol callback boundary
  ├─ UploadCoordinator           accepted transfer lifecycle
  │   └─ UploadScheduler         bounded fair queue and slot grants
  └─ EngineStateStore            shared live transfer projection

ShareCatalogManager
  ├─ share-index-{generation}.sqlite3
  ├─ browse-{generation}.bin
  └─ current.json                current plus one fallback generation

PersistenceRuntime
  └─ sockseek.db                 durable jobs and transfer history
```

The catalog database is not the historical database. A peer search or upload
MUST NOT wait on the history persistence writer. Upload history is an
asynchronous projection of coordinator state.

### 3.2 Layering

- Core owns settings, public-path identity, access policy, scanning contracts,
  callback adaptation, scheduling, and upload lifecycle.
- Persistence owns the SQLite catalog, immutable generation manager, and
  durable transfer history projection.
- Server owns daemon lifetime, HTTP/SignalR projection, actions, and operator
  authorization seam.
- API owns shared DTOs and reusable HTTP/live clients.
- CLI consumes those clients; it does not implement a second sharing state
  machine.

Core MUST NOT reference Server, CLI, or the historical EF model. Protocol
callbacks MUST NOT query the local filesystem by a peer-supplied path.

### 3.3 Why the remaining complexity exists

The following mechanisms have demonstrated value and stay:

- exact catalog lookup prevents peer-driven filesystem probing and local-path
  disclosure;
- staging plus atomic generation publication prevents partial catalogs;
- a file-backed browse artifact avoids a library-sized object graph or byte
  array;
- hard request, response, queue, and diagnostic bounds prevent untrusted peers
  from creating unbounded work;
- scheduler grants occur before `SoulseekClient.UploadAsync`, preventing hidden
  queues inside Soulseek.NET;
- queued paths are re-resolved against the current generation at dispatch;
- exact-length streams make resume offsets and truncation failures explicit;
- terminal state and slot release have one idempotent owner;
- owner-only artifacts protect the local paths stored in the catalog; and
- bounded live state plus paged history supports the v4 Web UI without loading
  all transfers into every client.

## 4. Configuration

### 4.1 Public settings

The supported operator surface is intentionally small:

```csharp
sealed class SharingSettings
{
    List<ShareRootSettings> Roots;
    List<string> ExcludedDirectories;
    List<string> Filters;
    bool ScanOnStart = true;
    TimeSpan? RescanInterval;
}

sealed class UploadSettings
{
    int Slots = 10;
    int? SpeedLimitKiBPerSecond; // null = unlimited
}

sealed class PeerAccessSettings
{
    List<string> BlockedUsernames;
    List<string> BlockedIpAddresses;
}
```

Scan workers, callback concurrency, callback queue capacity, search result
limits, and per-user admission bounds are safe internal constants. They are not
CLI/config compatibility commitments. A tuning option MAY be added later when
field evidence identifies a real need.

The scheduler has a hard 100,000 queued-item ceiling and an internal 1,000
outstanding-item ceiling per normalized username. Both reject with the same
actionable capacity category; byte-based per-user policy is deferred.

### 4.2 Root syntax

Both forms are accepted:

```ini
share = /srv/music
share = + [Archive]/mnt/archive/audio
share-exclude = + /srv/music/private
share-filter = + \.(part|tmp)$

share-scan-on-start = true
# share-rescan-interval = 6h

upload-slots = 10
# upload-speed-limit-kib = 2048

peer-blocked-user = + unwanted-user
peer-blocked-ip = + 192.0.2.10
```

`[Alias]path` supplies the public alias. An alias-less root derives it from the
last directory name. A volume root such as `/` or `C:\` requires an explicit
alias because no safe name can be derived.

An unprefixed list entry replaces values from the previous config layer; `+ `
appends. Named and automatic download profiles cannot contain daemon-lifetime
sharing settings.

### 4.3 Validation

- Local roots and exclusions MUST be absolute after variable expansion.
- Aliases MUST be non-empty, well-formed Unicode, separator-free, non-control,
  and not `.` or `..`.
- Effective aliases MUST be unique under `RemotePathKey` comparison.
- Local roots MAY overlap or expose the same directory under distinct aliases.
- An exclusion MUST be below at least one configured root and MUST NOT equal a
  root. An overlapping physical exclusion applies wherever that path appears.
- Existing roots MUST be real directories, not links or reparse points.
  Missing roots fail a scan, not daemon configuration parsing.
- Regexes use invariant case-insensitive matching and a finite timeout.
- Upload slots and speed limits MUST be positive and overflow-safe.
- Peer names and IPs are exact values. Usernames use the scheduler's normalized
  identity; IPs use parsed canonical address identity.
- Public strings and collections have encoded-size and count limits.

## 5. Public paths and filesystems

### 5.1 Remote identity

A peer sees only:

```text
<effective alias>\<relative path using Soulseek separators>
```

The catalog stores display spelling separately from `RemotePathKey`.
`RemotePathKey` is the single Core identity implementation for scan collision
detection, SQLite lookup, incoming upload resolution, and duplicate admission.
It:

- accepts `\` and `/` as input separators and stores a canonical `\` form;
- rejects roots, empty segments, `.`/`..`, control characters, invalid UTF-16,
  and encoded values beyond the protocol/storage bound;
- normalizes each segment to NFC; and
- applies invariant per-rune case folding before UTF-8 encoding.

SQLite `NOCASE` is not the identity rule. Catalog schema/runtime compatibility
changes trigger a rebuild; Sockseek does not expose a separate Unicode
algorithm version or migration protocol for this rebuildable index.

### 5.2 Filesystem support

Sockseek has no filesystem, drive-type, OS, or architecture allowlist. Any root
that the daemon account can enumerate and open through .NET is eligible. This
includes common ZFS, Btrfs, SMB/NFS, Docker bind, WSL, FUSE, NAS, Windows,
Linux, macOS, and ARM arrangements, subject to the actual permissions and
semantics of that deployment.

The catalog database SHOULD remain on reliable local storage in the daemon data
directory. That recommendation is independent of the filesystem containing
shared media.

### 5.3 Safe resolution and open

For an upload, Sockseek MUST:

1. parse and normalize the bounded remote path;
2. resolve an exact file row in the currently published catalog;
3. combine only the catalog-owned root and relative path;
4. canonicalize and verify containment;
5. reject traversal through a symbolic link or reparse point;
6. open with ordinary .NET read-only APIs and shared-read semantics;
7. read current length from the open stream;
8. compare current length and modification time with the accepted catalog row;
9. validate `0 <= startOffset <= advertisedLength`; and
10. return an `ExactLengthReadStream` for exactly the remaining bytes.

Stable handle identity or final-target APIs MAY add opportunistic hardening when
a portable and reliable runtime contract is available. Their absence MUST NOT
disable a platform, mount, or complete root. A concrete containment, link,
size, timestamp, or access failure rejects only that file/request.

Sockseek does not reopen a completed upload. Such a check cannot retract bytes
already delivered and creates another race. A truncation during transfer causes
the exact-length stream/library operation to fail normally.

## 6. Catalog and scanning

### 6.1 Storage model

There is one disk-backed catalog mode. The SQLite generation contains:

- metadata: schema, generation, settings fingerprint, counts, browse metadata;
- roots: effective alias and local path;
- directories and files: local relative path, remote display path, binary
  comparison key, size, modified time, protocol fields, and audio attributes;
- a cross-kind path-identity table preventing file/directory collisions; and
- an FTS index used only as a candidate prefilter.

Local paths make catalog files private. The sharing directory, manifest,
catalogs, and artifacts MUST be owner-only where the platform supports it.

### 6.2 Generations

A scan writes fresh staging filenames. Publication happens only after database
finalization, browse-artifact construction or an explicit oversize marker,
metadata validation, and owner-permission checks.

`current.json` is atomically replaced and names the current generation plus at
most one fallback. Existing leases keep a retired generation alive until their
search, directory, or browse stream finishes. Failed/cancelled scans and disk
full/write errors remove staging files and leave the current generation active.

The catalog is derived data. At ordinary startup Sockseek validates manifest and
schema compatibility, settings fingerprint, generation IDs, browse header/state,
and artifact length. The artifact hash is computed once while building; startup
does not rehash the complete artifact. If lightweight validation fails,
Sockseek tries the one fallback and otherwise rebuilds. Full flush and directory
durability primitives are best-effort platform details, not support gates.

### 6.3 Bounded scan pipeline

One scan runs at a time:

```text
Preparing → Enumerating → FinalizingIndex → BuildingBrowseArtifact
          → Validating → Publishing → Completed
```

Cancellation transitions through `Cancelling` to `Cancelled`; other failure is
`Failed`. Discovery is iterative rather than recursive. A bounded channel feeds
a small internal metadata-worker pool, and one writer batches SQLite records.
The pipeline MUST NOT retain the entire library or an unbounded error list.

The initial scan runs in the background so HTTP and downloads can start. A
manual trigger during an active scan reports the existing scan. Periodic ticks
coalesce rather than queue multiple scans.

### 6.4 Inclusion policy

- Excluded subtrees and regex-matched directories skip their complete subtree.
- Regex-matched files are omitted.
- Windows `Hidden` or `System` entries are skipped.
- Unix dotfiles and dot-directories are skipped.
- Links and reparse points are skipped, including directory subtrees.
- An attribute/read error records a bounded sample and skips that entry.
- Zero-byte regular files are indexed and served.
- Audio metadata failure records a sample but does not omit an otherwise
  readable file.
- A root that cannot be opened is a scan failure; the previous generation stays
  active.
- A remote-key collision fails the staging generation rather than publishing
  an ambiguous namespace.

This hidden-file policy is a simple privacy default, not a filesystem
qualification scheme. An include-hidden option is deferred until operators ask
for it.

## 7. Soulseek protocol serving

### 7.1 Callback boundary

`SoulseekSharingAdapter` owns all incoming protocol callbacks. It validates
encoded sizes and shapes, applies access policy, uses finite deadlines, catches
exceptions at the library boundary, and returns the protocol's safe empty or
denied behavior.

Small global callback gates bound executing plus waiting search, browse,
directory, and upload-admission work. There is no keyed per-user callback gate.
Duplicate detection and per-user capacity are atomic inside the scheduler, the
actual owner of that state.

### 7.2 Search

Incoming search:

1. validates username, query term/exclusion counts, and encoded query size;
2. rejects an exactly blocked username;
3. acquires the bounded global search budget;
4. acquires one catalog lease;
5. performs a bounded FTS candidate query;
6. applies Soulseek substring semantics and request exclusions;
7. applies the latest valid server-supplied excluded phrase set;
8. performs at most one bounded endpoint lookup when exact IP policy requires
   it; and
9. returns at most the internal response limit.

The excluded phrase set starts empty, like slskd. A valid server event atomically
replaces it. A malformed or oversized event is rejected and logged while the
last valid set remains active; it never disables unrelated search serving.

Endpoint-bearing callbacks check exact IP policy directly. Search uses an
endpoint already available or one `GetUserEndPointAsync` lookup. An offline or
unresolvable user receives no non-empty response. v4 has no endpoint cache,
freshness tiers, CIDR, ASN, or DNS policy.

### 7.3 Browse and directory responses

Full browse is serialized once per generation into a protocol-ready framed,
compressed file. Construction streams catalog rows to disk. It MUST NOT create
a complete `BrowseResponse` graph or a library-sized byte array.

Each response opens the immutable artifact, wraps it in an exact-length stream,
and owns a generation lease and bounded browse permit. Soulseek.NET 10.0.2 does
not dispose `RawBrowseResponse.Stream` in a `finally` if its network write fails.
`SelfExpiringReadStream` is therefore an isolated compatibility workaround that
releases Sockseek-owned resources by its idle deadline and makes future reads
fail. It does not promise to cancel a write already stalled inside the library.
Remove it when a pinned Soulseek.NET release disposes the stream reliably; do
not grow it into a general network-timeout subsystem.

Directory requests use exact catalog lookup and a bounded file/encoded response.
User information reports description, slots, queue size, and current free-slot
hint without exposing roots or policy contents.

If no catalog exists, responses are empty and uploads are denied as unavailable.
If full browse is oversized, search, directory lookup, and upload resolution may
remain ready while browse health is degraded.

## 8. Upload scheduler and lifecycle

### 8.1 Admission

The enqueue callback returns only `Task`; it cannot return a transfer resource.
Admission therefore behaves as follows:

1. validate bounded username/path and exact peer policy;
2. resolve the exact current catalog file;
3. create an admission request with transfer ID, normalized username, path key,
   size, and admission time;
4. under the scheduler lock, check duplicate and capacity indexes;
5. create one queued transfer if accepted;
6. complete a duplicate enqueue callback successfully without creating a
   transfer or changing counters; and
7. dispatch scheduler grants outside the lock.

`PlaceInQueueResolver` looks up the existing duplicate and reports its nullable
best-effort position. Duplicate coalescing has a low-cardinality metric.

Public admission categories are intentionally small: invalid request, denied,
not shared, unavailable, and capacity. Diagnostic distinctions belong in logs.

### 8.2 Fair queue

The scheduler stores no task per waiting item. Its authoritative indexes are:

- transfer ID to entry;
- `(normalized username, RemotePathKey)` to nonterminal transfer;
- FIFO linked list per username;
- round-robin ring of ready usernames;
- admission-order key index for paging; and
- active transfer IDs.

At most one transfer per username is active because Soulseek.NET also enforces
that constraint. With a free global slot, the scheduler removes the next ready
username, grants its first FIFO item, and rejoins that username only after the
active transfer ends. This prevents starvation and avoids entering
Soulseek.NET's per-user/global semaphores before Sockseek owns a slot.

Slots are configured daemon-wide. The speed cap is aggregate KiB/s and is
applied through Soulseek.NET's shared token bucket; null means unlimited.

### 8.3 Queue estimates and paging

Queue placement is a hint, not a promise. It returns:

```csharp
record TransferQueueEstimateDto(int? AheadCount, long QueueRevision);
```

The inexpensive estimate counts earlier files for the same user plus users
currently ahead in the ready ring. Missing means unavailable/not queued. There
are no timestamps, unavailable-code taxonomy, forecast modes, or start-time
prediction.

The operational live queue uses bounded keyset paging by
`(RequestedAtUtc, TransferId)`. Its opaque base64 cursor contains only that key
and the last observed queue revision. It is not signed and is not an
authorization token. Decoding is bounded and validated; malformed input returns
`400`. Mutation does not invalidate continuation. A page reports the current
`ObservedQueueRevision` and a best-effort `QueueChanged` hint. There is no strict
mode, `409` revision protocol, origin binding, or filter binding.

### 8.4 Transfer lifecycle

```text
Queued → Initializing → InProgress → Completed
   │          │              │
   ├──────────┴──────────────┼→ Cancelled
   └─────────────────────────┼→ Failed
                             └→ Interrupted (daemon shutdown/restart)
```

Every accepted transfer has zero or one attempt. Queued cancellation has zero.
The attempt is created when scheduler-dispatched protocol work begins. Sockseek
does not retry an upload automatically; another peer request is another
transfer.

Before dispatch, the coordinator re-resolves the path against the current
generation and checks the accepted size/modified metadata. The library stream
factory receives the peer's start offset. `seekInputStreamAutomatically` is
disabled because Sockseek positions the exact-length stream itself. Offset zero
through exact EOF is valid, including a zero-byte file.

Progress callbacks update bounded live state. A single idempotent terminal
transition owns state outcome, attempt completion, history notification, and
slot release. Late cancellation/progress callbacks cannot terminalize or release
twice.

Public upload failure categories are not shared, unavailable, invalid offset,
denied, and internal failure. Cancellation and interruption are terminal
outcomes rather than artificial failure subcodes.

Shutdown stops admission, interrupts queued transfers, gives active operations a
bounded grace interval, cancels remaining work, and terminalizes exactly once.
On restart, persisted nonterminal uploads are reconciled as interrupted; the
in-memory queue is not resumed.

## 9. Live state, API, clients, and history

### 9.1 Compact public state

Sharing and upload summaries use the same small health enum:

```csharp
enum SharingHealthState { Disabled, Starting, Ready, Degraded }
```

Each summary has at most one stable `Reason`. Scan progress remains a separate
resource because it is useful operator state. Component-level details remain in
logs/metrics or a future explicit diagnostics resource; implementation branches
do not each become a public health state.

`SharingStateDto` contains health/reason, public aliases, blocked-entry counts,
aggregate catalog metadata, and active/last scan. It never returns local roots,
exclusions, regex contents, or blocked values.

`UploadRuntimeStateDto` contains health/reason, accepting flag, slot/queue
counts, queue revision, and configured speed cap. The complete queue is not
replicated in daemon state.

Daemon summaries are latest-value coalescible. Ordered transfer terminal changes
remain ordinary transfer deltas. A slow SignalR consumer does not require one
outbound daemon delta for every intermediate queue mutation.

### 9.2 Transfer model and persistence

Uploads reuse the direction-neutral `TransferStateDto` and reducer:

- `Direction = Upload`;
- `JobId` and `WorkflowId` are null;
- identity contains username and remote public path, never local root;
- scheduling contains requested/started times;
- status contains attempt count, terminal outcome, compact failure/cancellation
  category, and actions; and
- progress contains bounded byte/speed state.

The coordinator is authoritative while a transfer is live. The persistence
adapter asynchronously writes one transfer row and zero or one attempt. History
failure degrades observability but MUST NOT block or fail a peer upload.
Nonterminal rows are reconciled to interrupted after a daemon restart.

### 9.3 HTTP and clients

Sharing resources:

```text
GET  /api/sharing
POST /api/sharing/scans
GET  /api/sharing/scans/{scanId}
POST /api/sharing/scans/{scanId}/cancel
```

Transfer resources:

```text
GET  /api/transfers?direction=upload&...
GET  /api/transfers/live?direction=upload&state=queued&cursor=...&limit=...
GET  /api/transfers/{transferId}
POST /api/transfers/{transferId}/cancel
GET  /api/transfers/{transferId}/attempts
```

History and live collections are bounded and paged. Transfer detail is
live-first and merges retained history/attempts when available.

`SockseekApiClient`, `SockseekLiveClient`, and `DaemonClientStore` expose these
resources using the same DTOs and reducer as the CLI. Scan and transfer
cancellation are discoverable `ResourceActionDto` actions. Remote commands
require `--remote`; they never start a temporary daemon.

The shared live protocol version changes whenever this DTO/reducer contract is
incompatible. OpenAPI is generated during the server build.

### 9.4 Authorization seam

Scan start/cancel and transfer cancellation carry the named
`Sockseek.Operator` policy. Its evaluator is pass-through until the roadmap's
daemon authentication work. This is an integration seam, not current access
control. A non-loopback unauthenticated daemon is explicitly insecure.

## 10. Failure behavior, security, and operations

### 10.1 Failure boundaries

- Invalid settings fail before sharing starts, without breaking ordinary CLI
  parsing or downloads.
- Missing/inaccessible roots fail only their staging scan.
- Per-entry I/O or metadata failures skip and record a bounded sample.
- Regex timeout or remote-key collision fails the staging generation.
- Disk full/write failure cleans staging and retains the old generation.
- Lightweight startup corruption falls back once or requests a rebuild.
- Invalid peer input produces an empty/denied callback response.
- A changed, missing, or no-longer-shared queued file fails that transfer.
- History persistence failure degrades history, not the network transfer.
- A listener/session outage reports compact degraded/starting health and empty
  protocol behavior; daemon HTTP and downloads remain available.

### 10.2 Abuse resistance

- No peer value becomes a local path lookup outside exact catalog resolution.
- Usernames, paths, queries, exclusions, phrases, pages, cursors, response
  sizes, regex execution, queue items, callback work, and error samples are
  bounded.
- SQL uses parameters and binary path keys.
- Full browse and live/history collections are file-backed or paged.
- Metrics never label usernames, paths, queries, or transfer IDs.
- Public APIs disclose aliases and aggregate counts only.

### 10.3 Metrics

The stable initial instrument set is deliberately compact:

- catalog file/directory/byte gauges;
- total scan duration and result;
- active/queued upload and queued-byte gauges;
- uploaded bytes and completed/rejected totals;
- duplicate coalescing; and
- dropped inbound requests by low-cardinality request type.

Add phase/latency histograms only when a measured budget or field diagnosis uses
them. Metric names and labels are compatibility surface, not free debug output.

### 10.4 Operator documentation

Operational behavior and current CLI configuration live in
[`docs/daemon.md`](../daemon.md). The README keeps only a short daemon overview
and generated option reference. API consumers use [`docs/api.md`](../api.md) and
the generated OpenAPI document.

## 11. Verification strategy

### 11.1 Automated coverage

The maintained suite covers:

- alias parsing, volume roots, overlapping roots, exclusions, finite regexes,
  exact peer identities, and `RemotePathKey` NFC/case/separator collisions;
- portable .NET safe open, containment, link rejection, current metadata,
  exact-length streams, and zero-byte scan inclusion;
- bounded scan behavior, hidden/system subtree policy, fatal root failure,
  catalog lookup/search, generation publication/fallback, lease drain, and
  owner-only files;
- empty and updated excluded phrase sets, invalid update retention, bounded
  callback gates, search filtering, and missing-listener behavior;
- round-robin/FIFO scheduling, one active upload per user, duplicate
  coalescing, internal capacity, cancellation, idempotent terminalization,
  best-effort queue paging, and 100,000-entry stress;
- zero/one attempt lifecycle, resume offsets, generation re-resolution,
  cancellation/shutdown, progress, and persistence non-interference;
- compact state coalescing, reducer/client behavior, API/OpenAPI contracts,
  operator actions, remote CLI/config/help parity, and restart reconciliation.

### 11.2 Release qualification

Qualification is intentionally small and evidence-based:

1. Record a representative 100,000-file scan plus browse build, warm exact
   lookup/search, restart, and retained-generation behavior on a homeserver-class
   host. Record fixture, hardware, filesystem/mount, runtime, duration, and peak
   memory. A one-million-row fixture is optional stress evidence.
2. Run the 100,000-entry scheduler stress and bounded persistence-outage/restart
   scenarios with managed-heap observations.
3. Compare governed and ungoverned Soulseek uploads on the same loopback network
   harness, recording throughput and speed-cap accuracy. Do not compare a
   protocol upload to unrelated `FileStream.CopyToAsync` disk copying.
4. Run one repeatable interoperability smoke suite covering search, full browse,
   directory listing, queue placement, zero-byte upload, resume, cancellation,
   and denial with the pinned Soulseek.NET stack and at least one independent
   client. Broader clients/platforms are periodic compatibility work, not a
   blocker for every patch.

Regression thresholds are recorded from this baseline and user expectations.
There are no invented universal p95/p99, heap-ratio, or scan-duration numbers.
A regression that causes unbounded growth, protocol failure, or plainly
unusable homeserver behavior blocks release; ordinary variance does not.

## 12. Implementation map

```text
Sockseek.Core/
  IO/
    ExactLengthReadStream.cs
    SelfExpiringReadStream.cs
  Settings/
    SharingSettings.cs
    UploadSettings.cs
    PeerAccessSettings.cs
  Sharing/
    PeerAccessPolicy.cs
    SafeSharedFileOpener.cs
    ShareCatalogContracts.cs
    SharePath.cs
    ShareScanner.cs
    SharingSettingsFingerprint.cs
    SharingSettingsValidator.cs
    SharingTelemetry.cs
  Soulseek/
    InboundCallbackGates.cs
    SoulseekBrowseArtifactBuilder.cs
    SoulseekClientManager.cs
    SoulseekSharingAdapter.cs
  Transfers/Uploads/
    UploadCoordinator.cs
    UploadScheduler.cs

Sockseek.Persistence/Sharing/
  ShareCatalogManager.cs
  ShareScanCoordinator.cs
  SqliteShareCatalog.cs

Sockseek.Server/
  LiveTransferCursorCodec.cs
  OperatorMutationPolicy.cs
  SharingRuntime.cs
  Persistence/UploadPersistenceAdapter.cs

Sockseek.Api/Contracts/SharingUploads.cs
Sockseek.Api/Contracts/LiveState.cs
Sockseek.Cli/DaemonResourceCommandRunner.cs
```

The removed `ShareFilesystemSupport` and `ShareDiskSpaceGuard` types MUST NOT be
reintroduced without evidence that a narrow capability check or measured disk
policy solves a real defect.

## 13. Authoritative implementation checklist

`[x]` means the current tree contains the behavior and proportionate automated
or source-backed evidence. Release qualification is tracked separately in
section 11.2 so this checklist does not confuse implemented architecture with a
manual release record.

### Architecture and configuration

- [x] **ARCH-01** Sharing and uploads have daemon lifetime and share one
  Soulseek session with downloads.
- [x] **ARCH-02** Catalog, scheduler/coordinator, protocol adapter, live
  projection, and history projection have separate owners.
- [x] **ARCH-03** The catalog is rebuildable disk-backed derived data, separate
  from historical persistence.
- [x] **ARCH-04** Settings expose roots, exclusions, filters, rescan policy,
  slots, speed cap, and exact peer denial without internal tuning knobs.
- [x] **ARCH-05** List config supports replace plus explicit append and rejects
  daemon settings in download profiles.
- [x] **ARCH-06** Volume roots require an explicit alias; overlapping roots are
  allowed under distinct remote aliases.
- [x] **ARCH-07** Exact username and IP policy is normalized once and applied at
  every relevant callback without an endpoint-cache subsystem.

### Paths, filesystems, catalog, and scan

- [x] **CAT-01** `RemotePathKey` is the one Core identity for collision,
  lookup, and duplicates; schema compatibility causes rebuild.
- [x] **CAT-02** Peer requests resolve exact catalog rows and never probe local
  paths directly.
- [x] **CAT-03** Safe open uses portable .NET APIs, containment, link/reparse
  rejection, current metadata, and exact-length resume without platform gates.
- [x] **CAT-04** Filesystem/OS/architecture allowlisting is absent and catalog
  local-storage advice is separate from media mounts.
- [x] **CAT-05** A scan is bounded, cancellable, iterative, and one-at-a-time,
  with bounded progress/error samples.
- [x] **CAT-06** Hidden/system/dot/link subtrees are skipped per the simple
  privacy policy; entry failures do not reject a filesystem.
- [x] **CAT-07** Zero-byte regular files are cataloged.
- [x] **CAT-08** Staging, validation, atomic manifest replacement, lease drain,
  and one fallback generation prevent partial publication.
- [x] **CAT-09** Startup uses lightweight schema/manifest/header/length checks
  instead of a complete artifact rehash.
- [x] **CAT-10** Disk/write failures clean staging and retain the prior
  generation without arbitrary reserve polling.
- [x] **CAT-11** Catalog artifacts receive owner-only protection where the
  platform supports it.

### Protocol serving and uploads

- [x] **SERVE-01** Incoming callbacks validate/bound untrusted input and use
  small global gates plus deadlines.
- [x] **SERVE-02** Excluded phrases start empty, valid updates replace
  atomically, and invalid updates preserve the last valid set without disabling
  search.
- [x] **SERVE-03** Search, browse, directory, and user-info responses use
  catalog leases and bounded results.
- [x] **SERVE-04** Full browse is a streamed immutable artifact; the temporary
  Soulseek.NET disposal workaround has a narrow resource-release contract.
- [x] **UP-01** Scheduler duplicate and capacity mutation is atomic without a
  keyed per-user callback gate.
- [x] **UP-02** Duplicate enqueue completes successfully without a new transfer,
  counter mutation, or impossible callback response object.
- [x] **UP-03** The scheduler is FIFO per user, round-robin between users, has
  one active upload per normalized user, and grants before calling the library.
- [x] **UP-04** Queue capacity is hard-bounded globally and internally per user;
  waiting entries allocate no task.
- [x] **UP-05** Queue position is nullable and best effort; live paging is
  mutation-tolerant keyset paging with validated unsigned cursors.
- [x] **UP-06** Dispatch re-resolves the current catalog and validates accepted
  metadata before stream creation.
- [x] **UP-07** Resume offsets, including exact EOF and zero-byte files, use an
  exactly bounded stream without double seek.
- [x] **UP-08** Every accepted transfer has zero or one attempt and no automatic
  retry.
- [x] **UP-09** One idempotent terminal owner arbitrates cancellation,
  interruption, history projection, and slot release.
- [x] **UP-10** Completion does not perform a misleading terminal file reopen.
- [x] **UP-11** Slots and aggregate speed limit use Soulseek.NET's established
  semaphore/governor contracts.

### State, persistence, API, clients, and operations

- [x] **STATE-01** Public sharing/upload summaries use only `Disabled`,
  `Starting`, `Ready`, and `Degraded` plus one reason.
- [x] **STATE-02** Full queue/history collections remain outside replicated
  daemon state; daemon summary replacements are coalescible.
- [x] **STATE-03** Uploads reuse direction-neutral transfer DTOs and reducer
  state with nullable job/workflow ownership.
- [x] **STATE-04** Durable history asynchronously records one transfer and zero
  or one attempt without interfering with peer service.
- [x] **STATE-05** Restart reconciliation marks persisted nonterminal uploads
  interrupted and does not resume the in-memory queue.
- [x] **API-01** Sharing scans, live queue, transfer detail/history/attempts, and
  cancellation have bounded HTTP resources and generated OpenAPI.
- [x] **API-02** `SockseekApiClient`, `SockseekLiveClient`, and
  `DaemonClientStore` expose the same contracts used by the CLI and future GUI.
- [x] **API-03** Actions use the shared `Sockseek.Operator` policy seam and the
  unauthenticated trust boundary is explicit.
- [x] **API-04** Status resources disclose aliases/aggregates but no local roots,
  filter contents, exclusions, or deny-list entries.
- [x] **OPS-01** Metrics are compact and low-cardinality.
- [x] **OPS-02** CLI help, README option reference, daemon guide, API guide, and
  OpenAPI reflect the supported surface.
- [x] **OPS-03** Automated Core, persistence, server, API, and CLI tests cover
  the implemented behavior without duplicating this checklist by phase.

## 14. Roadmap compatibility

The v4 roadmap next adds chat, user browsing, and a Web UI. This design supports
that work by keeping one daemon Soulseek session, shared live clients/reducer,
bounded daemon state, paged resources, and one operator authorization seam.

Future private/group sharing should introduce a real query/authorization context
when its visibility requirements are known. It may require separate artifacts,
filtered artifacts, or a different serving strategy; v4 does not pretend a
`Public` enum parameter solves that design in advance.

Authentication replaces the current pass-through `Sockseek.Operator` evaluator.
It must not require rewriting scan or transfer handlers.

## 15. Source record

### Sockseek

- [v4 branch](https://github.com/fiso64/sockseek/tree/v4)
- [v4 roadmap](https://github.com/fiso64/sockseek/blob/v4/TODO.md)
- [daemon operation](../daemon.md)
- [API and client integration](../api.md)
- [persistence design](archive/persistence-design.md)

### slskd

The architecture review used slskd as established implementation evidence, not
as a policy specification:

- [configuration](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/docs/config.md)
- [application resolver wiring](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Application.cs)
- [share service](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Shares/ShareService.cs)
- [share scanner](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Shares/ShareScanner.cs)
- [SQLite repository](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Shares/SqliteShareRepository.cs)
- [upload service](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Transfers/Uploads/UploadService.cs)
- [upload queue](https://github.com/slskd/slskd/blob/e42a525d700d6dc343f316447803138b8ea2fbe3/src/slskd/Transfers/Uploads/UploadQueue.cs)
- [large-share OOM issue #610](https://github.com/slskd/slskd/issues/610)
- [browse cache OOM issue #1765](https://github.com/slskd/slskd/issues/1765)

The useful lessons are separation of catalog/scheduler/governor/history and the
need for bounded large-library behavior. Sockseek deliberately does not copy
slskd's full configuration surface or infer a filesystem blacklist from its
implementation.

### Soulseek.NET

- [excluded phrase requirement](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/README.md#excluded-search-phrases)
- [callback contracts](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Options/SoulseekClientOptions.cs)
- [raw browse streaming](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Messaging/Handlers/PeerMessageHandler.cs)
- [upload semaphore ordering](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/SoulseekClient.cs)
- [transfer options](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Options/TransferOptions.cs)
- [package 10.0.2](https://www.nuget.org/packages/Soulseek/10.0.2)

Source conclusions tied to the pinned dependency:

- per-user upload concurrency is one;
- acquisition order is library per-user semaphore, caller `SlotAwaiter`, then
  library global upload semaphore, so queued work stays outside `UploadAsync`;
- the stream factory receives the peer offset, requiring automatic seek off;
- the upload overload accepts a size of zero (only negative sizes are rejected),
  and a transfer with zero remaining bytes completes without a connection write;
- the upload speed cap uses a shared patchable token bucket; and
- raw browse stream disposal still needs the isolated compatibility workaround
  described in section 7.3.

---

## Bottom line

Sockseek keeps the pieces that protect remote-path privacy, atomic publication,
fairness, protocol interoperability, and bounded memory. It removes speculative
filesystem qualification, transactional live-queue semantics, duplicated gates,
unmeasured resource policies, excessive public states/codes/metrics, and tuning
knobs that homeserver operators should not need.

That balance is the design constraint for later sharing work: learn from slskd's
failures, but do not turn those lessons into an unmaintainable second operating
system.
