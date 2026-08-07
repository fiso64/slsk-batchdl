# Sockseek v4 chats and notifications

**Status:** Implemented; independent-client qualification pending

**Target:** v4.0

**Sockseek source reviewed:**
`1931dd91eed87c87a60e5e61f1c2c1c435fff437`

**slskd source reviewed:**
`43a4ff64169df7f2304aa2348907fc5a9442474c` (2026-08-04)

**Soulseek.NET source reviewed:**
`52fc3e4267114d8cd9492cb4d7438b3eca0267bf` (package 10.0.2)

This is the implementation plan for the next Soulseek feature in
[`TODO.md`](../../TODO.md): private messages, public chat rooms, and a
notification API. It follows the product and maintainability rules established
by [`sharing-uploads-design.md`](archive/sharing-uploads-design.md).

Sockseek targets self-hosted and homeserver use. Soulseek users and message
content are untrusted. The daemon operator, daemon account, configuration, and
local database are inside the product trust boundary. The design bounds remote
input and preserves chat data without creating a general messaging platform or
second live-state implementation.

Normative terms such as **MUST**, **SHOULD**, and **MAY** use their RFC 2119
meanings.

---

## 1. Decision rules

The sharing design's rules remain authoritative. For chat they imply:

1. Use the existing daemon Soulseek session, persistence database, live-state
   protocol, API clients, and operator policy seam. Do not create parallel
   clients or stores.
2. Keep the Soulseek private-message acknowledgement separate from the user's
   read/unread state. They have different meanings.
3. Persist an accepted private message before acknowledging it to the Soulseek
   server. A replay after a crash MUST be idempotent.
4. Keep network callbacks fast and exception-contained. Durable work runs on a
   bounded coordinator, not on Soulseek.NET's message-reading callback.
5. Replicate small summaries and scoped active state. Message history,
   conversations, room rosters, and notifications remain paginated resources.
6. Provide a durable notification API that daemon clients can consume without
   coupling notification creation to protocol callbacks.
7. Add public configuration only for operator policy. Queue sizes, tail sizes,
   page limits, cache intervals, and worker counts remain internal constants.
8. Treat message bodies as plain text. Do not interpret remote HTML or Markdown.
9. Prefer a single understandable behavior. Chat requires the daemon persistence
   service instead of maintaining separate durable and volatile implementations.

## 2. Scope

### 2.1 Included in v4

- Receive, deduplicate, persist, display, send, archive, mark read, and delete
  private-message conversations.
- Delay Soulseek protocol acknowledgement until an inbound private message is
  durable; acknowledge blocked or invalid messages after intentional discard.
- List available public, private, and owned rooms; join, leave, reconnect, and
  optionally auto-join configured rooms.
- Persist room messages received while Sockseek is joined, send room messages,
  and maintain the current in-memory room roster.
- Match slskd's private-room surface: accept private-room invitations, expose
  private/owned/moderated classifications and joined-room owner/operator
  metadata, and allow adding a member to a joined private room.
- Track per-conversation and per-room read watermarks and unread counts.
- Create durable notifications for new private messages and whole-token mentions
  of the current username in rooms.
- Page notifications, mark one or many read, and publish newly created
  notification records plus compact read-summary changes to daemon subscribers.
- Add conversation and room live scopes using the existing snapshot, sequence,
  gap-recovery, and client-store machinery.
- Reuse exact blocked usernames for inbound and outbound private messages and
  inbound room messages.
- Add non-interactive remote CLI commands for chats, rooms, and notifications.
- Add bounded retention, metrics, owner-only storage, API documentation, and
  generated OpenAPI.

### 2.2 Not included

- Private-room administration beyond slskd parity: explicitly creating a
  private room, removing members, granting or revoking moderator status, and
  dropping private-room ownership or membership.
- Room tickers.
- Soulseek's separate all-room public-chat feed exposed by
  `StartPublicChatAsync`.
- The Web UI itself.
- API authentication and web login. The existing authorization seam is retained
  so the later authentication work does not require endpoint redesign.

This document names only chat capabilities represented by the pinned
Soulseek.NET API and Soulseek protocol. Local persistence, read watermarks,
history management, and the notification API are Sockseek application behavior
required to make the daemon usable; they are not presented as remote protocol
features.

## 3. Source findings and design consequences

### 3.1 Soulseek.NET 10.0.2

The pinned library establishes these contracts:

- `AutoAcknowledgePrivateMessages` defaults to `true`.
- For a private message, the library raises `PrivateMessageReceived` before it
  performs its optional automatic acknowledgement.
- The event supplies a server message ID, server timestamp, username, text, and
  replay flag. `AcknowledgePrivateMessageAsync(id)` is available separately.
- `SendPrivateMessageAsync` and `SendRoomMessageAsync` complete after writing a
  command to the server connection.
- `JoinRoomAsync` returns `RoomData`; joining a room already joined may receive
  no response and become `NoResponseException` after the wait timeout.
- A successful leave produces a `RoomLeft` event for the local user even though
  the library currently has to synthesize it.
- A room-message event has room, username, and text, but no protocol message ID
  or timestamp.
- `GetRoomListAsync` distinguishes public, private, owned, and moderated rooms.
  Joined `RoomData` identifies private rooms and supplies owner, operator, and
  user data.
- `AcceptPrivateRoomInvitations` controls the server's private-room invitation
  preference. Membership and moderation changes are exposed as events.
- `AddPrivateRoomMemberAsync` supports the one private-room mutation exposed by
  slskd. Soulseek.NET also exposes broader private-room administration, but it
  is outside the parity target in section 2.2.
- `StartPublicChatAsync` and its message event are a real, separate all-room
  public feed. They are not the same as messages from rooms Sockseek joined and
  remain outside this phase.

Consequences:

- Sockseek configures automatic private-message acknowledgement off.
- Local join state makes join/leave idempotent before calling the library.
- Incoming private-message identity uses the stable protocol fields and local
  uniqueness; incoming room messages receive a local ID and receive timestamp.
- Outgoing records use `Sent` only to mean that Soulseek.NET completed the
  server-connection write.
- Room directory and joined-room contracts retain the library's private-room
  classification instead of flattening every room into a public room.

### 3.2 Lessons from slskd

Useful slskd decisions:

- separate conversation and room services;
- persist private-message history;
- recognize server replays and deduplicate them;
- auto-join configured rooms and rejoin rooms after reconnect;
- accept private-room invitations, list private/owned/moderated rooms, retain
  joined-room owner/operator metadata, and allow an authorized account to add
  members;
- keep a small room-message tail rather than a library-sized global graph;
- ignore messages from blocked users; and
- notify for private messages and room mentions.

Sockseek should not copy several incidental implementation choices:

- slskd performs synchronous database work from a private-message event path;
- protocol acknowledgement doubles as its application acknowledgement/read
  state;
- ignored private messages are not explicitly acknowledged when library
  auto-acknowledgement is disabled, allowing repeated server replay;
- room messages are currently an in-memory mutable-list tail and are not written
  by the room service despite a database entity being present;
- the conversation API uses a fixed `Take(100)` instead of keyset paging;
- room tracking mutates lists stored inside a concurrent dictionary;
- notifications are sent directly from separate protocol event handlers,
  mention detection is a raw substring check, and notification cooldown
  substitutes for durable notification/read state;
- private-room membership and moderation events are logged but do not update
  room state, and the API omits the library's member-removal and moderator
  management operations; and
- direct event subscriptions spread persistence, room state, and notification
  behavior across multiple owners.

