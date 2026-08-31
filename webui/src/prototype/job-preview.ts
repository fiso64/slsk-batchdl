import type { AutomaticJobKind, ExtractSourceType } from './job-types';

export type InlineExtractSourceType = Exclude<ExtractSourceType, 'csv' | 'list' | 'string'>;
export type NewJobChoice = 'song' | 'album' | InlineExtractSourceType | 'csv' | 'list';
export type PreviewJobKind = AutomaticJobKind;

export interface JobPreviewNode {
  ref: string;
  kind: PreviewJobKind;
  title: string;
  detail?: string;
  sourceType?: ExtractSourceType;
  children: JobPreviewNode[];
}

export interface JobPreviewPlan {
  id: string;
  title: string;
  sourceLabel: string;
  roots: JobPreviewNode[];
}

export type SpotifyNewJobInput = 'url' | 'likes' | 'albums';

export interface CsvColumnMappingDraft {
  artistCol: string;
  albumCol: string;
  titleCol: string;
  lengthCol: string;
  descCol: string;
  ytIdCol: string;
  trackCountCol: string;
}

export interface NewJobDraft {
  choice: NewJobChoice;
  artist: string;
  title: string;
  album: string;
  source: string;
  spotifyInput: SpotifyNewJobInput;
  csvColumns: CsvColumnMappingDraft;
  uploadedFileName: string;
  uploadedFileType: string;
}

export const emptyNewJobDraft: NewJobDraft = {
  choice: 'song',
  artist: '',
  title: '',
  album: '',
  source: '',
  spotifyInput: 'url',
  csvColumns: {
    artistCol: '',
    albumCol: '',
    titleCol: '',
    lengthCol: '',
    descCol: '',
    ytIdCol: '',
    trackCountCol: '',
  },
  uploadedFileName: '',
  uploadedFileType: '',
};

let previewSequence = 1;
let createdJobSequence = 500;

function previewRef(prefix: string, index: number): string {
  return `${prefix}:${index}`;
}

function songPreview(ref: string, artist: string, title: string, detail?: string): JobPreviewNode {
  return { ref, kind: 'song', title: artist.trim() ? `${artist.trim()} — ${title}` : title, detail, children: [] };
}

function albumPreview(ref: string, artist: string, album: string, detail?: string): JobPreviewNode {
  return { ref, kind: 'album', title: artist.trim() ? `${artist.trim()} — ${album}` : album, detail, children: [] };
}

function spotifyPreview(source: string): JobPreviewPlan {
  if (source === 'spotify-likes') {
    const songs = [
      ['Floating Points', 'Birth4000'],
      ['Kelly Lee Owens', 'Corner of My Sky'],
      ['Burial', 'Phoneglow'],
      ['Four Tet', 'Loved'],
      ['Actress', 'Push Power (a 1)'],
      ['Oneohtrix Point Never', 'A Barely Lit Path'],
    ].map(([artist, title], index) => songPreview(previewRef('spotify-like', index), artist!, title!, `Liked song ${index + 1}`));
    return {
      id: `preview-${previewSequence++}`,
      title: 'Spotify Likes',
      sourceLabel: 'Spotify liked songs · 6 songs',
      roots: [{ ref: 'spotify-likes-extract', kind: 'extract', title: 'Spotify Likes', detail: source, sourceType: 'spotify', children: [{ ref: 'spotify-likes-list', kind: 'job-list', title: 'Spotify Likes', detail: '6 jobs', children: songs }] }],
    };
  }

  if (source === 'spotify-albums') {
    const albums = [
      ['Nujabes', 'Modal Soul'],
      ['Autechre', 'Amber'],
      ['Boards of Canada', 'Geogaddi'],
      ['Biosphere', 'Substrata'],
    ].map(([artist, album], index) => albumPreview(previewRef('spotify-album', index), artist!, album!, `Liked album ${index + 1}`));
    return {
      id: `preview-${previewSequence++}`,
      title: 'Spotify Liked Albums',
      sourceLabel: 'Spotify liked albums · 4 albums',
      roots: [{ ref: 'spotify-albums-extract', kind: 'extract', title: 'Spotify Liked Albums', detail: source, sourceType: 'spotify', children: [{ ref: 'spotify-albums-list', kind: 'job-list', title: 'Spotify Liked Albums', detail: '4 jobs', children: albums }] }],
    };
  }

  if (source.includes('/album/')) {
    return {
      id: `preview-${previewSequence++}`,
      title: 'Spotify album',
      sourceLabel: 'Spotify album',
      roots: [{ ref: 'spotify-album-extract', kind: 'extract', title: 'Spotify album', detail: source, sourceType: 'spotify', children: [albumPreview('spotify-direct-album', 'Nujabes', 'Modal Soul')] }],
    };
  }

  const songs = [
    ['Floating Points', 'Birth4000'],
    ['Kelly Lee Owens', 'Corner of My Sky'],
    ['Burial', 'Phoneglow'],
    ['Four Tet', 'Loved'],
    ['Actress', 'Push Power (a 1)'],
    ['Oneohtrix Point Never', 'A Barely Lit Path'],
    ['Skee Mask', 'Hedwig Transformation Group'],
    ['Nia Archives', 'Crowded Roomz'],
  ].map(([artist, title], index) => songPreview(previewRef('spotify-song', index), artist!, title!, `Track ${index + 1}`));
  return {
    id: `preview-${previewSequence++}`,
    title: 'Discover Weekly',
    sourceLabel: 'Spotify playlist · 8 songs',
    roots: [{ ref: 'spotify-extract', kind: 'extract', title: 'Discover Weekly', detail: source, sourceType: 'spotify', children: [{ ref: 'spotify-list', kind: 'job-list', title: 'Discover Weekly', detail: '8 jobs', children: songs }] }],
  };
}

