# Backend comments on the WebUI daemon audit

## Overall assessment

The audit is unusually careful and almost all of its factual observations match
the current code. The main risk is not that its individual DTO suggestions are
wrong, but that implementing them independently would leave several overlapping
models for the same concepts.

I would organize the work around three larger owners:

1. A definition-stable, revisioned, server-owned **search view** resource owns
   projection, filtering, ordering, paging, nested alternatives, and selection.
   It replaces the current family of whole-result projection responses rather
   than sitting beside them indefinitely.
2. A durable **submission** model records user intent, submission time, the
   normalized effective settings needed to reproduce that intent, and rerun
   lineage. Runtime jobs remain the execution model, and `/api/jobs` remains the
   only collection used to traverse those jobs.
3. A combined **transfer timeline and accounting** owner overlays live transfer
   state on retained rows and records byte activity explicitly. Transfer list
   pages and Dashboard analytics then do not invent separate meanings for the
   same events.

One cross-runtime rule applies to all three owners: semantic work belongs in
Core and is shared by the local CLI and daemon. Settings composition, planning,
condition evaluation, projection, grouping, ranking, and counters must not be
reimplemented in CLI renderers or server DTO adapters. The adapters may parse,
transport, persist, page, or render the same Core results. Parity tests should
feed identical settings and inputs through local and daemon consumers and compare
the resulting Core semantics, including intermediate search revisions.

The comments below follow the source document in order. “Accept” means the
recommendation is sound as written; “deepen” means the gap is real but the fix
should have a larger architectural boundary; “narrow” means the proposed public
surface should be smaller or more precise; and “reject” means the UI should not
be promised the described behavior.

## Opening claims and boundaries

### Scope and meaning of fan-out

**Accept.** The audit correctly distinguishes a value that can be composed from
a few bounded resources from one that requires exhausting an unbounded
collection or issuing one request per item. The latter is a backend projection
gap even when it is technically possible for a client to reconstruct the value.

### Fixed-size job details and future Job Preview

**Accept.** The cleanup plan is authoritative here. Runtime job detail must not
regain embedded descendants or preview drafts. A future preview is an ephemeral
daemon resource over planner output, with its own lifetime, not a special state
of `ExtractJob`.

There is one important extension: extract the recursive planning semantics into
a storage-agnostic Core service. Direct submission, Review, and local
`--print jobs`/`--print jobs-full` all consume that service; they do not all
consume the persisted preview resource. The current `PrintOption.Jobs` path is
evidence that the semantics exist, but it is not itself the right reusable
service boundary.

## Search, projection, and submission

### Effective search settings survive history/reprojection

**Deepen.** The diagnosis is correct. Live projection uses
`searchJob.Config.Search`, while all historical projection methods in
`EngineSupervisor` use `defaultDownloadSettings.Search`. `JobEntity.PayloadJson`
retains the default query/projection but not the effective search settings.

Persist a versioned, normalized `SearchDefinition` when the submission is
accepted. It should contain the settings baseline, typed query/default projection
kind, network query, default projection query, and only the effective settings
that affect result admission, grouping, and ranking. Persisting the entire
`DownloadSettings` object would retain unrelated output settings and make the
historical contract depend on a large internal configuration graph.

An edited Filtering/Ranking form should create a new search-view definition; it
should not mutate the retained definition of the original search. The default
historical view uses the retained definition.

The audit misses one other mutable ranking input: `ResultSorter` also reads the
engine's in-memory user-success counts. Exact reproducibility therefore requires
either snapshotting the relevant peer reputation values into the view or
explicitly declaring that a newly created view is ranked using current
reputation. A view cursor must never silently change ordering because those
counts changed between pages. The broader ownership, persistence, and decay of
these legacy counts is V4 work; this audit only requires a view to bind the
ranking context it actually used.

### Generic File Search has neutral defaults

**Deepen.** This is a real semantic bug, not merely a UI convenience. A generic
file search should not inherit built-in audio formats, length matching, MP3
preference, or strict title/album preferences.

