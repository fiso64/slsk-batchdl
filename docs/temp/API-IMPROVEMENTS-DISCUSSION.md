# API improvements discussion

The API work should be built around five decisions:

1. **HTTP snapshots establish client state; SignalR carries compact live
   changes.**
2. **Daemon-wide and workflow-scoped monitoring are explicit stream scopes with
   recoverable positions.**
3. **State deltas and activity events are different contracts, even when they
   share one network batch.**
4. **One Sockseek-owned client store powers the local CLI, remote CLI, and
   future GUI.**
5. **Live monitoring is not an unbounded export of persisted history.**

The central rule is:

> A client must be able to reconstruct every displayed state value from an HTTP
> snapshot plus subsequent ordered state deltas. Activity events may enrich the
> display or logs, but must never be required to make the state correct.

This branch may make breaking API changes for v4. It should replace the current
event protocol rather than indefinitely publishing old and new representations
in parallel.

---

## What exists today

The current implementation already has useful pieces:

- `EngineStateStore` projects mutable engine state into Sockseek-owned DTOs.
- HTTP exposes job and workflow snapshots plus paginated durable history.
- `ServerEventCoalescer` bounds update frequency and keeps only the latest
  progress value per transfer.
- workflow batches have workflow-local sequences.
- `WorkflowClientStore` can apply workflow snapshots and batches and detect a
  sequence gap.
- local and remote CLI backends expose similar monitoring APIs.
- Core job and transfer snapshots carry revisions that can protect clients from
  stale entity updates.

The current contract nevertheless has several problems:

- routine changes repeatedly send complete `JobSummaryDto` values;
- many events classified as activity actually carry durable UI state;
- activity payloads often repeat a complete job, query, candidate, folder, or
  track collection;
- `WorkflowUpdateBatchDto` nests full `ServerEventEnvelopeDto` values even
  though the batch already owns scope, sequence, and timing metadata;
- progress is keyed by job in the client store even though transfers have their
  own identities;
- `SubscribeAll` receives workflow batches but does not define a daemon stream
  or a daemon-wide recovery position;
- the HTTP workflow snapshot does not include active transfer/search state or a
  stream position;
- after a gap, the remote backend applies the gapped batch before asynchronously
  replacing it with an HTTP snapshot;
- SignalR connection and recovery behavior lives in the CLI rather than in the
  reusable API client layer;
- the store name and public queries imply workflow-only use even though it
  already accumulates multiple workflows.

These are protocol-shape problems. Adding more event types to the existing
envelope would make them harder to fix later.

---

# State, activity, and history

## Replicated state

Replicated state is everything a normal monitor or GUI may show as the current
truth:

- daemon/Soulseek connection state useful to clients;
- workflow summaries;
- job summaries, relationships, lifecycle, outcome, discovery counts, and
  available actions;
- current search revision, count, and completion state;
- active transfer identity, source, state, progress, and terminal transition.

Every replicated field must exist in a snapshot and be maintainable through a
state delta.

## Ephemeral activity

Activity is an edge that is useful to print or append to a log but is not
current state:

- job and workflow messages;
- diagnostics with additional exception detail;
- transfer-attempt failure messages;
- concise extraction/listing messages that the plain CLI still needs.

Activity delivery is best-effort:

- it is not replayed after reconnect;
- it is not required to recover client state;
- losing it during a sequence gap is acceptable;
- payloads contain IDs and only the fields needed to format the message.

If a GUI needs to show a value after reconnect, that value is state, not
activity. For example, search throttling cannot exist only as
`search.rate-limited` and `search.resumed` edges if it is meant to drive a
persistent status indicator.

## Persisted history

The existing paginated jobs, workflows, searches, and transfers APIs remain the
way to browse retained history.

“All daemon jobs/workflows” therefore means both:

- the daemon stream reports every live change for every workflow and job, without
  requiring individual workflow subscriptions; and
- the client can page through and display every retained workflow and job,
  merging requested history pages into its store.

It does not mean that startup must load the complete retained database into
memory.

