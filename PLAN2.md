# Interactive Search And Selection

## Core Decisions

- `SearchJob` is the shared primitive for interactive result discovery.
  - CLI and future GUI/server flows should search first, present results, then start concrete download jobs from explicit user selection.
  - This keeps local CLI, thin CLI, and GUI on the same model.

- `AlbumJob` remains the concrete album download primitive.
  - Non-interactive mode stays in-engine and may continue to auto-pick and auto-fallback between album candidates.
  - Interactive mode submits `AlbumJob` with `ResolvedTarget` prefilled instead of asking the engine to pause for selection.

- Remove `SelectAlbumVersion` completely.
  - It is the wrong abstraction for server/thin-client use.
  - We do not want a running engine job to block on a UI callback.

- Keep `RetrieveFolderJob` as the real folder-expansion path.
  - Interactive `r` should literally run a retrieve-folder job.
  - This preserves existing behavior, visibility, progress, and cancellation in both CLI and future GUI/server views.

- Interactive retry is a client-side loop over normal jobs.
  - Flow: search -> choose -> download.
  - If the chosen album job fails or is cancelled, prompt again and exclude the failed folder by `username + folder path`.
  - Global cancel still cancels everything; interactive `s` remains the explicit skip action.

- Preserve interactive state that is purely presentation.
  - Filter text should survive retries.
  - Rendering logic should be client-owned and ideally agnostic to local vs remote execution.

## Why

- This avoids callback-shaped Core tech debt.
- It keeps album download behavior in Core while leaving UI orchestration in the client.
- It gives CLI and GUI the same mental model without forcing non-interactive auto mode through a client-style path.
- It reuses the existing job/event model instead of inventing hidden special cases.

## Immediate Work

1. Remove `SelectAlbumVersion` from Core and CLI.
2. Implement CLI interactive album mode on top of `SearchJob`, album projections, and `RetrieveFolderJob`.
3. Start concrete `AlbumJob`s from selected album folders.
4. Re-prompt after selected album failure/cancel, excluding the attempted folder.
5. Add tests around album projection equivalence and interactive retry semantics where practical.
