import type { PrototypeScenario, ScenarioId, SoulseekClientStatusDto, StateSnapshotDto, TransferStateDto } from '../types';
import { busyTransfers, normalTransfers, stressTransfers } from '../fixtures/transfers';

function soulseekClient(flags: string[], isReady: boolean): SoulseekClientStatusDto {
  return {
    state: flags.length ? flags.join(', ') : 'None',
    flags,
    isReady,
  };
}

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
    soulseekClient: soulseekClient(['Connected', 'LoggedIn'], true),
    snapshot: snapshot(42, normalTransfers),
  },
  {
    id: 'busy',
    label: 'Busy',
    description: 'Enough simultaneous activity to test information density.',
    connection: 'connected',
    soulseekClient: soulseekClient(['Connected', 'LoggedIn'], true),
    snapshot: snapshot(128, busyTransfers),
  },
  {
    id: 'loading',
    label: 'Loading',
    description: 'Requests are in flight so loading and no-results-yet states can be inspected.',
    connection: 'connected',
    soulseekClient: soulseekClient(['Connected', 'LoggedIn'], true),
    snapshot: snapshot(64, []),
  },
  {
    id: 'empty',
    label: 'Empty',
    description: 'A healthy daemon with no current transfers or other activity.',
    connection: 'connected',
    soulseekClient: soulseekClient(['Connected', 'LoggedIn'], true),
    snapshot: snapshot(3, []),
  },
  {
    id: 'offline',
    label: 'Offline',
    description: 'The WebUI cannot currently reach the daemon.',
    connection: 'offline',
    soulseekClient: soulseekClient([], false),
    snapshot: snapshot(0, []),
  },
  {
    id: 'stress',
    label: 'Stress',
    description: 'Many transfers, long paths, and long usernames for layout pressure.',
    connection: 'connected',
    soulseekClient: soulseekClient(['Connecting'], false),
    snapshot: snapshot(9001, stressTransfers),
  },
] as const satisfies readonly PrototypeScenario[];

export function getScenario(id: ScenarioId): PrototypeScenario {
  return scenarios.find((scenario) => scenario.id === id) ?? scenarios[0];
}
