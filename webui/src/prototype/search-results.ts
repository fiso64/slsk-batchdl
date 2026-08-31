import type { components } from '../api/generated';
import type { SearchDraft, SearchResultMode } from './search';
import type { AutomaticJobSkipReason } from './job-types';
import { isAggregateSearchMode, searchModeFamily } from './search';
import type { AudioAttributes, ItemPeerInfo } from './items';
import { basename } from './items';
import { extension, isAudioFilePath } from './file-types';
import type { PrototypeDataLifetime } from './state';
import type {
  ProposedGenericDirectoryRetrievalRequestDto,
  ProposedPreferredResultDto,
  ProposedSearchResultProjectionRequestDto,
} from './contracts/search';
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
export type SearchView = 'list' | 'results' | 'wishlist';
export type SearchSort = 'relevance' | 'speed' | 'queue' | 'size' | 'count' | 'name';
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
  skipReason?: AutomaticJobSkipReason;
  resultState: SearchResultPersistenceState;
  lifetime: PrototypeDataLifetime;
  createdAtUtc: string;
  foundFiles: number;
  lockedFiles: number;
  distinctPeers: number;
  aggregateGroupCount?: number;
  when: string;
  conditions: PrototypeSearchConditions;
  submittedConditions: PrototypeSearchConditions;
  submission: PrototypeSearchSubmission;
  fixture: 'autechre' | 'boards' | 'aphex' | 'burial' | 'generic' | 'manuals' | 'historical';
  pagination: { resultPageSize: number; resultHasMore: boolean; resultPagesLoaded: number; historyPage: number };
}

export type { AudioAttributes };

type FileCandidateRefDto = components['schemas']['FileCandidateRefDto'];
type StartFileDownloadsRequestDto = components['schemas']['StartFileDownloadsRequestDto'];
type StartFolderDownloadRequestDto = components['schemas']['StartFolderDownloadRequestDto'];
type AlbumFolderRefDto = components['schemas']['AlbumFolderRefDto'];
type AlbumQueryDto = components['schemas']['AlbumQueryDto'];
type AggregateTrackProjectionRequestDto = components['schemas']['AggregateTrackProjectionRequestDto'];
type AggregateAlbumProjectionRequestDto = components['schemas']['AggregateAlbumProjectionRequestDto'];
type RetrieveFolderRequestDto = components['schemas']['RetrieveFolderRequestDto'];

export type AggregateSearchProjectionRequest =
  | { kind: 'song-aggregate'; request: AggregateTrackProjectionRequestDto }
  | { kind: 'album-aggregate'; request: AggregateAlbumProjectionRequestDto };

/** Generic File Search must opt into General so selected files become RemoteFile jobs. */
export function buildGenericFileDownloadRequest(
  files: FileCandidateRefDto[],
  options?: components['schemas']['SubmissionOptionsDto'],
): StartFileDownloadsRequestDto {
  return { files, requestedMode: 2, options: options ?? null };
}

export function buildTrackFileDownloadRequest(
  files: FileCandidateRefDto[],
  options?: components['schemas']['SubmissionOptionsDto'],
): StartFileDownloadsRequestDto {
  return { files, requestedMode: 0, options: options ?? null };
}

export function buildAlbumFolderDownloadRequest(
  album: AlbumSearchResult,
  selectedFileIds: Set<string>,
  options?: components['schemas']['SubmissionOptionsDto'],
  albumQuery?: AlbumQueryDto,
): StartFolderDownloadRequestDto {
  const selectedFiles = album.files.filter((file) => selectedFileIds.has(file.id));
  const wholeFolder = selectedFiles.length === album.files.length;
  return {
    folder: album.candidateRef,
    options: options ?? null,
    albumQuery: albumQuery ?? null,
    selection: wholeFolder ? null : {
      files: selectedFiles.map((file) => ({
        username: album.peer.username,
        filename: `${album.path.replace(/[\/]+$/, '')}/${file.relativePath.replace(/^[\/]+/, '')}`,
      })),
      exactFiles: true,
      skipTrackCountVerification: false,
    },
    requestedMode: 1,
  };
}

/** Album Search and Album Aggregate both use the daemon's existing retrieve-folder follow-up. */
export function buildAlbumFolderRetrievalRequest(album: AlbumSearchResult, albumQuery?: AlbumQueryDto): RetrieveFolderRequestDto {
  return { folder: album.candidateRef, albumQuery: albumQuery ?? null };
}

/** Generic File Search needs the same core directory browse through a generalized public follow-up contract. */
export function buildGenericDirectoryRetrievalRequest(directory: GenericDirectoryResult): ProposedGenericDirectoryRetrievalRequestDto {
  return { directory: { username: directory.peer.username, directoryPath: directory.path } };
}

/**
 * Mock daemon follow-up: a full-folder browse may discover ancillary files that
 * the search response did not include. Search-result selection remains per file.
 */
export function retrieveAlbumFolderFixture(album: AlbumSearchResult): AlbumSearchResult {
  if (album.retrievalState === 'retrieved') return album;
  const extras: AlbumFileResult[] = [
    { id: `${album.id}:retrieved:booklet`, relativePath: 'Scans/booklet.pdf', locked: album.locked, sizeBytes: 7_600_000 },
    { id: `${album.id}:retrieved:log`, relativePath: 'rip.log', locked: album.locked, sizeBytes: 58_000 },
  ];
  const existing = new Set(album.files.map((file) => file.relativePath.toLowerCase()));
  const newFiles = extras.filter((file) => !existing.has(file.relativePath.toLowerCase()));
  return {
    ...album,
    retrievalState: 'retrieved',
    files: [...album.files, ...newFiles],
    totalFileCount: album.files.length + newFiles.length,
    sizeBytes: album.sizeBytes + newFiles.reduce((total, file) => total + file.sizeBytes, 0),
  };
}

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

