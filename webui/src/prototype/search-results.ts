import type { SearchDraft, SearchResultMode } from './search';
import {
  cloneSearchConditions,
  createEmptySearchConditions,
  type PrototypeSearchConditions,
} from './search-config';

export type SearchStatus = 'searching' | 'receiving' | 'complete';
export type SearchView = 'list' | 'results';
export type SearchSort = 'relevance' | 'speed' | 'queue' | 'size';
export type SizeSortDirection = 'asc' | 'desc';

export interface SearchRecord {
  id: string;
  draft: SearchDraft;
  displayQuery: string;
  status: SearchStatus;
  foundFiles: number;
  lockedFiles: number;
  distinctPeers: number;
  when: string;
  conditions: PrototypeSearchConditions;
  fixture: 'autechre' | 'boards' | 'aphex' | 'burial' | 'generic';
}

export interface PeerInfo {
  username: string;
  uploadSpeedMbps: number;
  freeUploadSlot: boolean;
  queueLength: number;
}

export interface AudioAttributes {
  bitrateKbps?: number;
  sampleRateHz?: number;
  bitDepth?: number;
  lengthSeconds?: number;
}

export interface TrackSearchResult {
  kind: 'track';
  id: string;
  peer: PeerInfo;
  path: string;
  locked: boolean;
  sizeBytes: number;
  audio?: AudioAttributes;
  preferred: boolean;
}

export interface AlbumFileResult {
  id: string;
  relativePath: string;
  locked: boolean;
  sizeBytes: number;
  audio?: AudioAttributes;
}

export interface AlbumSearchResult {
  kind: 'album';
  id: string;
  peer: PeerInfo;
  path: string;
  locked: boolean;
  sizeBytes: number;
  files: AlbumFileResult[];
  preferred: boolean;
}

export type ProjectedSearchResult = TrackSearchResult | AlbumSearchResult;

const nightshift: PeerInfo = { username: 'nightshift', uploadSpeedMbps: 12.8, freeUploadSlot: true, queueLength: 0 };
const cassetteculture: PeerInfo = { username: 'cassetteculture', uploadSpeedMbps: 8.4, freeUploadSlot: false, queueLength: 2 };
const cloudarchive: PeerInfo = { username: 'cloudarchive', uploadSpeedMbps: 6.1, freeUploadSlot: true, queueLength: 0 };
const tapeLoop: PeerInfo = { username: 'tape_loop', uploadSpeedMbps: 2.7, freeUploadSlot: false, queueLength: 7 };
const silvermachine: PeerInfo = { username: 'silvermachine', uploadSpeedMbps: 5.2, freeUploadSlot: false, queueLength: 3 };

export const trackResults: TrackSearchResult[] = [
  { kind: 'track', id: 't1', peer: nightshift, path: 'Music/Boards of Canada/Geogaddi/02 - Music Is Math.flac', locked: false, sizeBytes: 41_900_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 }, preferred: true },
  { kind: 'track', id: 't2', peer: nightshift, path: 'Music/Boards of Canada/Geogaddi/09 - Julie and Candy.flac', locked: false, sizeBytes: 48_100_000, audio: { bitrateKbps: 1012, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 330 }, preferred: true },
  { kind: 'track', id: 't3', peer: cassetteculture, path: 'audio/autechre/gantz_graf/01-gantz_graf.flac', locked: false, sizeBytes: 58_400_000, audio: { bitrateKbps: 1031, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 358 }, preferred: true },
  { kind: 'track', id: 't4', peer: cassetteculture, path: 'audio/autechre/gantz_graf/02-dial.flac', locked: true, sizeBytes: 52_700_000, audio: { bitrateKbps: 987, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 376 }, preferred: true },
  { kind: 'track', id: 't5', peer: cloudarchive, path: 'Archive/IDM/Autechre/Gantz Graf/Gantz Graf.m4a', locked: false, sizeBytes: 14_700_000, audio: { bitrateKbps: 256, sampleRateHz: 44_100, lengthSeconds: 359 }, preferred: false },
  { kind: 'track', id: 't6', peer: cloudarchive, path: 'Archive/IDM/Autechre/Gantz Graf/Dial.flac', locked: false, sizeBytes: 52_300_000, audio: { bitrateKbps: 979, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 376 }, preferred: false },
  { kind: 'track', id: 't7', peer: tapeLoop, path: 'incoming/autechre/gantz graf live.mp3', locked: false, sizeBytes: 13_200_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 344 }, preferred: false },
  { kind: 'track', id: 't8', peer: silvermachine, path: 'lossless/Autechre/Gantz Graf/01 - Gantz Graf.wav', locked: false, sizeBytes: 63_100_000, audio: { sampleRateHz: 48_000, bitDepth: 24, lengthSeconds: 358 }, preferred: false },
];

