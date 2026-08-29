import type { components } from '../../api/generated';

/** Runtime-job history and transfer history deliberately have different identities/lifecycles. */
export interface ProposedJobHistoryArchiveRequestDto {
  jobIds: string[];
  semantics: 'permanent-delete' | 'archive-from-history';
}

/** Proposed UI-safe effective defaults for submission forms; excludes secrets. */
export interface ProposedSubmissionDefaultsDto {
  skipExisting: boolean;
  loadFullAlbumFolder: boolean;
  outputParentDir: string | null;
  nameFormat: string;
  writePlaylist: boolean;
  minSharesAggregate: number;
  aggregateLengthToleranceSeconds: number;
  maxTracks: number | null;
  offset: number;
  upgradeToAlbum: boolean;
}

/** Proposed server-owned upload metadata for extraction inputs supplied by remote clients. */
export interface ProposedInputArtifactUploadDto {
  filename: string;
  contentType: string;
  sizeBytes: number;
  purpose: 'job-extraction-input';
}

export interface ProposedInputArtifactDto extends ProposedInputArtifactUploadDto {
  artifactId: string;
  createdAtUtc: string;
  expiresAtUtc: string;
}

/** Proposed non-runtime planning resource used by WebUI/remote CLI before submission. */
export interface ProposedJobPreviewSummaryDto {
  previewId: string;
  state: 'resolving' | 'ready' | 'failed';
  rootCount: number;
  logicalJobCount: number;
  expiresAtUtc: string;
}

export interface ProposedJobPreviewItemDto {
  previewRef: string;
  parentPreviewRef: string | null;
  kind: string;
  itemName: string | null;
  detail: string | null;
  childCount: number;
}

export interface ProposedJobPreviewPageDto {
  items: ProposedJobPreviewItemDto[];
  nextCursor: string | null;
}

/**
 * Creates a short-lived plan outside runtime job history. Effective settings are
 * captured here so submitting the reviewed preview cannot silently re-resolve them.
 */
export interface ProposedCreateJobPreviewRequestDto {
  source:
    | { kind: 'job-draft'; draft: components['schemas']['JobDraftDto'] }
    | { kind: 'extract-input'; input: string; inputType: string | null }
    | { kind: 'artifact'; artifactId: string; inputType: 'csv' | 'list' };
  options?: components['schemas']['SubmissionOptionsDto'];
}

/** Commits an existing resolved preview; settings belong to preview creation, not commit. */
export interface ProposedSubmitJobPreviewRequestDto {
  selection:
    | { mode: 'all-except'; leafRefs: string[] }
    | { mode: 'only'; leafRefs: string[] };
}
