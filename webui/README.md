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

The shell connection footer keeps daemon transport reachability separate from Soulseek client state. The Daemon row reports the frontend-observed transport state (`Connected` / `Offline`), while the Soulseek row renders the daemon's `SoulseekClientStatusDto.Flags` in order and only humanizes enum token boundaries (for example `LoggedIn` → `Logged In`). It does not collapse those flags into a generic `ready` label or infer that a reachable daemon is `local`.

A global search field remains available in the header on every page. Pressing `/` focuses it and Enter submits the current query and opens its job in Jobs; submitting dismisses the focused overlay. The header field is an independent draft: opening an existing Job result or a user Profile/Shares resource must not rewrite its text. Switching between content search and Users still swaps the available search-type modes, and User/Shares navigation keeps that mode selector synchronized with the requested user resource. A shared icon mode picker sits at the left edge of the query bar for **File Search / Track Search / Album Search / Song Aggregate / Album Aggregate** (or **User / Shares** while browsing users); clicking it opens the explicit mode menu. The sliders button opens advanced search conditions where applicable. Applied conditions appear as removable pills in a focused overlay aligned to the query bar.

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

The shared search configuration popover has two search sections: **Filtering** and **Ranking**. Filtering contains hard conditions; Ranking mirrors the documented explicit `pref-*` controls and changes preference/order without filtering candidates out. Dense File/Audio quality spans the available width, while the remaining cards use two stable semantic columns rather than free auto-packing: **Matching**, **Album structure**, and **Aggregate grouping** belong to the primary/left column; **Peers** belongs to the secondary/right column. Each column is its own vertical stack, so a tall card in one column does not create an empty shelf beneath a shorter card in the other, and switching Filtering/Ranking does not make Peers jump between sides. The two column stacks collapse as whole units when their actual container is too narrow. Compact field pairs remain two-up inside each section where useful, and format choices wrap to their actual container width. The standalone popover is about 760px wide when space allows, keeps a viewport edge gap, and constrains its own height to the space around its trigger so long configurations scroll inside the surface rather than crossing the screen edge. Result-page configuration uses outside-click dismissal rather than a full-page transparent backdrop, so the underlying results viewport remains normally wheel-scrollable wherever the pointer is outside the panel. The ranking surface intentionally omits album track-count, required-track-title, missing-metadata, and strict-album-quality controls because the README's explicit preferred-option help does not expose counterparts for those controls. Automatic-job creation reuses the same Filtering and Ranking content and keeps **Download** as a third, separate submission-settings concept rather than mixing output/skip behavior into search semantics. In New Job, all three are independent expandable settings panels in the creation workspace, using the same shared option content instead of a secondary Job options modal. The New Job shell keeps a stable modal height while panel content scrolls inside the workspace; Review/Start remain in a fixed bottom action footer, and customized disclosure panels use the same small blue modified-state dot as other option controls.

Structured music modes expose audio formats, free min/max bitrate, min/max sample rate, min/max bit depth, metadata handling, identity matching, and peer allow/ban lists. Track mode exposes title and duration-related controls. Album mode instead exposes album matching, album track-count limits, required track titles, and strict album quality. Peer allow/ban controls are a normal compact card rather than a forced full-width region; at desktop widths they can share a row with another compact section, and their two inputs stack within that card. Example usernames are placeholders, not apparent configured values. In **Album Search**, format and quality rules describe the suitability/coverage of the album's audio tracks; accepting a FLAC album does not discard an ancillary JPEG cover from that accepted album folder.
Album-folder results also expose the daemon's explicit **retrieve full folder** follow-up without adding another text/action column: when a folder is not fully retrieved, a compact `+` sits immediately beside its file-count stack and its tooltip explains that the operation loads files the search may have omitted. The same control belongs to the concrete folder candidate in normal Album Search, Album Aggregate representatives, and Album Aggregate options. While retrieval runs it becomes a spinner; after refetch confirms `IsFullyRetrieved`, it becomes a passive completion mark and any newly discovered files appear in the existing folder card. If the user had selected the entire folder, newly discovered files join that selection; individual-file selections remain individual.

