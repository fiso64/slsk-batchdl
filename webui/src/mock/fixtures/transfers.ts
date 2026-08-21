import type { TransferStateDto } from '../types';

interface TransferFixtureOptions {
  id: string;
  username: string;
  remotePath: string;
  localPath?: string | null;
  state: string;
  transferredBytes: number;
  totalBytes: number;
  bytesPerSecond?: number | null;
  direction?: 'download' | 'upload';
  terminalOutcome?: 'Succeeded' | 'Cancelled' | 'Failed' | 'Interrupted';
  failureReason?: 'FileUnavailable' | 'FileNoLongerShared' | 'FileChanged' | 'InvalidOffset' | 'Denied' | 'PeerDisconnected' | 'ConnectionFailed' | 'TransferTimedOut' | 'Unknown';
  attemptCount?: number;
  requestedAtUtc?: string;
  startedAtUtc?: string;
}

const defaultRequestedAtUtc = '2026-08-07T08:00:00.000Z';
const defaultStartedAtUtc = '2026-08-07T08:00:05.000Z';

export function transferFixture(options: TransferFixtureOptions): TransferStateDto {
  const isTerminal = options.terminalOutcome !== undefined;

  return {
    transferId: options.id,
    revision: 1,
    identity: {
      jobId: null,
      workflowId: null,
      direction: options.direction ?? 'download',
      source: 'Soulseek',
      username: options.username,
      remotePath: options.remotePath,
      candidateKey: null,
    },
    status: {
      state: options.state,
      localPath: options.localPath ?? null,
      attemptCount: options.attemptCount ?? 1,
      isTerminal,
      ...(options.terminalOutcome ? { terminalOutcome: options.terminalOutcome } : {}),
      ...(options.failureReason ? { failureReason: options.failureReason } : {}),
      availableActions: options.direction === 'upload' && !isTerminal
        ? [{ kind: 'cancel', method: 'POST', href: `/api/transfers/${options.id}/cancel` }]
        : [],
    },
    progress: {
      bytesTransferred: options.transferredBytes,
      totalBytes: options.totalBytes,
      bytesPerSecond: options.bytesPerSecond ?? null,
      lastProgressAtUtc: options.bytesPerSecond ? '2026-08-07T08:14:40.000Z' : null,
    },
    scheduling: {
      requestedAtUtc: options.requestedAtUtc ?? defaultRequestedAtUtc,
      startedAtUtc: options.state === 'Queued' ? null : options.startedAtUtc ?? defaultStartedAtUtc,
    },
  };
}

