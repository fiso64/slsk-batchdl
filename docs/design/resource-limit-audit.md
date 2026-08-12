# Resource-limit and failure-boundary audit

Status: audit complete, 2026-08-12. This document records findings; it does not
change the audited designs or authorize their implementation.

## Scope

The audit covered every active and archived design document present at the time:

- [`resolved-remote-transfer-refactor-plan.md`](resolved-remote-transfer-refactor-plan.md)
- [`user-browsing-design.md`](user-browsing-design.md)
- [`archive/api-improvements-design.md`](archive/api-improvements-design.md)
- [`archive/chats-design.md`](archive/chats-design.md)
- [`archive/persistence-design.md`](archive/persistence-design.md), which is empty
- [`archive/sharing-uploads-design.correction.md`](archive/sharing-uploads-design.correction.md)
- [`archive/sharing-uploads-design.md`](archive/sharing-uploads-design.md)

Archived implementation claims were checked against current code where possible.
The audit looked for the same design error as `DirectoryTransferMemoryEstimator`:
an internal resource estimate, queue depth, or convenient implementation bound
being turned into user-visible refusal or data loss for otherwise valid work.

## Standard used

A bound is not inherently a problem. The relevant distinction is what it bounds.

Acceptable examples include:

- paging an HTTP response while preserving access to the complete underlying data;
- retaining only a bounded live projection when clients can detect gaps and
  rehydrate authoritative state;
- rejecting malformed framing, traversal, decoder bombs, or values beyond a real
  protocol representation;
- limiting diagnostic samples, previews, cached copies, or concurrent execution
  without rejecting the underlying work;
- failing on actual exhaustion such as disk-full or an I/O error and cleaning up
  safely.

Problematic examples include:

- refusing a valid directory because an estimated object graph is too large;
- requiring users to split a valid request merely to fit an internal job graph;
- silently returning no data for a large but representable directory;
- acknowledging and discarding valid, unrecoverable data because an internal queue
  is full;
- failing an entire scan or transfer for an entry that could be isolated, skipped,
  or deduplicated.

The preferred product behavior is best effort. Work may be processed lazily,
scheduled with bounded concurrency, paged, or spooled to disk. A limit that is an
operator policy should be explicit and configurable, normally disabled by default,
and should not be presented as a fundamental validity rule.

## Findings

### 1. Directory-transfer admission rejects valid downloads

Severity: critical. Status: active design and current implementation.

The remote-transfer plan specifies limits of 20,000 files, 2 TiB of known file
bytes, and 128 MiB of estimated retained memory. Current code implements those
limits in `DirectoryTransferAdmissionPolicy` and invokes them from
`DirectoryDownloadJob.BeginDirectoryAttempt`. Because `AlbumJob` and
`RemoteDirectoryJob` share that lifecycle base, the policy affects both ordinary
folder downloads and music-album downloads.

This does not provide the safety claimed by the design. A peer-directory download
retrieves and materializes the directory snapshot and constructs the complete plan
before admission. Album code has already selected/materialized its files before the
same validation. Pre-resolved jobs instead fail during construction. The policy
therefore adds inconsistent failure behavior without bounding the expensive
upstream operation.

Evidence:

- [`resolved-remote-transfer-refactor-plan.md`](resolved-remote-transfer-refactor-plan.md),
  implementation summary and release gate 5
- [`DirectoryTransferPlan.cs`](../../Sockseek.Core/Remote/DirectoryTransferPlan.cs),
  `DirectoryTransferAdmissionPolicy`
- [`DirectoryTransferMemoryEstimator.cs`](../../Sockseek.Core/Remote/DirectoryTransferMemoryEstimator.cs)
- [`DirectoryDownloadJob.cs`](../../Sockseek.Core/Jobs/DirectoryDownloadJob.cs),
  `BeginDirectoryAttempt`
- [`RemoteDirectoryDownloadExecutor.cs`](../../Sockseek.Core/Transfers/Downloads/DownloadExecutors/RemoteDirectoryDownloadExecutor.cs),
  `ResolvePlan`

Required direction:

- Remove the estimator, admission policy, limits, benchmark-as-validity-rule, and
  tests that assert rejection.
- Make directory execution best effort with bounded concurrent transfers.
- Avoid requiring one eagerly materialized runtime child object per file where that
  becomes a real problem; enumerate/schedule compact plan entries progressively.
- Preserve per-file progress and failure while allowing the rest of the directory
  to continue.

### 2. User-browse ingestion proposes total-size rejection despite streaming

Severity: critical. Status: active design; user browsing is not yet implemented.

The browse design correctly proposes streaming the peer response into a staging
artifact, but then requires limits on total compressed and decompressed bytes,
directory/file rows, and total staging-disk growth. Crossing one produces
`413 browse-limit-exceeded` and discards the browse.

Streaming already separates total response size from live managed-memory use.
Fixed total-size and total-row ceilings recreate the estimator mistake at the
network boundary. Staging should continue until completion, cancellation, a real
protocol error, or actual storage/I/O failure. If an operator needs a storage quota,
it should be an explicit policy rather than an undocumented definition of a valid
Soulseek share.

Individual field/framing limits remain appropriate when tied to the Soulseek wire
format, integer representation, or safe parser operation.

Evidence:

- [`user-browsing-design.md`](user-browsing-design.md), “Streaming ingress and
  dependency boundary”, `browse-limit-exceeded`, and its boundary tests

Required direction:

- Delete total compressed/decompressed byte, total row, and total staging-growth
  rejection from the default design.
- Retain streaming parsing, bounded transactions, exact end-of-message validation,
  cancellation cleanup, and field-level protocol validation.
- Treat disk-full as an operational failure, not as proof that the peer's browse
  response was invalid.

### 3. User-browse download selection repeats directory admission

Severity: critical. Status: active design; user browsing is not yet implemented.

The design expands selected folders, checks file/byte/job-graph bounds, and returns
`413 selection-limit-exceeded` with advice to choose smaller subtrees. It separately
requires a measured fixed engine-memory budget. This is the directory estimator
under a new API name.

The HTTP request can remain compact while the server retains directory selections
as subtree identities and resolves/enumerates their files progressively. The number
of selected IDs may receive an ordinary request-body/schema bound, but the size of
the valid subtree those IDs denote must not become a validity check.

Evidence:

- [`user-browsing-design.md`](user-browsing-design.md), “Download submission”,
  “Remote directory jobs over resolved plans”, `selection-limit-exceeded`, and
  delivery step 5

Required direction:

- Remove expanded file/byte/job-memory admission and the “choose a smaller subtree”
  product behavior.
- Keep selections compact and enumerate their exact targets lazily or from a
  disk-backed work source.
- Deduplicate overlapping selections without materializing the complete runtime job
  graph merely to validate it.

### 4. Large shared directories silently produce an empty response

Severity: high. Status: archived design and current implementation.

The sharing design says directory responses are bounded. Current code requests at
most 10,000 catalog files and returns an empty directory response when that count is
exceeded or an estimated encoded response exceeds 8 MiB. Soulseek directory lookup
has no pagination contract through which the requester can retrieve the remainder,
so this makes a legitimate large directory appear empty.

Evidence:

- [`archive/sharing-uploads-design.md`](archive/sharing-uploads-design.md), “Browse,
  directory contents, and user information”
- [`SoulseekSharingAdapter.cs`](../../Sockseek.Core/Soulseek/SoulseekSharingAdapter.cs),
  `MaximumDirectoryFiles`, `MaximumDirectoryEncodedBytes`, and
  `ResolveDirectoryContentsAsync`

Required direction:

- Serialize the complete representable directory best effort.
- Stream or build the response without a second complete in-memory copy where the
  library boundary permits it.
- Return empty only for genuine absence/denial, not internal capacity.

### 5. Upload queue capacity can reject a legitimate folder transfer

Severity: high. Status: archived design and current implementation.

The scheduler rejects admission after 1,000 outstanding files for one normalized
username or 100,000 queued files globally. A peer legitimately requesting a large
folder can therefore receive capacity rejection based solely on file count.