Sockseek keeps one coordinator boundary, commits before publishing live state,
uses immutable snapshots under one state owner, and creates notifications in the
same transaction as accepted inbound messages.

For private rooms, “slskd parity” is intentionally narrower than complete
Soulseek.NET support. Sockseek accepts invitations, represents and joins rooms
the server reports as private, exposes owner/operator metadata, and permits
adding a member. It does not add a general private-room administration model.

## 4. Architecture

### 4.1 Daemon session ownership

The first implementation step MUST extract Soulseek session ownership from
`SharingRuntime`. Chat must work when no share roots exist, and a feature named
"sharing runtime" must not own later chat and user-browse lifetimes.

```text
EngineSupervisor
  ├─ DaemonSoulseekRuntime
  │   ├─ SoulseekClientManager       one daemon-owned network session
  │   └─ PeerAccessPolicy            shared exact peer policy
  ├─ SharingRuntime                  catalog, callbacks, uploads
  ├─ ChatRuntime
  │   ├─ bounded event ingestion
  │   ├─ messages/read state/notifications
  │   └─ desired/current rooms and rosters
  ├─ EngineStateStore                compact daemon and scoped live state
  └─ DownloadEngine                  restartable workflow engine

PersistenceRuntime
  ├─ one SQLite writer
  ├─ awaitable critical chat commands
  └─ paged chat/notification readers
```

`DaemonSoulseekRuntime` creates and disposes the manager once. Sharing, chat,
future user browsing, and each restartable download engine receive the same
manager. Chat subscribes once to the concrete client before login and detaches
on shutdown. A small manager lifecycle event or participant registration seam
handles clients created lazily; consumers MUST NOT poll `Client` or attach on
every reconnect.

The session starts proactively when durable chat is available or sharing needs
it. Concurrent startup requests continue through the manager's existing
single-flight connection path.

### 4.2 Ownership and layering

- Core owns chat identity/value validation, mention detection, low-cardinality
  telemetry, and durable domain records.
- Persistence owns schema, critical chat commands, idempotent insertion,
  retention, deletion, and paged readers.
- Server owns daemon lifetime, protocol event adaptation, bounded ingestion,
  room/session state, HTTP/SignalR projection, authorization metadata, and
  mapping persistence results to API behavior.
- API owns DTOs and reusable HTTP/live clients.
- CLI consumes the API clients. It does not subscribe to Soulseek.NET directly
  or maintain another chat database.

`ChatRuntime` is the only subscriber that turns incoming private and room
message events into durable state. Notification logic consumes accepted domain
messages, not a second Soulseek.NET subscription. The ingress and room concerns
remain explicit regions of that one runtime; v4 does not add façade types that
would only forward calls without establishing a separate lifetime or invariant.

### 4.3 Why the remaining complexity exists

- durable-before-ack prevents acknowledged private messages from disappearing
  in a crash;
- a replay uniqueness key prevents duplicates and duplicate notifications;
- a bounded callback-to-worker channel prevents database latency from blocking
  the Soulseek server connection;
- an awaitable critical persistence lane avoids racing the current single SQLite
  writer;
- local idempotency keys prevent an HTTP retry from intentionally sending the
  same outgoing message twice;
- scoped live streams prevent busy room traffic from flooding every daemon
  monitor; and
- desired versus current room state makes reconnect behavior explicit.

There is no event-sourcing framework, generic actor runtime, or per-room
database.

## 5. Configuration, identity, and bounds

### 5.1 Public settings

The initial operator surface is small:

```csharp
sealed class ChatSettings
{
    List<string> AutoJoinRooms;
}

sealed class ServerPersistenceOptions
{
    TimeSpan? PrivateMessageHistoryAge = null; // forever
    TimeSpan? RoomMessageHistoryAge = TimeSpan.FromDays(30);
}
```

CLI/config names:

```ini
chat-room = indie
chat-room = + electronic
private-message-retention-days = forever
room-message-retention-days = 30
```

`chat-room` follows Sockseek's established `key = + value` append syntax.
Configured rooms are always desired while that configuration is active.
Runtime-joined rooms are persisted separately. Leaving a configured room leaves
it for the current connection, but configuration will request it again after a
future daemon start or Soulseek login; the API marks such rooms `Configured` so
this is not surprising.

There is no chat-enabled switch, notification-delivery configuration, message
tail knob, ingress capacity knob, page-size knob, or room-list cache knob.
Chat is available when daemon persistence is enabled and started. Even while
chat is disabled, the bounded protocol adapter remains attached so it can
acknowledge intentional blocked/invalid discards while leaving valid messages
unacknowledged for later replay.

Sockseek sets Soulseek.NET's `AcceptPrivateRoomInvitations` option to `true`, as
slskd does. This is the protocol's server-side invitation preference, not a
Sockseek invitation inbox or an automatic room join. Membership and moderation
events invalidate the available-room cache and refresh affected room metadata.
No additional public setting is added for the parity surface.

The current `EngineSettings` name is broader than its original download-engine
meaning. Session extraction may capture an immutable daemon settings view, but
this feature MUST NOT turn an internal naming cleanup into a public config
migration or a prerequisite-wide settings rewrite. Chat settings are
daemon-lifetime settings and remain forbidden in download profiles.

### 5.2 Identities

- Private-message peer identity uses the existing `PeerUsername.Normalize`
  implementation. Display spelling is stored separately.
- Room names are trimmed, validated as well-formed Unicode, normalized to NFC,
  and compared ordinally. The implementation MUST NOT assume public room names
  are case-insensitive without protocol evidence.
- Every durable chat row is scoped to the normalized local Soulseek account.
  Changing daemon credentials must not merge two accounts' conversations,
  protocol IDs, room subscriptions, notifications, or unread counts. APIs show
  the currently logged-in account; switching back restores that account's
  retained state.
- Message bodies preserve their text and normalization. They may contain tabs
  and line breaks, but not invalid UTF-16 or NUL. Empty/all-whitespace bodies are
  rejected.
- Each conversation and joined/remembered room has a stable local `Guid`
  target ID. API routes and live scope keys use this ID rather than embedding an
  arbitrary peer or room name in a path/group name.
- Each local message has a `Guid MessageId`. Outgoing callers supply it as an
  idempotency key; Sockseek generates it for inbound messages.
- Each stored message and notification also receives a database sequence used
  for watermarks and keyset paging. Protocol private-message IDs remain separate.

Message collections order by durable recorded sequence, not by a peer/server
timestamp. `OccurredAtUtc` remains display metadata. This keeps replayed messages
in the recoverable live tail and prevents an implausible protocol timestamp from
reordering or starving pagination.

### 5.3 Internal bounds

The implementation defines and tests fixed internal bounds for:

- encoded username, room-name, and message sizes;
- configured and simultaneously desired room counts;
- available-room directory, roster, operator, and provisional-roster counts;
- inbound event queue depth;
- available-room cache lifetime;
- API page sizes, live snapshot tails, and gap-recovery buffers;
- per-scope live transport queues and active scope count;
- notification preview length; and
- failure text retained in public state.

The first implementation SHOULD reuse the existing 1,024-byte peer-username
bound and use an 8 KiB UTF-8 message bound. These are abuse/resource bounds, not
claims about the protocol's theoretical maximum. Values are constants and can
be adjusted from interoperability evidence without creating public settings.

Over-bound inbound messages are intentionally discarded and private messages
are acknowledged so a poison message cannot replay forever. Logs and metrics do
not contain the discarded body.

## 6. Private-message lifecycle

