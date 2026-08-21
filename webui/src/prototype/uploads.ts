import type { PrototypeScenario, TransferStateDto } from '../mock/types';
import { groupAdjacentBy } from './grouping';
import type { AudioAttributes, FolderItemFile, TransferPresentation } from './items';
import type { PrototypeDataLifetime, ResourceActionDto } from './backend-contracts';
import { formatEta, formatSpeed, progressPercent, type TransferTimelineEntry, type TransferTimelinePeerGroup } from './transfers';
import { prototypeUuid } from './ids';

interface UploadTransferView {
  id: string;
  peer: string;
  path: string;
  folderPath: string;
  sizeBytes: number;
  audio?: AudioAttributes;
  transferredBytes: number;
  bytesPerSecond: number;
  requestedAtUtc: string;
  lifetime: PrototypeDataLifetime;
  availableActions: ResourceActionDto[];
  transfer: TransferPresentation;
}

export type UploadEntry = TransferTimelineEntry;
export type UploadFolderEntry = Extract<TransferTimelineEntry, { kind: 'folder' }>;
export type UploadPeerGroup = TransferTimelinePeerGroup;

function stableNumber(value: string): number {
  let hash = 2166136261;
  for (const char of value) hash = Math.imul(hash ^ char.charCodeAt(0), 16777619) >>> 0;
  return hash % 900_000;
}

function numeric(value: number | string | null | undefined): number {
  if (value === null || value === undefined) return 0;
  const result = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(result) ? result : 0;
}

function displayPath(path: string): string {
  return path.replace(/[\\/]+/g, '/');
}

function pathFor(transfer: TransferStateDto): string {
  return displayPath(transfer.identity.remotePath ?? transfer.status.localPath ?? '(unknown path)');
}

function folderFor(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash > 0 ? path.slice(0, slash) : '(root)';
}

function basename(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash >= 0 ? path.slice(slash + 1) : path;
}

function words(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ').toLowerCase().replace(/^./, (letter) => letter.toUpperCase());
}

function relativeAge(timestamp: string | null | undefined, capturedAtUtc: string): string | undefined {
  if (!timestamp) return undefined;
  const then = Date.parse(timestamp);
  const now = Date.parse(capturedAtUtc);
  if (!Number.isFinite(then) || !Number.isFinite(now)) return undefined;
  const seconds = Math.max(0, Math.floor((now - then) / 1000));
  if (seconds < 45) return 'just now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.floor(hours / 24);
  return `${days} d ago`;
}

function toneFor(transfer: TransferStateDto, cancelled: boolean): NonNullable<TransferPresentation['tone']> {
  if (cancelled || transfer.status.terminalOutcome === 'Cancelled') return 'cancelled';
  if (transfer.status.terminalOutcome === 'Succeeded') return 'complete';
  if (transfer.status.terminalOutcome === 'Failed' || transfer.status.terminalOutcome === 'Interrupted') return 'failed';
  if (transfer.status.state === 'Queued') return 'queued';
  return 'active';
}

function stateFor(tone: NonNullable<TransferPresentation['tone']>): string {
  if (tone === 'complete') return 'Complete';
  if (tone === 'failed') return 'Failed';
  if (tone === 'cancelled') return 'Cancelled';
  if (tone === 'queued') return 'Queued';
  return 'Uploading';
}

function transferPresentation(
  transfer: TransferStateDto,
  capturedAtUtc: string,
  cancelled: boolean,
): TransferPresentation {
  const tone = toneFor(transfer, cancelled);
  const percent = progressPercent(transfer);
  const age = relativeAge(transfer.scheduling?.requestedAtUtc, capturedAtUtc);
  const detailParts: string[] = [];
  if (transfer.status.failureReason) detailParts.push(words(transfer.status.failureReason));
  const attemptCount = numeric(transfer.status.attemptCount ?? 1);
  if (attemptCount > 1) detailParts.push(`${attemptCount} attempts`);
  const eta = tone === 'active' ? formatEta(transfer) : null;

  return {
    state: stateFor(tone),
    tone,
    direction: 'upload',
    cancellable: !cancelled && (transfer.status.availableActions ?? []).some((action) => action.kind === 'cancel'),
    progressPercent: percent ?? undefined,
    progressText: tone === 'queued' ? 'Queued' : tone === 'cancelled' ? 'Cancelled' : percent !== null ? `${percent.toFixed(0)}%` : undefined,
    speed: tone === 'active' ? formatSpeed(transfer.progress.bytesPerSecond) ?? undefined : undefined,
    eta: eta ? `${eta} remaining` : undefined,
    created: age ? `requested ${age}` : undefined,
    detail: detailParts.length ? detailParts.join(' · ') : undefined,
  };
}

function viewFor(
  transfer: TransferStateDto,
  capturedAtUtc: string,
  cancelledTransferIds: ReadonlySet<string>,
): UploadTransferView {
  const path = pathFor(transfer);
  return {
    id: transfer.transferId,
    peer: transfer.identity.username ?? '(unknown user)',
    path,
    folderPath: folderFor(path),
    sizeBytes: numeric(transfer.progress.totalBytes),
    transferredBytes: numeric(transfer.progress.bytesTransferred),
    bytesPerSecond: numeric(transfer.progress.bytesPerSecond),
    requestedAtUtc: transfer.scheduling?.requestedAtUtc ?? '',
    lifetime: transfer.status.isTerminal ? 'retained' : 'live-only',
    availableActions: transfer.status.availableActions ?? [],
    transfer: transferPresentation(transfer, capturedAtUtc, cancelledTransferIds.has(transfer.transferId)),
  };
}

