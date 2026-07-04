I would build this around four decisions:

1. **SQLite as the initial database, with EF Core as the primary data-access layer.**
2. **A dedicated `Sockseek.Persistence` project.**
3. **The running engine remains authoritative for live operations; the database is a durable projection and history store.**
4. **All database writes go through one persistence pipeline that batches ordinary changes, coalesces progress, and immediately flushes terminal state.**

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

### Mutable objects currently cross event boundaries

`DownloadEvents` explicitly notes that it sends mutable `Job` references and that server consumers mitigate this by immediately mapping them to immutable DTOs (`Sockseek.Core/Transfers/Downloads/Events/DownloadEvents.cs`).

The persistence adapter must use the same rule:

> Convert mutable jobs, candidates and Soulseek objects into immutable, Sockseek-owned snapshots synchronously inside the event handler, before placing anything on an asynchronous queue.

Before production persistence writes begin, Core should emit immutable domain
changes directly. A temporary adapter can help during migration, but persistence
must not enqueue live `Job` references.

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
    WorkflowEntity.cs
    JobEntity.cs
    SearchJobEntity.cs
    SearchResultEntity.cs
    TransferEntity.cs
    TransferAttemptEntity.cs

  Configurations/
    WorkflowConfiguration.cs
    JobConfiguration.cs
    SearchResultConfiguration.cs
    TransferConfiguration.cs

  Migrations/

  Write/
    PersistenceMutation.cs
    PersistenceInbox.cs
    PersistenceWriter.cs
    PersistenceWriterHostedService.cs
    SearchResultBatch.cs

  Read/
    JobHistoryReader.cs
    WorkflowHistoryReader.cs
    SearchHistoryReader.cs
    TransferHistoryReader.cs

  Sqlite/
    SqliteInitializer.cs
    SqliteMaintenanceService.cs
```

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

### `workflows`

```text
id
title
state
created_at_utc
updated_at_utc
completed_at_utc
revision
```

Workflow counts can initially be calculated from jobs. Add cached counters only if measurements show that they are needed.

### `jobs`

```text
id
workflow_id
parent_job_id
source_job_id
result_job_id

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
```

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

The same rule applies to download candidates. Persist a copied, Sockseek-owned
candidate snapshot or stable candidate key; do not persist `FileCandidate`,
`SearchResponse`, or `Soulseek.File` as object graphs.

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
candidate_key

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

Add a `TransferId` to the download lifecycle events. The current events are keyed only by `SongJob`, which is insufficient once retries, alternate candidates and uploads are first-class concepts.

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

This table is optional initially, but I would probably include it:

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

Progress belongs on `transfers`; attempt transitions and failures belong in `transfer_attempts`.

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

* Terminal state must never be dropped.
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
EnginePersistenceAdapter immediately creates immutable snapshot
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

Flush approximately every 500 ms to 1 second. Make this configurable, but do not begin with a very small interval.

A download finishing is a barrier:

1. Take the latest buffered progress for the transfer.
2. Write that progress.
3. Write the terminal transfer state.
4. Update the corresponding job.
5. Update any persisted workflow state.
6. Commit them in one transaction.

A late progress callback must not be able to change a completed transfer back to running.

### Add monotonic revisions

Every durable entity mutation should carry a monotonically increasing revision:

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

### Do not write from property-change handlers

`SongJob.BytesTransferred` currently updates from the Soulseek transfer callback in `Downloader.cs:93-106`. Persisting from `INotifyPropertyChanged` would couple database behavior to implementation details and generate writes for properties that may not represent durable business state.

Persist semantic records such as:

* Transfer started.
* Transfer progress observed.
* Transfer attempt failed.
* Transfer state changed.
* Transfer completed.
* Job reached terminal state.

### Channel design

I would use three mechanisms rather than one naïve channel:

1. **Low-volume, non-droppable state mutations**
   An unbounded or generously bounded single-reader channel.

2. **High-frequency progress**
   A concurrent latest-value dictionary keyed by transfer ID plus a lightweight wake signal. It cannot grow with callback count; it grows only with active transfer count.

3. **Search results**
   Per-search batches, flushed after either:

   * 100–500 results, or
   * roughly 100–250 ms.

A `SearchCompleted` mutation is also a barrier: flush every pending result for that search and mark the search complete in the same transaction.

There is an unavoidable durability decision to document: batching means an abrupt process or power failure can lose the newest fraction of a second of progress or search results. Requiring synchronous durability for every search file and every byte callback would put SQLite directly on the network hot path. For job history, bounded tail loss is normally the better tradeoff.

### Database failure behavior

A transient or persistent database failure should:

* Mark persistence as unhealthy.
* Retry bounded transient `SQLITE_BUSY` cases.
* Log a rate-limited error.
* Preserve the newest coalesced progress where possible.
* Never cancel an active download solely because history could not be saved.
* Expose persistence health through `/api/server/status` or a health endpoint.

The queue also needs a documented overload policy. Silent dropping of terminal state is never acceptable.

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

That creates confusing collisions once historical and new jobs are displayed together. There are two reasonable fixes:

### Per-workflow ordinal

Make display IDs unique within a workflow:

```text
UNIQUE(workflow_id, display_id)
```

This aligns with the existing endpoint that resolves display IDs within a workflow.

### Daemon-wide sequence

Persist a daemon-wide counter and allocate display IDs centrally.

This is more convenient for CLI references but becomes a permanent database identity policy.

Either is acceptable, but the allocator should move out of static state. At minimum, startup would have to seed the next value from the database maximum. A proper `IJobIdentityAllocator` owned by the supervisor/application layer is cleaner.

## Timestamps

Store timestamps in UTC. For SQLite, using UTC `DateTime` or an integer Unix-time representation will produce more predictable ordering and filtering than relying heavily on `DateTimeOffset`; Microsoft’s SQLite provider guidance recommends converting timestamps to UTC. ([Microsoft Learn][6])

Use `TimeProvider` throughout new persistence code so crash/restart and retention behavior can be tested deterministically.

---

# SQLite operational configuration

For a local database file, initialize and verify:

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
```