### 6.1 Inbound flow

```text
Soulseek.NET event
  → copy and validate bounded fields
  → exact username block check
  → bounded ingress channel
  → idempotent SQLite transaction
       message + conversation + optional notification
  → publish durable live changes if newly inserted
  → acknowledge protocol message ID
```

The synchronous event handler only validates/copies fields and calls
`TryWrite`. It catches every exception.

For a valid, allowed message:

1. The coordinator submits one awaitable critical persistence command.
2. The transaction inserts or finds the replayed message, reactivates its
   conversation, advances conversation summary state only for a new row, and
   creates at most one notification.
3. Only after commit does Sockseek publish state and call
   `AcknowledgePrivateMessageAsync`.
4. An acknowledgement failure leaves the durable message intact. A later server
   replay finds the existing row, creates no second live message/notification,
   and retries acknowledgement.

The replay uniqueness key is the normalized peer plus protocol message ID and
protocol timestamp. Phase 0 verifies that the pinned library preserves those
fields exactly across a replay. The schema does not rely on the replay flag for
uniqueness.

If the ingress channel is full or persistence is unavailable, a valid allowed
private message is not acknowledged. It can be replayed by the server after a
later login. Queue pressure is observable without retaining an unbounded backup
list.

Blocked or invalid private messages are intentional discards: they are not
stored or notified, but a bounded coordinator command attempts protocol
acknowledgement without an `async void`/fire-and-forget callback. If even that
command cannot be admitted, the message remains replayable. This avoids the
repeated blocked-message replay present in a naive manual-ACK implementation.

### 6.2 Protocol acknowledgement is not read state

Protocol acknowledgement tells the Soulseek server it may stop replaying a
message. It happens automatically after durable storage and is not exposed as a
user action.

Application read state is a local watermark changed by the API or UI. Marking a
conversation read does not send a Soulseek acknowledgement because accepted
messages were already acknowledged after commit.

### 6.3 Outbound flow

For a private or room send:

1. Validate target, body, connection readiness, access policy, and caller's
   `MessageId`.
2. Commit an outgoing row with `SendState = Pending` before network I/O.
3. If the same `MessageId`, target, and body already exist, return that record
   without sending again. Reuse with different content returns `409 Conflict`.
4. Call the corresponding Soulseek.NET send method exactly once.
5. Persist `Sent` after a successful server-connection write or `Failed` with a
   compact reason after failure.
6. Publish each committed state change.

There is no automatic retry. After an unclean shutdown, remaining `Pending`
messages become `Unknown`; Sockseek cannot know whether the socket write happened.
The UI may offer an explicit resend with a new `MessageId`.

`Sent` is documented only as "written to the Soulseek server connection."

## 7. Room lifecycle

### 7.1 Desired and current rooms

The room-state region of `ChatRuntime` owns:

- desired rooms: configured rooms union persisted runtime subscriptions;
- current join phase: `Disconnected`, `Joining`, `Joined`, `Leaving`, or
  `Failed` plus one reason;
- server-reported room classification: `Public` or `Private`, plus whether the
  current account owns or moderates the room;
- joined private-room metadata: owner, operator count, and operator names from
  `RoomData`;
- the current immutable member snapshot and member revision; and
- read watermark, unread count, and last message summary from persistence.

Membership mutations are serialized through one small gate. Network waits do
not hold the general chat state lock. The coordinator checks local state before
calling `JoinRoomAsync`, making duplicate join and leave requests idempotent and
avoiding the library's already-joined timeout behavior.

On disconnect, current rosters are cleared and desired rooms remain. On login,
the coordinator refreshes the accessible room list and rejoins desired rooms
sequentially. It passes `isPrivate: true` only when the server classified that
existing room as private; an arbitrary join request cannot create a private
room accidentally. One room failure does not prevent later rooms, and failures
remain visible on that room summary. Reconnect does not create duplicate
subscription rows.

Private-room membership and role-list events invalidate the available-room
cache, so the next discovery read obtains the affected classifications from the
server. Because Soulseek.NET's moderation-added/removed events specifically
describe the current account, they also update an already joined room's
moderated summary, operator detail, local roster role, and live target state
immediately. Revocation does not delete stored history. If a desired room is no
longer accessible, its join fails like any other room and remains visible with
the per-room reason.

### 7.2 Available rooms

`GetRoomListAsync` feeds one short-lived, latest-value cache guarded by a single
refresh task. Concurrent API requests share the refresh. Results expose every
room visible to the current account and preserve the server's public, private,
owned, and moderated classifications. They are sorted and paginated and are not
persisted as history.

An unexpired cached result remains usable. A required or explicitly forced
refresh failure reports `Unavailable`; there is no stale-result protocol,
cache configuration surface, or background room-directory crawler.

### 7.3 Private-room parity operations

Sockseek exposes `AddPrivateRoomMemberAsync` for a joined private room. The
request validates the username and current room classification, then lets the
Soulseek server enforce ownership/authorization. Success means only that the
server command completed; the roster and room metadata continue to follow
protocol events and later refreshes.

This operation does not imply a general role or invitation subsystem. Sockseek
does not expose private-room creation, member removal, moderator changes,
ownership drop, or membership drop in v4. Ordinary leave remains a
session action and must not be mislabeled as dropping private-room membership.

### 7.4 Messages and rosters

Room-message events pass through the same bounded ingress coordinator and exact
username block check as private messages. Accepted messages are persisted before
live publication. Because the protocol supplies no message ID or timestamp,
Sockseek assigns both locally. Room messages are not replayable; an event dropped
under sustained persistence pressure is counted and cannot be recovered.

Outgoing room rows are created before the send call. A subsequent room event
whose username is the daemon's own username is treated as the server echo of
local state and is not inserted again. Phase 0 confirms self-echo behavior with
the pinned library and a real server, but correctness does not depend on receiving
the echo.

The current roster begins with `RoomData.Users` from the join response and is
updated from joined/left events. The room enters `Joining` before network I/O;
membership events received before the join continuation runs are recorded as a
bounded provisional delta and applied over the response snapshot. This avoids
losing a join/leave event solely because waiter completion and event dispatch
resume on different tasks.

The roster is in-memory ephemeral state, not historical data. Updates replace
immutable snapshots under the room owner; mutable lists inside a concurrent
dictionary are prohibited. If a membership event cannot be admitted, the room
is marked `RosterComplete = false` instead of presenting a knowingly stale list
as authoritative. A later rejoin rebuilds it.

Rosters and message histories are paged. A room live snapshot contains member
count/revision and a bounded message tail, not every member of every room.

### 7.5 Read state

Each conversation and room stores `LastReadMessageSequence`. Mark-read requests
specify a message ID belonging to that target. The transaction advances, never
rewinds, the watermark and marks related notifications through that sequence
read. New incoming direct or room messages after the watermark are unread;
outgoing messages are never unread.

Room unread counts include accepted non-self messages. Notifications are created
only for mentions, so unread room activity and unread notifications intentionally
have different counts.

## 8. Persistence model

### 8.1 Tables

The existing `sockseek.db` gains:

```text
chat_conversations
  conversation_id PK, local_account_key, peer_key, display_username, archived_at,
  last_read_sequence,
  last_message_sequence, revision, created_at, updated_at

chat_room_subscriptions
  room_id PK, local_account_key, room_key, display_name, runtime_desired,
  last_read_sequence,
  last_message_sequence, revision, created_at, updated_at

chat_messages
  message_id PK, sequence UNIQUE, local_account_key, target_kind, target_key,
  display_target, sender_key, display_sender, direction, body,
  occurred_at, recorded_at, send_state, failure_reason,
  protocol_message_id, protocol_timestamp

notifications
  notification_id PK, sequence UNIQUE, local_account_key, kind, source_message_id,
  created_at, read_at

chat_sequences
  sequence_id singleton PK, last_message_sequence, last_notification_sequence
```

