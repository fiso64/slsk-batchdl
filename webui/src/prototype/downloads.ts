import type { ScenarioId } from '../mock/types';
import type { PrototypeDataLifetime, ResourceActionDto } from './backend-contracts';
import { prototypeUuid } from './ids';
import type { FolderItemFile, TransferPresentation } from './items';
import type { TransferTimelineEntry, TransferTimelineFileEntry, TransferTimelineFolderEntry } from './transfers';

interface DownloadEntryState {
  createdAt: string;
  peer: string;
  availableActions: ResourceActionDto[];
}

export type DownloadFileEntry = TransferTimelineFileEntry & DownloadEntryState;
export type DownloadFolderEntry = TransferTimelineFolderEntry & DownloadEntryState;
export type DownloadItem = DownloadFileEntry | DownloadFolderEntry;

function cancelAction(id: string): ResourceActionDto[] {
  return [{ kind: 'cancel', method: 'POST', href: `/api/jobs/${id}/cancel` }];
}

type UndecoratedDownload =
  | (Omit<TransferTimelineFileEntry, 'id' | 'lifetime' | 'sourceTransferIds'> & { createdAt: string; peer: string })
  | (Omit<TransferTimelineFolderEntry, 'id' | 'lifetime' | 'sourceTransferIds'> & { createdAt: string; peer: string });

function decorate(
  item: UndecoratedDownload,
  sequence: number,
  options: { lifetime?: PrototypeDataLifetime } = {},
): DownloadItem {
  const id = prototypeUuid(0x61000000, sequence);
  const terminal = item.transfer.tone === 'complete' || item.transfer.tone === 'failed' || item.transfer.tone === 'cancelled';
  const availableActions = terminal ? [] : cancelAction(id);
  const lifetime = options.lifetime ?? (terminal ? 'retained' : 'live-only');
  const sourceTransferIds = item.kind === 'folder' ? item.files.map((file) => file.id) : [id];
  return {
    ...item,
    id,
    lifetime,
    sourceTransferIds,
    availableActions,
    transfer: { ...item.transfer, cancellable: availableActions.some((action) => action.kind === 'cancel') },
  } as DownloadItem;
}

const normalDownloads: DownloadItem[] = [
  decorate({
    kind: 'file', createdAt: '2026-08-07T08:13:00Z', peer: 'nightshift',
    path: 'Music/Boards of Canada/Geogaddi/02 - Music Is Math.flac', sizeBytes: 41_900_000,
    audio: { bitrateKbps: 944, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 323 },
    transfer: { state: 'Downloading', tone: 'active', progressPercent: 69, progressText: '28.9 MB · 69%', speed: '2.85 MB/s', eta: '5s remaining', created: 'created 2 min ago' },
  }, 1),
  decorate({
    kind: 'folder', createdAt: '2026-08-07T08:07:00Z', peer: 'cassetteculture',
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
    kind: 'file', createdAt: '2026-08-07T08:03:00Z', peer: 'cloudarchive',
    path: 'music/Autechre/Tri Repetae/01 - Dael.flac', sizeBytes: 37_900_000,
    audio: { bitrateKbps: 928, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 292 },
    transfer: { state: 'Queued', tone: 'queued', progressText: 'Waiting for peer', created: 'created 12 min ago' },
  }, 3),
  decorate({
    kind: 'folder', createdAt: '2026-08-07T07:49:00Z', peer: 'nightshift',
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
    kind: 'file', createdAt: '2026-08-07T07:31:00Z', peer: 'cassetteculture',
    path: 'Aphex Twin/Selected Ambient Works 85-92/Xtal.flac', sizeBytes: 34_200_000,
    audio: { bitrateKbps: 905, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 291 },
    transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, created: 'created 44 min ago', detail: 'completed 31 min ago' },
  }, 5),
  decorate({
    kind: 'file', createdAt: '2026-08-07T07:15:00Z', peer: 'occasionally-offline-user',
    path: 'Library/Burial/Untrue/Archangel.flac', sizeBytes: 30_700_000,
    audio: { bitrateKbps: 892, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 240 },
    transfer: { state: 'Failed', tone: 'failed', progressPercent: 25, progressText: '7.8 MB · 25%', created: 'created 1 h ago', detail: 'Peer disconnected · 3 attempts' },
  }, 6),
];