For default composition, introduce only a `generic` versus `music` baseline.
The baseline discriminator's sole job is selecting neutral or music built-ins;
track and album share the music baseline. Their existing typed queries and
default projection kinds still remain distinct because they drive different
query/projection behavior and provide context for auto-profile matching. Do not
add a redundant `generic-file`/`track`/`album` intent enum that mixes those two
responsibilities.

After choosing the baseline, apply operator configuration, profiles, and the
request patch in the normal order. The complete typed submission shape, not only
the baseline, must still reach the real resolver. Baseline selection and patch
composition belong in Core; the local CLI resolver and
`ServerJobSettingsResolver` should supply their inputs to that same composer
rather than encode the order independently.

This cannot be implemented reliably by clearing selected properties after
`ServerJobSettingsResolver` has produced a fully materialized
`DownloadSettings`: at that point the daemon cannot distinguish built-in values
from explicit operator overrides. The settings composition boundary must retain
the distinction between a settings baseline and configured patches. Do not add a
growing list of “values a generic client must negate.”

### Submission forms display authoritative inherited defaults

**Deepen.** Profile summaries are intentionally insufficient, but exposing every
profile's effective settings would still make the client reproduce resolution
order and auto-profile matching.

Add a side-effect-free settings-resolution operation that accepts the same
typed submission shape and sparse options as a real submission and
returns a secrets-free effective projection, the matched profile names, and
useful provenance such as `built-in`, `operator-default`, `profile`, or
`request`. It must call the same resolver used by submission. Auto-profiles can
depend on the job/query context, so a single context-free “defaults” document is
not authoritative.

### Filter/rank/sort the complete search result set before paging

**Deepen; highest priority.** The present endpoints are unbounded twice: live
projection returns every item, and historical projection first fills a
`List<SearchProjectionInput>` with every retained row. Adding `cursor` and
`limit` parameters to the current POST methods would not fix the full-set sort
or give later pages stable meaning.

Create a search-view resource with an immutable definition bound to:

- the source search job and consumed raw-result sequence;
- the normalized projection/search definition;
- filter and order semantics; and
- any mutable ranking context used to build the view.

The daemon maintains or externally sorts the view using bounded memory and
disk-backed state, then pages it by opaque cursor. Each atomic publication
creates an immutable view revision. Responses include source revision, view
revision, exact projected totals, completeness, and raw-result retention state.
The live head advances as results arrive, while every cursor remains bound to
the immutable revision from which it was issued. Publishing a revision records
versioned row/group changes; it must not copy the complete view for every result
batch.

This should replace the current whole-array `/results/*` contract in one
breaking change. Keeping both as long-lived public abstractions would preserve
the unbounded path and double the projection code.

### Live search views advance incrementally

**Add; this is a material gap in the original audit.** A view must be available
before its search job completes and must produce the same rows, ordering,
grouping, preference tiers, and summary counters that a fresh projection of the
same raw-result prefix would produce. Completion only publishes `isComplete` on
the final drained revision; it must not trigger a different full-set projection
path.

Core already contains the useful seam: `SearchJob` caches incremental file,
folder, aggregate-track, and aggregate-album projectors and consumes only raw
results after the last sequence. Promote that into the shared search-view
projection kernel. For each input batch, compute admission and public condition
facts once, update sort/group state and exact counters, persist the changes, and
publish the new source/view revision atomically. Rebuilding from retained raw
rows calls the same incremental kernel from sequence zero rather than a separate
historical algorithm.

Over the network, a client should observe a cheap revision notification or poll
with `afterRevision`, receive the new fixed-size summary and a bounded page of
changed item/group refs, and refetch only affected or visible pages at the new
revision. It must not download the whole projected array every second. Updates
may be coalesced, but no published revision may combine new rows with stale
counts or ordering. Tests should compare every incrementally published prefix
with a from-scratch projection and then verify the final incremental revision is
identical to the completed view.