A daemon replication snapshot should contain all active workflows and the jobs
needed to render them, plus active search and transfer state. It should not load
every retained terminal row from SQLite. A GUI can hydrate recent or older
history through the existing paginated endpoints and merge those rows into a
separate history partition in the client store.

This distinction keeps initial load and recovery bounded:

```text
live snapshot + SignalR deltas    current daemon monitor
paginated HTTP queries            retained historical data
```

When a live workflow becomes terminal, its terminal delta is delivered normally.
After a reconnect or daemon restart, it may reappear through a history query
rather than the active daemon snapshot.

---

# Proposed protocol

The names below are illustrative, but the semantics should be fixed before the
DTO details.

## Stream scope and position

Use an API-owned position:

```csharp
public sealed record StateStreamScopeDto(
    StateStreamScopeKind Kind, // Daemon or Workflow
    Guid? WorkflowId);

public sealed record StateStreamPositionDto(
    Guid Epoch,
    long Sequence);
```

`Epoch` is a random identifier created for every daemon process. It must not
depend on persistence being enabled. A different epoch means the client must
replace its live state from a new snapshot.

Maintain independent sequence spaces:

- one daemon sequence for `SubscribeAll`;
- one workflow sequence per workflow for `SubscribeWorkflow`.

A workflow subscriber must not report gaps merely because unrelated workflows
changed. Do not expose `CoreChange.Sequence` directly as the public stream
cursor; the API stream also contains derived/coalesced changes and has different
scope semantics.

Because one batch may coalesce several source changes, a batch should carry both
the previous and resulting positions:

```csharp
public sealed record StateUpdateBatchDto(
    StateStreamScopeDto Scope,
    Guid Epoch,
    long PreviousSequence,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    StateDeltaDto State,
    IReadOnlyList<ActivityEventDto> Activity);
```

Activity items retain only the metadata needed to order, filter, and format the
edge:

```csharp
public sealed record ActivityEventDto(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Type,
    Guid? WorkflowId,
    Guid? JobId,
    Guid? TransferId,
    ActivityPayloadDto Payload);
```

Their per-item sequence lets a client suppress already-observed activity when a
buffered batch overlaps the HTTP snapshot position. It does not turn activity
into replayable state.

This is preferable to requiring `Sequence == lastSequence + 1`:

- `Sequence <= current` is stale and can be ignored;
- `PreviousSequence <= current < Sequence` overlaps the current state and can be
  safely applied;
- `PreviousSequence > current` is a real gap and requires snapshot recovery.

Sequence allocation, projection updates, and snapshot capture must share one
ordering boundary. Otherwise a snapshot can claim position N while containing
state from after N.

## Snapshot endpoints

Add replication-specific endpoints instead of overloading ordinary detail
queries:

```text
GET /api/daemon/snapshot
GET /api/workflows/{workflowId}/snapshot
```

Both snapshots carry their scope and `StateStreamPositionDto`.

The daemon snapshot contains:

- small daemon connection/runtime state;
- every active workflow;
- every job belonging to those active workflows, including already-terminal
  children needed to render the workflow;
- active transfers and their latest progress;
- active search metadata/revisions.

The workflow snapshot contains the complete requested workflow even if it has
just become terminal. This is the recovery path for a narrow CLI subscription.

Use flat entity collections rather than duplicating jobs inside nested workflow
objects:

```csharp
public sealed record StateSnapshotDto(
    StateStreamScopeDto Scope,
    StateStreamPositionDto Position,
    DateTimeOffset CapturedAtUtc,
    DaemonStateDto? Daemon,
    IReadOnlyList<WorkflowStateDto> Workflows,
    IReadOnlyList<JobStateDto> Jobs,
    IReadOnlyList<SearchStateDto> Searches,
    IReadOnlyList<TransferStateDto> Transfers);
```

Arrays are easier for generated clients than GUID-keyed JSON dictionaries. The
client store can index them after deserialization.

## Snapshot handoff

Startup and recovery should use the same race-safe procedure:

1. connect to SignalR and subscribe to the desired scope;
2. buffer incoming batches without rendering them;
3. fetch the matching HTTP snapshot;
4. replace the live partition of the store with that snapshot;
5. discard buffered batches ending at or before the snapshot position;
6. apply overlapping/newer batches in order;
7. begin normal live rendering.