Names above are logical. EF naming follows the existing schema conventions.

Required indexes support:

- conversation/room summaries by last message sequence;
- unique peer/room identities within one local account;
- chronological/keyset message pages by target;
- unread calculation after a target watermark;
- pending outbound reconciliation;
- unread notification pages;
- the partial inbound-private-message replay uniqueness key; and
- one notification kind per source message within one local account.

Message bodies are stored once. Notification responses join their source
message and compute a bounded one-line preview instead of duplicating the body.
The singleton allocator prevents durable sequence reuse after deletion or
retention; it is initialized from existing maxima during migration/startup.

### 8.2 Critical write lane

Chat data is user data, not a best-effort projection. The existing single
`PersistenceWriter` gains a bounded awaitable critical-command lane. A chat
command can return a result such as `Inserted`, `Duplicate`, or the committed
DTO identity after its transaction completes.

The lane:

- uses the same single SQLite writer and busy/recovery policy;
- waits asynchronously for bounded capacity from the chat worker/API, never
  from a Soulseek event callback;
- never coalesces or evicts distinct chat messages;
- completes callers only after commit;
- returns permanent failure explicitly; and
- completes/cancels outstanding callers during shutdown.

Ordinary job/transfer projections retain their current behavior. Chat must not
introduce a second concurrent SQLite writer or bypass transaction ordering.
The ingress worker may group up to 16 consecutive direct/room messages into one
ordered transaction. This amortizes SQLite commit and hydration costs without
creating an unbounded transaction or a long private-message ACK delay. Result
records retain input order and reflect each message's point-in-batch target
snapshot; publication and private-message ACKs start only after the whole batch
commits.

### 8.3 Commit and publication order

For every chat mutation:

```text
commit SQLite transaction
  → update Core/Server projection
  → emit ordered live delta
  → perform post-commit protocol ACK when applicable
```

An HTTP hydration performed after observing a delta can therefore read the row.
Notifications and their source inbound message commit atomically.
Projection/live observer exceptions are contained; once commit succeeds, the
protocol ACK is still attempted in a `finally`-equivalent post-commit path.

### 8.4 Startup, shutdown, and disabled persistence

- Startup reconciles outgoing `Pending` messages from an unfinished runtime to
  `Unknown`. They are not resent.
- Login selects the normalized local-account partition before callbacks are
  accepted. History from another configured Soulseek account remains isolated.
- Desired runtime room subscriptions survive restart; current room state does
  not.
- Shutdown stops new actions, detaches callbacks, drains the bounded ingress and
  critical commands within the daemon shutdown budget, then releases the shared
  Soulseek session.
- If persistence is disabled, chat reports `Disabled/PersistenceDisabled`, room
  auto-join is skipped, send/join mutations return `503`, and valid allowed
  private messages are not protocol-acknowledged. This is one explicit behavior,
  not a second volatile chat store.

### 8.5 Retention, archive, and deletion

Direct and room-message retention policies are independent, time based,
bounded-batch, and integrated with scheduled and manual persistence retention.
Private messages default to `forever`; higher-volume room messages default to 30
days. Either accepts a positive day count or `forever`. Expired messages and
their notifications are deleted transactionally, target summaries/watermarks
are repaired, and conversation and current room-subscription records remain.
After commit, affected live scopes receive a replacement bounded tail rather
than retaining messages that no longer exist in durable history.

Archiving a conversation hides it from the default list but retains messages;
new inbound/outbound activity reactivates it. Separate explicit history-delete
actions permanently remove a conversation's or room's stored messages and
related notifications. Leaving a room does not silently delete history.

Deletion endpoints and retention use the existing operator mutation seam.

## 9. Notifications

### 9.1 Semantics

The notification API is a durable in-app inbox. Initial kinds are stable string
codes:

- `PrivateMessage`
- `RoomMention`

A notification contains ID, kind, creation/read time, actor, target, source
message ID, a bounded plain-text preview, and a resource link. The database row
references the source message.

Exactly one notification is created for each newly inserted inbound private
message. A room notification is created for an accepted non-self message that
mentions the current username as a case-insensitive whole token, optionally
preceded by `@`. Adjacent letters, digits, `_`, or `-` prevent a match. This
avoids slskd's substring false positives while retaining common Soulseek usage.

Replay, blocked, invalid, duplicate, outgoing, and self room messages do not
create notifications.

### 9.2 Read behavior

- Marking one notification read is idempotent.
- A bulk action marks notifications through a supplied notification sequence or
  explicit bounded ID list.
- Marking a chat target read also marks its notifications through the selected
  source message read in the same transaction.
- Marking a notification read does not claim that every message in its target
  has been read.

The daemon does not infer whether a GUI tab is focused and does not suppress or
cool down durable notifications. Clients explicitly mark visible messages read,
so disconnected clients retain correct unread state.

### 9.3 API and live delivery

New notifications are sent as ordered SignalR state changes and remain pageable
over HTTP. Notification delivery does not perform outbound work from protocol
callbacks, and a live-publication failure cannot affect chat ingestion or the
durable notification record.

## 10. Public state and live protocol

### 10.1 Compact daemon summary

Rename the sharing-specific public health enum to a general
`DaemonFeatureState` and reuse its four values:

```csharp
enum DaemonFeatureState { Disabled, Starting, Ready, Degraded }
```

The rename occurs before v4 release rather than adding duplicate chat and
sharing enums with identical meanings.

`DaemonStateDto` adds compact summaries:

```csharp
ChatRuntimeStateDto Chat(
    DaemonFeatureState State,
    string? Reason,
    int DesiredRoomCount,
    int JoinedRoomCount,
    int UnreadPrivateMessageCount,
    int UnreadRoomMessageCount,
    long Revision);

NotificationSummaryDto Notifications(
    int UnreadCount,
    long Revision);
```

The daemon snapshot contains no message bodies, conversation list, room roster,
or notification list. Aggregate replacements are latest-value coalescible.

### 10.2 Scoped chat streams

Extend `StateStreamScopeKind` with `ChatConversation` and `ChatRoom` and extend
`StateStreamScopeDto` with one validated `ChatTargetId`. Existing
daemon/workflow validation remains. Raw usernames/room names are not SignalR
group keys. The live protocol version increments.

A chat scope snapshot contains:

- target summary and revision;
- a bounded latest message window plus `HasEarlierMessages`;
- room join/member count and member revision for room scopes; and
- the same epoch/sequence position used by existing streams.

Chat scope deltas contain cohesive target-summary replacement, message
add/status changes, bounded-tail replacement after deletion/retention, and room
runtime replacement. Individual roster mutations are not replicated. Member
pages remain independently hydrated; a client that has not loaded the roster
can update the member count and revision without fabricating member data.

Messages for a busy room are delivered only to subscribers of that room scope.
Daemon subscribers receive aggregate revisions and new notification records,
not every room message.

Mark-read operations replace the compact notification summary rather than
broadcasting every affected inbox row. A client displaying a notification page
refreshes it when the summary revision changes; this keeps a bulk `read all`
operation bounded.

