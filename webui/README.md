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

Durable UX rules:

- keep surfaces compact but not cramped, with alignment and visual hierarchy doing more work than prose;
- avoid explanatory product copy for behavior or ordering that is already obvious from the interface;
- use blue for active/focus/selection state, green for success, red for failure, and neutral gray for queued/indeterminate state;
- preserve backend/order semantics when grouping: peer/user grouping is adjacent-only unless the backend explicitly returns a grouped object;
- prefer shared file/folder presentation primitives and typed adapters over parallel Track/Album/Upload implementations.

Shell destinations are declared once in `src/prototype/navigation.ts`. `Sidebar.svelte` renders either the primary or secondary placement from that shared registry, so adding, removing, or rearranging a destination should not require parallel shell markup.

The shell currently has seven destinations:

- **Dashboard**
- **Jobs**
- **Downloads**
- **Uploads**
- **Users**
- **Chat**
- **Settings**

Shares live as a subview of **Users** rather than as a separate top-level destination. Transfer history lives in Downloads/Uploads, and other historical information should generally remain close to the feature that produced it.

A global search field remains available in the header on every page. Pressing `/` focuses it and Enter submits the current query and opens its job in Jobs; submitting dismisses the focused overlay. The header field is an independent draft: opening an existing Job result or a user Profile/Shares resource must not rewrite its text. Switching between content search and Users still swaps the available search-type modes, and User/Shares navigation keeps that mode selector synchronized with the requested user resource. A shared icon mode picker sits at the left edge of the query bar for **Track Search / Album Search / Song Aggregate / Album Aggregate** (or **User / Shares** while browsing users); clicking it opens the explicit mode menu. `•••` opens advanced search conditions where applicable. Applied conditions appear as removable pills in a focused overlay aligned to the query bar.

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

The shared search configuration popover has two sections: **Filtering** and **Ranking**. Filtering contains hard conditions; Ranking mirrors the documented explicit `pref-*` controls and changes preference/order without filtering candidates out. The ranking surface intentionally omits album track-count, required-track-title, missing-metadata, and strict-album-quality controls because the README's explicit preferred-option help does not expose counterparts for those controls.

Structured music modes expose audio formats, free min/max bitrate, min/max sample rate, min/max bit depth, metadata handling, identity matching, and peer allow/ban lists. Track mode exposes title and duration-related controls. Album mode instead exposes album matching, album track-count limits, required track titles, and strict album quality. In **Album Search**, format and quality rules describe the suitability/coverage of the album's audio tracks; accepting a FLAC album does not discard an ancillary JPEG cover from that accepted album folder.

**File Search** deliberately uses a neutral, generic condition surface rather than inheriting the daemon's song-oriented defaults. Formats are arbitrary file extensions (with PDF/EPUB/ZIP/TXT/JPG/PNG shortcuts), and format/bitrate/sample-rate/bit-depth/peer conditions are evaluated on each file independently. A required `PDF` format therefore removes a sibling JPEG rather than treating the parent directory as a logical album. File Search does not currently expose artist/title/album matching, length tolerance, album structure, or the music-specific missing-metadata toggle; generic filename/folder phrase conditions should be modeled explicitly if added later rather than smuggled through Song/Album terminology.

Ranking exposes preferred formats, min/max bitrate, min/max sample rate, min/max bit depth, matching preferences, peer preference/downranking, and track length tolerance where applicable. Structured Track/Album ranking defaults follow the documented Sockseek defaults (`pref-format=mp3`, bitrate 200–2500, max sample rate 48 kHz, title/album matching, and 3-second track tolerance); File Search starts with no inherited ranking preferences.

Filtering and Ranking both expose sample-rate and bit-depth ranges directly, matching the generated API's min/max condition fields. The adapter in `src/prototype/search-config.ts` maps Filtering to `necessaryCond` and Ranking to `preferredCond`, while clearing identity/folder semantics for File Search.


