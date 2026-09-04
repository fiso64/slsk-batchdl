export const dashboardRangeIds = ['24h', '7d', '30d', '90d', '1y', 'all'] as const;
export type DashboardRangeId = (typeof dashboardRangeIds)[number];
export const dashboardRangeLabels: Record<DashboardRangeId, string> = {
  '24h': '24h',
  '7d': '7d',
  '30d': '30d',
  '90d': '90d',
  '1y': '1y',
  all: 'All',
};

export interface DashboardPeerRow {
  peer: string;
  transferred: string;
  files: number;
}

export interface DashboardContentRow {
  folder: string;
  downloads: number;
  peers: number;
}

export interface DashboardErrorRow {
  error: string;
  count: number;
  lastSeen: string;
}

export interface DashboardRangeData {
  label: string;
  chartLabels: string[];
  downloadMbps: number[];
  uploadMbps: number[];
  downloadUsers: DashboardPeerRow[];
  uploadUsers: DashboardPeerRow[];
  content: DashboardContentRow[];
  errors: DashboardErrorRow[];
  summary: {
    downloaded: string;
    downloadFiles: number;
    uploaded: string;
    uploadFiles: number;
    distinctPeers: number;
    shareRatio: string;
    ratioDelta: string;
  };
}

const rankingUsers = [
  'nightshift', 'cassetteculture', 'cloudarchive', 'tape_loop', 'silvermachine',
  'neonrain', 'bitrot', 'deepcatalog', 'wavetable', 'orbiting',
  'subharmonic', 'magnetosphere', 'slowdive88', 'archivist', 'nocturne',
  'plasticpulse', 'riplog', 'phasecancel', 'spectralghost', 'databender',
  'lowquality_uploader', 'listener_17', 'vinylindex', 'roomtone', 'sinesweep',
];

const contentCatalog = [
  'Boards of Canada / Geogaddi', 'Autechre / Tri Repetae', 'Aphex Twin / SAW 85–92', 'Burial / Untrue',
  'Massive Attack / Mezzanine', 'Biosphere / Substrata', 'Plaid / Not for Threes', 'Squarepusher / Hard Normal Daddy',
  'µ-Ziq / Lunatic Harness', 'Clark / Body Riddle', 'Broadcast / Tender Buttons', 'Oneohtrix Point Never / R Plus Seven',
  'Tim Hecker / Virgins', 'Stars of the Lid / And Their Refinement of the Decline', 'Global Communication / 76:14',
  'The Future Sound of London / Lifeforms', 'Portishead / Dummy', 'Coil / Musick to Play in the Dark',
  'Flying Lotus / Cosmogramma', 'Four Tet / Rounds', 'Jon Hopkins / Immunity', 'Skee Mask / Compro',
  'Floating Points / Crush', 'DJ Shadow / Endtroducing.....', 'The Orb / Adventures Beyond the Ultraworld',
];

const errorCatalog = [
  'Peer disconnected', 'Transfer timed out', 'Connection failed', 'File unavailable', 'File no longer shared',
  'Remote queue rejected request', 'Peer went offline', 'Transfer cancelled remotely', 'Socket closed', 'Remote path unavailable',
  'Download stalled', 'Upload stalled', 'Connection reset', 'Handshake failed', 'Peer busy', 'Transfer interrupted',
  'Remote file changed', 'Permission denied', 'Invalid response', 'Unknown transfer error', 'Queue timeout', 'Peer not reachable',
];

function formatTransferredGb(gb: number): string {
  if (gb >= 1000) return `${(gb / 1000).toFixed(2)} TB`;
  return `${gb >= 100 ? gb.toFixed(0) : gb.toFixed(1)} GB`;
}