Snapshot race, buffered handoff, duplicate batch, sequence gap, reconnect, and
epoch recovery reuse `SockseekLiveClient`'s existing algorithm. After a gap, a
chat scope rehydrates its bounded tail; notification consumers refresh the
paged unread collection when its revision changed.

SignalR publication uses a bounded sender per active scope. A stalled room does
not block another scope. At capacity, the sender evicts an older queued batch
and retains the newest one; the resulting sequence gap makes clients rehydrate
instead of leaving them silently stale. Idle senders retire, and the number of
active senders is bounded.

### 10.3 Client store

`DaemonClientStore` retains only explicitly subscribed chat scopes and bounded
message windows. Paged older messages, full conversation/room lists, rosters,
and notification history are independent hydrated collections. Replacing live
state MUST preserve those pages, as it already does for retained workflow
history.

Activity events MAY describe join/leave diagnostics, but losing activity can
never lose a message, read state, room phase, notification, or unread count.

## 11. HTTP API

All collections use validated unsigned keyset cursors and hard page limits.
Message and notification cursors carry the last durable sequence; summary
cursors pair last activity sequence with target ID. Malformed cursors return
`400`; cursors are not transaction snapshots.

### 11.1 Chat status and conversations

```text
GET    /api/chat
GET    /api/chat/conversations?unread=&archived=&cursor=&limit=
POST   /api/chat/private-messages
GET    /api/chat/conversations/{conversationId}
GET    /api/chat/conversations/{conversationId}/messages?cursor=&limit=
POST   /api/chat/conversations/{conversationId}/messages
POST   /api/chat/conversations/{conversationId}/read
POST   /api/chat/conversations/{conversationId}/archive
DELETE /api/chat/conversations/{conversationId}/history
```

Send body:

```json
{ "messageId": "uuid", "text": "hello" }
```

`POST /api/chat/private-messages` adds `username` to that body and creates or
reactivates the conversation. Subsequent ID-based routes avoid arbitrary remote
names in URL paths.

Mark-read body names the last visible message ID. Archive and read actions are
idempotent. Permanent history deletion is explicit and does not masquerade as
archive.

### 11.2 Rooms

```text
GET    /api/chat/rooms/available?kind=&cursor=&limit=&refresh=
GET    /api/chat/rooms?state=&cursor=&limit=
POST   /api/chat/rooms
GET    /api/chat/rooms/{roomId}
DELETE /api/chat/rooms/{roomId}
GET    /api/chat/rooms/{roomId}/messages?cursor=&limit=
POST   /api/chat/rooms/{roomId}/messages
POST   /api/chat/rooms/{roomId}/read
GET    /api/chat/rooms/{roomId}/members?cursor=&limit=&revision=
POST   /api/chat/rooms/{roomId}/members
DELETE /api/chat/rooms/{roomId}/history
```

Join accepts `{ "roomName": "...", "remember": true }`. `remember` creates a
runtime subscription; configured rooms do not need one. Leave is idempotent and
returns the resulting desired/current summary so configuration-managed rejoin
behavior is visible.

Room sends use the same `messageId`/`text` body as private sends.
Adding a member accepts `{ "username": "..." }`, is valid only for a joined
private room, and maps to `AddPrivateRoomMemberAsync`. Available-room and joined
room DTOs expose private/owned/moderated flags; joined room detail also exposes
the owner, operator count, and operator names supplied by `RoomData`.

### 11.3 Notifications

```text
GET  /api/notifications?unread=&kind=&cursor=&limit=
GET  /api/notifications/{notificationId}
POST /api/notifications/{notificationId}/read
POST /api/notifications/read
```

The bulk read body contains either a through sequence or a bounded list of IDs,
not an unbounded query expression.

### 11.4 Contracts and errors

Core DTOs include:

- `ChatRuntimeStateDto`
- `ChatTargetDto` and `ChatTargetSummaryDto`
- `ConversationSummaryDto`
- `AvailableRoomDto`, `ChatRoomSummaryDto`, `ChatRoomDetailDto`, and
  `RoomMemberDto`
- `ChatMessageDto`
- `ChatMessagePageDto`
- `UserNotificationDto` and `NotificationPageDto`
- scoped chat snapshot/delta DTOs

Stable actionable error categories remain small: `InvalidRequest`, `Denied`,
`NotFound`, `Conflict`, `Capacity`, and `Unavailable`. Detailed library/database
exceptions stay in owner-only logs. OpenAPI declares the common `400`, `403`,
`404`, `409`, `429`, and `503` responses on every chat and notification route,
in addition to each route's success response.

Every chat/notification read and mutation endpoint carries `Sockseek.Operator`
authorization metadata for now because message contents are private operator
data. Generalize the current `RequireOperatorMutation` helper to
`RequireOperator`; retain the old helper as a delegating alias until its callers
are migrated. SignalR chat-scope subscription performs the same check. The
current evaluator is pass-through; a non-loopback unauthenticated daemon is
therefore explicitly insecure until the authentication roadmap item lands.

## 12. API clients, CLI, and documentation

`SockseekApiClient` adds typed paged queries and actions. `SockseekLiveClient`
adds scoped conversation/room subscriptions through the existing connection and
recovery machinery. `DaemonClientStore` remains the single reducer used by local
CLI, remote CLI, and the future Web UI.

The CLI provides scriptable commands, not a second interactive chat UI:

```text
sockseek chat status
sockseek chat conversations [--unread] [--json]
sockseek chat messages <username> [--limit N] [--json]
sockseek chat send <username> <message>
sockseek chat read <username> [--through <message-id>]
sockseek chat archive <username>

sockseek room available [--json]
sockseek room joined [--json]
sockseek room join <name> [--no-remember]
sockseek room leave <name>
sockseek room messages <name> [--limit N] [--json]
sockseek room send <name> <message>
sockseek room members <name> [--json]
sockseek room member add <name> <username>

sockseek notifications [--unread] [--json]
sockseek notification read <id|all>
```

Mutating and network chat commands require a configured remote daemon URL or a
`--remote` override; a temporary foreground download engine never receives
private messages or joins rooms.
CLI send commands generate a fresh outgoing `MessageId` per invocation. Typed
clients accept a caller-supplied ID so an application can preserve it across an
HTTP retry.

Operational configuration and privacy behavior go in `docs/daemon.md`. The
generated OpenAPI and source contracts are authoritative while the API is in
flux; `docs/api.md` stays a small integration overview and source map rather
than duplicating endpoint inventories or feature semantics. README keeps a
short feature overview and generated option/command reference.

## 13. Failure behavior and security

### 13.1 Failure boundaries

- Chat persistence unavailable at startup disables chat without breaking
  sharing, downloads, daemon history reads, or the HTTP host.
- A valid private message is acknowledged only after durable commit. Persistence
  failure leaves it replayable.
- Invalid/blocked private messages are acknowledged after deliberate discard.
- A duplicate private-message replay triggers another ACK attempt but no second
  row, unread increment, notification, or live message.
- Room messages lost to a full ingress channel increment a metric and degrade
  chat health; the protocol cannot replay them.
- One room join/rejoin failure affects only that room.
- Send persistence failure prevents the network send. Network send failure keeps
  a durable failed row. A crash with uncertain send becomes `Unknown` and is not
  retried.
- Notification publication failure does not roll back a committed message;
  notification HTTP hydration remains authoritative.
- A slow SignalR consumer recovers from a snapshot/page and cannot backpressure
  Soulseek callbacks.

### 13.2 Untrusted input and privacy

