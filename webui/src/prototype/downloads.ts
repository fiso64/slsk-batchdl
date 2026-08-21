import type { ScenarioId } from '../mock/types';
import type { FolderItemFile, AudioAttributes, TransferPresentation } from './items';

export interface DownloadBase {
  id: string;
  createdAt: string;
  peer: string;
  path: string;
  sizeBytes: number;
  transfer: TransferPresentation;
}

export interface TrackDownloadItem extends DownloadBase {
  kind: 'track';
  audio?: AudioAttributes;
}

export interface AlbumDownloadItem extends DownloadBase {
  kind: 'album';
  files: FolderItemFile[];
}

export type DownloadItem = TrackDownloadItem | AlbumDownloadItem;

const normalDownloads: DownloadItem[] = [
  {
    kind: 'track', id: 'download-music-is-math', createdAt: '2026-08-07T08:13:00Z', peer: 'nightshift',
    path: 'Music/Boards of Canada/Geogaddi/02 - Music Is Math.flac', sizeBytes: 41_900_000,
    audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 },
    transfer: { state: 'Downloading', tone: 'active', cancellable: true, progressPercent: 69, progressText: '28.9 MB · 69%', speed: '2.85 MB/s', eta: '5s remaining', created: 'created 2 min ago' },
  },
  {
    kind: 'album', id: 'download-tri-repetae', createdAt: '2026-08-07T08:07:00Z', peer: 'cassetteculture',
    path: 'lossless/Autechre/1995 - Tri Repetae', sizeBytes: 502_000_000,
    transfer: { state: 'Downloading', tone: 'active', cancellable: true, progressPercent: 47, progressText: '237 MB · 47%', speed: '8.4 MB/s', eta: '32s remaining', created: 'created 8 min ago', detail: '2 of 5 files complete' },
    files: [
      { id: 'tri-1', relativePath: '01 - Dael.flac', sizeBytes: 37_900_000, audio: { bitrateKbps: 928, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 292 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: 'tri-2', relativePath: '02 - Clipper.flac', sizeBytes: 43_100_000, audio: { bitrateKbps: 971, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 302 }, transfer: { state: 'Downloading', tone: 'active', cancellable: true, progressPercent: 57, progressText: '57%', speed: '8.4 MB/s', eta: '3s' } },
      { id: 'tri-3', relativePath: '03 - Leterel.flac', sizeBytes: 51_600_000, audio: { bitrateKbps: 995, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 361 }, transfer: { state: 'Queued', tone: 'queued', cancellable: true, progressText: 'Queued' } },
      { id: 'tri-4', relativePath: '04 - Rotar.flac', sizeBytes: 39_700_000, audio: { bitrateKbps: 949, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 267 }, transfer: { state: 'Queued', tone: 'queued', cancellable: true, progressText: 'Queued' } },
      { id: 'tri-5', relativePath: 'Artwork/cover.jpg', sizeBytes: 1_800_000, transfer: { state: 'Complete', tone: 'complete' } },
    ],
  },
  {
    kind: 'track', id: 'download-dael', createdAt: '2026-08-07T08:03:00Z', peer: 'cloudarchive',
    path: 'music/Autechre/Tri Repetae/01 - Dael.flac', sizeBytes: 37_900_000,
    audio: { bitrateKbps: 928, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 292 },
    transfer: { state: 'Queued', tone: 'queued', cancellable: true, progressText: 'Waiting for peer', created: 'created 12 min ago' },
  },
  {
    kind: 'album', id: 'download-geogaddi', createdAt: '2026-08-07T07:49:00Z', peer: 'nightshift',
    path: 'Music/Boards of Canada/Geogaddi', sizeBytes: 612_000_000,
    transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: '5 files complete', created: 'created 26 min ago', detail: 'completed 7 min ago' },
    files: [
      { id: 'geo-1', relativePath: '01 - Ready Lets Go.flac', sizeBytes: 21_800_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 60 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: 'geo-2', relativePath: '02 - Music Is Math.flac', sizeBytes: 41_900_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: 'geo-3', relativePath: '03 - Beware the Friendly Stranger.flac', sizeBytes: 13_400_000, audio: { bitrateKbps: 932, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 37 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: 'geo-4', relativePath: '04 - Gyroscope.flac', sizeBytes: 31_700_000, audio: { bitrateKbps: 956, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 214 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: 'geo-5', relativePath: 'Artwork/cover.jpg', sizeBytes: 1_400_000, transfer: { state: 'Complete', tone: 'complete' } },
    ],
  },
  {
    kind: 'track', id: 'download-xtal', createdAt: '2026-08-07T07:31:00Z', peer: 'cassetteculture',
    path: 'Aphex Twin/Selected Ambient Works 85-92/Xtal.flac', sizeBytes: 34_200_000,
    audio: { bitrateKbps: 905, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 291 },
    transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, created: 'created 44 min ago', detail: 'completed 31 min ago' },
  },
  {
    kind: 'track', id: 'download-archangel', createdAt: '2026-08-07T07:15:00Z', peer: 'occasionally-offline-user',
    path: 'Library/Burial/Untrue/Archangel.flac', sizeBytes: 30_700_000,
    audio: { bitrateKbps: 892, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 240 },
    transfer: { state: 'Failed', tone: 'failed', progressPercent: 25, progressText: '7.8 MB · 25%', created: 'created 1 h ago', detail: 'Peer disconnected · 3 attempts' },
  },
];

function extraTrack(index: number): TrackDownloadItem {
  const active = index % 4 !== 3;
  const percent = 18 + (index * 13) % 74;
  return {
    kind: 'track',
    id: `download-extra-${index}`,
    createdAt: new Date(Date.parse('2026-08-07T07:00:00Z') - index * 3 * 60_000).toISOString(),
    peer: `listener_${String(index + 1).padStart(2, '0')}`,
    path: `Collection/Various Artists/Compilation ${Math.floor(index / 4) + 1}/${String(index + 1).padStart(2, '0')} - Prototype Track ${index + 1}.flac`,
    sizeBytes: 42_000_000 + index * 750_000,
    audio: { bitrateKbps: 910 + index * 8, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 220 + index * 7 },
    transfer: active
      ? { state: 'Downloading', tone: 'active', cancellable: true, progressPercent: percent, progressText: `${percent}%`, speed: `${(0.8 + index * 0.18).toFixed(2)} MB/s`, eta: `${18 + index * 4}s remaining`, created: `created ${index + 2} min ago` }
      : { state: 'Queued', tone: 'queued', cancellable: true, progressText: 'Waiting for peer', created: `created ${index + 2} min ago` },
  };
}

export function downloadsForScenario(id: ScenarioId): DownloadItem[] {
  if (id === 'empty' || id === 'offline') return [];
  if (id === 'normal') return [...normalDownloads].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const extras = Array.from({ length: id === 'stress' ? 28 : 8 }, (_, index) => extraTrack(index));
  return [...normalDownloads, ...extras].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}
