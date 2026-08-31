import { createPrototypeSearchConditions, type PrototypeSearchConditions } from './search-config';
import {
  createPrototypeDownloadOptions,
  createPrototypeImportOptions,
  type PrototypeDownloadOptions,
  type PrototypeImportOptions,
} from './download-options';
import { emptyNewJobDraft, type NewJobDraft } from './job-preview';
import {
  effectiveJobSkipReason,
  effectiveJobStatus,
  presentationTarget,
  type AutomaticJobRecord,
  type AutomaticJobSkipReason,
  type AlbumJobRecord,
  type SongJobRecord,
} from './jobs';
import type { ScenarioId } from '../mock/types';

export type WishlistCadence = 'daily' | 'weekly' | 'monthly' | 'interval';
export type WishlistIntervalUnit = 'minutes' | 'hours';
export type WishlistRunStatus = 'complete' | 'running' | 'failed' | 'cancelled' | 'never';

export interface WishlistSchedule {
  enabled: boolean;
  cadence: WishlistCadence;
  time: string;
  weekday: string;
  monthDay: number;
  intervalValue: number;
  intervalUnit: WishlistIntervalUnit;
}

export interface WishlistDefaults {
  importOptions: PrototypeImportOptions;
  conditions: PrototypeSearchConditions;
  downloadOptions: PrototypeDownloadOptions;
}

export interface WishlistItemOverrides {
  importOptions?: PrototypeImportOptions;
  filtering?: PrototypeSearchConditions;
  ranking?: PrototypeSearchConditions;
  downloadOptions?: PrototypeDownloadOptions;
}

export interface WishlistItem {
  id: string;
  draft: NewJobDraft;
  overrides: WishlistItemOverrides;
  /** Latest runtime job produced for this saved item. */
  lastJobId?: string;
}

export interface WishlistRunStats {
  newCompleted: number;
  skipped: number;
  active: number;
  pending: number;
  failed: number;
  cancelled: number;
}

export interface WishlistRunSummary {
  status: WishlistRunStatus;
  when: string;
  runId?: string;
  workflowId?: string;
  stats: WishlistRunStats;
}

export interface WishlistRecord {
  id: string;
  name: string;
  schedule: WishlistSchedule;
  defaults: WishlistDefaults;
  items: WishlistItem[];
  lastRun: WishlistRunSummary;
  nextRun: string | null;
}

export interface WishlistRunStart {
  wishlist: WishlistRecord;
  jobs: AutomaticJobRecord[];
}

let wishlistSequence = 20;
let wishlistItemSequence = 200;
let wishlistRunSequence = 300;

/**
 * Prototype state is JSON-shaped, but Svelte may hand us reactive proxies.
 * JSON cloning deliberately unwraps those proxies before values cross the
 * reusable New Job / Wishlist model boundary.
 */
export function cloneValue<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

export function createWishlistDefaults(): WishlistDefaults {
  return {
    importOptions: createPrototypeImportOptions(),
    conditions: createPrototypeSearchConditions('track'),
    downloadOptions: createPrototypeDownloadOptions(),
  };
}

function emptyRun(): WishlistRunSummary {
  return {
    status: 'never',
    when: 'Never',
    stats: { newCompleted: 0, skipped: 0, active: 0, pending: 0, failed: 0, cancelled: 0 },
  };
}

export function createWishlist(name = 'New wishlist'): WishlistRecord {
  return {
    id: `wishlist-${wishlistSequence++}`,
    name,
    schedule: { enabled: true, cadence: 'daily', time: '03:00', weekday: 'Monday', monthDay: 1, intervalValue: 6, intervalUnit: 'hours' },
    defaults: createWishlistDefaults(),
    items: [],
    lastRun: emptyRun(),
    nextRun: 'Tomorrow · 03:00',
  };
}

export function createWishlistItem(
  draft: NewJobDraft,
  overrides: WishlistItemOverrides,
): WishlistItem {
  return {
    id: `wishlist-item-${wishlistItemSequence++}`,
    draft: cloneValue(draft),
    overrides: cloneValue(overrides),
  };
}