## Jobs exploration

Jobs is the home for user-initiated daemon jobs. The current prototype renders search jobs only: a newest-first Jobs list and the selected search job's Results view. Search-job type labels are **File Search**, **Track Search**, **Album Search**, **Song Aggregate**, and **Album Aggregate** so file/folder transfer terminology stays separate from job semantics. File Search submits the daemon's raw free-text `SearchJob`; Track/Album Search submit structured SearchJob variants and the aggregate modes are alternate projections of those discovery jobs. Submitting the global search creates a job and opens its results; the back button returns to Jobs, and leaving/returning preserves the last Jobs subview.

Search result refinement is deliberately compact: text filtering, sorting, and a **Filtering** button share one row. Applied condition pills render only when needed and reserve no empty row when a search has no hard filters. The Filtering button opens the same `SearchConfigPanel` component used by the global search rather than a second ad-hoc UI; its Ranking tab is available there as well. Text, Filtering, Ranking, and Sort changes each request a new daemon-owned projection over the complete retained result set and reset result pagination; they are not client-side operations over pages already received. The mock adapter responds synchronously only because the prototype is unwired.

Track results show the full file path, lock state, size, length, and available bitrate/sample-rate/bit-depth metadata. Whole track rows are selectable. Album results show the full album directory path and a nested file list using paths relative to the album folder; clicking the album summary selects or clears the whole album, while individual files remain independently selectable. Album projection is intentionally logical-album-oriented: quality/format conditions are coverage rules over audio tracks, and ancillary accepted-folder files remain available for selection/download.

File Search instead projects one root unit per server-owned **peer + directory root**. Necessary conditions prune files individually before the directory tree is constructed; empty subfolders and roots disappear, while surviving descendants retain paths relative to the projected root. Root count/size are the count/bytes of all surviving descendants, not claims about the peer's complete remote tree. Subfolders are display/selection scopes rather than independent sort units: each gets its own checkbox, selects exactly the surviving descendants beneath it, and can show partial state when only some descendants are selected. Root **Relevance** follows the daemon rank of the best surviving descendant; folders and files are naturally ordered within each displayed tree level. File Search additionally offers Upload speed, Directory size, File count, and Directory name orderings; it deliberately omits queue-depth sorting. Root selection selects all currently projected descendants, and download requests use `RequestedMode.General` so generic files become `RemoteFileJob`s rather than inferred Song jobs.

Adjacent result units from the same peer share one collapsible peer header with free-slot state beside the peer name and compact upload/queue/result statistics on the right. Structured Track/Album views request relevance, speed, queue, and two-way size orderings; relevance also returns the daemon's preferred/other classification after applying Ranking. File Search keeps one relevance-ordered directory stream instead of inventing Preferred/Other directory tiers. Selection actions appear contextually rather than reserving a permanent toolbar.

Song Aggregate and Album Aggregate are **projections of discovery SearchJobs**, not submissions of the daemon's automation-oriented `AggregateJob` / `AlbumAggregateJob` types. Track Search and Song Aggregate both submit the structured track-search `SearchJob`; Album Search and Album Aggregate both submit the structured album-search `SearchJob`. The selected view then requests the corresponding file/folder or aggregate projection from that same retained search result set. Aggregate groups stay in daemon order (most distinct sharers first) and the root view renders only each group's first/relevance-best option. Each group is presented as one unified card: the inferred song/album name and artist are the primary scan identity, while the chosen peer's username, observed upload speed, and free-slot state sit in the same card above the representative file/folder contents. Aggregate views offer a right-aligned Select all control that becomes Deselect all once every currently visible group is selected; Song Aggregate representatives select as files, while Album Aggregate representatives keep normal folder/file selection so individual tracks remain selectable. The inferred card header is itself a selection target, matching the representative row, while nested username and Options controls retain their own actions. An **Options** action opens that group's alternatives in daemon relevance order; alternatives use the same peer speed/free-slot treatment, and the option card itself (or its explicit action) chooses a replacement. Choosing one selects that representative for the SearchJob's normal follow-up download flow. Option-modal child rows are display-only. Separate Aggregate/Album Aggregate jobs remain future automation job types for Jobs and are not created by the global interactive search bar.

