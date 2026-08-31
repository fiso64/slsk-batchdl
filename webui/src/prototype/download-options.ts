import type { components } from '../api/generated';

type DownloadSettingsPatchDto = components['schemas']['DownloadSettingsPatchDto'];
export type SubmissionOptionsDto = components['schemas']['SubmissionOptionsDto'];

export interface PrototypeDownloadOptions {
  /** Effective daemon default in the current prototype baseline. */
  skipExisting: boolean;
  /** Positive UI wording for Search.NoBrowseFolder=false. */
  loadFullAlbumFolder: boolean;
  outputParentDir: string;
  nameFormat: string;
  writePlaylist: boolean;
}

export interface PrototypeImportOptions {
  maxTracks: string;
  offset: string;
  upgradeToAlbum: boolean;
}

export interface DownloadOptionCapabilities {
  albumFolderEnrichment: boolean;
  playlistOutput: boolean;
  nameFormat?: boolean;
}

export function createPrototypeDownloadOptions(): PrototypeDownloadOptions {
  return {
    skipExisting: true,
    loadFullAlbumFolder: true,
    outputParentDir: '',
    nameFormat: '',
    writePlaylist: false,
  };
}

export function createPrototypeImportOptions(): PrototypeImportOptions {
  return {
    maxTracks: '',
    offset: '0',
    upgradeToAlbum: false,
  };
}

function optionalNumber(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? Math.max(0, Math.trunc(parsed)) : null;
}

export function downloadOptionsCustomized(
  value: PrototypeDownloadOptions,
  capabilities: DownloadOptionCapabilities,
): boolean {
  return value.skipExisting !== true
    || (capabilities.albumFolderEnrichment && value.loadFullAlbumFolder !== true)
    || Boolean(value.outputParentDir.trim())
    || Boolean((capabilities.nameFormat ?? true) && value.nameFormat.trim())
    || (capabilities.playlistOutput && value.writePlaylist !== false);
}

export function importOptionsCustomized(value: PrototypeImportOptions): boolean {
  return Boolean(value.maxTracks.trim())
    || (value.offset.trim() !== '' && value.offset.trim() !== '0')
    || value.upgradeToAlbum;
}

/**
 * Maps the user-facing Download options to the daemon's real submission seams.
 * ParentDir intentionally uses SubmissionOptionsDto.OutputParentDir; the other
 * settings remain a DownloadSettingsPatchDto.
 */
export function buildSubmissionOptions(
  value: PrototypeDownloadOptions,
  capabilities: DownloadOptionCapabilities,
): SubmissionOptionsDto {
  const output: components['schemas']['OutputSettingsPatchDto'] = {
    nameFormat: (capabilities.nameFormat ?? true) && value.nameFormat.trim() ? value.nameFormat.trim() : null,
    writePlaylist: capabilities.playlistOutput ? value.writePlaylist : null,
  };
  const search: components['schemas']['SearchSettingsPatchDto'] = {
    noBrowseFolder: capabilities.albumFolderEnrichment ? !value.loadFullAlbumFolder : null,
  };
  const skip: components['schemas']['SkipSettingsPatchDto'] = {
    skipExisting: value.skipExisting,
  };

  return {
    outputParentDir: value.outputParentDir.trim() || null,
    downloadSettings: { output, search, skip },
  };
}

export function buildImportSettingsPatch(value: PrototypeImportOptions): DownloadSettingsPatchDto {
  return {
    extraction: {
      maxTracks: optionalNumber(value.maxTracks),
      offset: optionalNumber(value.offset),
      upgradeToAlbum: value.upgradeToAlbum || null,
    },
  };
}

export function mergeDownloadSettings(
  ...patches: Array<DownloadSettingsPatchDto | null | undefined>
): DownloadSettingsPatchDto {
  const result: DownloadSettingsPatchDto = {};
  for (const patch of patches) {
    if (!patch) continue;
    if (patch.output) result.output = { ...(result.output ?? {}), ...patch.output };
    if (patch.search) result.search = { ...(result.search ?? {}), ...patch.search };
    if (patch.skip) result.skip = { ...(result.skip ?? {}), ...patch.skip };
    if (patch.preprocess) result.preprocess = { ...(result.preprocess ?? {}), ...patch.preprocess };
    if (patch.extraction) result.extraction = { ...(result.extraction ?? {}), ...patch.extraction };
    if (patch.transfer) result.transfer = { ...(result.transfer ?? {}), ...patch.transfer };
    if (patch.spotify) result.spotify = { ...(result.spotify ?? {}), ...patch.spotify };
    if (patch.youTube) result.youTube = { ...(result.youTube ?? {}), ...patch.youTube };
    if (patch.ytDlp) result.ytDlp = { ...(result.ytDlp ?? {}), ...patch.ytDlp };
    if (patch.csv) result.csv = { ...(result.csv ?? {}), ...patch.csv };
    if (patch.bandcamp) result.bandcamp = { ...(result.bandcamp ?? {}), ...patch.bandcamp };
    if (patch.printOption !== undefined) result.printOption = patch.printOption;
  }
  return result;
}
