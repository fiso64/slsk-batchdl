## TODO

### v4.0

- Preconditions before starting GUI work:
    - Complete `TODO [ARCHITECTURE][GUI-EVENT-DELTAS]` in `Sockseek.Api/Contracts/ServerEvents.cs`: replace summary-heavy SignalR events with a snapshot + compact delta protocol before building the GUI.
    - Make daemon-wide monitoring, not just workflow monitoring. A GUI must be able to subscribe to and display all daemon jobs/workflows; the CLI should support the same mode.
    - Add GUI-friendly startup snapshot endpoints, e.g. a daemon snapshot containing workflows, jobs, current transfer/progress state, and enough metadata to hydrate `WorkflowClientStore` without relying on event replay.
    - Add `WorkflowClientStore` APIs for daemon-wide views: all workflows, all jobs, grouped jobs, active jobs, terminal jobs, and workflow/job lookup.
    - Define the `SubscribeAll` contract clearly: whether it means all workflow batches, global daemon batches, or both. Add parity tests for local/remote all-daemon monitoring.
    - Consider a global daemon sequence or snapshot epoch in addition to per-workflow sequences, so all-daemon consumers can detect gaps and recover coherently.
    - Keep SignalR as the primary live-update transport for GUI/remote CLI; use polling/HTTP snapshots for initial load and recovery, not as the main update loop.
    - Keep durable state updates and ephemeral activity/log edges conceptually separate in the API/client store, even when they travel in the same batch.
    - Think again about the overall API shape.

- Implement soulseek client features (Look how slskd does it for a start):
    - Sharing / Uploads
    - Chats (private, public)
    - User browsing

- Create a webui
    - All the usual functions of a soulseek client

- User+password webui authentication.

- Test performance again for song and album searches (CPU and allocations, include the raw search collection phase + projection) on big queries (e.g. `love`)

### Later

- (breaking) Maybe use yaml for settings instead of our custom format, and improve structure.