**File Search** deliberately uses a neutral, generic condition surface rather than inheriting the daemon's song-oriented defaults. Formats are arbitrary file extensions (with PDF/EPUB/ZIP/TXT/JPG/PNG shortcuts), and format/bitrate/sample-rate/bit-depth/peer conditions are evaluated on each file independently. A required `PDF` format therefore removes a sibling JPEG rather than treating the parent directory as a logical album. File Search does not currently expose artist/title/album matching, length tolerance, album structure, or the music-specific missing-metadata toggle; generic filename/folder phrase conditions should be modeled explicitly if added later rather than smuggled through Song/Album terminology.

Ranking exposes preferred formats, min/max bitrate, min/max sample rate, min/max bit depth, matching preferences, peer preference/downranking, and track length tolerance where applicable. Structured Track/Album ranking defaults follow the documented Sockseek defaults (`pref-format=mp3`, bitrate 200–2500, max sample rate 48 kHz, title/album matching, and 3-second track tolerance); File Search starts with no inherited ranking preferences.

Filtering and Ranking both expose sample-rate and bit-depth ranges directly, matching the generated API's min/max condition fields. The adapter in `src/prototype/search-config.ts` maps Filtering to `necessaryCond` and Ranking to `preferredCond`, while clearing identity/folder semantics for File Search. Aggregate discovery additionally exposes **Minimum sharers** and grouping-length tolerance in Filtering; minimum sharers is initialized to the daemon default `2`, so it appears immediately as the same removable condition pill used by other hard filters. Removing that pill clears the threshold in the UI; the submission adapter sends the daemon's neutral aggregate value `1` so single-sharer groups are actually included rather than silently inheriting the daemon default `2`.

**Download options** are a separate shared submission concept used by automatic jobs and by the floating Download action on search results. The first surface contains Skip existing, optional full-album-folder enrichment, output-directory and name-format overrides, plus playlist creation where the submission is collection/list-shaped. Search-result selections open the same compact panel immediately beside Download; per-download options are sent on the follow-up request rather than mutating the discovery SearchJob. Blank output/name-format fields inherit daemon configuration, and the positive “Load full album folder” toggle maps to the daemon's inverse `NoBrowseFolder` setting.


## Jobs exploration

Jobs is the home for all user-initiated daemon jobs. The newest-first list mixes discovery SearchJobs with automatic **Song**, **Album**, **Extract/Import**, and **Job List** work in one shared row grammar; exact **Remote File**, **Remote Directory**, and auxiliary **Retrieve Folder** jobs remain supported as navigable detail nodes when they occur inside a submission. Each root-list row presents its specific job type as the same compact icon + label badge on the secondary line before its timestamp, so **Track Search**, **Album Search**, automatic **Song/Album**, and source-specific imports remain easy to distinguish without grouping, reordering, or row-level highlighting. Search badges use only a restrained accent treatment; imports and automatic jobs retain neutral badge treatments. Search-job type labels remain **File Search**, **Track Search**, **Album Search**, **Song Aggregate**, and **Album Aggregate** so discovery projections stay distinct from automatic download jobs. Submitting the global search still creates a SearchJob and opens Results; automatic work is created explicitly through **New job**. Result selection is ephemeral UI state owned above the Jobs page so switching to another top-level tab and back preserves the active job's selected files/folders; changing job identity or scenario clears that selection. Every list row exposes one lifecycle action in the same place: cancellable jobs cancel, while terminal jobs are removed/archived from history.

**New job** is a modal creation surface rather than an "automatic" checkbox on discovery search. The initial direct choices are **Song** and **Album**; their direct-entry fields open blank with placeholder guidance, only the song/album title is required, and artist remains an optional identity hint. Source URL/link inputs also open blank and use placeholders rather than fixture-looking example values, so choosing an extractor never appears to submit an existing source by default. Automatic Aggregate/AlbumAggregate creation is deliberately not exposed yet because their intended interactive workflow needs separate design. Extract creation is presented by explicit source type—**Spotify**, **YouTube**, **Bandcamp**, **MusicBrainz**, and **Soulseek**—plus separate **CSV file** and **List file** upload choices. Spotify exposes playlist/album URL, liked songs, and liked albums as source-specific inputs; CSV normally relies on header auto-detection, with the daemon's configurable artist/title/album/length/description/YouTube-ID/track-count column names available behind an optional Column mapping control. Collection-capable extractors also expose a compact **Import options** disclosure for item limit, offset, and an **Upgrade song items to albums** checkbox; reverse-order extraction is intentionally not surfaced. The generic free-text extractor is intentionally not exposed as a first-class WebUI creation option. These are creation affordances, not new runtime job kinds: they still submit Extract jobs with an explicit input type, which gives each extractor a stable place for source-specific options without duplicating job-detail/navigation code. **Filtering**, **Ranking**, and **Download** are three additional expandable panels available for every creation path, including extract/import roots; they reuse the shared search/download option content and keep Review and Start in the same creation workspace.