export const normalTransfers = [
  transferFixture({
    id: '10000000-0000-4000-8000-000000000001',
    username: 'nightshift',
    remotePath: 'Music\\Boards of Canada\\Geogaddi\\02 - Music Is Math.flac',
    localPath: '/downloads/Boards of Canada/Geogaddi/02 - Music Is Math.flac',
    state: 'Transferring',
    transferredBytes: 28_600_000,
    totalBytes: 41_300_000,
    bytesPerSecond: 2_850_000,
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000002',
    username: 'cloudarchive',
    remotePath: 'music\\Autechre\\Tri Repetae\\01 - Dael.flac',
    state: 'Queued',
    transferredBytes: 0,
    totalBytes: 37_900_000,
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000003',
    username: 'cassetteculture',
    remotePath: 'Aphex Twin\\Selected Ambient Works 85-92\\Xtal.flac',
    localPath: '/downloads/Aphex Twin/Selected Ambient Works 85-92/Xtal.flac',
    state: 'Completed',
    transferredBytes: 34_200_000,
    totalBytes: 34_200_000,
    terminalOutcome: 'Succeeded',
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000004',
    username: 'occasionally-offline-user',
    remotePath: 'Library\\Burial\\Untrue\\Archangel.flac',
    state: 'Failed',
    transferredBytes: 7_800_000,
    totalBytes: 30_700_000,
    terminalOutcome: 'Failed',
    failureReason: 'PeerDisconnected',
    attemptCount: 3,
  }),

  // Uploads intentionally model chronological request bursts so the WebUI can
  // project adjacent requests from one peer/folder into a folder card without
  // inventing a daemon-side folder or album transfer object.
  transferFixture({
    id: '10000000-0000-4000-8000-000000000105',
    username: 'silvermachine',
    remotePath: 'Music\\Boards of Canada\\Music Has the Right to Children\\04 - Roygbiv.flac',
    localPath: '/shares/Boards of Canada/Music Has the Right to Children/04 - Roygbiv.flac',
    direction: 'upload',
    state: 'Transferring',
    transferredBytes: 18_500_000,
    totalBytes: 30_300_000,
    bytesPerSecond: 820_000,
    requestedAtUtc: '2026-08-07T08:14:30.000Z',
    startedAtUtc: '2026-08-07T08:14:33.000Z',
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000106',
    username: 'silvermachine',
    remotePath: 'Music\\Boards of Canada\\Music Has the Right to Children\\05 - Aquarius.flac',
    localPath: '/shares/Boards of Canada/Music Has the Right to Children/05 - Aquarius.flac',
    direction: 'upload',
    state: 'Queued',
    transferredBytes: 0,
    totalBytes: 35_600_000,
    requestedAtUtc: '2026-08-07T08:14:12.000Z',
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000107',
    username: 'silvermachine',
    remotePath: 'Music\\Boards of Canada\\Music Has the Right to Children\\03 - Telephasic Workshop.flac',
    localPath: '/shares/Boards of Canada/Music Has the Right to Children/03 - Telephasic Workshop.flac',
    direction: 'upload',
    state: 'Completed',
    transferredBytes: 44_200_000,
    totalBytes: 44_200_000,
    terminalOutcome: 'Succeeded',
    requestedAtUtc: '2026-08-07T08:13:51.000Z',
    startedAtUtc: '2026-08-07T08:13:54.000Z',
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000108',
    username: 'silvermachine',
    remotePath: 'Documents\\setlist.txt',
    localPath: '/shares/Documents/setlist.txt',
    direction: 'upload',
    state: 'Completed',
    transferredBytes: 84_000,
    totalBytes: 84_000,
    terminalOutcome: 'Succeeded',
    requestedAtUtc: '2026-08-07T08:13:10.000Z',
    startedAtUtc: '2026-08-07T08:13:11.000Z',
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000109',
    username: 'tape_loop',
    remotePath: 'Music\\Autechre\\Tri Repetae\\01 - Dael.flac',
    localPath: '/shares/Autechre/Tri Repetae/01 - Dael.flac',
    direction: 'upload',
    state: 'Transferring',
    transferredBytes: 11_600_000,
    totalBytes: 37_900_000,
    bytesPerSecond: 1_250_000,
    requestedAtUtc: '2026-08-07T08:12:42.000Z',
    startedAtUtc: '2026-08-07T08:12:45.000Z',
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000110',
    username: 'tape_loop',
    remotePath: 'Music\\Autechre\\Tri Repetae\\02 - Clipper.flac',
    localPath: '/shares/Autechre/Tri Repetae/02 - Clipper.flac',
    direction: 'upload',
    state: 'Failed',
    transferredBytes: 9_100_000,
    totalBytes: 43_100_000,
    terminalOutcome: 'Failed',
    failureReason: 'PeerDisconnected',
    requestedAtUtc: '2026-08-07T08:12:21.000Z',
    startedAtUtc: '2026-08-07T08:12:24.000Z',
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000111',
    username: 'silvermachine',
    remotePath: 'Music\\Boards of Canada\\Geogaddi\\02 - Music Is Math.flac',
    localPath: '/shares/Boards of Canada/Geogaddi/02 - Music Is Math.flac',
    direction: 'upload',
    state: 'Completed',
    transferredBytes: 41_900_000,
    totalBytes: 41_900_000,
    terminalOutcome: 'Succeeded',
    requestedAtUtc: '2026-08-07T08:10:55.000Z',
    startedAtUtc: '2026-08-07T08:10:59.000Z',
  }),
] satisfies TransferStateDto[];

export const busyTransfers = [
  ...normalTransfers,
  ...Array.from({ length: 12 }, (_, index) =>
    transferFixture({
      id: `20000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`,
      username: `listener_${String(index + 1).padStart(2, '0')}`,
      remotePath: `Collection\\Various Artists\\Compilation ${Math.floor(index / 4) + 1}\\${String(index + 1).padStart(2, '0')} - Prototype Track ${index + 1}.flac`,
      direction: index % 5 === 0 ? 'upload' : 'download',
      state: index % 4 === 0 ? 'Queued' : 'Transferring',
      transferredBytes: index % 4 === 0 ? 0 : (index + 2) * 1_900_000,
      totalBytes: 42_000_000 + index * 750_000,
      bytesPerSecond: index % 4 === 0 ? null : 480_000 + index * 125_000,
      requestedAtUtc: new Date(Date.parse('2026-08-07T08:09:00.000Z') - index * 25_000).toISOString(),
    }),
  ),
] satisfies TransferStateDto[];

export const stressTransfers = Array.from({ length: 80 }, (_, index) => {
  const queued = index % 7 === 0;
  const totalBytes = 12_000_000 + (index % 18) * 4_700_000;
  const transferredBytes = queued ? 0 : Math.floor(totalBytes * ((index % 9) + 1) / 10);

  return transferFixture({
    id: `30000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`,
    username: index % 11 === 0
      ? `very_long_username_intended_to_put_layouts_under_pressure_${index}`
      : `stress_user_${index + 1}`,
    remotePath: `A deliberately long library path\\Disc ${Math.floor(index / 20) + 1}\\Folder with metadata and a long title\\${String(index + 1).padStart(3, '0')} - A track name that is intentionally long enough to expose truncation and wrapping decisions.flac`,
    direction: index % 6 === 0 ? 'upload' : 'download',
    state: queued ? 'Queued' : 'Transferring',
    transferredBytes,
    totalBytes,
    bytesPerSecond: queued ? null : 80_000 + (index % 15) * 620_000,
    requestedAtUtc: new Date(Date.parse('2026-08-07T08:14:50.000Z') - index * 8_000).toISOString(),
  });
}) satisfies TransferStateDto[];
