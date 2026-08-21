# Sockseek WebUI prototype

This directory is an intentionally lightweight UI/UX prototype for the upcoming Sockseek v4 daemon WebUI.

Its purpose is to explore **navigation, information hierarchy, density, and interaction design** before committing to the production frontend technology. The prototype may later be partly or completely rewritten (for example in Blazor).

## Technology

- **Svelte 5** for declarative, reactive components.
- **TypeScript** for UI and mock-data type safety.
- **Vite** for development and production builds.
- **npm** for now; switching to pnpm or Bun later should be straightforward.
- **openapi-typescript** to generate types from Sockseek's checked-in `docs/openapi.json`.
- No SvelteKit, frontend state library, real daemon connection, or SignalR integration yet.

## Setup

The prototype expects to live at `webui/` in the Sockseek repository, next to `docs/`:

```text
sockseek/
  docs/
    openapi.json
  webui/
```

From `webui/`:

```bash
npm install
npm run api:generate
npm run dev
```

Useful checks:

```bash
npm run check
npm run build
```

`npm run api:generate` writes `src/api/generated.ts`. Treat it as generated code rather than editing it manually.

## Current design direction

The prototype currently uses a **Carbon + Blue** palette: nearly neutral black/gray surfaces in dark mode, neutral gray surfaces in light mode, and a restrained blue accent. The accent is reserved mainly for focus, selection, progress, and unread state. Light and dark variants follow the system theme.

The current scale pass favors roughly 14 px primary row text, 12 px secondary metadata and form labels, 32–34 px form controls, and 24–26 px condition pills. The global search remains intentionally larger at 54 px.

The shell currently has seven destinations:

- **Dashboard**
- **Search**
- **Downloads**
- **Uploads**
- **Users**
- **Chat**
- **Settings**

Shares live as a subview of **Users** rather than as a separate top-level destination. Transfer history lives in Downloads/Uploads, and other historical information should generally remain close to the feature that produced it.

A global search field remains available in the header on every page. Pressing `/` focuses it and Enter submits the current query and returns to Search. A fixed-width **Album / Track** button switches result mode, while `•••` opens advanced search conditions. Applied conditions appear as removable pills in a focused overlay aligned to the query bar.

### Morphing search

Search supports both the raw Soulseek model and a structured Sockseek model:

- start with one raw query field;
- type `Artist - Album` or `Artist — Track` to split automatically;
- use **split** manually when desired;
- structured mode exposes separate artist and album/track values for Sockseek heuristics;
- the split fields use a subdued vertical divider rather than a textual dash;
- **merge** returns to a single raw query;
- after merging, ordinary edits do not immediately split the query again;
- removing the delimiter rearms automatic splitting;
- Backspace in an empty album/track field moves focus to artist;
- Backspace in an empty artist field merges back to the single field.


### Search configuration

The shared search configuration popover has two sections: **Conditions** and **Ranking**. Conditions are hard filters; Ranking mirrors the documented explicit `pref-*` controls and changes preference/order without filtering candidates out. The ranking surface intentionally omits album track-count, required-track-title, missing-metadata, and strict-album-quality controls because the README's explicit preferred-option help does not expose counterparts for those controls.

Common Conditions controls include formats, free min/max bitrate, exact sample rate, exact bit depth, metadata handling, strict artist matching, and peer allow/ban lists. Format shortcuts cover All, FLAC, MP3, OGG, OPUS, M4A, and WAV; **All** is active when no required format filter exists, while custom mode accepts comma-separated arbitrary formats. Track mode exposes title and duration-related controls. Album mode instead exposes album matching, album track-count limits, required track titles, and strict album quality.

Ranking exposes preferred formats, min/max bitrate, min/max sample rate, min/max bit depth, matching preferences, peer preference/downranking, and track length tolerance where applicable. Prototype ranking defaults follow the documented Sockseek defaults (`pref-format=mp3`, bitrate 200–2500, max sample rate 48 kHz, title/album matching, and 3-second track tolerance).

The Conditions UI treats sample rate and bit depth as exact values. A small adapter in `src/prototype/search-config.ts` demonstrates how those choices map to both the generated API's min and max fields, while Ranking maps to `preferredCond`.


## Search tab exploration

The Search tab now has persistent prototype state with two views: a newest-first Searches list and a projected Results view. Submitting the global search creates a search and opens its results; opening an existing search restores its Track or Album projection. The back button returns to Searches, and clicking Search in the sidebar while already viewing results does the same. Leaving Search and returning from another tab preserves the last Search subview.

