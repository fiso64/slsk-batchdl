# Server / Thin Client Plan

## Current Status

Implemented so far:
- `slsk-batchdl.Server` exists as a long-lived daemon host.
- An `EngineSupervisor` sits above `DownloadEngine` and owns the stable submission queue and engine lifetime.
- `EngineStateStore` tracks canonical jobs/workflows plus server-owned presentation hints.
- HTTP endpoints exist for:
  - server info/status
  - jobs list/detail/cancel
  - workflows list/detail/cancel
  - search raw results / projections
  - search follow-up actions (`retrieve-folder`, `downloads/song`, `downloads/album`)
- SignalR event streaming exists at `/api/events` with typed server envelopes.
- Local CLI now has a real in-process backend (`ICliBackend` + `LocalCliBackend`) and much of the CLI has been migrated onto the shared client-facing DTO/event model.
- `RetrieveFolderJob` is a real queued job path in Core and preserves retrieval outcome (`NewFilesFoundCount`).
- Search/album semantics were clarified so album network search terms and file-match terms are explicitly distinct.
- The server now owns runtime profile resolution through a `ProfileCatalog`.
  - `GET /api/profiles` exposes the server catalog.
  - `SubmissionOptionsDto` accepts profile names and client-supplied profile context values.
  - The server applies default, matching auto, and named profile download settings per workflow/job.
  - Client-only variables such as `interactive` are context values, not daemon behavior.
  - Local CLI separately resolves client-only profile effects such as `interactive`, `no-progress`, and `progress-json`.
- `sldl daemon` starts the server from the CLI launcher.
  - The daemon reuses CLI config/profile parsing for startup.
  - Daemon listen settings are separate from CLI presentation settings and Soulseek listen settings.
  - `--server-ip` / `--server-port` choose the HTTP/SignalR listen address.
- `RemoteCliBackend` exists as a first HTTP + SignalR adapter.
  - `--remote <url>` runs the CLI against an existing daemon.
  - The remote backend can submit jobs, list/read jobs and workflows, read track/album projections, start retrieve-folder and concrete song/album follow-up downloads, and cancel by job/workflow.
  - The rich terminal/JSON progress reporters consume the same server-shaped event envelope for local and remote backends.
  - Remote print-result modes can render completed daemon search payloads from job snapshots, so the daemon does the same extraction/search work and the thin CLI only renders the result.
  - Remote print-tracks can render planned song, album, aggregate, and album-aggregate download leaves from workflow/job snapshots, including nested extraction outputs from list files.
  - Remote normal downloads render the final completed/failed summary from workflow/job snapshots.
- A typed download-settings delta exists for remote submissions.
  - The daemon still owns defaults and profiles.
  - The thin client sends explicit command-line download/search operations as a DTO, and the server applies that delta after server-side profile resolution.
  - The operation model preserves intent for explicit default-value overrides and append-style settings such as `--on-complete "+ ..."`.

Still open / not finished yet:
- SignalR progress batching/coalescing is still pending.
- The server/state-store boundary still relies on retained live Core objects for some reads and search-session subscriptions.
- Remote CLI is not yet fully feature-complete compared to local CLI.
  - Interactive remote mode is intentionally blocked for now.
  - Print-result modes now have a completed-job snapshot path; a dedicated live/SearchJob print path may still be useful later if we want incremental result printing.
  - Remote cancellation works by job id/display id and current workflow, but the exact UX may still differ from local "cancel all" behavior.
  - Plain no-progress remote rendering can currently repeat some status lines because rich Core events are bridged directly; progress/event coalescing should address this deliberately.

Immediate next likely steps:
1. Continue remote CLI parity: cancellation/no-progress polish, then interactive mode.
2. Progress/event batching for live event streaming.
3. Remote interactive CLI on top of SearchJob + follow-up jobs.

## Core Model

- Add a long-lived `slsk-batchdl.Server` host around one persistent `DownloadEngine`.
  - In daemon mode, the engine stays alive until server shutdown.
  - `EngineCompleted` should therefore only occur when the daemon is stopping, not during normal operation.

- Keep **jobs** as the canonical resource.
  - `SearchJob` is a normal job kind, not a separate top-level resource family.
  - Search-specific operations should primarily hang off job endpoints.
  - If we ever add `/searches/...`, it should only be a convenience alias, not a separate identity space.