Local `--print results`, `--print results-full`, JSON, and link modes may wait for
the one-shot search to finish, but they must consume this same Core projection
definition and kernel. Their formatters own presentation only; they must not
carry private filtering, tiering, grouping, or ranking rules. Together with the
shared planner for `--print jobs`, this is the other explicit CLI parity point
needed in this file. `--print index`/`index-failed` inspect a local index rather
than search results and are outside this projection path.

### Projected folders/groups remain bounded below the top-level page

**Accept.** Every item in a page must itself be fixed-size. Folder and group
summaries should carry an opaque view-scoped ref, counts, byte aggregates, a
representative, and retrieval state. Children and alternatives are separate
cursor-paged collections under the same immutable view.

The nested cursor must be bound to the view revision and parent ref. It must not
rerun projection against current defaults or current raw results. Remove
`includeFiles`, `includeCandidates`, and `includeFolders` whole-array switches
when the new contract lands.

### Preferred / Other result tiers are explicit

**Narrow.** The server must expose the tier boundary because it owns the
lexicographic comparator. A frontend-generated scalar score would be incorrect.

Expose a stable `preferenceTier` and, if the UI actually renders explanations,
a small documented enum/set of public condition matches. Do not expose the
packed sort flags, an invented numeric score, or internal comparer key names.
A result is `preferred` exactly when all configured preferred conditions are
satisfied; otherwise it is `other`. With no preferred conditions configured,
all admitted results are `preferred`. Compute that public-condition evaluation
once while constructing the projected sort entry, carry it with the view row,
and reuse it for sorting, tier, and optional explanations. DTO projection must
not evaluate the conditions a second time.

### File Search projects generic directory units

**Deepen.** A generic directory projection is useful, but “directory root” is
not yet a defined identity. Flat Soulseek hits do not reveal an album-like
semantic collection root. Grouping by an inferred common ancestor can collapse
an entire peer into one result or change as later hits arrive.

Use the existing Core `PeerDirectoryIdentity` as the canonical identity and
introduce one public `PeerDirectoryRefDto` used by album and generic directory
views. The safest initial grouping rule is the exact containing directory of a
matching file. If the visual design requires a larger inferred tree root, its
deterministic derivation must be specified and tested before it becomes a
contract; the frontend must not submit an arbitrary ancestor and receive
authoritative counts for it.

The fixed-size summary should contain matching public/locked counts and bytes,
best-child relevance, and whether the exact directory was subsequently browsed.
Paged children return exact relative paths, from which the client can render
subfolders without a second identity system.

### File Search can retrieve the exact full generic folder

**Accept with consolidation.** Core already has the correct generic primitive:
`RetrieveFolderJob` accepts peer-directory identity. The public album-only ref
and resolution path are the accidental specialization.

Generalize album folders to the canonical `PeerDirectoryRefDto`, and resolve a
follow-up against membership in the search view that issued the ref. Do not
reproject all retained raw rows with current settings merely to prove that the
directory once appeared. Retrieval and whole-directory download should then
reuse the same generic identity and Core job.

### Search peer queue depth is a result observation

**Accept.** `SearchResponse.QueueLength` is available at the same instant as
upload speed and free-slot state, but `SearchProjectionInput`, persistence, and
`PeerInfoDto` discard it.

Model these values as a search peer observation, including `ObservedAtUtc`, so
the API does not imply that they are current profile state. Persist queue depth
with each retained result observation and use it for view ordering. Exact
username spelling remains untouched.

### Locked/private search rows are actually representable

**Accept.** Soulseek.NET exposes `SearchResponse.LockedFiles`; the current
session retains only `response.Files` and a locked count. The missing rows can
therefore be retained, but cannot be reconstructed retroactively from old
history.

If the faithful UI includes individual locked rows, add them at search admission
and persist a visibility enum rather than a loose boolean. Public and locked
rows share the same structural identity rules, while selection resolution must
reject locked refs with a stable reason. Folder summaries can then derive
`public`, `locked`, or `mixed` visibility consistently with peer browsing.