export function wishlistItemTitle(item: WishlistItem): string {
  const draft = item.draft;
  if (draft.choice === 'song') return draft.artist.trim() ? `${draft.artist.trim()} — ${draft.title.trim()}` : draft.title.trim();
  if (draft.choice === 'album') return draft.artist.trim() ? `${draft.artist.trim()} — ${draft.album.trim()}` : draft.album.trim();
  if (draft.choice === 'spotify') {
    if (draft.spotifyInput === 'likes') return 'Spotify liked songs';
    if (draft.spotifyInput === 'albums') return 'Spotify liked albums';
  }
  if (draft.choice === 'csv' || draft.choice === 'list') return draft.uploadedFileName || `${draft.choice.toUpperCase()} input`;
  return draft.source.trim() || `${wishlistItemTypeLabel(item)} input`;
}

export function wishlistItemTypeLabel(item: WishlistItem): string {
  const choice = item.draft.choice;
  const labels: Record<NewJobDraft['choice'], string> = {
    song: 'Song',
    album: 'Album',
    spotify: 'Spotify',
    youtube: 'YouTube',
    bandcamp: 'Bandcamp',
    musicbrainz: 'MusicBrainz',
    soulseek: 'Soulseek',
    csv: 'CSV file',
    list: 'List file',
  };
  return labels[choice];
}

export function wishlistItemOverrideLabels(item: WishlistItem): string[] {
  const labels: string[] = [];
  if (item.overrides.importOptions) labels.push('Import');
  if (item.overrides.filtering) labels.push('Filtering');
  if (item.overrides.ranking) labels.push('Ranking');
  if (item.overrides.downloadOptions) labels.push('Download');
  return labels;
}

export function wishlistScheduleLabel(schedule: WishlistSchedule): string {
  const label = schedule.cadence === 'daily'
    ? `Daily · ${schedule.time}`
    : schedule.cadence === 'weekly'
      ? `${schedule.weekday}s · ${schedule.time}`
      : schedule.cadence === 'monthly'
        ? `Monthly · day ${schedule.monthDay} · ${schedule.time}`
        : `Every ${schedule.intervalValue} ${schedule.intervalValue === 1 ? schedule.intervalUnit.slice(0, -1) : schedule.intervalUnit}`;
  return schedule.enabled ? label : `Paused · ${label}`;
}

export function wishlistNextRunLabel(schedule: WishlistSchedule): string {
  if (!schedule.enabled) return 'Paused';
  if (schedule.cadence === 'daily') return `Tomorrow · ${schedule.time}`;
  if (schedule.cadence === 'weekly') return `${schedule.weekday} · ${schedule.time}`;
  if (schedule.cadence === 'monthly') return `Day ${schedule.monthDay} · ${schedule.time}`;
  const unit = schedule.intervalValue === 1 ? schedule.intervalUnit.slice(0, -1) : schedule.intervalUnit;
  return `In ${schedule.intervalValue} ${unit}`;
}

export interface WishlistRunMetric {
  value?: number;
  label: string;
}

