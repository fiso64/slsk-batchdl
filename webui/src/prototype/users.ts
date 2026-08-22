import type { components } from '../api/generated';
import type { ScenarioId } from '../mock/types';
import { prototypeNumericId, prototypeUuid } from './ids';
import type { PrototypeDataLifetime, ProposedShareTreeFilterRequestDto } from './backend-contracts';

export type UserProfileDto = components['schemas']['UserProfileDto'];
export type UserBrowseDto = components['schemas']['UserBrowseDto'];
export type BrowseDirectoryEntryDto = components['schemas']['BrowseDirectoryEntryDto'];
export type BrowseFileEntryDto = components['schemas']['BrowseFileEntryDto'];
export type ShareVisibility = components['schemas']['ShareVisibility'];
export type UserPresence = components['schemas']['UserProfilePresence'];
export type UserBrowseView = 'user' | 'shares';

export interface UserBrowseDraft {
  query: string;
  mode: UserBrowseView;
}

export interface UserProfile {
  username: string;
  presence: UserPresence;
  imageUrl?: string;
  description?: string;
  averageUploadSpeed: number | null;
  uploadCount: number | null;
  uploadSlots: number | null;
  queuedUploads: number | null;
  hasFreeUploadSlot: boolean | null;
}

export interface UserShareFile {
  id: string;
  name: string;
  sizeBytes: number;
  visibility?: ShareVisibility;
}

export interface UserShareFolder {
  id: string;
  name: string;
  visibility?: ShareVisibility;
  files?: UserShareFile[];
  folders?: UserShareFolder[];
}

interface RawUserBrowseFixture {
  profile: UserProfile;
  shares: UserShareFolder[];
}

export interface UserBrowseFixture extends RawUserBrowseFixture {
  profileDto: UserProfileDto;
  browseDto: UserBrowseDto;
  directoryDtos: BrowseDirectoryEntryDto[];
  fileDtos: BrowseFileEntryDto[];
  profileLifetime: PrototypeDataLifetime;
  browseLifetime: PrototypeDataLifetime;
}

export interface ShareMetrics {
  files: number;
  folders: number;
  sizeBytes: number;
}

export interface ShareTreeRow {
  kind: 'folder' | 'file';
  id: string;
  name: string;
  path: string;
  depth: number;
  sizeBytes: number;
  fileIds: string[];
  parentFolderIds: string[];
  parentDirectoryId?: string;
  directoryId?: string;
  fileId?: string;
  visibility: ShareVisibility;
}

export interface ShareTreeProjectionPage {
  rows: ShareTreeRow[];
  matchingFileCount: number;
  nextCursor: string | null;
  request: ProposedShareTreeFilterRequestDto;
}

const MB = 1_000_000;
const GB = 1_000_000_000;

function file(id: string, name: string, sizeMb: number, visibility: ShareVisibility = 'public'): UserShareFile {
  return { id, name, sizeBytes: Math.round(sizeMb * MB), visibility };
}

function stableNumber(value: string): number {
  let hash = 2166136261;
  for (const char of value) hash = Math.imul(hash ^ char.charCodeAt(0), 16777619) >>> 0;
  return hash % 900_000;
}

