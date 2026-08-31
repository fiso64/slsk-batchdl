import type { components } from '../api/generated';

export type ResourceActionDto = components['schemas']['ResourceActionDto'];

export type PrototypeDataLifetime =
  | 'live'
  | 'retained'
  | 'live-only'
  | 'pruned'
  | 'expired'
  | 'interrupted'
  | 'frontend-draft';

export type PrototypeMutationPhase = 'idle' | 'pending' | 'succeeded' | 'rejected' | 'partially-succeeded' | 'failed';

export interface PrototypeMutationState {
  phase: PrototypeMutationPhase;
  label?: string;
  detail?: string;
}

/**
 * Proposed backend contract: one bounded response supplying the entire dashboard
 * range. The semantics object is intentionally part of the contract so a later
 * implementation cannot silently change what the numbers count.
 */
export interface ProposedDashboardAnalyticsDto {
  contract: 'proposed-dashboard-analytics-v2';
  range: {
    startUtc: string;
    endUtc: string;
    bucketSeconds: number;
    comparisonStartUtc: string | null;
    comparisonEndUtc: string | null;
    partialRetention: boolean;
  };
  semantics: {
    peerBytes: 'terminal-and-progress-transfer-by-direction-and-remote-username';
    peerFiles: 'distinct-terminal-transfer-ids-by-direction';
    contentIdentity: 'logical-download-source-path';
    errorPopulation: 'terminal-transfer-attempt-failures';
    shareRatio: 'uploaded-bytes/divided-by-downloaded-bytes';
    distinctPeers: 'unique-remote-usernames-across-both-directions-in-range';
  };
  downloadMbps: number[];
  uploadMbps: number[];
  downloadPeers: ProposedDashboardPeerAggregateDto[];
  uploadPeers: ProposedDashboardPeerAggregateDto[];
  content: ProposedDashboardContentAggregateDto[];
  errors: ProposedDashboardErrorAggregateDto[];
  summary: ProposedDashboardSummaryDto;
}

export interface ProposedDashboardPeerAggregateDto {
  username: string;
  bytes: number;
  fileCount: number;
}

export interface ProposedDashboardContentAggregateDto {
  identity: string;
  displayPath: string;
  downloadCount: number;
  distinctPeerCount: number;
}

export interface ProposedDashboardErrorAggregateDto {
  key: string;
  message: string;
  count: number;
  lastSeenUtc: string;
}

export interface ProposedDashboardSummaryDto {
  downloadedBytes: number;
  downloadedFiles: number;
  uploadedBytes: number;
  uploadedFiles: number;
  distinctPeers: number;
  shareRatio: number | null;
  comparisonShareRatio: number | null;
}

/** Proposed result metadata that preserves the daemon's preference decision. */
export interface ProposedPreferredResultDto {
  candidateKey: string;
  tier: 'preferred' | 'other';
  matchedPreferenceKeys: string[];
}

/** Proposed surviving file inside a generic directory-tree projection. */
export interface ProposedGenericDirectoryFileDto {
  relativePath: string;
  candidate: components['schemas']['FileCandidateDto'];
}

/** Proposed generic directory-tree unit for a raw SearchJob projection. */
export interface ProposedGenericDirectoryResultDto {
  ref: { username: string; directoryPath: string };
  peer: components['schemas']['PeerInfoDto'];
  directoryPath: string;
  /** Surviving descendants, with paths relative to the projected root. */
  files: ProposedGenericDirectoryFileDto[];
  matchingFileCount: number;
  matchingBytes: number;
  /** True once the peer directory itself has been browsed rather than inferred only from search hits. */
  isFullyRetrieved: boolean;
  /** Highest-ranked surviving child determines root-directory relevance. */
  bestFile: components['schemas']['FileCandidateRefDto'];
}

/** Proposed generalization of the album-only retrieve-folder follow-up. */
export interface ProposedGenericDirectoryRetrievalRequestDto {
  directory: { username: string; directoryPath: string };
}

