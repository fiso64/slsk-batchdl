import type { components } from '../api/generated';
import type { SearchResultMode } from './search';
import { searchModeFamily } from './search';

export interface CommonSearchConditions {
  formats: string[];
  minBitrate: string;
  maxBitrate: string;
  minSampleRate: string;
  maxSampleRate: string;
  minBitDepth: string;
  maxBitDepth: string;
  rejectUnknownMetadata: boolean;
  strictArtist: boolean;
  allowedUsers: string;
  bannedUsers: string;
}

export interface TrackSearchConditions {
  strictTitle: boolean;
  expectedLength: string;
  lengthTolerance: string;
  acceptNoLength: boolean;
}

export interface AlbumSearchConditions {
  strictAlbum: boolean;
  minTrackCount: string;
  maxTrackCount: string;
  requiredTrackTitles: string[];
  strictAlbumQuality: boolean;
}

export interface CommonSearchRanking {
  formats: string[];
  minBitrate: string;
  maxBitrate: string;
  minSampleRate: string;
  maxSampleRate: string;
  minBitDepth: string;
  maxBitDepth: string;
  strictArtist: boolean;
  allowedUsers: string;
  bannedUsers: string;
}

export interface TrackSearchRanking {
  strictTitle: boolean;
  lengthTolerance: string;
}

export interface AlbumSearchRanking {
  strictAlbum: boolean;
}

export interface SearchRankingPreferences {
  common: CommonSearchRanking;
  track: TrackSearchRanking;
  album: AlbumSearchRanking;
}

export interface PrototypeSearchConditions {
  common: CommonSearchConditions;
  track: TrackSearchConditions;
  album: AlbumSearchConditions;
  ranking: SearchRankingPreferences;
}

export function createPrototypeSearchConditions(): PrototypeSearchConditions {
  // Required conditions intentionally begin unrestricted. The explicit `Any`
  // format state communicates this in the UI without manufacturing a no-op pill.
  // Ranking defaults mirror the documented pref-* defaults where they are useful
  // to expose in the prototype, but ranking never filters results out.
  const config = createEmptySearchConditions();
  config.ranking.common.formats = ['MP3'];
  config.ranking.common.minBitrate = '200';
  config.ranking.common.maxBitrate = '2500';
  config.ranking.common.maxSampleRate = '48000';
  config.ranking.track.lengthTolerance = '3';
  config.ranking.track.strictTitle = true;
  config.ranking.album.strictAlbum = true;
  return config;
}

export function createEmptySearchConditions(): PrototypeSearchConditions {
  return {
    common: {
      formats: [],
      minBitrate: '',
      maxBitrate: '',
      minSampleRate: '',
      maxSampleRate: '',
      minBitDepth: '',
      maxBitDepth: '',
      rejectUnknownMetadata: false,
      strictArtist: false,
      allowedUsers: '',
      bannedUsers: '',
    },
    track: {
      strictTitle: false,
      expectedLength: '',
      lengthTolerance: '3',
      acceptNoLength: true,
    },
    album: {
      strictAlbum: false,
      minTrackCount: '',
      maxTrackCount: '',
      requiredTrackTitles: [],
      strictAlbumQuality: false,
    },
    ranking: createEmptySearchRanking(),
  };
}

export function createEmptySearchRanking(): SearchRankingPreferences {
  return {
    common: {
      formats: [],
      minBitrate: '',
      maxBitrate: '',
      minSampleRate: '',
      maxSampleRate: '',
      minBitDepth: '',
      maxBitDepth: '',
      strictArtist: false,
      allowedUsers: '',
      bannedUsers: '',
    },
    track: {
      strictTitle: false,
      lengthTolerance: '',
    },
    album: {
      strictAlbum: false,
    },
  };
}

export type SearchSettingsPatchDto = components['schemas']['SearchSettingsPatchDto'];
type FileConditionsPatchDto = components['schemas']['FileConditionsPatchDto'];