export function wishlistRunMetrics(wishlist: WishlistRecord, allJobs: AutomaticJobRecord[]): WishlistRunMetric[] {
  if (wishlist.lastRun.status === 'never') return [{ label: 'Not run yet' }];
  const runId = wishlist.lastRun.runId;
  const jobs = wishlist.items
    .map((item) => item.lastJobId ? allJobs.find((candidate) => candidate.id === item.lastJobId) ?? null : null)
    .filter((job): job is AutomaticJobRecord => Boolean(job) && (!runId || job!.wishlist?.runId === runId))
    .map((job) => presentationTarget(job, allJobs));

  if (!jobs.length) {
    const fallback = wishlist.lastRun.stats;
    const metrics: WishlistRunMetric[] = [];
    if (fallback.newCompleted) metrics.push({ value: fallback.newCompleted, label: 'new completed' });
    if (fallback.skipped) metrics.push({ value: fallback.skipped, label: 'skipped' });
    if (fallback.active) metrics.push({ value: fallback.active, label: 'downloading' });
    if (fallback.pending) metrics.push({ value: fallback.pending, label: 'pending' });
    if (fallback.failed) metrics.push({ value: fallback.failed, label: 'failed' });
    if (fallback.cancelled) metrics.push({ value: fallback.cancelled, label: 'cancelled' });
    return metrics.length ? metrics : [{ label: wishlist.lastRun.status === 'cancelled' ? 'Cancelled' : 'No item changes' }];
  }

  const counts = { completed: 0, exists: 0, notFound: 0, manual: 0, filtered: 0, skipped: 0, active: 0, pending: 0, failed: 0, cancelled: 0 };
  for (const job of jobs) {
    const status = effectiveJobStatus(job, allJobs);
    if (status === 'complete') counts.completed += 1;
    else if (status === 'running') counts.active += 1;
    else if (status === 'pending') counts.pending += 1;
    else if (status === 'failed') counts.failed += 1;
    else if (status === 'cancelled') counts.cancelled += 1;
    else if (status === 'skipped') {
      switch (effectiveJobSkipReason(job, allJobs)) {
        case 'AlreadyExists': counts.exists += 1; break;
        case 'NotFoundLastTime': counts.notFound += 1; break;
        case 'Manual': counts.manual += 1; break;
        case 'Filtered': counts.filtered += 1; break;
        default: counts.skipped += 1;
      }
    }
  }

  const metrics: WishlistRunMetric[] = [];
  if (counts.completed) metrics.push({ value: counts.completed, label: 'new completed' });
  if (counts.exists) metrics.push({ value: counts.exists, label: counts.exists === 1 ? 'exists' : 'exist' });
  if (counts.notFound) metrics.push({ value: counts.notFound, label: 'not found' });
  if (counts.manual) metrics.push({ value: counts.manual, label: 'skipped manually' });
  if (counts.filtered) metrics.push({ value: counts.filtered, label: 'filtered' });
  if (counts.skipped) metrics.push({ value: counts.skipped, label: 'skipped' });
  if (counts.active) metrics.push({ value: counts.active, label: 'downloading' });
  if (counts.pending) metrics.push({ value: counts.pending, label: 'pending' });
  if (counts.failed) metrics.push({ value: counts.failed, label: 'failed' });
  if (counts.cancelled) metrics.push({ value: counts.cancelled, label: 'cancelled' });
  return metrics.length ? metrics : [{ label: wishlist.lastRun.status === 'cancelled' ? 'Cancelled' : 'No item changes' }];
}

export function wishlistRunDetail(wishlist: WishlistRecord, allJobs: AutomaticJobRecord[]): string {
  return wishlistRunMetrics(wishlist, allJobs)
    .map((metric) => metric.value === undefined ? metric.label : `${metric.value} ${metric.label}`)
    .join(' · ');
}

function song(title: string, artist: string): NewJobDraft {
  return { ...cloneValue(emptyNewJobDraft), choice: 'song', title, artist };
}

function album(albumTitle: string, artist: string): NewJobDraft {
  return { ...cloneValue(emptyNewJobDraft), choice: 'album', album: albumTitle, artist };
}

function source(choice: Exclude<NewJobDraft['choice'], 'song' | 'album' | 'csv' | 'list'>, input: string): NewJobDraft {
  return { ...cloneValue(emptyNewJobDraft), choice, source: input };
}

function uploaded(choice: 'csv' | 'list', filename: string, fileType: string): NewJobDraft {
  return { ...cloneValue(emptyNewJobDraft), choice, uploadedFileName: filename, uploadedFileType: fileType };
}