`NORMAL` versus `FULL` is a durability tradeoff. Start with `NORMAL` only if we explicitly accept that the database records history rather than controlling active downloads; otherwise choose `FULL`. Configuration can be added when there is a concrete user/operator need.

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
* Back up the database before a destructive migration.
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

## 1. Establish persistence contracts

Add immutable, persistence-safe records for:

* Job snapshots.
* Workflow snapshots.
* Search-result facts.
* Transfer snapshots.
* Transfer attempts.

Add revisions and a first-class `TransferId`. Model the transfer id as the
logical source/target file movement and model protocol retries as transfer
attempts. Do not treat duplicate-cache reuse, already-exists skips, or other
non-transfer job outcomes as transfers in the first persistence pass.

Do not begin by writing EF entities directly from `DownloadEvents` or any other mutable runtime event bus.

## 2. Add `Sockseek.Persistence`

Implement:

* `SockseekDbContext`.
* Explicit `IEntityTypeConfiguration<T>` classes.
* Initial migration.
* SQLite initialization.
* Temporary-file integration test fixture.
* Migration upgrade tests.

Use real temporary SQLite files in concurrency tests. An in-memory SQLite database will not reproduce file locking, WAL, restart, or shutdown behavior.

## 3. Persist jobs and workflows

Start with job registration and state transitions.

At this point:

* Job history survives restart.
* Previous active jobs become interrupted.
* Historical jobs appear through a combined query service.
* Search results and transfer detail can still be added next.

## 4. Add the transfer writer

Introduce:

* Transfer IDs.
* Transfer attempt IDs.
* Latest-value progress accumulator.
* Periodic flush.
* Terminal barriers.
* Revision checks.
* Attempt records.

Tests should send tens of thousands of progress callbacks and confirm:

* Database write count stays bounded.
* Final byte count is exact.
* Completed state cannot be overwritten by late progress.
* Database latency does not delay the engine callbacks.

## 5. Persist search results in batches

Introduce a Sockseek-owned search-result input type and make projection independent from Soulseek.NET types.

Test:

* Duplicate username/filename results.
* Sequence pagination.
* Completion while a batch is pending.
* Very large result sets.
* Reprojection after process restart.
* Pruning raw results without deleting the parent job.

## 6. Harden the API and operations

Add:

* Pagination and maximum limits.
* Persistence health.
* Retention.
* Backup/migration behavior.
* Graceful writer drain.
* Failure-injection tests.
* Database size and write-latency metrics.

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

Thinking further, **I would refactor the Core event boundary before implementing the persistence writer**, but I would keep the refactor deliberately narrow.

The prerequisite should be:

> Core domain runtimes emit immutable, Sockseek-owned descriptions of state changes.

It should **not** be:

> Rewrite the entire job model into a pure reducer architecture before persistence can begin.

That broader redesign could become a substantial detour.

## Why it belongs first

Persistence makes timing and ownership mistakes permanent. With the current event shape, an event can contain a mutable `Job`, and the eventual consumer observes whatever state the object happens to contain when it reads it—not necessarily the state when the event was raised.

For example:

```csharp
_engineEvents.Publish(new JobChanged(job));

// Another thread changes the same object:
job.BytesTransferred = 20_000_000;
job.State = JobState.Completed;
```

If an asynchronous persistence consumer receives the reference later, it may persist:

* A progress event as already completed.
* A failure event with a later failure reason.
* Multiple queued events as identical final snapshots.
* A collection while another thread is modifying it.

You can avoid this by immediately snapshotting inside a Server adapter, but then every consumer must correctly understand:

* Which fields must be copied.
* Which nested objects are mutable.
* How to copy search results.
* How to identify a transfer attempt.
* Which event combinations form a consistent state.

That duplicates domain knowledge outside Core. SignalR, persistence, logging, metrics, plugins, and future UI projections would each risk implementing a slightly different interpretation.

Once persistence is introduced, changing the event contract becomes harder because the persistence pipeline and tests will be built around it. So this is a good boundary to clean up first.

# The scope I recommend

Refactor `DownloadEvents` first, and apply the same rule to future upload, search,
sharing and messaging event streams: no public event should contain:

* `Job`.
* `Workflow`.
* `SearchSession`.
* `Soulseek.SearchResponse`.
* `Soulseek.File`.
* Mutable collections.
* Cancellation tokens or other runtime infrastructure.

Instead, expose immutable records containing copied values.

For example:

```csharp
public abstract record CoreChange(
    long Sequence,
    DateTimeOffset OccurredAt);

public sealed record JobRegistered(
    long Sequence,
    DateTimeOffset OccurredAt,
    JobSnapshot Job)
    : CoreChange(Sequence, OccurredAt);

public sealed record JobStateChanged(
    long Sequence,
    DateTimeOffset OccurredAt,
    Guid JobId,
    long Revision,
    JobLifecycleState State,
    JobActivityPhase ActivityPhase,
    JobFailureSnapshot? Failure,
    DateTimeOffset? CompletedAt)
    : CoreChange(Sequence, OccurredAt);

public sealed record TransferProgressed(
    long Sequence,
    DateTimeOffset OccurredAt,
    Guid TransferId,
    Guid JobId,
    long Revision,
    long TransferredBytes,
    long TotalBytes)
    : CoreChange(Sequence, OccurredAt);

public sealed record SearchResultsAdded(
    long Sequence,
    DateTimeOffset OccurredAt,
    Guid SearchJobId,
    long Revision,
    IReadOnlyList<SearchResultSnapshot> Results)
    : CoreChange(Sequence, OccurredAt);
```

The nested records should also be immutable:

```csharp
public sealed record SearchResultSnapshot(
    long Sequence,
    string Username,
    string RemoteFilename,
    long SizeBytes,
    int? BitRate,
    int? SampleRate,
    TimeSpan? Duration,
    long UploadSpeed,
    bool HasFreeUploadSlot);
```

Use immutable arrays for batches where practical:

```csharp
ImmutableArray<SearchResultSnapshot>
```

A read-only interface alone is not enough if the underlying collection can still be mutated.

## Snapshot events versus delta events

I would not force every event into either model exclusively.

