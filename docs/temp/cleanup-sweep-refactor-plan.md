# Cleanup sweep refactor plan

## Purpose and scope

This plan completes every unfinished refactor and recorded product decision in
`cleanup-sweep-findings.md`. The local bug fixes, performance fixes, and
architectural simplifications already listed there are the verified baseline;
they are not to be rewritten merely to make this plan larger. The explicit
"areas that should not receive a larger refactor" remain non-goals.

The production scope is `Sockseek.Api`, `Sockseek.Core`,
`Sockseek.Persistence`, and `Sockseek.Server`. Tests and the CLI may change as
needed to verify or consume the production contracts, but they do not count as
cleanup output.

The unfinished work is:

1. centralize leaf-download failure classification;
2. bound workflow history, job navigation, and job payloads;
3. replace embedded transfer-attempt history with one latest attempt;
4. retire terminal daemon workflow state from every live owner; and
5. consolidate persistence maintenance execution.

No unresolved product choice is assumed by this plan. If implementation
reveals a choice that changes the recorded behavior rather than a technical
detail needed to realize it, implementation pauses for discussion.

## Resolved extraction-preview product decision

The fixed-size job-detail audit found one pre-existing exception that the
initial inventory missed. When `AutoStartExtractedResult` is false,
`ExtractJobPayloadDto.ResultDraft` recursively embeds the extracted job draft.
A playlist can therefore inline every `JobListJobDraftDto.Jobs` item, and a
resolved Soulseek directory can inline every `DirectoryTransferPlanDto.Entries`
file. This contradicts the fixed-size detail rule. Terminal daemon retirement
also removes the only live copy immediately after extraction, so a polling
client cannot reliably retrieve the draft for later submission.

The public runtime-preview controls are removed. Server extract submissions
always process successful extracted results, and ordinary Extract job detail
keeps only the scalar input/type and semantic `ResultJobId` relationship. Core
retains its extract-only mechanism for internal/library callers, and the CLI's
existing `PrintOption.Jobs` path continues to recursively resolve extraction
and job lists without Soulseek discovery or downloading.

A future WebUI/remote-CLI review flow is a separate short-lived, server-owned
Job Preview resource. It must reuse the existing planning semantics while
remaining outside runtime job history, keep summary/detail fixed-size, page
roots and direct children, preserve effective settings, and submit the reviewed
plan without rerunning extraction. The removed one-level `ResultDraft` is not a
compatibility precursor for that resource.

Until that resource exists, remote runtime `--print jobs` output is rebuilt
after completion through the ordinary fixed-size job details and paged job
collection. The CLI may wait for the already-offered terminal mutations to
become visible in healthy persistence, but neither the engine nor live-state
owner waits for that commit. A persistence-disabled or degraded daemon does not
retain an additional terminal graph merely to support post-retirement printing.

## Locked product and design rules

- A cancellation-shaped exception is cancellation only when an owned token or
  explicit stale/manual cancellation source requested it. Otherwise it is a
  failure of the affected transfer/job.
- Peer-local and file-local failures stop at the affected leaf or directory
  child. They do not restart the daemon engine or abort unrelated roots.
- Daemon terminal live state is not history. Final immutable events and
  self-contained persistence mutations are offered synchronously, then live
  state is retired without waiting for persistence acceptance or commit.
- Persistence-disabled daemon operation has no retired history. Persistence
  degradation may leave history incomplete and is exposed through existing
  health and logging, not an in-memory fallback.
- Active, awaiting-selection, and genuinely resumable workflows stay live.
  In particular, a manual album that can still accept a selection is not
  retired merely because its current attempt is terminal.
- One-shot CLI engines retain their terminal queue because final rendering and
  exit-status calculation consume it. Retirement is an internal daemon-engine
  composition choice, not a user-facing retention option.
- Ordinary API summaries and details are fixed-size. Descendants and repeated
  source items are traversed through cursor-paged collection resources.
- The jobs collection is the one navigation contract for roots, direct
  children, and whole workflows. No nested paging convention or replacement
  recursive tree endpoint is introduced.
- Transfer detail contains `AttemptCount` and one `LatestAttempt`. Complete
  attempt history remains available only from the existing paged attempts
  endpoint.
