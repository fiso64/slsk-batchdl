## TODO

### v4.0

- Implement soulseek client features (Look how slskd does it for a start):
    - [x] Sharing / Uploads
    - [x] Chats (private DMs & public chatrooms, notification API;
      independent-client release qualification remains tracked in the design)
    - [x] User browsing (user description, profile picture, shares)

- Create a webui using the v4 live-state client
    - All the usual functions of a soulseek client
    - `SockseekLiveClient` and `DaemonClientStore` provide daemon-wide and workflow-scoped snapshot hydration, compact SignalR deltas, continuity recovery, paged history, and shared local/remote state reduction.
    - The CLI exposes daemon-wide monitoring as an orthogonal `--monitor` option. Input is optional, and an optional submitted workflow appears alongside all other active daemon work in the existing live UI.
    - Keep SignalR as the primary live-update transport. HTTP snapshots remain for hydration and recovery; paginated endpoints remain for retained history.
    - Keep replicated state and best-effort activity separate. A future GUI must reconstruct current state without activity replay.

- Secure API authentication, user+pass webui login.

- Rethink README presentation when daemon mode becomes the primary feature, reorganize.
    - Acknowledgements section: Soulseek.NET, architecture inspiration from slskd

- Every instance of `TODO [V4]`

- Test performance again for song and album searches (CPU and allocations, include the raw search collection phase + projection) on big queries (e.g. `love`)

### Maybe v4.0

- Wishlists with granular per-item conditions which can optionally auto-download matching results. This would be similar to the existing list.txt input, run periodically on a schedule and with a nice GUI. Since list files support any other input, you could also use it to sync e.g., a YouTube playlist.
- A "smart" search bar hooked up to a music database (like Musicbrainz) with a dropdown showing music suggestions on key press. Selecting a result would search for its canonical name and album track count.

### Maybe Later

- [Needs discussion] Shared user playlists: Peer A could opt-in to making some of their playlists playable. Peer B would browse peer A, then Sockseek would detect the shared playlists and expose them in the UI with a builtin player.
- (breaking) Maybe use yaml for settings instead of our custom format, and improve structure.