- Validate all peer strings before allocation into durable/live state.
- Never render message text as HTML. Web clients use text nodes.
- Do not log message bodies, notification previews, credentials, or protocol
  private-message IDs. Usernames/room names appear only where operationally
  necessary and not as metric labels.
- Owner-only database and backup permissions apply because chat history is
  private content. Documentation states that backups contain chats.
- SQL is parameterized. Cursors are bounded untrusted input.
- Exact username blocks apply without per-message endpoint lookups. Chat events
  do not carry peer IPs, so IP-only blocks are not claimed to filter chat.
- API pages, live tails, rosters, queues, message sizes, and diagnostic samples
  are bounded.
- Permanent history deletion is transactional and auditable in logs without
  logging deleted text.

## 14. Observability

Initial metrics are compact and low-cardinality:

- chat ingress queue depth;
- accepted, duplicate, blocked, invalid, and dropped inbound message totals by
  `direct|room`;
- outbound messages by `direct|room` and `sent|failed|unknown`;
- joined/desired room gauges;
- unread notification gauge and created/read totals; and
- critical chat persistence failures.

No username, room, body, message ID, or notification ID labels are permitted.
Add per-phase latency histograms only if qualification or field diagnosis uses
them.

Health remains compact: `Disabled`, `Starting`, `Ready`, or `Degraded` with one
stable reason. Detailed queue/database/room failures remain in metrics, logs,
and target detail resources.

## 15. Implementation sequence

### Phase 0: protocol and lifecycle qualification

Before schema/API implementation:

1. Add chat methods/events to Sockseek's existing `ISoulseekClient` test double.
2. Confirm automatic ACK is off, event-before-ACK behavior, replay field
   stability, and explicit ACK calls against pinned Soulseek.NET source/tests.
3. Capture a real server flow for new and replayed private messages, send write
   semantics, room self echo, join response/event ordering, already-joined
   behavior, leave, disconnect, and reconnect. Include an invitation to a
   private room, private/owned/moderated room-list classification, private join
   metadata, and an owner adding a member.
4. Record practical encoded room/message behavior with SoulseekQt or Nicotine+;
   adjust internal constants only if normal clients require it.
5. Decide no new architecture from undocumented behavior unless the capture
   demonstrates a concrete need.

Evidence updates section 3; it does not become a permanent capability matrix.

### Phase 1: neutral session owner and persistence foundation

- Extract `DaemonSoulseekRuntime` and shared `PeerAccessPolicy`.
- Make sharing and downloads consume the extracted manager without behavior
  regressions.
- Add chat settings validation and configure manual private-message ACK.
- Add schema, indexes, critical awaitable persistence lane, readers, retention,
  and startup reconciliation.
- Add persistence tests before attaching network events.

### Phase 2: private messages and notifications

- Implement bounded ingress, durable-before-ACK, replay deduplication, blocked
  discard ACK, outbound idempotency, read watermarks, archive/delete, and
  notification transaction.
- Add conversation/message/notification HTTP APIs and Core/API contracts.
- Publish compact daemon state and notification deltas.

### Phase 3: rooms

- Implement available-room cache, desired/current room coordinator, sequential
  reconnect, immutable rosters, room persistence, self-message handling, read
  state, and room APIs.
- Preserve public/private/owned/moderated room classifications, accept private
  invitations, expose joined private-room metadata, and add the parity-level
  add-member operation.
- Keep broader private-room administration, tickers, and global public chat
  unwired.

### Phase 4: live scopes, clients, CLI, and docs

- Extend live protocol scopes/snapshots/deltas and increment protocol version.
- Extend shared clients/store and deterministic recovery tests.
- Add scriptable CLI commands, config/help parity, daemon/API docs, generated
  OpenAPI, and concise README coverage.

### Phase 5: release qualification

- Run the bounded stress and independent-client smoke in section 16.
- Record evidence, fix observed regressions, and check the authoritative
  requirements in section 18.

## 16. Verification strategy

### 16.1 Automated coverage

Core tests cover:

- username/room/message validation, Unicode, bounds, mention boundaries, and
  exact username blocking;
- callback containment and bounded ingress behavior;
- new, duplicate, blocked, invalid, and persistence-failed private messages;
- commit-before-ACK and no duplicate notification/unread changes on replay;
- outgoing idempotency, conflicting reuse, send success/failure, and no retry;
- room join/leave idempotency, sequential reconnect, partial join failure,
  immutable roster updates, self messages, and disconnect cleanup;
- private invitation preference, room-list classifications, private join
  metadata, membership/moderation cache invalidation, add-member validation and
  authorization failures; and
- read watermark monotonicity and notification coupling.

Persistence tests cover:

- migrations, uniqueness/indexes, atomic message+notification insertion;
- critical command completion only after commit and bounded backpressure;
- paged chronological history and summary queries;
- archive/reactivation, delete, retention repair, and notification pages; and
- startup `Pending → Unknown` reconciliation.

Server/API/client/CLI tests cover:

- endpoint validation/statuses/actions and operator policy metadata;
- no bodies or unbounded collections in daemon state;
- scoped snapshot/delta equivalence, races, gaps, reconnects, epoch changes, and
  preservation of independently hydrated pages;
- busy-room isolation from daemon and unrelated chat scopes;
- OpenAPI/JSON round trips and cursor rejection; and
- config, CLI, generated help, README, daemon docs, and API docs parity.

### 16.2 Stress and failure qualification

Use a reproducible homeserver-class fixture to record:

1. A burst of at least 10,000 mixed direct/room events through validation,
   ingress, persistence, notification projection, and scoped live publication.
   Record peak managed memory, queue depth, database growth, drops, and drain
   time; do not invent universal latency ratios before measuring.
2. Persistence busy/outage/recovery while direct and room messages arrive.
   Verify direct messages remain unacknowledged until durable, duplicate replay
   is harmless, and any unrecoverable room loss is counted.
3. Retention/deletion against a representative large history with concurrent
   reads and new messages.
4. A stalled live transport scope alongside another active room; busy-room
   traffic must stay within the per-scope queue bound, retain a newest batch
   that exposes a recoverable sequence gap, and not delay the unrelated room or
   protocol callbacks. SignalR's own transport bounds remain responsible for
   individual network connections.

Unbounded growth, lost acknowledged direct messages, duplicate sends on API
retry, cross-scope message disclosure, or unusable homeserver behavior blocks
release. Numeric regression thresholds come from the recorded baseline.

### 16.3 Interoperability smoke

With the pinned library and at least one independent client:

- exchange direct messages in both directions;
- disconnect before/after ACK and verify replay/deduplication;
- block a sender and verify discard plus no repeated replay;
- list, join, send, receive, leave, reconnect, and rejoin a public room;
- receive a private-room invitation, list and join that room, verify owner and
  operator metadata, exchange messages, and add a member when the test account
  owns the room;
- verify self room messages appear once;
- verify mention boundaries and durable notification/read behavior; and
- exercise Unicode, multiline, empty, and near-bound message cases.

Broader clients/platforms are periodic compatibility evidence, not a blocker for
every patch.

#### Qualification procedure and record

This smoke is intentionally not replaceable with `ISoulseekClient` mocks. Use a
peer implemented independently of Soulseek.NET, such as a current slskd or
Nicotine+ release, two disposable test accounts, a fresh Sockseek data
directory, and non-sensitive test rooms. Record the Sockseek build or artifact
hash, Soulseek.NET package version, independent client/version, operating
systems, UTC date, and a sanitized result for every case below. Do not record
credentials or message bodies in the repository.