If this retention work is not implemented, the UI must show only the aggregate
locked count. Adding `locked` to today's public-only candidates would be false
precision.

### Album folder summaries expose authoritative projected bytes

**Accept with naming changes.** Add unambiguous fields such as
`matchingFileBytes` and, only when a browse completed, `retrievedTotalBytes`.
Likewise distinguish matching file counts from retrieved total file counts.
Never label the sum of optional/currently loaded children as total folder size.

Visibility-specific counts/bytes should come from the same retained result model
as the locked-row decision, not parallel ad hoc properties.

### Aggregate search has bounded group summaries + on-demand options

**Accept.** Aggregate group refs should be opaque and scoped to the immutable
search view; a derived display name is not an identity. Define `shareCount` as
the number of distinct exact usernames and `optionCount` as the number of
selectable alternatives after projection. Include one relevance-best
representative in the summary.

An alternatives page uses the same ordering contract as the parent view. Album
alternatives reference fixed-size directory summaries, whose files are paged in
turn. This replaces the current `IncludeCandidates`/`IncludeFolders` overfetch
switches.

### Selection over server-paged results stays bounded and reports resolution outcome

**Deepen.** `all-except` is compact for Select All, but an `only` array is still
unbounded when a user selects many rows. The current response is also unbounded
because it returns one `JobSummaryDto` per submitted file.

Make selection a short-lived server-owned set under a search view. Its mode is
`only` or `all-except`; item toggles can be applied incrementally and stored on
disk. Commit carries only the selection ref. A directory selection is one
directory ref, never an enumeration of its children.

Commit returns a fixed-size receipt containing the new submission/workflow ref,
requested/resolved/submitted/skipped/rejected counts, and bounded stable reason
buckets. Jobs are then traversed through `/api/jobs`; they are not embedded in
the mutation response. Resolve each entry independently so one stale or locked
row does not fail unrelated valid selections.

## Jobs and planning

### Jobs list creation time

**Accept, but define the clock.** `JobEntity.CreatedAtUtc` is the first
persistence mutation time, while live jobs have no equivalent public field.
Expose `CreatedAtUtc` as the job registration time and ensure the live state
store and persistence writer both derive it from the same registration event.

Also introduce `SubmittedAtUtc` on the submission owner described below. A
generated child can be registered much later than the user's original request;
using one ambiguously named timestamp for both concepts will produce confusing
history.

### Search-job summary counts are stable on history rows

**Accept with explicit populations.** Historical mapping currently sets both
discovery counters to null even though `SearchJobEntity` stores result and
locked counts.

Persist and expose named counters such as `publicFileCount`, `lockedFileCount`,
and `observedPeerCount`. Define whether locked-only peers participate in the
peer count. Projected item/group counts belong to a search view and must not be
copied into the job summary, because they vary by view definition. Populate the
same fields for live and retained rows.

### Terminal Jobs history can be archived/removed

**Deepen.** Prefer reversible archive semantics (`ArchivedAtUtc`, excluded from
default lists) over a UI hard-delete. Hard deletion cascades search history and
nulls job relationships, so it is not equivalent to hiding a history row.
Physical deletion remains the retention/purge owner's job.

The UI action normally targets a user submission, not an arbitrary internal
child. Archiving only an Extract root can leave its sibling result or an
orchestration root visible. Add a durable submission identity to jobs and
archive that semantic unit only once all of its jobs are terminal, returning
affected counts. If arbitrary child archive is later required, specify how
parent counts and relationships remain truthful rather than silently cascading.

### Search again has true rerun lineage

**Deepen.** `SourceJobId` must remain follow-up provenance. A rerun is a new
submission derived from an earlier submission, not a download sourced from its
results.

Persist a normalized submission command/specification and add
`RerunOfSubmissionId`. A rerun endpoint clones the retained command and effective
settings into a new submission and returns its ref. “Use current defaults” is a
different explicit operation; it must not be the accidental result of
reconstructing a job from today's config. This design can support rerunning
other submission kinds without adding a search-only lineage field to every job.

