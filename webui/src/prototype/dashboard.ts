export const dashboardRangeIds = ['24h', '7d', '30d', '90d'] as const;
export type DashboardRangeId = (typeof dashboardRangeIds)[number];

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
  peers: DashboardPeerRow[];
  content: DashboardContentRow[];
  errors: DashboardErrorRow[];
  summary: {
    downloaded: string;
    downloadFiles: number;
    uploaded: string;
    uploadFiles: number;
    shareRatio: string;
    ratioDelta: string;
  };
}

export const dashboardData: Record<DashboardRangeId, DashboardRangeData> = {
  '24h': {
    label: 'Last 24 hours',
    chartLabels: ['00:00', '04:00', '08:00', '12:00', '16:00', '20:00', 'Now'],
    downloadMbps: [1.4, 1.1, 1.6, 3.2, 8.1, 5.5, 4.2, 6.0, 9.4, 7.0, 6.2, 7.1, 5.4, 3.5, 4.3],
    uploadMbps: [0.7, 0.6, 0.8, 1.4, 3.0, 2.4, 2.1, 2.8, 4.1, 3.5, 2.9, 3.2, 2.8, 1.6, 2.0],
    peers: [
      { peer: 'nightshift', transferred: '18.4 GB', files: 284 },
      { peer: 'cassetteculture', transferred: '12.7 GB', files: 196 },
      { peer: 'cloudarchive', transferred: '9.2 GB', files: 117 },
      { peer: 'tape_loop', transferred: '7.8 GB', files: 96 },
      { peer: 'lowquality_uploader', transferred: '6.1 GB', files: 82 },
    ],
    content: [
      { folder: 'Boards of Canada / Geogaddi', downloads: 51, peers: 11 },
      { folder: 'Autechre / Tri Repetae', downloads: 32, peers: 5 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 31, peers: 5 },
      { folder: 'Burial / Untrue', downloads: 27, peers: 4 },
      { folder: 'Massive Attack / Mezzanine', downloads: 23, peers: 3 },
    ],
    errors: [
      { error: 'Peer disconnected', count: 3, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 1, lastSeen: '4h ago' },
    ],
    summary: { downloaded: '1.46 GB', downloadFiles: 18, uploaded: '5.72 GB', uploadFiles: 143, shareRatio: '3.92', ratioDelta: '+0.18' },
  },
  '7d': {
    label: 'Last 7 days',
    chartLabels: ['Fri', 'Sat', 'Sun', 'Mon', 'Tue', 'Wed', 'Thu'],
    downloadMbps: [3.1, 4.4, 2.6, 6.9, 5.2, 7.6, 4.8, 5.6, 8.4, 6.5, 7.2, 4.9, 5.8, 6.4, 5.1],
    uploadMbps: [1.8, 2.2, 1.5, 3.2, 2.8, 3.9, 2.6, 3.1, 4.4, 3.8, 4.0, 2.7, 3.2, 3.7, 3.0],
    peers: [
      { peer: 'cassetteculture', transferred: '88.2 GB', files: 1284 },
      { peer: 'nightshift', transferred: '74.6 GB', files: 936 },
      { peer: 'tape_loop', transferred: '53.1 GB', files: 721 },
      { peer: 'cloudarchive', transferred: '42.7 GB', files: 568 },
      { peer: 'silvermachine', transferred: '38.9 GB', files: 501 },
    ],
    content: [
      { folder: 'Boards of Canada / Geogaddi', downloads: 209, peers: 37 },
      { folder: 'Burial / Untrue', downloads: 184, peers: 31 },
      { folder: 'Autechre / Tri Repetae', downloads: 171, peers: 28 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 146, peers: 24 },
      { folder: 'Biosphere / Substrata', downloads: 118, peers: 20 },
    ],
    errors: [
      { error: 'Peer disconnected', count: 19, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 8, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 5, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 2, lastSeen: '3d ago' },
    ],
    summary: { downloaded: '18.7 GB', downloadFiles: 231, uploaded: '42.9 GB', uploadFiles: 1094, shareRatio: '2.29', ratioDelta: '+0.07' },
  },
  '30d': {
    label: 'Last 30 days',
    chartLabels: ['Jul 9', 'Jul 14', 'Jul 19', 'Jul 24', 'Jul 29', 'Aug 3', 'Aug 7'],
    downloadMbps: [2.2, 3.8, 4.6, 3.1, 5.4, 6.7, 5.1, 4.2, 7.9, 6.3, 5.8, 8.8, 7.1, 5.4, 6.0],
    uploadMbps: [2.8, 3.4, 3.1, 4.2, 5.0, 4.6, 5.7, 4.9, 6.1, 5.5, 6.3, 7.0, 6.2, 5.8, 6.6],
    peers: [
      { peer: 'nightshift', transferred: '312 GB', files: 4184 },
      { peer: 'cassetteculture', transferred: '287 GB', files: 3792 },
      { peer: 'silvermachine', transferred: '219 GB', files: 2801 },
      { peer: 'cloudarchive', transferred: '198 GB', files: 2450 },
      { peer: 'tape_loop', transferred: '176 GB', files: 2127 },
    ],
    content: [
      { folder: 'Boards of Canada / Geogaddi', downloads: 817, peers: 104 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 742, peers: 97 },
      { folder: 'Burial / Untrue', downloads: 608, peers: 82 },
      { folder: 'Autechre / Tri Repetae', downloads: 573, peers: 76 },
      { folder: 'Massive Attack / Mezzanine', downloads: 451, peers: 69 },
    ],
    errors: [
      { error: 'Peer disconnected', count: 87, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 41, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 24, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 15, lastSeen: '3d ago' },
      { error: 'File no longer shared', count: 7, lastSeen: '9d ago' },
    ],
    summary: { downloaded: '76.4 GB', downloadFiles: 926, uploaded: '192 GB', uploadFiles: 4682, shareRatio: '2.51', ratioDelta: '+0.24' },
  },
  '90d': {
    label: 'Last 90 days',
    chartLabels: ['May', 'Late May', 'Jun', 'Late Jun', 'Jul', 'Late Jul', 'Aug'],
    downloadMbps: [2.8, 4.1, 3.6, 5.0, 4.4, 5.8, 6.1, 5.3, 6.8, 7.1, 6.4, 7.8, 7.0, 6.2, 6.9],
    uploadMbps: [3.7, 4.0, 4.6, 4.9, 5.5, 5.2, 6.0, 6.4, 6.1, 6.8, 7.3, 7.0, 7.6, 7.2, 7.9],
    peers: [
      { peer: 'cassetteculture', transferred: '1.02 TB', files: 14240 },
      { peer: 'nightshift', transferred: '941 GB', files: 12588 },
      { peer: 'silvermachine', transferred: '803 GB', files: 10447 },
      { peer: 'tape_loop', transferred: '694 GB', files: 9031 },
      { peer: 'cloudarchive', transferred: '618 GB', files: 8122 },
    ],
    content: [
      { folder: 'Boards of Canada / Geogaddi', downloads: 2451, peers: 266 },
      { folder: 'Aphex Twin / SAW 85–92', downloads: 2194, peers: 241 },
      { folder: 'Burial / Untrue', downloads: 1892, peers: 204 },
      { folder: 'Autechre / Tri Repetae', downloads: 1707, peers: 197 },
      { folder: 'Biosphere / Substrata', downloads: 1328, peers: 162 },
    ],
    errors: [
      { error: 'Peer disconnected', count: 241, lastSeen: '31m ago' },
      { error: 'Transfer timed out', count: 126, lastSeen: '4h ago' },
      { error: 'Connection failed', count: 73, lastSeen: '1d ago' },
      { error: 'File unavailable', count: 46, lastSeen: '3d ago' },
      { error: 'File no longer shared', count: 19, lastSeen: '9d ago' },
    ],
    summary: { downloaded: '238 GB', downloadFiles: 2908, uploaded: '621 GB', uploadFiles: 14834, shareRatio: '2.61', ratioDelta: '+0.39' },
  },
};
