I would build this around four decisions:

1. **SQLite as the initial database, with EF Core as the primary data-access layer.**
2. **A dedicated `Sockseek.Persistence` project.**
3. **The running engine remains authoritative for live operations; the database is a durable projection and history store.**
4. **All database writes go through one persistence pipeline that batches ordinary changes, coalesces progress, and prioritizes terminal state.**

Dapper can still be added for a few measured bulk or reporting operations, but I would not make it the main persistence technology.

The important architectural distinction is:

> Persistence should record what the engine is doing, but it should not become part of the engine’s real-time control loop.

That is the most important lesson to take from slskd.

One terminology note: Sockseek is moving toward a full Soulseek client, not just a
smart downloader. In this document, "engine" should be read as "authoritative
domain runtime" rather than "one monolithic `DownloadEngine`". Downloads,
uploads, search, sharing, chat and user browsing can share persistence
infrastructure without sharing one giant event bus or database-shaped core API.

---

## What the existing project already gets right

The project is in a better position for this than it might initially appear.

There are already several useful boundaries:

* `DownloadEvents` describes meaningful download-workflow changes rather than forcing callers to poll.
* `EngineStateStore` is already a projection of mutable engine state into stable records and API DTOs.
* `ServerEventCoalescer` already distinguishes latest-value state such as download progress from event-like activity.
* Jobs and workflows have stable `Guid` identities.
* Search results have sequence and revision numbers.
* `TODO.md` already distinguishes durable state from ephemeral activity and anticipates startup snapshots, daemon-wide views, compact deltas, and sequence recovery.

Those are exactly the concepts a persistence layer needs.

There are also some constraints that should affect the design:

### Current `Job` objects are not persistence entities

`Job` contains:

* A construction-time-only `Id`.
* A process-local static display ID allocator.
* A `CancellationTokenSource`.
* Mutable state changed from background threads.
* `INotifyPropertyChanged`.
* References to settings and, in subclasses, network-library objects.

The comments in `Sockseek.Core/Jobs/Job.cs:35-51` already identify the longer-term direction: immutable snapshots and a reducer.

Consequently, historical database records should **not** be loaded back into the current `Job` hierarchy. A historical search job is not a dormant `SearchJob`; it is a persisted search-job record. A historical transfer is not an `ActiveDownload`.

### Mutable objects must not cross persistence boundaries

`DownloadEvents` now publishes immutable Core changes rather than mutable `Job`
references, and architecture tests guard the public event/snapshot contracts
against known runtime types. The persistence adapter should preserve that
boundary:

> Map immutable Core changes into compact persistence-owned mutations before placing anything on an asynchronous queue.

Persistence must not enqueue live `Job`, `SearchSession`, `FileCandidate`,
`SearchResponse`, `Soulseek.File`, or cancellation-token objects.

### The current REST query path is live-state-only

Endpoints such as `/api/jobs`, `/api/jobs/{id}`, and search-result endpoints query `EngineStateStore` or `EngineSupervisor` directly. Once history exists, these endpoints need a query facade capable of reading:

* Live state from memory.
* Historical state from SQLite.
* A merged result during the short period where an active job is also represented in the database.

This is a more consequential change than adding `SaveChanges()` calls.

---

# ORM and database choice

## Use EF Core 10 with SQLite

Suggested packages:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.x" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design"
                  Version="10.x"
                  PrivateAssets="all" />
```

`Microsoft.Data.Sqlite` will arrive transitively, though using it directly for a narrowly scoped bulk operation is also reasonable.

EF Core gives you:

* Schema migrations.
* Explicit entity configuration and constraints.
* Transactions.
* LINQ query composition.
* Change tracking where useful.
* `AsNoTracking` for history queries.
* A conventional way to evolve the schema across Sockseek releases.

A `DbContext` is intended to be a short-lived unit of work and is not thread-safe. The persistence writer should therefore use `IDbContextFactory<SockseekDbContext>` and create a new context for each batch or transaction, rather than holding a singleton context inside a hosted service. ([Microsoft Learn][1])

### Why not Dapper as the default?

Dapper describes itself as a micro-ORM for developers who like SQL but want less ADO.NET boilerplate. In other words, writing and maintaining SQL remains its core programming model. ([GitHub][2])

That can be excellent for a SQL-first service, but it conflicts somewhat with your stated concern about SQL becoming scattered across the application. A Dapper-first architecture would still need separate answers for:

* Migrations.
* Schema versioning.
* Reusable query composition.
* Change detection/upserts.
* Provider-specific types.
* Mapping conventions.
* Relationships and cascade behavior.

Dapper is appropriate after profiling identifies a concrete hotspot, for example:

```csharp
SearchResultBulkWriter.InsertBatch(...)
TransferStatisticsQueries.GetDailyTotals(...)
```

Any such SQL should remain entirely within `Sockseek.Persistence`, in named query/bulk-writer classes with integration tests.

### Other possibilities

| Technology              | Verdict                                                                                |
| ----------------------- | -------------------------------------------------------------------------------------- |
| EF Core                 | Best default for this project                                                          |
| Dapper                  | Add selectively after profiling                                                        |
| LINQ to DB              | Capable, but provides less benefit than EF here and has a smaller ecosystem            |
| LiteDB/document stores  | Poorer fit for relations, filtering, migrations, transfer statistics, and chat history |
| NHibernate              | More machinery than the project needs                                                  |
| PostgreSQL from day one | Operationally expensive for a self-hosted desktop/appliance-style service              |
| Event-store database    | Unnecessary; this need not be an event-sourced system                                  |

SQLite is a good deployment default because it requires no separate database service. It does, however, serialize writes even in WAL mode. WAL allows readers and a writer to operate concurrently, but only one writer may write at a time. That is a strong reason to intentionally create one application-level database writer. ([SQLite][3])

One subtlety: SQLite does not provide true asynchronous file I/O through `Microsoft.Data.Sqlite`; its asynchronous APIs execute synchronously. Isolating persistence inside its own writer service therefore matters more than whether the code spells the call `SaveChangesAsync`. ([Microsoft Learn][4])

## Do not support multiple providers prematurely

Design use-case-level interfaces so that SQLite is replaceable, but do not attempt to make the first schema transparently portable to PostgreSQL.

EF requires separate migration sets for multiple providers, and provider behavior differs around data types, generated values, JSON, migrations, and concurrency. ([Microsoft Learn][5])

PostgreSQL support should be a deliberate feature, probably motivated by multi-instance operation or users putting the database on another machine.

---

# Create `Sockseek.Persistence`

I would use the name **`Sockseek.Persistence`** rather than `Sockseek.Data`. `Data` is ambiguous because it can mean imported media, API data, search data, or settings; `Persistence` states the project’s responsibility clearly.

A reasonable first structure is:

```text
Sockseek.Persistence/
  Sockseek.Persistence.csproj

  SockseekDbContext.cs
  SockseekDbContextFactory.cs

  Entities/
    RuntimeSessionEntity.cs
    JobEntity.cs
    SearchJobEntity.cs

  Configurations/
    RuntimeSessionConfiguration.cs
    JobConfiguration.cs

  Migrations/

  Write/
    PersistenceMutation.cs
    PersistenceInbox.cs
    PersistenceWriter.cs
    PersistenceWriterHostedService.cs
    SearchResultBatch.cs

  Read/
    JobHistoryReader.cs
    SearchHistoryReader.cs      # added with search-result persistence
    TransferHistoryReader.cs    # added with transfer persistence

  Sqlite/
    SqliteInitializer.cs
    SqliteMaintenanceService.cs
```

Add `SearchResultEntity`, `TransferEntity`, and any future
`TransferAttemptEntity` in the vertical slice that persists those records.

And in Server:

```text
Sockseek.Server/
  Persistence/
    EnginePersistenceAdapter.cs
    HistoricalQueryFacade.cs
```

The dependency direction should be:

```text
Sockseek.Core
      ↑
Sockseek.Persistence       Sockseek.Api
      ↑                       ↑
             Sockseek.Server
```

The exact arrows may vary depending on where immutable snapshot contracts live, but two rules matter:

* `Sockseek.Core` must never reference EF Core or `Sockseek.Persistence`.
* Persistence must never map the live `Job`, `SearchSession`, or Soulseek.NET object graph as EF entities.

It is acceptable for `Sockseek.Persistence` to reference Core-owned **immutable records and enums**. It should not depend on mutable engine objects.

## Do not create a generic repository abstraction

Avoid interfaces such as:

```csharp
IRepository<TEntity>
IUnitOfWork
```

EF Core already represents a unit of work and repository-like collection abstraction. A generic wrapper generally obscures useful EF functionality without establishing a meaningful application boundary.

Use specific interfaces instead:

```csharp
public interface IJobHistoryReader
{
    Task<JobHistoryPage> GetJobsAsync(JobHistoryQuery query, CancellationToken ct);
    Task<PersistedJobDetail?> GetJobAsync(Guid jobId, CancellationToken ct);
}

public interface ISearchHistoryReader
{
    Task<SearchResultPage> GetRawResultsAsync(
        Guid searchJobId,
        long afterSequence,
        int limit,
        CancellationToken ct);
}
```

Likewise, the write side should accept persistence mutations, not expose `DbSet`s or repositories to Server.

---

# Persistence is a projection, not object serialization

Do not attempt to serialize the full Core object graph or use table-per-hierarchy mapping for every job subclass.

Use a hybrid schema:

* Normalize values that are filtered, sorted, joined, constrained, or aggregated.
* Store infrequently queried type-specific details as versioned JSON.
* Give high-volume collections such as search results and transfers proper tables.

Do not store API DTO JSON. API contracts and database contracts evolve for different reasons.

## Suggested schema

### `runtime_sessions`

```text
id
started_at_utc
stopped_at_utc nullable
shutdown_kind nullable
version
```

Create one row at daemon startup and mark it stopped on clean shutdown. Rows
created or updated by the runtime can carry `last_runtime_id` when useful.
Startup interruption is then a deliberate database transition: nonterminal rows
last touched by an unfinished runtime become interrupted and receive a new
persisted revision. Do not compare in-memory entity revisions across runtime
incarnations.

### `workflows` deferred

There is no separate Core workflow object today; workflow state is derived from
jobs. Do not include a `workflows` table in the first migration. Persist indexed
`workflow_id` on jobs and derive historical workflow state from jobs in the
reader.

Add a materialized `workflows` table later only if workflow queries are
measurably expensive or workflows gain independently owned fields/lifecycle. If
that table is added, treat it as disposable cached projection data and implement
one deterministic reducer shared by initial creation, incremental updates, and
repair/rebuild operations.

### `jobs`

```text
id
workflow_id
parent_job_id
source_job_id
result_job_id
last_runtime_id

display_id
kind
lifecycle_state
activity_phase
activity_until_utc
terminal_outcome
skip_reason
cancellation_source

failure_reason
failure_message
failure_detail

item_name
query_text

created_at_utc
started_at_utc
updated_at_utc
completed_at_utc

revision
payload_schema_version
payload_json
```

Use foreign keys for the graph relationships, but be careful with cascades. Deleting one source job should not unexpectedly delete a workflow’s entire history.

The first persistence slice must close the source/follow-up relationship gap.
Parent relationships are available from job registration and result relationships
from result-created changes, but `source_job_id` is currently maintained by the
server state store rather than represented in a Core change. Prefer enriching
registration so a job is registered once with its complete structural identity:

```csharp
JobRegisteredChange(
    JobSnapshot Job,
    Guid? ParentJobId,
    Guid? SourceJobId)
```

If registration cannot own this cleanly, the command side must emit an explicit
persistence mutation when follow-up jobs are created. Do not leave source
relationships as a server-only projection if persisted job history needs them.

Continue handling result production through `JobResultCreatedChange`; do not
force parent, source, and result semantics into a single relation enum.

The JSON payload can carry job-kind-specific information that is useful for display but not generally queried. It must have a schema version.

### `search_jobs`

```text
job_id
query
revision
result_count
locked_file_count
is_complete
completed_at_utc
result_persistence_state
results_pruned_at_utc nullable
```

`result_persistence_state` should distinguish at least:

```text
Complete
Incomplete
Pruned
NotPersisted
Interrupted
```

This prevents API consumers from confusing "the search returned no results" with
"raw results were pruned, lost during degraded mode, or never persisted."

### `search_results`

```text
id
search_job_id
sequence
revision

username
remote_filename
size_bytes
bit_rate
sample_rate
duration_seconds
extension

upload_speed
has_free_upload_slot
attributes_json

