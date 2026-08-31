# Cleanup sweep findings

## Scope

The sweep covers `Sockseek.Api`, `Sockseek.Core`, `Sockseek.Persistence`, and
`Sockseek.Server`, with the second pass concentrated on Core and Persistence
correctness and expected homeserver workloads. There is no deletion quota.
Tests and documentation support the work but are not themselves cleanup output.
Any simplification accounting includes only behavior-preserving deduplication
or genuine logic simplification in those four production projects. It excludes
tests, documentation/README/help text, other projects, and merely dead code.

Severity describes the pre-fix user impact: **high** means a core operation can
fail, hang, return the wrong content, or remain unusable; **medium** means a
recoverable but substantial correctness or performance problem; and **low**
means limited operational confusion or degradation. No finding in this sweep
was rated critical.

## Bugs fixed

### Exact remote paths rejected valid Soulseek names

**Severity: high.** If a peer shares a file whose name contains an unusual but
valid hidden character, viewing that peer's files or downloading from them can
fail even though the official Soulseek client accepts the name.

`RemotePathKey` rejected every Unicode control character even though Soulseek
paths can contain them. This prevented valid browse results from being indexed
or downloaded. Exact remote paths now reject only NUL, while local alias/path
validation remains strict. `RemotePathKey_PreservesNonNulControlCharacters`
provides the red/green regression case.

No follow-up refactor was warranted: exact remote identity and local filesystem
safety already have separate validation owners.

### One overflowing search poisoned unrelated search completions

**Severity: high.** If one search returns more results than Sockseek can record,
every later search can be labelled incomplete even when nothing was lost from
those later searches. The false warnings continue until Sockseek is restarted.

When the incomplete-search telemetry threshold was exceeded,
`PersistenceInbox` treated every later search as incomplete. It now retains the
exact IDs of searches whose results were dropped; the threshold remains an
observable health signal but no longer changes unrelated results.
`IncompleteSearchTrackingOverflow_DoesNotPoisonUnrelatedCompletions` covers the
failure. The older outage test was revised because bounding this exactness state
was the source of the bug, while genuinely lossy buffers remain bounded.

No additional abstraction was needed; the fix removes the global fallback and
keeps the exact state in its existing owner.

### Newly received chat messages ignored the read watermark

**Severity: medium.** After a user reads a conversation, receiving one new
message can make all of its older messages appear unread again.

The inbound chat snapshot counted all incoming messages already stored for a
target instead of only messages after `LastReadSequence`. A conversation could
therefore become unread again by receiving one new message after older messages
had been read. The initial count query now applies the same watermark as normal
conversation projections. `InboundSnapshotCountsOnlyMessagesAfterReadWatermark`
is the regression test.

The related projection refactor is described under performance: it centralizes
the unread calculation used by read paths.

### Chat history logging described the wrong operation

**Severity: low.** The logs can claim that chat history was deleted when a user
only marked it as read. When history really is deleted, the logs can omit the
event entirely, making troubleshooting misleading.

Marking a direct conversation or room as read logged that its history had been
deleted, while actual deletion emitted no such event. The event now occurs only
after a successful history deletion, for both target kinds.
`DeletingHistoryPublishesAReplacementWindow` verifies both the absence of the
false read log and the presence of the deletion log.

The direct/room read and deletion workflows were consolidated so the two target
kinds cannot drift independently again.

## Performance improvements

### Conversation and room pages performed N+1 queries

**Severity: medium.** Opening the chat list becomes progressively slower as the
number of conversations and rooms grows, even when the user views only one
page.

Each listed conversation or room issued separate queries for its last message
and unread count. A page of 20 conversations therefore required 41 database
commands. Correlated projections now retrieve the page, last-message data, and
unread counts in a constant number of commands. The same projection mechanism
also covers single-item and notification reads.
`ConversationPageUsesConstantDatabaseQueries` requires no more than three
commands for 20 conversations.

### History deletion materialized every message