Unlike a local download request, this boundary also faces untrusted remote peers, so
fairness and abuse resistance are real concerns. That does not make the chosen
numbers protocol validity rules. They need to be explicit operator policy, justified
by observed resource behavior, or replaced by a compact/durable scheduler
representation and fair backpressure.

Evidence:

- [`archive/sharing-uploads-design.md`](archive/sharing-uploads-design.md), settings
  and scheduler admission sections
- [`UploadScheduler.cs`](../../Sockseek.Core/Transfers/Uploads/UploadScheduler.cs),
  `MaximumQueuedUploads` and `MaximumQueuedUploadsPerUser`

### 6. Chat's arbitrary message limit acknowledges and discards valid data

Severity: high. Status: archived design and current implementation.

The chat design explicitly describes its 8 KiB UTF-8 message ceiling as an
abuse/resource bound rather than Soulseek's theoretical maximum. Over-bound inbound
messages are declared invalid, discarded, and private messages are acknowledged.
The sender therefore cannot replay them and the user cannot recover them.

Evidence:

- [`archive/chats-design.md`](archive/chats-design.md), “Inbound bounds and
  validation”
- [`ChatContracts.cs`](../../Sockseek.Core/Chat/ChatContracts.cs),
  `MaximumMessageUtf8Bytes` and `ValidateMessage`
- [`ChatRuntime.cs`](../../Sockseek.Server/ChatRuntime.cs), invalid private-message
  acknowledgement

Required direction:

- Enforce an actual protocol framing limit if one exists.
- Otherwise persist the body. If a presentation or notification needs truncation,
  truncate that projection and visibly mark it; do not acknowledge-and-discard the
  authoritative message.

### 7. Chat ingress capacity drops unreplayable room messages

Severity: high. Status: archived design and current implementation.

Chat uses a 1,024-item callback-to-worker channel. When it is full, private messages
remain replayable because they are not acknowledged, but room messages are dropped
and cannot be replayed. The design's stress qualification explicitly accepts this
loss as degraded health.

Bounded callback work is necessary, but loss of authoritative, unreplayable data is
not an acceptable pressure valve. A durable spill path, reserved critical lane, or
another earliest-safe-boundary strategy is needed. Bounded SignalR sender windows
are not the same problem because clients detect gaps and rehydrate durable state.

Evidence:

- [`archive/chats-design.md`](archive/chats-design.md), chat ingress pipeline and
  room-message handling
- [`ChatContracts.cs`](../../Sockseek.Core/Chat/ChatContracts.cs), `IngressCapacity`
- [`ChatRuntime.cs`](../../Sockseek.Server/ChatRuntime.cs), `TryWriteIngress` and
  room-message callback handling

### 8. Several failure boundaries reject more work than the bad entry requires

Severity: medium. Status: active/archived designs and current implementation.

These cases are not all resource estimators, but they express the same fail-closed
instinct at an unnecessarily broad scope:

- `DirectoryTransferPlan` rejects duplicate exact targets instead of canonicalizing
  or deduplicating them before execution.
- One invalid logical path component can reject a complete plan. Traversal and
  rooted components must never escape the output root, but an independently
  downloadable bad entry can be failed/skipped without losing unrelated entries.
- Sharing remote-key collision fails a complete staging generation, despite the
  sharing correction's rule to fail one entry or request where possible.
- One missing/inaccessible share root fails the complete staging scan and prevents
  unrelated roots from publishing an updated generation.

Evidence:

- [`resolved-remote-transfer-refactor-plan.md`](resolved-remote-transfer-refactor-plan.md),
  Core value tests
- [`DirectoryTransferPlan.cs`](../../Sockseek.Core/Remote/DirectoryTransferPlan.cs)
- [`archive/sharing-uploads-design.md`](archive/sharing-uploads-design.md), scan
  failure rules
- [`ShareScanCoordinator.cs`](../../Sockseek.Persistence/Sharing/ShareScanCoordinator.cs),
  root and remote-key-collision exception handling

