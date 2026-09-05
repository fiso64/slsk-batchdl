# WebUI daemon audit implementation plan

## Purpose

This is the executable sibling plan for `DAEMON-AUDIT.md` and
`DAEMON-AUDIT.comments.md`. It covers every accepted, deepened, narrowed, and
explicitly rejected item in the comments, including Core/CLI parity, storage,
API, migrations, observability, and behavioral tests. The plan is a live
requirements ledger: status and implementation notes must be updated as work
lands, and completion is not inferred merely from a green build.

The implementation is intentionally breaking where the comments call for one
owner to replace an existing unbounded contract. It must not leave permanent
parallel search projection, transfer-history, planning, or peer-restriction models.

## Status legend

- `[ ]` not started
- `[-]` in progress or partially satisfied
- `[x]` implemented and backed by the evidence named in the item
- `[?]` blocked on a product decision recorded in the decision ledger

## Baseline and non-negotiable rules

- [x] The current worktree and both audit documents were inspected before this
  plan was written.
- [x] Baseline tests pass: Core 793, Persistence 87, Server 176, CLI 289.
- [x] Avoid material warm-test regressions. Elapsed time is advisory in this
  environment; do not add polling, fixed waits, increased worker counts, or
  ordinary tests that are actually unmarked load/stress coverage.
- [x] Keep every public collection and repeated child collection cursor-paged;
  do not reject valid work because of an estimated aggregate size.
- [x] Stream, backpressure, spool, or persist work instead of accumulating an
  unbounded graph/response in memory.
- [x] Isolate source-row, result, transfer, and mutation failures to the
  smallest independent entry and report partial outcomes explicitly.
- [x] Keep logical feature owners inside the coarsest existing persistence
  lifecycle that preserves their consistency and failure isolation. Do not give
  each resource an independent database, migration path, health state, or
  pruning loop without a concrete independent lifecycle requirement. Optional
  feature degradation remains explicit and must not stop safe core operation.
- [x] Emit one coarse daemon lifecycle at the owning operation, with a stable
  ID, outcome, duration, and bounded safe counts. Terminal failures include the
  full exception exactly once at that owner. Do not log filenames, queries,
  usernames, chat text, or other per-item private/untrusted content.
- [x] Put settings composition, planning, condition evaluation, projection,
  grouping, ranking, and counters in Core. Server/CLI adapters may transport,
  persist, page, render, or parse those semantics but may not reimplement them.
- [x] Generate/update OpenAPI, API client usage, daemon docs, and WebUI generated
  contracts with each public breaking change. Delete superseded prototype-only
  `Proposed*` contracts once the generated contract is authoritative.
- [x] Add tests for observable behavior and failure isolation, not exact
  documentation wording or internal class layout. Tests remain quiet and use
  events/controllable clocks rather than polling or fixed waits.

## Decision ledger

No product choice is currently open. The choices discovered while writing the
plan were resolved as follows:

1. `[x]` **Peer-access merge.** One service merges configured username/IP blocks
   as a reloadable baseline with persisted exact-username `blocked`/`allowed`
   overrides. An explicit allowed override can unblock a configured username;
   removing it resets to configuration. Exact IP denial remains independent.
2. `[x]` **Dashboard Content meaning.** Match the slskd product meaning: rank
   shared directories successfully downloaded by remote peers (daemon Upload
   transfers), using a stable share-catalog directory key captured at creation.
   This is not cross-peer grouping of content downloaded by the daemon.
3. `[x]` **Preview lifetime.** Review uses a pageable, expiring, non-durable
   disk-backed temporary spool. Daemon restart may invalidate an uncommitted
   preview. Commit copies the exact reviewed plan and required provenance into
   the durable submission before releasing the spool.
4. `[x]` **Live Search View refresh.** Publish a cheap latest-revision/summary
   notification and have clients refetch visible pages and expanded groups. Do
   not retain or page a changed-ref feed.
5. `[x]` **Transfer timeline consistency.** Use a documented moving keyset over
   stable `(CreatedAtUtc, TransferId)` ordering rather than pinning an immutable
   snapshot across live, queued, and retained sources.
6. `[x]` **Accounting persistence.** Exact attempt-byte accounting shares the
   existing transfer persistence/write lifecycle. Report a simple availability
   or `completeFromUtc` boundary; add arbitrary gaps only for demonstrated
   non-contiguous loss.
7. `[x]` **Search definition authority.** `SearchDefinition` is one Core value
   model. Embed searches known at acceptance in the retained
   `SubmissionSpecification`/command; a search-capable child derived only after
   acceptance retains that same immutable value in its job execution record.
   Do not create a second public/configurable definition resource.

Resolved decisions that must not be reopened during implementation:

- interactive selection state is owned entirely by the client. There is no
  daemon selection resource, selection CRUD API, lease, or selection table.
  Preview/Search View commits carry a revision-bound `only`/`all-except`
  expression, item refs, and an idempotency key directly in an ordinary request
  DTO without an arbitrary item limit. Add streaming/spooling only if real
  payloads justify it; transient handling must never become an addressable
  selection. Durable retry/receipt ownership belongs to the resulting
  submission. This supersedes the server-owned selection recommendations in
  the audit comments;
- generic and music are the only built-in search-settings baselines; track and
  album retain distinct typed query/projection kinds but share music defaults;
- a result is preferred exactly when every configured preferred condition is
  satisfied; with none configured, every admitted result is preferred;
- uploaded artifacts are immutable and never receive CSV/List source mutation;
- a generic search directory is initially the exact containing directory of a
  matching file and uses the canonical peer-directory identity;
- search view cursors bind immutable view revisions; transfer timeline cursors
  use documented moving-keyset semantics over stable creation order;
- job and transfer removal is reversible archive; physical deletion remains a
  retention/purge concern;
- Job Preview is not a runtime job state or durable history. Review uses a
  temporary disk spool; local `--print jobs` needs neither daemon nor database;
- live activity remains ephemeral; no operational event journal is added;
- authentication, synthetic daemon health, folder-history trees, chat rail
  paging, and another Soulseek status field remain out of scope.

## Target ownership and dependency order

The implementation order prevents temporary public contracts from becoming
new permanent owners:

1. shared Core definitions/composition/planning/projection facts;
2. durable submission, artifact, search-input, and search-view storage plus the
   temporary preview spool;
3. replacement API resources plus local/remote CLI consumers;
4. transfer timeline/accounting and Dashboard analytics;
5. browse search and peer restrictions;
6. generated contracts/docs, end-to-end parity, performance, and deletion of
   superseded paths.

The logical owners/resources are:

- `Submission`: accepted intent, submission time, normalized command/effective
  settings, rerun lineage, semantic job roles, and archive unit.
- `JobPlanner` in Core: recursive, storage-agnostic planning stream shared by
  direct Start, daemon Review, and local print.
- `JobPreview`: expiring non-durable disk-backed daemon spool over planner
  records; the durable submission owns an accepted plan.
- `SearchView`: definition-stable revisioned projection, paging, nested
  collections, explanations, counters, and revision-bound selection
  resolution at commit; interactive selection state remains client-owned.
- `TransferTimeline`: newest-first union of authoritative live state and
  retained rows, deduplicated by transfer ID.
- `TransferAccounting`: logical owner of idempotent attempt byte activity,
  coverage, compact buckets/dimensions, and Dashboard range semantics inside
  the transfer persistence lifecycle.