**Severity: medium.** Deleting a long chat history can take much longer than
deleting a short one and can slow down other daemon activity while it runs.

Deleting 100 chat messages loaded all entities and then generated per-row delete
work, producing 103 database commands. `ExecuteDeleteAsync` now performs the
message removal as a set operation, while the target summary update remains
unchanged. `DeleteHistoryUsesConstantDatabaseCommands` requires no more than
four commands for 100 messages.

## Second-pass bugs fixed

### Caller cancellation was converted into upload unavailability

**Severity: medium.** If the program requesting an upload cancels or disconnects,
Sockseek can report that uploads are unavailable instead of saying that this
request was cancelled. This can prompt unnecessary retries or troubleshooting.

`UploadCoordinator.AdmitAsync` treated a caller-cancelled admission like its own
deadline expiring and returned `Unavailable`. The timeout catch now applies only
when the deadline, not the caller, caused cancellation.
`AdmissionPropagatesCallerCancellation` is the regression test.

### Persistence host cancellation was reported as ordinary shutdown or failure

**Severity: medium.** If an operator interrupts shutdown or database maintenance,
Sockseek can report a database failure even though the database is healthy.

`PersistenceRuntimeHost.StopAsync` swallowed caller cancellation into a
`Drained=false` result, and maintenance operations recorded caller cancellation
as unhealthy persistence. Caller cancellation now propagates after required
writer cleanup; maintenance cancellation no longer poisons health.
`RuntimeStopPropagatesCallerCancellationAndReleasesOwnership` also proves that
the SQLite ownership lease is released.

### Separator-built peer/file keys aliased exact Soulseek identities

**Severity: high.** Certain unusual combinations of peer usernames and folder
names can make two different files look identical to Sockseek. One result can
then disappear or be confused with the other in search and album views.

Search sessions, sorters, and incremental album/aggregate projections built
identity by concatenating username and remote path. Distinct pairs such as
`("alpha", "beta\\track")` and `("alpha\\beta", "track")` therefore collided.
All in-memory search/projection identity now uses the shared structural
`PeerPathKey`. Regression cases cover raw sessions, sorting, album folders,
aggregate tracks, and aggregate albums.

### Root files could abort album projection

**Severity: high.** If a peer shares a loose file at the top level alongside
album folders, Sockseek's album view can fail completely instead of simply
leaving out the loose file.

Album folder projection sliced paths at an unchecked directory separator. A
root-level result, or a top-level disc directory such as `CD 1`, could throw and
abort the entire projection. Root files are ignored when no folder can be
identified, while top-level disc folders remain valid.
`AlbumFolders_IgnoresRootFilesAndKeepsTopLevelDiscFolders` covers both cases.

### A failed share writer could deadlock the bounded scanner

**Severity: high.** If Sockseek cannot save a newly scanned share—for example,
because of a disk error—startup or share refresh can hang forever. The user's
shared catalog then remains unavailable or out of date.

Discovery was awaited before writer failure was observed. If the writer failed
after both bounded channels filled, producers waited forever. All pipeline
stages now cancel their shared pipeline on failure, observe the remaining tasks,
and rethrow the meaningful stage failure while preserving caller cancellation.
`ScanAsync_WriterFailureCancelsTheBoundedPipeline` previously timed out and now
completes promptly.

### Disposing the Soulseek manager left readiness waiters blocked

**Severity: high.** Stopping or restarting the daemon while an operation waits
for a Soulseek connection can leave that operation stuck forever and can
prevent shutdown from finishing.

`SoulseekClientManager.DisposeAsync` stopped its monitor without completing the
pending readiness task. Callers without their own timeout could wait forever.
Disposal now marks the manager fatally unavailable and wakes all waiters;
`DisposeAsync_WakesPendingReadinessWaiters` is the regression test.

### Search projection cache identity could silently collide

**Severity: high.** Two searches containing certain unusual characters can be
mistaken for the same search. A user can then see results filtered, grouped, or
formatted using the other search's settings.

