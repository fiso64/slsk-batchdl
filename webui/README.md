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

The shell currently has six destinations:

- **Dashboard**
- **Search**
- **Downloads**
- **Uploads**
- **Chat**
- **Settings**

There is intentionally no separate Shares or History page. Transfer history lives in Downloads/Uploads, and other historical information should generally remain close to the feature that produced it.

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


### Advanced conditions

The advanced-search popover is still prototype-only, but it follows the current required-condition API rather than inventing unrelated filters. Preferred (`pref-`) conditions are intentionally ignored for now.

Common controls currently include formats, free min/max bitrate, exact sample rate, exact bit depth, metadata handling, strict artist matching, and peer allow/ban lists. Format shortcuts cover FLAC, MP3, OGG, OPUS, M4A, and WAV; a custom mode accepts comma-separated arbitrary formats.

Track mode exposes title and duration-related controls. Album mode instead exposes album matching, album track-count limits, required track titles, and strict album quality. The prototype keeps mode-specific values when switching modes, but only conditions relevant to the active mode are shown as applied.

The UI treats sample rate and bit depth as exact values. A small adapter in `src/prototype/search-config.ts` demonstrates how those choices map to both the generated API's min and max fields.


## Search tab exploration

The Search tab now has persistent prototype state with two views: a newest-first Searches list and a projected Results view. Submitting the global search creates a search and opens its results; opening an existing search restores its Track or Album projection. The back button returns to Searches, and clicking Search in the sidebar while already viewing results does the same. Leaving Search and returning from another tab preserves the last Search subview.

Search result refinement is deliberately compact: text filtering and sort controls share one row, while the applied search conditions sit in a separate pill bar. **Edit** opens the same `SearchConfigPanel` component used by the global search rather than a second ad-hoc condition UI. Removing or changing conditions filters the mock result projection immediately.

Track results show the full file path, lock state, size, length, and available bitrate/sample-rate/bit-depth metadata. Whole track rows are selectable. Album results show the full album directory path and a nested file list using paths relative to the album folder; clicking the album summary selects or clears the whole album, while individual files remain independently selectable. Adjacent results from the same peer share one collapsible peer header with free-slot state beside the peer name and compact upload/queue/file statistics on the right. Relevance sorting separates backend-marked preferred matches from other matches; speed, queue, and two-way size sorting use one combined result stream. Selection actions appear contextually rather than reserving a permanent toolbar.

Prototype searches now start with neutral/no-op conditions so fixture results are visible until the user deliberately applies result constraints. Checkboxes use a dark native-like treatment throughout the application when the OS requests dark mode, including indeterminate album selection.

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
    SearchConditionPills.svelte
    SearchConfigPanel.svelte
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
    TransferPage.svelte
    Chat.svelte
    Settings.svelte
  prototype/
    navigation.ts
    search.ts
    search-config.ts
    search-results.ts
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
- treating the current Search, Transfers, or Chat layouts as final.

The next iterations should continue to optimize for **learning what Sockseek should feel like**, not for preserving prototype code.

Applied search conditions appear in a focused overlay aligned to the query bar. Once engaged, the overlay remains open while interacting with the query controls, result-mode toggle, settings button, or settings panel; it closes on an outside click or Escape from the search controls. A source comment marks where future online metadata suggestions (for example MusicBrainz results) can be inserted beneath the pills once that workflow is supported.

## Dashboard exploration

The prototype now opens on a Dashboard tab. Its history range control is intentionally dashboard-wide rather than chart-only: switching between 24h, 7d, 30d, and 90d updates the transfer activity chart, the Peers/Content/Errors ranking data, and the Transfer summary figures together. Current-rate cards, Recent activity, and Daemon health remain live/current-state views.

The lower dashboard uses independent vertical columns so panels size to their own content. Ranked tables are explicitly full-width rather than relying on a generic `.table` class, avoiding CSS collisions and unused horizontal space.

- Dashboard activity curves use bounded Bezier smoothing so sample values stay exact without angular joins.
- The workspace itself owns vertical scrolling, so the scrollbar stays at the browser edge on every tab.
- Normal tab content is centered inside a shared 90rem maximum-width container, avoiding a large one-sided void on ultrawide/4K displays.
- The top search controls use the same centered container but are capped independently at 76rem, so the query bar remains comfortably scannable on very wide displays.