export interface GenericFileResult extends AlbumFileResult {
  candidateRef: FileCandidateRefDto;
  preferred: boolean;
}

export interface GenericDirectoryResult {
  kind: 'generic-directory';
  id: string;
  peer: PeerInfo;
  path: string;
  locked: boolean;
  /** Sum of the files currently surviving generic per-file conditions. */
  sizeBytes: number;
  files: GenericFileResult[];
  totalFileCount: number;
  retrievalState: 'idle' | 'retrieving' | 'retrieved' | 'failed';
  preferred: boolean;
  preference: ProposedPreferredResultDto;
}

export type ProjectedSearchResult = TrackSearchResult | AlbumSearchResult | GenericDirectoryResult;

export interface SongAggregateGroup {
  kind: 'song-aggregate';
  id: string;
  artist: string;
  title: string;
  itemName: string;
  shareCount: number;
  options: TrackSearchResult[];
}

export interface AlbumAggregateGroup {
  kind: 'album-aggregate';
  id: string;
  artist: string;
  album: string;
  itemName: string;
  shareCount: number;
  options: AlbumSearchResult[];
}

export type AggregateSearchGroup = SongAggregateGroup | AlbumAggregateGroup;


const nightshift: PeerInfo = { username: 'nightshift', uploadSpeedMbps: 12.8, freeUploadSlot: true, queueLength: 0 };
const cassetteculture: PeerInfo = { username: 'cassetteculture', uploadSpeedMbps: 8.4, freeUploadSlot: false, queueLength: 2 };
const cloudarchive: PeerInfo = { username: 'cloudarchive', uploadSpeedMbps: 6.1, freeUploadSlot: true, queueLength: 0 };
const tapeLoop: PeerInfo = { username: 'tape_loop', uploadSpeedMbps: 2.7, freeUploadSlot: false, queueLength: 7 };
const silvermachine: PeerInfo = { username: 'silvermachine', uploadSpeedMbps: 5.2, freeUploadSlot: false, queueLength: 3 };

function genericFile(
  directoryId: number,
  fileId: number,
  peer: PeerInfo,
  directoryPath: string,
  relativePath: string,
  sizeBytes: number,
  audio?: AudioAttributes,
  locked = false,
): GenericFileResult {
  const filename = `${directoryPath}/${relativePath}`;
  return {
    id: prototypeUuid(0x5b000000 + directoryId, fileId),
    relativePath,
    candidateRef: { username: peer.username, filename },
    locked,
    sizeBytes,
    audio,
    preferred: false,
  };
}

function genericDirectory(
  directoryId: number,
  peer: PeerInfo,
  path: string,
  files: Array<[string, number, AudioAttributes? , boolean?]>,
): GenericDirectoryResult {
  const projectedFiles = files.map(([relativePath, sizeBytes, audio, locked], index) =>
    genericFile(directoryId, index + 1, peer, path, relativePath, sizeBytes, audio, locked ?? false));
  const sizeBytes = projectedFiles.reduce((total, file) => total + file.sizeBytes, 0);
  return {
    kind: 'generic-directory',
    id: prototypeUuid(0x5c000000, directoryId),
    peer,
    path,
    locked: projectedFiles.length > 0 && projectedFiles.every((file) => file.locked),
    sizeBytes,
    files: projectedFiles,
    totalFileCount: projectedFiles.length,
    retrievalState: 'idle',
    preferred: false,
    preference: {
      candidateKey: `${peer.username}:${path}`,
      tier: 'other',
      matchedPreferenceKeys: [],
    },
  };
}

/** Mock generalized directory retrieval: newly browsed files still pass the generic per-file projection. */
export function retrieveGenericDirectoryFixture(directory: GenericDirectoryResult): GenericDirectoryResult {
  if (directory.retrievalState === 'retrieved') return directory;
  const extras: GenericFileResult[] = [
    genericFile(90, 1, directory.peer, directory.path, 'Supplemental/Errata.pdf', 860_000, undefined, directory.locked),
    genericFile(90, 2, directory.peer, directory.path, 'Supplemental/Quick reference.epub', 1_420_000, undefined, directory.locked),
  ].map((file, index) => ({ ...file, id: `${directory.id}:retrieved:${index + 1}` }));
  const existing = new Set(directory.files.map((file) => file.relativePath.toLowerCase()));
  const newFiles = extras.filter((file) => !existing.has(file.relativePath.toLowerCase()));
  return {
    ...directory,
    retrievalState: 'retrieved',
    files: [...directory.files, ...newFiles],
    totalFileCount: directory.files.length + newFiles.length,
    sizeBytes: directory.sizeBytes + newFiles.reduce((total, file) => total + file.sizeBytes, 0),
  };
}

/**
 * Raw-ish generic search fixture. Directories intentionally mix matching and
 * non-matching file types so the File Search projection can demonstrate that
 * generic conditions prune individual files instead of judging an album as a unit.
 */