observed_at_utc
```

Important constraints and indexes:

```text
UNIQUE(search_job_id, username, remote_filename)
UNIQUE(search_job_id, sequence)
INDEX(search_job_id, sequence)
INDEX(search_job_id, username)
```

`SearchRawResult` currently retains `Soulseek.SearchResponse` and `Soulseek.File`. Do not try to EF-map those types. Define a Sockseek-owned persisted search-result record containing every fact needed to reproduce projection and ranking.

At minimum, that likely includes more than the current raw API DTO:

* Username.
* Remote filename.
* Size.
* Bit rate.
* Sample rate.
* Duration.
* Extension.
* Upload speed.
* Free-slot status.
* Relevant file attributes.
* Sequence and revision.

This also suggests a useful Core refactor: make `SearchResultProjector` consume a Sockseek-owned neutral input record rather than a tuple of third-party Soulseek types. Both a live `SearchSession` and the database can then produce that record. Historical searches can be reprojected using new settings without fabricating Soulseek library objects.

Before historical reprojection is promised, file attributes need a stable Sockseek-owned representation. Do not persist `FileAttributeSnapshot.Type` as only a Soulseek.NET enum name. Store a stable code, plus a display name if useful, so old rows remain interpretable if the third-party enum names change.

The same rule applies to download candidates. Persist a copied, Sockseek-owned
candidate snapshot; do not persist `FileCandidate`, `SearchResponse`, or
`Soulseek.File` as object graphs. Do not persist the current NUL-delimited
in-memory candidate key as a durable database value. If a compact durable
candidate key becomes necessary, use a documented hash over a canonical byte
representation; otherwise the normalized candidate columns are enough.

### `transfers`

Transfers should not merely be more columns on `SongJob`.

```text
id
job_id nullable
workflow_id nullable

direction
source
username
remote_path
local_path

state
total_bytes
transferred_bytes
attempt_count

created_at_utc
started_at_utc
last_progress_at_utc
completed_at_utc

failure_reason
failure_message
revision
```

Reasons to give transfers their own identity:

* One job can make multiple attempts.
* A candidate can change after a failed attempt.
* Future uploads may not originate from a user-submitted job.
* Upload and download history should use the same transfer concept from the first persistence schema. Downloads can be the first producer, but the identity model, table shape and change contracts should be direction-neutral now.
* A logical job and a protocol transfer have different lifetimes.

Use a Sockseek-owned transfer state/outcome enum in the database. `TransferSnapshot.State`
currently carries a string produced near the protocol boundary; that is useful
for live display, but it is not a stable durable contract.

Add explicit semantic terminal changes before persisting transfers broadly:

```csharp
TransferCompletedChange
TransferFailedChange
TransferCancelledChange
```

`TransferCompletedChange` should be emitted only after Sockseek has a valid final
local path, including any `.incomplete` rename/finalization and duplicate-cache
update. Low-level peer `Completed` callbacks are not sufficient as the durable
terminal barrier. Failure/cancellation should also be emitted from the logical
transfer boundary, not only from individual attempt callbacks.

#### Transfer identity model

Use `TransferId` for a logical file movement, not for a job and not for every
low-level protocol callback.

For downloads, create a new transfer when Sockseek starts moving one selected
candidate to one local output path. That means:

* A `SongJob` can have zero transfers when it is skipped, already exists, or
  satisfied without a peer/fallback transfer.
* A `SongJob` can have multiple transfers when candidate A fails and candidate B
  is tried.
* Reconnect retries or repeated `SoulseekClient.DownloadAsync` calls for the same
  candidate/output path are `transfer_attempts` under the same `TransferId`.
* A stale-cancelled peer call is an attempt outcome, not a separate logical
  transfer.
* Album downloads do not need one parent album transfer. Each embedded track/file
  transfer belongs to the concrete child `SongJob`; the album job remains the
  orchestration parent.
* Uploads should use the same transfer schema when they are implemented. They
  can allocate transfers without a job id, but still have a direction, user,
  remote/local path, state, progress and attempts.

This keeps three identities separate:

```text
JobId             user/workflow intent and orchestration state
TransferId        one logical file movement for one selected source/target pair
TransferAttemptId one low-level protocol/fallback attempt within that movement
```

The current `StaleDownloadCoordinator` also allocates an attempt id, but that id
is local to stale detection and disappears when the watched peer call completes.
It should not become the durable `TransferId`.

Once `TransferId` exists, active transfer tracking should also move away from
remote filename keys. A filename alone is not unique across users, workflows or
simultaneous downloads; `TransferId` is the right runtime and persistence key.

### `transfer_attempts`

This table should be deferred from the first migration unless the implementation
also adds a complete attempt lifecycle: attempt id allocation, attempt start,
attempt success, attempt failure, attempt kind, and terminal outcome. The current
event contract has `DownloadAttemptFailedChange`, but no attempt-start event and
no successful-attempt completion event. A half-modeled attempts table would be
more misleading than adding it cleanly in a second migration.

```text
id
transfer_id
attempt_number
attempt_kind
username
remote_path
local_path
started_at_utc
completed_at_utc
outcome
failure_type
failure_message
```

Until then, persist the logical transfer plus aggregate attempt count/failure
summary. Progress belongs on `transfers`; complete attempt transitions and
failures belong in `transfer_attempts` once that lifecycle exists.

### Optional `activity_events`

This can store useful diagnostic history:

```text
id
job_id nullable
workflow_id nullable
transfer_id nullable
event_type
level
occurred_at_utc
payload_json
```

It should have bounded retention and must not be the source of truth for current job state.

This is not full event sourcing. The normalized current-state records remain authoritative; activity events are an audit/diagnostic supplement.

---

# The download progress flow

Yes, there should be a central coalescer—but it should be a **persistence-specific writer buffer**, not the existing SignalR coalescer.

The current `ServerEventCoalescer` is optimized for:

* A 200 ms UI update cadence.
* Latest-value network events.
* Workflow snapshot substitution.
* Ordering state before activity.

Persistence has different requirements:

* Terminal state must be high-priority and never intentionally discarded while the process remains alive.
* State must remain monotonic.
* Search results must be inserted, not latest-value-coalesced.
* Transactions must update related records consistently.
* Shutdown must drain pending writes.
* Database failures must not stop active transfers.

The concepts can share a small generic latest-value accumulator, but the complete coalescer should not be shared.

## Proposed flow

```text
Soulseek progress callback
        │
        ▼
Downloader / engine reducer updates live state
        │
        ▼
Engine emits semantic event
        │
        ▼
Persistence adapter maps immutable Core changes
into compact persistence mutations
        │
        ├── progress ──────► latest-progress[TransferId] = snapshot
        │
        ├── search result ─► per-search insertion batch
        │
        └── state change ──► durable mutation channel
                                │
                                ▼
                     single PersistenceWriter
                                │
                         short EF DbContext
                                │
                          SQLite transaction
```

### Progress is state, not an event log

A file may emit hundreds or thousands of progress callbacks. There is almost no value in storing every one.

For each transfer, retain only:

```text
latest transferred byte count
latest total
latest progress timestamp
latest revision
```

Flush approximately every 2 to 5 seconds to start, plus an exact terminal flush.
Version one does not resume interrupted transfers, so persisted byte progress is
mostly observational. Make the interval configurable and shorten it only when a
real UI, recovery, or reporting requirement justifies the additional SQLite
writes.

### Durability guarantee

Version one should choose **non-blocking historical projection**, not strong
crash durability:

* Terminal mutations are never intentionally discarded while the process remains alive.
* Shutdown attempts to drain terminal mutations and latest coalesced state.
* An abrupt process or machine failure can lose an uncommitted terminal mutation.
* On restart, any previous nonterminal entity that was not durably completed is classified as `Interrupted`.
* Therefore a job or transfer that actually completed immediately before a crash may conservatively appear as interrupted in history.

This distinguishes queue-loss guarantees from power-loss guarantees. Strong
crash durability would require the terminal domain path to await database
acknowledgment, which would put SQLite on the completion path and contradict the
live-runtime-first design.

A download finishing is a barrier:

1. Take the latest buffered progress for the transfer.
2. Write that progress.
3. Write the terminal transfer state.
4. Update the corresponding job.
5. Update any persisted workflow state.
6. Commit them in one transaction.

A late progress callback must not be able to change a completed transfer back to running.

The adapter should construct a composite terminal persistence mutation rather
than relying on several separately queued changes to accidentally land in one
transaction:

```csharp
public sealed record CompleteTransferMutation(
    TransferPersistenceSnapshot Transfer,
    JobPersistenceSnapshot Job,
    DateTime OccurredAtUtc);
```

The terminal Core change must contain enough immutable information for the
adapter to build that mutation without querying live runtime state. The writer
then removes buffered progress for the transfer, upserts the terminal transfer,
upserts its job, derives any aggregate read state, and commits atomically.

Apply the same principle to search completion: a completion mutation means
"flush pending result batches for this search and complete its metadata in the
same transaction."

### Add monotonic revisions and runtime sessions

Every durable entity mutation should carry a monotonically increasing revision
owned by the entity that actually changed:

* `JobRevision` increments only when persisted job fields change.
* `TransferRevision` increments on transfer state/progress changes.
* `SearchRevision` increments on accepted search-result additions and completion if completion is versioned.
* `CoreChange.Sequence` orders publications within one runtime incarnation.

The persistence layer also needs a runtime/process epoch with a concrete
responsibility: `runtime_sessions.id` identifies which daemon run produced or
last touched a row, and unfinished sessions drive startup interruption. A bare
`CoreChange.Sequence` restarts after process restart; `(RuntimeId, Sequence)` is
only meaningful within that stored session context.

For example:

```csharp
public sealed record TransferProgressMutation(
    Guid TransferId,
    long Revision,
    long TransferredBytes,
    long TotalBytes,
    DateTime OccurredAtUtc);
```

Upserts should only apply a mutation newer than the stored revision.

This protects against:

* Multiple producer threads.
* Buffered progress crossing a terminal event.
* Retries.
* Delayed callbacks.
* A future implementation with more than one internal event consumer.

A single writer reduces ordering problems but does not completely eliminate them; version checks make the invariant explicit.

Do not treat every progress-carried job snapshot as a job mutation. Transfer
progress should update transfer revision/progress, not bump job revision unless a
persisted job field changed.

### Do not write from property-change handlers

`SongJob.BytesTransferred` currently updates from the Soulseek transfer callback in `Downloader.cs:93-106`. Persisting from `INotifyPropertyChanged` would couple database behavior to implementation details and generate writes for properties that may not represent durable business state.

Persist semantic records such as:

* Transfer started.
* Transfer progress observed.
* Transfer attempt failed.
* Transfer state changed.
* Transfer completed.
* Job reached terminal state.

### Publication safety

Persistence makes observer failure consequential. Before attaching the writer,
Core change publication should be observer-safe:

* Invoke each observer independently.
* Catch and report observer exceptions.
* Never allow an observational consumer to unwind the domain operation.
* Ensure persistence subscribes before the runtime can produce changes.

Longer term, the canonical `CoreChange` stream should be the primary
publication path and strongly typed per-change events can be convenience filters
over that stream. Do not let a UI/SignalR adapter exception prevent the database
writer from seeing a terminal state.

### Channel design

I would use three mechanisms rather than one naïve channel:

1. **Low-volume, high-priority state mutations**
   A bounded single-reader channel with a degraded-mode fallback.

2. **High-frequency progress**
   A concurrent latest-value dictionary keyed by transfer ID plus a lightweight wake signal. It cannot grow with callback count; it grows only with active transfer count.

3. **Search results**
   Per-search batches, flushed after either:

   * 100–500 results, or
   * roughly 100–250 ms.

A `SearchCompleted` mutation is also a barrier: flush every pending result for that search and mark the search complete in the same transaction.

Encode the completion invariant in Core before relying on it for persistence:
after `SearchSession.Complete()` has published completion, later additions
should be rejected, or the design must explicitly allow late results and advance
completion state/revision accordingly. Prefer rejecting late additions. The
database should enforce `UNIQUE(search_job_id, sequence)` so the sequence cursor
is structurally unique.

There is an unavoidable durability decision to document: batching means an abrupt process or power failure can lose the newest fraction of a second of progress or search results. Requiring synchronous durability for every search file and every byte callback would put SQLite directly on the network hot path. For job history, bounded tail loss is normally the better tradeoff.

### Database failure behavior

A transient or persistent database failure should:

* Mark persistence as unhealthy.
* Retry bounded transient `SQLITE_BUSY` cases.
* Log a rate-limited error.
* Preserve the newest coalesced progress where possible.
* Never cancel an active download solely because history could not be saved.
* Expose persistence health through `/api/server/status` or a health endpoint.

The queue also needs a documented overload policy. Silent dropping of terminal
state is never acceptable, but "never drop anything while the database is down
forever" is not possible without unbounded memory growth.

Use a bounded degraded mode:

* Keep only the latest job and transfer snapshots per entity.
* Keep terminal job/transfer mutations in a per-entity terminal map.
* Apply an absolute count and/or memory limit to degraded-mode maps; if a week-long outage creates more entities than the limit, evict older terminal projections and increment a loss counter.
* Drop or disable diagnostic/activity persistence first.
* Set a hard maximum on buffered raw search results.
* Mark affected searches as having incomplete persisted results when that limit is reached.
* On database recovery, reconcile latest live job and transfer snapshots.
* Expose counters for dropped terminal projections, dropped diagnostics, dropped activity events, and incomplete search persistence.

---

# Live state and history should remain separate

When the combined live/history query facade is introduced, rename `EngineStateStore`. It is an in-memory operational projection and is doing that job reasonably well. Given Sockseek's full-client direction, a name such as `LiveDaemonStateStore` may age better than `LiveEngineStateStore`, or the server can expose a facade over domain-specific live stores for downloads, uploads, search, sharing and chat.

Then introduce a query facade:

```csharp
public interface IJobQueryService
{
    Task<JobDetailDto?> GetJobAsync(Guid id, CancellationToken ct);
    Task<JobPageDto> GetJobsAsync(JobQueryDto query, CancellationToken ct);
}
```

Its behavior:

1. Query live state.
2. Query persisted state where needed.
3. Deduplicate by stable ID.
4. Let live state win for an active entity.
5. Map both sources into the same outward API contract.

Do not load all persisted jobs and search results into `EngineStateStore` at startup. History can become very large and should remain pageable in the database.

Current APIs should gain pagination before history is exposed broadly. `/api/jobs/{id}/raw?afterSequence=...` already has a good cursor-like basis, but it also needs a maximum page size.

For job listings, use a stable cursor such as `(created_at_utc, id)` rather than
display ID. Version one should choose brief eventual consistency: treat job
registration as high-priority persistence work, page persisted history by cursor,
and overlay live state for entities already present in the page. Newly registered
jobs may appear after their registration reaches persistence.

Do not implement the complex live/database page merger initially. If strict
overlay is later required, it needs lookahead, live-id merging, filter reapply,
and repeated database fetches until the requested page is full.

For a historical search:

```text
database search rows
    -> neutral SearchProjectionInput records
    -> SearchResultProjector
    -> API result snapshot