function top20Users(base: DashboardPeerRow[], startGb: number, stepGb: number, startFiles: number, fileStep: number): DashboardPeerRow[] {
  const used = new Set(base.map((row) => row.peer));
  const available = rankingUsers.filter((username) => !used.has(username));
  const result = [...base];
  for (let index = 0; result.length < 20 && index < available.length; index += 1) {
    result.push({
      peer: available[index]!,
      transferred: formatTransferredGb(Math.max(0.4, startGb - stepGb * index)),
      files: Math.max(1, Math.round(startFiles - fileStep * index)),
    });
  }
  return result.slice(0, 20);
}

function top20Content(base: DashboardContentRow[], startDownloads: number, step: number, startPeers: number): DashboardContentRow[] {
  const used = new Set(base.map((row) => row.folder));
  const available = contentCatalog.filter((folder) => !used.has(folder));
  const result = [...base];
  for (let index = 0; result.length < 20 && index < available.length; index += 1) {
    result.push({
      folder: available[index]!,
      downloads: Math.max(1, Math.round(startDownloads - step * index)),
      peers: Math.max(1, Math.round(startPeers - index * 0.6)),
    });
  }
  return result.slice(0, 20);
}

function top20Errors(base: DashboardErrorRow[], startCount: number): DashboardErrorRow[] {
  const used = new Set(base.map((row) => row.error));
  const available = errorCatalog.filter((error) => !used.has(error));
  const result = [...base];
  const ages = ['5h ago', '7h ago', '11h ago', '14h ago', '19h ago', '1d ago', '2d ago', '3d ago', '4d ago', '5d ago', '7d ago', '9d ago', '12d ago', '18d ago', '24d ago', '31d ago', '46d ago', '63d ago'];
  for (let index = 0; result.length < 20 && index < available.length; index += 1) {
    result.push({
      error: available[index]!,
      count: Math.max(1, startCount - index),
      lastSeen: ages[index] ?? 'older',
    });
  }
  return result.slice(0, 20);
}