After this handoff, SignalR is the normal update loop. Clients must not
periodically poll job, workflow, transfer, or search snapshots to simulate live
monitoring. HTTP is used for initial hydration, gap/reconnect recovery, explicit
detail queries, and paginated history.

On an epoch change or real gap, mark the affected scope stale and stop applying
its state deltas until recovery completes. The current behavior—apply a gapped
batch, render potentially incomplete state, and repair later—should not remain
the GUI contract.

The store itself should perform no HTTP calls. A reusable live-client
coordinator should own subscription, buffering, snapshot fetches, and retries.

---

# Compact state deltas

Do not use RFC 6902/JSON Patch or a `Dictionary<string, object>`. Both weaken the
typed .NET/OpenAPI contract and make validation difficult.

Also avoid treating `null` as both “field absent” and “clear this nullable
field.” Prefer replaceable cohesive components:

```csharp
public sealed record JobDeltaDto(
    Guid JobId,
    long Revision,
    JobStateDto? Added,
    JobLifecycleFieldsDto? Lifecycle,
    JobDiscoveryFieldsDto? Discovery,
    JobRelationshipFieldsDto? Relationships);
```

If `Lifecycle` is present, it replaces that complete component, including its
nullable failure and timing fields. `Added` is the complete row for a newly
observed job. This remains compact without inventing ambiguous optional-value
serialization.

Use the same pattern for other entities:

- workflow summaries are already small and may be sent as complete replacements;
- search state is small and may be latest-value coalesced by job ID;
- a transfer add carries stable identity/source metadata;
- transfer state and transfer progress are separate replaceable components;
- progress is keyed by `TransferId`, never just `JobId`;
- explicit removal ID lists remove entities that leave the replicated live set.

Every entity delta carries a monotonic entity revision where the Core model has
one. The client ignores an entity delta older than or equal to the stored
revision. Workflow revisions may be owned by the API projection because
workflows are derived aggregates rather than Core entities.

## Current event migration

The current events should move approximately as follows:

| Current event | v4 representation |
| --- | --- |
| `job.upserted` | job add or component delta |
| `workflow.upserted` | small workflow replacement |
| `search.updated` | search state delta |
| `download.started` | transfer add |
| `download.progress` | transfer progress delta |
| `download.state-changed` | transfer state delta |
| `job.started`, `job.activity-changed`, `song.state-changed` | job lifecycle delta |
| album start/state and folder-retrieving events | job/transfer state deltas |
| job/workflow messages | compact activity |
| diagnostic and attempt failures | compact activity plus state delta when state changed |
| search rate limit/resume | daemon state delta if displayed persistently |
| track-batch output | compact counts/activity; details remain in snapshots or result endpoints |

Do not send the same large payload once as state and again as activity. A
terminal job delta already communicates terminal state; a separate activity item
is justified only when it adds useful log text.

## Coalescing

`EngineStateStore` should compare previous and current outward state and produce
typed component changes. The network coalescer then:

- merges job components by job ID;
- keeps the latest workflow and search state;
- keeps the latest transfer state/progress by transfer ID;
- folds later patches into an unflushed entity add;
- preserves activity order;
- places state before activity in each batch;
- flushes terminal transitions promptly;
- never turns a terminal entity back into an earlier state.

The coalescer schedules and compresses delivery. It must not become the only
owner of current state; snapshot capture needs a complete live projection even
while a batch is pending.

---

# Subscription semantics

`SubscribeAll` should mean:

> Subscribe this connection to the daemon-scoped stream containing every live
> state change and activity edge visible to daemon-wide clients.

`SubscribeWorkflow(workflowId)` should mean:

> Subscribe this connection to the workflow-scoped stream for that workflow,
> with its own recoverable sequence.

A connection operates in one mode:

- daemon-wide, with one `SubscribeAll` scope; or
- workflow-scoped, with one or more explicit workflow subscriptions.