Review is optional. **Start** submits the configured work immediately, while **Review** resolves extraction/preprocessing recursively before submission so large or nested imports can be inspected and selectively deselected. Preview deliberately stops before Soulseek candidate discovery: a Spotify playlist can expand into its Song jobs, while a direct Song preview remains one logical Song job rather than pretending to know its eventual peer/file. Preview/selection is modeled as an ephemeral server-owned plan, not as fake terminal runtime jobs or job history; editing preview rows is intentionally out of scope for the first implementation. The latest daemon direction reinforces that boundary: ordinary runtime Job detail remains fixed-size and direct children are traversed through `/api/jobs?parentJobId=...`, while review belongs to a separate short-lived Job Preview resource. The prototype mirrors that separation internally: `job-preview.ts` models review-plan state, `job-preview-runtime.ts` is only the local mock adapter that turns an approved preview selection into runtime fixtures, and `jobs.ts` owns runtime job records/navigation. A production port should keep those domains separate rather than folding preview graphs into runtime job DTOs.

Job detail uses one shared shell with type-specific bodies rather than independent page layouts. Song and Album center the selected File/Folder presentation used elsewhere; Job List uses consistent child rows whose primary line is the job identity, whose secondary line carries type/time context, and whose right-side metadata keeps statistics and lifecycle state together; measurable active progress is the only extra row. Active/failed Extract jobs show their source/input, but once extraction has completed the UI silently presents its semantic `ResultJobId` in place of the wrapper so a CSV/Spotify import opens directly to the useful result. Nested child navigation is one level at a time: the Back control returns to the semantic parent, and the single-line ancestry/type kicker exposes the same hierarchy without moving the page title between job kinds. `ParentJobId` remains execution hierarchy while Extract-result flattening is presentation-only. Manual `AwaitingSelection` behavior is not surfaced in this first automatic-job pass.

Search result refinement is deliberately compact: text filtering, sorting, and a **Filtering** button share one row. Applied condition pills render only when needed and reserve no empty row when a search has no hard filters. The Filtering button opens the same `SearchConfigPanel` component used by the global search rather than a second ad-hoc UI; its Ranking tab is available there as well. Text, Filtering, Ranking, and Sort changes each request a new daemon-owned projection over the complete retained result set and reset result pagination; they are not client-side operations over pages already received. The mock adapter responds synchronously only because the prototype is unwired.

Track results show the full file path, lock state, size, length, and available bitrate/sample-rate/bit-depth metadata. Whole track rows are selectable. Album results show the full album directory path and a nested file list using paths relative to the album folder; clicking the album summary selects or clears the whole album, while individual files remain independently selectable. Album projection is intentionally logical-album-oriented: quality/format conditions are coverage rules over audio tracks, and ancillary accepted-folder files remain available for selection/download.

File Search instead projects one root unit per server-owned **peer + directory root**. Necessary conditions prune files individually before the directory tree is constructed; empty subfolders and roots disappear, while surviving descendants retain paths relative to the projected root. Root count/size are the count/bytes of all surviving descendants, not claims about the peer's complete remote tree. Subfolders are display/selection scopes rather than independent sort units: each gets its own checkbox, selects exactly the surviving descendants beneath it, and can show partial state when only some descendants are selected. The root directory uses the same compact `+` full-folder control as album results so a user can browse that concrete peer directory for additional files omitted by the search; newly discovered files are still subjected to File Search's per-file projection rules. Root **Relevance** follows the daemon rank of the best surviving descendant; folders and files are naturally ordered within each displayed tree level. File Search additionally offers Upload speed, Directory size, File count, and Directory name orderings; it deliberately omits queue-depth sorting. Root selection selects all currently projected descendants, and download requests use `RequestedMode.General` so generic files become `RemoteFileJob`s rather than inferred Song jobs.

