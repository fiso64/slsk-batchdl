import type { components } from '../api/generated';

export type StateSnapshotDto = components['schemas']['StateSnapshotDto'];
export type TransferStateDto = components['schemas']['TransferStateDto'];
export type SoulseekClientStatusDto = components['schemas']['SoulseekClientStatusDto'];

export const scenarioIds = ['normal', 'busy', 'loading', 'empty', 'offline', 'stress'] as const;
export type ScenarioId = (typeof scenarioIds)[number];

export type PrototypeConnectionState = 'connected' | 'offline';
export interface PrototypeScenario {
  id: ScenarioId;
  label: string;
  description: string;
  connection: PrototypeConnectionState;
  soulseekClient: SoulseekClientStatusDto;
  snapshot: StateSnapshotDto;
}