const wishlistFixtureItems: Array<{ id: string; draft: NewJobDraft }> = [
  { id: 'wishlist-item-xtal', draft: song('Xtal', 'Aphex Twin') },
  { id: 'wishlist-item-geogaddi', draft: album('Geogaddi', 'Boards of Canada') },
  { id: 'wishlist-item-untrue', draft: album('Untrue', 'Burial') },
  { id: 'wishlist-item-gantz-graf', draft: album('Gantz Graf', 'Autechre') },
  { id: 'wishlist-item-substrata', draft: album('Substrata', 'Biosphere') },
  { id: 'wishlist-item-modal-soul', draft: album('Modal Soul', 'Nujabes') },
  { id: 'wishlist-item-cascade', draft: song('Cascade', 'Floating Points') },
  { id: 'wishlist-item-three', draft: album('Three', 'Four Tet') },
  { id: 'wishlist-item-mezzanine', draft: album('Mezzanine', 'Massive Attack') },
  { id: 'wishlist-item-inner-song', draft: album('Inner Song', 'Kelly Lee Owens') },
];

const mixedWishlistFixtureItems: Array<{ id: string; draft: NewJobDraft }> = [
  { id: 'wishlist-mixed-spotify', draft: source('spotify', 'https://open.spotify.com/playlist/discover-weekly') },
  { id: 'wishlist-mixed-youtube', draft: source('youtube', 'https://www.youtube.com/playlist?list=ambient-sessions') },
  { id: 'wishlist-mixed-bandcamp', draft: source('bandcamp', 'https://example.bandcamp.com/album/new-release') },
  { id: 'wishlist-mixed-musicbrainz', draft: source('musicbrainz', 'https://musicbrainz.org/collection/example') },
  { id: 'wishlist-mixed-csv', draft: uploaded('csv', 'library-import.csv', 'text/csv') },
  { id: 'wishlist-mixed-list', draft: uploaded('list', 'favorites.list', 'text/plain') },
];

const fixtureRunId = 'wishlist-run-current';
const fixtureWorkflowId = 'wishlist-workflow-current';
const fixtureRootJobId = 'wishlist-job-list-current';

export function createInitialWishlists(scenario: ScenarioId = 'normal'): WishlistRecord[] {
  if (scenario === 'empty') return [];

  const wishlist = createWishlist('Wishlist');
  wishlist.id = 'wishlist-main';
  wishlist.schedule = { enabled: true, cadence: 'daily', time: '04:15', weekday: 'Monday', monthDay: 1, intervalValue: 6, intervalUnit: 'hours' };
  wishlist.nextRun = 'Tomorrow · 04:15';
  wishlist.lastRun = {
    status: 'running',
    when: 'Now',
    runId: fixtureRunId,
    workflowId: fixtureWorkflowId,
    stats: { newCompleted: 2, skipped: 3, active: 2, pending: 3, failed: 0, cancelled: 0 },
  };
  wishlist.items = wishlistFixtureItems.map(({ id, draft }, index) => ({
    id,
    draft,
    overrides: index === 2
      ? { downloadOptions: { ...createPrototypeDownloadOptions(), outputParentDir: '/music/inbox' } }
      : {},
    lastJobId: `wishlist-runtime-item-${index + 1}`,
  }));

  const sources = createWishlist('Source roundup');
  sources.id = 'wishlist-source-roundup';
  sources.schedule = { enabled: true, cadence: 'interval', time: '00:00', weekday: 'Monday', monthDay: 1, intervalValue: 6, intervalUnit: 'hours' };
  sources.nextRun = 'In 6 hours';
  sources.lastRun = {
    status: 'complete',
    when: '18 hours ago',
    runId: 'wishlist-run-source-roundup',
    workflowId: 'wishlist-workflow-source-roundup',
    stats: { newCompleted: 4, skipped: 2, active: 0, pending: 0, failed: 0, cancelled: 0 },
  };
  sources.defaults.downloadOptions.writePlaylist = true;
  sources.items = mixedWishlistFixtureItems.map(({ id, draft }, index) => ({
    id,
    draft,
    overrides: index === 4
      ? { importOptions: { ...createPrototypeImportOptions(), upgradeToAlbum: true } }
      : {},
    lastJobId: `wishlist-source-runtime-${index + 1}`,
  }));

  if (scenario !== 'stress') return [wishlist, sources];

  const archive = createWishlist('Archive refresh');
  archive.id = 'wishlist-archive-refresh';
  archive.schedule = { enabled: false, cadence: 'monthly', time: '02:00', weekday: 'Monday', monthDay: 1, intervalValue: 12, intervalUnit: 'hours' };
  archive.nextRun = null;
  archive.items = Array.from({ length: 28 }, (_, index) => ({
    id: `wishlist-stress-${index}`,
    draft: index % 2 ? song(`Archive track ${index + 1}`, `Artist ${index + 1}`) : album(`Archive album ${index + 1}`, `Artist ${index + 1}`),
    overrides: {},
  }));
  return [wishlist, sources, archive];
}