/** Proposed server-owned filtering, reprojection, grouping, ordering, and pagination. */
export interface ProposedSearchResultProjectionRequestDto {
  filterText: string | null;
  projection:
    | { kind: 'generic-directory'; request: { includeFiles: true; includeDescendants: true } }
    | { kind: 'track'; request: components['schemas']['FileSearchProjectionRequestDto'] }
    | { kind: 'album'; request: components['schemas']['FolderSearchProjectionRequestDto'] };
  search: components['schemas']['SearchSettingsPatchDto'];
  order:
    | 'relevance'
    | 'upload-speed'
    | 'queue-depth'
    | 'item-size-ascending'
    | 'item-size-descending'
    | 'directory-size-ascending'
    | 'directory-size-descending'
    | 'file-count-ascending'
    | 'file-count-descending'
    | 'directory-name-ascending'
    | 'directory-name-descending';
  cursor: string | null;
  limit: number;
}

/** Proposed mixed-tree browse query; filtering occurs before cursor pagination. */
export interface ProposedShareTreeFilterRequestDto {
  query: string | null;
  cursor: string | null;
  limit: number;
}

/**
 * Proposed optional metadata carried by generic transfer DTOs in both directions.
 * The daemon already knows these fields for download candidates and upload share
 * catalog entries; production should expose them without WebUI fan-out.
 */
export interface ProposedTransferFileMetadataDto {
  extension: string | null;
  bitrateKbps: number | null;
  bitDepth: number | null;
  sampleRateHz: number | null;
  lengthSeconds: number | null;
}

/** Proposed bulk mutation contract. Scope is deliberately explicit. */
export interface ProposedBulkActionRequestDto {
  direction: 'download' | 'upload';
  scope: 'current-view';
  action: 'cancel' | 'archive-terminal';
  filter: 'all' | 'queued' | 'in-progress' | 'terminal';
}

export interface ProposedBulkActionResponseDto {
  requestedCount: number;
  succeededCount: number;
  rejectedCount: number;
  failedCount: number;
}

/** Proposed durable deletion semantics used by the prototype. */
/** Proposed peer-access mutation needed by the prototype's chat block control. */
export interface ProposedPeerAccessMutationDto {
  username: string;
  blocked: boolean;
  semantics: 'chat-and-peer-access';
}

export interface ProposedHistoryDeleteRequestDto {
  resourceKind: 'job' | 'download-job' | 'upload-transfer';
  resourceIds: string[];
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

export interface ProposedSubmitJobPreviewRequestDto {
  /** Submission options are captured by the preview so Review and direct Start use identical settings. */
  options?: components['schemas']['SubmissionOptionsDto'];
  selection:
    | { mode: 'all-except'; leafRefs: string[] }
    | { mode: 'only'; leafRefs: string[] };
}

export interface PrototypeDownloadSelectionSummary {
  requestedCount: number;
  uniqueFileCount: number;
  resolvablePublicCount: number;
  lockedCount: number;
  skippedCount: number;
}

export interface DisplayCountDefinition {
  key: string;
  label: string;
  counts: 'transfer-rows' | 'logical-jobs' | 'files' | 'peers' | 'queued-entries' | 'unread-messages' | 'selected-files';
  scope: 'snapshot' | 'current-view' | 'retained-history' | 'selection';
}

export const displayCountDefinitions = {
  downloadsSidebar: { key: 'downloads-sidebar', label: 'active download transfer rows', counts: 'transfer-rows', scope: 'snapshot' },
  uploadsSidebar: { key: 'uploads-sidebar', label: 'active upload transfer rows', counts: 'transfer-rows', scope: 'snapshot' },
  chatSidebar: { key: 'chat-sidebar', label: 'unread chat messages', counts: 'unread-messages', scope: 'snapshot' },
  searchPeers: { key: 'search-peers', label: 'distinct peers in the complete search result set', counts: 'peers', scope: 'retained-history' },
  selection: { key: 'selection', label: 'deduplicated selected files', counts: 'selected-files', scope: 'selection' },
} as const satisfies Record<string, DisplayCountDefinition>;