### Remote CSV/List inputs are browser-uploadable

**Accept with storage/safety clarification.** Uploads must stream directly to a
daemon-owned artifact store with backpressure and atomic completion; do not
materialize the body in memory or trust the browser filename as a path. The API
returns a small opaque artifact ref, digest, metadata, and expiry.

“Bounded” should describe concurrency and response representation, not an
arbitrary file-size rejection. Any size/quota limit must come from an explicit
operator policy or real representation boundary. Expiry and cleanup are owned
by the artifact service and must not invalidate a committed preview/submission
that still references the content.

Uploaded artifacts should be immutable. Current CSV/List jobs attach source
mutations that clear successful rows in the source file; those mutations make
sense for operator-owned working files but not for an uploaded one-shot
artifact. Disable source mutation for immutable uploads (or create an explicit
mutable working copy), and document the choice. Preview commit must use the
already resolved plan rather than reading the artifact again.

### Optional New Job Review is a non-runtime preview

**Accept and deepen.** Build a storage-agnostic Core planning service that
recursively resolves extract/list inputs without Soulseek discovery or downloads
and emits a stream of planned-node records. Direct Start, Review, and local
`--print jobs` call this same service, so settings, auto-profiles, extraction
overrides, and source interpretation cannot drift. Direct Start consumes the
records as runtime submissions, the local CLI renders them without a database,
and only the daemon Review adapter persists them as a disk-backed preview.

Preview creation captures the source revision/artifact, normalized effective
settings for every planned node, and stable preview refs. Root/direct-child
queries are paged; repeated children are not placed in summary/detail. Each
independent source entry carries its own state/failure so one bad CSV row or
nested URL does not erase valid siblings. The overall preview may therefore be
partially ready rather than only `ready` or `failed`.

Commit accepts only a server-owned selection ref and creates runtime jobs from
the stored plan. It does not rerun extraction, re-resolve defaults, or reread a
mutable source. Preview lifecycle logs should use the preview ID and report
coarse outcome, duration, and safe counts.

Preview persistence is an optional feature boundary. If its store is unavailable,
Review fails clearly while direct Start and local `--print jobs` continue through
the Core planner. A remote CLI may use the daemon preview resource and page its
records; that does not make the local CLI depend on server storage.

### Semantic job navigation survives refresh/paging

**Deepen.** `ParentJobId`, `ResultJobId`, and `SourceJobId` have valid distinct
meanings and should not be overloaded. A role flag alone, however, is easy to
set inconsistently unless one owner establishes it.

Introduce a first-class `SubmissionId` assigned at API acceptance/preview
commit and persisted on every job produced by that intent. Add a small stable
job role such as `user-root`, `semantic-result`, `orchestration`, or
`execution-child`, assigned by the planner/submission owner. Keep
`ParentJobId` for cancellation hierarchy and `ResultJobId` for the Extract
result link.

`/api/jobs` remains the only job traversal collection: add submission/role
filters as needed, and continue using `parentJobId` for direct execution
children. Do not add a recursive semantic tree. A fixed-size submission summary
may own archive/rerun metadata, but its jobs are always listed through
`/api/jobs`.

This submission boundary also supplies the missing authoritative submission
time and prevents archive/rerun behavior from depending on heuristics over the
current page.

## Transfers

### Already-active uploads hydrate on a new WebUI connection

**Accept; use the smaller fix.** Active uploads are bounded by configured upload
slots. Include non-queued active uploads in the daemon snapshot even though
their `WorkflowId` is null. Keep the existing cursor-paged upload queue for
queued rows.

There is no need to generalize the queue endpoint merely to fix hydration.
Tests should cover an upload that was already active before snapshot capture,
then verify later deltas and removal do not duplicate it.

### Live download progress has parity with upload state

**Accept and push the data to the Core snapshot boundary.** The live download
`TransferSnapshot` lacks requested/started/progress timestamps and speed even
though Soulseek transfer state exposes timing and average speed. The server
mapper should not estimate these independently from whichever deltas happened
to survive coalescing.

