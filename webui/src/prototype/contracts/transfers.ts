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

export interface ProposedTransferHistoryArchiveRequestDto {
  direction: 'download' | 'upload';
  transferIds: string[];
  semantics: 'permanent-delete' | 'archive-from-history';
}