Incremental projection state was keyed by delimiter-joined query fields and
ordinary object hash codes. Distinct delimiter-bearing queries could reuse a
projector configured for another query, and hash collisions could do the same
for settings. Typed structural projection keys now retain component boundaries
and reference identity where intended.
`SearchJob_ProjectionCacheDoesNotAliasDistinctDelimiterBearingQueries` proves
the deterministic collision case.

### Folder retrieval failures escaped or masqueraded as cancellation

**Severity: high.** While downloading an album or folder, a temporary failure
fetching the rest of the folder can abort the whole download or incorrectly say
the user cancelled it. Files that Sockseek had already found may be lost from
the job instead of being downloaded.

The generic folder-retrieval path only handled `OperationCanceledException`.
An ordinary browse failure escaped the engine even though album completion is
best effort, and a transport-originated cancellation exception was reported as
if the user had cancelled the job. Retrieval failures now produce a failed
retrieval child and preserve already-known exact album files; only cancellation
requested by the job, parent, or runtime is classified as cancellation. The
same requested-cancellation rule now covers extraction, search, top-level
folder retrieval, and exact directory-file execution.
`AlbumFolderCompletionFailure_PreservesKnownSelectionAndFailsOnlyTheRetrievalJob`
covers both ordinary and cancellation-shaped transport failures.

### A transport cancellation could permanently stop Soulseek reconnection

**Severity: high.** A single temporary network timeout can stop Sockseek from
ever trying to reconnect. Searches, browsing, chat, and transfers then remain
offline until the daemon is restarted.

The connection monitor stopped on every `OperationCanceledException`, even
when its lifetime token had not been cancelled. A timeout-shaped exception
from connection work could therefore silently end all future reconnects.
Only monitor-lifetime cancellation now stops the loop; other cancellation
exceptions are treated as transient connection failures.
`MonitorContinuesAfterCancellationExceptionNotRequestedByItsToken` is the
regression test.

### Share-catalog startup swallowed caller cancellation

**Severity: medium.** If the user stops Sockseek while it is loading the shared
file catalog, the stop request can be ignored and shutdown can take longer than
expected.

Catalog initialization could complete successfully after its caller was
already cancelled when no manifest existed. Cancellation during manifest
loading was also caught by the corrupt/stale-catalog fallback and converted
into a cache miss. Initialization and cleanup now check cancellation even on
empty catalogs, and caller cancellation is rethrown before fallback handling.
`Initialize_PropagatesCallerCancellationWithoutAManifest` is the regression
test.

### Local-files search wrapped cancellation as an aggregate failure

**Severity: medium.** Cancelling a search of local files can produce an internal
error instead of a normal cancellation message, making an expected action look
like a program failure.

The response-handler overload of the in-process Soulseek client extracted an
async result through `ContinueWith` and `Task.Result`. Normal cancellation (and
any search fault) was therefore exposed as `AggregateException`, unlike the
other search overload and the real client contract. Ordinary `await` now
preserves the original exception and cancellation semantics.
`ResponseHandlerSearch_PropagatesCancellationWithoutAggregateException` is the
red/green regression case.

### Fresh peer-browse reuse ignored a missing artifact

**Severity: high.** If saved data for a previous peer browse is deleted or
damaged, every attempt to browse that peer can repeat the same failure instead
of fetching a fresh copy.

Freshness lookup trusted only the completed registry row. If its SQLite
artifact had disappeared through manual cleanup, a partial restore, or storage
damage, every ordinary request reused the broken resource and failed later
instead of starting a replacement browse. Reuse now also requires the resolved
artifact file to exist. `FreshLookup_IgnoresACompletedResourceWhoseArtifactIsMissing`
is the red/green regression case; the historical resource remains visible until
normal retention rather than being silently rewritten.

### A synchronous idempotent submission failure was cached permanently

**Severity: high.** If a request to start a user-share download fails immediately,
an app that automatically retries the request can receive the same old failure
on every retry. The download never gets another real attempt.