export const genericDirectoryResults: GenericDirectoryResult[] = [
  genericDirectory(1, nightshift, 'Docs/Linux/Kernel manuals', [
    ['Linux Kernel Module Programming Guide.pdf', 3_800_000],
    ['Linux Device Drivers 3rd Edition.epub', 5_900_000],
    ['Networking/Linux Network Administrator Guide.pdf', 9_600_000],
    ['Networking/Linux Advanced Routing & Traffic Control.epub', 6_700_000],
    ['Networking/figures/topology.png', 1_400_000],
    ['Kernel API/Linux Device Driver Model.pdf', 2_900_000],
    ['Kernel API/legacy/Linux 2.6 Driver API.epub', 4_800_000],
    ['Kernel API/legacy/notes.txt', 64_000],
    ['cover.jpg', 1_200_000],
    ['README.txt', 42_000],
  ]),
  genericDirectory(2, cassetteculture, 'Library/Unix & Linux/Manuals', [
    ['Linux Administration Handbook.djvu', 18_700_000],
    ['Linux Pocket Guide.epub', 4_100_000],
    ['Linux Filesystem Hierarchy.pdf', 2_300_000],
    ['Linux command cheat sheet.pdf', 1_100_000],
    ['source.txt', 31_000],
    ['index.nfo', 18_000],
  ]),
  // A sibling directory with no surviving PDF/EPUB children disappears completely;
  // generic conditions never pull artwork along just because it shares a tree.
  genericDirectory(6, cassetteculture, 'Library/Unix & Linux/Manuals/scans', [
    ['front-cover.png', 2_600_000],
    ['back-cover.jpg', 2_200_000],
  ]),
  genericDirectory(3, cloudarchive, 'Archive/Linux documentation', [
    ['Linux Network Administrator Guide.pdf', 9_600_000],
    ['Linux Filesystem Hierarchy.pdf', 2_300_000],
    ['HOWTO/Networking/Linux Network HOWTO.pdf', 3_400_000],
    ['HOWTO/Storage/Linux RAID HOWTO.epub', 2_700_000],
    ['HOWTO/Storage/diagram.png', 930_000],
    ['sources.zip', 28_000_000],
    ['preview.jpg', 840_000],
  ]),
  genericDirectory(4, silvermachine, 'Reference/Manuals/Linux', [
    ['Linux Command Line and Shell Scripting Bible.epub', 7_900_000],
    ['Linux Command Line and Shell Scripting Bible.pdf', 14_500_000],
    ['Bash quick reference.pdf', 1_800_000],
    ['examples.zip', 6_200_000],
  ]),
  genericDirectory(5, tapeLoop, 'incoming/manuals', [
    ['linux manual collection.pdf', 61_000_000, undefined, true],
    ['Linux installation notes.epub', 2_200_000, undefined, true],
    ['install.png', 4_100_000, undefined, true],
    ['notes.txt', 93_000],
    ['linux-installation-audio.flac', 34_000_000, { bitrateKbps: 912, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 421 }],
  ]),
];

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


function aggregateTrackOption(
  groupIndex: number,
  optionIndex: number,
  peer: PeerInfo,
  path: string,
  sizeBytes: number,
  audio: AudioAttributes,
): TrackSearchResult {
  return {
    kind: 'track',
    id: prototypeUuid(0x56000000 + groupIndex, optionIndex),
    candidateRef: { username: peer.username, filename: path },
    peer,
    path,
    locked: false,
    sizeBytes,
    audio,
    preferred: optionIndex === 1,
    preference: {
      candidateKey: `${peer.username}:${path}`,
      tier: optionIndex === 1 ? 'preferred' : 'other',
      matchedPreferenceKeys: optionIndex === 1 ? ['relevance'] : [],
    },
  };
}

function aggregateAlbumOption(
  groupIndex: number,
  optionIndex: number,
  peer: PeerInfo,
  path: string,
  trackNames: string[],
  quality: 'flac' | 'mp3' | 'hires' = 'flac',
): AlbumSearchResult {
  const bitrateKbps = quality === 'mp3' ? 320 : quality === 'hires' ? 2050 : 930;
  const sampleRateHz = quality === 'hires' ? 96_000 : 44_100;
  const bitDepth = quality === 'mp3' ? undefined : quality === 'hires' ? 24 : 16;
  const extensionName = quality === 'mp3' ? 'mp3' : 'flac';
  const files: AlbumFileResult[] = trackNames.map((trackName, fileIndex) => {
    const lengthSeconds = 220 + ((groupIndex * 37 + fileIndex * 29) % 190);
    const sizeBytes = quality === 'mp3'
      ? 10_000_000 + fileIndex * 1_100_000
      : quality === 'hires'
        ? 82_000_000 + fileIndex * 5_500_000
        : 34_000_000 + fileIndex * 2_700_000;
    return {
      id: prototypeUuid(0x57000000 + groupIndex * 16 + optionIndex, fileIndex + 1),
      relativePath: `${String(fileIndex + 1).padStart(2, '0')} - ${trackName}.${extensionName}`,
      locked: false,
      sizeBytes,
      audio: { bitrateKbps, sampleRateHz, bitDepth, lengthSeconds },
    };
  });
  const sizeBytes = files.reduce((total, file) => total + file.sizeBytes, 0) + 1_800_000;
  return {
    kind: 'album',
    id: prototypeUuid(0x58000000 + groupIndex, optionIndex),
    candidateRef: { username: peer.username, folderPath: path },
    peer,
    path,
    locked: false,
    sizeBytes,
    files,
    preferred: optionIndex === 1,
    preference: {
      candidateKey: `${peer.username}:${path}`,
      tier: optionIndex === 1 ? 'preferred' : 'other',
      matchedPreferenceKeys: optionIndex === 1 ? ['relevance'] : [],
    },
    retrievalState: 'idle',
    totalFileCount: files.length,
  };
}

