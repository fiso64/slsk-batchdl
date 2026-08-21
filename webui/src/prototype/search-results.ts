import type { components } from '../api/generated';
import type { SearchDraft, SearchResultMode } from './search';
import type { AudioAttributes, ItemPeerInfo } from './items';
import { basename, extension } from './items';
import type {
  PrototypeDataLifetime,
  ProposedPreferredResultDto,
  ProposedSearchResultProjectionRequestDto,
} from './backend-contracts';
import { prototypeUuid } from './ids';
import {
  cloneSearchConditions,
  createPrototypeSearchConditions,
  type PrototypeSearchConditions,
  toNecessarySearchPatch,
} from './search-config';
import {
  buildSearchSubmission,
  cloneSearchSubmission,
  immutableSubmittedConditions,
  type PrototypeSearchSubmission,
} from './search-submission';

export type SearchStatus = 'pending' | 'searching' | 'receiving' | 'complete' | 'failed' | 'cancelled' | 'skipped' | 'interrupted';
export type SearchResultPersistenceState = 'available' | 'incomplete' | 'pruned' | 'not-persisted' | 'interrupted';
export type SearchView = 'list' | 'results';
export type SearchSort = 'relevance' | 'speed' | 'queue' | 'size';
export type SizeSortDirection = 'asc' | 'desc';

export interface SearchResultProjectionPage {
  items: ProjectedSearchResult[];
  totalCount: number;
  nextCursor: string | null;
  request: ProposedSearchResultProjectionRequestDto;
}

export interface SearchRecord {
  id: string;
  workflowId: string;
  parentJobId: string | null;
  sourceJobId: string | null;
  draft: SearchDraft;
  displayQuery: string;
  status: SearchStatus;
  resultState: SearchResultPersistenceState;
  lifetime: PrototypeDataLifetime;
  createdAtUtc: string;
  foundFiles: number;
  lockedFiles: number;
  distinctPeers: number;
  when: string;
  conditions: PrototypeSearchConditions;
  submittedConditions: PrototypeSearchConditions;
  submission: PrototypeSearchSubmission;
  fixture: 'autechre' | 'boards' | 'aphex' | 'burial' | 'generic' | 'historical';
  pagination: { resultPageSize: number; resultHasMore: boolean; resultPagesLoaded: number; historyPage: number };
}

export type { AudioAttributes };

type FileCandidateRefDto = components['schemas']['FileCandidateRefDto'];
type AlbumFolderRefDto = components['schemas']['AlbumFolderRefDto'];

export type PeerInfo = ItemPeerInfo & {
  uploadSpeedMbps: number;
  freeUploadSlot: boolean;
  /** Proposed result-peer field; not in current PeerInfoDto. */
  queueLength: number;
};

export interface TrackSearchResult {
  kind: 'track';
  id: string;
  candidateRef: FileCandidateRefDto;
  peer: PeerInfo;
  path: string;
  /** Proposed per-result visibility until the daemon exposes it. */
  locked: boolean;
  sizeBytes: number;
  audio?: AudioAttributes;
  preference: ProposedPreferredResultDto;
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
  candidateRef: AlbumFolderRefDto;
  peer: PeerInfo;
  path: string;
  locked: boolean;
  sizeBytes: number;
  files: AlbumFileResult[];
  preferred: boolean;
  preference: ProposedPreferredResultDto;
  retrievalState: 'idle' | 'retrieving' | 'retrieved' | 'failed';
  totalFileCount: number;
}

export type ProjectedSearchResult = TrackSearchResult | AlbumSearchResult;

const nightshift: PeerInfo = { username: 'nightshift', uploadSpeedMbps: 12.8, freeUploadSlot: true, queueLength: 0 };
const cassetteculture: PeerInfo = { username: 'cassetteculture', uploadSpeedMbps: 8.4, freeUploadSlot: false, queueLength: 2 };
const cloudarchive: PeerInfo = { username: 'cloudarchive', uploadSpeedMbps: 6.1, freeUploadSlot: true, queueLength: 0 };
const tapeLoop: PeerInfo = { username: 'tape_loop', uploadSpeedMbps: 2.7, freeUploadSlot: false, queueLength: 7 };
const silvermachine: PeerInfo = { username: 'silvermachine', uploadSpeedMbps: 5.2, freeUploadSlot: false, queueLength: 3 };