- Keep **workflows** as the logical grouping resource.
  - A workflow is the user-visible operation grouped by `WorkflowId`.
  - Multiple executable jobs can belong to one workflow.
  - This covers interactive search -> retrieve-folder -> album download -> retry flows cleanly.

- Keep the **canonical job graph** truthful over the wire.
  - Do not fake parent/child relationships just to simplify UI rendering.
  - `ExtractJob -> ResultJob`, `RetrieveFolderJob`, and other real relationships should remain factual in the API.
  - Any flattening for CLI/GUI should be expressed through presentation hints, not by mutating the graph model.

- Local CLI and thin CLI should share the same presentation/orchestration model.
  - Rendering logic should target a shared client-facing DTO/event model.
  - Use two backends:
    - local in-process backend that adapts `EngineEvents` into the shared client model
    - remote HTTP/SignalR backend that consumes the same model from the server
  - Goal: thin CLI should support the same user-facing features as local CLI.
  - Any feature gap between local and remote CLI should be kept explicit in this file until closed.

## API Shape

### Transport split

- Use HTTP for commands and snapshots.
- Use SignalR for live updates.
- Clients must always be able to resync from HTTP snapshots after reconnect.

### Canonical resources

- `jobs`
- `workflows`
- `profiles`
- `server`

### Main endpoints

- `GET /api/server/info`
- `GET /api/server/status`
- `GET /api/profiles`

- `POST /api/jobs`
  - submit any root job, including `ExtractJob`, `SearchJob`, `SongJob`, `AlbumJob`, `JobList`

- `GET /api/jobs`
  - support filtering by:
    - `state`
    - `kind`
    - `workflowId`
    - `rootOnly`
    - `includeHidden`

- `GET /api/jobs/{jobId}`
- `POST /api/jobs/{jobId}/cancel`

- `GET /api/workflows`
- `GET /api/workflows/{workflowId}`
- `POST /api/workflows/{workflowId}/cancel`

### Search-specific subroutes

Only valid for jobs of kind `search`, and kept under `/jobs/{jobId}/...` because searches are jobs:

- `GET /api/jobs/{jobId}/raw?afterSequence=...`
- `GET /api/jobs/{jobId}/projections/tracks`
- `GET /api/jobs/{jobId}/projections/albums`
- `GET /api/jobs/{jobId}/projections/aggregate-tracks`
- `GET /api/jobs/{jobId}/projections/aggregate-albums`
- `POST /api/jobs/{jobId}/retrieve-folder`
- `POST /api/jobs/{jobId}/downloads/song`
- `POST /api/jobs/{jobId}/downloads/album`

These follow-up routes should use the source search job as the continuity anchor.

- The route already identifies the originating `SearchJob`.
- The server can therefore reuse the job's effective settings/context and attach follow-up jobs to the same workflow cleanly.
- For generic `POST /api/jobs`, allow an optional `workflowId` when a client wants to attach a newly submitted root job to an existing workflow.

## Interactive Model

- Interactive search/selection remains client-driven:
  - start `SearchJob`
  - display live or completed projections
  - optionally run `RetrieveFolderJob`
  - submit concrete prefilled `SongJob` / `AlbumJob`

- `RetrieveFolderJob` stays a real job.
  - It should be visible and cancellable.
  - It should appear inside the owning search/album view, not as a root workflow item.
  - The job/result model should preserve retrieval outcome such as "new files found count" so local and remote clients do not have to infer it by diffing folder state.

- Interactive retry remains a client-side loop over normal jobs.
  - If a chosen album job fails or is cancelled, prompt again.
  - Exclude the failed folder by `username + folder path`.

## DTO Direction

- The wire contract should be typed and client-agnostic.
- Do not expose CLI tokens or callback-shaped protocol.
- Do not expose raw Core patch/runtime objects directly if they are not serialization-friendly.
- This client-facing DTO/event layer should be the common target for:
  - GUI
  - thin CLI
  - local CLI via an in-process adapter

### Submission DTOs

- `SubmitJobRequestDto`
  - `JobSpec`
  - `SubmissionOptions`

