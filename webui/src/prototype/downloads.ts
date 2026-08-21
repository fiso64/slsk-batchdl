import type { ScenarioId } from '../mock/types';
import type { ProposedLogicalDownloadTimelineItemDto, PrototypeDataLifetime, ResourceActionDto } from './backend-contracts';
import { prototypeUuid } from './ids';
import type { FolderItemFile, AudioAttributes, TransferPresentation } from './items';

export interface DownloadBase {
  /** Logical daemon job identity. */
  id: string;
  workflowId: string | null;
  parentJobId: string | null;
  sourceJobId: string | null;
  createdAt: string;
  peer: string;
  path: string;
  sizeBytes: number;
  lifetime: PrototypeDataLifetime;
  detailAvailability: 'full' | 'summary-only';
  availableActions: ResourceActionDto[];
  timeline: ProposedLogicalDownloadTimelineItemDto;
  transfer: TransferPresentation;
}

export interface FileDownloadItem extends DownloadBase {
  kind: 'track' | 'remote-file';
  audio?: AudioAttributes;
}

export interface FolderDownloadItem extends DownloadBase {
  kind: 'album' | 'remote-directory';
  files: FolderItemFile[];
  totalFileCount?: number;
}

export type TrackDownloadItem = FileDownloadItem & { kind: 'track' };
export type AlbumDownloadItem = FolderDownloadItem & { kind: 'album' };
export type DownloadItem = FileDownloadItem | FolderDownloadItem;

function cancelAction(jobId: string): ResourceActionDto[] {
  return [{ kind: 'cancel', method: 'POST', href: `/api/jobs/${jobId}/cancel` }];
}

function decorate<T extends Omit<DownloadBase, 'workflowId' | 'parentJobId' | 'sourceJobId' | 'lifetime' | 'detailAvailability' | 'availableActions' | 'timeline'> & { kind: DownloadItem['kind'] }>(
  item: T,
  sequence: number,
  options: { lifetime?: PrototypeDataLifetime; detailAvailability?: 'full' | 'summary-only'; sourceJobId?: string | null; parentJobId?: string | null } = {},
): T & DownloadBase {
  const jobId = prototypeUuid(0x61000000, sequence);
  const workflowId = prototypeUuid(0x62000000, Math.ceil(sequence / 2));
  const terminal = item.transfer.tone === 'complete' || item.transfer.tone === 'failed' || item.transfer.tone === 'cancelled';
  const availableActions = terminal ? [] : cancelAction(jobId);
  const lifetime = options.lifetime ?? (terminal ? 'retained' : 'live-only');
  const detailAvailability = options.detailAvailability ?? 'full';
  return {
    ...item,
    id: jobId,
    workflowId,
    parentJobId: options.parentJobId ?? null,
    sourceJobId: options.sourceJobId ?? null,
    lifetime,
    detailAvailability,
    availableActions,
    transfer: { ...item.transfer, cancellable: availableActions.some((action) => action.kind === 'cancel') },
    timeline: {
      jobId,
      workflowId,
      parentJobId: options.parentJobId ?? null,
      sourceJobId: options.sourceJobId ?? null,
      kind: item.kind,
      createdAtUtc: item.createdAt,
      username: item.peer,
      sourcePath: item.path,
      lifetime,
      detailAvailability,
      availableActions,
    },
  };
}