Search result refinement is deliberately compact: text filtering, sorting, and a **Conditions** button share one row. Applied condition pills render only when needed and reserve no empty row when a search has no hard filters. The Conditions button opens the same `SearchConfigPanel` component used by the global search rather than a second ad-hoc UI; its Ranking tab is available there as well. Removing or changing required conditions filters the mock result projection immediately.

Track results show the full file path, lock state, size, length, and available bitrate/sample-rate/bit-depth metadata. Whole track rows are selectable. Album results show the full album directory path and a nested file list using paths relative to the album folder; clicking the album summary selects or clears the whole album, while individual files remain independently selectable. Adjacent results from the same peer share one collapsible peer header with free-slot state beside the peer name and compact upload/queue/file statistics on the right. Relevance sorting separates backend-marked preferred matches from other matches; speed, queue, and two-way size sorting use one combined result stream. Selection actions appear contextually rather than reserving a permanent toolbar.

Prototype searches now start with neutral required conditions so fixture results are visible until the user deliberately applies constraints. A fixed rounded-rectangle download action appears in the lower-right corner whenever any result items are selected, so long result lists do not require returning to the top. Checkboxes use a dark native-like treatment throughout the application when the OS requests dark mode, including indeterminate album selection.


## Users exploration

The Users destination has two subviews: **User** and **Shares**. While Users is active, the global search morphs into a username browser: it stays a single field, removes split/configuration controls, and repurposes the fixed result-mode button as **User / Shares**. The button chooses which request/view Enter opens; it does not immediately switch the current subview.

The User profile combines the distinct Soulseek concepts we will eventually request from user info/statistics/status calls: presence, optional profile picture and description, shared file/folder counts, average upload speed, lifetime upload count, slot count, queue depth, and free-slot state. Scenario fixtures deliberately cover missing pictures, missing descriptions, offline state, and long usernames. Usernames shown elsewhere in the prototype use one shared `UsernameLink` action: hovering underlines the name, and activation navigates directly to that peer's User profile without disturbing the originating page's state. The profile also exposes a Message action that opens or creates that user's private conversation in Chat.

Shares renders the full browsed directory tree rather than forcing it through the flat Album presentation. Folders and files are independently selectable, folder checkboxes represent all descendants with indeterminate state, folders can collapse, and filtering preserves matching paths and their ancestors. Total browse size is computed from the share tree and displayed only in the Shares view because Soulseek user statistics provide share counts but not aggregate shared bytes. Search Results and Shares reuse the same filter control and selection/download toolbar.

## Downloads exploration

Search and Downloads now share generic `FileItemCard` and `FolderItemCard` presentation primitives. Track is a file specialization and Album is a folder specialization in the domain/view-model layer rather than a separate visual implementation. Search composes selection/preference behavior around the primitives; Downloads composes transfer state, progress, speed, ETA, cancellation, and per-file transfer state around the same primitives.

The Downloads page is one chronological stream sorted by job creation time, newest first. Track and Album jobs are intentionally mixed in that stream; there is no separate Active/Recent split. Adjacent jobs from the same peer share the same reusable collapsible peer group used by Search, but peers are never regrouped globally because that would disturb chronological order. Queued, active, completed, failed, and cancelled jobs remain in place and are distinguished on the cards themselves.

## Uploads exploration

Soulseek uploads are individual file transfers, so the WebUI does not invent an Album object for them. Uploads are sorted by request time and first grouped into adjacent peer runs. Inside each peer run, adjacent transfers with the same normalized remote parent path are projected as one folder card when there are two or more files; a one-file run remains a file card. Matching peers or folders separated by other timeline items are not merged.

This projection uses the same `FileItemCard`, `FolderItemCard`, `PeerItemGroup`, and transfer cancellation affordances as Downloads. Folder size/progress/speed are derived from the child transfers. Folder cancellation is a UI bulk action over currently cancellable child transfers, while individual rows cancel by transfer id. Cancellability comes from the transfer DTO's `availableActions` rather than being inferred from a status string; the daemon exposes `POST /api/transfers/{transferId}/cancel` for queued or active uploads.


## Chat exploration