- `JobSpecDto` union:
  - `ExtractJobSpecDto`
  - `SearchJobSpecDto`
  - `SongJobSpecDto`
  - `AlbumJobSpecDto`
  - `JobListSpecDto`

- `SubmissionOptionsDto`
  - explicit profile names
  - profile context values
  - optional typed download-settings delta
  - optional raw server-side output path override

Profile context values are supplied by the client and evaluated by the server profile resolver.

- Example: a CLI client can submit `{ "interactive": true }` so a server-side profile with `profile-cond = interactive && album` can apply search/download settings.
- The server should not infer or implement interactive UI behavior from that flag.
- CLI-only profile effects stay client-side. Local CLI resolves them directly; thin CLI should do the same before submitting server jobs.

### Query / candidate DTOs

- `SongQueryDto`
- `AlbumQueryDto`
- `FileCandidateRefDto`
- `AlbumFolderRefDto`
- `FileCandidateDto`
- `AlbumFolderDto`

### Search DTOs

- `SearchRawResultDto`
- `SearchProjectionSnapshotDto<T>`

### Job / workflow DTOs

- `WorkflowSummaryDto`
- `JobSummaryDto`
- `JobDetailDto`

Use GUIDs as the real API identifiers.

- expose `Job.Id` and `WorkflowId`
- expose `DisplayId` only as display metadata, not as primary identity

### Presentation hint DTOs

Alongside canonical graph fields, include server-computed presentation hints so clients do not all have to reinvent the same flattening logic.

Examples:

- `IsHiddenFromRoot`
- `VisualParentJobId`
- `VisualOrder`
- `ReplaceWithJobId`

The canonical graph remains factual even when these hints recommend a flattened/root-friendly view.

## Presentation Projection

- The server should maintain both:
  - canonical job graph facts
  - presentation-oriented visibility/grouping hints

- Canonical facts come from Core events:
  - `JobRegistered`
  - `JobResultCreated`
  - `RetrieveFolderJobStarted`
  - state/progress/completion events

- Presentation rules should be server-owned, not Core-owned.
  - Successful `ExtractJob`s can be hidden from the default root list after their result job appears.
  - Extracted result jobs can appear as sibling/root-visible items in the visual model while still preserving the canonical relationship.
  - `RetrieveFolderJob`s should stay nested under their owning album/search context in the visual model.

This keeps Core factual while still giving GUI/thin CLI the shape they actually want.

## Live Event Stream

- SignalR should publish a typed server event envelope, not raw `EngineEvents`.
- Local CLI should not render directly from `EngineEvents` long-term either.
  - Instead, local mode should adapt `EngineEvents` into the same higher-level client event/snapshot model used remotely.
- Event kinds should include:
  - workflow upserted/completed
  - job registered/updated/completed
  - job result created
  - search raw result added
  - search completed
  - server status updated

- The server should explicitly coalesce/throttle noisy progress events before broadcast.
  - Maintain an in-memory progress/update accumulator.
  - Flush batched progress updates on a timer rather than broadcasting every raw engine event.
  - This is especially important for download progress.
  - A first low-frequency job/workflow event stream is fine before this, but progress-heavy events should not be wired directly without batching.

- Search projections should be revision-aware.
  - HTTP projection endpoints should return a full snapshot plus revision metadata.
  - Clients decide whether and when to request projections at all.
  - The server should not proactively compute or broadcast projections that no client has asked for.
  - Clients should avoid re-fetching unchanged projections.
  - Projection deltas are a likely later optimization, but not required for the first implementation slice.

## Important Boundaries

- `SearchJob` is the shared primitive for GUI, local CLI, and thin CLI.
- Auto/non-interactive album mode remains in-engine.
- Interactive mode remains client-driven and explicit.
- Core events remain facts, not UI protocol.
- Server snapshots/events form the actual client protocol.
- Raw server-side output paths should remain allowed by default.
  - Optional browse/restriction APIs are host-policy conveniences, not a reason to forbid explicit paths.
  - A personal daemon can allow arbitrary paths; a shared daemon may later restrict them to configured roots.

## Important Open Issue