export const dashboardData: Record<DashboardRangeId, DashboardRangeData> = {
  '24h': {
    label: 'Last 24 hours',
    chartLabels: ['00:00', '04:00', '08:00', '12:00', '16:00', '20:00', 'Now'],
    downloadMbps: [1.4, 1.1, 1.6, 3.2, 8.1, 5.5, 4.2, 6.0, 9.4, 7.0, 6.2, 7.1, 5.4, 3.5, 4.3],
    uploadMbps: [0.7, 0.6, 0.8, 1.4, 3.0, 2.4, 2.1, 2.8, 4.1, 3.5, 2.9, 3.2, 2.8, 1.6, 2.0],
    downloadUsers: top20Users([
      { peer: 'nightshift', transferred: '18.4 GB', files: 284 },
      { peer: 'cassetteculture', transferred: '12.7 GB', files: 196 },
      { peer: 'cloudarchive', transferred: '9.2 GB', files: 117 },
      { peer: 'tape_loop', transferred: '7.8 GB', files: 96 },
      { peer: 'lowquality_uploader', transferred: '6.1 GB', files: 82 },
    ], 5.6, 0.28, 76, 3.2),
    uploadUsers: top20Users([
      { peer: 'silvermachine', transferred: '24.7 GB', files: 611 },
      { peer: 'neonrain', transferred: '17.1 GB', files: 403 },
      { peer: 'tape_loop', transferred: '12.9 GB', files: 327 },
      { peer: 'bitrot', transferred: '9.8 GB', files: 241 },
      { peer: 'orbiting', transferred: '7.4 GB', files: 188 },
    ], 6.8, 0.31, 171, 6.5),
    content: top20Content([
      { folder: 'Boards of Canada / Geogaddi', downloads: 51, peers: 11 },
      { folder: 'Autechre / Tri Repetae', downloads: 32, peers: 5 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 31, peers: 5 },
      { folder: 'Burial / Untrue', downloads: 27, peers: 4 },
      { folder: 'Massive Attack / Mezzanine', downloads: 23, peers: 3 },
    ], 21, 1.1, 8),
    errors: top20Errors([
      { error: 'Peer disconnected', count: 3, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 1, lastSeen: '4h ago' },
    ], 2),
    summary: { downloaded: '1.46 GB', downloadFiles: 18, uploaded: '5.72 GB', uploadFiles: 143, distinctPeers: 54, shareRatio: '3.92', ratioDelta: '+0.18' },
  },
  '7d': {
    label: 'Last 7 days',
    chartLabels: ['Fri', 'Sat', 'Sun', 'Mon', 'Tue', 'Wed', 'Thu'],
    downloadMbps: [3.1, 4.4, 2.6, 6.9, 5.2, 7.6, 4.8, 5.6, 8.4, 6.5, 7.2, 4.9, 5.8, 6.4, 5.1],
    uploadMbps: [1.8, 2.2, 1.5, 3.2, 2.8, 3.9, 2.6, 3.1, 4.4, 3.8, 4.0, 2.7, 3.2, 3.7, 3.0],
    downloadUsers: top20Users([
      { peer: 'cassetteculture', transferred: '88.2 GB', files: 1284 },
      { peer: 'nightshift', transferred: '74.6 GB', files: 936 },
      { peer: 'tape_loop', transferred: '53.1 GB', files: 721 },
      { peer: 'cloudarchive', transferred: '42.7 GB', files: 568 },
      { peer: 'silvermachine', transferred: '38.9 GB', files: 501 },
    ], 35, 1.25, 470, 18),
    uploadUsers: top20Users([
      { peer: 'silvermachine', transferred: '121 GB', files: 2941 },
      { peer: 'neonrain', transferred: '96.7 GB', files: 2357 },
      { peer: 'bitrot', transferred: '82.4 GB', files: 2089 },
      { peer: 'tape_loop', transferred: '70.2 GB', files: 1742 },
      { peer: 'deepcatalog', transferred: '61.8 GB', files: 1510 },
    ], 57, 2.0, 1420, 47),
    content: top20Content([
      { folder: 'Boards of Canada / Geogaddi', downloads: 209, peers: 37 },
      { folder: 'Burial / Untrue', downloads: 184, peers: 31 },
      { folder: 'Autechre / Tri Repetae', downloads: 171, peers: 28 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 146, peers: 24 },
      { folder: 'Biosphere / Substrata', downloads: 118, peers: 20 },
    ], 108, 5.1, 19),
    errors: top20Errors([
      { error: 'Peer disconnected', count: 19, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 8, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 5, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 2, lastSeen: '3d ago' },
    ], 6),
    summary: { downloaded: '18.7 GB', downloadFiles: 231, uploaded: '42.9 GB', uploadFiles: 1094, distinctPeers: 168, shareRatio: '2.29', ratioDelta: '+0.07' },
  },
  '30d': {
    label: 'Last 30 days',
    chartLabels: ['Jul 9', 'Jul 14', 'Jul 19', 'Jul 24', 'Jul 29', 'Aug 3', 'Aug 7'],
    downloadMbps: [2.2, 3.8, 4.6, 3.1, 5.4, 6.7, 5.1, 4.2, 7.9, 6.3, 5.8, 8.8, 7.1, 5.4, 6.0],
    uploadMbps: [2.8, 3.4, 3.1, 4.2, 5.0, 4.6, 5.7, 4.9, 6.1, 5.5, 6.3, 7.0, 6.2, 5.8, 6.6],
    downloadUsers: top20Users([
      { peer: 'nightshift', transferred: '312 GB', files: 4184 },
      { peer: 'cassetteculture', transferred: '287 GB', files: 3792 },
      { peer: 'silvermachine', transferred: '219 GB', files: 2801 },
      { peer: 'cloudarchive', transferred: '198 GB', files: 2450 },
      { peer: 'tape_loop', transferred: '176 GB', files: 2127 },
    ], 163, 5.7, 2010, 72),
    uploadUsers: top20Users([
      { peer: 'silvermachine', transferred: '416 GB', files: 10192 },
      { peer: 'neonrain', transferred: '353 GB', files: 8641 },
      { peer: 'bitrot', transferred: '311 GB', files: 7428 },
      { peer: 'deepcatalog', transferred: '276 GB', files: 6811 },
      { peer: 'orbiting', transferred: '244 GB', files: 5982 },
    ], 226, 7.4, 5540, 176),
    content: top20Content([
      { folder: 'Boards of Canada / Geogaddi', downloads: 817, peers: 104 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 742, peers: 97 },
      { folder: 'Burial / Untrue', downloads: 608, peers: 82 },
      { folder: 'Autechre / Tri Repetae', downloads: 573, peers: 76 },
      { folder: 'Massive Attack / Mezzanine', downloads: 451, peers: 69 },
    ], 428, 17, 65),
    errors: top20Errors([
      { error: 'Peer disconnected', count: 87, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 41, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 24, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 15, lastSeen: '3d ago' },
      { error: 'File no longer shared', count: 7, lastSeen: '9d ago' },
    ], 12),
    summary: { downloaded: '76.4 GB', downloadFiles: 926, uploaded: '192 GB', uploadFiles: 4682, distinctPeers: 512, shareRatio: '2.51', ratioDelta: '+0.24' },
  },
  '90d': {
    label: 'Last 90 days',
    chartLabels: ['May', 'Late May', 'Jun', 'Late Jun', 'Jul', 'Late Jul', 'Aug'],
    downloadMbps: [2.8, 4.1, 3.6, 5.0, 4.4, 5.8, 6.1, 5.3, 6.8, 7.1, 6.4, 7.8, 7.0, 6.2, 6.9],
    uploadMbps: [3.7, 4.0, 4.6, 4.9, 5.5, 5.2, 6.0, 6.4, 6.1, 6.8, 7.3, 7.0, 7.6, 7.2, 7.9],
    downloadUsers: top20Users([
      { peer: 'cassetteculture', transferred: '1.02 TB', files: 14240 },
      { peer: 'nightshift', transferred: '941 GB', files: 12588 },
      { peer: 'silvermachine', transferred: '803 GB', files: 10447 },
      { peer: 'tape_loop', transferred: '694 GB', files: 9031 },
      { peer: 'cloudarchive', transferred: '618 GB', files: 8122 },
    ], 582, 18, 7750, 240),
    uploadUsers: top20Users([
      { peer: 'silvermachine', transferred: '1.48 TB', files: 36422 },
      { peer: 'neonrain', transferred: '1.21 TB', files: 30108 },
      { peer: 'bitrot', transferred: '1.04 TB', files: 26614 },
      { peer: 'deepcatalog', transferred: '918 GB', files: 23105 },
      { peer: 'orbiting', transferred: '836 GB', files: 20471 },
    ], 792, 24, 19650, 590),
    content: top20Content([
      { folder: 'Boards of Canada / Geogaddi', downloads: 2451, peers: 266 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 2194, peers: 241 },
      { folder: 'Burial / Untrue', downloads: 1892, peers: 204 },
      { folder: 'Autechre / Tri Repetae', downloads: 1707, peers: 197 },
      { folder: 'Biosphere / Substrata', downloads: 1328, peers: 162 },
    ], 1240, 47, 156),
    errors: top20Errors([
      { error: 'Peer disconnected', count: 241, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 126, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 73, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 46, lastSeen: '3d ago' },
      { error: 'File no longer shared', count: 19, lastSeen: '9d ago' },
    ], 18),
    summary: { downloaded: '238 GB', downloadFiles: 2908, uploaded: '621 GB', uploadFiles: 14834, distinctPeers: 1248, shareRatio: '2.61', ratioDelta: '+0.39' },
  },
  '1y': {
    label: 'Last 1 year',
    chartLabels: ['Sep', 'Nov', 'Jan', 'Mar', 'May', 'Jul', 'Now'],
    downloadMbps: [3.2, 4.6, 4.0, 5.4, 4.8, 6.2, 6.8, 5.9, 7.3, 6.7, 7.8, 7.1, 6.5, 7.5, 6.9],
    uploadMbps: [4.1, 4.5, 5.0, 5.4, 5.9, 5.6, 6.5, 6.9, 6.6, 7.2, 7.8, 7.5, 8.0, 7.7, 8.4],
    downloadUsers: top20Users([
      { peer: 'cassetteculture', transferred: '3.84 TB', files: 51822 },
      { peer: 'nightshift', transferred: '3.46 TB', files: 46718 },
      { peer: 'silvermachine', transferred: '2.91 TB', files: 38107 },
      { peer: 'tape_loop', transferred: '2.57 TB', files: 33614 },
      { peer: 'cloudarchive', transferred: '2.28 TB', files: 30195 },
    ], 2100, 61, 27800, 810),
    uploadUsers: top20Users([
      { peer: 'silvermachine', transferred: '5.32 TB', files: 129844 },
      { peer: 'neonrain', transferred: '4.71 TB', files: 114382 },
      { peer: 'bitrot', transferred: '4.08 TB', files: 99874 },
      { peer: 'deepcatalog', transferred: '3.62 TB', files: 88416 },
      { peer: 'orbiting', transferred: '3.31 TB', files: 80691 },
    ], 2980, 83, 74200, 2050),
    content: top20Content([
      { folder: 'Boards of Canada / Geogaddi', downloads: 8927, peers: 742 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 8144, peers: 681 },
      { folder: 'Burial / Untrue', downloads: 7231, peers: 612 },
      { folder: 'Autechre / Tri Repetae', downloads: 6918, peers: 584 },
      { folder: 'Biosphere / Substrata', downloads: 5327, peers: 491 },
    ], 5010, 185, 468),
    errors: top20Errors([
      { error: 'Peer disconnected', count: 1084, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 564, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 331, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 207, lastSeen: '3d ago' },
      { error: 'File no longer shared', count: 91, lastSeen: '9d ago' },
    ], 42),
    summary: { downloaded: '1.12 TB', downloadFiles: 13820, uploaded: '2.78 TB', uploadFiles: 67422, distinctPeers: 3912, shareRatio: '2.48', ratioDelta: '+0.31' },
  },
  all: {
    label: 'All retained history',
    chartLabels: ['2024', 'Late 2024', '2025', 'Late 2025', 'Early 2026', 'Summer', 'Now'],
    downloadMbps: [2.9, 3.8, 4.5, 4.1, 5.2, 5.8, 6.1, 5.6, 6.9, 6.4, 7.2, 6.8, 7.5, 7.0, 7.4],
    uploadMbps: [3.6, 4.2, 4.8, 5.1, 5.5, 6.0, 6.4, 6.1, 7.0, 7.4, 7.1, 7.8, 8.1, 7.7, 8.3],
    downloadUsers: top20Users([
      { peer: 'cassetteculture', transferred: '7.92 TB', files: 107384 },
      { peer: 'nightshift', transferred: '7.16 TB', files: 95820 },
      { peer: 'silvermachine', transferred: '6.08 TB', files: 79931 },
      { peer: 'tape_loop', transferred: '5.44 TB', files: 71302 },
      { peer: 'cloudarchive', transferred: '4.83 TB', files: 63844 },
    ], 4490, 127, 59800, 1670),
    uploadUsers: top20Users([
      { peer: 'silvermachine', transferred: '11.4 TB', files: 276412 },
      { peer: 'neonrain', transferred: '9.87 TB', files: 241109 },
      { peer: 'bitrot', transferred: '8.91 TB', files: 218735 },
      { peer: 'deepcatalog', transferred: '7.86 TB', files: 193006 },
      { peer: 'orbiting', transferred: '7.19 TB', files: 176248 },
    ], 6510, 177, 161000, 4320),
    content: top20Content([
      { folder: 'Boards of Canada / Geogaddi', downloads: 19382, peers: 1417 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 17631, peers: 1296 },
      { folder: 'Burial / Untrue', downloads: 15844, peers: 1182 },
      { folder: 'Autechre / Tri Repetae', downloads: 14920, peers: 1104 },
      { folder: 'Biosphere / Substrata', downloads: 11832, peers: 947 },
    ], 11050, 390, 902),
    errors: top20Errors([
      { error: 'Peer disconnected', count: 2614, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 1372, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 806, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 514, lastSeen: '3d ago' },
      { error: 'File no longer shared', count: 226, lastSeen: '9d ago' },
    ], 78),
    summary: { downloaded: '2.46 TB', downloadFiles: 30418, uploaded: '6.91 TB', uploadFiles: 164307, distinctPeers: 7264, shareRatio: '2.81', ratioDelta: '+0.00' },
  },
};