function songRuntime(index: number, artist: string, title: string, status: SongJobRecord['status'], progress?: number): SongJobRecord {
  const complete = status === 'complete';
  const running = status === 'running';
  return {
    id: `wishlist-runtime-item-${index}`,
    workflowId: fixtureWorkflowId,
    parentJobId: fixtureRootJobId,
    kind: 'song',
    title: `${artist} — ${title}`,
    subtitle: `Wishlist · item ${index}`,
    status,
    ...(status === 'skipped' ? { skipReason: 'AlreadyExists' as const } : {}),
    createdAtUtc: new Date(Date.parse('2026-08-30T09:34:00Z') + index * 1000).toISOString(),
    when: 'Now',
    lifetime: complete || status === 'skipped' ? 'retained' : 'live',
    wishlist: { wishlistId: 'wishlist-main', runId: fixtureRunId, itemId: wishlistFixtureItems[index - 1]?.id },
    payload: {
      artist,
      title,
      candidateCount: status === 'pending' || status === 'skipped' ? 0 : 8 + index,
      ...(complete || running ? {
        resolved: {
          username: index % 2 ? 'nightshift' : 'cloudarchive',
          filename: `Wishlist/${artist}/${title}.flac`,
          sizeBytes: 36_000_000 + index * 1_700_000,
          audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 930, lengthSeconds: 220 + index * 10 },
          freeUploadSlot: true,
          uploadSpeedMbps: 9.4,
        },
        transfer: running
          ? { state: 'Downloading', tone: 'active', progressPercent: progress ?? 48, progressText: `${progress ?? 48}%`, speed: '4.6 MB/s', eta: '9s' }
          : { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' },
      } : {}),
    },
  };
}

function albumRuntime(index: number, artist: string, albumTitle: string, status: AlbumJobRecord['status'], progress?: number): AlbumJobRecord {
  const complete = status === 'complete';
  const running = status === 'running';
  const files = complete || running
    ? [
        { id: `wishlist-file-${index}-1`, relativePath: '01 - Track.flac', sizeBytes: 42_000_000, audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 940, lengthSeconds: 260 }, transfer: complete ? { state: 'Complete', tone: 'complete' as const, progressPercent: 100, progressText: 'Complete' } : { state: 'Downloading', tone: 'active' as const, progressPercent: progress ?? 42, progressText: `${progress ?? 42}%`, speed: '3.9 MB/s', eta: '18s' } },
        { id: `wishlist-file-${index}-2`, relativePath: '02 - Track.flac', sizeBytes: 39_000_000, audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 920, lengthSeconds: 244 }, transfer: complete ? { state: 'Complete', tone: 'complete' as const, progressPercent: 100, progressText: 'Complete' } : { state: 'Queued', tone: 'queued' as const, progressText: 'Queued' } },
      ]
    : [];
  return {
    id: `wishlist-runtime-item-${index}`,
    workflowId: fixtureWorkflowId,
    parentJobId: fixtureRootJobId,
    kind: 'album',
    title: `${artist} — ${albumTitle}`,
    subtitle: `Wishlist · item ${index}`,
    status,
    ...(status === 'skipped' ? { skipReason: 'AlreadyExists' as const } : {}),
    createdAtUtc: new Date(Date.parse('2026-08-30T09:34:00Z') + index * 1000).toISOString(),
    when: 'Now',
    lifetime: complete || status === 'skipped' ? 'retained' : 'live',
    ...(running ? { progress: { completed: 1, total: 8 } } : {}),
    wishlist: { wishlistId: 'wishlist-main', runId: fixtureRunId, itemId: wishlistFixtureItems[index - 1]?.id },
    payload: {
      artist,
      album: albumTitle,
      resultCount: status === 'pending' || status === 'skipped' ? 0 : 6,
      ...(complete || running ? { resolved: { username: index % 2 ? 'nightshift' : 'cloudarchive', folderPath: `Wishlist/${artist}/${albumTitle}` } } : {}),
      files,
      ...(complete ? { transfer: { state: 'Complete', tone: 'complete' as const, progressPercent: 100, progressText: 'Complete' } } : {}),
      ...(running ? { transfer: { state: 'Downloading', tone: 'active' as const, progressPercent: progress ?? 42, progressText: '1 of 8 files complete', speed: '3.9 MB/s', eta: '42s' } } : {}),
    },
  };
}


