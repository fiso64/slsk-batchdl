import type { SoulseekClientStatusDto } from '../mock/types';

export function humanizeStateValue(value: string): string {
  const normalized = value
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim();

  return normalized ? normalized[0]!.toUpperCase() + normalized.slice(1) : normalized;
}

export function soulseekClientStatusLabel(status: SoulseekClientStatusDto, reachable = true): string {
  if (!reachable) return 'Unavailable';

  const flags = status.flags.filter((flag) => flag && flag !== 'None');
  const values = flags.length
    ? flags
    : status.state.split(',').map((part) => part.trim()).filter((part) => part && part !== 'None');

  return values.length ? values.map(humanizeStateValue).join(', ') : 'None';
}