The user-share submission single-flight store cleaned up a task that failed
asynchronously, but its cleanup path read `Lazy.Value` again after a submission
delegate threw before returning a task. That second read rethrew before removal,
so every retry with the same request ID replayed the cached failure. Shared task
acquisition now has its own cleanup boundary and synchronous and asynchronous
failures are both retryable. `SynchronouslyFailedSubmissionCanBeRetried` is the
red/green regression case.

## Second-pass performance improvements

### Retention and startup reconciliation used row-by-row SQL

**Severity: high.** A user with a large search, transfer, or chat history can
face very slow daemon startup and cleanup. Searches, chats, and transfers may be
delayed until thousands of old records have been processed individually.

- Search-result retention now deletes and marks a whole selected batch with two
  set-based statements instead of one delete/update pair per search.
- Runtime startup reconciliation now updates interrupted searches, jobs,
  transfers, attempts, and sessions in set-based statements. A 50-row case per
  entity remains constant-command rather than issuing 207 commands.
- Pending chat message reconciliation is one set-based update; the 50-message
  regression fell from 51 commands to a constant count.
- Chat retention deletes a batch and repairs all affected direct/room
  watermarks with set-based correlated updates. The 12-target regression fell
  from 49 commands to no more than eight.

### Search persistence repeatedly rescanned buffered and stored results

**Severity: high.** A search that receives many results can make Sockseek use
far more CPU and memory than the results themselves require. Results may appear
slowly, and the daemon can become unresponsive or run out of memory.

Terminal search persistence previously performed duplicate lookup and local
deduplication once per pending result batch. It now combines ordered pending
batches into one deduplication pass. Existing-row lookup also filters by both
username and filename instead of loading every stored result from a matching
user. In-memory buffering now keeps an explicit per-search result count and a
queue, removing repeated full sums and front-removal from `List<T>`.

Search sessions also retained the same result scalars in two objects, copied
the attribute array twice, and maintained a concurrent dictionary containing
the original response/file pair in addition to the ordered raw-result list.
The ordered list now owns exact identity and legacy snapshot references once;
projection properties and counts derive from that single retained result.
This reduces per-result memory without changing projection or snapshot output.

### Chat read and ingress operations scaled SQL work with message count

**Severity: medium.** Marking a large conversation as read or reconnecting after
many messages can become noticeably slow and can delay other daemon activity.

Marking a target or notification range read materialized every matching
notification and updated rows individually. A 100-notification target required
104 commands. Both paths now use one set-based update. Inbound private-message
replay detection also performed one indexed lookup per message; exact replay
keys for a persistence batch are now preloaded once and kept in a structural
dictionary, including duplicates arriving within the same batch.
`ReadOperationsUpdateManyNotificationsWithConstantDatabaseCommands` covers the
two update paths and bounds inbound select commands while retaining idempotency
coverage.

### Browse download selection repeated work per selected ID and root

**Severity: high.** After selecting many folders or files from a large peer
share, a user can wait several seconds before the download even begins. The
delay grows sharply as more separate folders are selected.

Selected browse IDs were inserted into temporary tables with one command per
ID, directory/file redundancy checks scanned the growing root list, and each
canonical directory executed a separate subtree query. IDs are now loaded with
SQLite `json_each`, ancestor checks use an exact path set, and all canonical
roots are expanded by one recursive query through the indexed parent relation.
The query visits selected subtrees instead of comparing every root with every
stored directory. Multi-root, control-bearing, locked, and antichain selection
behavior remains covered by the artifact-store tests.

### Discovery progress persisted once per file

**Severity: medium.** When one peer returns hundreds or thousands of files for a
search, Sockseek can repeatedly save nearly identical progress updates. Search
progress may lag, and other daemon actions can slow down at the same time.

`Searcher` subscribed job state to the per-file raw event, so a single response
with hundreds of files caused hundreds of state-store updates. It now publishes
discovery progress once per response while the final count remains exact.
`SearchAlbum_CoalescesDiscoveryProgressPerResponseAndPublishesExactFinalCount`
proves both properties.