- `BrowseArtifact`: ordinary browsing plus one indexed mixed global-search
  projection over the same immutable artifact.
- `PeerRestrictionService`: persisted exact-ordinal overrides and one immutable
  runtime snapshot containing independent upload-access and incoming-private-
  message policies, consumed only by their relevant inbound paths.

### Shared persistence topology

- [x] Bring durable submission/history, Search View, input-artifact metadata,
  transfer/accounting, and peer-override tables under the existing persistence
  host's initialization, migrations, health reporting, and maintenance
  coordination. Services retain logical ownership of their tables/contracts.
- [x] Remove the current pattern of independently initialized durable SQLite
  feature stores and per-feature availability platforms. A separate physical file may
  remain only when demonstrated write load, size, consistency, retention, or
  failure isolation makes it a genuinely better boundary; it still reuses the
  common lifecycle machinery.
- [x] Keep large immutable artifact payloads, external-sort work files, and
  active preview spools as owner-managed files where relational storage is the
  wrong representation. Durable metadata/lifecycle hooks participate in the
  common persistence maintenance coordinator. Per-generation share catalogs,
  completed peer-browse SQLite artifacts, the short-lived browse registry, and
  active preview spools retain independent physical lifetimes because they are
  immutable/temporary resources and remain usable when history persistence is
  disabled; they are not parallel domain-history stores.

## Phase 1 — shared settings, submission definitions, and introspection

### Core settings composition

- [x] Add a Core baseline discriminator with exactly `generic` and `music`.
- [x] Represent built-in baselines independently from operator/default/profile/
  request patches so generic submission never has to clear materialized music
  values.
- [x] Move the full composition order into one Core service: built-in baseline,
  operator default, default profile, matching auto-profiles, named profiles,
  launch patch, and request/job patch.
- [x] Keep the complete typed submission/query shape available to auto-profile
  matching; baseline is not a replacement for typed job context.
- [x] Make local CLI and daemon submissions use the same
  `SubmissionOptionsJobSettingsResolver` adapter over that composer. Remove
  duplicated condition-composition order once all callers migrate.
- [x] Preserve path normalization as an adapter-supplied finalizer without
  moving daemon filesystem knowledge into semantic composition.
- [x] Add Core tests proving generic defaults contain no implicit audio format,
  duration, MP3, bitrate, sample-rate, title, or album constraints while track
  and album get the same music baseline.
- [x] Add composition precedence/auto-profile tests using identical typed input
  for local and daemon adapters.

### Normalized definitions and safe introspection

- [x] Add a versioned normalized submission specification that records command
  kind/payload, typed query, effective settings needed by execution, and source
  revision without retaining unrelated secret configuration.
- [x] Define normalized `SearchDefinition` as a Core value object embedded in
  `SubmissionSpecification` and nested search commands. It contains baseline,
  typed query/default projection kind, network/default projection queries, and
  only effective admission/grouping/ranking settings.
- [x] Make the versioned retained submission the durable authority for searches
  known when accepted. For search-capable children that an extractor derives
  only after acceptance, retain the same immutable `SearchDefinition` value on
  the job execution record because it cannot be derived from the root command.
  Keep one model/codec, deterministic serialization, and schema rejection;
  view copies are internal projection/index data, not separately configurable
  definitions. Preserve exact Soulseek username/path spelling.
- [x] Remove the current persisted search-view reputation snapshot. Preserve
  legacy user-success effects within the active workflow, but persist only the
  resulting revision-bound projection sort keys/facts; do not introduce a
  shared or durable reputation resource in this audit.
- [x] Add the source-code `TODO [V4]` for deciding ownership, lifetime,
  persistence/decay, and ranking reproducibility of legacy user-success counts.
  V4 policy itself is not part of this audit.
- [x] Add a side-effect-free effective-settings endpoint that accepts the same
  typed submission shape and sparse options as real submission.
- [x] Return only UI-safe effective values, matched profile names, and per-field
  provenance (`built-in`, `operator-default`, `profile`, `request`). Never send
  secrets or make the UI reproduce profile resolution.
- [x] Prove in tests that introspection and accepted submission call the same
  resolver and return equal effective public values.

## Phase 2 — durable submissions, job semantics, history, and rerun

### Submission and job persistence

- [x] Add `Submission` storage with ID, `SubmittedAtUtc`, normalized command,
  effective settings/specification, optional `RerunOfSubmissionId`, preview/
  artifact provenance, lifecycle revision, and `ArchivedAtUtc`.
- [x] Add `SubmissionId` and stable role (`user-root`, `semantic-result`,
  `orchestration`, `execution-child`) to every daemon-produced job.
- [x] Assign role and submission identity in the planner/submission owner, not
  independently in DTO mappers.
- [x] Define `CreatedAtUtc` as the shared job-registration instant; feed the
  same timestamp into live state and persistence. Expose it in job summary and
  detail.
- [x] Keep `ParentJobId`, `ResultJobId`, and `SourceJobId` distinct. Do not use
  `SourceJobId` for rerun lineage.
- [x] Add `submissionId`/role filters to cursor-paged `/api/jobs` while keeping
  that endpoint the only runtime-job traversal collection.
- [x] Populate search job summaries identically live/historical with explicit
  `publicFileCount`, `lockedFileCount`, and `observedPeerCount`; document whether
  locked-only peers count. Keep view-dependent projected counts off jobs.
- [x] Migrate old rows conservatively: preserve existing job timestamps and
  relationships, mark legacy role/submission provenance explicitly rather than
  inventing false lineage, and keep historical detail readable.

### Archive and rerun

- [x] Add submission archive mutation, allowed only after all member jobs are
  terminal, setting `ArchivedAtUtc` and returning fixed-size affected/rejected
  counts and stable reason buckets.
- [x] Exclude archived submissions/jobs by default and support an explicit
  archive filter; never cascade-delete search results or null relationships.
- [x] Add rerun endpoint that clones retained normalized command and effective
  settings into a new submission with `RerunOfSubmissionId`.
- [x] The current UI has no “resolve with current defaults” rerun operation, so
  keep it deliberately absent. If product adds that behavior later, make it a
  separate explicit operation; never obtain it accidentally by reconstructing
  from current config.
- [x] Test delayed generated children (registration time differs from submission
  time), semantic navigation across pages, archive of mixed orchestration jobs,
  nonterminal rejection, restart persistence, and exact-settings rerun.

## Phase 3 — storage-agnostic Job Planner, artifacts, and Job Preview

### Shared Core planner

- [x] Extract recursive Extract/List/CSV planning into a Core `JobPlanner` that
  emits an async stream of fixed-size planned-node records and performs no
  Soulseek discovery/download.
- [x] Each record has stable planner ref, parent ref, role, typed command,
  normalized effective settings, provenance/source revision, child count/state,
  and independent failure information.
- [x] Preserve valid siblings when one row, nested URL, or source item fails;
  expose `partially-ready` when appropriate rather than replacing output with
  empty/failed wholesale.
- [x] Direct daemon Start consumes planner records into runtime submission
  without first creating a preview.
- [x] Local `--print jobs` and `--print jobs-full` stream those same records to
  presentation formatters without daemon/database use. A temporary spool may be
  used only if rendering needs a second pass.
- [x] Remote CLI `--print jobs` Review consumes daemon preview pages;
  client-local CSV/List inputs stream into immutable artifacts, and ordinary
  direct remote Start remains supported.