- Breaking DTO and route cleanup is made directly. No compatibility aliases,
  duplicate legacy DTOs, or transitional endpoints are added.

## Phase 1: centralize leaf-download failure classification

### Production changes

1. Add one download-runtime exception classifier at the leaf execution
   boundary. It receives the job, parent/runtime tokens, and the exception and
   produces either a requested-cancellation outcome or a failure outcome with
   bounded diagnostic text.
2. Make `DownloadExecutorCoordinator` use that classifier for every leaf type
   instead of treating every `OperationCanceledException` as cancellation.
   Unexpected leaf exceptions are committed to the job and returned rather
   than escaping `ProcessRootJob`.
3. Make `ExactPeerFileTransferRunner` classify terminal transfer events by the
   actual owned cancellation state. Preserve stale cancellation, manual skip,
   retries, and finalization failures. An unrequested transport
   `OperationCanceledException` publishes a failed transfer and then reaches
   the leaf classifier as a job failure.
4. Align directory-child execution with the same requested-cancellation rule.
   One child failure remains isolated and directory aggregation determines the
   parent outcome.
5. Retain specialized lower-level behavior: candidate retry, reconnect waits,
   stale-transfer conversion, manual skip, and album best-effort folder
   completion are not flattened into a universal cancellation framework.

### Tests

- Remote file: requested cancellation is cancelled; a transport-originated
  cancellation exception is failed; an ordinary transfer exception fails only
  that root while an unrelated root completes.
- Remote directory: requested parent/child cancellation is cancelled and an
  unrequested cancellation-shaped child failure is failed and isolated.
- Song and album: requested cancellation remains cancelled, while exact-target
  and selected-folder transport cancellation is reported as failure.
- Exact transfer events distinguish stale, manual, requested, peer failure,
  and finalization failure without duplicate terminal events.

## Phase 2: bound workflow history and navigation

### API contract

1. Replace `WorkflowSummaryDto.RootJobIds` with `RootJobCount`.
2. Make `WorkflowDetailDto` fixed-size; it contains the workflow summary and no
   job collection.
3. Make `JobDetailDto` contain the summary, scalar typed payload, and
   `ChildCount`; remove direct child summaries.
4. Add `ParentJobId` to `JobQuery`. When present, `/api/jobs` returns direct
   children of that parent. Without it, `IncludeAll=false` continues to mean
   roots and `IncludeAll=true` means every matching job.
5. Remove `WorkflowTreeDto`, `WorkflowJobNodeDto`, the workflow-tree route, JSON
   metadata, client method, and all server tree-building logic.
6. Remove descendant/source-item collections from typed job payloads rather
   than retaining always-null compatibility fields:
   - song candidates;
   - album results and tracks;
   - aggregate songs;
   - job-list direct songs;
   - retrieve-folder folder contents; and
   - resolved/active directory transfer-plan entries; and
   - extracted result drafts, including nested job lists and resolved directory
     plans.
   Existing scalar counts, selected identities, directory state, and paged
   search/job resources remain the representation.

### Persistence and live queries

1. Change job cursor ordering to stable `DisplayId, JobId` ordering and keep the
   cursor fixed-size and pre-decode length checked. Add the `ParentJobId`
   predicate and an index if query-plan verification shows it is needed.
2. Replace `PersistedWorkflow`'s complete job list with a fixed-size
   `PersistedWorkflowSummary` projection. `JobHistoryReader` computes title,
   state, root count, and active/failed/completed counts in SQL without
   materializing workflow jobs. A single-workflow summary query backs detail.
3. Use the same failed-workflow classification in live and persisted
   projections, including cancelled, partial-success, and unsuccessful skipped
   jobs.
4. Page live jobs and workflows with the same cursor keys. The historical
   facade merges bounded live and persisted pages, de-duplicates by identity,
   and treats live rows as authoritative without materializing either complete
   collection.
5. Job detail obtains a direct-child count, never the children themselves.
   Historical detail uses `COUNT`; live detail uses its relationship index.
6. Remove unbounded workflow-job and child-reader methods once no production
   caller remains.

### Client and caller changes

1. Make the API client's all-jobs helper genuinely exhaustive by following
   cursors. Keep the page method as the normal bounded primitive.