Adjacent result units from the same peer share one collapsible peer header with free-slot state beside the peer name and compact upload/queue/result statistics on the right. Structured Track/Album views request relevance, speed, queue, and two-way size orderings; relevance also returns the daemon's preferred/other classification after applying Ranking. File Search keeps one relevance-ordered directory stream instead of inventing Preferred/Other directory tiers. Selection actions appear contextually rather than reserving a permanent toolbar.

Song Aggregate and Album Aggregate are **projections of discovery SearchJobs**, not submissions of the daemon's automation-oriented `AggregateJob` / `AlbumAggregateJob` types. Track Search and Song Aggregate both submit the structured track-search `SearchJob`; Album Search and Album Aggregate both submit the structured album-search `SearchJob`. The selected view then requests the corresponding file/folder or aggregate projection from that same retained search result set. Aggregate groups stay in daemon order (most distinct sharers first) and the root view renders only each group's first/relevance-best option. Each group is presented as one unified card: the inferred song/album name and artist are the primary scan identity, while the chosen peer's username, observed upload speed, and free-slot state sit in the same card above the representative file/folder contents. Aggregate views offer a right-aligned Select all control that becomes Deselect all once every currently visible group is selected; Song Aggregate representatives select as files, while Album Aggregate representatives keep normal folder/file selection so individual tracks remain selectable. The inferred card header is itself a selection target, matching the representative row, while nested username and Options controls retain their own actions. An **Options** action opens that group's alternatives in daemon relevance order; alternatives use the same peer speed/free-slot treatment, and the option card itself (or its explicit action) chooses a replacement. Root representatives and option alternatives use the same shared `FolderItemCard` header grammar—including file count, full-folder retrieval control, then size—without modal-specific column reordering. Choosing one selects that representative for the SearchJob's normal follow-up download flow. Option-modal child rows are display-only. The similarly named automatic Aggregate/AlbumAggregate daemon jobs are not exposed by New Job yet; the global interactive aggregate modes remain discovery SearchJob projections.

Album search folders render the files already returned by the search. Full-folder retrieval is a separate, user-initiated augmentation: while it runs, the existing folder contents remain visible and the retrieval state is layered on top; newly discovered files can then extend that folder. Ordinary search results should not look as though retrieval is running. Retained historical folders whose authoritative child detail is no longer available remain summary-only rather than showing a knowingly partial history. Search results render every item returned by the loaded backend projection pages; **Load more results** exists only when the latest page returns another cursor.

Prototype searches now start with neutral required conditions so fixture results are visible until the user deliberately applies constraints. Built-in Track Search and Album Search fixtures deliberately include one or two candidates that satisfy their saved Ranking defaults, so the Preferred/Other tiering is visible without first editing the search; those tiers are still recomputed from the real prototype ranking adapter rather than hard-coded presentation flags. A fixed rounded-rectangle download action appears in the lower-right corner whenever any result items are selected, so long result lists do not require returning to the top. Checkboxes use a dark native-like treatment throughout the application when the OS requests dark mode, including indeterminate album selection.


## Users exploration

The Users destination has two subviews: **User** and **Shares**. While Users is active, the global search morphs into a username browser: it stays a single field, removes split/configuration controls, and uses the shared mode picker for **User / Shares** at the left edge of the query bar. Clicking the icon opens the explicit mode menu. The selected mode chooses which request/view Enter opens; it does not immediately switch the current subview.

The User profile combines the distinct Soulseek concepts we will eventually request from user info/statistics/status calls: presence, optional profile picture and description, shared file/folder counts, average upload speed, lifetime upload count, slot count, queue depth, and free-slot state. The profile keeps the broad three-part layout: a prominent profile picture, identity/description/actions, and a separate compact Upload capacity card, with the four long-lived sharing statistics in the row below. Scenario fixtures deliberately cover missing pictures, missing descriptions, offline state, and long usernames. Usernames shown elsewhere in the prototype use one shared `UsernameLink` action: hovering underlines the name, and activation opens a compact action menu for **Profile**, **Shares**, or **Message** without forcing a profile navigation first. Popup action menus use the shared viewport-aware anchored-menu positioning helper, which clamps horizontally and flips above the trigger when needed so chat, user, mode-picker, and transfer-scope menus stay on-screen near viewport edges. The profile also exposes a Message action that opens or creates that user's private conversation in Chat.