function createMixedWishlistJobs(): AutomaticJobRecord[] {
  const workflowId = 'wishlist-workflow-source-roundup';
  const runId = 'wishlist-run-source-roundup';
  const rootId = 'wishlist-source-root';
  const jobs: AutomaticJobRecord[] = [{
    id: rootId,
    workflowId,
    parentJobId: null,
    kind: 'job-list',
    title: 'Source roundup',
    subtitle: 'Wishlist run · 6 jobs',
    status: 'complete',
    createdAtUtc: '2026-08-29T15:15:00Z',
    when: '18 hours ago',
    lifetime: 'retained',
    progress: { completed: 6, total: 6 },
    wishlist: { wishlistId: 'wishlist-source-roundup', runId },
    payload: { name: 'Source roundup', childCount: 6, succeeded: 6, failed: 0 },
  }];

  const definitions: Array<{ sourceType: 'spotify' | 'youtube' | 'bandcamp' | 'musicbrainz' | 'csv' | 'list'; input: string; resultKind: 'job-list' | 'song' | 'album'; title: string; skipReason?: AutomaticJobSkipReason }> = [
    { sourceType: 'spotify', input: 'https://open.spotify.com/playlist/discover-weekly', resultKind: 'job-list', title: 'Discover Weekly' },
    { sourceType: 'youtube', input: 'https://www.youtube.com/playlist?list=ambient-sessions', resultKind: 'job-list', title: 'Ambient sessions', skipReason: 'NotFoundLastTime' },
    { sourceType: 'bandcamp', input: 'https://example.bandcamp.com/album/new-release', resultKind: 'album', title: 'Bandcamp release' },
    { sourceType: 'musicbrainz', input: 'https://musicbrainz.org/collection/example', resultKind: 'job-list', title: 'MusicBrainz collection' },
    { sourceType: 'csv', input: 'artifact:library-import.csv', resultKind: 'job-list', title: 'library-import.csv' },
    { sourceType: 'list', input: 'artifact:favorites.list', resultKind: 'job-list', title: 'favorites.list', skipReason: 'AlreadyExists' },
  ];

  definitions.forEach((definition, index) => {
    const itemNumber = index + 1;
    const extractId = `wishlist-source-runtime-${itemNumber}`;
    const resultId = `wishlist-source-result-${itemNumber}`;
    const itemId = mixedWishlistFixtureItems[index]!.id;
    jobs.push({
      id: extractId,
      workflowId,
      parentJobId: rootId,
      kind: 'extract',
      title: definition.title,
      subtitle: `Wishlist · ${definition.sourceType}`,
      status: 'complete',
      createdAtUtc: new Date(Date.parse('2026-08-29T15:15:00Z') + index * 2_000).toISOString(),
      when: '18 hours ago',
      lifetime: 'retained',
      wishlist: { wishlistId: 'wishlist-source-roundup', runId, itemId },
      payload: { sourceType: definition.sourceType, input: definition.input, resultJobId: resultId },
    });

    if (definition.resultKind === 'album') {
      jobs.push({
        id: resultId,
        workflowId,
        parentJobId: null,
        kind: 'album',
        title: 'Biosphere — Substrata',
        subtitle: 'Bandcamp result',
        status: definition.skipReason ? 'skipped' : 'complete',
        ...(definition.skipReason ? { skipReason: definition.skipReason } : {}),
        createdAtUtc: new Date(Date.parse('2026-08-29T15:15:01Z') + index * 2_000).toISOString(),
        when: '18 hours ago',
        lifetime: 'retained',
        wishlist: { wishlistId: 'wishlist-source-roundup', runId, itemId },
        payload: { artist: 'Biosphere', album: 'Substrata', resultCount: 6, files: [] },
      });
      return;
    }

    const childCount = definition.sourceType === 'csv' || definition.sourceType === 'list' ? 3 : 2;
    jobs.push({
      id: resultId,
      workflowId,
      parentJobId: null,
      kind: 'job-list',
      title: definition.title,
      subtitle: `${definition.sourceType.toUpperCase()} result · ${childCount} jobs`,
      status: definition.skipReason ? 'skipped' : 'complete',
        ...(definition.skipReason ? { skipReason: definition.skipReason } : {}),
      createdAtUtc: new Date(Date.parse('2026-08-29T15:15:01Z') + index * 2_000).toISOString(),
      when: '18 hours ago',
      lifetime: 'retained',
      progress: { completed: childCount, total: childCount },
      wishlist: { wishlistId: 'wishlist-source-roundup', runId, itemId },
      payload: { name: definition.title, childCount, succeeded: childCount, failed: 0 },
    });

    jobs.push({
      id: `${resultId}-song`, workflowId, parentJobId: resultId, kind: 'song',
      title: `${definition.sourceType === 'spotify' ? 'Floating Points — Birth4000' : 'Boards of Canada — Roygbiv'}`,
      subtitle: `${definition.sourceType} item 1`, status: 'complete', createdAtUtc: new Date(Date.parse('2026-08-29T15:15:02Z') + index * 2_000).toISOString(), when: '18 hours ago', lifetime: 'retained',
      wishlist: { wishlistId: 'wishlist-source-roundup', runId, itemId },
      payload: { artist: definition.sourceType === 'spotify' ? 'Floating Points' : 'Boards of Canada', title: definition.sourceType === 'spotify' ? 'Birth4000' : 'Roygbiv', candidateCount: 8 },
    });
    jobs.push({
      id: `${resultId}-album`, workflowId, parentJobId: resultId, kind: 'album',
      title: 'Autechre — Amber', subtitle: `${definition.sourceType} item 2`, status: 'complete', createdAtUtc: new Date(Date.parse('2026-08-29T15:15:03Z') + index * 2_000).toISOString(), when: '18 hours ago', lifetime: 'retained',
      wishlist: { wishlistId: 'wishlist-source-roundup', runId, itemId },
      payload: { artist: 'Autechre', album: 'Amber', resultCount: 5, files: [] },
    });
    if (childCount === 3) {
      jobs.push({
        id: `${resultId}-remote`, workflowId, parentJobId: resultId, kind: 'remote-directory',
        title: 'nightshift — Jazz/Casiopea/Mint Jams', subtitle: `${definition.sourceType} item 3`, status: 'complete', createdAtUtc: new Date(Date.parse('2026-08-29T15:15:04Z') + index * 2_000).toISOString(), when: '18 hours ago', lifetime: 'retained',
        wishlist: { wishlistId: 'wishlist-source-roundup', runId, itemId },
        payload: { username: 'nightshift', folderPath: 'Jazz/Casiopea/Mint Jams', files: [] },
      });
    }
  });

  return jobs;
}

