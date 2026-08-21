import type { TransferStateDto } from '../mock/types';
import type { PrototypeDataLifetime } from './backend-contracts';
import type { AudioAttributes, FolderItemFile, TransferPresentation } from './items';

export interface TransferTimelineFileEntry {
  kind: 'file';
  id: string;
  path: string;
  sizeBytes: number;
  audio?: AudioAttributes;
  lifetime: PrototypeDataLifetime;
  sourceTransferIds: string[];
  transfer: TransferPresentation;
}

export interface TransferTimelineFolderEntry {
  kind: 'folder';
  id: string;
  path: string;
  sizeBytes: number;
  files: FolderItemFile[];
  totalFileCount?: number;
  lifetime: PrototypeDataLifetime;
  sourceTransferIds: string[];
  transfer: TransferPresentation;
}

export type TransferTimelineEntry = TransferTimelineFileEntry | TransferTimelineFolderEntry;

export interface TransferTimelinePeerGroup {
  key: string;
  peer: string;
  /** Number of underlying file transfers represented by this adjacent run. */
  transferCount: number;
  items: TransferTimelineEntry[];
}


export function limitTransferGroups(groups: TransferTimelinePeerGroup[], itemLimit: number): TransferTimelinePeerGroup[] {
  let remaining = Math.max(0, itemLimit);
  const limited: TransferTimelinePeerGroup[] = [];
  for (const group of groups) {
    if (remaining <= 0) break;
    const items = group.items.slice(0, remaining);
    if (!items.length) continue;
    const transferCount = items.reduce((total, item) => total + (item.kind === 'folder' ? (item.totalFileCount ?? item.files.length) : 1), 0);
    limited.push({ ...group, items, transferCount });
    remaining -= items.length;
  }
  return limited;
}

export function transferGroupItemCount(groups: TransferTimelinePeerGroup[]): number {
  return groups.reduce((total, group) => total + group.items.length, 0);
}

export function isTerminalTransfer(transfer?: TransferPresentation): boolean {
  return transfer?.tone === 'complete' || transfer?.tone === 'failed' || transfer?.tone === 'cancelled';
}

function numeric(value: number | string | null | undefined): number | null {
  if (value === null || value === undefined) return null;
  const result = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(result) ? result : null;
}

export function progressPercent(transfer: TransferStateDto): number | null {
  const total = numeric(transfer.progress.totalBytes);
  const transferred = numeric(transfer.progress.bytesTransferred);
  if (total === null || transferred === null || total <= 0) return null;
  return Math.min(100, Math.max(0, (transferred / total) * 100));
}

export function formatSpeed(bytesPerSecond: number | string | null | undefined): string | null {
  const speed = numeric(bytesPerSecond);
  if (speed === null || speed <= 0) return null;
  return `${formatBytes(speed)}/s`;
}

function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit += 1;
  }

  const digits = value >= 100 || unit === 0 ? 0 : value >= 10 ? 1 : 2;
  return `${value.toFixed(digits)} ${units[unit]}`;
}

export function formatEta(transfer: TransferStateDto): string | null {
  const speed = numeric(transfer.progress.bytesPerSecond);
  const total = numeric(transfer.progress.totalBytes);
  const transferred = numeric(transfer.progress.bytesTransferred);
  if (speed === null || speed <= 0 || total === null || transferred === null) return null;

  const seconds = Math.max(0, Math.ceil((total - transferred) / speed));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  if (minutes < 60) return `${minutes}m ${String(remainder).padStart(2, '0')}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}