Album search folders render the files already returned by the search. Full-folder retrieval is a separate, user-initiated augmentation: while it runs, the existing folder contents remain visible and the retrieval state is layered on top; newly discovered files can then extend that folder. Ordinary search results should not look as though retrieval is running. Retained historical folders whose authoritative child detail is no longer available remain summary-only rather than showing a knowingly partial history. Search results render every item returned by the loaded backend projection pages; **Load more results** exists only when the latest page returns another cursor.

Prototype searches now start with neutral required conditions so fixture results are visible until the user deliberately applies constraints. A fixed rounded-rectangle download action appears in the lower-right corner whenever any result items are selected, so long result lists do not require returning to the top. Checkboxes use a dark native-like treatment throughout the application when the OS requests dark mode, including indeterminate album selection.


## Users exploration

The Users destination has two subviews: **User** and **Shares**. While Users is active, the global search morphs into a username browser: it stays a single field, removes split/configuration controls, and uses the shared mode picker for **User / Shares** at the left edge of the query bar. Clicking the icon opens the explicit mode menu. The selected mode chooses which request/view Enter opens; it does not immediately switch the current subview.

The User profile combines the distinct Soulseek concepts we will eventually request from user info/statistics/status calls: presence, optional profile picture and description, shared file/folder counts, average upload speed, lifetime upload count, slot count, queue depth, and free-slot state. Scenario fixtures deliberately cover missing pictures, missing descriptions, offline state, and long usernames. Usernames shown elsewhere in the prototype use one shared `UsernameLink` action: hovering underlines the name, and activation opens a compact action menu for **Profile**, **Shares**, or **Message** without forcing a profile navigation first. The profile also exposes a Message action that opens or creates that user's private conversation in Chat.

Shares renders the browsed directory tree rather than forcing it through the flat Album presentation. Folders and files are independently selectable, folder checkboxes represent all descendants with indeterminate state, and folders can collapse. The filter bar requests a new daemon-owned mixed-tree projection across the complete browse artifact before pagination; the response preserves matching paths and ancestor context, supplies the total matching-file count, and has its own cursor. It does not filter only the currently loaded tree page. Total browse size is computed by the daemon for the browse artifact and displayed only in the Shares view because Soulseek user statistics provide share counts but not aggregate shared bytes. Search Results and Shares reuse the same filter control and selection/download toolbar.

Opening/submitting the Shares view should itself acquire or reacquire the user's browse data when needed; an expired browse artifact must not require a second explicit refresh action from the user.

## Downloads exploration

Downloads and Uploads are raw transfer views built from the same generic file/folder presentation model. `TransferTimeline`, `FileItemCard`, `FolderItemCard`, `PeerItemGroup`, transfer status/progress, contextual cancel/remove actions, and page limiting are shared. Optional file metadata is presentation data rather than a download-only assumption: cards render it when present and collapse cleanly when absent. Logical Song/Album job types belong in Jobs, not in transfer rendering.

Downloads is one newest-first chronological file/folder stream. Adjacent transfers from the same peer share a collapsible peer group but are never regrouped globally. Folder cards are a transfer presentation and must not require the renderer to know whether the originating job was an Album, remote directory, or another future job type. Queued, active, completed, failed, and cancelled transfers remain in place and are distinguished on the cards themselves.

## Uploads exploration

Uploads follows the same transfer timeline and rendering rules as Downloads. Soulseek exposes upload work as individual file transfers, so adjacent same-peer transfers with the same normalized remote parent path may be projected into a folder card; a one-file run remains a file. Matching peers or folders separated by other timeline items are not merged.