### Upload queue lookup ignored the scheduler's exact index

**Severity: medium.** When many uploads are queued, responding to another peer's
upload request can become progressively slower, making the user's share less
responsive.

`UploadCoordinator.GetQueueEstimate` scanned every live work item even though
`UploadScheduler` already owns the exact peer/path duplicate index. The lookup
now goes directly through that index under the scheduler lock.
`QueueEstimate_ResolvesExactPeerAndPathFromTheSchedulerIndex` covers the path.

### Large download structures eagerly created one task per item

**Severity: high.** Starting a huge directory or playlist can make Sockseek
prepare every file at once, causing a large memory spike, a long pause, or a
crash even though only a few files can download at the same time.

Directory transfers, album files, job-list children, and root submissions used
`Task.WhenAll(source.Select(...))`, creating and retaining one async task per
item even though real work was semaphore-bounded. They now share a bounded
fan-out helper that invokes items in source order, runs every item, retains
failure observation, and limits active scheduling to configured job/extractor
concurrency. Completion and later asynchronous stages remain concurrent and may
finish out of order. Ordered invocation preserves input priority at the first
concurrency boundary without recreating one task per item. `BoundedAsyncTests`
verifies source-order invocation, bounded activity, and all-item failure
isolation.

### Fixed-size cursors decoded before rejecting oversized input

**Severity: medium.** A broken or malicious client can send an extremely large
page token and make the daemon waste memory and CPU before rejecting it. Other
users can experience a pause or slowdown as a result.

Historical job and transfer readers could allocate for arbitrarily padded
Base64 cursors before discovering that the decoded token was invalid. They now
reject input longer than the fixed cursor representation before decoding.

### Live child updates scanned all retained jobs

**Severity: high.** As a long-running daemon accumulates completed download
history, progress updates for new downloads can become slower and slower. The
CLI or GUI may eventually feel sluggish during otherwise normal downloads.

Every song/file-child progress update searched every snapshot retained by
`EngineStateStore` to find albums, directories, aggregates, and lists that
embed that child. Since the store currently retains terminal workflow state,
the cost of an active download grew with the daemon's entire job history. The
store now maintains exact nested-job-to-container indexes as snapshots change,
making containing-record refresh proportional to the actual number of owners.
The existing recursive job-list, album, aggregate, workflow-summary, and live
transfer projection tests cover the indexed refresh behavior.

### Source-file locks accumulated across engine lifetimes

**Severity: medium.** A user who repeatedly starts downloads from different CSV
or text files can see Sockseek's memory use grow over time and never fall back,
even after those downloads finish.

Removing completed items from CSV/text sources added one static semaphore per
distinct source path and never removed it. The process therefore retained path
strings and synchronization objects forever, including across engine restarts.
Source rewrites now use a fixed 64-stripe lock set, which preserves same-path
exclusion with constant memory. The duplicated CSV/text read-modify-write flow
was consolidated into the same owner; existing source-mutation tests verify the
file and header behavior.

### Compiled title-template cache had user-controlled cardinality

**Severity: medium.** If a user or connected app submits downloads with many
different naming templates, Sockseek can permanently retain memory for every
template until it is restarted.

`TrackTemplateParser` permanently cached a compiled regex for every distinct
template. Per-submission download settings can supply arbitrary templates, so a
long-running daemon could accumulate regexes and their template strings without
limit. The cache now retains at most 128 recent insertions using a small FIFO;
all existing template parsing and update tests remain unchanged.

### Auto-profile summaries retained terminal workflow state

**Severity: low.** A daemon that processes many downloads using auto profiles
can slowly retain memory for workflows that already finished. Results remain
correct, but memory use grows unnecessarily over a long session.

The auto-profile reporter kept every workflow's counted job IDs, profile names,
and per-kind counters after emitting its one terminal summary. That state has no
later role but accumulated for the daemon lifetime. Terminal summary emission
now removes the exact workflow state; `FinalSummary_ReleasesPerWorkflowCountingState`
proves both release and one-summary behavior.

