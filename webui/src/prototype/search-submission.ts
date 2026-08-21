import type { components } from '../api/generated';
import type { SearchDraft } from './search';
import { networkQuery } from './search';
import { cloneSearchConditions, toNecessarySearchPatch, type PrototypeSearchConditions } from './search-config';

export type SubmitTrackSearchJobRequestDto = components['schemas']['SubmitTrackSearchJobRequestDto'];
export type SubmitAlbumSearchJobRequestDto = components['schemas']['SubmitAlbumSearchJobRequestDto'];

export type PrototypeSearchSubmission =
  | { kind: 'track'; request: SubmitTrackSearchJobRequestDto }
  | { kind: 'album'; request: SubmitAlbumSearchJobRequestDto };

function numberOrUndefined(value: string): number | undefined {
  if (!value.trim()) return undefined;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

export function buildSearchSubmission(draft: SearchDraft, conditions: PrototypeSearchConditions): PrototypeSearchSubmission {
  const searchPatch = toNecessarySearchPatch(draft.resultMode, conditions);
  const options = { downloadSettings: { search: searchPatch } };

  if (draft.resultMode === 'track') {
    const request: SubmitTrackSearchJobRequestDto = {
      songQuery: draft.mode === 'split'
        ? {
            artist: draft.artist.trim() || null,
            title: draft.title.trim() || null,
            album: null,
            uri: null,
            length: numberOrUndefined(conditions.track.expectedLength) ?? null,
            artistMaybeWrong: false,
          }
        : {
            artist: null,
            title: networkQuery(draft) || null,
            album: null,
            uri: null,
            length: numberOrUndefined(conditions.track.expectedLength) ?? null,
            artistMaybeWrong: false,
          },
      includeFullResults: true,
      options,
    };
    return { kind: 'track', request };
  }

  const request: SubmitAlbumSearchJobRequestDto = {
    albumQuery: draft.mode === 'split'
      ? {
          artist: draft.artist.trim() || null,
          album: draft.title.trim() || null,
          searchHint: null,
          uri: null,
          artistMaybeWrong: false,
        }
      : {
          artist: null,
          album: null,
          searchHint: networkQuery(draft) || null,
          uri: null,
          artistMaybeWrong: false,
        },
    options,
  };
  return { kind: 'album', request };
}

export function cloneSearchSubmission(submission: PrototypeSearchSubmission): PrototypeSearchSubmission {
  // Search records may be wrapped in Svelte proxies by the time they are rerun.
  // The generated request DTOs are JSON data, so clone through JSON rather than
  // structuredClone(), which rejects Proxy objects in browsers.
  return JSON.parse(JSON.stringify(submission)) as PrototypeSearchSubmission;
}

export function immutableSubmittedConditions(conditions: PrototypeSearchConditions): PrototypeSearchConditions {
  return cloneSearchConditions(conditions);
}