Folder size/progress/speed are derived from child transfers. Folder cancellation is a UI bulk action over cancellable children, while individual rows cancel by transfer id. Current upload DTOs do not carry audio attributes, so Uploads normally omits that metadata; the shared transfer model already accepts it if the daemon later exposes generic transfer file metadata.


## Chat exploration

Chat is a first-class borderless workspace rather than a card inside the page. The left rail is split into **Rooms** first and **Users** second, with small add controls for joining/opening destinations. Both room and private-message threads are interactive prototype state: selecting a destination clears its unread badge, sending appends messages, leaving a room removes it from the rail, deleting a private chat removes that conversation from the rail, and a user can be locally blocked/unblocked. Usernames reuse the global `UsernameLink` behavior in the thread header and room messages, while conversation-rail usernames remain plain text and message bodies reuse `LinkifiedText` for safe external links.

The composer is multiline. Enter inserts a newline; Ctrl+Enter (or Cmd+Enter) sends. It starts at one line, automatically grows to a capped height, and sent messages preserve line breaks. The prototype structure follows the daemon's separate private-conversation and room APIs; blocking remains prototype state because the daemon models blocked usernames as peer-access settings rather than a chat-only action.

## Mock data and OpenAPI

The prototype does not need a live daemon, but its transfer fixtures satisfy the generated `TransferStateDto` contract. Scenarios compose these fixtures into situations that are useful for design:

- normal;
- busy;
- loading;
- empty;
- offline;
- stress.

The scenario switcher remains deliberately visible as a prototype tool. It lets us inspect the same screens under different data pressure without building a fake daemon. The **Loading** scenario specifically keeps several resources in flight so initial no-results search, profile, shares, and transfer-loading treatments remain easy to review.

Pagination/page-size limits in the prototype are intentionally very small so pagination, scrolling, and partial-loading interactions are easy to exercise. **Production limits are expected to be much larger everywhere**; the prototype values are not product recommendations.

Generated API types are an **input**, not the UI architecture. UI-specific models can be introduced whenever the wire shape becomes inconvenient.

## Current project shape