function youtubePreview(source: string): JobPreviewPlan {
  return { id: `preview-${previewSequence++}`, title: 'YouTube import', sourceLabel: 'YouTube', roots: [{ ref: 'youtube-extract', kind: 'extract', title: 'YouTube source', detail: source, sourceType: 'youtube', children: [songPreview('youtube-song', 'Boards of Canada', 'Dayvan Cowboy', 'Resolved from video metadata')] }] };
}

function bandcampPreview(source: string): JobPreviewPlan {
  return { id: `preview-${previewSequence++}`, title: 'Bandcamp import', sourceLabel: 'Bandcamp album', roots: [{ ref: 'bandcamp-extract', kind: 'extract', title: 'Bandcamp album', detail: source, sourceType: 'bandcamp', children: [albumPreview('bandcamp-album', 'Biosphere', 'Substrata')] }] };
}

function musicBrainzPreview(source: string): JobPreviewPlan {
  return { id: `preview-${previewSequence++}`, title: 'MusicBrainz import', sourceLabel: 'MusicBrainz release', roots: [{ ref: 'mb-extract', kind: 'extract', title: 'MusicBrainz release', detail: source, sourceType: 'musicbrainz', children: [albumPreview('mb-album', 'Aphex Twin', 'Selected Ambient Works 85–92')] }] };
}

function soulseekPreview(source: string): JobPreviewPlan {
  return { id: `preview-${previewSequence++}`, title: 'Soulseek import', sourceLabel: 'Soulseek link', roots: [{ ref: 'slsk-extract', kind: 'extract', title: 'Soulseek link', detail: source, sourceType: 'soulseek', children: [{ ref: 'slsk-dir', kind: 'remote-directory', title: 'nightshift — Jazz/Casiopea/Mint Jams', detail: 'Exact remote directory', children: [] }] }] };
}

export function previewSource(source: string, sourceType: InlineExtractSourceType): JobPreviewPlan {
  const input = source.trim();
  switch (sourceType) {
    case 'spotify': return spotifyPreview(input);
    case 'youtube': return youtubePreview(input);
    case 'bandcamp': return bandcampPreview(input);
    case 'musicbrainz': return musicBrainzPreview(input);
    case 'soulseek': return soulseekPreview(input);
  }
}