const fixtures: Record<ScenarioId, RawUserBrowseFixture> = {
  normal: {
    profile: {
      username: 'tape_loop',
      presence: 'online',
      imageUrl: '/mock/user-normal.jpg',
      description: 'Mostly electronic, ambient and oddball records. Original rips where possible; folders are kept close to the physical releases. Listening lately: https://www.last.fm/music/Boards+of+Canada',
      averageUploadSpeed: 8_450_000,
      uploadCount: 48_231,
      uploadSlots: 3,
      queuedUploads: 1,
      hasFreeUploadSlot: true,
    },
    shares: [
      {
        id: 'normal-music', name: 'Music', folders: [
          {
            id: 'normal-boc', name: 'Boards of Canada', folders: [
              {
                id: 'normal-geogaddi', name: 'Geogaddi', visibility: 'mixed', files: [
                  file('n-geo-01', '01 - Ready Lets Go.flac', 21.8),
                  file('n-geo-02', '02 - Music Is Math.flac', 41.9),
                  file('n-geo-03', '03 - Beware the Friendly Stranger.flac', 13.4),
                  file('n-geo-04', '04 - Gyroscope.flac', 31.7),
                  file('n-geo-cover', 'Artwork - cover.jpg', 1.4, 'locked'),
                ],
              },
              {
                id: 'normal-mhtrtc', name: 'Music Has the Right to Children', files: [
                  file('n-mh-01', '01 - Wildlife Analysis.flac', 12.6),
                  file('n-mh-02', '02 - An Eagle in Your Mind.flac', 38.2),
                  file('n-mh-03', '03 - The Color of the Fire.flac', 11.4),
                  file('n-mh-04', '04 - Telephasic Workshop.flac', 44.2),
                  file('n-mh-05', '05 - Triangles & Rhombuses.flac', 9.8),
                  file('n-mh-art', 'Artwork/booklet.pdf', 8.6),
                ],
              },
            ],
          },
          {
            id: 'normal-autechre', name: 'Autechre', folders: [
              {
                id: 'normal-tri', name: 'Tri Repetae', files: [
                  file('n-tri-01', '01 - Dael.flac', 37.9),
                  file('n-tri-02', '02 - Clipper.flac', 43.1),
                  file('n-tri-03', '03 - Leterel.flac', 51.6),
                  file('n-tri-04', '04 - Rotar.flac', 39.7),
                  file('n-tri-cover', 'Artwork/cover.jpg', 1.8),
                ],
              },
              {
                id: 'normal-exai', name: 'Exai', files: [
                  file('n-exai-01', '01 - Fleure.flac', 54.3),
                  file('n-exai-02', '02 - irlite (get 0).flac', 49.7),
                  file('n-exai-03', '03 - prac-f.flac', 46.1),
                ],
              },
            ],
          },
        ],
      },
      {
        id: 'normal-mixes', name: 'DJ mixes', files: [
          file('n-mix-01', 'late-night-radio-2025-11.opus', 184.0),
          file('n-mix-02', 'warehouse-set-2026-03.opus', 236.5),
        ],
      },
      {
        id: 'normal-docs', name: 'Lists & notes', files: [
          file('n-doc-01', 'setlist.txt', 0.08),
          file('n-doc-02', 'wantlist.txt', 0.11),
        ],
      },
    ],
  },
  busy: {
    profile: {
      username: 'silvermachine',
      presence: 'online',
      averageUploadSpeed: 14_800_000,
      uploadCount: 121_904,
      uploadSlots: 6,
      queuedUploads: 14,
      hasFreeUploadSlot: false,
    },
    shares: [
      {
        id: 'busy-library', name: 'Library', folders: [
          { id: 'busy-hawkwind', name: 'Hawkwind', files: [
            file('b-hawk-01', '1971 - In Search of Space.flac', 412),
            file('b-hawk-02', '1972 - Doremi Fasol Latido.flac', 438),
            file('b-hawk-03', '1973 - Space Ritual CD1.flac', 391),
            file('b-hawk-04', '1973 - Space Ritual CD2.flac', 407),
          ] },
          { id: 'busy-can', name: 'Can', files: [
            file('b-can-01', '1971 - Tago Mago.flac', 746),
            file('b-can-02', '1972 - Ege Bamyasi.flac', 394),
            file('b-can-03', '1973 - Future Days.flac', 362),
          ] },
          { id: 'busy-fela', name: 'Fela Kuti', files: [
            file('b-fela-01', 'Expensive Shit.flac', 284),
            file('b-fela-02', 'Zombie.flac', 313),
            file('b-fela-03', 'Gentleman.flac', 298),
          ] },
        ],
      },
      { id: 'busy-bootlegs', name: 'Live & bootlegs', files: [
        file('b-live-01', 'Berlin 1977-10-14.flac', 892),
        file('b-live-02', 'Manchester 1981-06-02.flac', 1054),
        file('b-live-03', 'Peel Session 1974.flac', 247),
      ] },
    ],
  },
  loading: {
    profile: {
      username: 'buffering_peer',
      presence: 'online',
      description: 'Profile and shares requests are intentionally left in flight in this scenario.',
      averageUploadSpeed: 6_700_000,
      uploadCount: 12_440,
      uploadSlots: 2,
      queuedUploads: 3,
      hasFreeUploadSlot: false,
    },
    shares: [
      { id: 'loading-music', name: 'Music', folders: [
        { id: 'loading-collection', name: 'Collection', files: [
          file('l-01', '01 - pending browse.flac', 34),
          file('l-02', '02 - pending browse.flac', 39),
        ] },
      ] },
    ],
  },
  empty: {
    profile: {
      username: 'quiet_catalogue',
      presence: 'away',
      description: 'Small library, carefully tagged. Usually online in the evenings.',
      averageUploadSpeed: 2_150_000,
      uploadCount: 3_884,
      uploadSlots: 1,
      queuedUploads: 0,
      hasFreeUploadSlot: true,
    },
    shares: [
      { id: 'empty-music', name: 'Music', folders: [
        { id: 'empty-jazz', name: 'Jazz', files: [
          file('e-jazz-01', 'Alice Coltrane - Journey in Satchidananda.flac', 286),
          file('e-jazz-02', 'Pharoah Sanders - Karma.flac', 311),
        ] },
        { id: 'empty-ambient', name: 'Ambient', files: [
          file('e-amb-01', 'Hiroshi Yoshimura - Green.flac', 267),
          file('e-amb-02', 'Midori Takada - Through the Looking Glass.flac', 302),
        ] },
      ] },
    ],
  },
  offline: {
    profile: {
      username: 'ghost_packet',
      presence: 'offline',
      averageUploadSpeed: 5_300_000,
      uploadCount: 19_422,
      uploadSlots: 2,
      queuedUploads: 0,
      hasFreeUploadSlot: false,
    },
    shares: [
      { id: 'offline-archive', name: 'Archive', folders: [
        { id: 'offline-industrial', name: 'Industrial', files: [
          file('o-ind-01', 'Coil - Horse Rotorvator.flac', 421),
          file('o-ind-02', 'Throbbing Gristle - 20 Jazz Funk Greats.flac', 398),
        ] },
        { id: 'offline-noise', name: 'Noise', files: [
          file('o-noise-01', 'Merzbow - Pulse Demon.flac', 487),
          file('o-noise-02', 'Nurse With Wound - Homotopy to Marie.flac', 356),
        ] },
      ] },
    ],
  },
  stress: {
    profile: {
      username: 'lossless_archivist_with_an_unnecessarily_long_username_1999',
      presence: 'online',
      description: 'Archival mirrors, box sets, field recordings and radio captures. Paths intentionally preserve source naming, punctuation and edition notes, so some of them get very long.',
      averageUploadSpeed: 32_400_000,
      uploadCount: 908_771,
      uploadSlots: 12,
      queuedUploads: 87,
      hasFreeUploadSlot: false,
    },
    shares: [
      {
        id: 'stress-root', name: 'Very Large Archival Collection', folders: Array.from({ length: 8 }, (_, index) => ({
          id: `stress-series-${index}`,
          name: `Series ${String(index + 1).padStart(2, '0')} - Extremely Long Descriptive Collection Name (${1990 + index}-${1994 + index})`,
          folders: [
            {
              id: `stress-edition-${index}`,
              name: `Disc ${index + 1} - Remastered Deluxe Expanded Edition`,
              files: Array.from({ length: 7 }, (_, fileIndex) => file(
                `s-${index}-${fileIndex}`,
                `${String(fileIndex + 1).padStart(2, '0')} - A deliberately long track filename for narrow-layout pressure test ${index + 1}-${fileIndex + 1}.flac`,
                34 + index * 2 + fileIndex * 1.7,
              )),
            },
          ],
        })),
      },
      { id: 'stress-video', name: 'Concert video', visibility: 'locked', files: [
        { id: 's-video-01', name: 'Festival recording - full set - 2160p.mkv', sizeBytes: 18 * GB },
        { id: 's-video-02', name: 'Festival recording - alternate camera - 1080p.mkv', sizeBytes: 7.5 * GB },
      ] },
    ],
  },
};

