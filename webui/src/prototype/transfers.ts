import type { TransferStateDto } from '../mock/types';

export function transferFilename(transfer: TransferStateDto): string {
  const path = transfer.identity.remotePath ?? transfer.status.localPath ?? '(unknown path)';
  const parts = path.split(/[\\/]/);
  return parts.at(-1) || path;
}

export function transferFolder(transfer: TransferStateDto): string {
  const path = transfer.identity.remotePath ?? transfer.status.localPath ?? '';
  const parts = path.split(/[\\/]/);
  return parts.slice(0, -1).join(' / ') || 'Unknown folder';
}

export function transferUser(transfer: TransferStateDto): string {
  return transfer.identity.username ?? '(unknown user)';
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

export function formatBytes(bytes: number): string {
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