export const songAggregateGroups: SongAggregateGroup[] = [
  {
    kind: 'song-aggregate', id: prototypeUuid(0x59000000, 1), artist: 'Casiopea', title: 'Asayake', itemName: 'Asayake', shareCount: 5,
    options: [
      aggregateTrackOption(1, 1, nightshift, 'Jazz/Casiopea/Mint Jams/02 - Asayake.flac', 48_900_000, { bitrateKbps: 1010, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 296 }),
      aggregateTrackOption(1, 2, cassetteculture, 'Casiopea/1982 Mint Jams/02 Asayake.flac', 47_300_000, { bitrateKbps: 978, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 296 }),
      aggregateTrackOption(1, 3, cloudarchive, 'Fusion/Casiopea/Asayake.mp3', 11_800_000, { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 295 }),
      aggregateTrackOption(1, 4, silvermachine, 'lossless/casiopea/asayake.wav', 61_200_000, { sampleRateHz: 48_000, bitDepth: 24, lengthSeconds: 296 }),
      aggregateTrackOption(1, 5, tapeLoop, 'incoming/Casiopea - Asayake.flac', 46_700_000, { bitrateKbps: 962, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 297 }),
    ],
  },
  {
    kind: 'song-aggregate', id: prototypeUuid(0x59000000, 2), artist: 'Casiopea', title: 'Midnight Rendezvous', itemName: 'Midnight Rendezvous', shareCount: 4,
    options: [
      aggregateTrackOption(2, 1, cassetteculture, 'Casiopea/Casiopea/03 - Midnight Rendezvous.flac', 42_100_000, { bitrateKbps: 955, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 309 }),
      aggregateTrackOption(2, 2, nightshift, 'Jazz/Casiopea/Casiopea/03 Midnight Rendezvous.flac', 43_900_000, { bitrateKbps: 989, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 310 }),
      aggregateTrackOption(2, 3, cloudarchive, 'Fusion/Casiopea/Midnight Rendezvous.m4a', 9_900_000, { bitrateKbps: 256, sampleRateHz: 44_100, lengthSeconds: 309 }),
      aggregateTrackOption(2, 4, tapeLoop, 'Casiopea - Midnight Rendezvous.mp3', 12_000_000, { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 310 }),
    ],
  },
  {
    kind: 'song-aggregate', id: prototypeUuid(0x59000000, 3), artist: 'Casiopea', title: 'Galactic Funk', itemName: 'Galactic Funk', shareCount: 3,
    options: [
      aggregateTrackOption(3, 1, nightshift, 'Jazz/Casiopea/Mint Jams/04 - Galactic Funk.flac', 53_200_000, { bitrateKbps: 1004, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 384 }),
      aggregateTrackOption(3, 2, silvermachine, 'Casiopea/Mint Jams/Galactic Funk.flac', 51_600_000, { bitrateKbps: 971, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 383 }),
      aggregateTrackOption(3, 3, cloudarchive, 'Fusion/Casiopea/Galactic Funk.mp3', 14_900_000, { bitrateKbps: 320, sampleRateHz: 44_100, lengthSeconds: 384 }),
    ],
  },
  {
    kind: 'song-aggregate', id: prototypeUuid(0x59000000, 4), artist: 'Casiopea', title: 'Swallow', itemName: 'Swallow', shareCount: 1,
    options: [
      aggregateTrackOption(4, 1, cloudarchive, 'Fusion/Casiopea/Eyes of the Mind/08 - Swallow.flac', 44_600_000, { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 271 }),
    ],
  },
];

export const albumAggregateGroups: AlbumAggregateGroup[] = [
  {
    kind: 'album-aggregate', id: prototypeUuid(0x5a000000, 1), artist: 'Casiopea', album: 'Mint Jams', itemName: 'Mint Jams', shareCount: 5,
    options: [
      aggregateAlbumOption(1, 1, nightshift, 'Jazz/Casiopea/1982 - Mint Jams', ['Take Me', 'Asayake', 'Midnight Rendezvous', 'Time Limit'], 'flac'),
      aggregateAlbumOption(1, 2, cassetteculture, 'Casiopea/Mint Jams [Japan]', ['Take Me', 'Asayake', 'Midnight Rendezvous', 'Time Limit'], 'flac'),
      aggregateAlbumOption(1, 3, silvermachine, 'lossless/Casiopea/Mint Jams 24-96', ['Take Me', 'Asayake', 'Midnight Rendezvous', 'Time Limit'], 'hires'),
      aggregateAlbumOption(1, 4, cloudarchive, 'Fusion/Casiopea/Mint Jams MP3', ['Take Me', 'Asayake', 'Midnight Rendezvous', 'Time Limit'], 'mp3'),
      aggregateAlbumOption(1, 5, tapeLoop, 'incoming/Casiopea - Mint Jams', ['Take Me', 'Asayake', 'Midnight Rendezvous', 'Time Limit'], 'flac'),
    ],
  },
  {
    kind: 'album-aggregate', id: prototypeUuid(0x5a000000, 2), artist: 'Casiopea', album: 'Make Up City', itemName: 'Make Up City', shareCount: 4,
    options: [
      aggregateAlbumOption(2, 1, cassetteculture, 'Casiopea/1980 - Make Up City', ['Gypsy Wind', 'Eyes of Mind', 'Reflections of You', 'Ripple Dance'], 'flac'),
      aggregateAlbumOption(2, 2, nightshift, 'Jazz/Casiopea/Make Up City', ['Gypsy Wind', 'Eyes of Mind', 'Reflections of You', 'Ripple Dance'], 'flac'),
      aggregateAlbumOption(2, 3, cloudarchive, 'Fusion/Casiopea/Make Up City', ['Gypsy Wind', 'Eyes of Mind', 'Reflections of You', 'Ripple Dance'], 'mp3'),
      aggregateAlbumOption(2, 4, tapeLoop, 'incoming/Casiopea Make Up City', ['Gypsy Wind', 'Eyes of Mind', 'Reflections of You', 'Ripple Dance'], 'flac'),
    ],
  },
  {
    kind: 'album-aggregate', id: prototypeUuid(0x5a000000, 3), artist: 'Casiopea', album: 'Eyes of the Mind', itemName: 'Eyes of the Mind', shareCount: 3,
    options: [
      aggregateAlbumOption(3, 1, nightshift, 'Jazz/Casiopea/1981 - Eyes of the Mind', ['A Place in the Sun', 'Take Me', 'Eyes of the Mind', 'Swallow'], 'flac'),
      aggregateAlbumOption(3, 2, silvermachine, 'lossless/Casiopea/Eyes of the Mind', ['A Place in the Sun', 'Take Me', 'Eyes of the Mind', 'Swallow'], 'hires'),
      aggregateAlbumOption(3, 3, cloudarchive, 'Fusion/Casiopea/Eyes of the Mind MP3', ['A Place in the Sun', 'Take Me', 'Eyes of the Mind', 'Swallow'], 'mp3'),
    ],
  },
  {
    kind: 'album-aggregate', id: prototypeUuid(0x5a000000, 4), artist: 'Casiopea', album: 'Super Flight', itemName: 'Super Flight', shareCount: 2,
    options: [
      aggregateAlbumOption(4, 1, cassetteculture, 'Casiopea/1979 - Super Flight', ['Take Me', 'Sailing Alone', 'Olion', 'Magic Ray'], 'flac'),
      aggregateAlbumOption(4, 2, tapeLoop, 'Casiopea/Super Flight', ['Take Me', 'Sailing Alone', 'Olion', 'Magic Ray'], 'flac'),
    ],
  },
  {
    kind: 'album-aggregate', id: prototypeUuid(0x5a000000, 5), artist: 'Casiopea', album: 'Cross Point', itemName: 'Cross Point', shareCount: 1,
    options: [
      aggregateAlbumOption(5, 1, silvermachine, 'lossless/Casiopea/1981 - Cross Point', ['Smile Again', 'Swallow', 'A Sparkling Day', 'Galactic Funk'], 'hires'),
    ],
  },
];

