import type { PrototypeDataLifetime } from './state';
import type { AudioAttributes, FolderItemFile, TransferPresentation } from './items';
import { prototypeUuid } from './ids';
import type { AppIconName } from './icons';

import type { AutomaticJobKind, AutomaticJobSkipReason, AutomaticJobStatus, ExtractSourceType } from './job-types';
export type { AutomaticJobKind, AutomaticJobSkipReason, AutomaticJobStatus, ExtractSourceType } from './job-types';

interface JobBase {
  id: string;
  workflowId: string;
  parentJobId: string | null;
  kind: AutomaticJobKind;
  title: string;
  subtitle?: string;
  status: AutomaticJobStatus;
  /** Daemon-provided reason when status is skipped. */
  skipReason?: AutomaticJobSkipReason;
  createdAtUtc: string;
  when: string;
  lifetime: PrototypeDataLifetime;
  progress?: { completed: number; total: number };
}

export interface SongJobRecord extends JobBase {
  kind: 'song';
  payload: {
    artist: string;
    title: string;
    album?: string;
    candidateCount: number;
    resolved?: {
      username: string;
      filename: string;
      sizeBytes: number;
      audio?: AudioAttributes;
      freeUploadSlot: boolean;
      uploadSpeedMbps: number;
    };
    transfer?: TransferPresentation;
  };
}

export interface AlbumJobRecord extends JobBase {
  kind: 'album';
  payload: {
    artist: string;
    album: string;
    resultCount: number;
    resolved?: { username: string; folderPath: string };
    files: FolderItemFile[];
    transfer?: TransferPresentation;
  };
}

export interface AggregateJobRecord extends JobBase {
  kind: 'aggregate';
  payload: { artist?: string; title?: string; songCount: number; succeeded: number; failed: number };
}

export interface AlbumAggregateJobRecord extends JobBase {
  kind: 'album-aggregate';
  payload: { artist?: string; album?: string; albumCount: number; succeeded: number; failed: number };
}

export interface ExtractJobRecord extends JobBase {
  kind: 'extract';
  payload: {
    sourceType: ExtractSourceType;
    input: string;
    resultJobId: string | null;
  };
}

export interface JobListRecord extends JobBase {
  kind: 'job-list';
  payload: { name: string; childCount: number; succeeded: number; failed: number };
}

export interface RemoteFileJobRecord extends JobBase {
  kind: 'remote-file';
  payload: {
    username: string;
    path: string;
    sizeBytes: number;
    audio?: AudioAttributes;
    transfer?: TransferPresentation;
  };
}

export interface RemoteDirectoryJobRecord extends JobBase {
  kind: 'remote-directory';
  payload: {
    username: string;
    folderPath: string;
    files: FolderItemFile[];
    transfer?: TransferPresentation;
  };
}

export interface RetrieveFolderJobRecord extends JobBase {
  kind: 'retrieve-folder';
  payload: { username: string; folderPath: string; newFilesFoundCount: number; outcome: 'retrieving' | 'complete' | 'failed' | 'cancelled' };
}

export interface GenericJobRecord extends JobBase {
  kind: 'generic';
  payload: { text: string };
}

export type AutomaticJobRecord =
  | SongJobRecord
  | AlbumJobRecord
  | AggregateJobRecord
  | AlbumAggregateJobRecord
  | ExtractJobRecord
  | JobListRecord
  | RemoteFileJobRecord
  | RemoteDirectoryJobRecord
  | RetrieveFolderJobRecord
  | GenericJobRecord;

const baseTime = Date.parse('2026-08-07T08:40:00Z');
const jobId = (index: number) => prototypeUuid(0x51000000, index);
const workflowId = (index: number) => prototypeUuid(0x51010000, index);

function file(id: string, relativePath: string, sizeBytes: number, lengthSeconds: number, transfer?: TransferPresentation): FolderItemFile {
  return {
    id,
    relativePath,
    sizeBytes,
    audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 930, lengthSeconds },
    ...(transfer ? { transfer } : {}),
  };
}