Shares renders the browsed directory tree rather than forcing it through the flat Album presentation. Folders and files are independently selectable, folder checkboxes stay in the left selection column and represent all descendants with indeterminate state, and the entire remainder of each folder row is one full-width expand/collapse target rather than another selection target. Only depth-0/root folders start expanded; nested folders start collapsed so large shares open at a useful overview level. File glyphs use the same shared extension/filename classifier as transfer-folder rows: an explicit extension is preferred when available and the filename/path is the fallback. Shares deliberately keeps both audio and generic file glyphs neutral gray, while transfer folders may accent audio glyphs. The filter bar requests a new daemon-owned mixed-tree projection across the complete browse artifact before pagination; the response preserves matching paths and ancestor context, supplies the total matching-file count, and has its own cursor. It does not filter only the currently loaded tree page. Total browse size is computed by the daemon for the browse artifact and displayed only in the Shares view because Soulseek user statistics provide share counts but not aggregate shared bytes. Search Results and Shares reuse the same filter control and selection/download toolbar.

Opening/submitting the Shares view should itself acquire or reacquire the user's browse data when needed; an expired browse artifact must not require a second explicit refresh action from the user.

## Downloads exploration

Downloads and Uploads are raw transfer views built from the same generic file/folder presentation model. `TransferTimeline`, `FileItemCard`, `FolderItemCard`, `PeerItemGroup`, transfer status/progress, contextual cancel/remove actions, and page limiting are shared. Optional file metadata is presentation data rather than a download-only assumption: cards render it when present and collapse cleanly when absent. Logical Song/Album job types belong in Jobs, not in transfer rendering.

Downloads is one newest-first chronological file/folder stream. Adjacent transfers from the same peer share a collapsible peer group but are never regrouped globally. Repeating peer-header metadata uses fixed right-side stat columns so speed, queue/free-slot state, and result counts remain scannable even when usernames have very different lengths; the group count stays visually quieter than the child card metadata beneath it. Folder cards are a transfer presentation and must not require the renderer to know whether the originating job was an Album, remote directory, or another future job type. Transfer-folder summaries stay to two lines: the folder name is the flexible left identity on the first line while count, size, status, and actions form one evenly spaced right-side group, with the lifecycle label after the properties; the second line keeps the path flexible on the left with only the transfer age on the right. Single-file transfer cards use the same compact hierarchy: filename on the left, available audio/length/size metadata followed by lifecycle state and actions on the right, then path plus transfer age on the second line. Completion is conveyed by transfer progress/state rather than repeated in either header, and progress remains in the band immediately below. Inside transfer folders, the left row icon describes file type rather than lifecycle state. File-type classification is centralized in `prototype/file-types.ts`: explicit extension metadata wins when available, with filename/path as the fallback, and known audio extensions may provide the track glyph without inventing audio attributes. Shares consumes the same classifier with different styling rather than maintaining its own extension table. File properties keep stable columns: duration precedes size so size remains the final fundamental file property, and non-audio rows leave the unavailable duration slot empty rather than shifting size into another column. Active progress keeps percentage, bar, and speed/ETA together in one right-side strip. The common Complete, active Downloading/Uploading, and Queued states terminate that strip with the compact circular state icons, while exceptional states such as Failed or Cancelled remain explicit text labels. Queued, active, completed, failed, and cancelled transfers remain in place and are distinguished on the cards themselves.

## Uploads exploration

Uploads follows the same transfer timeline and rendering rules as Downloads. Soulseek exposes upload work as individual file transfers, so adjacent same-peer transfers with the same normalized remote parent path may be projected into a folder card; a one-file run remains a file. Matching peers or folders separated by other timeline items are not merged.

