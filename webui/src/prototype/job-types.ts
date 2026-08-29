export type AutomaticJobKind =
  | 'song'
  | 'album'
  | 'aggregate'
  | 'album-aggregate'
  | 'extract'
  | 'job-list'
  | 'remote-file'
  | 'remote-directory'
  | 'retrieve-folder'
  | 'generic';

export type AutomaticJobStatus = 'pending' | 'running' | 'complete' | 'failed' | 'cancelled' | 'skipped';

export type ExtractSourceType =
  | 'spotify'
  | 'youtube'
  | 'bandcamp'
  | 'musicbrainz'
  | 'soulseek'
  | 'csv'
  | 'list'
  | 'string';