Use **deltas for high-volume changes**:

* Transfer progress.
* Search-result additions.
* Queue position changes.
* Chat messages, when messaging exists.
* Upload progress, when uploads exist.

Use **snapshots for low-frequency lifecycle boundaries**:

* Job registered.
* Job terminal.
* Workflow created.
* Workflow completed.
* Initial synchronization.
* Recovery after a sequence gap.

For example, `TransferProgressed` should not contain a complete job snapshot on every callback. Conversely, `JobCompleted` can reasonably contain a complete final job snapshot because it creates a useful consistency boundary.

This matches the concepts already present in the server coalescer: some information is latest-value state, while other information is an occurrence that must not be lost.

# Where snapshots should be created

They should be created **inside Core, at the point where the change is accepted into authoritative state**.

Not here:

```text
Core emits mutable Job
    -> Server maps it
    -> Persistence maps it differently
```

Prefer:

```text
Core updates authoritative state
    -> Core captures immutable values
    -> Core publishes one immutable change
        -> Live state projector
        -> SignalR projector
        -> Persistence projector
        -> Metrics
```

This gives all consumers the same factual description of what happened.

There is an important ordering issue: the state update and change creation need to occur under the same synchronization boundary. Otherwise, merely replacing mutable objects with immutable records does not guarantee that the snapshot represents a coherent transition.

Conceptually:

```csharp
CoreChange change;

lock (_stateGate)
{
    job.ApplyProgress(transferredBytes, totalBytes);
    change = CoreChangeFactory.TransferProgressed(job, transfer);
}

_events.Publish(change);
```

Do not hold the state lock while running external subscribers. Create the immutable change under the lock, then publish it after releasing the lock.

# Add revisions and a daemon event sequence now

This refactor is the right time to establish two distinct forms of ordering.

## Per-entity revision

Each job, workflow, search session, and transfer should have a monotonically increasing revision.

```csharp
public sealed record TransferProgressed(
    Guid TransferId,
    long Revision,
    long TransferredBytes,
    long TotalBytes,
    DateTimeOffset OccurredAt);
```

Consumers can reject stale changes:

```csharp
if (change.Revision <= stored.Revision)
{
    return;
}
```

## Daemon-wide sequence

A daemon-wide event sequence is useful for SignalR and projections:

```csharp
public abstract record CoreChange(
    long Sequence,
    DateTimeOffset OccurredAt);
```

It lets consumers detect:

* A lost event.
* An event-stream reconnection gap.
* Incorrect ordering.
* The need to request a fresh snapshot.

The two counters serve different purposes:

* `Sequence` orders the overall stream.
* `Revision` orders changes to a specific entity.

Neither needs to be globally durable in the first implementation. After restart, the server can expose a new daemon/session ID together with a restarted sequence. Persisted entity revisions should remain meaningful for database updates.

# Avoid turning domain changes into a persistence-shaped API

The records should describe the domain, not database operations.

Good:

```csharp
TransferStarted
TransferProgressed
TransferAttemptFailed
TransferCompleted
SearchResultsAdded
JobStateChanged
```

Too persistence-specific:

```csharp
UpsertTransferRow
InsertSearchResultRows
UpdateJobColumns
```

Core should not know whether consumers use SQLite, SignalR, logs, or nothing at all.

Likewise, avoid one generic event such as:

```csharp
EntityChanged(string entityType, Guid id, object payload)
```

That sacrifices compile-time safety, discoverability, versioning clarity, and exhaustiveness.

# This does not require eliminating mutable jobs yet

The live engine can continue using the current mutable `Job` hierarchy internally.

That gives an incremental architecture:

```text
Mutable Core implementation
        │
        ▼
Immutable public change boundary
        │
        ├── LiveDaemonStateStore
        ├── SignalR
        └── Persistence
```

Later, Core can move toward reducers and immutable internal state:

```text
Command
   -> reducer
   -> new immutable state
   -> immutable changes
```

The consumers would not need another major redesign because the public boundary would already be correct.

This is why I would treat the immutable event refactor as a prerequisite, but not require the full reducer refactor.