export const albumResults: AlbumSearchResult[] = [
  {
    kind: 'album', id: 'a1', peer: nightshift, path: 'Music/Boards of Canada/Geogaddi', locked: false, sizeBytes: 612_000_000, preferred: true,
    files: [
      { id: 'a1f1', relativePath: '01 - Ready Lets Go.flac', locked: false, sizeBytes: 21_800_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 60 } },
      { id: 'a1f2', relativePath: '02 - Music Is Math.flac', locked: false, sizeBytes: 41_900_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 } },
      { id: 'a1f3', relativePath: '03 - Beware the Friendly Stranger.flac', locked: false, sizeBytes: 13_400_000, audio: { bitrateKbps: 932, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 37 } },
      { id: 'a1f4', relativePath: '04 - Gyroscope.flac', locked: false, sizeBytes: 31_700_000, audio: { bitrateKbps: 956, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 214 } },
      { id: 'a1f5', relativePath: 'Artwork/cover.jpg', locked: false, sizeBytes: 1_400_000 },
    ],
  },
  {
    kind: 'album', id: 'a2', peer: nightshift, path: 'Music/Boards of Canada/Geogaddi [24bit]', locked: true, sizeBytes: 1_290_000_000, preferred: true,
    files: [
      { id: 'a2f1', relativePath: '01 - Ready Lets Go.flac', locked: true, sizeBytes: 42_300_000, audio: { bitrateKbps: 2110, sampleRateHz: 96_000, bitDepth: 24, lengthSeconds: 60 } },
      { id: 'a2f2', relativePath: '02 - Music Is Math.flac', locked: true, sizeBytes: 84_600_000, audio: { bitrateKbps: 2198, sampleRateHz: 96_000, bitDepth: 24, lengthSeconds: 323 } },
      { id: 'a2f3', relativePath: '03 - Beware the Friendly Stranger.flac', locked: true, sizeBytes: 26_400_000, audio: { bitrateKbps: 2080, sampleRateHz: 96_000, bitDepth: 24, lengthSeconds: 37 } },
    ],
  },
  {
    kind: 'album', id: 'a3', peer: cassetteculture, path: 'lossless/Boards of Canada/2002 - Geogaddi', locked: false, sizeBytes: 594_000_000, preferred: true,
    files: [
      { id: 'a3f1', relativePath: 'CD/01 Ready Lets Go.flac', locked: false, sizeBytes: 21_300_000, audio: { bitrateKbps: 936, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 60 } },
      { id: 'a3f2', relativePath: 'CD/02 Music Is Math.flac', locked: false, sizeBytes: 40_800_000, audio: { bitrateKbps: 921, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 } },
      { id: 'a3f3', relativePath: 'CD/03 Beware the Friendly Stranger.flac', locked: false, sizeBytes: 13_200_000, audio: { bitrateKbps: 919, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 37 } },
      { id: 'a3f4', relativePath: 'cover.png', locked: false, sizeBytes: 2_800_000 },
    ],
  },
  {
    kind: 'album', id: 'a4', peer: cloudarchive, path: 'Archive/Boards of Canada/Geogaddi (MP3)', locked: false, sizeBytes: 178_000_000, preferred: false,
    files: [
      { id: 'a4f1', relativePath: '01 Ready Lets Go.mp3', locked: false, sizeBytes: 2_400_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 60 } },
      { id: 'a4f2', relativePath: '02 Music Is Math.mp3', locked: false, sizeBytes: 12_400_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 323 } },
      { id: 'a4f3', relativePath: '03 Beware The Friendly Stranger.mp3', locked: false, sizeBytes: 1_600_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 37 } },
    ],
  },
  {
    kind: 'album', id: 'a5', peer: tapeLoop, path: 'BoC/Geogaddi incomplete', locked: false, sizeBytes: 386_000_000, preferred: false,
    files: [
      { id: 'a5f1', relativePath: '02 - Music Is Math.flac', locked: false, sizeBytes: 41_200_000, audio: { bitrateKbps: 932, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 } },
      { id: 'a5f2', relativePath: '04 - Gyroscope.flac', locked: false, sizeBytes: 31_300_000, audio: { bitrateKbps: 947, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 214 } },
    ],
  },
];