Folder size/progress/speed are derived from child transfers. Folder cancellation is a UI bulk action over cancellable children, while individual rows cancel by transfer id. Current upload DTOs do not carry audio attributes, so Uploads omits bitrate/sample-rate/length metadata; file-type icons can still classify known audio extensions from the path. The shared transfer model already accepts richer metadata if the daemon later exposes generic transfer file metadata.


## Chat exploration

Chat is a first-class borderless workspace rather than a card inside the page. The left rail is split into **Rooms** first and **Users** second, with small add controls for joining/opening destinations. Short conversations top-align instead of stretching a large empty gap above a bottom-anchored message stack, while longer threads continue to use the available viewport naturally. Both room and private-message threads are interactive prototype state: selecting a destination clears its unread badge, sending appends messages, leaving a room removes it from the rail, deleting a private chat removes that conversation from the rail, and a user can be locally blocked/unblocked. Usernames reuse the global `UsernameLink` behavior in the thread header and room messages, while conversation-rail usernames remain plain text and message bodies reuse `LinkifiedText` for safe external links.

The composer is multiline and sits on a subtly elevated/tinted interaction surface so it remains distinct from the message log without becoming a separate card. Enter inserts a newline; Ctrl+Enter (or Cmd+Enter) sends. It starts at one line, automatically grows to a capped height, and sent messages preserve line breaks. The prototype structure follows the daemon's separate private-conversation and room APIs; blocking remains prototype state because the daemon models blocked usernames as peer-access settings rather than a chat-only action.

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
    SearchConfigPanel.svelte
    SelectionToolbar.svelte
    TransferBulkActions.svelte
    TransferTimeline.svelte
    ResourceStateNotice.svelte
    UsernameLink.svelte
    jobs/
      AutomaticJobDetail.svelte
      JobCompactRow.svelte
      JobPreviewTree.svelte
      JobsHistoryList.svelte
      JobTypeBadge.svelte
      NewJobComposer.svelte
    items/
      FileItemCard.svelte
      FolderItemCard.svelte
      PeerItemGroup.svelte
      TransferItemActionButton.svelte
    ...small shared shell/search/presentation primitives
  lib/
    anchored-menu.ts
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
    contracts/
      dashboard.ts
      jobs.ts
      search.ts
      transfers.ts
      users.ts
    file-types.ts
    state.ts
    dashboard.ts
    download-options.ts
    job-preview.ts
    job-preview-runtime.ts
    job-types.ts
    jobs.ts
    search-config-schema.ts
    search-config.ts
    search-results.ts
    search-submission.ts
    status.ts
    transfers.ts
    users.ts
    ...other fixture/projection adapters
  App.svelte