- [x] Delete private recursive planning logic from CLI and runtime submission
  after parity tests prove all three consumers share Core semantics.

### Immutable input artifacts

- [x] Add streaming daemon artifact upload with opaque ID, digest, safe metadata,
  atomic completion, expiry, and backpressure/disk spooling.
- [x] Move artifact metadata/maintenance from its independently initialized
  feature database into the shared persistence lifecycle; immutable payloads
  remain owner-managed files.
- [x] Do not trust browser filename as a path and do not impose a size limit
  absent explicit operator policy/representation constraint.
- [x] Make artifacts immutable; disable CSV/List source mutation for them.
- [x] Ensure expiry cleanup does not invalidate an active preview spool or a
  committed submission that still references the content; the submission
  becomes the durable pin/copy owner at commit.
- [x] Expose clear unavailable/degraded behavior without blocking direct local
  inputs or non-artifact submissions.

### Temporary disk-backed preview and client-owned commit selection

- [x] Replace the current durable preview tables/repository with an expiring,
  non-durable disk-backed temporary spool. It publishes state/revisions
  atomically and remains outside runtime history; daemon restart may invalidate
  every uncommitted preview.
- [x] Spool planner output, source digest/revision, stable refs, independent
  entry outcomes, and references to deduplicated immutable effective-settings
  records. Do not repeat complete settings on every node.
- [x] Keep preview summary/detail fixed-size and page roots/direct children by
  opaque cursor; do not embed repeated descendants.
- [x] Remove the current preview-selection resource, CRUD routes, leases, and
  persistence tables. Uncommitted Review checkbox state belongs only to the
  client and may be lost on reload/restart.
- [x] Make preview commit accept the immutable preview revision, an
  `only`/`all-except` expression with stable planner refs, and an idempotency
  key through an ordinary request DTO without an arbitrary item limit. Add
  incremental parsing/spooling only if real payloads justify it; never create
  an addressable selection resource.
- [x] Commit creates runtime jobs from the exact spooled plan and never reruns
  extraction, defaults, profile matching, or source read.
- [x] Return a fixed-size receipt with submission/workflow ref and requested,
  resolved, submitted, skipped, rejected counts plus bounded reason buckets.
- [x] Copy the accepted plan and necessary provenance into the durable
  submission before releasing the preview spool. Do not retain a committed
  preview merely for diagnostics.
- [x] If temporary spool creation is unavailable, fail Review clearly while
  direct Start and local planner/print continue.
- [x] Log preview create/complete/commit/expire lifecycle with preview ID,
  duration, outcome, and safe bounded counts.
- [x] Test mutable local source changes between preview and commit, exact-plan
  commit, artifact expiry/pinning, restart invalidation, partial-row failure,
  paged plans, client-owned `only`/`all-except` commit expressions, idempotent
  retry, and temporary-spool failure. Mark true scale/stress cases as `Load`.

## Phase 4 — retained search observations and shared projection kernel

### Search admission and retained input

- [x] Extend raw search admission to retain both `SearchResponse.Files` and
  presentation-safe `LockedFiles` rows with a visibility enum.
- [x] Preserve public/locked row identity and exact Soulseek spelling; selection
  resolution rejects locked rows with a stable reason.
- [x] Retain queue depth with upload speed/free-slot state in
  `SearchProjectionInput`, persistence, and API.
- [x] Model peer values as an observation with `ObservedAtUtc`, not as current
  user profile state, and use the same observation for ordering/display.
- [x] Resolve a search's default definition from its retained
  `SubmissionSpecification`/nested command when known at acceptance, otherwise
  from the immutable derived definition on its retained execution record.
  Reject conflicting accepted/execution values and never fall back to current
  daemon defaults. A view copy may remain only for immutable projection/indexing.
- [x] Migrate old search history as public-only with unavailable queue and
  explicit unknown/legacy definition state; never fabricate locked identities.
- [x] Populate public/locked/peer counters consistently for live and retained
  jobs, including the chosen locked-only peer rule.

### Core projection facts and incremental kernel

- [x] Define public preference condition identifiers as a documented small enum
  used by shared projection consumers, including local/remote
  `--print results-full`; the WebUI may render them later.
- [x] Evaluate admission and preferred-condition facts once per input when
  constructing the projected sort row. Reuse those facts for admission,
  lexicographic sort, exact preferred/other tier, and explanations; DTO mapping
  must not reevaluate conditions.
- [x] Enforce: all configured preferred conditions satisfied means `preferred`;
  any unsatisfied means `other`; no configured preferred conditions means every
  admitted row is `preferred`.
- [x] Promote existing incremental file/folder/aggregate-track/aggregate-album
  seams into one Core search-view kernel with typed projection definitions.
- [x] Feed only new raw sequences into the kernel; update sorted rows/groups,
  nested membership, exact counters, and byte aggregates in one
  atomic projection result.
- [x] Rebuild retained history by feeding the same kernel from sequence zero;
  remove the separate whole-list historical semantics.
- [x] Make local and remote `--print results`, `results-full`, JSON, and link
  renderers consume the kernel's projected facts. In particular,
  `results-full` formats condition explanations without reevaluating conditions.
  `--print index` and `index-failed` remain independent local-index inspection.
- [x] Add prefix-equivalence tests: after every batch, incremental rows/order/
  groups/tiers/counters equal a from-scratch run over that exact prefix; final
  completion changes only completeness and equals one-shot output.
- [x] Add local/daemon parity tests for identical definitions, raw observations,
  workflow-local user-success inputs, and every projection kind.

## Phase 5 — revisioned disk-backed Search View API

### View/revision model

- [x] Put search-view definition and revision tables under the shared
  persistence lifecycle, bound to source job,
  consumed raw sequence, normalized search/projection settings, filter/order,
  and the resulting projected sort keys/facts. Do not persist the workflow-local
  user-success input as a new reputation resource.
- [x] Remove the current persisted changed-ref feed while retaining atomic
  immutable revisions with exact totals, completeness, source/view revision,
  and raw-retention state. Persist only internal row/group changes needed for a
  new immutable revision; do not expose a change feed or copy the complete view
  for every batch.
- [x] Make a view available while search is live and publish the drained final
  revision with `isComplete`; do not switch to a completion-only recompute path.
- [x] Bind opaque cursors to view ID, immutable revision, collection kind,
  parent ref, order key, and tie-break identity; reject mismatch/stale/oversized
  cursors clearly.
- [x] Provide a cheap `afterRevision` summary poll or equivalent notification
  returning the latest revision and fixed-size counters. The client refetches
  only currently visible pages and expanded groups at that revision; do not
  retain/page changed refs or download the whole result set every second.
- [x] Permit coalescing unpublished work, but never expose new rows with stale
  order/groups/counters. Report retained-input coverage and degradation.
- [x] Use disk-backed/external sort and paged writes where a projection cannot
  remain compact; do not reject valid searches because of result count.

### Fixed-size top-level and nested collections

- [x] Replace the public whole-array `/results/files`, `/folders`,
  `/aggregate-tracks`, and `/aggregate-albums` contracts in one breaking change.
  Remove `includeFiles`, `includeCandidates`, and `includeFolders` switches from
  HTTP/OpenAPI. The CLI may privately collect fully paged results for one-shot
  rendering, but both local and remote adapters feed one shared presentation
  contract and own no projection semantics.
- [x] Page every top-level projection after complete-set filter/group/order and
  return exact totals/retention/completeness for the bound revision.