export function buildAggregateSearchProjectionRequest(
  record: SearchRecord,
  includeOptions = false,
): AggregateSearchProjectionRequest {
  if (record.submission.kind === 'song-aggregate') {
    return {
      kind: 'song-aggregate',
      request: { songQuery: record.submission.request.songQuery, includeCandidates: includeOptions },
    };
  }
  if (record.submission.kind === 'album-aggregate') {
    return {
      kind: 'album-aggregate',
      request: { albumQuery: record.submission.request.albumQuery, includeFolders: includeOptions },
    };
  }
  throw new Error('Aggregate projection requested for a non-aggregate search view.');
}

export function aggregateGroupsForRecord(record: SearchRecord, filterText = ''): AggregateSearchGroup[] {
  if (!isAggregateSearchMode(record.draft.resultMode)) return [];
  if ((record.status === 'pending' || record.status === 'searching') && record.foundFiles === 0) return [];
  // Aggregate modes are SearchJob projections, not AggregateJob/AlbumAggregateJob submissions.
  const projection = buildAggregateSearchProjectionRequest(record, false);
  const source: AggregateSearchGroup[] = projection.kind === 'album-aggregate' ? albumAggregateGroups : songAggregateGroups;
  const query = filterText.trim().toLowerCase();
  const minShares = Math.max(1, Number(record.conditions.aggregate.minShares || 1));
  return source
    .filter((group) => group.shareCount >= minShares)
    .filter((group) => !query || `${group.artist} ${group.itemName} ${group.options.map((option) => option.path).join(' ')}`.toLowerCase().includes(query))
    .sort((a, b) => b.shareCount - a.shareCount);
}


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

function retargetTrackResult(result: TrackSearchResult, path: string): TrackSearchResult {
  return {
    ...result,
    path,
    candidateRef: { username: result.peer.username, filename: path },
    preference: { ...result.preference, candidateKey: `${result.peer.username}:${path}` },
  };
}

function trackResultsForRecord(record: SearchRecord): TrackSearchResult[] {
  const results: TrackSearchResult[] = trackResults.map((result) => ({ ...result, audio: result.audio ? { ...result.audio } : undefined }));
  const artist = record.draft.artist.trim();
  const title = record.draft.title.trim();
  if (!artist || !title) return results;

  if (record.fixture === 'autechre' || record.fixture === 'aphex') {
    // Saved FLAC-oriented track searches need a small number of candidates that
    // actually satisfy the saved strict-title ranking. The remaining corpus stays
    // deliberately noisy so the Preferred/Other partition remains visible.
    results[0] = retargetTrackResult(results[0]!, `Music/${artist}/${title}/${title}.flac`);
    results[1] = retargetTrackResult(results[1]!, `lossless/${artist}/${title}/01 - ${title}.flac`);
  } else if (record.fixture === 'historical') {
    // The ordinary Track Search ranking defaults prefer MP3. Give the historical
    // fixture one exact-title MP3 candidate without manufacturing a special rank.
    results[6] = retargetTrackResult(results[6]!, `incoming/${artist}/${title}.mp3`);
  }

  return results;
}

function retargetAlbumFile(file: AlbumFileResult, title: string, index: number): AlbumFileResult {
  if (!file.audio) return { ...file };
  const suffix = file.relativePath.includes('.') ? file.relativePath.slice(file.relativePath.lastIndexOf('.')) : '';
  const prefix = file.relativePath.includes('/') ? file.relativePath.slice(0, file.relativePath.lastIndexOf('/') + 1) : '';
  return { ...file, relativePath: `${prefix}${String(index + 1).padStart(2, '0')} - ${title}${suffix}` };
}