- Daemon resilience must be designed explicitly.
  - A per-job infrastructure failure inside `DownloadEngine.RunAsync` must not tear down the whole server host.
  - Current Core behavior can still let login/setup failures escape the long-lived engine loop and stop the daemon.
  - Short term, the server should reject clearly unsupported submissions early (for example login-requiring jobs when no login is configured).
  - Longer term, the daemon needs a more robust strategy so job failures and host lifetime are properly isolated.

### Preferred daemon resilience direction

- Add an engine supervisor layer above `DownloadEngine`.
  - The hosted service should supervise the engine instead of simply awaiting one `RunAsync` call.
  - The supervisor owns:
    - the stable submission queue
    - the current engine instance
    - engine recreation/restart logic
    - mapping engine failures into workflow/job failure state

- Distinguish clearly between:
  - job/workflow failure
  - engine-instance failure
  - host shutdown

- If an engine instance fails at runtime:
  - keep the daemon process alive
  - mark affected in-flight jobs/workflows as infrastructure-failed
  - create a fresh engine instance
  - continue accepting new submissions

- Do not try to transparently resume interrupted in-flight jobs in the first implementation.
  - Failing them explicitly is simpler and more honest.
  - Clients can inspect the state and resubmit if desired.

- Avoid relying on host-level background-service exception behavior as the main resilience strategy.
  - Catch and recover inside the supervisor loop.
  - Host shutdown should be reserved for truly fatal daemon startup/configuration failures.

- The server state-store boundary should stay under review.
  - Right now the server can still rely on live Core job objects for some on-demand reads such as search projections.
  - This is acceptable for the early server slices, but it is not the final separation-of-concerns shape.
  - The same currently applies to search revision notifications: the server can subscribe directly to retained `SearchJob.Session` objects to emit `search.updated`.
  - Longer term, decide more explicitly which responsibilities belong to:
    - retained live Core objects
    - server-owned factual snapshots
    - server-owned projection/cache state
  - The goal is to avoid accidental over-coupling between the daemon API layer and the in-process Core object graph.

- Server-side profile loading has a first startup path, but should stay under review.
  - Runtime profile application is now server-owned once a `ProfileCatalog` exists.
  - `sldl daemon` currently populates that catalog through the CLI launcher's config parser.
  - Longer term, decide whether config/profile parsing moves into Core/shared infrastructure or remains launcher-owned.

- Daemon/server logging needs an explicit pass before this is considered polished.
  - Think through local CLI, thin CLI, daemon console/stdout, service managers such as systemd, optional log files, and framework/server logs together.

- Keep protocol expectations explicit around local/mock file identity.
  - In `MockFilesDir` / local-files mode, folder paths can currently surface as absolute local paths rather than remote-style relative paths.
  - This is acceptable for internal tests, but the client-facing protocol should stay deliberate about what path identity means in local vs remote modes.
  - Do not accidentally let local test fixtures define the public API shape.
  - In particular, Soulseek search/file identities are backslash-separated regardless of host OS.
  - Tests and client code must not use `System.IO.Path` semantics on Soulseek-side identity strings unless they normalize them deliberately.

## First Implementation Slice

1. Add `slsk-batchdl.Server` as a long-lived daemon host.
2. Add an in-memory state store that subscribes to `EngineEvents`.
3. Add HTTP commands:
   - submit job
   - cancel job
   - cancel workflow
   - retrieve folder from search job
   - start concrete song/album download from search selection
4. Add HTTP snapshots:
   - jobs list/detail
   - workflows list/detail
   - search raw/projection endpoints
5. Add SignalR live updates.
6. Build a shared CLI backend abstraction so local and remote CLI use the same rendering/orchestration layer.
   - local backend: in-process adapter from `EngineEvents` / engine state to client DTOs
   - remote backend: HTTP + SignalR adapter to the same DTOs/events
   - It is fine to migrate this incrementally.
   - Client-backend convenience helpers are acceptable when they compose real protocol/job primitives rather than inventing a second hidden execution path (for example, "retrieve folder and wait" on top of a real `RetrieveFolderJob`).
   - Migrate JSON progress mode first if that gives a cleaner proof of the shared event surface, but the end state should also move the rich terminal progress renderer onto the same backend events.
   - The shared client-facing DTO model needs enough resolved-file metadata to support faithful rendering, not just ids and names. In particular, album/track UI may need resolved file attributes and response metadata rather than reconstructing fake placeholder files client-side.
