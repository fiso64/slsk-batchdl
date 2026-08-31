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
    const detail = resource === 'downloads' ? 'Current download state cannot be loaded while the daemon is offline.'
      : resource === 'uploads' ? 'Current upload state cannot be loaded while the daemon is offline.'
        : resource === 'profile' ? 'Profile data cannot be loaded while the daemon is offline.'
          : resource === 'shares' ? 'Shared files cannot be loaded while the daemon is offline.'
            : 'This resource cannot be loaded while the daemon is offline.';
    return { phase: 'offline', title: 'Daemon unavailable', detail, blocking: true };
  }


  if (scenario === 'loading') {
    if (resource === 'search-list' || resource === 'chat') return { phase: 'ready' };
    if (resource === 'search-results') return { phase: 'loading', title: 'Waiting for results' };
    if (resource === 'downloads') return { phase: 'loading', title: 'Loading downloads', detail: 'Fetching current transfers…', blocking: true };
    if (resource === 'uploads') return { phase: 'loading', title: 'Loading uploads', detail: 'Fetching current transfers…', blocking: true };
    if (resource === 'profile') return { phase: 'loading', title: 'Loading profile', detail: 'Fetching profile information…', blocking: true };
    if (resource === 'shares') return { phase: 'loading', title: 'Loading shares', detail: 'Fetching shared folders and files…', blocking: true };
    return { phase: 'loading', title: 'Loading' };
  }

  if (scenario === 'empty') {
    if (resource === 'search-list') return { phase: 'empty', title: 'No jobs yet', detail: 'New searches and automatic jobs will appear here.', blocking: true };
    if (resource === 'downloads') return { phase: 'empty', title: 'No downloads', detail: 'Downloaded files and folders will appear here in creation order.', blocking: true };
    if (resource === 'uploads') return { phase: 'empty', title: 'No uploads', detail: 'Uploads will appear here as peers request shared files.', blocking: true };
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
