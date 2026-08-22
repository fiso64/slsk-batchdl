import type { components } from '../api/generated';

export type StateSnapshotDto = components['schemas']['StateSnapshotDto'];
export type TransferStateDto = components['schemas']['TransferStateDto'];

export const scenarioIds = ['normal', 'busy', 'loading', 'empty', 'offline', 'stress'] as const;
export type ScenarioId = (typeof scenarioIds)[number];

export type PrototypeConnectionState = 'connected' | 'offline';
export type PrototypeSoulseekState = 'ready' | 'connecting' | 'disconnected';

export interface PrototypeScenario {
  id: ScenarioId;
  label: string;
  description: string;
  connection: PrototypeConnectionState;
  soulseek: PrototypeSoulseekState;
  snapshot: StateSnapshotDto;
}
