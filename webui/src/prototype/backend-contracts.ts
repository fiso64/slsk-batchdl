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
  contract: 'proposed-dashboard-analytics-v1';
  range: {
    startUtc: string;
    endUtc: string;
    bucketSeconds: number;
    comparisonStartUtc: string;
    comparisonEndUtc: string;
    partialRetention: boolean;
  };
  semantics: {
    peerBytes: 'terminal-and-progress-transfer-bytes-by-remote-username';
    peerFiles: 'distinct-terminal-transfer-ids';
    contentIdentity: 'logical-download-source-path';
    errorPopulation: 'terminal-transfer-attempt-failures';
    activityOrdering: 'occurred-at-descending';
    shareRatio: 'uploaded-bytes/divided-by-downloaded-bytes';
  };
  downloadMbps: number[];
  uploadMbps: number[];
  peers: ProposedDashboardPeerAggregateDto[];
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
  shareRatio: number | null;
  comparisonShareRatio: number | null;
}

/** Proposed durable feed contract; current daemon activity edges are not history. */
export interface ProposedActivityFeedItemDto {
  activityId: string;
  occurredAtUtc: string;
  kind: 'download' | 'upload' | 'chat';
  actor?: string;
  itemName: string;
  detail?: string;
}

/** Proposed result metadata that preserves the daemon's preference decision. */
export interface ProposedPreferredResultDto {
  candidateKey: string;
  tier: 'preferred' | 'other';
  matchedPreferenceKeys: string[];
}

/** Proposed server-owned filtering, reprojection, ordering, and pagination. */
export interface ProposedSearchResultProjectionRequestDto {
  filterText: string | null;
  projection:
    | { kind: 'track'; request: components['schemas']['FileSearchProjectionRequestDto'] }
    | { kind: 'album'; request: components['schemas']['FolderSearchProjectionRequestDto'] };
  search: components['schemas']['SearchSettingsPatchDto'];
  order: 'relevance' | 'upload-speed' | 'queue-depth' | 'item-size-ascending' | 'item-size-descending';
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
 * Proposed denormalized logical timeline projection. It is not a current API DTO;
 * its purpose is to make the prototype's card contract explicit.
 */
export interface ProposedLogicalDownloadTimelineItemDto {
  jobId: string;
  workflowId: string | null;
  parentJobId: string | null;
  sourceJobId: string | null;
  kind: 'track' | 'album' | 'remote-file' | 'remote-directory';
  createdAtUtc: string;
  username: string | null;
  sourcePath: string;
  lifetime: PrototypeDataLifetime;
  detailAvailability: 'full' | 'summary-only';
  availableActions: ResourceActionDto[];
}

/** Proposed bulk mutation contract. Scope is deliberately explicit. */
export interface ProposedBulkActionRequestDto {
  direction: 'download' | 'upload';
  scope: 'current-view';
  action: 'cancel' | 'archive-terminal';
  filter: 'all' | 'queued' | 'in-progress' | 'terminal';
  logicalItems: boolean;
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
  resourceKind: 'search-job' | 'download-job' | 'upload-transfer';
  resourceIds: string[];
  semantics: 'permanent-delete' | 'archive-from-history';
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