# A likely implementation sequence

## 1. Introduce immutable Core contracts

Create a namespace or folder such as:

```text
Sockseek.Core/
  Events/
    CoreChange.cs
    JobChanges.cs
    SearchChanges.cs
    TransferChanges.cs
  Snapshots/
    JobSnapshot.cs
    WorkflowSnapshot.cs
    SearchResultSnapshot.cs
    TransferSnapshot.cs
```

These should contain no references to EF, Server DTOs, or Soulseek.NET types.

## 2. Add a single mapping location inside Core

Create something like:

```csharp
internal static class CoreSnapshotFactory
{
    public static JobSnapshot Create(Job job);
    public static SearchResultSnapshot Create(
        Soulseek.SearchResponse response,
        Soulseek.File file);
}
```

This is an intermediate tool. After this boundary is stable, Core can stop retaining third-party objects in more places, but the mapping is centralized immediately. A single `CoreSnapshotFactory` is acceptable at first, but domain-specific snapshot factories may become clearer as uploads, sharing and chat arrive.

## 3. Emit immutable changes alongside the old events

Temporarily dual-publish while migrating consumers:

```csharp
_events.Publish(newJobChange);
_legacyEvents.RaiseJobUpdated(job);
```

Or adapt the old subscription API from the new stream. Keep this phase brief so the two systems do not diverge.

## 4. Move `EngineStateStore` to the new contracts

This is the best first consumer because it already maps live mutable state into records.

After conversion, verify that all existing API and SignalR behavior remains correct.

## 5. Move `ServerEventCoalescer`

Its keys and payloads should become immutable changes or server DTOs derived from immutable changes. No delayed closure should retain a mutable job.

## 6. Remove mutable public events

Add an architectural test or dependency test preventing events from containing known mutable domain types.

A basic reflection test could assert that every engine-change property is composed only of:

* Primitive/value types.
* Enums.
* Immutable records.
* Immutable collections.

## 7. Build persistence on the stable event stream

At that point, the persistence adapter becomes straightforward:

```csharp
switch (change)
{
    case TransferProgressed progress:
        _progressAccumulator.Set(progress.TransferId, progress);
        break;

    case SearchResultsAdded results:
        _searchResultBuffer.Add(results);
        break;

    case JobStateChanged state:
        await _durableMutations.Writer.WriteAsync(state, cancellationToken);
        break;
}
```

# One further question: events or state changes?

I would call them **changes**, not necessarily events, because some messages represent replaceable latest-value state.

This distinction matters for coalescing:

| Change                      |         Safe to coalesce? |
| --------------------------- | ------------------------: |
| Transfer progress           | Yes, latest revision wins |
| Job activity phase          |               Usually yes |
| Workflow aggregate counters |                       Yes |
| Search result added         |                        No |
| Transfer attempt failed     |                        No |
| Job completed               |                        No |
| Chat message received       |                        No |
| User joined a room          |                Usually no |

The contract should make this classification explicit, perhaps through separate marker interfaces:

```csharp
public interface ICoreChange;

public interface ICoalescibleCoreChange : ICoreChange
{
    string CoalescingKey { get; }
    long Revision { get; }
}

public interface IDurableCoreEvent : ICoreChange;
```

I would avoid making the marker system too elaborate initially, but documenting which changes are replaceable is important.

# The acceptance criterion

Before persistence work starts, I would require this invariant:

> Once a domain change has been published, its observable contents can never change, and it contains enough information for a consumer to process it without dereferencing mutable runtime state.

That does not mean every event needs every piece of information. A progress event can intentionally contain only progress. It means consumers should never have to do this:

```csharp
var job = supervisor.FindJob(change.JobId);
```

to discover what the event meant at the time it occurred.

Consumers may query live state to construct a current snapshot after detecting a gap, but ordinary event handling should be self-contained.

So: **Do this first**. It will improve the current SignalR/state architecture independently of persistence, eliminate a class of concurrency bugs, and give the persistence layer a stable domain-facing input. Keep it to an immutable boundary and ordering model; do not make persistence wait for a complete rewrite of Core’s mutable internals.