function emptyDashboardRange(range: DashboardRangeId): DashboardRangeData {
  const source = dashboardData[range];
  return {
    label: source.label,
    chartLabels: [...source.chartLabels],
    downloadMbps: source.downloadMbps.map(() => 0),
    uploadMbps: source.uploadMbps.map(() => 0),
    downloadUsers: [],
    uploadUsers: [],
    content: [],
    errors: [],
    summary: {
      downloaded: '0 B',
      downloadFiles: 0,
      uploaded: '0 B',
      uploadFiles: 0,
      distinctPeers: 0,
      shareRatio: '—',
      ratioDelta: '+0.00',
    },
  };
}

export const emptyDashboardData = {
  '24h': emptyDashboardRange('24h'),
  '7d': emptyDashboardRange('7d'),
  '30d': emptyDashboardRange('30d'),
  '90d': emptyDashboardRange('90d'),
  '1y': emptyDashboardRange('1y'),
  all: emptyDashboardRange('all'),
} satisfies Record<DashboardRangeId, DashboardRangeData>;


import type { DashboardAnalyticsDto, DashboardAnalyticsCoverageDto } from './contracts/dashboard';

interface DashboardFixtureRange {
  startUtc: string;
  endUtc: string;
  bucketSeconds: number;
  comparisonStartUtc: string | null;
  comparisonEndUtc: string | null;
}

