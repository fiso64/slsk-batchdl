import type { ScenarioId } from '../mock/types';

export type PrototypeResourcePhase =
  | 'ready'
  | 'loading'
  | 'empty'
  | 'unavailable'
  | 'expired'
  | 'offline'
  | 'degraded'
  | 'pruned'
  | 'interrupted';

export interface PrototypeResourceState {
  phase: PrototypeResourcePhase;
  title?: string;
  detail?: string;
  blocking?: boolean;
}

export type PrototypeResourceKind = 'dashboard' | 'search-list' | 'search-results' | 'downloads' | 'uploads' | 'profile' | 'shares' | 'chat';

export function resourceStateForScenario(scenario: ScenarioId, resource: PrototypeResourceKind): PrototypeResourceState {
  if (scenario === 'offline') {
    return { phase: 'offline', title: 'Daemon unavailable', blocking: true };
  }

  if (scenario === 'empty') {
    if (resource === 'search-list') return { phase: 'empty', title: 'No searches', blocking: true };
    if (resource === 'downloads') return { phase: 'empty', title: 'No downloads', blocking: true };
    if (resource === 'uploads') return { phase: 'empty', title: 'No uploads', blocking: true };
  }

  if (scenario === 'busy') {
    if (resource === 'search-results') return { phase: 'ready' };
    if (resource === 'shares') return { phase: 'loading', title: 'Loading shares' };
    if (resource === 'profile') return { phase: 'degraded', title: 'Profile partially available' };
  }

  if (scenario === 'stress') {
    if (resource === 'dashboard') return { phase: 'degraded', title: 'Partial history' };
    if (resource === 'search-results') return { phase: 'ready' };
    if (resource === 'downloads' || resource === 'uploads') return { phase: 'ready' };
    if (resource === 'profile') return { phase: 'degraded', title: 'Some profile fields unavailable' };
    if (resource === 'shares') return { phase: 'expired', title: 'Shares expired', blocking: true };
    if (resource === 'chat') return { phase: 'degraded', title: 'Chat degraded' };
  }

  return { phase: 'ready' };
}
