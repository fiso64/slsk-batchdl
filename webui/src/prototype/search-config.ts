import type { components } from '../api/generated';
import type { SearchResultMode } from './search';

export interface CommonSearchConditions {
  formats: string[];
  minBitrate: string;
  maxBitrate: string;
  sampleRate: string;
  bitDepth: string;
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

export interface PrototypeSearchConditions {
  common: CommonSearchConditions;
  track: TrackSearchConditions;
  album: AlbumSearchConditions;
}

export function createPrototypeSearchConditions(): PrototypeSearchConditions {
  // Keep the global search's initial pills visible without narrowing the mock
  // result fixtures. These are the audio formats represented by the prototype
  // data, so a fresh search still shows the complete fixture set.
  const conditions = createEmptySearchConditions();
  conditions.common.formats = ['FLAC', 'MP3', 'M4A', 'WAV'];
  return conditions;
}

export function createEmptySearchConditions(): PrototypeSearchConditions {
  return {
    common: {
      formats: [],
      minBitrate: '',
      maxBitrate: '',
      sampleRate: '',
      bitDepth: '',
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
  };
}

type SearchSettingsPatchDto = components['schemas']['SearchSettingsPatchDto'];
type FileConditionsPatchDto = components['schemas']['FileConditionsPatchDto'];

function numberOrNull(value: string): number | null {
  if (!value.trim()) return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function csv(value: string): string[] {
  return value.split(',').map((item) => item.trim()).filter(Boolean);
}

/**
 * Prototype seam showing how the user-facing controls can map onto the daemon API.
 * Exact sample rate / bit depth intentionally map to both min and max fields.
 */
export function toNecessarySearchPatch(
  resultMode: SearchResultMode,
  conditions: PrototypeSearchConditions,
): SearchSettingsPatchDto {
  const sampleRate = numberOrNull(conditions.common.sampleRate);
  const bitDepth = numberOrNull(conditions.common.bitDepth);
  const allowedUsers = csv(conditions.common.allowedUsers);
  const bannedUsers = csv(conditions.common.bannedUsers);

  const necessaryCond: FileConditionsPatchDto = {
    minBitrate: numberOrNull(conditions.common.minBitrate),
    maxBitrate: numberOrNull(conditions.common.maxBitrate),
    minSampleRate: sampleRate,
    maxSampleRate: sampleRate,
    minBitDepth: bitDepth,
    maxBitDepth: bitDepth,
    strictArtist: conditions.common.strictArtist || null,
    strictTitle: resultMode === 'track' ? conditions.track.strictTitle || null : null,
    strictAlbum: resultMode === 'album' ? conditions.album.strictAlbum || null : null,
    formats: conditions.common.formats.length ? { replace: conditions.common.formats } : null,
    allowedUsers: allowedUsers.length ? { replace: allowedUsers } : null,
    bannedUsers: bannedUsers.length ? { replace: bannedUsers } : null,
    acceptNoLength: resultMode === 'track' ? conditions.track.acceptNoLength : null,
    acceptMissingProps: conditions.common.rejectUnknownMetadata ? false : null,
    lengthTolerance: resultMode === 'track' ? numberOrNull(conditions.track.lengthTolerance) : null,
  };

  return {
    necessaryCond,
    necessaryFolderCond: resultMode === 'album' ? {
      minTrackCount: numberOrNull(conditions.album.minTrackCount),
      maxTrackCount: numberOrNull(conditions.album.maxTrackCount),
      requiredTrackTitles: conditions.album.requiredTrackTitles.length
        ? { replace: conditions.album.requiredTrackTitles }
        : null,
    } : null,
    strictAlbumQuality: resultMode === 'album' ? conditions.album.strictAlbumQuality || null : null,
  };
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
  };
}
