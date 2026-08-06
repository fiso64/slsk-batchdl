# API and Client integration

> [!WARNING]
> The API is experimental and has not yet been tested much. Expect bugs and breaking changes.

The daemon exposes HTTP snapshots and durable history queries plus a SignalR hub
for ordered live-state deltas and compact activity.

## .NET clients

.NET consumers should use `Sockseek.Api.SockseekApiClient` for HTTP queries and
`Sockseek.Api.SockseekLiveClient` for live monitoring. The live client supports
either daemon-wide or workflow-scoped subscriptions per connection, handles
snapshot hydration and recovery, and exposes current state through
`DaemonClientStore`.

Sharing and upload consumers use the same clients and transfer reducer. The
HTTP client exposes share scan start/detail/cancel, live transfer paging,
live-first transfer detail, transfer cancellation, durable transfer history,
and attempt paging. Uploads do not have a separate client-side transfer model.

## Sharing and upload resources

The bounded sharing status and scan resources are:

```text
GET  /api/sharing
POST /api/sharing/scans
GET  /api/sharing/scans/{scanId}
POST /api/sharing/scans/{scanId}/cancel
```

`GET /api/sharing` exposes the compact `Disabled`, `Starting`, `Ready`, or
`Degraded` health summary, one optional reason, public aliases, aggregate catalog
counts, scan state, and blocked-peer counts. It never returns local share roots
or blacklist contents.

Transfers use the generic resources:

```text
GET  /api/transfers?direction=upload&...
GET  /api/transfers/live?direction=upload&state=queued&cursor=...&limit=...
GET  /api/transfers/{transferId}
POST /api/transfers/{transferId}/cancel
GET  /api/transfers/{transferId}/attempts
```

`/api/transfers` is cursor-paged durable history. `/api/transfers/live` pages
only the bounded runtime queue and is not merged with persistence. Its default
best-effort keyset cursor remains usable during churn and reports the observed
queue revision plus a `QueueChanged` hint. Cursors are bounded, validated query
state rather than authorization tokens; malformed values return `400`. Transfer
detail is live-first, with retained history and attempts as fallback.

All command failures use `ApiErrorDto.Code` for control flow. Scan and transfer
mutations carry the named `Sockseek.Operator` endpoint policy. The current
daemon has no authentication, so that policy is a pass-through trust-domain
seam—not access control. Anyone who can reach a non-loopback daemon can invoke
these commands; see [daemon security and operation](daemon.md).

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