function conditionsFor(_mode: SearchResultMode, _variant: 'default' | 'simple' | 'aphex'): PrototypeSearchConditions {
  // Demo searches start with neutral conditions so the result fixtures remain
  // visible. Users can add restrictive conditions from the shared editor.
  return createEmptySearchConditions();
}

export function createInitialSearches(): SearchRecord[] {
  return [
    {
      id: 'search-autechre',
      draft: { mode: 'split', resultMode: 'track', query: '', artist: 'Autechre', title: 'Gantz Graf' },
      displayQuery: 'Autechre - Gantz Graf', status: 'searching', foundFiles: 187, lockedFiles: 14, distinctPeers: 42, when: 'just now',
      conditions: conditionsFor('track', 'simple'), fixture: 'autechre',
    },
    {
      id: 'search-boards',
      draft: { mode: 'split', resultMode: 'album', query: '', artist: 'Boards of Canada', title: 'Geogaddi' },
      displayQuery: 'Boards of Canada - Geogaddi', status: 'receiving', foundFiles: 412, lockedFiles: 27, distinctPeers: 68, when: '4 min ago',
      conditions: conditionsFor('album', 'default'), fixture: 'boards',
    },
    {
      id: 'search-aphex',
      draft: { mode: 'split', resultMode: 'track', query: '', artist: 'Aphex Twin', title: 'Xtal' },
      displayQuery: 'Aphex Twin - Xtal', status: 'complete', foundFiles: 93, lockedFiles: 5, distinctPeers: 31, when: '26 min ago',
      conditions: conditionsFor('track', 'aphex'), fixture: 'aphex',
    },
    {
      id: 'search-burial',
      draft: { mode: 'split', resultMode: 'album', query: '', artist: 'Burial', title: 'Untrue' },
      displayQuery: 'Burial - Untrue', status: 'complete', foundFiles: 221, lockedFiles: 18, distinctPeers: 57, when: '1 h ago',
      conditions: conditionsFor('album', 'simple'), fixture: 'burial',
    },
  ];
}

export function displayQueryForDraft(draft: SearchDraft): string {
  if (draft.mode === 'split') return [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' - ');
  return draft.query.trim();
}

export function createSearchRecord(draft: SearchDraft, conditions: PrototypeSearchConditions): SearchRecord {
  return {
    id: `search-${Date.now()}`,
    draft: { ...draft },
    displayQuery: displayQueryForDraft(draft) || 'Untitled search',
    status: 'searching',
    foundFiles: draft.resultMode === 'album' ? 128 : 187,
    lockedFiles: draft.resultMode === 'album' ? 9 : 14,
    distinctPeers: draft.resultMode === 'album' ? 24 : 42,
    when: 'just now',
    conditions: cloneSearchConditions(conditions),
    fixture: 'generic',
  };
}
