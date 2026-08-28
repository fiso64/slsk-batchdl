export type SearchInputMode = 'simple' | 'split';
export type SearchResultMode = 'generic' | 'track' | 'album' | 'song-aggregate' | 'album-aggregate';
export type SearchModeFamily = 'generic' | 'track' | 'album';

export interface SearchDraft {
  mode: SearchInputMode;
  resultMode: SearchResultMode;
  query: string;
  artist: string;
  title: string;
}

export const emptySearchDraft: SearchDraft = {
  mode: 'simple',
  resultMode: 'album',
  query: '',
  artist: '',
  title: '',
};

export function searchModeFamily(mode: SearchResultMode): SearchModeFamily {
  if (mode === 'generic') return 'generic';
  return mode === 'track' || mode === 'song-aggregate' ? 'track' : 'album';
}

export function isAggregateSearchMode(mode: SearchResultMode): boolean {
  return mode === 'song-aggregate' || mode === 'album-aggregate';
}

export function searchModeLabel(mode: SearchResultMode): string {
  switch (mode) {
    case 'generic': return 'File Search';
    case 'track': return 'Track Search';
    case 'album': return 'Album Search';
    case 'song-aggregate': return 'Song Aggregate';
    case 'album-aggregate': return 'Album Aggregate';
  }
}

export function networkQuery(draft: SearchDraft): string {
  if (draft.resultMode === 'generic') {
    return draft.mode === 'simple'
      ? draft.query.trim()
      : [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' ');
  }

  if (draft.mode === 'split') {
    return [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' ');
  }

  return draft.query.trim();
}