export const trackResults: TrackSearchResult[] = [
  { kind: 'track', id: prototypeUuid(0x51000000, 1), peer: nightshift, path: 'Music/Boards of Canada/Geogaddi/02 - Music Is Math.flac', candidateRef: { username: nightshift.username, filename: 'Music/Boards of Canada/Geogaddi/02 - Music Is Math.flac' }, locked: false, sizeBytes: 41_900_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 }, preferred: true, preference: { candidateKey: `${nightshift.username}:Music/Boards of Canada/Geogaddi/02 - Music Is Math.flac`, tier: 'preferred', matchedPreferenceKeys: ['format','quality'] } },
  { kind: 'track', id: prototypeUuid(0x51000000, 2), peer: nightshift, path: 'Music/Boards of Canada/Geogaddi/09 - Julie and Candy.flac', candidateRef: { username: nightshift.username, filename: 'Music/Boards of Canada/Geogaddi/09 - Julie and Candy.flac' }, locked: false, sizeBytes: 48_100_000, audio: { bitrateKbps: 1012, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 330 }, preferred: true, preference: { candidateKey: `${nightshift.username}:Music/Boards of Canada/Geogaddi/09 - Julie and Candy.flac`, tier: 'preferred', matchedPreferenceKeys: ['format','quality'] } },
  { kind: 'track', id: prototypeUuid(0x51000000, 3), peer: cassetteculture, path: 'audio/autechre/gantz_graf/01-gantz_graf.flac', candidateRef: { username: cassetteculture.username, filename: 'audio/autechre/gantz_graf/01-gantz_graf.flac' }, locked: false, sizeBytes: 58_400_000, audio: { bitrateKbps: 1031, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 358 }, preferred: true, preference: { candidateKey: `${cassetteculture.username}:audio/autechre/gantz_graf/01-gantz_graf.flac`, tier: 'preferred', matchedPreferenceKeys: ['format','quality'] } },
  { kind: 'track', id: prototypeUuid(0x51000000, 4), peer: cassetteculture, path: 'audio/autechre/gantz_graf/02-dial.flac', candidateRef: { username: cassetteculture.username, filename: 'audio/autechre/gantz_graf/02-dial.flac' }, locked: true, sizeBytes: 52_700_000, audio: { bitrateKbps: 987, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 376 }, preferred: true, preference: { candidateKey: `${cassetteculture.username}:audio/autechre/gantz_graf/02-dial.flac`, tier: 'preferred', matchedPreferenceKeys: ['format','quality'] } },
  { kind: 'track', id: prototypeUuid(0x51000000, 5), peer: cloudarchive, path: 'Archive/IDM/Autechre/Gantz Graf/Gantz Graf.m4a', candidateRef: { username: cloudarchive.username, filename: 'Archive/IDM/Autechre/Gantz Graf/Gantz Graf.m4a' }, locked: false, sizeBytes: 14_700_000, audio: { bitrateKbps: 256, sampleRateHz: 44_100, lengthSeconds: 359 }, preferred: false, preference: { candidateKey: `${cloudarchive.username}:Archive/IDM/Autechre/Gantz Graf/Gantz Graf.m4a`, tier: 'other', matchedPreferenceKeys: [] } },
  { kind: 'track', id: prototypeUuid(0x51000000, 6), peer: cloudarchive, path: 'Archive/IDM/Autechre/Gantz Graf/Dial.flac', candidateRef: { username: cloudarchive.username, filename: 'Archive/IDM/Autechre/Gantz Graf/Dial.flac' }, locked: false, sizeBytes: 52_300_000, audio: { bitrateKbps: 979, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 376 }, preferred: false, preference: { candidateKey: `${cloudarchive.username}:Archive/IDM/Autechre/Gantz Graf/Dial.flac`, tier: 'other', matchedPreferenceKeys: [] } },
  { kind: 'track', id: prototypeUuid(0x51000000, 7), peer: tapeLoop, path: 'incoming/autechre/gantz graf live.mp3', candidateRef: { username: tapeLoop.username, filename: 'incoming/autechre/gantz graf live.mp3' }, locked: false, sizeBytes: 13_200_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 344 }, preferred: false, preference: { candidateKey: `${tapeLoop.username}:incoming/autechre/gantz graf live.mp3`, tier: 'other', matchedPreferenceKeys: [] } },
  { kind: 'track', id: prototypeUuid(0x51000000, 8), peer: silvermachine, path: 'lossless/Autechre/Gantz Graf/01 - Gantz Graf.wav', candidateRef: { username: silvermachine.username, filename: 'lossless/Autechre/Gantz Graf/01 - Gantz Graf.wav' }, locked: false, sizeBytes: 63_100_000, audio: { sampleRateHz: 48_000, bitDepth: 24, lengthSeconds: 358 }, preferred: false, preference: { candidateKey: `${silvermachine.username}:lossless/Autechre/Gantz Graf/01 - Gantz Graf.wav`, tier: 'other', matchedPreferenceKeys: [] } },
];