- [x] Expose stable preference tier and public condition matches on fixed-size
  result/representative rows for shared WebUI/local/remote CLI projection;
  never expose packed flags or scores.
- [x] Introduce one view-scoped `PeerDirectoryRefDto` based on Core
  `PeerDirectoryIdentity`, shared by generic and album directory projections.
- [x] Group generic results by exact containing directory. Summary includes
  public/locked matching counts and bytes, best-child relevance, peer
  observation, visibility, and exact-directory retrieval state.
- [x] Album directory summary distinguishes matching count/bytes from
  browse-authoritative retrieved total count/bytes.
- [x] Page directory children under the same view revision and return exact
  relative paths; clients derive subfolders without another identity model.
- [x] Aggregate group summaries use opaque view-scoped refs and fixed-size
  share count (distinct exact usernames), selectable option count, and one
  relevance-best representative.
- [x] Page one aggregate group's ordered alternatives. Album alternatives point
  to fixed-size directory summaries and then to paged children.
- [x] Generalize retrieve-folder and directory selection to
  `PeerDirectoryRefDto`, resolve directory and individual child-file refs in the
  issuing view revision, and reuse Core retrieval/download jobs without
  reprojecting current defaults. The UI may commit specific files rather than
  the whole directory.

### Client-owned Search View selection and direct commit

- [x] Remove the current Search View selection resource, CRUD routes, leases,
  and persistence tables. The UI owns `only`/`all-except` mode and its selected
  or excluded top-level refs while live revisions arrive.
- [x] Commit the client expression directly against one immutable view revision
  with a submission idempotency key using an ordinary request DTO without an
  arbitrary count/body limit. Add incremental parsing/spooling only if real
  payloads justify it; never turn transient parsing state into a server
  selection resource.
- [x] Allow directories/groups as single top-level refs; never enumerate all
  children/options through the client.
- [x] Return a fixed-size receipt containing the durable submission/workflow
  ref, requested/resolved/submitted/skipped/rejected counts, and bounded stable
  reason buckets. A retry with the same idempotency key returns the same
  submission receipt.
- [x] Resolve each selected entry independently; stale/locked/missing entries do
  not fail unrelated valid selections. Traverse created jobs through `/api/jobs`
  rather than embedding them in the response.
- [x] Test cursor immutability during live updates, latest-revision notification
  plus visible-page refresh, filter/order correctness before paging, unbounded
  nested populations, locked/stale selection isolation, both expression modes,
  idempotent commit retry, restart/rebuild, retention pruning, and view
  degradation. No test expects uncommitted selection state to survive UI or
  daemon restart.

## Phase 6 — live transfer parity and combined timeline

### Core transfer snapshot and hydration

- [x] Extend Core `TransferSnapshot` with scheduling/request/start/progress
  timestamps, authoritative speed, terminal outcome/failure/cancellation,
  operation/group ref, and optional reusable file metadata.
- [x] Populate the same generic fields for downloads and uploads at the Core
  snapshot/event boundary; server mappers must not estimate speed from observed
  deltas.
- [x] Advertise valid download cancellation on transfer rows while routing the
  command to the job/transfer owner; upload rows use `UploadCoordinator`.
- [x] Include already-active non-queued uploads in initial daemon snapshot even
  with null workflow ID. Keep queued upload paging as-is.
- [x] Test an upload active before snapshot, later deltas/removal without
  duplicates, live download speed/timestamps/actions, and null metadata for
  genuine unknown/fallback cases.

### Metadata persistence and timeline

- [x] Persist the same `FileMetadataDto` projection on retained transfers for
  both directions; do not create a transfer-only near-duplicate.
- [x] Add `ArchivedAtUtc` and exclude archived transfer rows by default.
- [x] Make `/api/transfers` the combined newest-first timeline over live,
  queued, and retained rows ordered by stable `(CreatedAtUtc, TransferId)`.
- [x] Overlay authoritative live state and deduplicate by transfer ID per
  request. Use a moving keyset bound only to the stable creation-order boundary;
  new rows may appear above an existing traversal and mutable status updates do
  not reorder it. Do not materialize/pin a combined timeline snapshot.
- [x] With persistence disabled/degraded, return live rows and explicit retained
  coverage state rather than an empty complete-looking history.
- [x] Retain authoritative operation/job/group ref so UI grouping never relies
  on peer/path-prefix coincidence.
- [x] Add scoped bulk cancellation by direction and `all`/`queued`/
  `in-progress` target snapshot. Execute targets independently and return
  resolved/succeeded/already-terminal/rejected/failed counts and reason buckets.
- [x] Add individual/filtered terminal archive with the same failure-isolated
  receipt shape but a distinct command/precondition from cancellation.
- [x] Test live/persisted overlap at moving-keyset page boundaries, concurrent
  inserts/status changes under the documented consistency contract, persistence
  outage, bulk race/failure isolation, and reversible archive.

## Phase 7 — transfer accounting and Dashboard analytics

### Idempotent accounting within transfer persistence

- [x] Add a transfer-accounting owner that consumes cumulative per-attempt
  progress revisions and computes non-negative transport-byte deltas keyed by
  transfer/attempt/revision.
- [x] Persist checkpoints, compact time buckets, direction, exact username,
  stable share-directory key, completion, and stable failure reason dimensions
  in batches through the existing transfer persistence/write lifecycle.
- [x] Handle retries/resets, resume, terminal snapshots, replay, and restart
  without double-counting or losing the final delta.
- [x] Do not reuse today's coalescible/droppable progress inbox unchanged.
  Extend the shared transfer write/outbox path with durable checkpoints,
  batching, and backpressure; do not add a separate accounting handoff,
  database, migration path, or health platform.
- [x] Capture the stable share-catalog directory key and presentation path at
  upload creation without exposing the configured local root.
- [x] Treat accounting degradation as optional: transfers continue, analytics
  reports unavailable/partial coverage with a simple availability or
  `completeFromUtc` boundary and logs rate-limited degradation rather than
  returning zero as complete. Add arbitrary gap intervals only if demonstrated
  failure modes can create non-contiguous loss.

### Bounded range response

- [x] Add one bounded Dashboard analytics endpoint for 24h, 7d, 30d, 90d, 1y,
  and All, with fixed bucket count, fixed summary, bounded top-N peer/content/
  error rankings, `accountingVersion`, and a simple coverage/availability
  boundary.
- [x] Define bandwidth buckets and share ratio as positive per-attempt transport
  bytes by direction during the range.
- [x] Define downloaded/uploaded file counts as successful logical transfers
  completed in the range.
- [x] Define distinct peers as exact usernames with transport byte activity in
  the range.
- [x] Define content ranking as successful logical uploads completed in the
  range, grouped by stored shared-directory identity and reporting file count
  plus distinct requesting peers (downloads by peers in UI language).
- [x] Define errors as terminal failed attempts completed in the range, grouped
  by stable reason code rather than exception text.
- [x] Apply the same populations and independent coverage to comparison periods.
  `All` means all retained accounting coverage, not implied all-time history.
- [x] Test retries, resets, crossing range boundaries, restart/replay,
  completion/error populations, coverage boundary/unavailability, retention,
  exact username spelling, bounded top-N/buckets, and shared-persistence
  degradation.

## Phase 8 — mixed browse search

