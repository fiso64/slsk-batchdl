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

/** Normalized presentation value of the daemon's ServerJobSkipReason. */
export type AutomaticJobSkipReason = 'None' | 'AlreadyExists' | 'NotFoundLastTime' | 'Manual' | 'Filtered';

export type ExtractSourceType =
  | 'spotify'
  | 'youtube'
  | 'bandcamp'
  | 'musicbrainz'
  | 'soulseek'
  | 'csv'
  | 'list'
  | 'string';