| ID | Real-client action and required observation |
|---|---|
| `I-01` | Send a direct message in each direction. Each appears once through `chat messages`; the received message creates one unread notification and can be marked read. |
| `I-02` | Hold the test database under an external SQLite write lock, send a direct message, and terminate Sockseek before releasing the lock. After restart, the server replays the unacknowledged message and Sockseek stores/ACKs it once. Repeat after allowing commit/ACK and prove restart creates no duplicate. |
| `I-03` | Configure the peer username as blocked, send a direct message, and reconnect Sockseek. No conversation or notification is created and the discarded message is not replayed repeatedly. |
| `I-04` | List, join, exchange messages in, leave, reconnect, and automatically rejoin a public room. Joined state and roster changes agree with the independent client. |
| `I-05` | From the independent client, create a private room and invite the Sockseek account. Sockseek lists and joins it as private, reports owner/operator metadata, exchanges messages, and adds a third test account when authorized. |
| `I-06` | Send from Sockseek in a joined room and prove the local message appears once despite server echo. Exercise non-mention substrings and whole-token mentions; only the latter creates a notification, and the target read action clears it. |
| `I-07` | Exercise Unicode and multiline bodies, reject an empty body locally, and send valid bodies near Sockseek's 8 KiB UTF-8 limit in both directions. Record any smaller server/client limit as an interoperability constraint rather than weakening local bounds silently. |

Run the Sockseek side through the documented daemon and remote CLI commands so
the same HTTP/API path used by future GUI clients is covered. A qualification
record is complete only when all seven cases pass or identify an accepted,
documented compatibility limitation. Check `TEST-03` only after linking that
record from section 21.

## 17. Implementation map

```text
Sockseek.Core/
  Settings/ChatSettings.cs
  Chat/
    ChatContracts.cs
    ChatSettingsValidator.cs
    ChatTelemetry.cs
  Soulseek/SoulseekInboundRequestRouter.cs
  Soulseek/SoulseekClientManager.cs

Sockseek.Persistence/
  Entities/PersistenceEntities.cs
  Configurations/EntityConfigurations.cs
  Migrations/<AddChatAndNotifications>.cs
  Chat/
    ChatPersistenceStore.cs
  Write/PersistenceInbox.cs
  Write/PersistenceWriter.cs
  Read/RetentionService.cs

Sockseek.Server/
  DaemonSoulseekRuntime.cs
  ChatRuntime.cs
  DisabledChatIngress.cs
  BoundedStateBatchDispatcher.cs
  EngineSupervisor.cs
  EngineStateStore.cs
  ServerEventBroadcaster.cs
  ServerHost.cs

Sockseek.Api/
  Contracts/Chats.cs
  Client/SockseekApiClient.cs
  Client/SockseekLiveClient.cs
  Client/DaemonClientStore.cs

Sockseek.Cli/
  ChatCommandRunner.cs
```

Names may be adjusted to fit existing conventions. Ownership boundaries and the
single-session/single-writer constraints are normative.

## 18. Authoritative implementation checklist

`[ ]` means not yet satisfied. This is the only functional completion and
release-evidence checklist; phase sections provide order without duplicate
boxes.

### Architecture and configuration

- [x] **ARCH-01** A neutral daemon runtime, not `SharingRuntime`, owns the one
  Soulseek manager used by downloads, sharing, chat, and later user browsing.
- [x] **ARCH-02** Chat has one bounded ingress owner and one state coordinator;
  notification logic does not subscribe independently to protocol events.
- [x] **ARCH-03** Chat uses the existing SQLite database/single writer and is
  explicitly disabled when daemon persistence is disabled.
- [x] **ARCH-04** Public config exposes auto-join rooms and one chat retention
  age without internal queue/cache/tail/delivery knobs.
- [x] **ARCH-05** Config uses the established `key = + value` list append syntax
  and rejects daemon chat settings in download profiles.
- [x] **ARCH-06** Sharing and download behavior remains equivalent after session
  ownership extraction.

### Private messages and rooms

- [x] **DM-01** Soulseek.NET automatic private-message ACK is disabled and valid
  direct messages commit before explicit protocol ACK.
- [x] **DM-02** Replay identity is idempotent and a duplicate retries ACK without
  duplicating history, unread count, notification, or live delivery.
- [x] **DM-03** Blocked/invalid direct messages are discarded and acknowledged;
  valid messages rejected by backpressure/persistence are not acknowledged.
- [x] **DM-04** Protocol ACK and local read watermark are separate behaviors.
- [x] **SEND-01** Outgoing intent commits before network I/O and caller message
  IDs make identical HTTP retries idempotent.
- [x] **SEND-02** Conflicting ID reuse fails, network send occurs at most once,
  and no automatic retry is performed.
- [x] **SEND-03** Public send state distinguishes `Pending`, `Sent`, `Failed`, and
  `Unknown`; `Sent` means only that the server-connection write completed.
- [x] **ROOM-01** Available rooms are bounded, paged, use one shared short-lived
  refresh, preserve public/private/owned/moderated classifications, and do not
  persist a directory history.
- [x] **ROOM-02** Desired/current room state makes join/leave idempotent and
  reconnects desired rooms sequentially with per-room failure isolation.
- [x] **ROOM-03** Roster state uses immutable snapshots, clears on disconnect,
  and remains paged/outside daemon-wide replicated state; joined private-room
  detail preserves owner and operator metadata.
- [x] **ROOM-04** Accepted room messages persist once, self sends appear once,
  and unrecoverable ingress drops are measured.
- [x] **ROOM-05** Private-room invitations are accepted, membership/moderation
  events refresh classifications, and a joined private room supports adding a
  member with server authorization.
- [x] **ROOM-06** Private-room creation, member removal, moderator changes,
  ownership/membership drop, tickers, and global public chat stay outside the
  implemented surface.

### Persistence and notifications

- [x] **DATA-01** Chat schema, local-account isolation, indexes, replay
  uniqueness, migrations, and owner-only database/backup behavior are covered
  by tests.
- [x] **DATA-02** Awaitable critical chat commands share the existing bounded
  single writer and complete only after transaction commit.
- [x] **DATA-03** Message plus notification creation is atomic and live state is
  published only after commit.
- [x] **DATA-04** Startup changes unfinished outgoing messages to `Unknown`
  without resending them.
- [x] **DATA-05** Archive/reactivation, explicit history deletion, paged reads,
  and bounded retention have documented behavior.
- [x] **NOTIFY-01** New accepted direct messages and whole-token non-self room
  mentions create one durable notification.
- [x] **NOTIFY-02** Notification and target read actions are idempotent and keep
  their explicitly different semantics.
- [x] **NOTIFY-03** Notifications page over HTTP; new records and compact read
  summaries publish as ordered live changes, and publication failure cannot
  affect chat ingestion or durable records.

### State, API, clients, and operations

- [x] **STATE-01** Sharing/chat reuse a general four-value daemon feature health
  enum plus one reason.
- [x] **STATE-02** Daemon state contains only compact chat/notification aggregates;
  full histories, conversations, rooms, rosters, and notifications are paged.
- [x] **STATE-03** Conversation and room live scopes reuse epoch/sequence
  continuity and hydrate bounded message tails.
- [x] **STATE-04** Busy room traffic is isolated from daemon and unrelated chat
  scopes, and activity loss cannot lose reconstructable state.
- [x] **API-01** Conversation, room, message, read/archive/delete, and notification
  resources have typed contracts, bounded cursors, actions, and OpenAPI.
- [x] **API-02** All chat reads/mutations and live subscriptions carry the
  generalized operator authorization seam and document the current
  unauthenticated risk.