const materializedFixtures = new Map<string, UserBrowseFixture>();

function section(state: 'available' | 'unavailable' | 'timed-out', reason: string | null = null): components['schemas']['UserProfileSectionDto'] {
  return { state, reason };
}

function materializeFixture(id: ScenarioId, raw: RawUserBrowseFixture): UserBrowseFixture {
  const key = `${id}:${raw.profile.username}`;
  const cached = materializedFixtures.get(key);
  if (cached) return cached;
  const metrics = shareMetrics(raw.shares);
  const partial = id === 'busy' || id === 'stress';
  const observedAt = '2026-08-07T08:15:00.000Z';
  const profileDto: UserProfileDto = {
    username: raw.profile.username,
    presence: raw.profile.presence,
    status: section('available'),
    info: section(id === 'stress' ? 'timed-out' : 'available', id === 'stress' ? 'Peer info timed out' : null),
    statistics: section(id === 'busy' ? 'unavailable' : 'available', id === 'busy' ? 'Statistics request failed' : null),
    pictureSection: section('available'),
    description: raw.profile.description ?? null,
    sharedFileCount: partial && id === 'busy' ? null : metrics.files,
    sharedDirectoryCount: partial && id === 'busy' ? null : metrics.folders,
    averageUploadSpeed: id === 'busy' ? null : raw.profile.averageUploadSpeed,
    uploadCount: id === 'busy' ? null : raw.profile.uploadCount,
    uploadSlots: raw.profile.uploadSlots,
    queueLength: raw.profile.queuedUploads,
    hasFreeUploadSlot: raw.profile.hasFreeUploadSlot,
    picture: raw.profile.imageUrl ? { url: raw.profile.imageUrl, mediaType: 'image/jpeg', byteLength: 149437, eTag: 'prototype-normal-picture' } : null,
    observedAt,
  };
  const browseState = id === 'loading' || id === 'busy' ? 'running' : id === 'offline' ? 'failed' : id === 'stress' ? 'failed' : 'complete';
  const browseDto: UserBrowseDto = {
    browseId: prototypeUuid(0x71000000, stableNumber(raw.profile.username)),
    username: raw.profile.username,
    state: browseState,
    phase: id === 'loading' ? 'waiting-for-peer' : id === 'busy' ? 'indexing' : 'ready',
    compressedBytesReceived: id === 'loading' ? 0 : id === 'busy' ? 64000 : 128000,
    compressedBytesExpected: id === 'loading' ? null : id === 'busy' ? 160000 : 128000,
    directoryCount: metrics.folders, fileCount: metrics.files, totalFileBytes: metrics.sizeBytes,
    createdAt: '2026-08-07T08:10:00.000Z', updatedAt: observedAt,
    expiresAt: id === 'stress' ? '2026-08-07T08:14:00.000Z' : '2026-08-08T08:15:00.000Z',
    failure: id === 'stress' ? { error: 'Browse artifact expired', code: 'browse_expired' } : id === 'offline' ? { error: 'Peer unavailable', code: 'offline' } : null,
    revision: 3,
  };
  const rows = flattenShareTree(raw.shares);
  const directoryDtos: BrowseDirectoryEntryDto[] = rows.filter((row) => row.kind === 'folder').map((row) => ({
    directoryId: row.directoryId!, parentId: row.parentDirectoryId ?? null, name: row.name, displayPath: row.path, visibility: row.visibility, isSynthetic: false,
    directDirectoryCount: 0, directFileCount: 0, recursiveFileCount: row.fileIds.length, recursiveFileBytes: row.sizeBytes,
    lockedDescendantCount: row.visibility === 'public' ? 0 : 1, hasChildren: row.fileIds.length > 0,
  }));
  const fileDtos: BrowseFileEntryDto[] = rows.filter((row) => row.kind === 'file').map((row) => ({
    fileId: row.fileId!, directoryId: row.parentDirectoryId ?? '0', visibility: row.visibility,
    file: { name: row.name, size: row.sizeBytes, extension: row.name.includes('.') ? row.name.split('.').at(-1) ?? null : null, bitRate: null, bitDepth: null, sampleRate: null, length: null, attributes: null },
  }));
  const result: UserBrowseFixture = { ...raw, profileDto, browseDto, directoryDtos, fileDtos, profileLifetime: 'live-only', browseLifetime: id === 'stress' ? 'expired' : id === 'loading' || id === 'busy' ? 'live' : 'retained' };
  materializedFixtures.set(key, result);
  return result;
}

