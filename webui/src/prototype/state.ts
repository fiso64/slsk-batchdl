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
