# Search Jobs and Search Sessions

## Direction

- Add a reusable internal search-session primitive that collects raw Soulseek result files, emits live raw-result events, tracks revision/counts, supports cancellation, and exposes snapshots.
- Keep existing download jobs (`SongJob`, `AlbumJob`, `AggregateJob`, etc.) as the user-facing job model. They should use the shared search-session primitive internally, not necessarily create visible child `SearchJob` nodes.
- Add a first-class `SearchJob` later as a thin user-visible wrapper around the same search-session primitive. This is for GUI/server live search and CLI `--print results`.
- Keep expensive interpretation separate from search execution. Sorting, album grouping, equivalent-track grouping, and aggregate-album grouping should be explicit projections over a raw result snapshot.
- Let clients request projections on demand, with paging/options where useful. Do not sort or group every incoming result unconditionally.
- Use revision-based projection caching so repeated GUI refreshes do not redo work when raw results have not changed.
- Preserve fast-search as a lightweight live-result consumer. It should inspect incoming raw results cheaply and opportunistically, while final candidates still come from the normal projection after search completion.

## Migration

1. Extract raw result collection from `Searcher.RunSearches` into an internal `SearchRun`/`SearchSession`.
2. Extract projection helpers for sorted track candidates, album folders, aggregate tracks, and aggregate albums.
3. Refactor existing `Searcher.SearchSong`, `SearchAlbum`, `SearchAggregate`, and `SearchAggregateAlbum` to use the session plus projections without changing public behavior.
4. Add first-class `SearchJob` for live raw results and on-demand projections.
5. Move CLI `--print results` onto `SearchJob` after the existing paths are stable.