```text
src/
  api/
    generated.ts
  components/
    AppShell.svelte
    GlobalSearch.svelte
    ModeIconToggle.svelte
    Icon.svelte
    LinkifiedText.svelte
    LoadMoreButton.svelte
    MutationStatus.svelte
    ResourceStateNotice.svelte
    ResultFilterControl.svelte
    SelectionToolbar.svelte
    TransferBulkActions.svelte
    TransferTimeline.svelte
    UsernameLink.svelte
    SearchConditionPills.svelte
    SearchConfigPanel.svelte
    items/
      FileItemCard.svelte
      FolderItemCard.svelte
      PeerItemGroup.svelte
      TransferItemActionButton.svelte
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
    Jobs.svelte
    Users.svelte
    Downloads.svelte
    Uploads.svelte
    Chat.svelte
    Settings.svelte
  prototype/
    icons.ts
    navigation.ts
    backend-contracts.ts
    resource-state.ts
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
- a production routing or state-management framework;
- a reusable design system before the UX is understood;
- treating the current prototype layouts as production-final.

The next iterations should continue to optimize for **learning what Sockseek should feel like**, not for preserving prototype code.

The global search-type control is icon-only and sits at the left edge of the query bar, replacing the generic search/user glyph. Content search uses Track Search/Album Search/Song Aggregate/Album Aggregate and Users uses User/Shares through the same `ModeIconToggle` component. Clicking the icon only opens the checked mode menu; it must not engage an otherwise unfocused query bar, and when the query is already focused the picker must not steal that focus. The picker menu renders above the focused-condition overlay. Modes are not cycled implicitly. The split/merge control remains embedded in the query bar.

Applied search conditions appear in a focused overlay aligned to the query bar. Once engaged, the overlay remains open while interacting with the query controls, result-mode toggle, settings button, or settings panel; it closes on an outside click or Escape from the search controls. A source comment marks where future online metadata suggestions (for example MusicBrainz results) can be inserted beneath the pills once that workflow is supported.

## Dashboard exploration

The prototype now opens on a Dashboard tab. Its history range control is intentionally dashboard-wide rather than chart-only: switching between 24h, 7d, 30d, and 90d updates the transfer activity chart, the Downloads/Uploads/Content/Errors ranking data, and the Transfer summary figures together. Current-rate cards and Daemon health remain live/current-state views. The lower dashboard gives the ranking projection the full content width, then pairs the range summary with current daemon health rather than reserving space for a generic activity feed.

The lower dashboard gives the ranking panel the full row, then places Transfer summary and Daemon health together in a responsive bottom grid. Ranked tables are explicitly full-width rather than relying on a generic `.table` class, avoiding CSS collisions and unused horizontal space. Download and upload user rankings are deliberately directional: Downloads ranks remote users we downloaded from, while Uploads ranks remote users we uploaded to. All four ranking tabs retain the top 20 rows for the selected history range and scroll inside the ranking panel.

- Dashboard activity curves use bounded Bezier smoothing so sample values stay exact without angular joins.
- The workspace itself owns vertical scrolling, so the scrollbar stays at the browser edge on every tab.
- Normal tab content is centered inside a shared 90rem maximum-width container, avoiding a large one-sided void on ultrawide/4K displays.
- The top search controls use the same centered container but are capped independently at 76rem, so the query bar remains comfortably scannable on very wide displays.

### Search configuration structure

The search configuration UI deliberately keeps the daemon's required-vs-ranking vocabulary in one declarative registry (`src/prototype/search-config-schema.ts`). Required and preferred labels live next to each other there, and conditions without an explicit documented `pref-*` counterpart have no ranking entry. Compound controls remain explicit Svelte rather than being forced through a generic form generator; shared presentation such as the format selector is factored into reusable components. The API adapter in `search-config.ts` stays explicit so its wire mapping is easy to audit against the generated OpenAPI types.


## Backend contract discipline

Current OpenAPI-generated DTOs are used at daemon-facing fixture boundaries; UI-only grouping and presentation remain view models. When the prototype needs data or mutations the daemon does not expose cleanly, define that assumption once in `src/prototype/backend-contracts.ts` rather than reconstructing it ad hoc in components. This includes the proposed search reprojection and mixed share-tree query requests: their mock adapters deliberately apply request semantics before pagination. The backend audit remains in `DAEMON-AUDIT.md`; the README records only durable UI and architecture decisions.

Resource views should keep loading, empty, unavailable, offline, and terminal states distinct without adding explanatory product copy when the state is already visually obvious. Search reruns follow immutable daemon-job semantics while replacing the prior run in the same logical UI slot. New private-chat targets remain frontend drafts until the first accepted send.

## Prototype URL/history behavior

Top-level destinations now synchronize with the browser pathname (`/dashboard`, `/jobs`, `/downloads`, `/uploads`, `/users/...`, `/chat`, `/settings`) using the native History API. Job detail views use `/jobs/{id}`. User profile/share subviews use `/users/{username}` and `/users/{username}/shares`. `popstate` restores the matching prototype view so browser Back/Forward works without a routing dependency. Dynamically created mock jobs remain in-memory prototype state, so a hard reload of a newly generated `/jobs/{id}` that is not one of the built-in fixtures falls back to `/jobs`.

Search Results and Shares no longer reserve a select-visible toolbar row. Selection remains per item/folder, and selected items produce a floating Download action with a neighboring Deselect all control. Downloads and Uploads share `TransferBulkActions` for remove-completed and scoped bulk cancellation, while individual terminal transfer entries reuse the same contextual action slot with a trash icon instead of a cancel icon.