export function createInitialWishlistJobs(scenario: ScenarioId = 'normal'): AutomaticJobRecord[] {
  if (scenario === 'empty') return [];
  const children: AutomaticJobRecord[] = [
    songRuntime(1, 'Aphex Twin', 'Xtal', 'complete'),
    albumRuntime(2, 'Boards of Canada', 'Geogaddi', 'skipped'),
    albumRuntime(3, 'Burial', 'Untrue', 'running', 46),
    albumRuntime(4, 'Autechre', 'Gantz Graf', 'pending'),
    albumRuntime(5, 'Biosphere', 'Substrata', 'complete'),
    albumRuntime(6, 'Nujabes', 'Modal Soul', 'skipped'),
    songRuntime(7, 'Floating Points', 'Cascade', 'running', 68),
    albumRuntime(8, 'Four Tet', 'Three', 'pending'),
    albumRuntime(9, 'Massive Attack', 'Mezzanine', 'skipped'),
    albumRuntime(10, 'Kelly Lee Owens', 'Inner Song', 'pending'),
  ];
  const root: AutomaticJobRecord = {
    id: fixtureRootJobId,
    workflowId: fixtureWorkflowId,
    parentJobId: null,
    kind: 'job-list',
    title: 'Wishlist',
    subtitle: 'Wishlist run · 10 jobs',
    status: 'running',
    createdAtUtc: '2026-08-30T09:34:00Z',
    when: 'Now',
    lifetime: 'live',
    progress: { completed: 5, total: 10 },
    wishlist: { wishlistId: 'wishlist-main', runId: fixtureRunId },
    payload: { name: 'Wishlist', childCount: 10, succeeded: 2, failed: 0 },
  };
  return [root, ...children, ...createMixedWishlistJobs()];
}