The exact remediation depends on where canonicalization belongs. Security-invalid
paths must still be isolated and never placed. The important requirement is that an
unrelated valid entry/root not fail merely because it arrived in the same aggregate.

### 9. Identity byte ceilings lack cited Soulseek constraints

Severity: medium. Status: active design and current implementation; requires
protocol verification before changing.

Exact peer usernames and remote paths preserve wire spelling, which is correct, but
the shared validators impose 1,024-byte usernames and 16 KiB remote paths. The
design calls for rejecting over-limit identities without citing Soulseek framing or
the pinned library's actual representable limits.

These may be reasonable parser/allocation guards, but they must be derived from a
real wire/library boundary or moved to the boundary that needs them. A peer-provided
value already materialized by Soulseek.NET should not become an invalid download
target solely because Sockseek chose a smaller number.

Evidence:

- [`resolved-remote-transfer-refactor-plan.md`](resolved-remote-transfer-refactor-plan.md),
  exact-identity tests
- [`PeerFileTarget.cs`](../../Sockseek.Core/Remote/PeerFileTarget.cs),
  `PeerIdentityLimits`

### 10. Browse concurrency exposes internal capacity as request refusal

Severity: medium. Status: active design; user browsing is not yet implemented.

The user-browse design allows excess global browse work to receive
`429 browse-capacity`. A retryable shared-daemon capacity response is less harmful
than refusing a directory by size, but an arbitrary hidden queue depth still leaks
an implementation detail into otherwise valid user operations. Prefer a durable or
compact queued browse resource. If an operator-configured maximum wait queue is
necessary, document it as availability policy and include useful retry state.

Evidence:

- [`user-browsing-design.md`](user-browsing-design.md), “Concurrency and reuse” and
  the `browse-capacity` error contract

## Reviewed limits that are not findings

The audit deliberately does not classify every bounded value as defective:

- HTTP pagination and page-size limits preserve access to all underlying rows.
- Bounded live snapshots, tails, and SignalR send windows are sound when clients
  detect gaps and rehydrate authoritative state.
- Bounded diagnostic/error samples prevent secondary reporting from becoming the
  resource problem.
- Image byte, pixel, and decode-work limits protect against untrusted compressed
  image bombs; rejecting the image does not reject browsing/downloading the user's
  share.
- Traversal, containment, invalid framing, integer overflow, and malformed Unicode
  checks enforce correctness/security boundaries.
- The current full-share browse artifact defaults to `int.MaxValue` because the
  Soulseek peer-message frame length is a signed 32-bit value. Its oversize marker
  is therefore a real representation boundary, unlike the 8 MiB directory estimate.
- Search-result limits and excluded-phrase input bounds constrain protocol response
  policy or trusted-control input, not a user's request to download an exact known
  directory.
- Retention and cache-eviction policies are acceptable when the artifact is
  disposable/refreshable and active reader leases are honored.
- `archive/api-improvements-design.md` bounds projections and recovery snapshots,
  not authoritative work; no estimator-like finding was identified there.
- `archive/persistence-design.md` is empty.
- `archive/sharing-uploads-design.correction.md` is itself a prior audit that
  correctly rejects a filesystem allowlist, fixed free-space reserve, fragile
  fail-closed search readiness, and unmeasured extra gates. Its reasoning should be
  reused for the findings above.

## Recommended order

1. Remove `DirectoryTransferMemoryEstimator` and
   `DirectoryTransferAdmissionPolicy`, then make directory execution progressively
   scheduled and best effort.
2. Rewrite browse-ingress and browse-selection admission in
   `user-browsing-design.md` before implementation begins.
3. Remove the 10,000-file/8 MiB directory-share response refusal and re-evaluate
   upload scheduler capacity as explicit abuse/operator policy.
4. Stop chat's 8 KiB acknowledge-and-discard behavior and provide a non-lossy path
   for unreplayable room ingress.
5. Narrow whole-plan and whole-scan failures to the smallest independently invalid
   entry, root, or request.
6. Verify identity bounds against the pinned Soulseek protocol/library and retain
   only evidence-backed constraints.