function numberOrNull(value: string): number | null {
  if (!value.trim()) return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function csv(value: string): string[] {
  return value.split(',').map((item) => item.trim()).filter(Boolean);
}

function collectionOrNull(values: string[]): { replace: string[] } | null {
  return values.length ? { replace: values } : null;
}

/**
 * Prototype seam showing how user-facing Conditions and Ranking controls map
 * onto the daemon API. The Ranking tab intentionally follows the documented
 * explicit pref-* help surface: album track-count / required-track-title / strict
 * album-quality controls remain Conditions-only here even though the raw API has
 * a generic preferredFolderCond patch seam.
 */
export function toNecessarySearchPatch(
  resultMode: SearchResultMode,
  conditions: PrototypeSearchConditions,
): SearchSettingsPatchDto {
  const family = searchModeFamily(resultMode);
  const allowedUsers = csv(conditions.common.allowedUsers);
  const bannedUsers = csv(conditions.common.bannedUsers);

  const necessaryCond: FileConditionsPatchDto = {
    minBitrate: numberOrNull(conditions.common.minBitrate),
    maxBitrate: numberOrNull(conditions.common.maxBitrate),
    minSampleRate: numberOrNull(conditions.common.minSampleRate),
    maxSampleRate: numberOrNull(conditions.common.maxSampleRate),
    minBitDepth: numberOrNull(conditions.common.minBitDepth),
    maxBitDepth: numberOrNull(conditions.common.maxBitDepth),
    strictArtist: conditions.common.strictArtist || null,
    strictTitle: family === 'track' ? conditions.track.strictTitle || null : null,
    strictAlbum: family === 'album' ? conditions.album.strictAlbum || null : null,
    formats: collectionOrNull(conditions.common.formats),
    allowedUsers: collectionOrNull(allowedUsers),
    bannedUsers: collectionOrNull(bannedUsers),
    acceptNoLength: family === 'track' ? conditions.track.acceptNoLength : null,
    acceptMissingProps: conditions.common.rejectUnknownMetadata ? false : null,
    lengthTolerance: family === 'track' ? numberOrNull(conditions.track.lengthTolerance) : null,
  };

  const ranking = conditions.ranking;
  const preferredAllowedUsers = csv(ranking.common.allowedUsers);
  const preferredBannedUsers = csv(ranking.common.bannedUsers);
  const preferredCond: FileConditionsPatchDto = {
    minBitrate: numberOrNull(ranking.common.minBitrate),
    maxBitrate: numberOrNull(ranking.common.maxBitrate),
    minSampleRate: numberOrNull(ranking.common.minSampleRate),
    maxSampleRate: numberOrNull(ranking.common.maxSampleRate),
    minBitDepth: numberOrNull(ranking.common.minBitDepth),
    maxBitDepth: numberOrNull(ranking.common.maxBitDepth),
    strictArtist: ranking.common.strictArtist || null,
    strictTitle: family === 'track' ? ranking.track.strictTitle || null : null,
    strictAlbum: family === 'album' ? ranking.album.strictAlbum || null : null,
    formats: collectionOrNull(ranking.common.formats),
    allowedUsers: collectionOrNull(preferredAllowedUsers),
    bannedUsers: collectionOrNull(preferredBannedUsers),
    lengthTolerance: family === 'track' ? numberOrNull(ranking.track.lengthTolerance) : null,
  };

  return {
    necessaryCond,
    preferredCond,
    necessaryFolderCond: family === 'album' ? {
      minTrackCount: numberOrNull(conditions.album.minTrackCount),
      maxTrackCount: numberOrNull(conditions.album.maxTrackCount),
      requiredTrackTitles: collectionOrNull(conditions.album.requiredTrackTitles),
    } : null,
    strictAlbumQuality: family === 'album' ? conditions.album.strictAlbumQuality || null : null,
  };
}

export function hasAppliedConditions(mode: SearchResultMode, conditions: PrototypeSearchConditions): boolean {
  const family = searchModeFamily(mode);
  return Boolean(
    conditions.common.formats.length
      || conditions.common.minBitrate
      || conditions.common.maxBitrate
      || conditions.common.minSampleRate
      || conditions.common.maxSampleRate
      || conditions.common.minBitDepth
      || conditions.common.maxBitDepth
      || conditions.common.strictArtist
      || conditions.common.rejectUnknownMetadata
      || conditions.common.allowedUsers.trim()
      || conditions.common.bannedUsers.trim()
      || (family === 'track'
        ? conditions.track.strictTitle || conditions.track.expectedLength
        : conditions.album.strictAlbum
          || conditions.album.minTrackCount
          || conditions.album.maxTrackCount
          || conditions.album.requiredTrackTitles.length
          || conditions.album.strictAlbumQuality),
  );
}

export function cloneSearchConditions(conditions: PrototypeSearchConditions): PrototypeSearchConditions {
  return {
    common: {
      ...conditions.common,
      formats: [...conditions.common.formats],
    },
    track: { ...conditions.track },
    album: {
      ...conditions.album,
      requiredTrackTitles: [...conditions.album.requiredTrackTitles],
    },
    ranking: {
      common: {
        ...conditions.ranking.common,
        formats: [...conditions.ranking.common.formats],
      },
      track: { ...conditions.ranking.track },
      album: { ...conditions.ranking.album },
    },
  };
}