function runtimeJobForItem(item: WishlistItem, index: number, workflowId: string, runId: string, rootId: string): AutomaticJobRecord {
  const common = {
    id: `wishlist-run-${wishlistRunSequence}-${index}`,
    workflowId,
    parentJobId: rootId,
    title: wishlistItemTitle(item),
    subtitle: `Wishlist · item ${index + 1}`,
    status: 'pending' as const,
    createdAtUtc: new Date(Date.now() + index).toISOString(),
    when: 'Just now',
    lifetime: 'live' as const,
    wishlist: { wishlistId: '', runId, itemId: item.id },
  };
  if (item.draft.choice === 'song') {
    return { ...common, kind: 'song', payload: { artist: item.draft.artist, title: item.draft.title, candidateCount: 0 } };
  }
  if (item.draft.choice === 'album') {
    return { ...common, kind: 'album', payload: { artist: item.draft.artist, album: item.draft.album, resultCount: 0, files: [] } };
  }
  const sourceType = item.draft.choice === 'csv' || item.draft.choice === 'list' ? item.draft.choice : item.draft.choice;
  return {
    ...common,
    kind: 'extract',
    payload: {
      sourceType,
      input: item.draft.choice === 'csv' || item.draft.choice === 'list' ? item.draft.uploadedFileName : item.draft.source,
      resultJobId: null,
    },
  };
}

export function runWishlistNow(record: WishlistRecord): WishlistRunStart {
  const sequence = wishlistRunSequence++;
  const runId = `wishlist-run-${sequence}`;
  const workflowId = `wishlist-workflow-${sequence}`;
  const rootId = `wishlist-run-root-${sequence}`;
  const childJobs = record.items.map((item, index) => {
    const job = runtimeJobForItem(item, index, workflowId, runId, rootId);
    job.wishlist = { wishlistId: record.id, runId, itemId: item.id };
    return job;
  });
  const root: AutomaticJobRecord = {
    id: rootId,
    workflowId,
    parentJobId: null,
    kind: 'job-list',
    title: record.name,
    subtitle: `Wishlist run · ${record.items.length} jobs`,
    status: 'running',
    createdAtUtc: new Date().toISOString(),
    when: 'Just now',
    lifetime: 'live',
    progress: { completed: 0, total: record.items.length },
    wishlist: { wishlistId: record.id, runId },
    payload: { name: record.name, childCount: record.items.length, succeeded: 0, failed: 0 },
  };
  return {
    wishlist: {
      ...record,
      lastRun: {
        status: 'running',
        when: 'Just now',
        runId,
        workflowId,
        stats: { newCompleted: 0, skipped: 0, active: 0, pending: record.items.length, failed: 0, cancelled: 0 },
      },
      items: record.items.map((item, index) => ({ ...item, lastJobId: childJobs[index]?.id })),
    },
    jobs: [root, ...childJobs],
  };
}