Chat is a first-class borderless workspace rather than a card inside the page. The left rail is split into **Rooms** first and **Users** second, with small add controls for joining/opening destinations. Both room and private-message threads are interactive prototype state: selecting a destination clears its unread badge, sending appends messages, leaving a room removes it from the rail, conversation history can be deleted, and a user can be locally blocked/unblocked. Usernames reuse the global `UsernameLink` behavior in the rail, thread header, and room messages, while message bodies reuse `LinkifiedText` for safe external links.

The composer is multiline. Enter inserts a newline; Ctrl+Enter (or Cmd+Enter) sends. It starts at two lines, automatically grows to a capped height, and sent messages preserve line breaks. The prototype structure follows the daemon's separate private-conversation and room APIs; blocking remains prototype state because the daemon models blocked usernames as peer-access settings rather than a chat-only action.

## Mock data and OpenAPI

The prototype does not need a live daemon, but its transfer fixtures satisfy the generated `TransferStateDto` contract. Scenarios compose these fixtures into situations that are useful for design:

- normal;
- busy;
- empty;
- offline;
- stress.

The scenario switcher remains deliberately visible as a prototype tool. It lets us inspect the same screens under different data pressure without building a fake daemon.

Generated API types are an **input**, not the UI architecture. UI-specific models can be introduced whenever the wire shape becomes inconvenient.

## Current project shape

```text
src/
  api/
    generated.ts
  components/
    AppShell.svelte
    GlobalSearch.svelte
    Icon.svelte
    ResultFilterControl.svelte
    SelectionToolbar.svelte
    UsernameLink.svelte
    SearchConditionPills.svelte
    SearchConfigPanel.svelte
    items/
      FileItemCard.svelte
      FolderItemCard.svelte
      PeerItemGroup.svelte
      TransferCancelButton.svelte
    PrototypeScenarioPicker.svelte
    Sidebar.svelte
  mock/
    fixtures/
      transfers.ts
    scenarios/
      index.ts
    types.ts
  pages/
    Dashboard.svelte
    Search.svelte
    Users.svelte
    Downloads.svelte
    Uploads.svelte
    Chat.svelte
    Settings.svelte
  prototype/
    icons.ts
    navigation.ts
    search.ts
    search-config.ts
    search-results.ts
    users.ts
    items.ts
    downloads.ts
    uploads.ts
    grouping.ts
    transfers.ts
  App.svelte
```

## Deliberately out of scope

- choosing the final production WebUI technology;
- real HTTP or SignalR integration;
- authentication;
- production hosting/packaging;
- a comprehensive routing or state-management system;
- a reusable design system before the UX is understood;
- treating the current prototype layouts as production-final.

The next iterations should continue to optimize for **learning what Sockseek should feel like**, not for preserving prototype code.

Applied search conditions appear in a focused overlay aligned to the query bar. Once engaged, the overlay remains open while interacting with the query controls, result-mode toggle, settings button, or settings panel; it closes on an outside click or Escape from the search controls. A source comment marks where future online metadata suggestions (for example MusicBrainz results) can be inserted beneath the pills once that workflow is supported.

## Dashboard exploration

The prototype now opens on a Dashboard tab. Its history range control is intentionally dashboard-wide rather than chart-only: switching between 24h, 7d, 30d, and 90d updates the transfer activity chart, the Peers/Content/Errors ranking data, and the Transfer summary figures together. Current-rate cards, Recent activity, and Daemon health remain live/current-state views.

The lower dashboard uses independent vertical columns so panels size to their own content. Ranked tables are explicitly full-width rather than relying on a generic `.table` class, avoiding CSS collisions and unused horizontal space.

- Dashboard activity curves use bounded Bezier smoothing so sample values stay exact without angular joins.
- The workspace itself owns vertical scrolling, so the scrollbar stays at the browser edge on every tab.
- Normal tab content is centered inside a shared 90rem maximum-width container, avoiding a large one-sided void on ultrawide/4K displays.
- The top search controls use the same centered container but are capped independently at 76rem, so the query bar remains comfortably scannable on very wide displays.

### Search configuration structure

The search configuration UI deliberately keeps the daemon's required-vs-ranking vocabulary in one declarative registry (`src/prototype/search-config-schema.ts`). Required and preferred labels live next to each other there, and conditions without an explicit documented `pref-*` counterpart have no ranking entry. Compound controls remain explicit Svelte rather than being forced through a generic form generator; shared presentation such as the format selector is factored into reusable components. The API adapter in `search-config.ts` stays explicit so its wire mapping is easy to audit against the generated OpenAPI types.