2. Update workflow, job-info, interactive, and completion callers to request
   roots/direct children/all jobs explicitly through paged job queries.
3. Remote completion and exit-status accounting must consume every page; a
   failure after the first 100 jobs changes the final status correctly.

### Tests

- A workflow larger than one page has a fixed-size list item and detail.
- Root, direct-child, and whole-workflow traversal returns every job exactly
  once across cursors and preserves source order.
- Live-only, persisted-only, and overlapping live/persisted pages have no gaps
  or duplicates, and live state wins for overlapping jobs.
- A failed job beyond the first page affects remote completion/exit status.
- Job detail reports the correct child count but serializes no descendants.
- Album, aggregate, list, retrieve-folder, and directory payloads contain no
  repeated source/child collections.
- The recursive tree route and DTO metadata are absent.
- Cursor size rejection and indexed query-plan coverage remain intact.

## Phase 3: fixed-size transfer detail

### Production changes

1. Replace `TransferDetailDto.Attempts` with top-level `AttemptCount` and
   nullable `LatestAttempt`. Remove `attemptLimit` from the server route,
   facade, API client, persistence interface, and DTOs.
2. Change `TransferHistoryReader.GetTransferAsync` to read only the transfer
   row and its highest-numbered attempt. Keep
   `GetAttemptsAsync` cursor-paged and complete.
3. Track one latest attempt for each live download transfer in
   `EngineStateStore`. Attempt-start/completion/failure/cancellation events
   update that projection, including source identity, paths, timestamps,
   outcome, and failure. Remove it with the live transfer.
4. Project the current upload attempt into the same live representation.
5. In merged detail, live state and its latest attempt are authoritative;
   persisted transfer data supplies retained history and fills the latest
   attempt only when no live attempt exists. `AttemptCount` is the maximum
   authoritative live/persisted count.

### Tests

- Live download and upload detail expose the current/latest attempt without
  persistence.
- Merged detail prefers the live latest attempt and retains historical transfer
  metadata.
- Retained detail returns the highest attempt and correct count after live
  removal.
- More than 200 attempts still produce constant-size detail, while the attempts
  endpoint pages all attempts without gaps or duplicates.
- The detail route no longer accepts or advertises `attemptLimit`.

## Phase 4: retire terminal daemon workflow state

### Retirement boundary

1. Add an internal daemon-engine retirement mode. Server-created engines enable
   it; one-shot/local CLI engines do not.
2. Add a workflow-lifetime owner that serializes root enqueue/resume and final
   retirement decisions. A workflow retires only when:
   - no queued/running root execution remains;
   - every registered job is terminal;
   - no job is awaiting selection; and
   - no terminal manual album remains eligible for another selection.
   A later explicit submission reusing the same workflow ID starts a new live
   generation and can merge with its persisted history.
3. In daemon retirement mode, contain preparation, initial service setup, and
   unexpected root-execution failures at the submitted workflow boundary.
   Register and terminalize the affected jobs so final events and retirement
   still occur; unrelated queued roots continue, while failures outside a
   submitted root retain the daemon restart path. One-shot engines preserve
   their existing top-level startup/error reporting.
4. After every final job/execution event and auto-profile summary has been
   published, synchronously publish a `WorkflowRetiredChange` containing only
   the workflow ID and timestamp. Event subscribers finish offering their
   self-contained persistence mutations before the publisher continues.
5. The retirement operation never waits for inbox acceptance, writer drain, or
   SQLite acknowledgement.

### Live-owner cleanup

1. `DownloadEngine` removes workflow roots from `Queue`, registered jobs and
   display/source indexes, prepared contexts, per-workflow diagnostic state,
   pending terminal-transfer bookkeeping, and manual-selection bookkeeping.
   It disposes per-job cancellation sources.
2. The settings resolver receives a retirement notification so daemon
   workflow options, job options, and job output-path overrides are released,
   including entries for prepared but never registered descendants.
3. `DownloadEvents` releases job/transfer/attempt revision, gate, and terminal
   de-duplication state belonging to the workflow after publishing retirement.