- [x] Keep the immutable browse artifact as owner and add one auxiliary indexed
  normalized search representation (appropriate SQLite FTS/trigram strategy)
  inside the same persistence boundary, pointing back to exact stored
  directory/file rows. Design for roughly 100,000 files and a sub-300 ms
  interactive target without adding a separate service/store.
- [x] Add one flat cursor-paged mixed search over the complete artifact, not a
  recursive tree page and not a duplicate ordinary browse API.
- [x] Return fixed-size directory/file rows with exact refs/spelling,
  visibility, display path/breadcrumb context, and public/locked matching
  counts/bytes.
- [x] Return exact matching-file totals before paging and bind cursor to browse
  artifact/revision so refresh cannot reorder an existing traversal.
- [x] Keep ordinary parent-directory and per-directory file endpoints unchanged
  as the non-search navigation primitive.
- [x] Test global file matches without directory fan-out, ancestor context,
  visibility aggregates, special/unicode/exact spelling, page boundaries,
  refreshed artifacts, query-plan/index use, and large artifacts without
  unbounded materialization.

## Phase 9 — independent mutable peer restrictions

- [x] Replace immutable startup-only peer blocking with one daemon policy that
  publishes an atomic snapshot containing independent upload-access and
  incoming-private-message dimensions.
- [x] For each dimension, merge reloadable configured exact usernames with
  persisted exact-ordinal `blocked`/`allowed` overrides. An `allowed` override
  supersedes only that configured username baseline; removal resets only that
  dimension. Keep exact configured IP denial upload-only and authoritative when
  an endpoint is known.
- [x] Add one per-user read/mutation resource with an explicit restriction kind;
  Chat's Block/Unblock action uses `private-messages`, while Users may expose
  both kinds.
- [x] Project `uploadAccessBlocked` and `privateMessagesBlocked` into profiles,
  and only `privateMessagesBlocked` into direct-conversation summaries, without
  per-row fan-out.
- [x] Apply upload restrictions only to future inbound share search, browse,
  directory, and upload admissions. Apply private-message restrictions only to
  future incoming DMs. Do not block outbound profiles/browses/downloads, room
  messages, or outgoing DMs, and do not cancel transfers or delete history.
- [x] Preserve exact ordinal username spelling and make mutations durable across
  restart. Isolate persistence failure and return a clear non-applied outcome.
- [x] Store peer-restriction overrides in the main persistence database and
  reuse its migrations, ownership, backup, health, and maintenance lifecycle;
  retain one logical policy owner and failure-isolated mutation contract.
- [x] Test the two dimensions independently, configured allow overrides, exact
  case/spelling, IP precedence, restart, concurrent mutation/read, conversation
  and profile hydration, outbound non-effects, and persistence degradation.

## Phase 10 — API/UI/CLI cutover and removal of superseded paths

- [x] Update public API DTOs/routes/client in one cutover for search views;
  remove whole-array projection endpoints/HTTP DTOs and include switches. Keep
  the CLI's private completed-print adapter shared between local and remote.
- [x] Update the fixture-backed WebUI prototype adapters to consume generated
  production contracts, revision notifications, visible-page refresh,
  fixed-size summaries, and client-owned selection expressions plus fixed-size
  commit receipts. Remove `Proposed*` duplicates. Live HTTP/SignalR wiring is
  deliberately outside this implementation slice.
- [x] Update remote CLI for preview paging, revised search-view paging, combined
  timeline/archive/rerun where commands exist, while retaining local Core paths.
- [x] Update local CLI print formatters only as required by shared Core records;
  confirm no semantic filter/rank/group/count logic remains in CLI.
- [x] Regenerate `docs/openapi.json` and update `docs/api.md`/`docs/daemon.md` for
  immutable Search View revision/cursor semantics, moving transfer keysets,
  retention/coverage, archive, artifacts, temporary preview lifetime, analytics
  populations, and independent peer-restriction mutation effects.
- [x] Remove orphaned repository readers, DTO mappers, persistence mutations,
  settings paths, and compatibility aliases after callers migrate.
- [x] Confirm deliberate non-gaps remain absent: recursive job/search/browse
  trees, synthetic health, event journal, auth, duplicate presence/status,
  daemon-owned transfer folder cards, and an index-print daemon path.

## Extra notes from me (user)

I add some things I forgot here.

- [x] Model the two meanings of a "blocked user" independently. Upload-access
  blocking and incoming-private-message blocking now have explicit Core/config/
  CLI/API names and independent mutations; Chat's three-dot Block action maps to
  private messages only.

## Phase 11 — verification and completion audit

### Behavioral suites

- [x] Core: settings baseline/composition, planner failure isolation, one-pass
  public conditions, every-prefix incremental equality, transfer progress
  facts/accounting deltas, and local/daemon semantic parity.
- [x] Persistence: migrations from the previous schema, cursor/index plans,
  artifact/view expiry and restart, temporary preview spool cleanup/invalidation,
  removal of obsolete durable preview/selection tables, archived filtering,
  accounting replay/coverage, browse search index, and peer policy durability.
- [x] Server: API fixed-size/page behavior, live revision consistency,
  shared-persistence and temporary-spool degradation, receipts/reason buckets,
  lifecycle logging, and initial active-upload hydration.
- [x] CLI: local planner/projection works without database/daemon, remote paging
  exhausts cursors correctly, and render modes differ only in presentation.
- [x] Contract: OpenAPI contains replacement resources and no superseded
  whole-array/include-switch/recursive shapes.
- [x] WebUI contract adapter: reacts to a newer revision by refetching visible
  pages and expanded groups and keeps rows plus counters on one immutable
  revision. The prototype remains fixture-backed; live transport wiring is not
  a gate for this slice.

### Required gates

- [x] `dotnet build --no-restore` succeeds without new warnings.
- [x] Targeted tests pass after each phase.
- [x] Warm `dotnet test --no-restore` passes without a material regression; no
  worker increase, sequential-host-guard removal, polling/fixed waits, or
  unmarked load/stress tests were introduced.
- [x] Tests emit no incidental application logs.
- [x] `git diff --check` passes.
- [x] Generated OpenAPI is current.
- [x] Search confirms removed public contracts/parallel semantic owners have no
  production callers.
- [x] Requirement-by-requirement audit links every checkbox above to current
  code/API/schema/runtime/test evidence; uncertainty counts as incomplete.

### Completion evidence index

The behavioral wording in each checkbox remains the requirement. The entries
below bind every checkbox in the named section to its current implementation
and behavioral evidence; the final command evidence is recorded separately so
a green build is not being used as a substitute for this mapping.

- **Baseline rules and persistence topology:** Core owns semantics under
  [`Planning`](../Sockseek.Core/Planning), [`SearchProjection`](../Sockseek.Core/Search/SearchProjection),
  and [`Settings`](../Sockseek.Core/Settings); the shared SQLite lifecycle is
  [`PersistenceRuntimeHost.cs`](../Sockseek.Persistence/Runtime/PersistenceRuntimeHost.cs).
  [`PersistenceArchitectureTests.cs`](../Sockseek.Server.Tests/PersistenceArchitectureTests.cs),
  [`LoggingArchitectureTests.cs`](../Sockseek.Core.Tests/LoggingArchitectureTests.cs),
  and the phase suites below cover provider boundaries, generated logging,
  paging/bounded representations, degradation, and entry-level isolation.