export function getUserBrowseFixture(id: ScenarioId): UserBrowseFixture {
  return materializeFixture(id, fixtures[id]);
}

const fallbackFixtures = new Map<string, UserBrowseFixture>();

function usernameSeed(username: string): number {
  let seed = 0;
  for (const char of username) seed = ((seed * 31) + char.charCodeAt(0)) >>> 0;
  return seed;
}

function fallbackUserFixture(username: string): UserBrowseFixture {
  const cached = fallbackFixtures.get(username);
  if (cached) return cached;

  const seed = usernameSeed(username);
  const safeId = username.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'peer';
  const uploadSlots = 1 + (seed % 4);
  const queuedUploads = seed % 7;
  const raw: RawUserBrowseFixture = {
    profile: {
      username,
      presence: username.toLowerCase().includes('offline') ? 'offline' : 'online',
      averageUploadSpeed: 2_000_000 + (seed % 13_000_000),
      uploadCount: 2_000 + (seed % 90_000),
      uploadSlots,
      queuedUploads,
      hasFreeUploadSlot: queuedUploads < uploadSlots,
    },
    shares: [
      {
        id: `${safeId}-music`,
        name: 'Music',
        folders: [
          {
            id: `${safeId}-collection`,
            name: 'Collection',
            files: [
              file(`${safeId}-01`, '01 - shared track.flac', 28 + (seed % 17)),
              file(`${safeId}-02`, '02 - shared track.flac', 31 + (seed % 21)),
              file(`${safeId}-03`, 'cover.jpg', 1.2 + ((seed % 8) / 10)),
            ],
          },
        ],
      },
      {
        id: `${safeId}-misc`,
        name: 'Misc',
        files: [file(`${safeId}-notes`, 'list.txt', 0.05)],
      },
    ],
  };

  const fixture = materializeFixture('normal', raw);
  fallbackFixtures.set(username, fixture);
  return fixture;
}