Mixing modes on one connection should be rejected rather than silently
delivering duplicate representations of the same change. Global activity that
has no workflow belongs only to the daemon stream.

Both local and remote backends must implement these exact semantics. A local
subscription may bypass SignalR transport, but it must feed the same snapshot
and delta DTOs through the same client reducer.

The CLI must expose this as a user-facing daemon monitor, not only as a backend
method. The proposed shape is:

```text
sockseek monitor --remote <url>
```

It starts no workflow, invokes `SubscribeAll`, hydrates the daemon snapshot, and
renders daemon-wide activity until interrupted. Narrow command execution may
continue using `SubscribeWorkflow`.

---

# Client shape

`WorkflowClientStore` is becoming a daemon state store. Rename it to
`DaemonClientStore` in v4 rather than making a workflow-named type own daemon
status, transfers, and all workflows indefinitely.

The store should expose immutable snapshots of its indexes:

```text
GetWorkflows()
GetWorkflow(workflowId)
GetJobs()
GetJob(jobId)
GetWorkflowJobs(workflowId)
GetJobsGroupedByWorkflow()
GetActiveJobs()
GetTerminalJobs()
GetTransfers()
GetTransfer(transferId)
GetJobTransfers(jobId)
GetSearchState(jobId)
```

“Grouped jobs” means grouping all hydrated jobs by `WorkflowId`. Each group
contains its `WorkflowSummaryDto` when known and jobs ordered by display ID.
Parent/child execution structure remains available through `ParentJobId` or a
separate tree helper; it is not a second meaning hidden behind the same
`GetGroupedJobs` name.

“All” means all rows currently hydrated in the store, not all rows retained in
the database. The live client must also expose paginated history-loading methods
so callers can hydrate and display the complete retained collection rather than
being limited to the initial active snapshot.

The store should maintain separate partitions:

- replicated live state, replaced by daemon/workflow snapshots;
- paged history explicitly hydrated by the caller;
- ephemeral activity delivered to observers but not treated as state.

Applying a live recovery snapshot must not discard unrelated history pages the
GUI already loaded.

Keep `SockseekApiClient` as the stateless HTTP client. Add a reusable live
monitoring client in `Sockseek.Api` that owns SignalR, subscriptions, buffering,
reconnect recovery, and `DaemonClientStore`. `RemoteCliBackend` should consume
that client instead of owning a private SignalR implementation.

Add an explicit live-protocol version to server identity and reject incompatible
clients with a clear error. Do not infer compatibility only from the application
version string.

The old `SnapshotInvalidation` flag and machine-readable event catalog should be
removed or narrowed to activity discovery once state changes are typed deltas.
Normal state handling should not depend on string event names.

---

# Implementation sequence

## 1. Establish the state model

- define daemon/workflow scope and stream-position DTOs;
- define complete workflow, job, search, and active-transfer state rows;
- decide the bounded live snapshot membership precisely;
- add API-owned workflow revisions;
- add protocol versioning.

## 2. Add coherent snapshots

- make the server projection capture daemon and workflow snapshots with stream
  positions;
- add the two snapshot endpoints and .NET HTTP client methods;
- cover the subscribe-before-snapshot race deterministically.

## 3. Replace summary-heavy batches

- add typed job, workflow, search, and transfer deltas;
- refactor `EngineStateStore` to emit previous/current component changes;
- refactor the coalescer to merge components and position ranges;
- migrate state-like activity events;
- remove nested event envelopes from state batches.

## 4. Build the shared live client

- rename and expand the store;
- add daemon-wide queries and transfer/search state;
- define workflow grouping and paginated history hydration;
- add stale-scope behavior and snapshot replacement;
- move SignalR/reconnect orchestration out of `RemoteCliBackend`.

## 5. Prove local/remote parity and remove v3 contracts

- route local monitoring through the same reducer;
- add `SubscribeAll` parity tests;
- add the user-facing `monitor --remote` daemon-wide CLI flow;
- update plain CLI formatting to consume compact activity;
- delete obsolete DTOs, converters, catalog entries, and compatibility aliases;
- regenerate OpenAPI;
- update `docs/api.md` only where the implemented public contract needs
  explanation.

