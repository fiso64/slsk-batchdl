# Sockseek WebUI prototype

This directory is an intentionally lightweight UI/UX prototype for the upcoming Sockseek v4 daemon WebUI.

The immediate goal is to explore **how Sockseek data and workflows should be presented**, not to commit to the production frontend architecture. The prototype may be partially or completely rewritten later (for example in Blazor) once the daemon and production requirements have settled.

## Technology

- **Svelte 5** for declarative, reactive UI components.
- **TypeScript** for frontend and mock-data type safety.
- **Vite** for the development server and build.
- **npm** as the package manager for now. There is no architectural dependency on npm; switching to pnpm or Bun later should be straightforward.
- **openapi-typescript** to generate TypeScript types from Sockseek's checked-in `docs/openapi.json`.
- No SvelteKit, frontend state framework, real daemon connection, or SignalR integration yet.

## Setup

From this directory:

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

`npm run api:generate` reads `../docs/openapi.json` and writes `src/api/generated.ts`. The generated file should be treated as generated code rather than edited manually.

## Why use OpenAPI in a disposable prototype?

The prototype does not need a live daemon, but the checked-in OpenAPI document gives it the real backend vocabulary and shapes. This helps in several ways:

- mock data can be checked against the actual daemon DTOs;
- UI work can reveal information that the backend does not expose yet;
- backend contract changes become visible to the prototype after regeneration;
- none of this requires the prototype to implement every endpoint or use the eventual production transport/client architecture.

The starter `App.svelte` deliberately imports generated types and creates a small typed `StateSnapshotDto` probe. This is only a smoke test that Svelte and generated API types are wired together.

## Prototype direction

Keep the prototype optimized for fast UI iteration. Mock **user-facing situations**, rather than faithfully emulating the entire daemon protocol. Later examples might include normal, busy, offline, large-search, failed-transfer, and long-filename scenarios.

A likely structure as the prototype grows is:

```text
src/
  api/
    generated.ts      # generated from docs/openapi.json
  components/
  pages/
  mock/
    fixtures/
    scenarios/
  App.svelte
```

Generated API DTOs are useful inputs, but they should not dictate the visual/component model. UI-specific view models or adapters can be introduced when that makes a screen easier to design.

## Deliberately out of scope for now

- choosing the final production WebUI technology;
- reimplementing `SockseekLiveClient` semantics in TypeScript;
- real HTTP/SignalR integration;
- authentication;
- production packaging/hosting;
- comprehensive application architecture.

The prototype is successful if it helps answer questions about navigation, information hierarchy, density, grouping, live activity, transfers, search results, chat, sharing, and other user-facing behavior before production implementation starts.