```

For “download this historical search result”:

```text
persisted result
    -> validate it still contains necessary source fields
    -> create a new command/new SongJob
    -> assign a new JobId and TransferId
```

Do not resurrect the historical job as an active mutable job.

---

# Identity needs attention before persistence

## `TransferId`

`TransferId` should be an application-generated `Guid`, allocated before the
first start/progress/state event for a logical transfer is published. Do not
derive it from `JobId`, username/path, or attempt number.

Add the shared transfer identity policy in the first persistence implementation,
under `Transfers/` rather than inside a download-only namespace. Downloads should
allocate ids from it first, and uploads should use the same allocator and schema
when upload support is added.

## `DisplayId`

`Job._nextDisplayId` is process-local and resets after restart (`Job.cs:72-90`).

That creates confusing collisions once historical and new jobs are displayed
together. Use a daemon-wide monotonically increasing display ID for the first
persistence implementation, because that preserves current CLI lookup behavior
and avoids a broad per-workflow display-id refactor.

Implementation rule:

* GUIDs remain the real identity.
* Move allocation out of static `Job` state.
* Seed the allocator from the persisted maximum display ID at startup.
* Do not request a database-generated display ID for each submission.
* Consider moving from `int` to `long` before long-lived history makes overflow
  or reset semantics harder to change.

## Timestamps

Store timestamps in UTC. For SQLite, using UTC `DateTime` or an integer Unix-time representation will produce more predictable ordering and filtering than relying heavily on `DateTimeOffset`; Microsoft’s SQLite provider guidance recommends converting timestamps to UTC. ([Microsoft Learn][6])

Use `TimeProvider` at the Core publication boundary, not only in the database
writer. `DownloadEvents` and `SearchSession` should derive `OccurredAtUtc`,
`created_at_utc`, `started_at_utc`, and `completed_at_utc` source timestamps
from injectable clocks so crash/restart, retention, and ordering tests can be
deterministic.

---

# SQLite operational configuration

For a local database file, initialize and verify:

```text
Connection string:
  Foreign Keys=True
  Default Timeout=5

Database initialization:
  PRAGMA journal_mode = WAL;
  verify configured journal mode

Connection initialization/interceptor:
  PRAGMA synchronous = FULL;
```

Start with `FULL` for clearer power-loss behavior while writes are still batched
and measured. `NORMAL` can become an explicit operator tradeoff later.

With `IDbContextFactory`, contexts will open multiple connections. Ensure
foreign keys and other connection-scoped settings are applied to every
connection, not only the initializer connection. Add an integration test that
opens a fresh context and verifies foreign-key enforcement.

The database should live in the persistent application data directory, for example:

```text
/data/sockseek.db
```

Do not place a WAL database on an NFS/network filesystem. SQLite’s WAL implementation relies on same-host shared memory and explicitly does not support network filesystems. ([SQLite][3])

Document that requirement. A remote PostgreSQL provider can serve users who require network-hosted storage when that provider becomes a deliberate feature.

## Migrations

Keep migrations in `Sockseek.Persistence` initially. A second migrations-only project is unnecessary unless packaging or provider support later gives a concrete reason for it.

For a self-hosted application, automatic startup migration is pragmatic, but use safeguards:

* Never use `EnsureCreated`.
* Inspect generated migrations in review.
* Test upgrading from every supported prior database version.
* Automatic startup migrations may only be additive or demonstrably non-destructive until safe SQLite backup support exists.
* Back up the database before any destructive/rebuilding migration once backup tooling exists.
* Fail startup clearly if migration fails.
* Ensure only one Sockseek process can migrate/use a database.
* Consider a `--migrate-only` command for packaging and troubleshooting.

Official EF guidance favors reviewed scripts or migration bundles for conventional production deployments and warns that migrations should be inspected and tested. A packaged single-process self-hosted daemon is a somewhat different operational environment, but the underlying migration risks remain. ([Microsoft Learn][7])

---

# Lessons from slskd

The current slskd release line still uses and evolves an EF/SQLite transfer database; its April 2026 releases include additional transfer fields and migration changes. ([GitHub][8])

Useful lessons from its history include:

### SQLite should not control high-frequency live transfer behavior

slskd has encountered database-lock errors in `TransfersDbContext`. ([GitHub][9])

More recently, its maintainer described transfer-history queries over roughly 150,000 records taking up to 15 seconds and concluded that SQLite should be removed from application hot paths and reserved for historical information. Some filesystem-ordering claims in that issue are explicitly presented as an unproven hypothesis, so they should not be treated as established SQLite behavior; the performance and coupling lesson is still relevant. ([GitHub][10])

For Sockseek, that means:

* Active-transfer admission logic uses in-memory counters.
* Rate limits use in-memory state.
* Progress rendering uses live state.
* Transfer completion is never waiting on a history query.
* The database supports history, recovery metadata, reporting, and startup views.

### Indexes and retention are features, not cleanup tasks

slskd’s release history contains repeated transfer/share indexing, retention, progress-update and SQLite configuration changes. That is normal for a growing history database, not an indication that an ORM removes the need for schema design. ([GitHub][8])

Add retention configuration early:

```text
CompletedJobRetention
FailedJobRetention
SearchResultRetention
ActivityEventRetention
TransferRetention
```

Users may reasonably want:

* Forever.
* A fixed number of days.
* A fixed maximum row count.
* Job history retained but raw search results pruned sooner.

Raw search results can include usernames and remote paths, so deletion and privacy behavior should be explicit.

---

# Other decisions to make now

## History versus recovery

For the first version, I recommend:

* Persist active state and progress.
* On startup, convert every nonterminal persisted job and transfer from the previous process into `Interrupted`.
* Do not automatically resume it.

Resuming transfers safely involves separate questions:

* Is the incomplete file present?
* Does its length agree with persisted progress?
* Does the remote user still expose the same file?
* Is the candidate identity stable?
* Can a job’s processing be repeated idempotently?
* Which post-processing stages have already run?

History and recovery should not be conflated. Add resumability as a separate explicit feature after durable history is working.

## Settings snapshots

It can be useful to know which settings produced a historical result. Store a versioned, whitelisted settings snapshot or settings delta.

Do not blindly serialize all configuration:

* Do not store credentials or tokens.
* Do not assume today’s settings type can deserialize every historical version.
* Do not make history loading dependent on the current settings model.

## Single-instance policy

SQLite supports many readers but a single writer at a time. Sockseek should initially declare that one daemon owns one database file.

A file/process lock and a clear startup error are preferable to two daemon processes contending over the same database.

## Backup and corruption

Defer the following operational tooling until persistence exists and the first schema is stable, unless packaging or support needs force it sooner:

* A safe “backup database” operation.
* A maintenance status endpoint.
* An integrity-check command.
* A documented restore procedure.
* Optional pruning and `VACUUM` maintenance.

Copying only `sockseek.db` while it has active WAL files is not always a valid backup strategy.

## Chats and shares

Do not try to anticipate them with a universal entity or generic JSON event table.

Future bounded contexts can add:

```text
users
shares
shared_files
rooms
room_messages
private_conversations
private_messages
uploads/transfers
```

They can share database infrastructure, migrations, timestamps, retention, and query conventions without sharing an artificial base entity.

Consider SQLite FTS when implementing chat or shared-file search. It does not need to influence the first migration.

---

# Suggested implementation sequence

## 1. Close the remaining Core persistence-contract gaps

This is implementation work, not another broad design phase. Before creating the
final EF schema, close the small contract gaps that affect persisted meaning:

* Define runtime-session responsibility and make publication clocks injectable with `TimeProvider`.
* Fix revision ownership so transfer progress does not imply a persisted job revision.
* Add complete job relationship information at registration, especially `SourceJobId`.
* Add explicit transfer terminal changes: completed, failed, and cancelled.
* Make Core change delivery observer-safe.
* Rename `IDurableCoreEvent` to an ordered/non-coalescible marker and have persistence use an explicit whitelist.
* Enforce the `SearchSession` completion barrier.
* Define composite persistence mutation records for terminal transfer and search-completion barriers.
* Document the non-blocking crash-durability guarantee.
* Add concurrency tests showing late progress cannot supersede terminal state.

Do not begin by writing EF entities directly from `DownloadEvents` or any other mutable runtime event bus.

## 2. Add `Sockseek.Persistence` and the first schema

Implement:

* `SockseekDbContext`.
* Explicit `IEntityTypeConfiguration<T>` classes.
* Initial migration.
* SQLite initialization.
* Temporary-file integration test fixture.
* Migration upgrade tests.

The first migration should include `runtime_sessions`, jobs, and search-job
metadata only if required by the job contract. Defer materialized workflows,
transfers, and `transfer_attempts` until their dedicated vertical slices.

Use real temporary SQLite files in concurrency tests. An in-memory SQLite database will not reproduce file locking, WAL, restart, or shutdown behavior.

## 3. Persist job history

Start with job registration and state transitions only. Store compact
persistence rows produced by a mapper from Core changes; do not serialize
`JobSnapshot.Payload` wholesale.

At this point:

* Job history survives restart.
* Previous active jobs become interrupted.
* Historical jobs appear through a minimal paginated query service.
* Historical workflow views are derived from persisted jobs.
* Search results and transfer detail can still be added next.

## 4. Prove restart survival with a minimal reader

Add a read-only internal reader or endpoint that can show persisted jobs after
restart and mark previous nonterminal jobs as interrupted. This verifies the
projection before high-volume transfer/search data enters the system.

## 5. Add the transfer writer

Introduce:

* Transfer IDs.
* Latest-value progress accumulator.
* Periodic progress flush, starting around 2 to 5 seconds.
* Explicit terminal barriers.
* Revision checks.
* Sockseek-owned transfer state/outcome values.

Add attempt records only after attempt lifecycle events exist.

Tests should send tens of thousands of progress callbacks and confirm:

* Database write count stays bounded.
* Final byte count is exact.
* Completed state cannot be overwritten by late progress.
* Database latency does not delay the engine callbacks.

## 6. Persist search results in batches

Introduce a Sockseek-owned search-result input type and make projection independent from Soulseek.NET types.

Test:

* Duplicate username/filename results.
* Sequence pagination.
* Completion while a batch is pending.
* Very large result sets.
* Reprojection after process restart.
* Pruning raw results without deleting the parent job.
* Search completion rejecting or explicitly handling late additions.
* Bounded degraded mode marking searches with incomplete persisted results.

## 7. Harden the API and operations

Add:

* Pagination and maximum limits.
* Persistence health.
* Retention.
* Backup/migration behavior.
* Graceful writer drain.
* Failure-injection tests.
* Database size and write-latency metrics.
* Live/history cursor semantics, preferably by `(created_at_utc, id)`.

Hosted-service ordering should ensure:

1. Persistence starts before the engine produces events.
2. The engine stops producing events during shutdown.
3. The persistence writer drains after engine shutdown.
4. The database then closes.

---

# Concrete architectural shape

The result I would aim for is:

```text
                    ┌────────────────────────┐
                    │    Domain runtimes     │
                    │ authoritative live work│
                    └────────────┬───────────┘
                                 │ immutable changes
                  ┌──────────────┴───────────────┐
                  │                              │
        ┌─────────▼──────────┐         ┌─────────▼────────────┐
        │ Live state project │         │ Persistence adapter  │
        │ daemon/domain views│         │ copies immutable data│
        └─────────┬──────────┘         └─────────┬────────────┘
                  │                              │
             live queries               buffer/coalesce/batch
                  │                              │
                  │                    ┌─────────▼────────────┐
                  │                    │ single DB writer     │
                  │                    └─────────┬────────────┘
                  │                              │
                  │                         SQLite / EF
                  │                              │
        ┌─────────▼──────────────────────────────▼────────────┐
        │              Combined query facade                 │
        │        live overlay + paginated history            │
        └──────────────────────────┬──────────────────────────┘
                                   │
                         HTTP / SignalR / CLI / GUI