```

The prototype deliberately separates daemon/runtime concepts from review and presentation concepts. Generated OpenAPI types stay at daemon-facing boundaries; `prototype/*` adapters own fixture/projection semantics; pages own local interaction orchestration; reusable rendering belongs in small components. Prototype interaction state/lifetime/count vocabulary lives in `prototype/state.ts`, while assumptions about missing daemon capabilities are grouped by domain under `prototype/contracts/` instead of a cross-app contract grab-bag. In particular, Jobs history presentation lives in `JobsHistoryList.svelte` instead of being embedded in the already complex Jobs controller, and the preview model has no dependency on runtime job records. `job-preview-runtime.ts` exists only because this unwired prototype needs to emulate committing a preview locally; a real client should replace that adapter with the eventual preview-submit API.

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

The prototype now opens on a Dashboard tab. Its history range control is intentionally dashboard-wide rather than chart-only: switching between 24h, 7d, 30d, 90d, 1y, and All updates all four historical KPI cards (Downloaded, Uploaded, Share ratio, and **Distinct peers**), the transfer activity chart, and Downloads/Uploads/Content/Errors ranking data together. Distinct peers means unique remote usernames observed across download and upload transfers in the selected range, counted once across both directions. Current download/upload rates and active-transfer count live together in the bottom **Transfer rates** panel instead of competing with historical totals at the top. The historical Downloaded/Uploaded/Share ratio cards retain compact range-dependent bar strips for quick visual texture without replacing the main transfer-activity chart.

The lower dashboard gives the ranking panel the full row, then places Transfer rates and Daemon health together in a responsive bottom grid. Transfer rates keeps its compact label-above-value rate blocks, while Daemon health uses denser label-left/value-right status pairs with the final row free of dangling separators. Ranked tables are explicitly full-width rather than relying on a generic `.table` class, avoiding CSS collisions and unused horizontal space; the scrollable ranking body starts directly beneath the tabs so scrolled rows cannot show through a padding slit above the sticky column heading. Download and upload user rankings are deliberately directional: Downloads ranks remote users we downloaded from, while Uploads ranks remote users we uploaded to. All four ranking tabs retain the top 20 rows for the selected history range and scroll inside the ranking panel.

- Dashboard activity curves use bounded Bezier smoothing so sample values stay exact without angular joins.
- The workspace itself owns vertical scrolling, so the scrollbar stays at the browser edge on every tab.
- Normal tab content is centered inside a shared 90rem maximum-width container, avoiding a large one-sided void on ultrawide/4K displays.
- The top search controls use the same centered container but are capped independently at 76rem, so the query bar remains comfortably scannable on very wide displays.

### Search configuration structure

The search configuration UI deliberately keeps the daemon's required-vs-ranking vocabulary in one declarative registry (`src/prototype/search-config-schema.ts`). Required and preferred labels live next to each other there, and conditions without an explicit documented `pref-*` counterpart have no ranking entry. Compound controls remain explicit Svelte rather than being forced through a generic form generator; shared presentation such as the format selector is factored into reusable components. The panel's presentation uses stable semantic column ownership only at the layout layer: search-family-specific sections can appear or disappear, but Matching/Album structure/Grouping stay in the primary stack and Peers stays in the secondary stack. This keeps spatial memory predictable without encoding those presentation columns into the search schema or daemon model. The API adapter in `search-config.ts` stays explicit so its wire mapping is easy to audit against the generated OpenAPI types.


## Backend contract discipline

Current OpenAPI-generated DTOs are used at daemon-facing fixture boundaries; UI-only grouping and presentation remain view models. Milestone 108 refreshes the checked-in `docs/openapi.json` from the supplied 2026-08-29 daemon repository, so generated-type changes in that milestone are intentional and reflect the daemon's fixed-size Job detail/direct-child paging and bounded transfer-detail cleanup. When the prototype needs data or mutations the daemon does not expose cleanly, define that assumption once in the matching domain module under `src/prototype/contracts/` rather than reconstructing it ad hoc in components. Search contracts deliberately model fixed-size top-level summaries **and** separately paged folder/group children; adding only a top-level cursor while retaining unbounded nested arrays would not satisfy the daemon's bounded-resource direction. The proposed Job Preview, Dashboard analytics, transfer mutations, and mixed share-tree query contracts follow the same domain-owned boundary. The backend audit remains in `DAEMON-AUDIT.md`; the README records only durable UI and architecture decisions.

Resource views should keep loading, empty, unavailable, offline, and terminal states distinct without adding explanatory product copy when the state is already visually obvious. When one of those states explains why an entire resource view has no content, it owns a modest centered content-state surface rather than looking like a thin leftover banner; blocking loading states use content-shaped skeleton rows to reduce the jump into the eventual view. Search reruns follow immutable daemon-job semantics while replacing the prior run in the same logical UI slot. New private-chat targets remain frontend drafts until the first accepted send.

## Prototype URL/history behavior

Top-level destinations now synchronize with the browser pathname (`/dashboard`, `/jobs`, `/downloads`, `/uploads`, `/users/...`, `/chat`, `/settings`) using the native History API. Job detail views use `/jobs/{id}`. User profile/share subviews use `/users/{username}` and `/users/{username}/shares`. `popstate` restores the matching prototype view so browser Back/Forward works without a routing dependency. Dynamically created mock jobs remain in-memory prototype state, so a hard reload of a newly generated `/jobs/{id}` that is not one of the built-in fixtures falls back to `/jobs`.

Search Results and Shares no longer reserve a select-visible toolbar row. Selection remains per item/folder, and selected items produce a floating Download action with a neighboring Deselect all control. Downloads and Uploads share `TransferBulkActions` for remove-completed and scoped bulk cancellation, while individual terminal transfer entries reuse the same contextual action slot with a trash icon instead of a cancel icon.