- **Phase 1:** [`JobSettingsComposer.cs`](../Sockseek.Core/Settings/JobSettingsComposer.cs),
  [`SearchDefinition.cs`](../Sockseek.Core/Search/SearchDefinition.cs), and
  [`EffectiveSettingsMapper.cs`](../Sockseek.Server/EffectiveSettingsMapper.cs)
  are exercised by [`JobSettingsComposerTests.cs`](../Sockseek.Core.Tests/JobSettingsComposerTests.cs),
  [`SubmissionSpecificationTests.cs`](../Sockseek.Core.Tests/SubmissionSpecificationTests.cs),
  and [`CliJobSettingsParityTests.cs`](../Sockseek.Cli.Tests/CliJobSettingsParityTests.cs).
- **Phase 2:** [`SubmissionStore.cs`](../Sockseek.Persistence/Read/SubmissionStore.cs),
  the submission migrations in [`Migrations`](../Sockseek.Persistence/Migrations),
  and [`SubmissionCommitCoordinator.cs`](../Sockseek.Server/Persistence/SubmissionCommitCoordinator.cs)
  are covered by [`PersistenceDaemonTests.cs`](../Sockseek.Server.Tests/PersistenceDaemonTests.cs),
  [`SubmissionCommitCoordinatorTests.cs`](../Sockseek.Server.Tests/SubmissionCommitCoordinatorTests.cs),
  and [`HistoricalQueryFacadeTests.cs`](../Sockseek.Server.Tests/HistoricalQueryFacadeTests.cs).
- **Phase 3:** [`JobPlanner.cs`](../Sockseek.Core/Planning/JobPlanner.cs),
  [`JobPreviewStore.cs`](../Sockseek.Persistence/Planning/JobPreviewStore.cs),
  and [`InputArtifactStore.cs`](../Sockseek.Persistence/Planning/InputArtifactStore.cs)
  are covered by [`JobPlannerTests.cs`](../Sockseek.Core.Tests/JobPlannerTests.cs),
  [`JobPreviewStoreTests.cs`](../Sockseek.Persistence.Tests/JobPreviewStoreTests.cs),
  [`InputArtifactStoreTests.cs`](../Sockseek.Persistence.Tests/InputArtifactStoreTests.cs),
  [`JobPreviewTests.cs`](../Sockseek.Server.Tests/JobPreviewTests.cs), and the
  local/remote preview cases in [`RemoteCliBackendTests.cs`](../Sockseek.Cli.Tests/RemoteCliBackendTests.cs).
- **Phases 4–5:** [`SearchViewKernel.cs`](../Sockseek.Core/Search/SearchProjection/SearchViewKernel.cs),
  [`SearchViewStore.cs`](../Sockseek.Persistence/Planning/SearchViewStore.cs), and
  [`SearchViewCoordinator.cs`](../Sockseek.Server/Planning/SearchViewCoordinator.cs)
  are covered at every-prefix, store/cursor/restart, live publication, nested
  paging, direct selection, and CLI parity boundaries by
  [`SearchViewKernelTests.cs`](../Sockseek.Core.Tests/SearchViewKernelTests.cs),
  [`SearchViewStoreTests.cs`](../Sockseek.Persistence.Tests/SearchViewStoreTests.cs),
  [`SearchViewCoordinatorTests.cs`](../Sockseek.Server.Tests/SearchViewCoordinatorTests.cs),
  [`SearchViewCursorCodecTests.cs`](../Sockseek.Server.Tests/SearchViewCursorCodecTests.cs),
  and [`CliSearchViewParityTests.cs`](../Sockseek.Cli.Tests/CliSearchViewParityTests.cs).
- **Phase 6:** the authoritative snapshot/event boundary in
  [`CoreSnapshots.cs`](../Sockseek.Core/Snapshots/CoreSnapshots.cs), the live
  union in [`HistoricalQueryFacade.cs`](../Sockseek.Server/Persistence/HistoricalQueryFacade.cs),
  and retained transfer storage in [`PersistenceWriter.cs`](../Sockseek.Persistence/Write/PersistenceWriter.cs)
  are covered by [`DownloadEventsTests.cs`](../Sockseek.Core.Tests/DownloadEventsTests.cs),
  [`UploadCoordinatorTests.cs`](../Sockseek.Core.Tests/UploadCoordinatorTests.cs),
  [`PersistenceWriterTests.cs`](../Sockseek.Persistence.Tests/PersistenceWriterTests.cs),
  and [`PersistenceDaemonTests.cs`](../Sockseek.Server.Tests/PersistenceDaemonTests.cs).
- **Phase 7:** accounting checkpoints/buckets share
  [`PersistenceWriter.cs`](../Sockseek.Persistence/Write/PersistenceWriter.cs),
  while [`TransferAnalyticsReader.cs`](../Sockseek.Persistence/Read/TransferAnalyticsReader.cs)
  and [`DashboardAnalyticsFacade.cs`](../Sockseek.Server/Persistence/DashboardAnalyticsFacade.cs)
  own the bounded query. Their replay, range, reset, retention, and degradation
  cases are in [`PersistenceWriterTests.cs`](../Sockseek.Persistence.Tests/PersistenceWriterTests.cs)
  and [`PersistenceDaemonTests.cs`](../Sockseek.Server.Tests/PersistenceDaemonTests.cs).
- **Phase 8:** [`PeerBrowseArtifactStore.cs`](../Sockseek.Persistence/PeerBrowsing/PeerBrowseArtifactStore.cs)
  and [`PeerBrowseService.cs`](../Sockseek.Server/PeerBrowsing/PeerBrowseService.cs)
  are covered by [`PeerBrowseArtifactStoreTests.cs`](../Sockseek.Persistence.Tests/PeerBrowseArtifactStoreTests.cs),
  [`PeerBrowseServiceTests.cs`](../Sockseek.Server.Tests/PeerBrowseServiceTests.cs),
  and [`UserBrowseApiTests.cs`](../Sockseek.Server.Tests/UserBrowseApiTests.cs),
  including FTS query-plan and marked large-artifact cases.
- **Phase 9:** [`PeerRestrictionPolicy.cs`](../Sockseek.Core/Sharing/PeerRestrictionPolicy.cs),
  [`PeerRestrictionOverrideStore.cs`](../Sockseek.Persistence/PeerRestrictions/PeerRestrictionOverrideStore.cs),
  and [`PeerRestrictionCoordinator.cs`](../Sockseek.Server/PeerRestrictions/PeerRestrictionCoordinator.cs)
  are covered by [`PeerRestrictionOverrideStoreTests.cs`](../Sockseek.Persistence.Tests/PeerRestrictionOverrideStoreTests.cs),
  [`PeerRestrictionCoordinatorTests.cs`](../Sockseek.Server.Tests/PeerRestrictionCoordinatorTests.cs),
  [`ChatRuntimeTests.cs`](../Sockseek.Server.Tests/ChatRuntimeTests.cs), and the
  sharing adapter/coordinator suites.
- **Phase 10 and contracts:** routes and DTOs are authoritative in
  [`ServerHost.cs`](../Sockseek.Server/ServerHost.cs) and [`Contracts`](../Sockseek.Api/Contracts);
  [`OpenApiContractTests.cs`](../Sockseek.Server.Tests/OpenApiContractTests.cs)
  proves replacement resources and removed public projection shapes.
  [`generated.ts`](src/api/generated.ts) feeds the fixture-backed adapters;
  `bun run api:generate`, `bun run check`, and `bun run build` prove generation,
  typing, and production bundling without claiming live transport wiring.

## Implementation log