const dashboardRangeContracts: Record<DashboardRangeId, DashboardFixtureRange> = {
  '24h': { startUtc: '2026-08-06T08:15:00.000Z', endUtc: '2026-08-07T08:15:00.000Z', bucketSeconds: 7_200, comparisonStartUtc: '2026-08-05T08:15:00.000Z', comparisonEndUtc: '2026-08-06T08:15:00.000Z' },
  '7d': { startUtc: '2026-07-31T08:15:00.000Z', endUtc: '2026-08-07T08:15:00.000Z', bucketSeconds: 43_200, comparisonStartUtc: '2026-07-24T08:15:00.000Z', comparisonEndUtc: '2026-07-31T08:15:00.000Z' },
  '30d': { startUtc: '2026-07-08T08:15:00.000Z', endUtc: '2026-08-07T08:15:00.000Z', bucketSeconds: 172_800, comparisonStartUtc: '2026-06-08T08:15:00.000Z', comparisonEndUtc: '2026-07-08T08:15:00.000Z' },
  '90d': { startUtc: '2026-05-09T08:15:00.000Z', endUtc: '2026-08-07T08:15:00.000Z', bucketSeconds: 518_400, comparisonStartUtc: '2026-02-08T08:15:00.000Z', comparisonEndUtc: '2026-05-09T08:15:00.000Z' },
  '1y': { startUtc: '2025-08-07T08:15:00.000Z', endUtc: '2026-08-07T08:15:00.000Z', bucketSeconds: 2_102_400, comparisonStartUtc: '2024-08-07T08:15:00.000Z', comparisonEndUtc: '2025-08-07T08:15:00.000Z' },
  all: { startUtc: '2024-01-01T00:00:00.000Z', endUtc: '2026-08-07T08:15:00.000Z', bucketSeconds: 5_184_000, comparisonStartUtc: null, comparisonEndUtc: null },
};