export const albumResults: AlbumSearchResult[] = [
  {
    kind: 'album', id: prototypeUuid(0x52000000, 1), peer: nightshift, path: 'Music/Boards of Canada/Geogaddi', candidateRef: { username: nightshift.username, folderPath: 'Music/Boards of Canada/Geogaddi' }, locked: false, sizeBytes: 612_000_000, preferred: true, preference: { candidateKey: `${nightshift.username}:Music/Boards of Canada/Geogaddi`, tier: 'preferred', matchedPreferenceKeys: ['format','quality'] }, retrievalState: 'idle', totalFileCount: 5,
    files: [
      { id: prototypeUuid(0x53000000 + 1, 1), relativePath: '01 - Ready Lets Go.flac', locked: false, sizeBytes: 21_800_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 60 } },
      { id: prototypeUuid(0x53000000 + 1, 2), relativePath: '02 - Music Is Math.flac', locked: false, sizeBytes: 41_900_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 } },
      { id: prototypeUuid(0x53000000 + 1, 3), relativePath: '03 - Beware the Friendly Stranger.flac', locked: false, sizeBytes: 13_400_000, audio: { bitrateKbps: 932, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 37 } },
      { id: prototypeUuid(0x53000000 + 1, 4), relativePath: '04 - Gyroscope.flac', locked: false, sizeBytes: 31_700_000, audio: { bitrateKbps: 956, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 214 } },
      { id: prototypeUuid(0x53000000 + 1, 5), relativePath: 'Artwork/cover.jpg', locked: false, sizeBytes: 1_400_000 },
    ],
  },
  {
    kind: 'album', id: prototypeUuid(0x52000000, 2), peer: nightshift, path: 'Music/Boards of Canada/Geogaddi [24bit]', candidateRef: { username: nightshift.username, folderPath: 'Music/Boards of Canada/Geogaddi [24bit]' }, locked: true, sizeBytes: 1_290_000_000, preferred: true, preference: { candidateKey: `${nightshift.username}:Music/Boards of Canada/Geogaddi [24bit]`, tier: 'preferred', matchedPreferenceKeys: ['format','quality'] }, retrievalState: 'idle', totalFileCount: 3,
    files: [
      { id: prototypeUuid(0x53000000 + 2, 1), relativePath: '01 - Ready Lets Go.flac', locked: true, sizeBytes: 42_300_000, audio: { bitrateKbps: 2110, sampleRateHz: 96_000, bitDepth: 24, lengthSeconds: 60 } },
      { id: prototypeUuid(0x53000000 + 2, 2), relativePath: '02 - Music Is Math.flac', locked: true, sizeBytes: 84_600_000, audio: { bitrateKbps: 2198, sampleRateHz: 96_000, bitDepth: 24, lengthSeconds: 323 } },
      { id: prototypeUuid(0x53000000 + 2, 3), relativePath: '03 - Beware the Friendly Stranger.flac', locked: true, sizeBytes: 26_400_000, audio: { bitrateKbps: 2080, sampleRateHz: 96_000, bitDepth: 24, lengthSeconds: 37 } },
    ],
  },
  {
    kind: 'album', id: prototypeUuid(0x52000000, 3), peer: cassetteculture, path: 'lossless/Boards of Canada/2002 - Geogaddi', candidateRef: { username: cassetteculture.username, folderPath: 'lossless/Boards of Canada/2002 - Geogaddi' }, locked: false, sizeBytes: 594_000_000, preferred: true, preference: { candidateKey: `${cassetteculture.username}:lossless/Boards of Canada/2002 - Geogaddi`, tier: 'preferred', matchedPreferenceKeys: ['format','quality'] }, retrievalState: 'idle', totalFileCount: 4,
    files: [
      { id: prototypeUuid(0x53000000 + 3, 1), relativePath: 'CD/01 Ready Lets Go.flac', locked: false, sizeBytes: 21_300_000, audio: { bitrateKbps: 936, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 60 } },
      { id: prototypeUuid(0x53000000 + 3, 2), relativePath: 'CD/02 Music Is Math.flac', locked: false, sizeBytes: 40_800_000, audio: { bitrateKbps: 921, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 } },
      { id: prototypeUuid(0x53000000 + 3, 3), relativePath: 'CD/03 Beware the Friendly Stranger.flac', locked: false, sizeBytes: 13_200_000, audio: { bitrateKbps: 919, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 37 } },
      { id: prototypeUuid(0x53000000 + 3, 4), relativePath: 'cover.png', locked: false, sizeBytes: 2_800_000 },
    ],
  },
  {
    kind: 'album', id: prototypeUuid(0x52000000, 4), peer: cloudarchive, path: 'Archive/Boards of Canada/Geogaddi (MP3)', candidateRef: { username: cloudarchive.username, folderPath: 'Archive/Boards of Canada/Geogaddi (MP3)' }, locked: false, sizeBytes: 178_000_000, preferred: false, preference: { candidateKey: `${cloudarchive.username}:Archive/Boards of Canada/Geogaddi (MP3)`, tier: 'other', matchedPreferenceKeys: [] }, retrievalState: 'idle', totalFileCount: 3,
    files: [
      { id: prototypeUuid(0x53000000 + 4, 1), relativePath: '01 Ready Lets Go.mp3', locked: false, sizeBytes: 2_400_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 60 } },
      { id: prototypeUuid(0x53000000 + 4, 2), relativePath: '02 Music Is Math.mp3', locked: false, sizeBytes: 12_400_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 323 } },
      { id: prototypeUuid(0x53000000 + 4, 3), relativePath: '03 Beware The Friendly Stranger.mp3', locked: false, sizeBytes: 1_600_000, audio: { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 37 } },
    ],
  },
  {
    kind: 'album', id: prototypeUuid(0x52000000, 5), peer: tapeLoop, path: 'BoC/Geogaddi', candidateRef: { username: tapeLoop.username, folderPath: 'BoC/Geogaddi' }, locked: false, sizeBytes: 72_500_000, preferred: false, preference: { candidateKey: `${tapeLoop.username}:BoC/Geogaddi`, tier: 'other', matchedPreferenceKeys: [] }, retrievalState: 'idle', totalFileCount: 2,
    files: [
      { id: prototypeUuid(0x53000000 + 5, 1), relativePath: '02 - Music Is Math.flac', locked: false, sizeBytes: 41_200_000, audio: { bitrateKbps: 932, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 } },
      { id: prototypeUuid(0x53000000 + 5, 2), relativePath: '04 - Gyroscope.flac', locked: false, sizeBytes: 31_300_000, audio: { bitrateKbps: 947, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 214 } },
    ],
  },
];


