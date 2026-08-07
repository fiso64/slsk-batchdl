import type { PrototypeScenario, ScenarioId, StateSnapshotDto, TransferStateDto } from '../types';
import { busyTransfers, normalTransfers, stressTransfers } from '../fixtures/transfers';

function snapshot(sequence: number, transfers: TransferStateDto[]): StateSnapshotDto {
  return {
    scope: { kind: 'daemon' },
    position: {
      epoch: '00000000-0000-4000-8000-000000000001',
      sequence,
    },
    capturedAtUtc: '2026-08-07T08:15:00.000Z',
    daemon: null,
    workflows: [],
    jobs: [],
    searches: [],
    transfers,
    chatTarget: null,
  };
}

export const scenarios = [
  {
    id: 'normal',
    label: 'Normal',
    description: 'A small mix of active, queued, completed, and failed transfers.',
    connection: 'connected',
    soulseek: 'ready',
    snapshot: snapshot(42, normalTransfers),
  },
  {
    id: 'busy',
    label: 'Busy',
    description: 'Enough simultaneous activity to test information density.',
    connection: 'connected',
    soulseek: 'ready',
    snapshot: snapshot(128, busyTransfers),
  },
  {
    id: 'empty',
    label: 'Empty',
    description: 'A healthy daemon with no current transfers or other activity.',
    connection: 'connected',
    soulseek: 'ready',
    snapshot: snapshot(3, []),
  },
  {
    id: 'offline',
    label: 'Offline',
    description: 'The WebUI cannot currently reach the daemon.',
    connection: 'offline',
    soulseek: 'disconnected',
    snapshot: snapshot(0, []),
  },
  {
    id: 'stress',
    label: 'Stress',
    description: 'Many transfers, long paths, and long usernames for layout pressure.',
    connection: 'connected',
    soulseek: 'connecting',
    snapshot: snapshot(9001, stressTransfers),
  },
] as const satisfies readonly PrototypeScenario[];

export function getScenario(id: ScenarioId): PrototypeScenario {
  return scenarios.find((scenario) => scenario.id === id) ?? scenarios[0];
}