## Architectural simplifications

- The string-valued `DownloadSettingsDeltaDto` contract and its parallel merge
  machinery were replaced by the existing typed `DownloadSettingsPatchDto`.
  Difference construction and patch combination now have one typed owner.
- `SockseekApiClient` shares required-response, optional-response, POST, and
  DELETE request mechanics instead of repeating status and deserialization
  handling in each endpoint wrapper.
- Live daemon, workflow, chat, and browse subscriptions share their connection,
  buffering, recovery, and rollback lifecycle while keeping mode and
  compatibility decisions explicit at each call site.
- Batch album aggregation reuses the incremental aggregation engine instead of
  maintaining a second implementation. Duplicate-folder behavior is retained.
- Settings cloning uses a shared shallow clone for scalar state and explicit
  deep copies for nested mutable values. This replaces handwritten copies of
  every scalar property without sharing mutable download settings.
- Peer-browse artifact readers share one file/attribute row-folding routine
  instead of maintaining three nearly identical materializers.
- Server endpoint wrappers, optional query parsing, job resolution, settings
  resolution, user-profile/browse network serialization, and direct/room chat
  workflows now use common local owners where their behavior is genuinely the
  same. Route-specific errors and compatibility rules remain explicit.

## Verification

- First-pass verification passed all 1,284 tests in 13.36 seconds with no
  application log noise.
- Debug and Release solution builds pass. The builds retain 69 pre-existing
  MSTest analyzer warnings and add no compilation errors.
- The final warm Debug suite passes all 1,310 non-load tests in 14.47 seconds,
  below the 15-second repository target, with no application log noise.
- All nine separately categorized load tests pass in both Debug and Release,
  including the 100,000-file peer-browse index and 10,000-event chat cases.
- A concurrently scheduled interactive CLI test exposed an ordering assumption
  in its fixture. The second search is now explicitly gated behind the first
  accepted selection; the unchanged behavior assertion passed five consecutive
  isolated runs and the final suite.
- Test-duration profiling found one broad daemon-store parity test duplicated by
  focused client-store, local-backend, and remote multi-workflow subscription
  coverage. Removing that duplicate restored the warm target without changing
  worker counts or production behavior; it is excluded from cleanup accounting.
- Targeted cancellation, folder-retrieval, interactive-selection, replay,
  retention, browse-selection, scanner-failure, and reconnect regressions pass.
- `git diff --check` reports no whitespace errors.

## Resolved product decisions

- **Severity: high.** If a user leaves the daemon running and completes many
  downloads, its memory use can keep growing because finished jobs are never
  released. It can eventually slow down or run out of memory while idle.

  The daemon's live `DownloadEngine` queue and `EngineStateStore` retain every
  submitted terminal job tree and its prepared settings indefinitely, even
  though persistence has its own history and retention policy. Terminal live
  state is not a fallback history store: after publishing the final immutable
  state and offering self-contained terminal mutations to persistence, the
  daemon will retire the workflow from every live owner. Retirement will not
  wait for SQLite confirmation and will still happen when persistence is
  disabled or unhealthy. Healthy persistence provides history; disabled
  persistence provides none after retirement; unhealthy persistence may leave
  history incomplete and must expose that degradation through health and logs.
  There will be no in-memory history fallback, grace-period option, or other
  retention policy for terminal live state.

  This follows the useful part of slskd's model: operational in-memory state is
  released while history comes from SQLite. Unlike slskd, which makes SQLite
  authoritative even for running transfers and uses in-memory SQLite in its
  volatile mode, Sockseek will keep persistence optional and will not make its
  availability a dependency of download execution.