Extend the generic Core transfer snapshot/event projection with scheduling,
last-progress time, and speed, then map the same fields for both directions.
Also advertise the valid download cancellation action (currently job-owned) on
the transfer row and populate terminal outcome/failure/cancellation fields, not
just `IsTerminal`.

### Generic transfer rows retain optional file/audio metadata

**Accept with reuse.** `TransferSnapshot.Candidate`/`Target` and the upload share
catalog already know these values. Add the existing `FileMetadataDto` as an
optional transfer field rather than defining a nearly identical transfer-only
metadata type. Persist the same projection on `TransferEntity` so live and
retained shapes do not diverge.

Fallback transfers and genuinely unknown metadata remain null. Exact remote
path/username identity stays separate from presentation metadata.

### Downloads/Uploads have an exact bounded chronological timeline

**Deepen.** Make the existing `/api/transfers` collection the combined timeline
rather than adding a parallel history-like endpoint. It should page newest
first by stable `(CreatedAtUtc, TransferId)`, overlay authoritative live state
on persisted rows, include active downloads/uploads and queued uploads, and
deduplicate by transfer ID. With persistence disabled it still returns live
rows and clearly reports that retained coverage is unavailable.

Current transfer persistence receives start/progress mutations, so retained
storage can supply much of this projection, but it cannot be the sole live
source: persistence is optional and may lag or degrade. A cursor should bind a
snapshot/revision when exact paging is promised; otherwise the contract must
state its live-list consistency behavior.

Folder grouping remains presentation, but timeline rows need an authoritative
operation/job/group ref if the UI is expected to avoid grouping unrelated
transfers that merely share a peer and path prefix.

### Scoped bulk cancellation

**Accept with non-atomic execution semantics.** Resolve the direction/state
filter to a target snapshot under the relevant owners, then cancel each target
independently. The request is atomic only in deciding its target population;
remote transfer state can change while cancellations run and one rejection must
not prevent unrelated cancellations.

Use a fixed-size resolution receipt with resolved/succeeded/already-terminal/
rejected/failed counts and stable reasons. The generic command layer routes
download transfers to their job/transfer cancellation owner and uploads to
`UploadCoordinator`. Individual transfer rows should advertise the same action
semantics.

### Terminal transfer history can be archived/removed

**Accept with archive semantics.** Add `ArchivedAtUtc` to terminal retained
transfers and exclude archived rows by default. Individual and filtered bulk
archive operations can share the resolution-summary shape, but archive and
cancel should remain separate commands because their owners and preconditions
are different.

Do not present archive as permanent deletion. Physical deletion and cascade of
attempt rows remain explicit retention/purge behavior.

## Dashboard analytics

### Range-wide analytics: 24h / 7d / 30d / 90d / 1y / All

**Deepen; high priority for the Dashboard, but not an endpoint-only change.**
The audit correctly rejects reconstructing analytics from transfer creation
times. Adding a SQL aggregate endpoint over today's `Transfers` and
`TransferAttempts` tables would still produce wrong bandwidth and range totals.

Introduce one transfer-accounting owner that consumes cumulative per-attempt
progress, computes non-negative byte deltas idempotently by transfer/attempt
revision, and persists compact time buckets plus peer/content dimensions. It
must account terminal snapshots, survive retries where byte counters reset, and
retain a checkpoint so restart/replay does not double count. Use batching,
backpressure, and disk-backed state rather than dropping work because a buffer
is full.

The current progress path is coalescible and persistence may drop progress
mutations. Therefore an “exact” analytics contract must also return retention
and coverage intervals/gaps. Optional analytics degradation must not stop core
transfers, but it may not silently return a complete-looking zero or partial
range.

The content ranking needs a stable daemon-owned content key captured at transfer
creation; remote path alone is peer-specific and cannot reliably identify the
same logical content across users. Error ranking should group stable terminal
failure reason codes, not raw exception messages with unbounded cardinality.

