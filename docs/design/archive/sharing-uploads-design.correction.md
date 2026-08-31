# Sharing and uploads design correction record

This file preserves former section 28 of `sharing-uploads-design.md` for future
reference. The audit was written on 2026-08-06 after the first implementation.
Its required direction has since been merged into the main design and
implemented. This is a historical correction record, not a second normative
specification.

## 28. Self-hosting and maintainability complexity audit

This audit was requested after implementation, on 2026-08-06. Sockseek is a
self-hosted/homeserver application. Remote Soulseek peers are untrusted, but the
operator, daemon account, configured roots, mounted storage, and ordinary local
administration are inside the product's trust boundary. Correctness and bounded
resource use remain important; hypothetical protection against a hostile local
administrator or storage stack does not justify rejecting common homeserver
deployments or building a second operating system inside Sockseek.

This section records the intended simplification direction. Where it conflicts
with an earlier prescription, this section controls the next design/code
revision. It does not pretend that the current implementation has already been
changed. Requirements affected by an open item below remain unchecked even when
the stricter current implementation happens to enforce them.

### 28.1 Decision filter

Future sharing decisions should use these rules:

1. Prefer .NET and Soulseek.NET behavior unless a demonstrated defect requires a
   narrow workaround.
2. Reject hostile peer input, but do not treat every locally mounted filesystem
   as hostile.
3. Fail one entry or request when possible. Reject a complete root or catalog
   only when continuing could expose a path outside the configured namespace or
   publish an internally inconsistent generation.
4. Add configuration knobs only for meaningful operator choices, not every
   internal concurrency constant.
5. Add a hard release gate only when it protects interoperability, data/privacy,
   bounded resource use, or an observed regression. Measurements establish
   thresholds; invented thresholds do not establish correctness.
6. Prefer one understandable conservative behavior over capability matrices,
   strict/relaxed modes, or fallback hierarchies that ordinary operators cannot
   reason about.

### 28.2 Decisions to remove or simplify