function alternateTrackResult(result: TrackSearchResult, index: number): TrackSearchResult {
  const path = `Mirrors/Page 2/${result.path}`;
  return {
    ...result,
    id: prototypeUuid(0x54000000, index + 1),
    path,
    candidateRef: { username: result.peer.username, filename: path },
    preference: { ...result.preference, candidateKey: `${result.peer.username}:${path}` },
  };
}

function alternateAlbumResult(result: AlbumSearchResult, index: number): AlbumSearchResult {
  const path = `Mirrors/Page 2/${result.path}`;
  return {
    ...result,
    id: prototypeUuid(0x55000000, index + 1),
    path,
    candidateRef: { username: result.peer.username, folderPath: path },
    preference: { ...result.preference, candidateKey: `${result.peer.username}:${path}` },
    files: result.files.map((file, fileIndex) => ({
      ...file,
      id: prototypeUuid(0x55100000 + index, fileIndex + 1),
    })),
  };
}

const trackNextPageResults = trackResults.map(alternateTrackResult);
const albumNextPageResults = albumResults.map(alternateAlbumResult);

function availableSearchResultCorpus(record: SearchRecord): ProjectedSearchResult[] {
  const firstPage: ProjectedSearchResult[] = record.draft.resultMode === 'album' ? albumResults : trackResults;
  if (!record.pagination.resultHasMore && record.pagination.resultPagesLoaded <= 1) return [...firstPage];
  const secondPage: ProjectedSearchResult[] = record.draft.resultMode === 'album' ? albumNextPageResults : trackNextPageResults;
  return [...firstPage, ...secondPage];
}