export function getUserBrowseFixtureForUsername(id: ScenarioId, username: string): UserBrowseFixture {
  const normalized = username.trim();
  if (!normalized) return materializeFixture(id, fixtures[id]);

  const knownEntry = (Object.entries(fixtures) as [ScenarioId, RawUserBrowseFixture][]).find(
    ([, fixture]) => fixture.profile.username.toLowerCase() === normalized.toLowerCase(),
  );
  return knownEntry ? materializeFixture(knownEntry[0], knownEntry[1]) : fallbackUserFixture(normalized);
}

export function shareMetrics(folders: readonly UserShareFolder[]): ShareMetrics {
  let files = 0;
  let folderCount = 0;
  let sizeBytes = 0;

  function visit(folder: UserShareFolder): void {
    folderCount += 1;
    for (const item of folder.files ?? []) {
      files += 1;
      sizeBytes += item.sizeBytes;
    }
    for (const child of folder.folders ?? []) visit(child);
  }

  for (const folder of folders) visit(folder);
  return { files, folders: folderCount, sizeBytes };
}

export function flattenShareTree(folders: readonly UserShareFolder[]): ShareTreeRow[] {
  const rows: ShareTreeRow[] = [];
  let directorySequence = 0;
  let fileSequence = 0;

  function visitFolder(
    folder: UserShareFolder,
    depth: number,
    parentPath: string,
    parents: string[],
    parentDirectoryId: string | undefined,
  ): { fileIds: string[]; sizeBytes: number; rowId: string; directoryId: string } {
    const path = parentPath ? `${parentPath}\\${folder.name}` : folder.name;
    directorySequence += 1;
    const rowId = prototypeUuid(0x74000000, directorySequence);
    const directoryId = prototypeNumericId(72, directorySequence);
    const rowIndex = rows.length;
    const folderRow: ShareTreeRow = {
      kind: 'folder', id: rowId, name: folder.name, path, depth, sizeBytes: 0, fileIds: [], parentFolderIds: parents,
      parentDirectoryId, directoryId, visibility: folder.visibility ?? 'public',
    };
    rows.push(folderRow);

    const descendantIds: string[] = [];
    let sizeBytes = 0;
    const nextParents = [...parents, rowId];

    for (const item of folder.files ?? []) {
      const filePath = `${path}\\${item.name}`;
      fileSequence += 1;
      const fileRowId = prototypeUuid(0x75000000, fileSequence);
      const fileId = prototypeNumericId(73, fileSequence);
      descendantIds.push(fileRowId);
      sizeBytes += item.sizeBytes;
      rows.push({
        kind: 'file', id: fileRowId, name: item.name, path: filePath, depth: depth + 1, sizeBytes: item.sizeBytes,
        fileIds: [fileRowId], parentFolderIds: nextParents, parentDirectoryId: directoryId, fileId, visibility: item.visibility ?? 'public',
      });
    }

    for (const child of folder.folders ?? []) {
      const childResult = visitFolder(child, depth + 1, path, nextParents, directoryId);
      descendantIds.push(...childResult.fileIds);
      sizeBytes += childResult.sizeBytes;
    }

    folderRow.fileIds = descendantIds;
    folderRow.sizeBytes = sizeBytes;
    rows[rowIndex] = folderRow;
    return { fileIds: descendantIds, sizeBytes, rowId, directoryId };
  }

  for (const folder of folders) visitFolder(folder, 0, '', [], undefined);
  return rows;
}