```

This architecture scales naturally to chats, uploads and sharing because those features can add their own durable projections and query services without making EF Core or SQL part of the download runtime.

## Bottom line

Use **EF Core with SQLite**, create **`Sockseek.Persistence`**, and implement a **single-writer durable projection pipeline**. Keep the in-memory engine authoritative for active work, represent file transfers separately from jobs, batch search-result inserts, and coalesce progress with terminal-state barriers and monotonic revisions.

The existing `EngineStateStore` and `ServerEventCoalescer` provide good conceptual prototypes, but persistence should be a parallel projection with stronger durability and ordering semantics—not database calls added to either class.

I performed a static review of the solution structure, TODOs, project dependencies, primary job/search/download execution paths, API/state/event architecture, and associated tests. I could not compile or execute the test suite because the review environment does not contain the `dotnet` executable.

[1]: https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/?utm_source=chatgpt.com "DbContext Lifetime, Configuration, and Initialization"
[2]: https://github.com/DapperLib/Dapper/blob/main/docs/readme.md?utm_source=chatgpt.com "Dapper/docs/readme.md at main"
[3]: https://sqlite.org/wal.html?utm_source=chatgpt.com "Write-Ahead Logging"
[4]: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async?utm_source=chatgpt.com "Async limitations - Microsoft.Data.Sqlite"
[5]: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers?utm_source=chatgpt.com "Migrations with Multiple Providers - EF Core"
[6]: https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations?utm_source=chatgpt.com "SQLite EF Core Database Provider Limitations"
[7]: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying?utm_source=chatgpt.com "Applying Migrations - EF Core"
[8]: https://github.com/slskd/slskd/releases?utm_source=chatgpt.com "Releases · slskd/slskd"
[9]: https://github.com/slskd/slskd/issues/748?utm_source=chatgpt.com "Tons of errored transfers, \"database is locked\" in logs · ..."
[10]: https://github.com/slskd/slskd/issues/1468?utm_source=chatgpt.com "SQLite is a major performance bottleneck, and a source of ..."


---

# Current Core event-boundary status

The Core event-boundary prerequisite for persistence is:

> Core domain runtimes emit immutable, Sockseek-owned descriptions of state changes.

It is not:

> Rewrite the entire job model into a pure reducer architecture before persistence can begin.

That broader reducer work remains valuable, but it is a separate architecture refactor. Persistence must not wait for it.

## Implemented prerequisite

The current boundary now has these properties:

* Public `DownloadEvents` subscribers receive `CoreChange` records, not mutable `Job`, `FileCandidate`, `SearchResponse`, `Soulseek.File`, cancellation tokens, or mutable collections.
* Core owns immutable snapshots in `Sockseek.Core/Snapshots`, including job, candidate, search-result and transfer snapshots.
* `CoreSnapshotFactory` is the single mapping point from mutable runtime objects and Soulseek.NET objects into Sockseek-owned immutable records.
* `EngineStateStore` and SignalR DTO adaptation consume immutable snapshots/changes instead of delayed mutable job references.
* Search result additions are exposed as `SearchResultsAddedChange` with copied `SearchResultSnapshot` values. `SearchSession` may still keep Soulseek.NET objects internally for live projection, but public search-result change payloads do not expose them.
* Download lifecycle changes carry a first-class `TransferId` through `DownloadStartedChange`, `DownloadProgressedChange`, `DownloadStateChangedChange`, and `DownloadAttemptFailedChange`.
* A `TransferId` is allocated when Sockseek starts moving one selected candidate to one local output path. Retries for that same candidate/output path stay under the same transfer id.
* Active download tracking is keyed by `TransferId`, not remote filename.
* Stale-download attempt ids remain separate from durable transfer ids.
* Core changes have a process-wide sequence and per-job/per-transfer revisions. These are ordering aids for live projections; durable post-restart sequence/session semantics belong in the persistence implementation.
* Architecture tests guard public Core event/snapshot/search contracts against known mutable runtime types.

## Breaking contract changes

This prerequisite intentionally changes public-ish contracts:

* `DownloadEvents` event handlers now receive one immutable `CoreChange`-derived record. Older handlers expecting mutable jobs or ad hoc argument lists must be updated.
* Download change records now carry `TransferSnapshot` and expose `TransferId`.
* API download event DTOs include `TransferId`. Existing JSON consumers that ignore unknown properties continue to work, but source/binary consumers of positional record constructors should rebuild/update.
* `SearchSession` can be constructed with an owning job id, and it publishes immutable search-result/completion changes in addition to the raw-result stream.

## Snapshot and delta shape

The boundary deliberately uses both snapshots and deltas:

* Job lifecycle boundaries use job snapshots because they are low-frequency consistency points.
* Download progress uses transfer snapshots containing the latest transfer state, not a complete workflow snapshot.
* Search results are additions and must not be latest-value coalesced.
* Activity/status messages remain semantic changes; persistence can decide which are durable history and which are transient diagnostics.

Marker interfaces distinguish the intended treatment:

```csharp
public interface ICoreChange;
public interface ICoalescibleCoreChange : ICoreChange;
public interface IDurableCoreEvent : ICoreChange;
```

These markers are intentionally small. They classify changes without turning Core into a database-operation API.

Before wiring persistence, rename `IDurableCoreEvent` to something less
database-loaded, such as `IOrderedCoreChange` or `INonCoalescibleCoreChange`.
"Durable" sounds like the event has already been persisted or must always be
stored, which is not true for messages, diagnostics, or search-result tails under
bounded degraded mode. The persistence adapter should use an explicit whitelist
of persisted change types rather than assuming every ordered/non-coalescible
change belongs in the database.

## What is not part of this prerequisite

The following items are still valid work, but they are not required before starting the persistence writer:

* Rewriting the whole job model as immutable records.
* Moving every job mutation behind a reducer/state-store boundary.
* Making search projection consume only neutral persisted search-result records.
* Implementing the actual EF/SQLite persistence project, writer, migrations, retention, health endpoints, and history query facade.

Those belong to later explicit phases. The job-model TODOs should remain in code until that reducer/state-store work is actually done. As an interim guardrail, production Core code should commit terminal job states through `JobOutcomeCommitter`; the architecture tests now reject direct `SetDone`/`Fail`/`SetSkipped`/`SetCancelled` calls outside the job model and commit boundary.

## Acceptance criterion

Before persistence writes live in production, this invariant must hold:

> Once a domain change has been published, its observable public contents can never change, and ordinary consumers can process it without dereferencing mutable runtime state.

Consumers may still query live state for commands, manual operations, follow-up submissions, or recovery after detecting a sequence gap. Ordinary event handling should not need to do this:

```csharp
var job = supervisor.FindJob(change.JobId);
```

to discover what the published change meant at the time it occurred.

This prerequisite is satisfied for immutability at the current download and
search-result boundaries: a persistence writer does not need to enqueue live
`Job`, `SearchSession`, `FileCandidate`, `SearchResponse`, or `Soulseek.File`
objects. It is not yet a complete persistence contract. The first persistence
implementation slice must still add complete relationship events, transfer
terminal semantics, runtime epoch/revision rules, observer-safe delivery, and
bounded outage behavior before the schema is treated as final.

----

# Completion and Stop Conditions

This document is the hard definition of done for the persistence work described in this file.

It is intentionally broader than a first vertical slice. **The implementation may not be declared complete after job history alone.** Completion requires durable jobs, relationships, runtime sessions, transfers, transfer attempts, raw search results, historical reprojection, live/history queries, retention, degraded-mode behavior, operational tooling, and release-level verification.

## Normative language

* **MUST / MUST NOT**: mandatory for completion.
* **SHOULD / SHOULD NOT**: expected unless a reviewed architecture decision records a justified exception.
* **MAY**: optional.
* **Stop condition**: a condition that must be true before the persistence implementation can stop and be called complete.
* **Stop-work condition**: a condition that requires implementation to pause until the issue is resolved.

## Global completion rule

Persistence is complete only when all of the following are true:

1. Every mandatory checkbox in this document is satisfied.
2. Every mandatory condition has automated evidence, or a documented manual verification where automation is genuinely impractical.
3. The full solution builds in Release configuration.
4. The complete automated test suite passes without ignored persistence tests.
5. A real file-backed SQLite end-to-end test proves survival across process restart.
6. No known current-code blocker listed below remains unresolved.
7. No completion claim relies on future work described as “later,” “follow-up,” “first slice,” or “good enough for now.”

The following are **not** sufficient stopping points:

* Creating `Sockseek.Persistence` and an initial migration.
* Persisting only job registration and terminal job state.
* Persisting transfers without terminal finalization semantics.
* Persisting search metadata without raw results.
* Writing history that is not exposed through the query/API surface.
* Passing only in-memory SQLite tests.
* Having graceful shutdown work while crash/restart behavior remains undefined.
* Deferring transfer attempts even though current downloads retry and expose attempt failures.
* Leaving retention, health, migration safety, or degraded-mode data-loss reporting unimplemented.

---

# 1. Scope boundary

## 1.1 Mandatory scope

The completed persistence feature MUST include:

* Runtime-session records and clean/unclean shutdown detection.
* Durable records for every current job kind.
* Durable parent, source, and result-job relationships.
* Daemon-wide display-ID continuity across restart.
* Durable logical transfers for every actual remote/fallback file movement.
* Durable transfer-attempt lifecycle records.
* Coalesced transfer progress with exact terminal barriers.
* Durable search-job metadata and raw search results.
* Historical search reprojection through Sockseek-owned neutral records.
* Starting new follow-up downloads from persisted historical search results.
* Paginated live/history job and workflow reads.
* Queryable transfer and attempt history.
* Bounded persistence queues and bounded degraded mode.
* Persistence health and loss counters.
* Retention and pruning behavior.
* Safe schema migration behavior.
* Safe backup and integrity-check behavior, or a hard prohibition on destructive migrations until those operations exist.
* Startup and shutdown ordering.
* Documentation of consistency and crash-durability guarantees.

## 1.2 Explicitly out of scope

The implementation MAY stop without the following, provided the public documentation does not claim them:

* Automatic resumption of interrupted jobs or transfers.
* Rehydrating historical rows into mutable `Job` objects.
* PostgreSQL or multi-provider migrations.
* Multi-daemon ownership of one database.
* Event sourcing or full replay of every domain event.
* A materialized `workflows` table; workflow views may be derived from jobs.
* Persistence for chat, shares, rooms, private messages, or uploads that do not yet exist in the product.
* Optional `activity_events`, unless the product explicitly adds activity-history requirements.
* Exact preservation of every progress callback.
* Strong crash durability that blocks domain completion on a database acknowledgment.

The transfer schema and contracts MUST still be direction/source-neutral enough that future uploads do not require redefining transfer identity.

---

# 2. Known current-code blockers

The implementation MUST NOT be declared complete while any row in this table still describes the codebase.

| ID    | Current location                                                                                      | Current condition                                                                                                                   | Required end state                                                                                                                             |
| ----- | ----------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| CB-01 | `Sockseek.Core/Events/CoreChanges.cs`                                                                 | `IDurableCoreEvent` implies storage semantics that the marker does not guarantee.                                                   | Rename it to an ordering/non-coalescing concept; persistence uses an explicit whitelist.                                                       |
| CB-02 | `DownloadEvents` and `SearchSession`                                                                  | Events use `DateTimeOffset.UtcNow` directly.                                                                                        | Event timestamps come from injected `TimeProvider`.                                                                                            |
| CB-03 | `DownloadEvents.RaiseJobRegistered`                                                                   | Registration carries a parent snapshot but no `SourceJobId`.                                                                        | Registration or an explicit structural-link change carries complete persisted relationships.                                                   |
| CB-04 | `EngineStateStore.SetSourceJob` / `EngineSupervisor`                                                  | Source relationships exist only in the server live projection.                                                                      | Source relationships are emitted as immutable authoritative data and persisted.                                                                |
| CB-05 | `DownloadEvents.RaiseDownloadProgress`, `RaiseDownloadStateChanged`, and `RaiseDownloadAttemptFailed` | Transfer-only changes increment the job revision.                                                                                   | Job revision changes only when persisted job fields change.                                                                                    |
| CB-06 | `DownloadEvents.Publish` and `SearchSession.Publish`                                                  | One throwing observer can prevent later observers and `ChangePublished` from running.                                               | Observer invocation is isolated; observational exceptions never unwind domain work or hide a change from persistence.                          |
| CB-07 | `SearchSession.AddResponse`                                                                           | Results may still be accepted after `Complete()`.                                                                                   | Completion and result addition have an enforced atomic invariant; late additions are rejected.                                                 |
| CB-08 | `CoreChanges.cs`                                                                                      | There are no explicit logical `TransferCompleted`, `TransferFailed`, or `TransferCancelled` changes.                                | Explicit terminal changes exist and represent Sockseek-level terminal outcomes.                                                                |
| CB-09 | `Downloader.DownloadFile`                                                                             | A protocol `Completed` callback can occur before `.incomplete` rename and duplicate-cache publication.                              | Durable completion is emitted only after final local-path validation/finalization and cache publication.                                       |
| CB-10 | `Downloader.DownloadFile`                                                                             | Manual skip, stale cancellation, final rename failure, and outer logical failure do not emit a complete terminal transfer contract. | Every started logical transfer receives exactly one terminal outcome.                                                                          |
| CB-11 | `TransferSnapshot`                                                                                    | State is an unstable string and candidate is mandatory.                                                                             | Sockseek-owned stable state/outcome values are used; source-specific fields are nullable as required.                                          |
| CB-12 | `CoreSnapshotFactory.CandidateKey`                                                                    | The transfer carries a NUL-delimited candidate key.                                                                                 | The key is not persisted, or is replaced by a documented canonical hash.                                                                       |
| CB-13 | `FileAttributeSnapshot`                                                                               | Attribute type is only `Soulseek.NET` enum text.                                                                                    | Persist a Sockseek-owned stable attribute code, optionally with display text.                                                                  |
| CB-14 | `Job`                                                                                                 | Display IDs use process-local static `int _nextDisplayId`.                                                                          | Allocation is daemon-wide, persisted/seeded, thread-safe, and does not collide after restart.                                                  |
| CB-15 | `JobSnapshot`                                                                                         | Lifecycle timestamps are absent.                                                                                                    | Persisted mutations have deterministic source timestamps for create/start/update/complete.                                                     |
| CB-16 | `SearchResultProjector` / `SearchSession`                                                             | Live projection depends on third-party Soulseek objects.                                                                            | Live and historical projection consume one Sockseek-owned neutral input record.                                                                |
| CB-17 | `EngineStateStore` and current routes                                                                 | Job, workflow, and raw-result reads are live-only.                                                                                  | Reads go through a live/history query facade with stable pagination and documented eventual consistency.                                       |
| CB-18 | `/api/jobs` and `/api/jobs/{id}/raw`                                                                  | History pagination and raw-result maximum page limits are absent.                                                                   | Cursor pagination and enforced finite maximum page sizes exist.                                                                                |
| CB-19 | Solution/project files                                                                                | No `Sockseek.Persistence` project or EF/SQLite migration infrastructure exists.                                                     | Dedicated project, migrations, integration tests, and dependency boundaries exist.                                                             |
| CB-20 | Fallback download path                                                                                | `yt-dlp` fallback produces a file without a first-class transfer lifecycle.                                                         | Every fallback file movement has a logical transfer and terminal record, or fallback is explicitly removed from supported persisted downloads. |
| CB-21 | Retry path                                                                                            | Attempt failures exist, but attempt start/success/cancel lifecycle and durable attempt IDs do not.                                  | Complete attempt lifecycle exists and is persisted.                                                                                            |

---

# 3. Architecture and dependency boundaries

## Stop conditions

* [ ] **ARCH-01** A `Sockseek.Persistence` project exists and is included in `Sockseek.sln`.
* [ ] **ARCH-02** `Sockseek.Core` has no reference to EF Core, SQLite, `Sockseek.Persistence`, database entities, migrations, or SQL.
* [ ] **ARCH-03** `Sockseek.Api` has no reference to EF Core, SQLite, or database entities.
* [ ] **ARCH-04** `Sockseek.Persistence` references only immutable Sockseek-owned contracts required for mapping; it does not accept mutable `Job`, `SearchSession`, `FileCandidate`, `SearchResponse`, `Soulseek.File`, `CancellationTokenSource`, streams, or live engine services.
* [ ] **ARCH-05** Database entities never escape `Sockseek.Persistence`.
* [ ] **ARCH-06** Server code talks to persistence through use-case-level writer/read interfaces, not `DbSet<T>`, `DbContext`, generic repositories, or raw connection objects.
* [ ] **ARCH-07** There is no generic `IRepository<T>` or generic `IUnitOfWork` wrapper.
* [ ] **ARCH-08** All ordinary runtime writes pass through one application-level persistence writer.
* [ ] **ARCH-09** The only exceptions to the single runtime writer are migrations, startup initialization, backup/integrity operations, and explicitly read-only query contexts.
* [ ] **ARCH-10** Active admission, cancellation, retry decisions, transfer control, progress rendering, and rate limiting do not query SQLite.
* [ ] **ARCH-11** Historical records are never rehydrated into the current mutable job hierarchy.
* [ ] **ARCH-12** Persistence mutations are compact persistence-owned records; the writer does not serialize `JobSnapshot.Payload` wholesale.
* [ ] **ARCH-13** An architecture test fails if forbidden mutable/runtime/third-party types appear in public Core changes or persistence mutations.
* [ ] **ARCH-14** An architecture test fails if EF Core or SQLite references are introduced into Core or API projects.

## Required evidence

* Architecture tests that inspect public types and project references.
* A repository-wide search showing database writes are confined to the persistence writer/maintenance/migration paths.
* A code review checklist confirming no live-object serialization.

---

# 4. Core change contract

## Stop conditions

* [ ] **CORE-01** Every persisted change is immutable after publication.
* [ ] **CORE-02** A persistence consumer can interpret every whitelisted change without looking up a mutable runtime object.
* [ ] **CORE-03** `TimeProvider` is injected at all persisted publication boundaries, including `DownloadEvents`, `SearchSession`, transfer-attempt publication, and runtime-session creation.
* [ ] **CORE-04** `CoreChange.OccurredAtUtc` is the source timestamp used for persistence; the writer clock does not replace it.
* [ ] **CORE-05** Observer dispatch invokes subscribers independently and catches/logs each observer exception.
* [ ] **CORE-06** A throwing UI, SignalR, logging, or test observer cannot prevent persistence from receiving the same change.
* [ ] **CORE-07** Observer exceptions cannot unwind or fail the domain operation that published the change.
* [ ] **CORE-08** Persistence subscribes before any domain runtime can publish persisted changes.
* [ ] **CORE-09** The database adapter uses an explicit exhaustive whitelist of persisted Core change types.
* [ ] **CORE-10** Adding a new persisted Core change without updating the adapter causes a test failure rather than silent omission.
* [ ] **CORE-11** Marker interfaces describe ordering/coalescing only; no marker is treated as an automatic command to store every implementing event.

## Revision ownership

* [ ] **CORE-12** Job revisions increment only when at least one persisted job field changes.
* [ ] **CORE-13** Transfer revisions increment for transfer state, progress, attempt aggregate, or terminal changes.
* [ ] **CORE-14** Search revisions increment for accepted result additions and completion-state changes.
* [ ] **CORE-15** Transfer progress does not increment the job revision solely because a job snapshot accompanies the event.
* [ ] **CORE-16** Activity-only changes increment a job revision only if activity fields are part of the persisted job projection.
* [ ] **CORE-17** Revision tests cover concurrent publication, duplicate delivery, late delivery, and stale delivery.
* [ ] **CORE-18** Upserts reject a mutation whose entity revision is older than or equal to the durable row, except for a deliberately idempotent replay of identical content.

## Structural relationships

* [ ] **CORE-19** Job registration carries `ParentJobId` and `SourceJobId` as IDs, not nested parent/source snapshots.
* [ ] **CORE-20** `JobResultCreatedChange` or an equivalent explicit link change carries extract-to-result identity.
* [ ] **CORE-21** Parent, source, and result relationships can coexist on one job where applicable.
* [ ] **CORE-22** Follow-up jobs created by current `EngineSupervisor` manual file/folder/retrieve operations publish their source relationship before or with registration.
* [ ] **CORE-23** Relationship publication is not dependent on `EngineStateStore.SetSourceJob`.

## Required evidence

* Unit tests using a fake `TimeProvider`.
* A test with one throwing subscriber and at least two succeeding subscribers, including persistence.
* Architecture tests for immutable payloads.
* Relationship tests for extract results, album/list children, manual search-result follow-ups, and retrieve-folder jobs.

---

# 5. Identity and timestamp semantics

## Runtime identity

* [ ] **ID-01** One `RuntimeId` is allocated before the runtime publishes persistable changes.
* [ ] **ID-02** Every persistence mutation carries or is enveloped with `RuntimeId` and Core sequence.
* [ ] **ID-03** Applied rows record `last_runtime_id`; storing `last_sequence` is required if sequence-based diagnostics/deduplication are exposed.
* [ ] **ID-04** Core sequence is documented as runtime-local, not globally monotonic across restarts.
* [ ] **ID-05** Entity revisions are not compared across runtime incarnations except through an explicit startup reconciliation transition.

## Job identity

* [ ] **ID-06** `Guid JobId` remains the authoritative job identity.
* [ ] **ID-07** Display IDs do not collide with retained history after restart.
* [ ] **ID-08** Display-ID allocation is moved out of `Job._nextDisplayId` static state.
* [ ] **ID-09** The allocator is initialized from durable state before job submission is enabled.
* [ ] **ID-10** Allocation is thread-safe and tested with concurrent submissions.
* [ ] **ID-11** Display IDs are `long` end-to-end, or checked overflow behavior is explicitly implemented and tested before retaining `int`. Silent wraparound is forbidden.

## Transfer and attempt identity

* [ ] **ID-12** `TransferId` is an application-generated GUID allocated before the first transfer event.
* [ ] **ID-13** One logical selected source/target movement uses one `TransferId` across retries.
* [ ] **ID-14** A new selected candidate/output pair receives a new `TransferId`.
* [ ] **ID-15** Every low-level retry/fallback invocation receives a distinct `TransferAttemptId`.
* [ ] **ID-16** Stale-download coordinator watch IDs are not reused as durable transfer or attempt IDs unless their lifecycle and uniqueness contract is deliberately unified.
* [ ] **ID-17** Runtime active-transfer tracking is keyed by `TransferId`, never by remote filename alone.

## Timestamps

* [ ] **ID-18** All durable timestamps are stored in UTC using one documented SQLite representation.
* [ ] **ID-19** Job `created_at_utc` equals the registration source timestamp.
* [ ] **ID-20** Job `started_at_utc` is set once on the first transition into running execution.
* [ ] **ID-21** Job `updated_at_utc` equals the newest applied mutation timestamp and never moves backward.
* [ ] **ID-22** Job `completed_at_utc` is set once on the terminal transition.
* [ ] **ID-23** Transfer and attempt start/progress/completion timestamps follow the same source-time and monotonic rules.
* [ ] **ID-24** Search-result `observed_at_utc` comes from the accepted result publication path.
* [ ] **ID-25** Timestamp behavior is deterministic under fake time and does not depend on database write latency.

---

# 6. Database and schema

## Database initialization

* [ ] **DB-01** SQLite is the only supported initial provider.
* [ ] **DB-02** The connection string enables foreign keys on every connection.
* [ ] **DB-03** The connection/default timeout is finite and documented; the planned default is five seconds.
* [ ] **DB-04** Database initialization requests WAL mode and verifies the resulting journal mode.
* [ ] **DB-05** Every writer connection uses `PRAGMA synchronous=FULL` for the initial release.
* [ ] **DB-06** A fresh context opened after initialization still enforces foreign keys.
* [ ] **DB-07** The database path is under the persistent application data directory and is configurable.
* [ ] **DB-08** Startup clearly rejects or documents unsupported network-filesystem/WAL placement.
* [ ] **DB-09** A single-instance lock is acquired before migration and runtime database ownership.
* [ ] **DB-10** A second Sockseek process targeting the same database fails quickly with a clear error.

## Required tables

Full completion requires, at minimum:

* [ ] **DB-11** `runtime_sessions`.
* [ ] **DB-12** `jobs`.
* [ ] **DB-13** `search_jobs`.
* [ ] **DB-14** `search_results`.
* [ ] **DB-15** `transfers`.
* [ ] **DB-16** `transfer_attempts`.

A materialized `workflows` table is not required. If added, it MUST be rebuildable from jobs and treated as cached projection data.

## Constraints and indexes

* [ ] **DB-17** Primary keys and foreign keys match the identity model.
* [ ] **DB-18** Job relationship deletes use explicit restrict/set-null/cascade behavior; deleting one source job cannot accidentally erase unrelated workflow history.
* [ ] **DB-19** `display_id` has a uniqueness constraint across retained job history.
* [ ] **DB-20** `UNIQUE(search_job_id, sequence)` exists.
* [ ] **DB-21** `UNIQUE(search_job_id, username, remote_filename)` exists, or duplicate semantics are deliberately changed and tested.
* [ ] **DB-22** Search pagination has an index beginning with `(search_job_id, sequence)`.
* [ ] **DB-23** Job listing has an index supporting `(created_at_utc, id)` cursor order and common filters.
* [ ] **DB-24** Transfer listing has indexes for job, workflow, state/outcome, direction, and start/completion time as required by the public queries.
* [ ] **DB-25** Transfer attempts have a unique attempt ID and `UNIQUE(transfer_id, attempt_number)`.
* [ ] **DB-26** Revision columns are non-null and constrained to valid nonnegative values.
* [ ] **DB-27** Terminal timestamps/outcomes have schema or application constraints preventing impossible combinations.

## Durable enums and payloads

* [ ] **DB-28** Job, transfer, attempt, search-persistence, failure, cancellation, skip, direction, and source values use Sockseek-owned stable codes.
* [ ] **DB-29** Unknown future enum codes can be surfaced safely rather than crashing history reads.
* [ ] **DB-30** Job kind-specific JSON is versioned by `payload_schema_version`.
* [ ] **DB-31** Payload readers support every schema version still present in a supported database.
* [ ] **DB-32** Nested `JobSnapshot` graphs are normalized into job rows/relationships rather than serialized recursively.
* [ ] **DB-33** API DTO JSON is not stored as the database contract.
* [ ] **DB-34** The current NUL-delimited candidate key is not stored.
* [ ] **DB-35** File attributes use stable Sockseek-owned codes.
* [ ] **DB-36** Secrets, credentials, access tokens, cookies, and complete unfiltered settings objects are never persisted.

---

# 7. Migrations and compatibility

## Stop conditions

* [ ] **MIG-01** `EnsureCreated` is not used in production or migration tests.
* [ ] **MIG-02** A reviewed initial migration creates the complete persistence schema.
* [ ] **MIG-03** Migrations live in `Sockseek.Persistence` unless a reviewed packaging need requires otherwise.
* [ ] **MIG-04** A new empty database migrates to the latest version automatically or through the documented migration command.
* [ ] **MIG-05** Every supported previous released schema upgrades to the latest schema in automated tests.
* [ ] **MIG-06** Upgrade tests use real temporary SQLite files, not only in-memory connections.
* [ ] **MIG-07** Migration failure prevents domain runtime startup and produces a clear actionable error.
* [ ] **MIG-08** Only one process may migrate a given database.
* [ ] **MIG-09** Generated migrations are checked into source control and reviewed for destructive/rebuild behavior.
* [ ] **MIG-10** A destructive/rebuilding migration cannot run automatically unless a verified safe backup has succeeded.
* [ ] **MIG-11** If backup tooling is not implemented, automatic migrations are restricted to additive or demonstrably non-destructive operations.
* [ ] **MIG-12** Migration code does not silently discard rows, reset display IDs, change stable enum meanings, or invalidate retained JSON without an explicit transformation.
* [ ] **MIG-13** A schema/version compatibility error is distinguishable from database corruption and ordinary database unavailability.

## Required evidence

* Golden database fixtures for each supported released migration version.
* Tests that verify row counts, identities, relationships, timestamps, and payload interpretation after upgrade.
* At least one test of a SQLite table-rebuild migration before destructive migrations are permitted in releases.

---

# 8. Runtime sessions and restart reconciliation

## Stop conditions

* [ ] **RUN-01** Startup creates a `runtime_sessions` row before persistable domain work begins.
* [ ] **RUN-02** Clean shutdown marks the current session stopped with a documented shutdown kind.
* [ ] **RUN-03** An unclean prior session is detectable by `stopped_at_utc IS NULL` or equivalent durable state.
* [ ] **RUN-04** On startup, jobs and transfers last touched by an unfinished prior runtime and still nonterminal are transitioned to `Interrupted` in a database transaction.
* [ ] **RUN-05** Startup interruption uses a deliberate persisted transition with new revision/timestamp semantics; it does not compare a reset live revision to an old runtime revision.
* [ ] **RUN-06** Completed, failed, cancelled, skipped, and already interrupted rows are not changed by reconciliation.
* [ ] **RUN-07** Search jobs from an unfinished prior runtime become `Interrupted` unless durable completion was committed.
* [ ] **RUN-08** Search result rows already committed remain readable after the search is marked interrupted.
* [ ] **RUN-09** A clean prior session does not cause false interruption.
* [ ] **RUN-10** No historical job or transfer is automatically resumed.
* [ ] **RUN-11** The documented crash guarantee states that a real completion occurring immediately before an abrupt crash may conservatively appear as interrupted if its terminal mutation was not committed.

## Required evidence

A real-file restart test MUST cover:

1. Create and migrate database.
2. Start runtime session A.
3. Persist pending/running jobs, transfer progress, attempts, and partial search results.
4. Simulate abrupt termination without marking session A stopped.
5. Start runtime session B.
6. Verify transactional interruption and retained partial history.
7. Verify new display IDs do not collide.
8. Verify historical query endpoints return the reconciled records.

---

# 9. Job persistence

## Coverage

Every current `JobSnapshotKind` MUST round-trip through persistence:

* [ ] **JOB-01** Generic.
* [ ] **JOB-02** Extract.
* [ ] **JOB-03** Search.
* [ ] **JOB-04** Song.
* [ ] **JOB-05** Album.
* [ ] **JOB-06** Aggregate.
* [ ] **JOB-07** AlbumAggregate.
* [ ] **JOB-08** JobList.
* [ ] **JOB-09** RetrieveFolder.

## State and relationships

* [ ] **JOB-10** Registration creates one durable row with workflow, parent, source, display ID, kind, timestamps, and initial state.
* [ ] **JOB-11** Replayed registration is idempotent and cannot reset a newer row.
* [ ] **JOB-12** Lifecycle, activity, outcome, skip, cancellation, failure, item name, query text, and applicable discovery fields are durable.
* [ ] **JOB-13** Terminal state is monotonic; no later stale mutation can return a terminal job to pending/running/awaiting selection.
* [ ] **JOB-14** The result-job relationship is persisted when an extract job produces a result.
* [ ] **JOB-15** Parent relationships reproduce the current execution tree.
* [ ] **JOB-16** Source relationships reproduce current follow-up semantics for manual file/folder/retrieve operations.
* [ ] **JOB-17** A job may be a workflow root while still having a `SourceJobId`.
* [ ] **JOB-18** Historical workflow lists, details, and trees derived from jobs match the live store’s relationship semantics.
* [ ] **JOB-19** Current-state fields are mapped explicitly; `CanCancel`, `PrintOption`, and other presentation-only fields are not stored as authoritative history unless a documented query requires them.
* [ ] **JOB-20** Job-kind payload JSON contains only durable display/reprojection facts and has round-trip tests.
* [ ] **JOB-21** Retained historical jobs remain readable after current settings or runtime object models change within the supported compatibility window.

## Transaction boundaries

* [ ] **JOB-22** Structural registration/link mutations are high priority.
* [ ] **JOB-23** A terminal job mutation commits all fields needed to render the final job state atomically.
* [ ] **JOB-24** Transfer terminal mutations that also finalize a job update both rows in one transaction.
* [ ] **JOB-25** Search completion that also finalizes search-job/job metadata commits those records with the final result batch in one transaction.

---

# 10. Transfer persistence

## Transfer contract

* [ ] **XFER-01** Transfer contracts use stable Sockseek-owned `Direction`, `Source`, `State`, and `Outcome` values.
* [ ] **XFER-02** Transfer contracts are not Soulseek-candidate-only; fallback transfers can be represented without fabricating a `FileCandidate`.
* [ ] **XFER-03** `JobId` is nullable at the durable schema boundary for future non-job uploads, while all current download transfers retain their owning job when one exists.
* [ ] **XFER-04** Username/remote path/source-specific identifiers are nullable or constrained according to transfer source.
* [ ] **XFER-05** Local reused/existing-file satisfaction does not create a fake peer/fallback transfer unless the product explicitly models local-copy transfers.
* [ ] **XFER-06** Every actual Soulseek peer movement creates one logical transfer.
* [ ] **XFER-07** Every actual `yt-dlp` or other supported fallback movement creates one logical transfer.
* [ ] **XFER-08** Selecting a replacement candidate creates a new logical transfer rather than mutating the identity of the failed transfer.

## Start, progress, and terminal semantics

* [ ] **XFER-09** Transfer start is published before low-level progress/state callbacks can be observed.
* [ ] **XFER-10** Progress is latest-value state keyed by `TransferId`, not an append-only event stream.
* [ ] **XFER-11** The default progress flush interval is configurable and between two and five seconds.
* [ ] **XFER-12** Terminal completion flushes the latest known progress before or with terminal state.
* [ ] **XFER-13** `TransferCompleted` is emitted only after the final output path exists and is the path Sockseek will report.
* [ ] **XFER-14** For `.incomplete` downloads, completion occurs only after successful final rename/move.
* [ ] **XFER-15** Completion occurs only after any required downloaded-file/duplicate-cache publication.
* [ ] **XFER-16** Final rename failure emits a failed logical transfer.
* [ ] **XFER-17** Manual skip/cancel of an already-started transfer emits a cancelled terminal transfer.
* [ ] **XFER-18** Stale cancellation emits an attempt outcome and eventually one logical transfer terminal outcome.
* [ ] **XFER-19** Exhausted retries or unrecoverable peer failures emit a failed logical transfer.
* [ ] **XFER-20** Fallback cancellation/failure/success emits the same logical terminal contract.
* [ ] **XFER-21** Every started transfer has exactly one durable terminal outcome: completed, failed, or cancelled.
* [ ] **XFER-22** No terminal outcome is emitted for a transfer that never started.
* [ ] **XFER-23** A late progress or protocol-state callback cannot overwrite terminal state or reduce final bytes.
* [ ] **XFER-24** Completed bytes are exact when a trustworthy total is known.
* [ ] **XFER-25** Unknown totals are represented explicitly, not as a misleading successful zero-byte total.

## Composite terminal mutation

* [ ] **XFER-26** The adapter creates one composite persistence mutation for logical transfer termination.
* [ ] **XFER-27** The terminal mutation contains complete immutable transfer and owning-job persistence snapshots and does not query live state.
* [ ] **XFER-28** The writer removes buffered progress, applies final progress, applies terminal transfer state, applies owning job state, applies final attempt outcome, and commits atomically.
* [ ] **XFER-29** Failed transaction commits none of the composite terminal changes.
* [ ] **XFER-30** Retrying the identical composite mutation is idempotent.

## Required evidence

Deterministic tests MUST cover:

* 100,000 progress callbacks within one flush window produce a bounded number of database writes independent of callback count.
* A blocking/failing database writer does not block the Soulseek progress callback thread.
* Late progress after completion is ignored by revision/terminal checks.
* Rename failure, manual skip, stale cancellation, peer failure, successful peer download, and successful fallback download each produce the expected final rows.
* Candidate replacement produces two transfers; connection retry for one candidate produces one transfer with multiple attempts.

---

# 11. Transfer-attempt persistence

Because the current downloader retries and already reports attempt failures, transfer-attempt history is mandatory for full completion.

## Stop conditions

* [ ] **ATT-01** A `TransferAttemptId` is allocated before each low-level peer/fallback invocation.
* [ ] **ATT-02** Attempt-start, attempt-completed, attempt-failed, and attempt-cancelled changes exist.
* [ ] **ATT-03** Every started attempt has exactly one terminal outcome.
* [ ] **ATT-04** Attempt number is monotonic within one transfer and begins at a documented value.
* [ ] **ATT-05** Retry calls for the same candidate/output stay under one `TransferId` and receive distinct attempt IDs/numbers.
* [ ] **ATT-06** Stale cancellation is recorded as an attempt outcome, not a separate logical transfer.
* [ ] **ATT-07** Manual cancellation identifies the attempt outcome separately from the logical transfer outcome.
* [ ] **ATT-08** Attempt kind/source distinguishes Soulseek peer, fallback provider, and any future protocol-specific retry type.
* [ ] **ATT-09** Attempt records contain source identity/path, target path, start/end time, outcome, and normalized failure data.
* [ ] **ATT-10** Successful final attempt, terminal transfer, and final owning-job state commit atomically where they represent one completion boundary.
* [ ] **ATT-11** Attempt failure followed by retry is durable without prematurely terminalizing the transfer.
* [ ] **ATT-12** Attempt failure details are bounded/sanitized so unbounded exception strings cannot grow rows without limit.
* [ ] **ATT-13** Attempt history is pageable/queryable by transfer.

---

# 12. Search persistence and historical reprojection

## Neutral search record

* [ ] **SEARCH-01** One Sockseek-owned neutral search-result input type contains every fact required for current projection/ranking.
* [ ] **SEARCH-02** Live `SearchSession` converts Soulseek results into that neutral type before publication.
* [ ] **SEARCH-03** Persistence stores that neutral data without retaining Soulseek objects.
* [ ] **SEARCH-04** Historical readers reconstruct the same neutral type.
* [ ] **SEARCH-05** Live and historical projection use the same projector implementation.
* [ ] **SEARCH-06** Golden parity tests show identical projected file/folder/aggregate results from live and persisted neutral inputs.

## Result acceptance and completion

* [ ] **SEARCH-07** Result acceptance and completion are synchronized so no result can be accepted after completion publication.
* [ ] **SEARCH-08** Duplicate username/remote-filename results follow one documented policy enforced consistently in memory and SQLite.
* [ ] **SEARCH-09** Sequence is unique and strictly increasing within a search job.
* [ ] **SEARCH-10** Search revision is monotonic.
* [ ] **SEARCH-11** Accepted results carry source observation timestamps.
* [ ] **SEARCH-12** Result batches flush after a configurable count within 100–500 or a configurable interval within 100–250 ms.
* [ ] **SEARCH-13** Search completion is a barrier that flushes all pending results and completes metadata in one transaction.
* [ ] **SEARCH-14** Completion records final result count, locked-file count, revision, and completion time.
* [ ] **SEARCH-15** An abrupt crash may lose only the documented uncommitted tail; restart marks the search interrupted rather than falsely complete.

## Persistence-state semantics

* [ ] **SEARCH-16** `result_persistence_state` distinguishes `Complete`, `Incomplete`, `Pruned`, `NotPersisted`, and `Interrupted`.
* [ ] **SEARCH-17** Zero durable result rows plus `Complete` means the search genuinely completed with zero stored results.
* [ ] **SEARCH-18** Degraded-mode loss sets `Incomplete` and cannot later be reported as `Complete` unless reconciliation proves completeness.
* [ ] **SEARCH-19** Retention pruning sets `Pruned` and records `results_pruned_at_utc`.
* [ ] **SEARCH-20** Parent job deletion/pruning behavior for raw results is explicit and tested.

## Historical behavior

* [ ] **SEARCH-21** Raw historical results are cursor-paged by sequence with an enforced finite maximum page size.
* [ ] **SEARCH-22** Historical file, folder, aggregate-track, and aggregate-album projections work after a full process restart.
* [ ] **SEARCH-23** Projection does not fabricate `SearchResponse` or `Soulseek.File` objects.
* [ ] **SEARCH-24** A persisted search result contains enough source information to start a new download command when still valid.
* [ ] **SEARCH-25** Existing follow-up endpoints can resolve candidate/folder references from persisted search rows when the source search job is no longer live.
* [ ] **SEARCH-26** Starting a historical follow-up creates a new job and new transfer identities; it never reactivates the historical search job.
* [ ] **SEARCH-27** Missing/pruned/incomplete result data produces a clear API error rather than a null reference, fabricated candidate, or silent fallback to live state.

---

# 13. Persistence writer and buffering

## Single writer

* [ ] **WRITE-01** One hosted writer is the only ordinary runtime database writer.
* [ ] **WRITE-02** The writer uses `IDbContextFactory<SockseekDbContext>`.
* [ ] **WRITE-03** A short-lived context is created per batch/transaction; no singleton context is retained.
* [ ] **WRITE-04** Runtime event handlers only map/enqueue/update in-memory buffers; they do not call EF or SQLite.
* [ ] **WRITE-05** A deliberately blocked database operation cannot block domain callbacks or cancel active downloads.

## Queues and buffers

* [ ] **WRITE-06** Low-volume structural/state mutations use a bounded single-reader queue.
* [ ] **WRITE-07** Transfer progress uses a latest-value map keyed by transfer ID plus a wake signal.
* [ ] **WRITE-08** Search results use per-search bounded batches.
* [ ] **WRITE-09** No normal or degraded queue/map is unbounded by both count and memory behavior.
* [ ] **WRITE-10** All limits have finite defaults, validation, metrics, and test overrides.
* [ ] **WRITE-11** Terminal and structural work is prioritized over progress, diagnostics, and ordinary activity.
* [ ] **WRITE-12** Search completion and transfer terminal barriers cannot be overtaken by older buffered data for the same entity.
* [ ] **WRITE-13** The writer coalesces by entity/revision rather than queue arrival order alone.

## Transactions and idempotency

* [ ] **WRITE-14** Related rows required for one semantic barrier commit in one SQLite transaction.
* [ ] **WRITE-15** Upsert predicates enforce monotonic revision.
* [ ] **WRITE-16** Duplicate delivery is idempotent.
* [ ] **WRITE-17** Out-of-order delivery cannot regress durable state.
* [ ] **WRITE-18** A failed transaction is retried only when its failure class is considered transient.
* [ ] **WRITE-19** Retry policy for `SQLITE_BUSY` is bounded by attempts/time and uses cancellation.
* [ ] **WRITE-20** Non-transient schema, constraint, corruption, and disk errors are not retried forever.
* [ ] **WRITE-21** Writer failures are rate-limited in logs without hiding the first error or state transitions.

---

# 14. Degraded mode and recovery

## Stop conditions

* [ ] **DEG-01** Persistence has explicit `Healthy`, `Degraded`, and `Unhealthy` states, or equivalent documented states.
* [ ] **DEG-02** A transient write failure changes health and records the last error/time.
* [ ] **DEG-03** Persistence failure alone never cancels or fails an active domain download.
* [ ] **DEG-04** In degraded mode, only the latest job and transfer state per entity is retained.
* [ ] **DEG-05** Terminal job/transfer projections are retained preferentially over nonterminal snapshots.
* [ ] **DEG-06** Degraded maps have explicit finite count and/or memory limits.
* [ ] **DEG-07** Limit eviction is deterministic and increments a dropped-terminal-projection counter when terminal state is lost.
* [ ] **DEG-08** Diagnostics and optional activity persistence are dropped before structural/terminal state.
* [ ] **DEG-09** Raw search buffering has an explicit finite cap per search and globally.
* [ ] **DEG-10** Exceeding a search cap marks the search persistence state `Incomplete`.
* [ ] **DEG-11** Health reports all dropped/evicted categories, not only a generic error flag.
* [ ] **DEG-12** On recovery, the writer reconciles the latest live job and transfer snapshots still available.
* [ ] **DEG-13** Reconciliation cannot overwrite newer durable terminal state.
* [ ] **DEG-14** Search history is never silently promoted from incomplete to complete after data loss.
* [ ] **DEG-15** Recovery returns health to `Healthy` only after a successful write and reconciliation pass.
* [ ] **DEG-16** A week-long simulated outage with many new entities remains within configured memory bounds.

## Required counters

At minimum, health/metrics MUST expose:

* Queue depth and capacity.
* Coalesced transfer count.
* Buffered search-result count.
* Last successful commit time.
* Last write error and time.
* Busy retry count.
* Dropped diagnostic/activity count.
* Dropped search-result count.
* Incomplete-search count.
* Evicted terminal projection count.
* Reconciliation success/failure count.
* Batch size and commit latency distributions.

---

# 15. Live/history query facade and API behavior

## Query facade

* [ ] **QUERY-01** REST handlers do not query `EngineStateStore` directly for history-capable reads.
* [ ] **QUERY-02** A query facade reads live state and persisted history and maps both into one outward contract.
* [ ] **QUERY-03** Live state wins when the same active entity is present in both sources.
* [ ] **QUERY-04** History is not loaded wholesale into the live state store at startup.
* [ ] **QUERY-05** Historical reads use `AsNoTracking` or equivalent read-only behavior.
* [ ] **QUERY-06** Current brief eventual-consistency semantics are documented: a newly registered live job may not appear in paged history until registration commits.
* [ ] **QUERY-07** The facade does not claim strict snapshot consistency across concurrently changing live and durable sources.

## Jobs and workflows

* [ ] **QUERY-08** `/api/jobs` is cursor-paginated by `(created_at_utc, id)` or an equivalent stable unique order.
* [ ] **QUERY-09** `/api/jobs` enforces a finite maximum page size.
* [ ] **QUERY-10** Existing lifecycle, terminal outcome, kind, workflow, skip, and include-all filters work against history.
* [ ] **QUERY-11** Live overlay re-applies effective fields for rows already in the durable page.
* [ ] **QUERY-12** `/api/jobs/{id}` returns a historical job after restart.
* [ ] **QUERY-13** `/api/workflows`, workflow detail, and workflow tree work from persisted jobs after restart.
* [ ] **QUERY-14** Workflow tree uses parent relationships only; source links remain separately visible.
* [ ] **QUERY-15** Display-ID lookup is unambiguous within the documented scope and works after restart for history reads.
* [ ] **QUERY-16** Commands such as cancel/next-candidate/manual completion operate only on live active jobs and return a clear non-actionable response for historical jobs.

## Searches

* [ ] **QUERY-17** `/api/jobs/{id}/raw` reads live or persisted results and enforces a maximum page size.
* [ ] **QUERY-18** Existing result projection endpoints work for historical search jobs.
* [ ] **QUERY-19** Historical follow-up download/retrieve endpoints resolve persisted source data without runtime object lookup.

## Transfers and attempts

* [ ] **QUERY-20** There is a documented paginated public query for transfer history, either dedicated endpoints or an explicit job-detail child resource.
* [ ] **QUERY-21** A transfer can be read by transfer ID.
* [ ] **QUERY-22** Transfers can be filtered by job, workflow, direction, source, state/outcome, username where applicable, and time range.
* [ ] **QUERY-23** Attempt history can be paged for one transfer.
* [ ] **QUERY-24** Paths/usernames are exposed consistently with the project’s privacy and authorization model.

## API compatibility

* [ ] **QUERY-25** OpenAPI output is regenerated and checked in if this repository tracks generated OpenAPI.
* [ ] **QUERY-26** New pagination contracts have stable cursor encoding and validation.
* [ ] **QUERY-27** Invalid/malformed cursors return a clear client error.
* [ ] **QUERY-28** Unknown/pruned/incomplete historical data is represented explicitly, not as misleading empty success.
* [ ] **QUERY-29** Existing clients that ignore new fields continue to function where backward compatibility is claimed.

---

# 16. Retention, pruning, and privacy

## Retention policies

* [ ] **RET-01** Configurable policies exist for completed jobs, failed/cancelled/interrupted jobs, transfers, transfer attempts, raw search results, and optional activity events.
* [ ] **RET-02** Each policy supports a documented “forever” setting.
* [ ] **RET-03** Supported bounded modes—age, row count, or both—are documented and tested.
* [ ] **RET-04** Active/nonterminal rows are never pruned.
* [ ] **RET-05** Retention runs outside the domain hot path through the persistence writer/maintenance boundary.
* [ ] **RET-06** Pruning uses bounded batches/transactions and cannot hold a write lock for an unbounded duration.
* [ ] **RET-07** Raw search results may be pruned before the parent job; parent metadata becomes `Pruned` with timestamp.
* [ ] **RET-08** Relationship and foreign-key behavior remains valid after job/history pruning.
* [ ] **RET-09** Transfer attempts do not outlive their transfer unless intentionally archived through a documented model.
* [ ] **RET-10** Retention never changes retained job/transfer outcomes.
* [ ] **RET-11** Retention reports rows removed, duration, and failures.

## Privacy and deletion

* [ ] **RET-12** Documentation states that the database may contain usernames, remote paths, local paths, queries, and search-result metadata.
* [ ] **RET-13** A supported way exists to delete/prune persisted history according to configuration or an explicit maintenance command.
* [ ] **RET-14** No credentials/tokens are stored in settings snapshots or payload JSON.
* [ ] **RET-15** Database file permissions are restricted as far as the supported platform permits.
* [ ] **RET-16** Backup files receive equivalent privacy handling and permissions.

---

# 17. Backup, integrity, and maintenance

Full persistence is operational state, not merely ORM mappings.

## Stop conditions

* [ ] **OPS-01** A safe backup operation exists before any destructive/rebuilding automatic migration is enabled.
* [ ] **OPS-02** Backup uses SQLite’s backup API or another WAL-safe procedure; copying only the main `.db` file while active is forbidden.
* [ ] **OPS-03** Backup produces a self-contained file that opens and passes integrity verification.
* [ ] **OPS-04** Backup failure aborts a migration that requires backup.
* [ ] **OPS-05** A documented restore procedure exists and is tested against a temporary application data directory.
* [ ] **OPS-06** An integrity-check maintenance command or endpoint exists.
* [ ] **OPS-07** Integrity failures produce an explicit unhealthy/startup failure state and do not silently create a replacement empty database over the damaged file.
* [ ] **OPS-08** WAL checkpoint behavior is available to maintenance/backup operations without blocking active runtime work indefinitely.
* [ ] **OPS-09** Optional `VACUUM` is never run automatically on the transfer hot path.
* [ ] **OPS-10** Database size and schema version are observable.
* [ ] **OPS-11** A `--migrate-only` or equivalent packaging/troubleshooting path exists, or the absence is explicitly justified in a reviewed decision.

If safe backup and integrity tooling are intentionally excluded from the first persistence release, then **OPS-01 through OPS-07 remain incomplete and persistence must not be described as fully operational**; additionally, destructive migrations MUST remain disabled.

---

# 18. Startup and shutdown ordering

## Startup

* [ ] **LIFE-01** Single-instance ownership is acquired first.
* [ ] **LIFE-02** Configuration and database path are validated before migration.
* [ ] **LIFE-03** Database initialization/migration completes before runtime-session creation.
* [ ] **LIFE-04** Runtime-session creation and interruption reconciliation complete before job submission is accepted.
* [ ] **LIFE-05** Display-ID allocator seeding completes before any job calls `EnsureDisplayId` or replacement allocation API.
* [ ] **LIFE-06** Persistence adapter and writer are ready before domain runtimes publish persistable changes.
* [ ] **LIFE-07** Query endpoints do not report healthy persistence before initialization/reconciliation succeeds.

## Shutdown

* [ ] **LIFE-08** Job submission is stopped before runtime shutdown begins.
* [ ] **LIFE-09** Domain runtimes stop producing changes before the persistence writer is completed.
* [ ] **LIFE-10** Pending search batches, latest transfer progress, structural mutations, and terminal mutations are drained in the documented order.
* [ ] **LIFE-11** Drain has a configurable finite timeout.
* [ ] **LIFE-12** Timeout does not hang process shutdown indefinitely.
* [ ] **LIFE-13** A timed-out drain is reported with remaining counts and does not falsely mark the runtime session clean.
* [ ] **LIFE-14** The runtime session is marked cleanly stopped only after the required drain succeeds.
* [ ] **LIFE-15** Database/context resources close after the writer and session-finalization step.

---

# 19. Health and observability

## Stop conditions

* [ ] **HEALTH-01** `/api/server/status` or a dedicated health endpoint includes persistence state.
* [ ] **HEALTH-02** Health includes database initialized/migrated status and schema version.
* [ ] **HEALTH-03** Health includes current runtime-session ID and start time.
* [ ] **HEALTH-04** Health includes last successful commit and last failure summary/time.
* [ ] **HEALTH-05** Health includes queue/buffer depth, capacity, and degraded-mode counters.
* [ ] **HEALTH-06** Health distinguishes temporary busy/retry pressure from persistent write failure, migration failure, and integrity failure.
* [ ] **HEALTH-07** Data-loss counters remain visible after recovery until explicitly reset by a documented lifecycle.
* [ ] **HEALTH-08** Logs include mutation type/entity identity/revision where safe, without logging credentials or unbounded payloads.
* [ ] **HEALTH-09** Metrics include commit latency, batch sizes, rows written, busy retries, database size, retention activity, and reconciliation results.
* [ ] **HEALTH-10** Health reporting does not itself require a write lock or block domain work.

---

# 20. Correctness and concurrency test matrix

All mandatory tests MUST use deterministic synchronization rather than relying only on wall-clock sleeps.

## Core/event tests

* [ ] **TEST-01** Published change payloads remain unchanged after the underlying mutable runtime objects are modified.
* [ ] **TEST-02** Throwing observer isolation.
* [ ] **TEST-03** Persistence subscription ordering.
* [ ] **TEST-04** Revision ownership and monotonicity.
* [ ] **TEST-05** Search completion rejects late additions under concurrent `AddResponse`/`Complete` calls.
* [ ] **TEST-06** Relationship publication for all current submission/follow-up paths.

## File-backed SQLite tests

* [ ] **TEST-07** Fresh migration and foreign-key enforcement on newly opened contexts.
* [ ] **TEST-08** WAL mode and synchronous configuration verification.
* [ ] **TEST-09** Concurrent readers while the single writer commits.
* [ ] **TEST-10** Real `SQLITE_BUSY` lock contention and bounded retry behavior.
* [ ] **TEST-11** Disk/unwritable-path failure enters degraded/unhealthy mode without stopping domain work.
* [ ] **TEST-12** Constraint failure is surfaced and not retried forever.
* [ ] **TEST-13** Restart reconciliation from an unfinished runtime.
* [ ] **TEST-14** Clean restart does not interrupt completed/clean rows.
* [ ] **TEST-15** Migration upgrade fixtures.
* [ ] **TEST-16** Backup opens independently and passes integrity check.

## Job tests

* [ ] **TEST-17** Round-trip every current job kind.
* [ ] **TEST-18** Parent/source/result relationships.
* [ ] **TEST-19** Display-ID continuation after restart and concurrent allocation.
* [ ] **TEST-20** Terminal job cannot regress from stale updates.
* [ ] **TEST-21** Workflow history derived from jobs matches live workflow semantics.

## Transfer tests

* [ ] **TEST-22** Successful Soulseek transfer.
* [ ] **TEST-23** Successful fallback transfer.
* [ ] **TEST-24** Retry then success under one transfer with multiple attempts.
* [ ] **TEST-25** Candidate failure then replacement candidate under a new transfer.
* [ ] **TEST-26** Stale cancellation.
* [ ] **TEST-27** Manual skip/cancel.
* [ ] **TEST-28** Final rename failure.
* [ ] **TEST-29** Late progress after terminal.
* [ ] **TEST-30** Unknown total-size behavior.
* [ ] **TEST-31** Composite terminal transaction rollback and retry.

## Search tests

* [ ] **TEST-32** Duplicate result policy.
* [ ] **TEST-33** Sequence pagination without gaps/duplicates.
* [ ] **TEST-34** Completion with a pending batch.
* [ ] **TEST-35** Large result set batching.
* [ ] **TEST-36** Live/historical projection parity.
* [ ] **TEST-37** Historical follow-up download after restart.
* [ ] **TEST-38** Pruned, incomplete, interrupted, and zero-result distinctions.
* [ ] **TEST-39** Degraded-mode cap marks incomplete state.

## Query/API tests

* [ ] **TEST-40** Stable job cursor pagination across retained history.
* [ ] **TEST-41** Live overlay wins for the same entity.
* [ ] **TEST-42** Historical job/workflow/search/transfer/attempt reads after restart.
* [ ] **TEST-43** Maximum page-size enforcement.
* [ ] **TEST-44** Malformed cursor behavior.
* [ ] **TEST-45** Historical commands return clear non-actionable responses unless they intentionally create a new job.

## Retention/operations tests

* [ ] **TEST-46** Age-based pruning.
* [ ] **TEST-47** Count-based pruning.
* [ ] **TEST-48** Active rows are retained.
* [ ] **TEST-49** Search result pruning sets `Pruned` metadata.
* [ ] **TEST-50** Restore procedure.
* [ ] **TEST-51** Second-instance lock rejection.

---

# 21. Performance and non-interference conditions

These are invariants, not hardware-specific benchmark promises.

* [ ] **PERF-01** Database work is never executed synchronously inside Soulseek progress/state callbacks.
* [ ] **PERF-02** A test can block the database writer indefinitely while progress callbacks continue to return and live transfer state continues to update.
* [ ] **PERF-03** Progress database write count scales with active transfer count and flush intervals, not callback count.
* [ ] **PERF-04** Search inserts scale by configured batch count/time, not one transaction per file.
* [ ] **PERF-05** Readers use indexed pagination and do not load all history into memory.
* [ ] **PERF-06** Retention operates in bounded batches.
* [ ] **PERF-07** Database commit latency and queue growth are measured in integration/load tests.
* [ ] **PERF-08** The configured bounded queues remain within their stated limits under sustained producer load.
* [ ] **PERF-09** Large-history query tests include at least 100,000 jobs/transfers or an equivalently justified fixture size and verify indexed query plans for primary listings.
* [ ] **PERF-10** Large search-history tests include at least 100,000 raw results for one or more searches and verify cursor reads remain bounded.

No fixed millisecond pass threshold is required across all CI hardware. Instead, the tests MUST prove structural non-interference, bounded allocation, bounded write count, indexed plans, and absence of callback-thread database work.

---

# 22. Documentation and release conditions

* [ ] **DOC-01** The architecture document describes the implemented design rather than obsolete mutable-boundary behavior.
* [ ] **DOC-02** The non-blocking crash-durability guarantee is explicit.
* [ ] **DOC-03** Brief live/history eventual consistency is explicit.
* [ ] **DOC-04** Automatic resume is explicitly not provided.
* [ ] **DOC-05** Database location and configuration are documented.
* [ ] **DOC-06** Local-filesystem/WAL requirement and network-filesystem limitation are documented.
* [ ] **DOC-07** Retention defaults and privacy implications are documented.
* [ ] **DOC-08** Backup, restore, integrity check, and migration behavior are documented.
* [ ] **DOC-09** Persistence health fields and loss counters are documented.
* [ ] **DOC-10** Search `Complete`/`Incomplete`/`Pruned`/`NotPersisted`/`Interrupted` meanings are documented.
* [ ] **DOC-11** Transfer, attempt, job, source-job, and parent-job identities are documented.
* [ ] **DOC-12** Historical follow-up download semantics are documented: a new job/transfer is created.
* [ ] **DOC-13** Unsupported operations on historical jobs are documented.
* [ ] **DOC-14** Migration compatibility policy states which prior database versions are supported.
* [ ] **DOC-15** Release notes call out API/schema/configuration changes and display-ID compatibility changes.

---

# 23. Stop-work conditions

Implementation MUST pause and resolve the issue before proceeding if any of the following occurs:

1. A persistence mutation requires dereferencing a mutable runtime object after enqueue.
2. A database call appears on a Soulseek callback, domain transition, or transfer-completion control path.
3. A terminal transfer change cannot describe final local-path state without querying live objects.
4. The same started transfer can finish without exactly one terminal outcome.
5. A late/stale mutation can regress a terminal job, transfer, attempt, or completed search.
6. Queue or degraded-mode growth is unbounded.
7. Terminal projection eviction can occur without a visible loss counter and unhealthy/degraded status.
8. A destructive migration is introduced without verified backup and upgrade tests.
9. A history query requires loading the complete jobs/search-results/transfers table into memory.
10. Historical projection requires constructing fake Soulseek objects.
11. Historical follow-up commands require resurrecting a mutable historical `Job`.
12. Source, parent, or result relationships cannot be derived authoritatively from immutable changes.
13. Observer exceptions can prevent persistence delivery.
14. Search results can be accepted after completion.
15. Fallback downloads remain successful file movements with no transfer history while the feature claims full transfer persistence.
16. Transfer retries remain visible only as aggregate failure text while the feature claims full attempt persistence.
17. Persistence failure cancels or stalls active download work.
18. A second process can migrate/write the same database without an ownership error.
19. Integrity or migration failure causes the application to silently replace the user’s database.
20. A test passes only with in-memory SQLite for behavior involving locking, WAL, restart, shutdown, migration, or backup.
21. Required tests are skipped, flaky, timing-dependent without deterministic synchronization, or not run in CI.
22. The implementation changes a public consistency/durability promise without updating documentation and tests.

---

# 24. Final release gate

The persistence implementation may stop and be released as complete only when a reviewer can answer **yes** to every question below:

* [ ] Can every current job kind and relationship be read after restart?
* [ ] Can every current real remote/fallback file movement be read as a logical transfer?
* [ ] Can every retry be read as a durable transfer attempt?
* [ ] Can a transfer terminal row be trusted to mean the local finalization boundary was crossed?
* [ ] Can raw search results and all current projections be used after restart?
* [ ] Can a user start a new download from a retained historical search result?
* [ ] Can history be paged without loading it all into memory?
* [ ] Can the database be unavailable without stopping live downloads or growing memory without bound?
* [ ] Can operators see when persistence lost or pruned data?
* [ ] Can an unclean shutdown be distinguished from a clean shutdown and reconciled safely?
* [ ] Can migrations, backup, restore, and integrity checks be exercised on real SQLite files?
* [ ] Can no stale event regress terminal state?
* [ ] Can no observer exception hide a terminal mutation from persistence?
* [ ] Can the solution build and all unit, integration, concurrency, restart, load, migration, and API tests pass?
* [ ] Does the documentation accurately state durability, consistency, retention, privacy, and operational behavior?

If any answer is **no**, persistence is not finished.
