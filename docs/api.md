# API and Client integration

> [!WARNING]
> The API is experimental and has not yet been tested much. Expect bugs and breaking changes.

The daemon exposes HTTP snapshots and durable history queries plus a SignalR hub
for ordered live-state deltas and compact activity.

## .NET clients

.NET consumers should use `Sockseek.Api.SockseekApiClient` for stateless HTTP
queries and `Sockseek.Api.SockseekLiveClient` for live monitoring. The live
client performs the subscribe/snapshot/buffered-delta handoff, detects stream
gaps and epoch changes, recovers through HTTP snapshots, and exposes its current
state through `DaemonClientStore`.

Live protocol version 4 has two non-overlapping subscription modes:

- `SubscribeAll` produces one daemon-wide stream and hydrates from
  `GET /api/daemon/snapshot`.
- `SubscribeWorkflow(workflowId)` produces independently positioned workflow
  streams and hydrates from `GET /api/workflows/{workflowId}/snapshot`.

A connection cannot mix daemon and workflow subscriptions. Each stream uses an
epoch and sequence position. State must be rendered from `StateSnapshotDto`
plus `StateUpdateBatchDto.State`; `ActivityEventDto` is best-effort and is not
required to reconstruct correct current state. SignalR is the normal update
loop. HTTP snapshots are used for initial hydration and recovery, while retained
history remains available through paginated HTTP endpoints.

Renderers that need several entity types should use
`DaemonClientStore.GetLiveStateView()`. It returns workflows, jobs, searches,
transfers, and daemon state from one store lock, and deliberately excludes
independently hydrated history. Replacing the rendered current-state model from
that view keeps rows and status counts correct after snapshot recovery.

The checked-in
[`live-state-update.json`](examples/live-state-update.json) document shows the
wire shape of a compact workflow update batch. Server contract tests deserialize
this example with the same source-generated JSON metadata used by clients.

## OpenAPI

OpenAPI spec is in `docs/openapi.json` (auto-generated during build). The same document is also served by a running daemon at `GET /api/openapi.json`.

If you are not using .NET, use the OpenAPI document with your viewer or client generator of choice.

## Local mock daemon

For client development, start from mock files instead of a real Soulseek account:

```bash
python scripts/create_mock_music_library.py -o /tmp/sockseek-fixture

sockseek daemon \
  --mock-files-dir /tmp/sockseek-fixture/mock-library \
  --mock-files-no-read-tags \
  --mock-files-slow \
  --server-port 5030 \
  -o /tmp/sockseek-out
```

## Source map

- `Sockseek.Api/Client/SockseekApiClient.cs` — .NET client wrapper and the most convenient reference for supported client flows.
- `Sockseek.Api/Client/SockseekLiveClient.cs` — reusable SignalR subscription,
  buffering, and recovery coordinator.
- `Sockseek.Api/Client/DaemonClientStore.cs` — shared reducer and daemon/workflow
  query store.
- `Sockseek.Api/Contracts/` — request/response DTOs shared by the server, CLI, and .NET clients.
- `Sockseek.Api/Contracts/LiveState.cs` — versioned snapshot, typed delta,
  transfer, stream-position, and compact activity DTOs.
- `Sockseek.Server/ServerHost.cs` — endpoint registration and OpenAPI metadata.
- `Sockseek.Cli/Services/RemoteCliBackend.cs` — real remote client usage, including SignalR subscription behavior.
- `Sockseek.Cli.Tests/RemoteCliBackendTests.cs` — executable examples of remote API flows.
