/**
 * Proposed backend contract: one bounded response supplying the entire dashboard
 * range. The semantics object is intentionally part of the contract so a later
 * implementation cannot silently change what the numbers count.
 */
export interface ProposedDashboardAnalyticsDto {
  contract: 'proposed-dashboard-analytics-v3';
  range: {
    startUtc: string;
    endUtc: string;
    bucketSeconds: number;
    comparisonStartUtc: string | null;
    comparisonEndUtc: string | null;
    partialRetention: boolean;
  };
  semantics: {
    byteAccounting: 'bytes-transferred-during-range-by-direction';
    peerBytes: 'bytes-transferred-during-range-by-direction-and-remote-username';
    peerFiles: 'distinct-transfers-with-byte-activity-in-range-by-direction';
    contentIdentity: 'logical-download-source-path';
    errorPopulation: 'terminal-transfer-attempt-failures-completed-in-range';
    shareRatio: 'uploaded-bytes/divided-by-downloaded-bytes';
    distinctPeers: 'unique-remote-usernames-with-byte-activity-across-both-directions-in-range';
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