4. `EngineStateStore` publishes an explicit final removal delta, then removes
   workflow jobs, snapshots, summaries, parent/result/source relationships,
   execution markers, nested reverse indexes, search/transfer projections, and
   workflow stream bookkeeping atomically under its state lock. A live workflow
   subscription reserves the generation epoch before fetching its snapshot and
   releases an empty reservation on unsubscribe/disconnect; arbitrary snapshot
   requests therefore cannot retain per-ID stream state.
5. `EnginePersistenceAdapter` handles retirement only as a bookkeeping cleanup:
   it removes cached job relationships after earlier self-contained mutations
   have been offered. It does not enqueue a persistence barrier or retain the
   job graph.

### Observability

- Emit one structured debug retirement event with workflow ID and bounded job
  counts. Persistence rejection/eviction/failure remains owned by persistence
  health and its rate-limited operator logs.
- Do not log job contents, settings, remote paths, or usernames during cleanup.

### Tests

- With persistence disabled, a terminal daemon workflow publishes its final
  events, disappears from live lookup/commands/state, and has no history.
- With a rejecting or unhealthy persistence sink, retirement still occurs and
  bounded health/log evidence remains; no acknowledgement is awaited.
- With healthy persistence, final job/transfer mutations precede relationship
  cleanup and the retired workflow is subsequently served from history.
- Multiple roots sharing a workflow retire together only after the last root;
  a concurrently queued root prevents premature retirement.
- Awaiting and resumable manual-selection workflows remain live, then retire
  after their final explicit completion.
- Large prepared workflows release registered and never-registered contexts,
  settings, queue graphs, reverse indexes, revision state, and cancellation
  sources. Repeated workflows do not grow retained-owner counts.
- A workflow subscription's initial snapshot and first delta share one epoch,
  a remote client explicitly reusing a retired workflow ID observes the
  successor generation, and repeated snapshots for unknown IDs retain no
  stream state.
- One-shot engines retain their queue and preserve final rendering/exit status.
- A preparation failure fails and retires only its workflow and does not abort
  an unrelated queued root.

## Phase 5: consolidate persistence maintenance execution

### Production changes

1. Add one private generic maintenance executor in
   `PersistenceRuntimeHost`. It owns `EnsureStarted`, gate acquisition/release,
   requested-cancellation propagation, operational-failure recording, and
   exception rethrow.
2. Route integrity checks, backup, checkpoint, and retention through it.
   Integrity's unhealthy-result health transition remains an explicit
   operation-specific result check rather than being hidden in the helper.
3. Do not generalize startup, writer shutdown/drain, or database ownership into
   this helper; their lifecycle and cancellation semantics differ.

### Tests

- Each maintenance operation is mutually exclusive and releases the gate after
  success, failure, and caller cancellation.
- Caller cancellation propagates without marking persistence unhealthy.
- Non-cancellation failures are recorded once and rethrown.
- An unhealthy integrity result records the existing corruption/degradation
  signal through its explicit result check.

## Implementation order

1. Implement and verify leaf failure classification first because it is local
   and protects later retirement from escaped root failures.
2. Change the fixed-size DTOs, persistence readers, live queries, routes, and
   client navigation together so no parallel old/new contract survives.
3. Implement latest-attempt detail on the new fixed-size contract.
4. Add workflow retirement after history can independently serve bounded
   summaries/details and callers no longer require retained live trees.
5. Consolidate maintenance execution last as an independent low-risk cleanup.

Each phase must compile before proceeding. Focused tests run after each phase;
obsolete or implementation-coupled tests are replaced or removed rather than
mechanically rewritten to assert deleted contracts.

## Completion and release gates

- Search confirms no production reference remains to `RootJobIds`, embedded
  job descendants/source items, workflow-tree contracts/routes,
  `attemptLimit`, or unbounded workflow/child history readers.
- Every finding and locked rule above is mapped to an observable test or direct
  source inspection; narrow tests are not used as proof of broader completion.
- Debug and Release builds pass.
- The warm non-load solution suite passes in under 15 seconds with no
  application log noise and without raising worker counts.
- Existing and new `Load` tests pass separately. Add a load test only where the
  behavior cannot be established cheaply; do not disguise timing assertions as
  ordinary architecture tests.
- `git diff --check` is clean, and the final worktree audit distinguishes these
  changes from unrelated user work.