The response itself should remain one bounded range document: a fixed bucket
count, fixed-size summary, and bounded top-N rankings. “All” means all retained
accounting coverage, not all time if retention has removed earlier buckets. A
comparison period uses the same accounting populations and reports its own
coverage; it is not inferred by scaling the selected range.

### Analytics byte/time semantics are explicit

**Accept the requirement; revise the proposed v3 semantics.** “Bytes transferred
during the range” still needs to say whether it means positive transport bytes
per attempt (including retries/partials) or logical completed file bytes. Those
populations diverge on retries, resume, cancellation, and failure.

For the current labels, I recommend:

- bandwidth buckets and share ratio use positive transport byte activity by
  direction, measured per attempt;
- `downloadedFiles`/`uploadedFiles` count successful logical transfers completed
  in the range, because the UI labels them as downloaded/uploaded files;
- distinct peers use peers with transport byte activity in the range;
- content ranking uses successful logical downloads completed in the range; and
- errors use terminal failed attempts completed in the range.

If product intent instead wants “transfers with any byte activity” for the file
counts, rename the fields/labels accordingly. The semantics should be normative
server contract text, while a compact `accountingVersion` is enough in every
response; repeating long prose strings in each payload does not enforce the
rule.

The audit's note about current live transfer count and health is correct. Once
active upload hydration and download speed are fixed, the live Dashboard cards
can compose bounded live state without a second metric owner.

## Users / Shares and Chat

### Shares global filter produces one mixed result tree + exact count

**Accept with a flatter API shape.** The immutable browse artifact is already
the right owner. Add one cursor-paged search projection over that artifact; do
not duplicate ordinary directory and per-directory file browsing.

Page logical matches as fixed-size directory/file rows with exact refs,
visibility, display path/breadcrumb context, and matching aggregates. Returning
a recursively nested “tree page” would make page boundaries and ancestor
duplication ambiguous. The client can render a tree from refs and breadcrumbs.
Return public and locked matching counts/bytes separately so “exact” has one
meaning.

The current `ordinal_contains` queries scan candidates. A global search over a
large homeserver browse artifact should use an indexed auxiliary normalized
search representation (for example an appropriate SQLite FTS/trigram index)
while returning the exact stored spelling. The cursor is bound to the immutable
browse artifact/revision, so later refreshes cannot reorder an existing page.

### Chat/User actions know and mutate per-user block state

**Deepen substantially.** `PeerAccessPolicy` is currently an immutable snapshot
constructed from static settings and shared by chat, uploads, browsing, and
profiles. Adding GET/POST endpoints without changing that ownership would either
have no runtime effect or create competing policy sources.

Refactor it into one daemon-owned peer-access service that publishes immutable
policy snapshots to all consumers and persists exact ordinal usernames. The API
reads and mutates that service; Chat and Users use the same resource. A mutation
must survive restart and update every admission path consistently.

Project `isBlocked` from that owner into the user/profile and direct-conversation
summaries that already name a peer, and offer a direct per-user lookup for detail
views. Do not make a chat list issue one peer-access request per conversation.

The migration rule for usernames already present in configuration must be
explicit: configuration cannot remain an unremovable second deny list if the UI
offers Unblock. Prefer one authoritative persisted operator policy, with config
used only for an explicit import/bootstrap rule. Mutations should affect future
admissions; do not silently cancel existing transfers or delete chat history
unless a separate product action says so.

## Re-audit dispositions deliberately removed from the gap list

### Prototype controls and branding

**Agree.** These are frontend review affordances and do not belong in daemon
contracts.

### Soulseek connection detail

**Agree.** `SoulseekClientStatusDto.Flags` is sufficient; another boolean/status
endpoint would duplicate state.

### Dashboard health and client-observed latency

**Agree.** Persistence/sharing/chat states already expose daemon-owned health.
HTTP latency observed by a browser is client state, and a synthetic daemon
`Healthy` boolean would hide useful degradation.

### Current transfer count

**Agree.** It is a bounded live composition after active uploads are included in
the initial snapshot. Analytics storage should not become its source.