- [x] **CLIENT-01** Shared HTTP/live clients and `DaemonClientStore` implement one
  reducer/recovery model for CLI and future Web UI.
- [x] **CLI-01** Scriptable remote chat/room/notification commands, JSON output,
  standard configured-remote resolution with CLI override, help, daemon docs,
  API docs, and README remain in sync.
- [x] **SEC-01** Bodies are plain text, bounded, absent from normal logs/metrics,
  and never rendered as remote HTML/Markdown.
- [x] **SEC-02** Exact username blocks apply to chat without claiming IP-only
  filtering or adding per-message endpoint lookups.
- [x] **OPS-01** Metrics are bounded and low-cardinality, with no peer, room,
  body, message, or notification labels.
- [x] **TEST-01** Automated Core, persistence, server, API, client-store, CLI,
  generated-help, migration, and OpenAPI coverage passes.
- [x] **TEST-02** The bounded stress and failure qualification in section 16.2
  has recorded regression evidence and numeric homeserver-oriented limits.
- [ ] **TEST-03** The pinned-library independent-client smoke in section 16.3
  has recorded evidence.

## 19. Roadmap compatibility

### Web UI

The future Web UI can hydrate paged conversations/rooms/notifications, subscribe
only to open targets, recover gaps, and render unread badges from durable state
without adding a second backend protocol. Recent slskd fixes for chat
input/initiation reinforce keeping send idempotency and client state in shared
tested APIs rather than view-specific request logic.

### User browsing

Chat DTOs expose usernames but do not prematurely define a complete user profile
model. The later user-browsing design may add links/actions from a chat actor to
description, picture, and shares. It should reuse the neutral daemon session and
`PeerUsername`, not make chat own a user cache.

### Authentication

Chat content makes authentication more urgent, but authentication remains its
own TODO item. Every chat route/scope is already marked as private operator data,
so replacing the pass-through evaluator with user/password authentication does
not change chat service contracts.

### Protocol-supported additions outside this plan

Soulseek.NET exposes broader private-room administration, room tickers, and the
separate all-room public-chat feed. If the roadmap later selects one of these
real protocol capabilities, it should extend the existing room coordinator and
contracts rather than bypass durable ingestion or subscribe independently to
Soulseek events.

## 20. Source record

### Sockseek

- [v4 roadmap](../../TODO.md)
- [sharing/uploads design and maintainability rules](archive/sharing-uploads-design.md)
- [daemon operation](../daemon.md)
- [API/live clients](../api.md)
- [persistence design](archive/persistence-design.md)
- [API improvements design](archive/api-improvements-design.md)

### slskd

- [application message handlers](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Application.cs)
- [conversation service](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Messaging/ConversationService.cs)
- [room service](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Messaging/RoomService.cs)
- [room tracker](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Messaging/RoomTracker.cs)
- [messaging database](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Messaging/MessagingDbContext.cs)
- [conversation API](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Messaging/API/Controllers/ConversationsController.cs)
- [room API](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Messaging/API/Controllers/RoomsController.cs)
- [room model and private-room metadata](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Messaging/Types/Room.cs)
- [event bus](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/src/slskd/Events/EventBus.cs)
- [chat and notification configuration](https://github.com/slskd/slskd/blob/43a4ff64169df7f2304aa2348907fc5a9442474c/docs/config.md)
- [chat/room input fix #1719](https://github.com/slskd/slskd/pull/1719)
- [conversation initiation fix #1780](https://github.com/slskd/slskd/pull/1780)

### Soulseek.NET

- [client options and private-message auto-ack](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Options/SoulseekClientOptions.cs)
- [server message handler and event/ACK order](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/Messaging/Handlers/ServerMessageHandler.cs)
- [private-message event fields](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/EventArgs/PrivateMessageReceivedEventArgs.cs)
- [room-message event fields](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/EventArgs/RoomMessageReceivedEventArgs.cs)
- [client interface, private rooms, and public-chat feed](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/ISoulseekClient.cs)
- [room list classifications](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/RoomList.cs)
- [joined-room metadata](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/RoomData.cs)
- [send/join/leave implementations](https://github.com/jpdillingham/Soulseek.NET/blob/52fc3e4267114d8cd9492cb4d7438b3eca0267bf/src/SoulseekClient.cs)
- [package 10.0.2](https://www.nuget.org/packages/Soulseek/10.0.2)

## 21. Implementation evidence and open release gates

As of 2026-08-07, the implementation and checked requirements above are covered
by the repository's Core, persistence, server, API/client-store, CLI, migration,
generated-help, and generated-OpenAPI tests. The design was updated during
implementation for durable sequence allocation, bounded partial-roster flags,
strict cursor decoding, live-tail replacement after deletion/retention, correct
state-filter pagination, explicit joined private-room role metadata, login-time
account selection, incremental post-commit summaries, and bounded per-scope
live dispatch. Private-room moderator callbacks now update the joined-room
summary and local roster role immediately, including when they race the join
response; reconnect tests also prove that remembered rooms rejoin once and an
explicit leave removes that runtime desire. Retention policies are separate so
private messages default to durable storage while higher-volume room history
has an operator-overridable 30-day default.
Top-level chat, sharing/transfer, and offline database commands now pass through
the same config/profile/CLI precedence entry point before command-specific
parsing; a source-level architecture test rejects direct runner dispatch or a
parallel configuration-loading path.

The repeatable 10,000-event mixed direct/room fixture completed in 9.230 s with
653 bounded writer commits, a peak ingress depth of 250/1,024, zero ingress
drops, 9,710,496 bytes of peak managed-heap growth, and 12,318,744 bytes of
SQLite plus WAL growth. All 5,000 direct messages were acknowledged after
commit, all 10,000 messages and notifications were observed, and all target
changes drained. The regression limits are 30 seconds, at most 1,300 writer
commits, 256 MiB managed growth, and 256 MiB database growth.

Additional qualification holds an exclusive SQLite lock while a direct message
and more than one ingress queue of room traffic arrive: the direct ACK remains
at zero until recovery, accepted work drains afterward, and unrecoverable room
overflow is counted and degrades health. A 1,000-message retention batch runs
with four readers and 64 new messages without sequence reuse or inconsistent
final state. A stalled room sender retains at most three queued batches in its
test fixture, an unrelated room sends immediately, and overflow retains the
newest batch with an observable recovery gap.

`TEST-03` remains the only open release-evidence gate. It requires real Soulseek
interoperability with an independent client and a suitable test account; mocks
and source review cannot satisfy it. No completed qualification record is linked
yet; section 16.3 defines the required seven-case record. The fast default
`dotnet test sockseek.sln --no-build --no-restore` lane passes 1,079 regression
tests (663 Core, 52 persistence, 112 server, and 252 CLI); the latest Release
run on the qualification workstation completed in 14.0 seconds. The separate
`--filter TestCategory=Load` lane passes seven load qualifications (one Core,
five persistence, and one server) in 13.8 seconds wall-clock, so all 1,086 tests
remain in CI. This includes bounded critical-command shutdown and in-flight
cancellation.

---

## Bottom line

Chat should turn Sockseek's existing daemon architecture into a genuinely shared
Soulseek runtime, not bolt another client and event bus beside it. The critical
correctness rule is simple: accepted private messages become durable before the
server is told to forget them, while user read state remains local and explicit.

Everything else stays deliberately small: public rooms plus slskd-scale private
room compatibility, one durable message model, one writer, bounded scoped live
state, and one notification inbox.