| Area | Finding | Required direction |
| --- | --- | --- |
| Filesystem allowlist | Restricting shares to local fixed NTFS and native-root ext4/XFS excludes ZFS, Btrfs, SMB/NFS, Docker bind mounts, FUSE, WSL mounts, macOS, ARM homeservers, and common NAS layouts without evidence that they are unusable. Filesystem names are not reliable capability probes. slskd imposes no corresponding allowlist. | Remove `ShareFilesystemSupport` filesystem/drive/mount qualification and the `UnsupportedFilesystem` root failure. Any root that .NET can enumerate and open is eligible. Keep the catalog database's local-storage recommendation separate from the filesystem containing shared media. |
| Safe open | Exact catalog lookup and containment are remote security boundaries; scan-to-open file identity is mostly protection against a local actor who can already mutate the configured share. Requiring native final-path and stable-identity primitives everywhere turned optional hardening into a portability blocker. | Keep exact catalog resolution, canonical containment, link/reparse exclusion, read-only open, current length validation, and resume bounds. Prefer normal .NET file APIs. Use handle identity/final-target validation opportunistically where reliable, and fail an individual open when a concrete check reports danger. Do not reject an OS, architecture, mount, or root because optional identity data is unavailable. |
| Volume and overlapping roots | Rejecting a volume root is paternalistic, and rejecting every overlapping root prevents useful separately aliased views. Neither is needed to prevent remote-path ambiguity. | Permit a volume root when it has an explicit safe alias. Permit overlapping local roots when their effective remote namespaces are distinct. Continue rejecting duplicate aliases and actual remote-key collisions. Warn about likely accidental broad sharing rather than inventing a filesystem policy. |
| Hidden/system entries | The current policy turns differences in attribute reliability into filesystem support decisions. That couples a privacy default to mount qualification. | Keep a simple documented default using the platform/.NET hidden, system, and reparse semantics, including subtree skipping. Attribute-read failures skip that entry. They do not make the filesystem unsupported. Consider an include-hidden option only after real operator demand; do not add a matrix now. |
| File mutation | Reopening a file after upload and changing a successful network outcome to `FileChangedDuringTransfer` cannot retract bytes already delivered and adds another filesystem race and I/O operation. | Validate immediately before returning the stream and bound it to the advertised remaining length. Remove terminal reopen/revalidation and the dedicated terminal failure code. If an in-progress file changes or truncates, the stream/library failure is sufficient; mark the catalog stale when detected. |
| Free-space guard | A fixed 512 MiB reserve checked every 1,024 catalog rows and 4,096 browse rows is arbitrary, adds hot-loop filesystem calls, and still cannot guarantee that another process will not consume the disk. | Check available space before large phases, optionally warn when low, handle SQLite/write `disk full` failures, delete staging artifacts, and retain the old generation. Do not promise a reserve or fixed polling cadence without measurements showing it solves a real problem. |
| Excluded search phrases | Disabling all search until an "authoritative" event arrives, and disabling it again for one malformed/oversized server event, creates a fragile readiness subsystem. slskd starts with an empty list and atomically replaces it when the event arrives. | Start with an empty immutable set, apply valid server updates, retain the last valid set when an invalid update is received, and log/measure the rejection. Reasonable input bounds remain useful, but an invalid trusted-server event must not disable unrelated sharing indefinitely. |
| Exact IP denial | Exact-IP blocking itself is not analogous to the filesystem allowlist: slskd also resolves the requester endpoint before returning non-empty search results, and Soulseek.NET needs the endpoint to deliver the response. The extra risk is turning that lookup into another cache/readiness subsystem. | Keep exact username/IP entries. Use an endpoint already supplied by the callback or one bounded `GetUserEndPointAsync` lookup when necessary; an offline/unresolvable peer receives no response. Do not add endpoint freshness tiers, a background endpoint cache, or broader CIDR/ASN policy in this release. |
| Request gates | A separate ref-counted one-at-a-time admission gate per username duplicates atomic duplicate/limit handling already owned by the scheduler and creates another keyed lifetime to clean up. | Keep small global bounded callback gates and request deadlines. Perform per-user duplicate and limit decisions under the scheduler/coordinator's existing atomic mutation boundary. Add a keyed gate only if a measured callback race cannot be solved there. |
| Live queue pagination | Best-effort keyset paging is sufficient for an operational queue. Strict revision mode, `409 QueueRevisionChanged`, cursor signing, consistency-mode binding, and origin/observed revision enforcement form a transaction-like protocol for volatile data with little user value. | Keep hard page limits, `(RequestedAtUtc, TransferId)` keyset paging, an observed queue revision, and an optional `QueueChanged` hint. Remove strict mode and integrity protection. Treat the cursor as validated untrusted input and return `400` when malformed. Parameterized queries and bounded parsing are the security boundary. |
| Queue estimates | Simulating deep round-robin order with work budgets, a special unavailable code, timestamps, and revisions is more machinery than the protocol/UI needs. | Return a nullable best-effort ahead count for the requested transfer and the current queue revision. A missing estimate means unavailable. Do not promise start times or add more forecast modes. Preserve O(1) scheduling even if an estimate is approximate. |
| Catalog durability | The catalog and browse artifact are rebuildable, yet the design asks for database checks, full artifact hashing at startup, file flush, atomic replacement, parent-directory durability handling, retained rollback, and multiple cleanup states. Some of this is justified; treating it like irreplaceable financial data is not. | Keep staging generations, validation before publication, atomic manifest replacement, one prior fallback generation, and safe cleanup. Hash once when building; use manifest/schema/header/length checks at ordinary startup and rebuild/fallback after actual corruption. Parent-directory flush and full startup hash verification are best-effort/platform details, not product support gates. |
| Browse disposal workaround | `SelfExpiringReadStream` compensates for a specific Soulseek.NET 10.0.2 disposal bug and spreads timer/lifetime behavior into Sockseek. | Prefer upgrading or carrying a tiny pinned upstream patch that disposes the raw response stream in `finally`. Retain the wrapper only as a documented temporary compatibility workaround and delete it when the dependency contract is fixed. It must not grow into a general network-timeout subsystem. |
| Public tuning surface | Scan workers, search concurrency, search queue capacity, result limits, and multiple upload admission capacities expose implementation details and multiply config/help/test burden. Most homeserver operators should never tune them. | Keep roots, exclusions, filters, rescan policy, upload slots, speed limit, and peer deny entries as the primary surface. Keep safe internal constants for callback gates and response limits. Expose a tuning option later only when field evidence identifies a real deployment need. Reconsider whether two independent per-user queue-limit knobs earn their public cost. |
| Health/state taxonomy | Separate catalog, scan, browse, search-readiness, listener, upload-admission, persistence, stale-file, and unavailable-reason axes can produce a large state space that clients and operators must understand. | Preserve scan progress separately, but present a small public summary such as `Disabled`, `Starting`, `Ready`, and `Degraded`, plus one stable reason when degraded. Detailed component diagnostics can remain in metrics/logs or an explicit diagnostics response. Do not add a state solely because one implementation branch exists. |
| Reason-code taxonomy | The design defines broad admission, failure, cancellation, scan, health, and API code families before field experience shows clients need each distinction. | Keep codes that change operator/client behavior: denied, not shared, unavailable, capacity, invalid offset, cancelled, interrupted, and internal failure. Consolidate diagnostic-only distinctions in logs. Add a stable public code only when a client can act differently on it. |
| Metrics | Twenty-plus mandatory instruments and phase histograms are excessive for the first homeserver release and create a compatibility surface of their own. | Require a compact set: catalog counts, total scan duration/result, active/queued uploads, bytes, completed/rejected totals, and dropped requests. Add phase and latency histograms when they are used to diagnose or enforce a measured budget. Never add high-cardinality labels. |
| Remote-path key versioning | One normalized key and collision detection are necessary because SQLite `NOCASE` is insufficient. A separate Unicode algorithm version, runtime golden-vector migration regime, and rebuild rule are more elaborate than a rebuildable catalog needs. | Keep `RemotePathKey` as the single Core implementation and keep NFC/case/separator collision tests. Rebuild the catalog on schema/runtime compatibility changes instead of maintaining a public mini-versioning protocol for Unicode tables. Retain a bounded encoded path length based on actual protocol/storage limits. |
| Zero-byte files | A permanent product exclusion based on contradictory library documentation is not justified. | Settle the behavior with one focused Soulseek.NET/real-client test. If the pinned upload overload accepts zero-length transfers correctly, share them; otherwise skip them with a simple diagnostic. Do not build further architecture around the uncertainty. |
| Performance gates | Thirty-two structural requirements and nineteen precise numeric budgets are difficult to maintain, largely unmeasured, and contain arbitrary ratios/latencies. Making a one-million-row catalog, 100,000-entry queue, two filesystem variants, heap deltas, and many p95/p99 limits all release-blocking is disproportionate for a homeserver application. | Replace them with a small reproducible qualification suite: representative 100k-file scan/browse, warm lookup/search, 100k scheduler stress, bounded persistence-outage behavior, restart, and upload throughput/cap accuracy. Keep 1M rows as optional stress evidence. Establish regression thresholds from a recorded baseline and user expectations, not invented numbers. |
| Real-client matrix | Real protocol testing is valuable, but requiring two independent clients for every path, failure, and platform creates a large manual release ritual. | Require one repeatable interoperability smoke suite covering search, browse, directory listing, queue, resume, cancellation, and denial with Soulseek.NET plus at least one independent client. Broader clients/platforms are periodic compatibility testing, not a blocker for every patch. |
| Repeated completion rules | Sections 20, 21, 22, 23, and 24 restate the same semantics. This already caused an unchecked 200-item document after the implementation existed. | Make section 22 the compact authoritative functional checklist. Keep a shorter performance checklist and a short release checklist that reference IDs. Convert phase sections to sequencing guidance without duplicate stop-condition checkboxes. Each checked item should eventually link to a test, source decision, or qualification record. |

