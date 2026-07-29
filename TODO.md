## TODO

### v4.0

- Implement soulseek client features (Look how slskd does it for a start):
    - Sharing / Uploads
    - Chats (private DMs & public chatrooms, notification API)
    - User browsing (user description, profile picture, shares)

- Create a webui using the v4 live-state client
    - All the usual functions of a soulseek client
    - `SockseekLiveClient` and `DaemonClientStore` provide daemon-wide and workflow-scoped snapshot hydration, compact SignalR deltas, continuity recovery, paged history, and shared local/remote state reduction.
    - The CLI exposes daemon-wide monitoring as an orthogonal `--monitor` option. Input is optional, and an optional submitted workflow appears alongside all other active daemon work in the existing live UI.
    - Keep SignalR as the primary live-update transport. HTTP snapshots remain for hydration and recovery; paginated endpoints remain for retained history.
    - Keep replicated state and best-effort activity separate. A future GUI must reconstruct current state without activity replay.

- Secure API authentication, user+pass webui login.

- Rethink README presentation when daemon mode becomes the primary feature, reorganize.
    - Acknowledgements section: Soulseek.NET, architecture inspiration from slskd

- Test performance again for song and album searches (CPU and allocations, include the raw search collection phase + projection) on big queries (e.g. `love`)

### Later

- (breaking) Maybe use yaml for settings instead of our custom format, and improve structure.