- **Severity: high.** After downloading a very large playlist, opening its
  history or details can load information for every song at once. The client or
  daemon can become slow, use excessive memory, or time out.

  Historical workflow pages return compact summaries but first read every job
  in each selected workflow. Ordinary historical workflow detail does the same
  before filtering to roots, job detail embeds every direct child, and live
  album detail reconstructs and embeds every track despite its contract saying
  otherwise. `SockseekApiClient.GetJobsAsync` introduces the opposite failure:
  it sounds exhaustive but silently returns only the first page of 100 jobs, so
  remote completion and exit-status accounting can miss later failures.

  Ordinary summaries and details will not contain collections that grow with a
  workflow's descendants or source items. Workflow summaries will come from a
  fixed-size persisted projection and replace the unbounded `RootJobIds` list
  with a root count. Workflow detail will contain fixed-size workflow data, and
  job detail will contain its summary, scalar/aggregate payload, and optionally
  a child count, but not child summaries. Clients will page roots, direct
  children, or every workflow job through the existing `/api/jobs` collection;
  that query will gain a `parentJobId` filter. The full-tree endpoint will be
  removed, and clients that need a complete tree will assemble it progressively
  from paged flat jobs.

  The same rule applies to descendant or source-item collections duplicated in
  job payloads, including album tracks and directory transfer-plan entries;
  those items must use an appropriate paged resource. The misleading
  all-items `GetJobsAsync` helper will be removed or made explicitly paged, and
  callers that truly need every job must consume every page rather than silently
  truncating or forcing one unbounded response.

- **Severity: medium.** When a file has been retried more than 200 times, its
  detail view silently leaves out the later attempts. A user can wrongly
  conclude that those retries never happened because there is no truncation
  warning.

  Transfer detail currently returns only the first `attemptLimit` attempts (200
  by default) without indicating truncation, although a separate paged attempts
  endpoint exists. Replace the embedded collection with one fixed-size
  `LatestAttempt` and retain `AttemptCount`; remove `attemptLimit`. The latest
  attempt supplies the attempt-specific source, paths, timing, outcome, and
  failure that make the detail response useful, while
  `/api/transfers/{id}/attempts` remains the cursor-paged complete history.

  For an active transfer, the latest attempt must come from a live projection
  rather than depending on SQLite being enabled or caught up. Retain one latest
  attempt record per live transfer and release it with that transfer. After
  terminal retirement, retained detail reads the latest persisted attempt.
  Tests should prove the detail remains fixed-size, the latest attempt is
  correct for live, merged, and retained transfers, `AttemptCount` reveals
  earlier attempts, and paging retrieves attempts beyond 200 without gaps or
  duplication.

## Architectural follow-up assessment

Most findings are adequately addressed by their current local fixes and focused
shared owners. A broad cleanup rewrite would add risk without improving the
relevant boundaries. The following follow-up work is justified.

### Centralize leaf-download failure classification

This should be completed before treating the cancellation fixes as finished.
`DownloadExecutorCoordinator` currently converts every
`OperationCanceledException` into a cancelled job, and
`ExactPeerFileTransferRunner` reports every such exception as a requested
transfer cancellation. This bypasses the requested-token check already owned by
`JobOrchestrator`. An ordinary `RemoteFileJob` transfer exception can also escape
the job boundary and restart the daemon engine instead of failing only that job.

Give leaf download execution one exception-to-outcome boundary. It should:

- classify cancellation only when the job, parent, runtime, stale-transfer, or
  another explicitly owned token was actually cancelled;
- treat an unrequested cancellation-shaped transport exception as a transfer
  failure;
- convert peer-local and file-local failures into a terminal outcome for the
  affected job rather than allowing them to escape into the daemon engine; and
- retain the more specific retry, stale-transfer, and manual-skip behavior
  already owned below that boundary.

Regression tests should cover requested and transport-originated cancellation
for remote files, remote directories, songs, and albums, plus isolation of an
ordinary remote-file failure from unrelated root jobs. This should remain a
contained download-runtime refactor rather than a universal cancellation
framework: profile fetches, daemon-owned shared operations, timeouts, and
shutdown deliberately have different ownership semantics.