function csvPreview(filename: string): JobPreviewPlan {
  return {
    id: `preview-${previewSequence++}`,
    title: filename,
    sourceLabel: 'CSV · 6 jobs',
    roots: [{
      ref: 'csv-extract', kind: 'extract', title: filename, detail: 'Uploaded artifact', sourceType: 'csv', children: [{
        ref: 'csv-list', kind: 'job-list', title: filename.replace(/\.csv$/i, ''), detail: 'Mixed jobs', children: [
          songPreview('csv-1', 'Boards of Canada', 'Roygbiv', 'CSV row 2'),
          albumPreview('csv-2', 'Autechre', 'Amber', 'CSV row 3'),
          songPreview('csv-3', 'Burial', 'Archangel', 'CSV row 4'),
          albumPreview('csv-4', 'Nujabes', 'Modal Soul', 'CSV row 5'),
          songPreview('csv-5', 'Aphex Twin', 'Xtal', 'CSV row 6'),
          albumPreview('csv-6', 'Biosphere', 'Substrata', 'CSV row 7'),
        ],
      }],
    }],
  };
}

function listPreview(filename: string): JobPreviewPlan {
  return {
    id: `preview-${previewSequence++}`,
    title: filename,
    sourceLabel: 'List file · nested sources',
    roots: [{
      ref: 'list-extract', kind: 'extract', title: filename, detail: 'Uploaded artifact', sourceType: 'list', children: [{
        ref: 'list-jobs', kind: 'job-list', title: filename.replace(/\.(txt|list)$/i, ''), detail: '4 source items', children: [
          { ref: 'list-spotify', kind: 'extract', title: 'Discover Weekly', detail: 'Spotify playlist', sourceType: 'spotify', children: [{ ref: 'list-spotify-jobs', kind: 'job-list', title: 'Discover Weekly', detail: '3 songs', children: [songPreview('list-sp-1', 'Floating Points', 'Birth4000'), songPreview('list-sp-2', 'Kelly Lee Owens', 'Corner of My Sky'), songPreview('list-sp-3', 'Burial', 'Phoneglow')] }] },
          { ref: 'list-bandcamp', kind: 'extract', title: 'Bandcamp source', detail: 'Album URL', sourceType: 'bandcamp', children: [albumPreview('list-bc-album', 'Biosphere', 'Substrata')] },
          { ref: 'list-string', kind: 'extract', title: 'Boards of Canada - Roygbiv', detail: 'String source', sourceType: 'string', children: [songPreview('list-string-song', 'Boards of Canada', 'Roygbiv')] },
          { ref: 'list-slsk', kind: 'extract', title: 'Soulseek directory', detail: 'slsk://nightshift/Jazz/Casiopea/Mint Jams', sourceType: 'soulseek', children: [{ ref: 'list-slsk-dir', kind: 'remote-directory', title: 'nightshift — Jazz/Casiopea/Mint Jams', detail: 'Exact remote directory', children: [] }] },
        ],
      }],
    }],
  };
}


export function previewUploadedFile(filename: string, kind: 'csv' | 'list'): JobPreviewPlan {
  return kind === 'csv' ? csvPreview(filename) : listPreview(filename);
}

export function previewDirectJob(draft: NewJobDraft): JobPreviewPlan {
  let root: JobPreviewNode;
  let title: string;
  if (draft.choice === 'album') {
    title = draft.artist.trim() ? `${draft.artist.trim()} — ${draft.album.trim()}` : draft.album.trim();
    root = albumPreview('direct-album', draft.artist.trim(), draft.album.trim());
  } else {
    title = draft.artist.trim() ? `${draft.artist.trim()} — ${draft.title.trim()}` : draft.title.trim();
    root = songPreview('direct-song', draft.artist.trim(), draft.title.trim());
  }
  return { id: `preview-${previewSequence++}`, title, sourceLabel: 'Direct job', roots: [root] };
}

export function previewLeafRefs(plan: JobPreviewPlan): string[] {
  const refs: string[] = [];
  const visit = (node: JobPreviewNode) => {
    if (node.children.length === 0) refs.push(node.ref);
    else node.children.forEach(visit);
  };
  plan.roots.forEach(visit);
  return refs;
}

export function descendantLeafRefs(node: JobPreviewNode): string[] {
  if (!node.children.length) return [node.ref];
  return node.children.flatMap(descendantLeafRefs);
}