function aggregateFolderPresentation(files: UploadTransferView[], capturedAtUtc: string): TransferPresentation {
  const tones = files.map((file) => file.transfer.tone);
  const complete = tones.filter((tone) => tone === 'complete').length;
  const failed = tones.filter((tone) => tone === 'failed').length;
  const cancelled = tones.filter((tone) => tone === 'cancelled').length;

  let tone: NonNullable<TransferPresentation['tone']>;
  if (tones.every((value) => value === 'complete')) tone = 'complete';
  else if (tones.every((value) => value === 'cancelled')) tone = 'cancelled';
  else if (tones.includes('active')) tone = 'active';
  else if (tones.includes('queued')) tone = 'queued';
  else if (tones.includes('failed')) tone = 'failed';
  else tone = 'cancelled';

  const totalBytes = files.reduce((total, file) => total + file.sizeBytes, 0);
  const transferredBytes = files.reduce((total, file) => total + file.transferredBytes, 0);
  const progress = totalBytes > 0 ? Math.min(100, Math.max(0, (transferredBytes / totalBytes) * 100)) : undefined;
  const activeFiles = files.filter((file) => file.transfer.tone === 'active');
  const speedBytes = activeFiles.reduce((total, file) => total + file.bytesPerSecond, 0);
  const remainingBytes = files
    .filter((file) => file.transfer.tone === 'active' || file.transfer.tone === 'queued')
    .reduce((total, file) => total + Math.max(0, file.sizeBytes - file.transferredBytes), 0);
  const etaSeconds = speedBytes > 0 ? Math.ceil(remainingBytes / speedBytes) : 0;
  const eta = etaSeconds > 0
    ? etaSeconds < 60
      ? `${etaSeconds}s remaining`
      : `${Math.floor(etaSeconds / 60)}m ${String(etaSeconds % 60).padStart(2, '0')}s remaining`
    : undefined;
  const latest = files.reduce((value, file) => file.requestedAtUtc > value ? file.requestedAtUtc : value, '');
  const age = relativeAge(latest, capturedAtUtc);

  const statusParts: string[] = [];
  if (complete && !failed && !cancelled) statusParts.push(`${complete} of ${files.length} files complete`);
  else {
    if (complete) statusParts.push(`${complete} complete`);
    if (failed) statusParts.push(`${failed} failed`);
    if (cancelled) statusParts.push(`${cancelled} cancelled`);
  }
  if (!statusParts.length && progress !== undefined) statusParts.push(`${progress.toFixed(0)}%`);

  return {
    state: stateFor(tone),
    tone,
    direction: 'upload',
    cancellable: files.some((file) => file.transfer.cancellable),
    progressPercent: progress,
    progressText: statusParts.join(' · '),
    speed: speedBytes > 0 ? formatSpeed(speedBytes) ?? undefined : undefined,
    eta,
    created: age ? `latest request ${age}` : undefined,
  };
}

function projectFolderRun(
  key: string,
  folderPath: string,
  files: UploadTransferView[],
  capturedAtUtc: string,
): UploadEntry {
  if (files.length === 1) {
    const file = files[0]!;
    return {
      kind: 'file',
      id: file.id,
      path: file.path,
      sizeBytes: file.sizeBytes,
      audio: file.audio,
      lifetime: file.lifetime,
      sourceTransferIds: [file.id],
      transfer: file.transfer,
    };
  }

  return {
    kind: 'folder',
    id: prototypeUuid(0x64000000, stableNumber(key)),
    path: folderPath,
    sizeBytes: files.reduce((total, file) => total + file.sizeBytes, 0),
    sourceTransferIds: files.map((file) => file.id),
    lifetime: files.every((file) => file.lifetime === 'retained') ? 'retained' : 'live-only',
    files: files.map((file) => ({
      id: file.id,
      relativePath: basename(file.path),
      sizeBytes: file.sizeBytes,
      audio: file.audio,
      lifetime: file.lifetime,
      sourceTransferIds: [file.id],
      transfer: file.transfer,
    })),
    transfer: aggregateFolderPresentation(files, capturedAtUtc),
  };
}

export function uploadsForScenario(
  scenario: PrototypeScenario,
  cancelledTransferIds: ReadonlySet<string> = new Set<string>(),
  removedTransferIds: ReadonlySet<string> = new Set<string>(),
): UploadPeerGroup[] {
  const capturedAtUtc = scenario.snapshot.capturedAtUtc;
  const uploads = scenario.snapshot.transfers
    .filter((transfer) => transfer.identity.direction === 'upload' && !removedTransferIds.has(transfer.transferId))
    .slice()
    .sort((a, b) => (b.scheduling?.requestedAtUtc ?? '').localeCompare(a.scheduling?.requestedAtUtc ?? ''))
    .map((transfer) => viewFor(transfer, capturedAtUtc, cancelledTransferIds));

  return groupAdjacentBy(uploads, (upload) => upload.peer, `${scenario.id}:uploads:user:`).map((peerGroup) => {
    const folderRuns = groupAdjacentBy(peerGroup.items, (upload) => upload.folderPath, `${peerGroup.key}:folder:`);
    return {
      key: peerGroup.key,
      peer: peerGroup.identity,
      transferCount: peerGroup.items.length,
      items: folderRuns.map((folderRun) => projectFolderRun(folderRun.key, folderRun.identity, folderRun.items, capturedAtUtc)),
    };
  });
}