### Transfer folder grouping

**Mostly agree.** Visual adjacency and folder cards are frontend presentation.
The daemon only needs to retain a stable operation/job/group ref and file
metadata so the frontend does not infer ownership from path strings. No nested
folder-history DTO is warranted.

### Chat destination-rail paging

**Agree.** Existing paged chat collections plus client virtualization are the
correct boundary.

### Direct-user presence

**Agree.** `UserProfileDto` already owns it. Peer-access state is the only
missing shared user action state.

### Operational event journal

**Agree.** It may later be useful for diagnostics/integrations, but no current
prototype feature justifies making durable events another authoritative state
source.

### Authentication

**Agree with the stated scope.** The prototype does not depend on daemon auth,
so authentication should not distort these contracts. Operator mutation policy
still applies to the new archive, block, preview-commit, selection-commit, and
bulk-cancel endpoints.

## Planning details

### Job Preview

**Agree with the source section, subject to the planning-service and failure
isolation changes above.** The preview repository is a daemon adapter over the
Core planner, not the planner itself. Effective settings belong to preview
creation and are stored per planned node. Commit carries a server selection ref
only. The stored plan, not an extracted draft sent through the browser, is
authoritative.

Preview storage should be disk-backed and expiring, with a small persisted
summary and paged node records. Expiry is a lifecycle rule, not a reason to cap
the number of valid planned entries. A preview that has been committed must
remain referenced long enough for submission provenance/diagnostics even if its
interactive browsing lease expires.

Local `--print jobs` does not create this resource, receive a preview ID, or need
a configured database. It streams the same planner records to its formatter; if
formatting ever requires a second pass, an anonymous temporary spool is an
implementation detail rather than daemon preview persistence.

### Operational activity

**Agree.** Keep live activity ephemeral and best-effort. It is diagnostic/prompt
context, not authoritative state and not the source for history or Dashboard
accounting. If a journal is introduced later, give it its own typed/versioned
contract and retention policy; do not rename or weaken today's live state in
anticipation.

## Summary of proposals

1. Replace whole-array result projection with one definition-stable, revisioned,
   disk-backed search-view resource that updates incrementally while a search is
   live. It owns filtering, ordering, paging, nested children/options, exact
   counters, preference metadata, and server-side selection.
2. Persist a normalized `SearchDefinition`, including its generic/music settings
   baseline, typed query/projection kind, and effective projection settings. Use
   the real resolver for UI-safe settings introspection.
3. Retain queue depth and locked rows at search admission. Use one canonical
   peer-directory ref for album and generic directory views, retrieval, and
   whole-directory download.
4. Add a durable submission identity/specification with submission time, job
   roles, effective settings, and rerun lineage. Keep runtime execution hierarchy
   and `/api/jobs` traversal unchanged; use the submission boundary for semantic
   history, archive, preview commit, and rerun.
5. Build one storage-agnostic Core Job Planner shared by direct Start, Review,
   and local `--print jobs`; expose Review as a separate disk-backed daemon Job
   Preview resource. Stream remote CSV/List inputs into immutable expiring
   artifacts and never reread/re-resolve them on preview commit.
6. Include active uploads in daemon snapshots, enrich Core transfer snapshots so
   downloads and uploads map identically, retain `FileMetadataDto`, and make
   `/api/transfers` the combined newest-first live/retained timeline.
7. Implement filtered bulk cancellation with per-transfer failure isolation and
   archive terminal job/transfer history reversibly; keep physical deletion in
   retention/purge owners.
8. Add a real transfer-accounting owner before Dashboard analytics. Record
   per-attempt byte activity and explicit coverage, use stable content/failure
   identities, and define bandwidth, file-count, peer, content, and error
   populations separately.
9. Add a flat mixed browse-search projection over the existing immutable browse
   artifact, backed by an indexed auxiliary search representation.
10. Replace immutable startup-only peer access with one persisted, mutable
    daemon service shared by chat, uploads, browsing, and profiles before adding
    Block/Unblock endpoints.
