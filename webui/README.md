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

The prototype currently uses a cool neutral palette with a muted violet accent. The accent is reserved mainly for focus, selection, progress, and unread state. Light and dark variants follow the system theme.

The shell currently has five destinations:

- **Search**
- **Downloads**
- **Uploads**
- **Chat**
- **Settings**

There is intentionally no separate Shares or History page. Transfer history lives in Downloads/Uploads, and other historical information should generally remain close to the feature that produced it.

A global search field remains available in the header on every page. Pressing `/` focuses it and Enter submits the current query and returns to Search. A fixed-width **Album / Track** button switches result mode, while `•••` opens advanced search conditions. Applied conditions appear as removable pills below the bar.

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
    PrototypeScenarioPicker.svelte
    Sidebar.svelte
  mock/
    fixtures/
      transfers.ts
    scenarios/
      index.ts
    types.ts
  pages/
    Search.svelte
    TransferPage.svelte
    Chat.svelte
    Settings.svelte
  prototype/
    navigation.ts
    search.ts
    search-config.ts
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
