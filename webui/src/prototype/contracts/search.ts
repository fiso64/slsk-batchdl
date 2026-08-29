import type { components } from '../../api/generated';

/** Proposed result metadata that preserves the daemon's preference decision. */
export interface ProposedPreferredResultDto {
  candidateKey: string;
  tier: 'preferred' | 'other';
  matchedPreferenceKeys: string[];
}

/** One surviving child returned by a separately paged directory-children resource. */
export interface ProposedGenericDirectoryFileDto {
  relativePath: string;
  candidate: components['schemas']['FileCandidateDto'];
}

/** Fixed-size generic directory summary for a raw SearchJob projection. */
export interface ProposedGenericDirectoryResultDto {
  ref: { username: string; directoryPath: string };
  peer: components['schemas']['PeerInfoDto'];
  directoryPath: string;
  matchingFileCount: number;
  matchingBytes: number;
  /** True once the peer directory itself has been browsed rather than inferred only from search hits. */
  isFullyRetrieved: boolean;
  /** Highest-ranked surviving child determines root-directory relevance. */
  bestFile: components['schemas']['FileCandidateRefDto'];
}

/** Child contents remain bounded even when one projected directory contains many files. */
export interface ProposedGenericDirectoryChildrenRequestDto {
  directory: { username: string; directoryPath: string };
  cursor: string | null;
  limit: number;
}

export interface ProposedGenericDirectoryChildrenPageDto {
  items: ProposedGenericDirectoryFileDto[];
  nextCursor: string | null;
}

/** Fixed-size aggregate-group summary; alternatives are loaded through a separate page. */
export interface ProposedAggregateGroupSummaryDto {
  groupRef: string;
  itemName: string;
  shareCount: number;
  optionCount: number;
  representativeRef: string;
}

export interface ProposedAggregateOptionsRequestDto {
  groupRef: string;
  cursor: string | null;
  limit: number;
}


/** Selection expression for a server-owned paged result projection. */
export interface ProposedSearchSelectionDto {
  projectionRevision: number;
  mode: 'only' | 'all-except';
  itemRefs: string[];
}

export interface ProposedSearchSelectionResolutionDto {
  requestedCount: number;
  resolvedCount: number;
  submittedCount: number;
  skippedCount: number;
  rejectedCount: number;
  reasons: Record<string, number>;
}

/** Proposed generalization of the album-only retrieve-folder follow-up. */
export interface ProposedGenericDirectoryRetrievalRequestDto {
  directory: { username: string; directoryPath: string };
}

/** Proposed server-owned filtering, reprojection, grouping, ordering, and pagination. */
export interface ProposedSearchResultProjectionRequestDto {
  filterText: string | null;
  projection:
    | { kind: 'generic-directory'; request: Record<string, never> }
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