function patchValues(patch: components['schemas']['CollectionPatchDtoOfstring'] | null | undefined): string[] {
  return [...(patch?.replace ?? []), ...(patch?.append ?? [])];
}

function numeric(value: number | string | null | undefined): number | undefined {
  if (value === null || value === undefined) return undefined;
  if (typeof value === 'string' && !value.trim()) return undefined;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function isAudioPath(path: string): boolean {
  return ['FLAC', 'MP3', 'OGG', 'OPUS', 'M4A', 'WAV', 'AAC', 'APE'].includes(extension(path));
}

function fileMatchesPatch(
  record: SearchRecord,
  result: ProjectedSearchResult,
  path: string,
  audio: AudioAttributes | undefined,
  patch: components['schemas']['FileConditionsPatchDto'] | null | undefined,
  expectedLength?: number,
): boolean {
  if (!patch) return true;
  const formats = patchValues(patch.formats).map((value) => value.toUpperCase());
  const allowed = patchValues(patch.allowedUsers);
  const banned = patchValues(patch.bannedUsers);
  if (formats.length && !formats.includes(extension(path))) return false;
  if (allowed.length && !allowed.includes(result.peer.username)) return false;
  if (banned.includes(result.peer.username)) return false;
  if (patch.acceptMissingProps === false && isAudioPath(path) && !audio) return false;

  const bitrate = audio?.bitrateKbps;
  const sampleRate = audio?.sampleRateHz;
  const bitDepth = audio?.bitDepth;
  const minBitrate = numeric(patch.minBitrate);
  const maxBitrate = numeric(patch.maxBitrate);
  const minSampleRate = numeric(patch.minSampleRate);
  const maxSampleRate = numeric(patch.maxSampleRate);
  const minBitDepth = numeric(patch.minBitDepth);
  const maxBitDepth = numeric(patch.maxBitDepth);
  if (minBitrate !== undefined && (bitrate === undefined || bitrate < minBitrate)) return false;
  if (maxBitrate !== undefined && (bitrate === undefined || bitrate > maxBitrate)) return false;
  if (minSampleRate !== undefined && (sampleRate === undefined || sampleRate < minSampleRate)) return false;
  if (maxSampleRate !== undefined && (sampleRate === undefined || sampleRate > maxSampleRate)) return false;
  if (minBitDepth !== undefined && (bitDepth === undefined || bitDepth < minBitDepth)) return false;
  if (maxBitDepth !== undefined && (bitDepth === undefined || bitDepth > maxBitDepth)) return false;

  const normalized = path.toLowerCase();
  if (record.draft.mode === 'split' && patch.strictArtist && record.draft.artist && !normalized.includes(record.draft.artist.toLowerCase())) return false;
  if (record.draft.mode === 'split' && patch.strictTitle && record.draft.title && !basename(path).toLowerCase().includes(record.draft.title.toLowerCase())) return false;
  if (record.draft.mode === 'split' && patch.strictAlbum && record.draft.title && !normalized.includes(record.draft.title.toLowerCase())) return false;

  if (record.submission.kind === 'track') {
    if (expectedLength !== undefined) {
      const tolerance = numeric(patch.lengthTolerance) ?? 0;
      if (audio?.lengthSeconds === undefined || Math.abs(audio.lengthSeconds - expectedLength) > tolerance) return false;
    } else if (patch.acceptNoLength === false && audio?.lengthSeconds === undefined) {
      return false;
    }
  }
  return true;
}

function matchesNecessaryProjection(
  record: SearchRecord,
  result: ProjectedSearchResult,
  request: ProposedSearchResultProjectionRequestDto,
): boolean {
  const query = request.filterText?.trim().toLowerCase();
  const haystack = result.kind === 'album'
    ? `${result.peer.username} ${result.path} ${result.files.map((file) => file.relativePath).join(' ')}`
    : `${result.peer.username} ${result.path}`;
  if (query && !haystack.toLowerCase().includes(query)) return false;

  if (result.kind === 'track') {
    const expectedLength = request.projection.kind === 'track'
      ? numeric(request.projection.request.songQuery?.length)
      : undefined;
    return fileMatchesPatch(record, result, result.path, result.audio, request.search.necessaryCond, expectedLength);
  }

  const folderPatch = request.search.necessaryFolderCond;
  const audioFiles = result.files.filter((file) => isAudioPath(file.relativePath));
  const matchingFiles = audioFiles.filter((file) => fileMatchesPatch(record, result, file.relativePath, file.audio, request.search.necessaryCond));
  const minTracks = numeric(folderPatch?.minTrackCount);
  const maxTracks = numeric(folderPatch?.maxTrackCount);
  if (minTracks !== undefined && audioFiles.length < minTracks) return false;
  if (maxTracks !== undefined && audioFiles.length > maxTracks) return false;
  for (const title of patchValues(folderPatch?.requiredTrackTitles)) {
    if (!result.files.some((file) => file.relativePath.toLowerCase().includes(title.toLowerCase()))) return false;
  }
  if (request.search.strictAlbumQuality) return matchingFiles.length === audioFiles.length;
  return request.search.necessaryCond && Object.values(request.search.necessaryCond).some((value) => value !== null && value !== undefined && value !== false)
    ? matchingFiles.length > 0
    : true;
}

function hasRankingPatch(request: ProposedSearchResultProjectionRequestDto): boolean {
  const patch = request.search.preferredCond;
  return Boolean(patch && Object.values(patch).some((value) => value !== null && value !== undefined && value !== false));
}

function withProjectedPreference(
  record: SearchRecord,
  result: ProjectedSearchResult,
  request: ProposedSearchResultProjectionRequestDto,
): ProjectedSearchResult {
  if (!hasRankingPatch(request)) return result;
  const preferred = result.kind === 'track'
    ? fileMatchesPatch(
        record,
        result,
        result.path,
        result.audio,
        request.search.preferredCond,
        request.projection.kind === 'track' ? numeric(request.projection.request.songQuery?.length) : undefined,
      )
    : result.files.some((file) => fileMatchesPatch(record, result, file.relativePath, file.audio, request.search.preferredCond));
  return {
    ...result,
    preferred,
    preference: {
      ...result.preference,
      tier: preferred ? 'preferred' : 'other',
      matchedPreferenceKeys: preferred ? ['configured-ranking'] : [],
    },
  };
}

export function buildSearchResultProjectionRequest(
  record: SearchRecord,
  filterText: string,
  sort: SearchSort,
  sizeDirection: SizeSortDirection,
  cursor: string | null,
  limit: number,
): ProposedSearchResultProjectionRequestDto {
  const projection: ProposedSearchResultProjectionRequestDto['projection'] = record.submission.kind === 'track'
    ? {
        kind: 'track',
        request: {
          songQuery: {
            ...record.submission.request.songQuery,
            length: numeric(record.conditions.track.expectedLength) ?? null,
          },
          includeFullResults: true,
        },
      }
    : {
        kind: 'album',
        request: {
          albumQuery: record.submission.request.albumQuery,
          includeFiles: true,
        },
      };
  return {
    filterText: filterText.trim() || null,
    projection,
    search: toNecessarySearchPatch(record.draft.resultMode, record.conditions),
    order: sort === 'speed'
      ? 'upload-speed'
      : sort === 'queue'
        ? 'queue-depth'
        : sort === 'size'
          ? `item-size-${sizeDirection === 'asc' ? 'ascending' : 'descending'}`
          : 'relevance',
    cursor,
    limit,
  };
}

/** Mock daemon boundary: filter, conditions, ranking, and order precede paging. */
export function requestSearchResultProjection(
  record: SearchRecord,
  request: ProposedSearchResultProjectionRequestDto,
): SearchResultProjectionPage {
  const projected = availableSearchResultCorpus(record)
    .filter((result) => matchesNecessaryProjection(record, result, request))
    .map((result) => withProjectedPreference(record, result, request));

  if (request.order === 'relevance') projected.sort((a, b) => Number(b.preferred) - Number(a.preferred));
  else if (request.order === 'upload-speed') projected.sort((a, b) => b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps);
  else if (request.order === 'queue-depth') projected.sort((a, b) => a.peer.queueLength - b.peer.queueLength || b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps);
  else if (request.order === 'item-size-ascending') projected.sort((a, b) => a.sizeBytes - b.sizeBytes);
  else projected.sort((a, b) => b.sizeBytes - a.sizeBytes);

  const offset = Math.max(0, numeric(request.cursor) ?? 0);
  const items = projected.slice(offset, offset + request.limit);
  return {
    items,
    totalCount: projected.length,
    nextCursor: offset + items.length < projected.length ? String(offset + items.length) : null,
    request,
  };
}


function conditionsFor(_mode: SearchResultMode, _variant: 'default' | 'simple' | 'aphex'): PrototypeSearchConditions {
  return createPrototypeSearchConditions();
}

function makeRecord(
  index: number,
  draft: SearchDraft,
  status: SearchStatus,
  foundFiles: number,
  lockedFiles: number,
  distinctPeers: number,
  when: string,
  fixture: SearchRecord['fixture'],
  resultState: SearchResultPersistenceState = 'available',
  lifetime: PrototypeDataLifetime = 'retained',
): SearchRecord {
  const conditions = conditionsFor(draft.resultMode, fixture === 'aphex' ? 'aphex' : fixture === 'boards' ? 'default' : 'simple');
  const submission = buildSearchSubmission(draft, conditions);
  return {
    id: prototypeUuid(0x50000000, index),
    workflowId: prototypeUuid(0x50010000, index),
    parentJobId: null,
    sourceJobId: null,
    draft: { ...draft },
    displayQuery: displayQueryForDraft(draft),
    status,
    resultState,
    lifetime,
    createdAtUtc: new Date(Date.parse('2026-08-07T08:15:00Z') - index * 11 * 60_000).toISOString(),
    foundFiles,
    lockedFiles,
    distinctPeers,
    when,
    conditions: cloneSearchConditions(conditions),
    submittedConditions: immutableSubmittedConditions(conditions),
    submission: cloneSearchSubmission(submission),
    fixture,
    pagination: { resultPageSize: draft.resultMode === 'album' ? albumResults.length : trackResults.length, resultHasMore: false, resultPagesLoaded: 1, historyPage: index <= 4 ? 1 : 2 },
  };
}

export const defaultSearchId = prototypeUuid(0x50000000, 2);

export function createInitialSearches(scenario: 'normal' | 'busy' | 'empty' | 'offline' | 'stress' = 'normal'): SearchRecord[] {
  if (scenario === 'empty') return [];
  const records: SearchRecord[] = [
    makeRecord(1, { mode: 'split', resultMode: 'track', query: '', artist: 'Autechre', title: 'Gantz Graf' }, 'searching', 187, 14, 42, 'just now', 'autechre'),
    makeRecord(2, { mode: 'split', resultMode: 'album', query: '', artist: 'Boards of Canada', title: 'Geogaddi' }, 'receiving', 412, 27, 68, '4 min ago', 'boards'),
    makeRecord(3, { mode: 'split', resultMode: 'track', query: '', artist: 'Aphex Twin', title: 'Xtal' }, 'complete', 93, 5, 31, '26 min ago', 'aphex'),
    makeRecord(4, { mode: 'split', resultMode: 'album', query: '', artist: 'Burial', title: 'Untrue' }, 'complete', 221, 18, 57, '1 h ago', 'burial'),
    makeRecord(5, { mode: 'split', resultMode: 'track', query: '', artist: 'Biosphere', title: 'Poa Alpina' }, 'complete', 64, 2, 19, 'Yesterday', 'historical'),
  ];

  if (scenario === 'busy') {
    records[0] = { ...records[0]!, status: 'pending' };
    records[1] = { ...records[1]!, resultState: 'incomplete', pagination: { ...records[1]!.pagination, resultHasMore: true } };
  }
  if (scenario === 'stress') {
    records[0] = { ...records[0]!, status: 'interrupted', resultState: 'interrupted', lifetime: 'interrupted', pagination: { ...records[0]!.pagination, resultHasMore: true } };
    records[1] = { ...records[1]!, resultState: 'incomplete', pagination: { ...records[1]!.pagination, resultHasMore: true } };
    records[2] = { ...records[2]!, status: 'failed' };
    records[3] = { ...records[3]!, status: 'cancelled' };
    records[4] = { ...records[4]!, status: 'skipped', resultState: 'pruned', lifetime: 'pruned' };
    records.push(makeRecord(6, { mode: 'simple', resultMode: 'album', query: 'old unavailable search', artist: '', title: '' }, 'complete', 0, 0, 0, '30 d ago', 'historical', 'not-persisted', 'live-only'));
  }
  return records;
}

export function displayQueryForDraft(draft: SearchDraft): string {
  if (draft.mode === 'split') return [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' - ');
  return draft.query.trim();
}

let createdSearchSequence = 100;

export function createSearchRecord(draft: SearchDraft, conditions: PrototypeSearchConditions): SearchRecord {
  createdSearchSequence += 1;
  const submission = buildSearchSubmission(draft, conditions);
  return {
    id: prototypeUuid(0x50000000, createdSearchSequence),
    workflowId: prototypeUuid(0x50010000, createdSearchSequence),
    parentJobId: null,
    sourceJobId: null,
    draft: { ...draft },
    displayQuery: displayQueryForDraft(draft) || 'Untitled search',
    status: 'pending',
    resultState: 'available',
    lifetime: 'live',
    createdAtUtc: new Date().toISOString(),
    foundFiles: draft.resultMode === 'album' ? 128 : 187,
    lockedFiles: draft.resultMode === 'album' ? 9 : 14,
    distinctPeers: draft.resultMode === 'album' ? 24 : 42,
    when: 'just now',
    conditions: cloneSearchConditions(conditions),
    submittedConditions: immutableSubmittedConditions(conditions),
    submission: cloneSearchSubmission(submission),
    fixture: 'generic',
    pagination: { resultPageSize: draft.resultMode === 'album' ? albumResults.length : trackResults.length, resultHasMore: true, resultPagesLoaded: 1, historyPage: 1 },
  };
}

export function rerunSearchRecord(previous: SearchRecord): SearchRecord {
  createdSearchSequence += 1;
  return {
    ...previous,
    id: prototypeUuid(0x50000000, createdSearchSequence),
    workflowId: prototypeUuid(0x50010000, createdSearchSequence),
    sourceJobId: previous.id,
    status: 'pending',
    resultState: 'available',
    lifetime: 'live',
    createdAtUtc: new Date().toISOString(),
    when: 'just now',
    conditions: cloneSearchConditions(previous.submittedConditions),
    submittedConditions: immutableSubmittedConditions(previous.submittedConditions),
    submission: cloneSearchSubmission(previous.submission),
  };
}