Do not update `README.md` as part of this branch.

---

# Completion conditions

## Architecture

- [ ] **ARCH-01** Correct client state is reconstructable without activity
  replay.
- [ ] **ARCH-02** Snapshot capture and stream positions share a coherent ordering
  boundary.
- [ ] **ARCH-03** Daemon and workflow streams have independent, documented
  positions.
- [ ] **ARCH-04** Active transfer state is keyed by `TransferId`.
- [ ] **ARCH-05** Persisted history is not loaded wholesale into a replication
  snapshot.
- [ ] **ARCH-06** No mutable Core or Soulseek.NET object crosses the public API
  boundary.
- [ ] **ARCH-07** SignalR is the normal live-update loop; HTTP snapshots are used
  only for hydration, recovery, explicit queries, and paginated history.

## Protocol

- [ ] **PROTO-01** Snapshot DTOs contain every field needed to render current
  state.
- [ ] **PROTO-02** Batches contain typed compact deltas and no nested
  `ServerEventEnvelopeDto`.
- [ ] **PROTO-03** Nullable fields can be explicitly cleared without ambiguity.
- [ ] **PROTO-04** New entities, component replacement, terminal state, and
  removal are all representable.
- [ ] **PROTO-05** Epoch changes, stale batches, overlapping batches, and real
  gaps have deterministic behavior.
- [ ] **PROTO-06** `SubscribeAll` and `SubscribeWorkflow` have non-overlapping
  documented semantics.
- [ ] **PROTO-07** The server exposes and enforces a live-protocol version.

## Client

- [ ] **CLIENT-01** One client reducer is used by local CLI, remote CLI, and
  future GUI code.
- [ ] **CLIENT-02** The store exposes daemon-wide workflow, job, transfer, and
  search views.
- [ ] **CLIENT-03** Grouped jobs have one documented meaning: jobs grouped by
  workflow.
- [ ] **CLIENT-04** Every retained workflow and job can be paged into the store
  and displayed without requiring it in the startup snapshot.
- [ ] **CLIENT-05** The CLI exposes a daemon-wide monitor that uses
  `SubscribeAll`.
- [ ] **CLIENT-06** A gapped scope is not rendered as current before recovery.
- [ ] **CLIENT-07** Reconnect uses subscribe, snapshot, buffered-delta handoff,
  and then live delivery.
- [ ] **CLIENT-08** Replacing live state preserves independently hydrated
  history.
- [ ] **CLIENT-09** SignalR and recovery logic is reusable outside the CLI
  project.

## Tests and evidence

- [ ] **TEST-01** Applying a snapshot and every subsequent delta produces the
  same state as the server projection.
- [ ] **TEST-02** Daemon-wide local and remote monitoring produce equivalent
  client-store state.
- [ ] **TEST-03** Workflow-scoped local and remote monitoring remain equivalent.
- [ ] **TEST-04** The user-facing CLI monitor hydrates daemon state and observes
  jobs from multiple workflows without polling.
- [ ] **TEST-05** Snapshot races, reconnects, sequence gaps, duplicate batches,
  stale batches, and epoch changes use deterministic synchronization tests.
- [ ] **TEST-06** Coalescing cannot lose an entity add, terminal transition,
  relationship change, or final transfer progress.
- [ ] **TEST-07** Activity loss does not affect reconstructed state.
- [ ] **TEST-08** Event traffic profiling demonstrates a material reduction in
  serialized bytes for large workflows and active transfers.
- [ ] **TEST-09** OpenAPI, JSON round-trip tests, and checked-in examples match
  the final DTOs.

## Out of scope

- the web UI itself;
- API authentication and web login;
- sharing, uploads, chat, and user browsing;
- durable activity/event replay;
- persistence schema redesign;
- README presentation changes;
- a general-purpose event-sourcing or JSON-patch framework.

The API-improvements branch is complete when a daemon-wide client can connect,
hydrate bounded current state, follow compact ordered changes, detect and recover
from lost continuity, page and display all retained workflows/jobs, and render
the same result through local and remote backends without relying on activity
events or periodic HTTP polling.