- 2026-08-29: Plan created from the current repository, original audit, and
  backend comments. Baseline solution tests pass (1,345 total) but require
  53.160 seconds on its first measured run. The user later made elapsed time
  advisory for this environment and retained the prohibition on new waits,
  polling, or unmarked load tests.
- 2026-08-29: The next warm solution run completed in 28.852 seconds but had one
  transient Server failure while all four test projects overlapped. The Server
  suite immediately passed all 176 tests alone in 11.181 seconds. Do not call
  the baseline stable until repeated full warm runs pass; investigate
  cross-project contention if the failure repeats.
- 2026-08-29: Product decisions resolved. Peer restrictions use configured
  baselines plus persisted allow/block overrides under one owner. Dashboard Content ranks
  successful uploads by stable shared-directory identity, matching slskd's
  user-facing meaning while retaining Sockseek's stronger accounting semantics.
- 2026-08-29: Completed the shared settings-composition slice. Core now owns
  generic/music baseline selection and the complete sparse-layer precedence;
  local and daemon request adapters use it before auto-profile matching. Added
  neutral-generic, shared-music, precedence, request-auto-profile, and adapter
  parity coverage. Focused Core (4), CLI (11), and Server supervisor (30) tests
  pass; `dotnet build --no-restore` and `git diff --check` pass.
- 2026-08-30: Added first-class durable submissions and semantic job ownership.
  Accepted intent is persisted before runtime enqueue; jobs expose submission,
  role, and shared creation time; archive is reversible and terminal-only; rerun
  uses the retained normalized command/settings with explicit lineage. Added the
  schema migration, paged submission API/client, archived job filtering, restart,
  nonterminal rejection, and exact-settings rerun coverage.
- 2026-08-30: Completed the shared-planner/Job Preview backend slice. Core
  `JobPlanner` now supplies direct daemon Start and persistence-free local
  `--print jobs`; planned extraction results, per-node settings, search
  definitions, and source revisions survive serialization/rerun without a
  second source read. Review currently uses an independent expiring SQLite
  resource with an asynchronous worker, revisioned bounded rows, authenticated
  cursors, fixed receipts, and restart recovery. Product review subsequently
  rejected durable preview persistence and its separate lifecycle: replace it
  with the temporary spool specified above. Its initial disk-selection
  implementation was also superseded and is scheduled for removal.
- 2026-08-30: Added immutable streamed input artifacts with opaque IDs, SHA-256,
  safe filename metadata, atomic disk publication, bounded upload concurrency,
  expiry/pins, direct-Start and Preview resolution, and forced suppression of
  CSV/List source mutation. Focused Core, Persistence, Server, and local CLI
  tests cover mutable-source commit, artifact provenance/pinning, partial plans,
  the then-current selection/restart behavior, optional Preview failure, and
  local print with no database; selection coverage must be replaced with the
  client-owned direct-commit contract.
- 2026-08-30: Implemented the first revisioned Search View slice for file rows.
  Raw admission now retains public and locked observations, queue depth, one
  observation timestamp, and exact peer/count facts without lossy count caps.
  Core evaluates admission and public preference facts once, with `preferred`
  defined exactly as all configured preferred conditions satisfied. A separate
  SQLite/WAL view store owns immutable revisions, external ordering, fixed-size
  counters, keyset pages, a bounded revision-bound change feed, expiry, frozen
  definitions, and restart recovery. Product review subsequently rejected the
  changed-ref feed and the frozen reputation snapshot; both must be removed.
  The live daemon and local file result renderer feed the same Core kernel;
  daemon projection does not retain a second in-memory copy of projected rows.
  Event-driven tests cover every-prefix
  equivalence, live pre-completion pages/counters, completion without recompute,
  cursor restart/tamper/boundary checks, immutable paging during updates, daemon
  restart, and local locked-row parity. Folder and aggregate kernels, nested
  paging/selection, remote CLI cutover, and removal of old whole-array routes
  remain deliberately unchecked.
- 2026-08-31: Replaced startup-only peer denial with one atomic
  `PeerRestrictionPolicy` containing independent upload-access and incoming-DM
  dimensions. The exact-username override table now shares the main persistence
  migration/health/backup lifecycle. Config/CLI/API names select one dimension;
  profiles expose both and conversations expose only the DM state. Upload denial
  affects only inbound sharing, DM denial discards only incoming private messages,
  and outbound profiles/browses/downloads/chat plus room messages remain
  unaffected. Persistence commits before snapshot publication and failures leave
  the prior policy active.
- 2026-08-30: Corrected selection ownership after product review. Interactive
  Preview/Search View selection is client state, not a leased or persisted
  daemon resource. Commits will carry revision-bound `only`/`all-except` refs
  and a submission idempotency key directly. Existing preview/search selection
  tables and CRUD routes are temporary, now explicitly scheduled for removal;
  durable Search Views and submissions remain the respective authorities, while
  active previews move to a non-durable temporary disk spool.
- 2026-08-31: Accepted the simplicity review. The plan now removes persisted
  changed-ref feeds, exact combined-timeline snapshots, durable Job Preview,
  independently authoritative SearchDefinition storage, persisted reputation
  inputs, and a separate accounting handoff. Visible Search View pages refresh
  by latest revision; transfers use moving keysets; accounting shares transfer
  persistence; independently initialized feature stores are consolidated under
  common lifecycle machinery unless a concrete boundary justifies otherwise.
- 2026-08-31: Removed daemon-owned Preview/Search View selection resources,
  CRUD routes, and tables. Both commits now accept an exact revision plus a
  client-owned `only`/`all-except` expression and submission idempotency key;
  durable submission receipts survive daemon restart. Job Preview now uses a
  per-daemon temporary disk spool, restart invalidates uncommitted previews,
  and successful commit releases the preview after durable submission
  acceptance. Search Views no longer persist reputation input or a changed-ref
  log; live workflow counts remain in memory, and clients poll one fixed-size
  latest summary before refetching visible pages. Focused persistence and
  server tests cover both selection modes, missing/locked isolation, retry
  conflict, receipt restart, preview invalidation/release, immutable paging,
  and summary-only live refresh.
- 2026-08-31: Completed the shared search-definition and one-pass presentation
  cutover. Accepted searches resolve from their immutable submission; search
  children created only after extractor execution retain the same typed
  definition in their job record, and conflicting copies are rejected rather
  than falling back to current daemon defaults. File/folder/aggregate kernels
  share incremental prefix semantics and persisted projection facts. Local and
  remote `results-full` now format those facts without evaluating conditions a
  second time; focused tests prove the formatter follows retained facts and
  completed remote CLI results still reproject after the live job retires.
- 2026-08-31: Cut remote CLI search-result reads over to immutable, cursor-paged
  Search Views for file, directory, aggregate-track, and aggregate-album
  projections. The eight old whole-array HTTP routes and matching API-client
  methods are gone, OpenAPI contract tests assert their absence, and local mode
  still runs the same Core projection kernel with no daemon or database.
  `results-full` uses retained public condition facts rather than reevaluating
  conditions. `docs/api.md` and `docs/daemon.md` now describe revision polling,
  visible-page refresh, and local/remote semantic ownership. The generated
  WebUI TypeScript client and remaining internal legacy projection DTOs were
  deliberately left visible as unfinished cutover work at this checkpoint.
