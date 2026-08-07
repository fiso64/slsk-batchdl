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
}

const requestedAtUtc = '2026-08-07T08:00:00.000Z';
const startedAtUtc = '2026-08-07T08:00:05.000Z';

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
    },
    progress: {
      bytesTransferred: options.transferredBytes,
      totalBytes: options.totalBytes,
      bytesPerSecond: options.bytesPerSecond ?? null,
      lastProgressAtUtc: options.bytesPerSecond ? '2026-08-07T08:10:00.000Z' : null,
    },
    scheduling: {
      requestedAtUtc,
      startedAtUtc: options.state === 'Queued' ? null : startedAtUtc,
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
  transferFixture({
    id: '10000000-0000-4000-8000-000000000005',
    username: 'silvermachine',
    remotePath: 'Music\\Boards of Canada\\Music Has the Right to Children\\04 - Roygbiv.flac',
    localPath: '/shares/Boards of Canada/Music Has the Right to Children/04 - Roygbiv.flac',
    direction: 'upload',
    state: 'Transferring',
    transferredBytes: 18_500_000,
    totalBytes: 30_300_000,
    bytesPerSecond: 820_000,
  }),
  transferFixture({
    id: '10000000-0000-4000-8000-000000000006',
    username: 'tape_loop',
    remotePath: 'Music\\Boards of Canada\\Music Has the Right to Children\\06 - Turquoise Hexagon Sun.flac',
    localPath: '/shares/Boards of Canada/Music Has the Right to Children/06 - Turquoise Hexagon Sun.flac',
    direction: 'upload',
    state: 'Completed',
    transferredBytes: 28_800_000,
    totalBytes: 28_800_000,
    terminalOutcome: 'Succeeded',
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
  });
}) satisfies TransferStateDto[];