export function createInitialAutomaticJobs(scenario: 'normal' | 'busy' | 'loading' | 'empty' | 'offline' | 'stress' = 'normal'): AutomaticJobRecord[] {
  if (scenario === 'empty') return [];

  const spotifyExtractId = jobId(1);
  const spotifyListId = jobId(2);
  const csvExtractId = jobId(10);
  const csvListId = jobId(11);
  const albumId = jobId(20);

  const extraSpotifySeed: Array<[string, string]> = [
    ['Four Tet', 'Loved'],
    ['Actress', 'Push Power (a 1)'],
    ['Oneohtrix Point Never', 'A Barely Lit Path'],
    ['Skee Mask', 'Hedwig Transformation Group'],
    ['Nia Archives', 'Crowded Roomz'],
    ['Kelela', 'Contact'],
    ['Overmono', 'Good Lies'],
    ['Caribou', 'Honey'],
    ['Kelly Moran', 'Sodalis'],
  ];
  const extraSpotifySongs: SongJobRecord[] = extraSpotifySeed.map(([artist, title], index) => ({
    id: jobId(50 + index),
    workflowId: workflowId(1),
    parentJobId: spotifyListId,
    kind: 'song' as const,
    title: `${artist} — ${title}`,
    subtitle: `Spotify · track ${index + 4}`,
    status: index < 4 ? 'complete' as const : 'pending' as const,
    createdAtUtc: new Date(baseTime - 2 * 60_000 + (index + 4) * 1_000).toISOString(),
    when: '2 min ago',
    lifetime: index < 4 ? 'retained' as const : 'live' as const,
    payload: index < 4
      ? {
          artist,
          title,
          candidateCount: 7 + index,
          resolved: {
            username: index % 2 ? 'cloudarchive' : 'nightshift',
            filename: `Electronic/${artist}/${title}.flac`,
            sizeBytes: 34_000_000 + index * 2_900_000,
            audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 930 + index * 18, lengthSeconds: 220 + index * 17 },
            freeUploadSlot: index % 2 === 0,
            uploadSpeedMbps: index % 2 ? 6.1 : 12.8,
          },
          transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' },
        }
      : { artist, title, candidateCount: 0 },
  }));

  const records: AutomaticJobRecord[] = [
    {
      id: spotifyExtractId,
      workflowId: workflowId(1),
      parentJobId: null,
      kind: 'extract',
      title: 'Discover Weekly',
      subtitle: 'Spotify playlist',
      status: 'complete',
      createdAtUtc: new Date(baseTime - 3 * 60_000).toISOString(),
      when: '3 min ago',
      lifetime: 'retained',
      payload: {
        sourceType: 'spotify',
        input: 'https://open.spotify.com/playlist/37i9dQZEVXcExample',
        resultJobId: spotifyListId,
      },
    },
    {
      id: spotifyListId,
      workflowId: workflowId(1),
      parentJobId: null,
      kind: 'job-list',
      title: 'Discover Weekly',
      subtitle: 'Spotify import · 12 songs',
      status: 'running',
      createdAtUtc: new Date(baseTime - 3 * 60_000 + 1_000).toISOString(),
      when: '3 min ago',
      lifetime: 'live',
      progress: { completed: 7, total: 12 },
      payload: { name: 'Discover Weekly', childCount: 12, succeeded: 7, failed: 1 },
    },
    {
      id: jobId(3), workflowId: workflowId(1), parentJobId: spotifyListId, kind: 'song',
      title: 'Floating Points — Birth4000', subtitle: 'Spotify · track 1', status: 'complete', createdAtUtc: new Date(baseTime - 2 * 60_000).toISOString(), when: '2 min ago', lifetime: 'retained',
      payload: { artist: 'Floating Points', title: 'Birth4000', candidateCount: 14, resolved: { username: 'nightshift', filename: 'Electronic/Floating Points/Birth4000.flac', sizeBytes: 44_200_000, audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 1012, lengthSeconds: 288 }, freeUploadSlot: true, uploadSpeedMbps: 12.8 }, transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' } },
    },
    {
      id: jobId(4), workflowId: workflowId(1), parentJobId: spotifyListId, kind: 'song',
      title: 'Kelly Lee Owens — Corner of My Sky', subtitle: 'Spotify · track 2', status: 'running', createdAtUtc: new Date(baseTime - 2 * 60_000 + 2_000).toISOString(), when: '2 min ago', lifetime: 'live',
      payload: { artist: 'Kelly Lee Owens', title: 'Corner of My Sky', candidateCount: 9, resolved: { username: 'cassetteculture', filename: 'Electronic/Kelly Lee Owens/Inner Song/08 - Corner of My Sky.flac', sizeBytes: 39_600_000, audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 941, lengthSeconds: 247 }, freeUploadSlot: false, uploadSpeedMbps: 8.4 }, transfer: { state: 'Downloading', tone: 'active', progressPercent: 62, progressText: '62%', speed: '4.8 MB/s', eta: '6s' } },
    },
    {
      id: jobId(5), workflowId: workflowId(1), parentJobId: spotifyListId, kind: 'song',
      title: 'Burial — Phoneglow', subtitle: 'Spotify · track 3', status: 'failed', createdAtUtc: new Date(baseTime - 2 * 60_000 + 3_000).toISOString(), when: '2 min ago', lifetime: 'retained',
      payload: { artist: 'Burial', title: 'Phoneglow', candidateCount: 0 },
    },
    ...extraSpotifySongs,
    {
      id: albumId,
      workflowId: workflowId(20),
      parentJobId: null,
      kind: 'album',
      title: 'Nujabes — Modal Soul',
      subtitle: 'Automatic album download',
      status: 'running',
      createdAtUtc: new Date(baseTime - 9 * 60_000).toISOString(),
      when: '9 min ago',
      lifetime: 'live',
      progress: { completed: 3, total: 5 },
      payload: {
        artist: 'Nujabes', album: 'Modal Soul', resultCount: 7,
        resolved: { username: 'cloudarchive', folderPath: 'Hip-Hop/Nujabes/2005 - Modal Soul' },
        files: [
          file('modal-1', '01 - Feather.flac', 41_200_000, 175, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('modal-2', '02 - Ordinary Joe.flac', 48_400_000, 313, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('modal-3', '03 - Reflection Eternal.flac', 44_100_000, 257, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('modal-4', '04 - Luv (sic.) pt3.flac', 51_900_000, 337, { state: 'Downloading', tone: 'active', progressPercent: 46, progressText: '46%', speed: '6.1 MB/s', eta: '11s' }),
          { id: 'modal-cover', relativePath: 'cover.jpg', sizeBytes: 1_800_000, transfer: { state: 'Queued', tone: 'queued', progressText: 'Queued' } },
        ],
        transfer: { state: 'Downloading', tone: 'active', progressPercent: 68, progressText: '3 of 5 files complete', speed: '6.1 MB/s', eta: '22s' },
      },
    },
    {
      id: jobId(21), workflowId: workflowId(20), parentJobId: albumId, kind: 'retrieve-folder',
      title: 'Retrieve Modal Soul', subtitle: 'cloudarchive', status: 'complete', createdAtUtc: new Date(baseTime - 9 * 60_000 + 1_000).toISOString(), when: '9 min ago', lifetime: 'retained',
      payload: { username: 'cloudarchive', folderPath: 'Hip-Hop/Nujabes/2005 - Modal Soul', newFilesFoundCount: 5, outcome: 'complete' },
    },
    {
      id: csvExtractId,
      workflowId: workflowId(10), parentJobId: null, kind: 'extract',
      title: 'library-import.csv', subtitle: 'CSV import', status: 'complete', createdAtUtc: new Date(baseTime - 41 * 60_000).toISOString(), when: '41 min ago', lifetime: 'retained',
      payload: { sourceType: 'csv', input: 'artifact:library-import.csv', resultJobId: csvListId },
    },
    {
      id: csvListId,
      workflowId: workflowId(10), parentJobId: null, kind: 'job-list',
      title: 'library-import.csv', subtitle: 'CSV import · 4 jobs', status: 'complete', createdAtUtc: new Date(baseTime - 41 * 60_000 + 1_000).toISOString(), when: '41 min ago', lifetime: 'retained', progress: { completed: 4, total: 4 },
      payload: { name: 'library-import', childCount: 4, succeeded: 4, failed: 0 },
    },
    {
      id: jobId(12), workflowId: workflowId(10), parentJobId: csvListId, kind: 'song',
      title: 'Boards of Canada — Roygbiv', subtitle: 'CSV row 2', status: 'complete', createdAtUtc: new Date(baseTime - 40 * 60_000).toISOString(), when: '40 min ago', lifetime: 'retained',
      payload: {
        artist: 'Boards of Canada', title: 'Roygbiv', candidateCount: 16,
        resolved: {
          username: 'cloudarchive',
          filename: 'Electronic/Boards of Canada/Music Has the Right to Children/10 - Roygbiv.flac',
          sizeBytes: 36_800_000,
          audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 948, lengthSeconds: 147 },
          freeUploadSlot: true,
          uploadSpeedMbps: 6.1,
        },
        transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' },
      },
    },
    {
      id: jobId(13), workflowId: workflowId(10), parentJobId: csvListId, kind: 'album',
      title: 'Autechre — Amber', subtitle: 'CSV row 3', status: 'complete', createdAtUtc: new Date(baseTime - 40 * 60_000 + 1_000).toISOString(), when: '40 min ago', lifetime: 'retained',
      payload: {
        artist: 'Autechre', album: 'Amber', resultCount: 5,
        resolved: { username: 'nightshift', folderPath: 'Electronic/Autechre/1994 - Amber' },
        files: [
          file('amber-1', '01 - Foil.flac', 41_700_000, 383, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-2', '02 - Montreal.flac', 47_900_000, 455, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-3', '03 - Silverside.flac', 11_300_000, 117, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-4', '04 - Slip.flac', 39_800_000, 381, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-5', '05 - Glitch.flac', 44_600_000, 391, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-6', '06 - Piezo.flac', 52_100_000, 493, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-7', '07 - Nine.flac', 30_500_000, 236, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-8', '08 - Further.flac', 64_400_000, 622, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-9', '09 - Yulquen.flac', 39_100_000, 397, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-10', '10 - Nil.flac', 43_300_000, 438, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('amber-11', '11 - Teartear.flac', 69_200_000, 485, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          { id: 'amber-cover', relativePath: 'cover.jpg', sizeBytes: 1_400_000, transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' } },
        ],
        transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: '12 files complete' },
      },
    },
    {
      id: jobId(14), workflowId: workflowId(10), parentJobId: csvListId, kind: 'remote-file',
      title: 'liner-notes.pdf', subtitle: 'Soulseek link', status: 'complete', createdAtUtc: new Date(baseTime - 39 * 60_000).toISOString(), when: '39 min ago', lifetime: 'retained',
      payload: { username: 'archive_bot', path: 'Docs/liner-notes.pdf', sizeBytes: 3_400_000, transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' } },
    },
    {
      id: jobId(15), workflowId: workflowId(10), parentJobId: csvListId, kind: 'remote-directory',
      title: 'Artwork pack', subtitle: 'Soulseek directory', status: 'complete', createdAtUtc: new Date(baseTime - 39 * 60_000 + 1_000).toISOString(), when: '39 min ago', lifetime: 'retained',
      payload: { username: 'archive_bot', folderPath: 'Artwork/Head Hunters', files: [{ id: 'art-1', relativePath: 'front.jpg', sizeBytes: 2_200_000 }, { id: 'art-2', relativePath: 'back.jpg', sizeBytes: 1_900_000 }], transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: '2 files complete' } },
    },

    {
      id: jobId(80), workflowId: workflowId(80), parentJobId: null, kind: 'song',
      title: 'Massive Attack — Teardrop', subtitle: 'Automatic song download', status: 'complete', createdAtUtc: new Date(baseTime - 60 * 60_000).toISOString(), when: '1 h ago', lifetime: 'retained',
      payload: {
        artist: 'Massive Attack', title: 'Teardrop', album: 'Mezzanine', candidateCount: 11,
        resolved: {
          username: 'nightshift',
          filename: 'Trip-Hop/Massive Attack/Mezzanine/03 - Teardrop.flac',
          sizeBytes: 42_700_000,
          audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 952, lengthSeconds: 330 },
          freeUploadSlot: true,
          uploadSpeedMbps: 12.8,
        },
        transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' },
      },
    },
    {
      id: jobId(70), workflowId: workflowId(70), parentJobId: null, kind: 'extract',
      title: 'favorites.list', subtitle: 'List import', status: 'complete', createdAtUtc: new Date(baseTime - 52 * 60_000).toISOString(), when: '52 min ago', lifetime: 'retained',
      payload: { sourceType: 'list', input: 'artifact:favorites.list', resultJobId: jobId(71) },
    },
    {
      id: jobId(71), workflowId: workflowId(70), parentJobId: null, kind: 'job-list',
      title: 'favorites.list', subtitle: 'List import · 3 jobs', status: 'running', createdAtUtc: new Date(baseTime - 52 * 60_000 + 1_000).toISOString(), when: '52 min ago', lifetime: 'live', progress: { completed: 1, total: 3 },
      payload: { name: 'favorites.list', childCount: 3, succeeded: 1, failed: 0 },
    },
    {
      id: jobId(72), workflowId: workflowId(70), parentJobId: jobId(71), kind: 'song',
      title: 'Burial — Archangel', subtitle: 'List item 1', status: 'complete', createdAtUtc: new Date(baseTime - 51 * 60_000).toISOString(), when: '51 min ago', lifetime: 'retained',
      payload: {
        artist: 'Burial', title: 'Archangel', candidateCount: 12,
        resolved: {
          username: 'cassetteculture',
          filename: 'Electronic/Burial/Untrue/02 - Archangel.flac',
          sizeBytes: 40_200_000,
          audio: { bitDepth: 16, sampleRateHz: 44_100, bitrateKbps: 927, lengthSeconds: 240 },
          freeUploadSlot: false,
          uploadSpeedMbps: 8.4,
        },
        transfer: { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' },
      },
    },
    {
      id: jobId(73), workflowId: workflowId(70), parentJobId: jobId(71), kind: 'album',
      title: 'Biosphere — Substrata', subtitle: 'List item 2', status: 'running', createdAtUtc: new Date(baseTime - 51 * 60_000 + 1_000).toISOString(), when: '51 min ago', lifetime: 'live', progress: { completed: 3, total: 9 },
      payload: {
        artist: 'Biosphere', album: 'Substrata', resultCount: 8,
        resolved: { username: 'cloudarchive', folderPath: 'Electronic/Biosphere/1997 - Substrata' },
        files: [
          file('substrata-1', '01 - As the Sun Kissed the Horizon.flac', 14_600_000, 107, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('substrata-2', '02 - Poa Alpina.flac', 31_900_000, 251, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('substrata-3', '03 - Chukhung.flac', 49_500_000, 426, { state: 'Complete', tone: 'complete', progressPercent: 100, progressText: 'Complete' }),
          file('substrata-4', '04 - The Things I Tell You.flac', 39_100_000, 354, { state: 'Downloading', tone: 'active', progressPercent: 38, progressText: '38%', speed: '3.7 MB/s', eta: '19s' }),
          file('substrata-5', '05 - Times When I Know You Will Be Sad.flac', 24_800_000, 216, { state: 'Queued', tone: 'queued', progressText: 'Queued' }),
          file('substrata-6', '06 - Hyperborea.flac', 42_300_000, 345, { state: 'Queued', tone: 'queued', progressText: 'Queued' }),
          file('substrata-7', '07 - Kobresia.flac', 49_700_000, 427, { state: 'Queued', tone: 'queued', progressText: 'Queued' }),
          file('substrata-8', '08 - Antennaria.flac', 35_600_000, 306, { state: 'Queued', tone: 'queued', progressText: 'Queued' }),
          { id: 'substrata-cover', relativePath: 'cover.jpg', sizeBytes: 1_100_000, transfer: { state: 'Queued', tone: 'queued', progressText: 'Queued' } },
        ],
        transfer: { state: 'Downloading', tone: 'active', progressPercent: 34, progressText: '3 of 9 files complete', speed: '3.7 MB/s', eta: '38s' },
      },
    },
    {
      id: jobId(74), workflowId: workflowId(70), parentJobId: jobId(71), kind: 'remote-directory',
      title: 'nightshift — Jazz/Casiopea/Mint Jams', subtitle: 'List item 3', status: 'pending', createdAtUtc: new Date(baseTime - 51 * 60_000 + 2_000).toISOString(), when: '51 min ago', lifetime: 'live',
      payload: { username: 'nightshift', folderPath: 'Jazz/Casiopea/Mint Jams', files: [] },
    },
  ];

  if (scenario === 'loading') return records.slice(0, 1).map((record) => ({ ...record, status: 'pending' as const, lifetime: 'live' as const }));
  if (scenario === 'busy') return records.map((record, index) => index < 4 ? { ...record, status: index === 0 ? 'running' as const : record.status } : record);
  if (scenario === 'stress') {
    return records.map((record, index) => index === 0 ? { ...record, title: 'A very long imported Spotify playlist whose display name keeps going well beyond the comfortable width of an ordinary jobs row', status: 'running' as const } : record);
  }
  return records;
}

export function jobKindLabel(kind: AutomaticJobKind): string {
  switch (kind) {
    case 'song': return 'Song';
    case 'album': return 'Album';
    case 'aggregate': return 'Song Aggregate';
    case 'album-aggregate': return 'Album Aggregate';
    case 'extract': return 'Import';
    case 'job-list': return 'Job List';
    case 'remote-file': return 'Remote File';
    case 'remote-directory': return 'Remote Directory';
    case 'retrieve-folder': return 'Retrieve Folder';
    case 'generic': return 'Generic';
  }
}


export function extractSourceLabel(sourceType: ExtractSourceType): string {
  switch (sourceType) {
    case 'spotify': return 'Spotify';
    case 'youtube': return 'YouTube';
    case 'bandcamp': return 'Bandcamp';
    case 'musicbrainz': return 'MusicBrainz';
    case 'soulseek': return 'Soulseek';
    case 'csv': return 'CSV';
    case 'list': return 'List';
    case 'string': return 'Text';
  }
}

export function isAutomaticJobActive(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[] = [job]): boolean {
  const status = effectiveJobStatus(job, allJobs);
  return status === 'pending' || status === 'running';
}

export function jobKindIcon(kind: AutomaticJobKind): AppIconName {
  switch (kind) {
    case 'song': return 'track';
    case 'album': return 'album';
    case 'aggregate': return 'aggregate-job';
    case 'album-aggregate': return 'album-aggregate-job';
    case 'extract': return 'extract';
    case 'job-list': return 'job-list';
    case 'remote-file': return 'file';
    case 'remote-directory': return 'folder';
    case 'retrieve-folder': return 'retrieve-folder';
    case 'generic': return 'jobs';
  }
}

export function jobStatusLabel(status: AutomaticJobStatus, skipReason: AutomaticJobSkipReason = 'None'): string {
  switch (status) {
    case 'pending': return 'Pending';
    case 'running': return 'Running';
    case 'complete': return 'Complete';
    case 'failed': return 'Failed';
    case 'cancelled': return 'Cancelled';
    case 'skipped':
      switch (skipReason) {
        case 'AlreadyExists': return 'Skipped · Exists';
        case 'NotFoundLastTime': return 'Skipped · Not found';
        case 'Manual': return 'Skipped · Manual';
        case 'Filtered': return 'Skipped · Filtered';
        default: return 'Skipped';
      }
  }
}

export function jobStatusClass(status: AutomaticJobStatus, skipReason: AutomaticJobSkipReason = 'None'): string {
  if (status === 'running') return 'receiving';
  if (status === 'skipped') return skipReason === 'AlreadyExists' ? 'skipped-exists' : 'skipped-other';
  return status;
}

/**
 * Extract jobs are terminal once extraction itself finishes, but their semantic
 * result may still be active. Top-level Jobs rows represent the submitted work,
 * so use the result's state/progress there while keeping Extract detail honest
 * about the extraction phase itself.
 */
export function effectiveJobStatus(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobStatus {
  if (job.kind !== 'extract' || job.status !== 'complete' || !job.payload.resultJobId) return job.status;
  const result = allJobs.find((candidate) => candidate.id === job.payload.resultJobId);
  return result ? effectiveJobStatus(result, allJobs) : job.status;
}

export function effectiveJobSkipReason(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobSkipReason {
  if (job.kind === 'extract' && job.status === 'complete' && job.payload.resultJobId) {
    const result = allJobs.find((candidate) => candidate.id === job.payload.resultJobId);
    if (result) return effectiveJobSkipReason(result, allJobs);
  }
  return job.skipReason ?? 'None';
}

export function effectiveJobProgress(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): JobBase['progress'] {
  if (job.kind === 'extract' && job.payload.resultJobId) {
    const result = allJobs.find((candidate) => candidate.id === job.payload.resultJobId);
    if (result) return effectiveJobProgress(result, allJobs);
  }
  return job.progress;
}

export function reverseExtractParent(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): ExtractJobRecord | null {
  return allJobs.find((candidate): candidate is ExtractJobRecord => candidate.kind === 'extract' && candidate.payload.resultJobId === job.id) ?? null;
}

export function semanticParent(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobRecord | null {
  if (job.parentJobId) return allJobs.find((candidate) => candidate.id === job.parentJobId) ?? null;
  return reverseExtractParent(job, allJobs);
}

export function isSemanticRoot(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): boolean {
  return semanticParent(job, allJobs) === null;
}

export function semanticChildren(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobRecord[] {
  const result: AutomaticJobRecord[] = [];
  if (job.kind === 'extract' && job.payload.resultJobId) {
    const extracted = allJobs.find((candidate) => candidate.id === job.payload.resultJobId);
    if (extracted) result.push(extracted);
  }
  result.push(...allJobs.filter((candidate) => candidate.parentJobId === job.id));
  return result;
}

export function semanticAncestors(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobRecord[] {
  const result: AutomaticJobRecord[] = [];
  const visited = new Set<string>();
  let current: AutomaticJobRecord | null = job;
  while (current) {
    const parent = semanticParent(current, allJobs);
    if (!parent || visited.has(parent.id)) break;
    visited.add(parent.id);
    result.unshift(parent);
    current = parent;
  }
  return result;
}

/**
 * Completed Extract jobs are an implementation step in the UI. Once their result
 * exists, navigate/render the result directly while preserving the import as
 * provenance on the root row. Active/failed extraction remains visible itself.
 */
export function presentationTarget(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobRecord {
  const visited = new Set<string>();
  let current = job;
  while (current.kind === 'extract' && current.status === 'complete' && !visited.has(current.id)) {
    const resultJobId = current.payload.resultJobId;
    if (!resultJobId) break;
    visited.add(current.id);
    const result = allJobs.find((candidate) => candidate.id === resultJobId);
    if (!result) break;
    current = result;
  }
  return current;
}

export function presentationParent(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobRecord | null {
  const parent = semanticParent(job, allJobs);
  if (!parent) return null;
  if (parent.kind === 'extract' && parent.status === 'complete' && parent.payload.resultJobId === job.id) {
    const grandparent = semanticParent(parent, allJobs);
    return grandparent ? presentationTarget(grandparent, allJobs) : null;
  }
  return presentationTarget(parent, allJobs);
}

export function presentationChildren(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobRecord[] {
  const seen = new Set<string>();
  return semanticChildren(job, allJobs)
    .map((child) => presentationTarget(child, allJobs))
    .filter((child) => {
      if (seen.has(child.id)) return false;
      seen.add(child.id);
      return true;
    });
}

export function presentationAncestors(job: AutomaticJobRecord, allJobs: AutomaticJobRecord[]): AutomaticJobRecord[] {
  const result: AutomaticJobRecord[] = [];
  const visited = new Set<string>();
  let current: AutomaticJobRecord | null = job;
  while (current) {
    const parent = presentationParent(current, allJobs);
    if (!parent || visited.has(parent.id)) break;
    visited.add(parent.id);
    result.unshift(parent);
    current = parent;
  }
  return result;
}