/** Mock daemon boundary: the complete browse artifact is filtered before paging. */
export function requestShareTreeProjection(
  folders: readonly UserShareFolder[],
  request: ProposedShareTreeFilterRequestDto,
): ShareTreeProjectionPage {
  const allRows = flattenShareTree(folders);
  const query = request.query?.trim().toLowerCase();
  let projected = allRows;

  if (query) {
    const included = new Set<string>();
    for (const row of allRows) {
      if (!row.path.toLowerCase().includes(query)) continue;
      included.add(row.id);
      for (const parent of row.parentFolderIds) included.add(parent);
      if (row.kind === 'folder') {
        for (const candidate of allRows) {
          if (candidate.parentFolderIds.includes(row.id)) included.add(candidate.id);
        }
      }
    }
    projected = allRows.filter((row) => included.has(row.id));
  }

  const parsedCursor = Number(request.cursor ?? 0);
  const offset = Number.isFinite(parsedCursor) ? Math.max(0, parsedCursor) : 0;
  const rows = projected.slice(offset, offset + request.limit);
  return {
    rows,
    matchingFileCount: projected.filter((row) => row.kind === 'file').length,
    nextCursor: offset + rows.length < projected.length ? String(offset + rows.length) : null,
    request,
  };
}

export function formatShareSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit += 1;
  }
  const digits = value >= 100 || unit === 0 ? 0 : value >= 10 ? 1 : 2;
  return `${value.toFixed(digits)} ${units[unit]}`;
}