function retargetAlbumResult(
  result: AlbumSearchResult,
  path: string,
  trackTitles: string[],
): AlbumSearchResult {
  let audioIndex = 0;
  const files = result.files.map((file) => {
    if (!file.audio) return { ...file };
    const title = trackTitles[audioIndex] ?? `Track ${audioIndex + 1}`;
    const next = retargetAlbumFile(file, title, audioIndex);
    audioIndex += 1;
    return next;
  });
  return {
    ...result,
    path,
    candidateRef: { username: result.peer.username, folderPath: path },
    files,
    preference: { ...result.preference, candidateKey: `${result.peer.username}:${path}` },
  };
}

function makeAlbumResultHighResolution(result: AlbumSearchResult): AlbumSearchResult {
  return {
    ...result,
    files: result.files.map((file) => file.audio ? {
      ...file,
      audio: {
        ...file.audio,
        bitrateKbps: Math.max(file.audio.bitrateKbps ?? 0, 2050),
        sampleRateHz: 96_000,
        bitDepth: 24,
      },
    } : { ...file }),
  };
}

function albumResultsForRecord(record: SearchRecord): AlbumSearchResult[] {
  let results: AlbumSearchResult[] = albumResults.map((result) => ({
    ...result,
    files: result.files.map((file) => ({ ...file, audio: file.audio ? { ...file.audio } : undefined })),
  }));

  if (record.fixture === 'burial') {
    const artist = record.draft.artist.trim() || 'Burial';
    const album = record.draft.title.trim() || 'Untrue';
    const tracks = ['Untitled', 'Archangel', 'Near Dark', 'Ghost Hardware'];
    const paths = [
      `Music/${artist}/${album}`,
      `Music/${artist}/${album} [24bit]`,
      `lossless/${artist}/2007 - ${album}`,
      `Archive/${artist}/${album} (MP3)`,
      `${artist}/${album}`,
    ];
    results = results.map((result, index) => retargetAlbumResult(result, paths[index]!, tracks));
  }

  // Keep exactly two ordinary CD-quality FLAC folders in the preferred tier. A
  // third FLAC folder remains visible but exceeds the default preferred sample-rate
  // ceiling, making the distinction between necessary and preferred conditions clear.
  results[4] = makeAlbumResultHighResolution(results[4]!);
  return results;
}