const normalDownloads: DownloadItem[] = [
  decorate({
    kind: 'track', id: '', createdAt: '2026-08-07T08:13:00Z', peer: 'nightshift',
    path: 'Music/Boards of Canada/Geogaddi/02 - Music Is Math.flac', sizeBytes: 41_900_000,
    audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 },
    transfer: { state: 'Downloading', tone: 'active', progressPercent: 69, progressText: '28.9 MB · 69%', speed: '2.85 MB/s', eta: '5s remaining', created: 'created 2 min ago' },
  }, 1),
  decorate({
    kind: 'album', id: '', createdAt: '2026-08-07T08:07:00Z', peer: 'cassetteculture',
    path: 'lossless/Autechre/1995 - Tri Repetae', sizeBytes: 502_000_000,
    transfer: { state: 'Downloading', tone: 'active', progressPercent: 47, progressText: '237 MB · 47%', speed: '8.4 MB/s', eta: '32s remaining', created: 'created 8 min ago', detail: '2 of 5 files complete' },
    files: [
      { id: prototypeUuid(0x63000000, 1), relativePath: '01 - Dael.flac', sizeBytes: 37_900_000, audio: { bitrateKbps: 928, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 292 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: prototypeUuid(0x63000000, 2), relativePath: '02 - Clipper.flac', sizeBytes: 43_100_000, audio: { bitrateKbps: 971, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 302 }, transfer: { state: 'Downloading', tone: 'active', cancellable: true, progressPercent: 57, progressText: '57%', speed: '8.4 MB/s', eta: '3s' } },
      { id: prototypeUuid(0x63000000, 3), relativePath: '03 - Leterel.flac', sizeBytes: 51_600_000, audio: { bitrateKbps: 995, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 361 }, transfer: { state: 'Queued', tone: 'queued', cancellable: true, progressText: 'Queued' } },
      { id: prototypeUuid(0x63000000, 4), relativePath: '04 - Rotar.flac', sizeBytes: 39_700_000, audio: { bitrateKbps: 949, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 267 }, transfer: { state: 'Queued', tone: 'queued', cancellable: true, progressText: 'Queued' } },
      { id: prototypeUuid(0x63000000, 5), relativePath: 'Artwork/cover.jpg', sizeBytes: 1_800_000, transfer: { state: 'Complete', tone: 'complete' } },
    ],
  }, 2),
  decorate({
    kind: 'track', id: '', createdAt: '2026-08-07T08:03:00Z', peer: 'cloudarchive',
    path: 'music/Autechre/Tri Repetae/01 - Dael.flac', sizeBytes: 37_900_000,
    audio: { bitrateKbps: 928, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 292 },
    transfer: { state: 'Queued', tone: 'queued', progressText: 'Waiting for peer', created: 'created 12 min ago' },
  }, 3),
  decorate({
    kind: 'album', id: '', createdAt: '2026-08-07T07:49:00Z', peer: 'nightshift',
    path: 'Music/Boards of Canada/Geogaddi', sizeBytes: 612_000_000,
    transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: '5 files complete', created: 'created 26 min ago', detail: 'completed 7 min ago' },
    files: [
      { id: prototypeUuid(0x63000000, 11), relativePath: '01 - Ready Lets Go.flac', sizeBytes: 21_800_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 60 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: prototypeUuid(0x63000000, 12), relativePath: '02 - Music Is Math.flac', sizeBytes: 41_900_000, audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: prototypeUuid(0x63000000, 13), relativePath: '03 - Beware the Friendly Stranger.flac', sizeBytes: 13_400_000, audio: { bitrateKbps: 932, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 37 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: prototypeUuid(0x63000000, 14), relativePath: '04 - Gyroscope.flac', sizeBytes: 31_700_000, audio: { bitrateKbps: 956, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 214 }, transfer: { state: 'Complete', tone: 'complete' } },
      { id: prototypeUuid(0x63000000, 15), relativePath: 'Artwork/cover.jpg', sizeBytes: 1_400_000, transfer: { state: 'Complete', tone: 'complete' } },
    ],
  }, 4),
  decorate({
    kind: 'track', id: '', createdAt: '2026-08-07T07:31:00Z', peer: 'cassetteculture',
    path: 'Aphex Twin/Selected Ambient Works 85-92/Xtal.flac', sizeBytes: 34_200_000,
    audio: { bitrateKbps: 905, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 291 },
    transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, created: 'created 44 min ago', detail: 'completed 31 min ago' },
  }, 5),
  decorate({
    kind: 'track', id: '', createdAt: '2026-08-07T07:15:00Z', peer: 'occasionally-offline-user',
    path: 'Library/Burial/Untrue/Archangel.flac', sizeBytes: 30_700_000,
    audio: { bitrateKbps: 892, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 240 },
    transfer: { state: 'Failed', tone: 'failed', progressPercent: 25, progressText: '7.8 MB · 25%', created: 'created 1 h ago', detail: 'Peer disconnected · 3 attempts' },
  }, 6),
];