### 28.3 Complexity that is justified and should remain

The audit does **not** recommend reducing the feature to direct path
concatenation or an unbounded in-memory cache. These decisions have a concrete
benefit and fit the product:

- one daemon-owned Soulseek session shared by downloads, sharing, uploads, and
  later chat/user-browse features;
- exact remote-path lookup through the published catalog, with no peer-driven
  filesystem probing and no absolute-path disclosure;
- one disk-backed catalog mode rather than disk/memory semantic variants;
- staging plus atomic generation publication so a failed scan cannot expose a
  partial catalog;
- a streaming, file-backed full-browse artifact rather than a whole-library
  object graph or `byte[]`;
- bounded request/result sizes, finite regex execution, bounded diagnostic
  samples, and a hard scheduler capacity;
- one explicit round-robin/FIFO scheduler that grants work before calling
  Soulseek.NET, preventing hidden library queues and per-user starvation;
- re-resolving a queued remote path against the currently published catalog
  before dispatch;
- exact-length resume streams and single-owner terminal/slot arbitration;
- asynchronous, non-blocking history projection and paginated history/live
  collections rather than replicating or preloading them wholesale;
- owner-only catalog artifacts because they contain local paths; and
- shared API clients/reducer and discoverable actions, which directly support
  the v4 Web UI roadmap and avoid duplicate client state machines.

### 28.4 Follow-up order

Before release, simplify in this order:

1. Remove the filesystem allowlist and make safe-open identity hardening
   portable/opportunistic; update `docs/daemon.md` and affected tests.
2. Remove excluded-phrase fail-closed readiness and the redundant per-user
   callback gate.
3. Collapse live queue paging to one best-effort keyset contract and simplify
   queue estimates.
4. Remove terminal file reopen, arbitrary disk-reserve polling, and unnecessary
   catalog durability work.
5. Reduce public knobs, health/reason states, and mandatory metrics where doing
   so remains API-compatible before v4 ships.
6. Replace the current performance/release matrix and duplicate checklists with
   a small evidence-backed qualification record.

The implementation should be re-tested after each group. Simplification is not
complete merely because this audit documents it.