function availableSearchResultCorpus(record: SearchRecord): ProjectedSearchResult[] {
  const family = searchModeFamily(record.draft.resultMode);
  if (family === 'generic') return genericDirectoryResults.map((directory) => ({
    ...directory,
    files: directory.files.map((file) => ({ ...file })),
  }));

  const firstPage: ProjectedSearchResult[] = family === 'album'
    ? albumResultsForRecord(record)
    : trackResultsForRecord(record);
  if (!record.pagination.resultHasMore && record.pagination.resultPagesLoaded <= 1) return firstPage;
  const secondPage: ProjectedSearchResult[] = family === 'album'
    ? (firstPage as AlbumSearchResult[]).map(alternateAlbumResult)
    : (firstPage as TrackSearchResult[]).map(alternateTrackResult);
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
  if (patch.acceptMissingProps === false && isAudioFilePath(path) && !audio) return false;

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

  if (record.submission.kind === 'track' || record.submission.kind === 'song-aggregate') {
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

  if (result.kind === 'generic-directory') return false;

  if (result.kind === 'track') {
    const expectedLength = request.projection.kind === 'track'
      ? numeric(request.projection.request.songQuery?.length)
      : undefined;
    return fileMatchesPatch(record, result, result.path, result.audio, request.search.necessaryCond, expectedLength);
  }

  const folderPatch = request.search.necessaryFolderCond;
  const audioFiles = result.files.filter((file) => isAudioFilePath(file.relativePath));
  const matchingFiles = audioFiles.filter((file) => fileMatchesPatch(record, result, `${result.path}/${file.relativePath}`, file.audio, request.search.necessaryCond));
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
  if (result.kind === 'generic-directory') return result;
  if (!hasRankingPatch(request)) {
    return {
      ...result,
      preferred: false,
      preference: { ...result.preference, tier: 'other', matchedPreferenceKeys: [] },
    };
  }
  const preferred = result.kind === 'track'
    ? fileMatchesPatch(
        record,
        result,
        result.path,
        result.audio,
        request.search.preferredCond,
        request.projection.kind === 'track' ? numeric(request.projection.request.songQuery?.length) : undefined,
      )
    : result.files.some((file) => fileMatchesPatch(record, result, `${result.path}/${file.relativePath}`, file.audio, request.search.preferredCond));
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

function projectGenericDirectory(
  record: SearchRecord,
  directory: GenericDirectoryResult,
  request: ProposedSearchResultProjectionRequestDto,
): GenericDirectoryResult | null {
  const filter = request.filterText?.trim().toLowerCase() ?? '';
  const directoryMatchesFilter = !filter || `${directory.peer.username} ${directory.path}`.toLowerCase().includes(filter);
  const preferredPatch = request.search.preferredCond;
  const hasPreference = hasRankingPatch(request);
  const survivingFiles = directory.files
    .filter((file) => fileMatchesPatch(record, directory, file.candidateRef.filename, file.audio, request.search.necessaryCond))
    .filter((file) => directoryMatchesFilter || file.relativePath.toLowerCase().includes(filter))
    .map((file) => ({
      ...file,
      preferred: hasPreference
        ? fileMatchesPatch(record, directory, file.candidateRef.filename, file.audio, preferredPatch)
        : false,
    }))
    .sort((a, b) => a.relativePath.localeCompare(b.relativePath, undefined, { numeric: true, sensitivity: 'base' }));

  if (!survivingFiles.length) return null;
  const preferred = survivingFiles.some((file) => file.preferred);
  return {
    ...directory,
    locked: survivingFiles.every((file) => file.locked),
    files: survivingFiles,
    totalFileCount: survivingFiles.length,
    sizeBytes: survivingFiles.reduce((total, file) => total + file.sizeBytes, 0),
    preferred,
    preference: {
      ...directory.preference,
      tier: preferred ? 'preferred' : 'other',
      matchedPreferenceKeys: preferred ? ['best-child-file'] : [],
    },
  };
}

function compareGenericDirectories(
  a: GenericDirectoryResult,
  b: GenericDirectoryResult,
  order: ProposedSearchResultProjectionRequestDto['order'],
): number {
  if (order === 'upload-speed') return b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps || a.path.localeCompare(b.path);
  if (order === 'directory-size-ascending') return a.sizeBytes - b.sizeBytes || a.path.localeCompare(b.path);
  if (order === 'directory-size-descending') return b.sizeBytes - a.sizeBytes || a.path.localeCompare(b.path);
  if (order === 'file-count-ascending') return a.files.length - b.files.length || a.path.localeCompare(b.path);
  if (order === 'file-count-descending') return b.files.length - a.files.length || a.path.localeCompare(b.path);
  if (order === 'directory-name-ascending') return a.path.localeCompare(b.path, undefined, { numeric: true, sensitivity: 'base' });
  if (order === 'directory-name-descending') return b.path.localeCompare(a.path, undefined, { numeric: true, sensitivity: 'base' });
  // Relevance is the daemon ordering of each directory's best surviving child.
  // The prototype approximates that lexical key with preference, slot and speed;
  // production requires the server-owned grouped projection documented in the audit.
  return Number(b.preferred) - Number(a.preferred)
    || Number(b.peer.freeUploadSlot) - Number(a.peer.freeUploadSlot)
    || b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps
    || a.path.localeCompare(b.path);
}

export function buildSearchResultProjectionRequest(
  record: SearchRecord,
  filterText: string,
  sort: SearchSort,
  sizeDirection: SizeSortDirection,
  cursor: string | null,
  limit: number,
): ProposedSearchResultProjectionRequestDto {
  const family = searchModeFamily(record.draft.resultMode);
  let projection: ProposedSearchResultProjectionRequestDto['projection'];
  if (record.submission.kind === 'generic') {
    projection = { kind: 'generic-directory', request: {} };
  } else if (record.submission.kind === 'track' || record.submission.kind === 'song-aggregate') {
    projection = {
      kind: 'track',
      request: {
        songQuery: {
          ...record.submission.request.songQuery,
          length: numeric(record.conditions.track.expectedLength) ?? null,
        },
        includeFullResults: true,
      },
    };
  } else {
    projection = {
      kind: 'album',
      request: {
        albumQuery: record.submission.request.albumQuery,
        includeFiles: true,
      },
    };
  }

  let order: ProposedSearchResultProjectionRequestDto['order'] = 'relevance';
  if (sort === 'speed') order = 'upload-speed';
  else if (family === 'generic' && sort === 'size') order = `directory-size-${sizeDirection === 'asc' ? 'ascending' : 'descending'}`;
  else if (family === 'generic' && sort === 'count') order = `file-count-${sizeDirection === 'asc' ? 'ascending' : 'descending'}`;
  else if (family === 'generic' && sort === 'name') order = `directory-name-${sizeDirection === 'asc' ? 'ascending' : 'descending'}`;
  else if (sort === 'queue') order = 'queue-depth';
  else if (sort === 'size') order = `item-size-${sizeDirection === 'asc' ? 'ascending' : 'descending'}`;

  return {
    filterText: filterText.trim() || null,
    projection,
    search: toNecessarySearchPatch(record.draft.resultMode, record.conditions),
    order,
    cursor,
    limit,
  };
}

/** Mock daemon boundary: filter, conditions, ranking, and order precede paging. */
export function requestSearchResultProjection(
  record: SearchRecord,
  request: ProposedSearchResultProjectionRequestDto,
): SearchResultProjectionPage {
  if ((record.status === 'pending' || record.status === 'searching') && record.foundFiles === 0) {
    return { items: [], totalCount: 0, nextCursor: null, request };
  }

  let projected: ProjectedSearchResult[];
  if (request.projection.kind === 'generic-directory') {
    projected = availableSearchResultCorpus(record)
      .filter((result): result is GenericDirectoryResult => result.kind === 'generic-directory')
      .map((directory) => projectGenericDirectory(record, directory, request))
      .filter((directory): directory is GenericDirectoryResult => directory !== null)
      .sort((a, b) => compareGenericDirectories(a, b, request.order));
  } else {
    projected = availableSearchResultCorpus(record)
      .filter((result) => result.kind !== 'generic-directory')
      .filter((result) => matchesNecessaryProjection(record, result, request))
      .map((result) => withProjectedPreference(record, result, request));

    if (request.order === 'relevance') projected.sort((a, b) => Number(b.preferred) - Number(a.preferred));
    else if (request.order === 'upload-speed') projected.sort((a, b) => b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps);
    else if (request.order === 'queue-depth') projected.sort((a, b) => a.peer.queueLength - b.peer.queueLength || b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps);
    else if (request.order === 'item-size-ascending') projected.sort((a, b) => a.sizeBytes - b.sizeBytes);
    else if (request.order === 'item-size-descending') projected.sort((a, b) => b.sizeBytes - a.sizeBytes);
  }

  const offset = Math.max(0, numeric(request.cursor) ?? 0);
  const items = projected.slice(offset, offset + request.limit);
  return {
    items,
    totalCount: projected.length,
    nextCursor: offset + items.length < projected.length ? String(offset + items.length) : null,
    request,
  };
}


function conditionsFor(mode: SearchResultMode, variant: 'default' | 'simple' | 'aphex' | 'manuals'): PrototypeSearchConditions {
  const conditions = createPrototypeSearchConditions(mode);
  const family = searchModeFamily(mode);
  if (family === 'generic' || variant === 'manuals') {
    // Generic search demonstrates simple per-file semantics: only PDF/EPUB files
    // survive, while PDF is a ranking preference. Neighboring cover/archive files
    // do not hitchhike into the projected directory.
    conditions.common.formats = ['PDF', 'EPUB'];
    conditions.ranking.common.formats = ['PDF'];
    return conditions;
  }
  if (variant === 'default' && family === 'album') {
    // Album mode demonstrates coverage semantics: FLAC determines whether an album
    // is acceptable, but ancillary artwork remains part of an accepted folder.
    // This saved-search fixture also prefers FLAC so its default result view visibly
    // demonstrates the Preferred tier rather than requiring a ranking edit first.
    conditions.common.formats = ['FLAC'];
    conditions.ranking.common.formats = ['FLAC'];
  } else if (variant === 'aphex' && family === 'track') {
    // Track mode uses the same format condition as an individual-file predicate.
    // Keep the fixture ranking aligned with that condition so one obvious matching
    // result lands in Preferred under the saved search's default configuration.
    conditions.common.formats = ['FLAC'];
    conditions.ranking.common.formats = ['FLAC'];
  }
  return conditions;
}

function aggregateGroupCountForConditions(
  mode: SearchResultMode,
  conditions: PrototypeSearchConditions,
): number | undefined {
  const source = mode === 'album-aggregate' ? albumAggregateGroups : mode === 'song-aggregate' ? songAggregateGroups : null;
  if (!source) return undefined;
  const minShares = Math.max(1, Number(conditions.aggregate.minShares || 1));
  return source.filter((group) => group.shareCount >= minShares).length;
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
  const conditions = conditionsFor(draft.resultMode, fixture === 'manuals' ? 'manuals' : fixture === 'aphex' || fixture === 'autechre' ? 'aphex' : fixture === 'boards' ? 'default' : 'simple');
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
    aggregateGroupCount: aggregateGroupCountForConditions(draft.resultMode, conditions),
    when,
    conditions: cloneSearchConditions(conditions),
    submittedConditions: immutableSubmittedConditions(conditions),
    submission: cloneSearchSubmission(submission),
    fixture,
    pagination: { resultPageSize: searchModeFamily(draft.resultMode) === 'generic' ? genericDirectoryResults.length : searchModeFamily(draft.resultMode) === 'album' ? albumResults.length : trackResults.length, resultHasMore: false, resultPagesLoaded: 1, historyPage: index <= 4 ? 1 : 2 },
  };
}

export const defaultSearchId = prototypeUuid(0x50000000, 2);

export function createInitialSearches(scenario: 'normal' | 'busy' | 'loading' | 'empty' | 'offline' | 'stress' = 'normal'): SearchRecord[] {
  if (scenario === 'empty') return [];
  if (scenario === 'loading') {
    return [makeRecord(2, { mode: 'split', resultMode: 'album', query: '', artist: 'Boards of Canada', title: 'Geogaddi' }, 'searching', 0, 0, 0, 'just now', 'boards', 'available', 'live')];
  }
  const records: SearchRecord[] = [
    makeRecord(8, { mode: 'simple', resultMode: 'generic', query: 'linux manual', artist: '', title: '' }, 'complete', 23, 2, 5, 'just now', 'manuals'),
    makeRecord(1, { mode: 'split', resultMode: 'track', query: '', artist: 'Autechre', title: 'Gantz Graf' }, 'searching', 187, 14, 42, '2 min ago', 'autechre'),
    makeRecord(2, { mode: 'split', resultMode: 'album', query: '', artist: 'Boards of Canada', title: 'Geogaddi' }, 'receiving', 412, 27, 68, '4 min ago', 'boards'),
    makeRecord(6, { mode: 'split', resultMode: 'album-aggregate', query: '', artist: 'Casiopea', title: '' }, 'complete', 536, 8, 91, '11 min ago', 'generic'),
    makeRecord(7, { mode: 'split', resultMode: 'song-aggregate', query: '', artist: 'Casiopea', title: '' }, 'complete', 684, 6, 104, '18 min ago', 'generic'),
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
    records[4] = { ...records[4]!, status: 'skipped', skipReason: 'Filtered', resultState: 'pruned', lifetime: 'pruned' };
    records.push(makeRecord(9, { mode: 'simple', resultMode: 'album', query: 'old unavailable search', artist: '', title: '' }, 'complete', 0, 0, 0, '30 d ago', 'historical', 'not-persisted', 'live-only'));
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
  const family = searchModeFamily(draft.resultMode);
  const aggregate = isAggregateSearchMode(draft.resultMode);
  const aggregateGroupCount = aggregateGroupCountForConditions(draft.resultMode, conditions);
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
    foundFiles: family === 'generic' ? 96 : family === 'album' ? 128 : 187,
    lockedFiles: family === 'generic' ? 3 : family === 'album' ? 9 : 14,
    distinctPeers: family === 'generic' ? 18 : family === 'album' ? 24 : 42,
    aggregateGroupCount,
    when: 'just now',
    conditions: cloneSearchConditions(conditions),
    submittedConditions: immutableSubmittedConditions(conditions),
    submission: cloneSearchSubmission(submission),
    fixture: 'generic',
    pagination: { resultPageSize: family === 'generic' ? genericDirectoryResults.length : family === 'album' ? albumResults.length : trackResults.length, resultHasMore: family === 'generic' || aggregate ? false : true, resultPagesLoaded: 1, historyPage: 1 },
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