- 2026-08-31: Historical job pages/details now join retained search metadata so
  raw public-file, locked-file, and distinct-peer discovery counts match live
  summaries; distinct peers explicitly include peers seen only through locked
  files. Focused persistence and server mapper tests cover list, ID, and display
  ID reads. Root runtime preparation now uses the same non-inheriting settings
  boundary as `JobPlanner`, while extractor-produced children alone inherit
  parent search constraints; this fixed the remaining CLI auto-profile test.
- 2026-08-31: Completed live transfer parity and the combined moving-keyset
  timeline. Core download and upload boundaries now retain authoritative
  request/start/progress time, protocol speed, terminal facts, reusable file
  metadata, and job/group ownership; pre-existing workflow-less uploads hydrate
  daemon snapshots without duplicate deltas. `/api/transfers` overlays live,
  queued, and retained rows by stable creation key and reports retained coverage
  explicitly. Transfer metadata and reversible archive share the main SQLite
  lifecycle, while terminal upload retirement now awaits an exact persistence
  acknowledgement instead of sleeping and polling. Direction/state-scoped bulk
  cancellation and individual/filtered archive use bounded receipts with
  per-target cancellation isolation. Focused Core, persistence, server, API,
  migration, cursor, outage, race, and archive tests pass.
- 2026-08-31: Completed transfer accounting and the bounded Dashboard API.
  Download and upload adapters now attach cumulative attempt observations to
  the existing transfer mutations; the shared inbox compacts them by
  attempt/five-minute bucket while preserving reset boundaries and absorbs all
  buffered observations into terminal writes. The main SQLite transaction
  advances idempotent attempt checkpoints, exact-username/direction byte
  buckets, transfer/attempt outcomes, and captured upload directory identity
  plus display path. Retention and unclean/unhealthy restart advance one
  contiguous `completeFromUtc` boundary. `/api/dashboard/analytics` supplies
  bounded 24h/7d/30d/90d/1y/All buckets, summaries, peer/content/error top-N,
  and independently covered comparison summaries under accounting version 1;
  disabled, degraded, unhealthy, partial, and read-failure states cannot look
  like complete zeroes. Focused migration, replay/reset, boundary, retention,
  restart, range, OpenAPI, and .NET-client tests pass.
- 2026-08-31: Completed mixed search over immutable remote-user browse
  artifacts. Each newly completed artifact now contains one artifact-local
  SQLite FTS5 trigram representation pointing back to exact directory/file
  rows; exact ordinal-ignore-case post-filtering defines public substring
  semantics, short queries fall back to an artifact scan, and broad-query
  temporary state is disk-backed. The new flat cursor API returns matching
  files/directories plus ancestors, fixed-size metadata and display paths,
  per-directory and whole-query public/locked counts and bytes, and binds
  continuation to browse ID, immutable revision, normalized query, kind, and
  row ref. Ordinary tree browsing and exact file/folder download selection are
  unchanged. Focused persistence/API/client/OpenAPI tests cover global matches,
  Unicode and punctuation, ancestor aggregates, page boundaries, refreshed
  artifacts, FTS query-plan use, and the existing marked large-artifact fixture.
- 2026-09-01: Consolidated input-artifact metadata/pins and Search View
  definitions/revisions into `sockseek.db`. Both now use the persistence host's
  migrations, ownership, backup, health, and retention lifecycle; immutable
  input bodies remain files and Job Preview remains a per-session temporary
  spool. Removed the former `artifacts.db`/`search-views.db` schema owners and
  added migration, backup/restore, startup-topology, restart, and focused store
  coverage. Immutable share catalogs/peer-browse databases and the short-lived
  browse registry remain generation resources rather than parallel durable
  history stores.
- 2026-09-01: Regenerated the WebUI TypeScript API from current OpenAPI, removed
  duplicate `Proposed*` contracts, and converted the fixture-backed adapters to
  production DTO shapes. The revision refresh helper refetches only visible
  pages and expanded groups against one immutable revision. `svelte-check` and
  the production Vite build pass; live HTTP/SignalR wiring remains explicitly
  outside this slice.
- 2026-09-01: Cut remote `--print jobs`/`jobs-full` over to the expiring Job
  Preview resource while preserving direct Start for ordinary remote commands
  and the database-free Core planner for local print. The remote CLI now streams
  client-local CSV/List files into immutable daemon artifacts for both Review
  and direct Start, recursively exhausts cursor-paged preview nodes, and reuses
  the existing job presentation formatter. A page-boundary integration test
  verifies 205 planned jobs render without creating runtime jobs or mutating the
  client CSV.
- 2026-09-04: Completed temporary Preview representation cleanup. Planned nodes
  now reference deduplicated immutable effective-settings records in the
  daemon-session SQLite spool; mutable-source commit, artifact pinning/expiry,
  partial rows, restart invalidation, page boundaries, both client-owned
  selection expressions, idempotent retry, and feature-level storage failure
  have focused coverage. Direct Start and local print do not create or depend on
  this resource.
- 2026-09-04: Completed Search View nested selection and memory cleanup.
  Revision-bound commit resolves aggregate alternatives, album directory
  options, and individual public children directly from persisted membership,
  skips children whose selected container already covers them, and isolates
  locked/missing refs. Daemon file projection no longer retains a redundant
  result-sized admission set when raw sequence durability is authoritative. A
  205-child directory test crosses the page boundary without a collection cap.
- 2026-09-04: Finished the public search projection cutover. Obsolete
  whole-array request/snapshot DTOs moved out of `Sockseek.Api` into one private
  adapter shared by local and remote CLI renderers; the Server's orphaned live
  and historical whole-array projection methods were removed. OpenAPI exposes
  only immutable Search View pages and contains no nested include switches.
  Actual local/remote integration coverage compares all four projection kinds,
  and Core coverage compares identical workflow-local user-success inputs.
- 2026-09-04: Final architecture checks moved new daemon lifecycle messages to
  generated unique event IDs and restored the Server/Persistence provider
  boundary for the temporary preview spool. Submission integration coverage now
  archives one mixed `user-root`/`orchestration`/`execution-child` hierarchy as
  a unit and retains exact-settings rerun behavior.
- 2026-09-04: The completion audit removed the last public recursive candidate/
  folder snapshot DTOs; those completed one-shot shapes now exist only in the
  shared local/remote CLI presentation adapter, while HTTP/OpenAPI exposes
  Search View pages and compact selection commands. Concurrent daemon fixtures
  also exposed global SQLite pool cleanup as a cross-instance race. Runtime,
  Search View, and Preview shutdown now clear only their own connection pools;
  the parallel CLI backend batch and the full sequential solution suite pass
  without incidental daemon logs.
- 2026-09-04: Final verification passed: `dotnet build --no-restore -m:1`,
  all 1,427 solution tests (Core 818, CLI 299, Server 202, Persistence 108),
  `bun run api:generate`, `bun run check`, `bun run build`, contract/ownership
  searches, and `git diff --check`. The build reports only the repository's
  existing MSTest analyzer warnings; this slice adds none. Live WebUI transport
  wiring remains deliberately outside the agreed slice.
- 2026-09-04: The post-completion simplification pass centralized CLI projection
  mapping, draft-setting traversal, selection follow-up semantics, remote-transfer
  validation, query normalization, cursor authentication, and local/daemon
  settings composition. Ordinary settings resolution now bypasses the reflective
  provenance snapshots used only by preview/introspection, while auto-profile
  conditions remain single-evaluation. The solution build and all 1,427 tests
  still pass; live WebUI transport wiring remains outside this slice.