function extraTrack(index: number): FileDownloadItem {
  const active = index % 4 !== 3;
  const percent = 18 + (index * 13) % 74;
  return decorate({
    kind: 'track', id: '',
    createdAt: new Date(Date.parse('2026-08-07T07:00:00Z') - index * 3 * 60_000).toISOString(),
    peer: `listener_${String(index + 1).padStart(2, '0')}`,
    path: `Collection/Various Artists/Compilation ${Math.floor(index / 4) + 1}/${String(index + 1).padStart(2, '0')} - Prototype Track ${index + 1}.flac`,
    sizeBytes: 42_000_000 + index * 750_000,
    audio: { bitrateKbps: 910 + index * 8, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 220 + index * 7 },
    transfer: active
      ? { state: 'Downloading', tone: 'active', progressPercent: percent, progressText: `${percent}%`, speed: `${(0.8 + index * 0.18).toFixed(2)} MB/s`, eta: `${18 + index * 4}s remaining`, created: `created ${index + 2} min ago` }
      : { state: 'Queued', tone: 'queued', progressText: 'Waiting for peer', created: `created ${index + 2} min ago` },
  }, 100 + index);
}

function remoteFileFixture(sequence: number): FileDownloadItem {
  return decorate({
    kind: 'remote-file', id: '', createdAt: '2026-08-07T06:58:00Z', peer: 'tape_loop',
    path: 'Documents/setlist.txt', sizeBytes: 84_000,
    transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete', created: 'created 1 h ago', detail: 'downloaded from user shares' },
  }, sequence, { lifetime: 'retained', detailAvailability: 'full' });
}

function remoteDirectoryFixture(sequence: number, stress = false): FolderDownloadItem {
  return decorate({
    kind: 'remote-directory', id: '', createdAt: '2026-08-07T06:54:00Z', peer: 'silvermachine',
    path: 'Bootlegs/Live Session', sizeBytes: 188_000_000,
    transfer: { state: stress ? 'Downloading' : 'Complete', tone: stress ? 'active' : 'complete', progressPercent: stress ? 62 : 100, progressText: stress ? '62%' : '4 files complete', created: 'created 1 h ago', detail: 'downloaded from user shares' },
    totalFileCount: 4,
    files: [
      { id: prototypeUuid(0x6300f000, 1), relativePath: '01 - Intro.flac', sizeBytes: 31_000_000, transfer: { state: 'Complete', tone: 'complete' } },
      { id: prototypeUuid(0x6300f000, 2), relativePath: '02 - Session.flac', sizeBytes: 74_000_000, transfer: { state: stress ? 'Downloading' : 'Complete', tone: stress ? 'active' : 'complete', cancellable: stress, progressPercent: stress ? 41 : 100 } },
      { id: prototypeUuid(0x6300f000, 3), relativePath: '03 - Encore.flac', sizeBytes: 81_000_000, transfer: { state: stress ? 'Queued' : 'Complete', tone: stress ? 'queued' : 'complete', cancellable: stress } },
      { id: prototypeUuid(0x6300f000, 4), relativePath: 'info.txt', sizeBytes: 2_000_000, transfer: { state: 'Complete', tone: 'complete' } },
    ],
  }, sequence, { lifetime: stress ? 'live-only' : 'retained', detailAvailability: stress ? 'full' : 'summary-only' });
}

export function downloadsForScenario(id: ScenarioId): DownloadItem[] {
  if (id === 'empty' || id === 'offline') return [];
  if (id === 'normal') return [...normalDownloads].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const extras = Array.from({ length: id === 'stress' ? 28 : 8 }, (_, index) => extraTrack(index));
  const shareDownloads: DownloadItem[] = [remoteFileFixture(900), remoteDirectoryFixture(901, id === 'stress')];
  return [...normalDownloads, ...extras, ...shareDownloads].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}