function decimalBytes(label: string): number {
  const match = /^([0-9.]+)\s*(GB|TB)$/i.exec(label);
  if (!match) return 0;
  return Number(match[1]) * (match[2]!.toUpperCase() === 'TB' ? 1_000_000_000_000 : 1_000_000_000);
}

export function dashboardContractFor(
  range: DashboardRangeId,
  options: { data?: DashboardRangeData; partialRetention?: boolean } = {},
): DashboardAnalyticsDto {
  const data = options.data ?? dashboardData[range];
  const partialRetention = options.partialRetention ?? false;
  const fixtureRange = dashboardRangeContracts[range];
  const shareRatio = Number(data.summary.shareRatio);
  const ratioDelta = Number(data.summary.ratioDelta);
  const coverage: DashboardAnalyticsCoverageDto = {
    state: partialRetention ? 'Degraded' : 'Available',
    completeFromUtc: fixtureRange.startUtc,
    isComplete: !partialRetention,
    reason: partialRetention ? 'The selected range begins before retained accounting coverage.' : null,
  };
  const start = Date.parse(fixtureRange.startUtc);
  const bucketMilliseconds = fixtureRange.bucketSeconds * 1_000;
  const bandwidth = data.downloadMbps.map((download, index) => ({
    startUtc: new Date(start + index * bucketMilliseconds).toISOString(),
    endUtc: new Date(start + (index + 1) * bucketMilliseconds).toISOString(),
    downloadBytes: Math.round(download * 1_000_000 / 8 * fixtureRange.bucketSeconds),
    uploadBytes: Math.round((data.uploadMbps[index] ?? 0) * 1_000_000 / 8 * fixtureRange.bucketSeconds),
  }));
  const summary = {
    downloadedBytes: decimalBytes(data.summary.downloaded),
    downloadedFiles: data.summary.downloadFiles,
    uploadedBytes: decimalBytes(data.summary.uploaded),
    uploadedFiles: data.summary.uploadFiles,
    distinctPeers: data.summary.distinctPeers,
    shareRatio: Number.isFinite(shareRatio) ? shareRatio : null,
  };
  return {
    accountingVersion: 1,
    range: {
      range,
      startUtc: fixtureRange.startUtc,
      endUtc: fixtureRange.endUtc,
      bucketSeconds: fixtureRange.bucketSeconds,
      coverage,
    },
    bandwidth,
    downloadPeers: data.downloadUsers.map((peer) => ({ username: peer.peer, transferredBytes: decimalBytes(peer.transferred), successfulFileCount: peer.files })),
    uploadPeers: data.uploadUsers.map((peer) => ({ username: peer.peer, transferredBytes: decimalBytes(peer.transferred), successfulFileCount: peer.files })),
    content: data.content.map((item) => ({ identity: item.folder, displayPath: item.folder, downloadCount: item.downloads, distinctPeerCount: item.peers })),
    errors: data.errors.map((error) => ({ reason: error.error, count: error.count, lastSeenUtc: '2026-08-07T07:44:00.000Z' })),
    summary,
    comparison: fixtureRange.comparisonStartUtc && fixtureRange.comparisonEndUtc
      ? {
          startUtc: fixtureRange.comparisonStartUtc,
          endUtc: fixtureRange.comparisonEndUtc,
          coverage,
          summary: {
            ...summary,
            shareRatio: Number.isFinite(shareRatio) && Number.isFinite(ratioDelta)
              ? Math.max(0, shareRatio - ratioDelta)
              : null,
          },
        }
      : null,
  };
}