function extraFile(index: number): DownloadFileEntry {
  const active = index % 4 !== 3;
  const percent = 18 + (index * 13) % 74;
  return decorate({
    kind: 'file',
    createdAt: new Date(Date.parse('2026-08-07T07:00:00Z') - index * 3 * 60_000).toISOString(),
    peer: `listener_${String(index + 1).padStart(2, '0')}`,
    path: `Collection/Various Artists/Compilation ${Math.floor(index / 4) + 1}/${String(index + 1).padStart(2, '0')} - Prototype Track ${index + 1}.flac`,
    sizeBytes: 42_000_000 + index * 750_000,
    audio: { bitrateKbps: 910 + index * 8, sampleRateHz: 44_100, bitDepth: 16, lengthSeconds: 220 + index * 7 },
    transfer: active
      ? { state: 'Downloading', tone: 'active', progressPercent: percent, progressText: `${percent}%`, speed: `${(0.8 + index * 0.18).toFixed(2)} MB/s`, eta: `${18 + index * 4}s remaining`, created: `created ${index + 2} min ago` }
      : { state: 'Queued', tone: 'queued', progressText: 'Waiting for peer', created: `created ${index + 2} min ago` },
  }, 100 + index) as DownloadFileEntry;
}

function shareFileFixture(sequence: number): DownloadFileEntry {
  return decorate({
    kind: 'file', createdAt: '2026-08-07T06:58:00Z', peer: 'tape_loop',
    path: 'Documents/setlist.txt', sizeBytes: 84_000,
    transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete', created: 'created 1 h ago', detail: 'downloaded from user shares' },
  }, sequence, { lifetime: 'retained' }) as DownloadFileEntry;
}

function shareFolderFixture(sequence: number, stress = false): DownloadFolderEntry {
  const files: FolderItemFile[] = [
    { id: prototypeUuid(0x6300f000, 1), relativePath: '01 - Intro.flac', sizeBytes: 31_000_000, transfer: { state: 'Complete', tone: 'complete' } },
    { id: prototypeUuid(0x6300f000, 2), relativePath: '02 - Session.flac', sizeBytes: 74_000_000, transfer: { state: stress ? 'Downloading' : 'Complete', tone: stress ? 'active' : 'complete', cancellable: stress, progressPercent: stress ? 41 : 100 } },
    { id: prototypeUuid(0x6300f000, 3), relativePath: '03 - Encore.flac', sizeBytes: 81_000_000, transfer: { state: stress ? 'Queued' : 'Complete', tone: stress ? 'queued' : 'complete', cancellable: stress } },
    { id: prototypeUuid(0x6300f000, 4), relativePath: 'info.txt', sizeBytes: 2_000_000, transfer: { state: 'Complete', tone: 'complete' } },
  ];
  return decorate({
    kind: 'folder', createdAt: '2026-08-07T06:54:00Z', peer: 'silvermachine',
    path: 'Bootlegs/Live Session', sizeBytes: 188_000_000,
    transfer: { state: stress ? 'Downloading' : 'Complete', tone: stress ? 'active' : 'complete', progressPercent: stress ? 62 : 100, progressText: stress ? '62%' : '4 files complete', created: 'created 1 h ago', detail: 'downloaded from user shares' },
    totalFileCount: 4,
    // Retained history currently cannot reconstruct authoritative child metadata.
    files: stress ? files : [],
  }, sequence, { lifetime: stress ? 'live-only' : 'retained' }) as DownloadFolderEntry;
}

export function downloadsForScenario(id: ScenarioId): DownloadItem[] {
  if (id === 'empty' || id === 'offline') return [];
  if (id === 'normal') return [...normalDownloads].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const extras = Array.from({ length: id === 'stress' ? 28 : 8 }, (_, index) => extraFile(index));
  const shareDownloads: DownloadItem[] = [shareFileFixture(900), shareFolderFixture(901, id === 'stress')];
  return [...normalDownloads, ...extras, ...shareDownloads].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}