### Retire terminal live workflow state

This is the one finding that warrants a large architectural refactor. Terminal
workflow trees and settings are retained by `DownloadEngine.Queue`,
`DownloadJobTracker`, `DownloadJobContextStore`, and `EngineStateStore`. The new
reverse indexes make live child updates efficient but do not bound memory.

Introduce explicit, atomic workflow retirement:

1. Keep active, waiting, and resumable workflows in live state.
2. When a workflow is fully terminal, publish its final immutable state and
   offer self-contained terminal mutations to the persistence inbox.
3. Atomically remove it from the engine queue, job and display indexes,
   prepared contexts, command targets, state projections, nested-job indexes,
   and persistence-adapter relationship bookkeeping. Do this regardless of
   whether persistence accepted or durably committed the mutations.
4. Serve retired history only from persistence through the existing historical
   query boundary. Disabled persistence therefore has no retired history, and
   persistence degradation may make recent history incomplete.
5. Surface rejected, evicted, or failed terminal persistence through bounded
   health state and operator logs rather than retaining the workflow graph as
   emergency history.

This work should have its own plan because retirement must preserve final live
events, immutable persistence handoff, command availability, and whole-workflow
consistency. It must not add an in-memory history fallback, a terminal grace
period, or a database-acknowledgement dependency.

### Bound workflow history and navigation

`JobHistoryReader.GetWorkflowsAsync` first pages workflow IDs and then loads
every job belonging to those workflows merely to construct list summaries.
Persisted workflow pages should instead read a fixed-size projection containing
the title, state, root count, and active/failed/completed counts. The historical
facade can use `EngineStateStore.GetWorkflowSummary` for a still-live workflow
and the persisted summary after retirement.

Make the public navigation boundary consistently paged at the same time:

- remove descendant lists from workflow detail, job detail, and job payloads;
- add a `parentJobId` filter to the existing cursor-paged jobs collection;
- remove the recursive whole-workflow tree response and build trees client-side
  from flat pages when needed;
- replace unbounded workflow root IDs with a root count; and
- remove or correct `SockseekApiClient.GetJobsAsync`, whose first-page-only
  behavior currently causes incomplete remote completion accounting.

Tests should use workflows larger than one page and prove that list/detail
responses stay bounded, direct-child and whole-workflow traversal reaches every
job across cursors, remote exit status observes a failure beyond the first page,
and no detail payload re-inlines the traversed descendants. This is one coherent
API/Persistence/Server refactor; it should reuse the jobs cursor contract rather
than introduce nested paging conventions for individual DTOs.

### Consolidate persistence maintenance execution

`PersistenceRuntimeHost` repeats the same maintenance gate, caller-cancellation
propagation, failure-health recording, and gate release for integrity checks,
backup, checkpointing, and retention. Move that lifecycle into one private
maintenance-execution helper while leaving each operation's result-specific
health checks explicit. This is a small local refactor intended to prevent the
cancellation behavior from drifting again.

### Areas that should not receive a larger refactor

- Keep exact peer/path structural identity separate from the case-folded,
  normalized comparison identity used by the local sharing catalog. Combining
  them would erase an intentional semantic distinction.
- Keep chat projections, unread calculations, direct/room workflows, bulk read
  updates, and set-based deletion with their current feature owners. A generic
  repository layer would obscure the SQL and transaction behavior.
- Keep peer-browse selection and artifact lifecycle in the artifact store.
  Splitting the class solely because it is large would not improve ownership.
- Keep `BoundedAsync` as the common large-fan-out primitive and the share
  scanner's multi-stage pipeline local to the scanner; they solve different
  concurrency shapes.
- Keep upload indexing, search buffering, progress coalescing, striped source
  locks, bounded template caching, auto-profile cleanup, fresh-browse reuse,
  and idempotent submission as focused local mechanisms. There is currently
  only one meaningful owner for each, so generic scheduler, cache, batching, or
  single-flight frameworks would add maintenance overhead without preventing
  another known bug.
