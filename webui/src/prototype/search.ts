export type SearchInputMode = 'simple' | 'split';
export type SearchResultMode = 'track' | 'album' | 'song-aggregate' | 'album-aggregate';
export type SearchModeFamily = 'track' | 'album';

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
  return mode === 'track' || mode === 'song-aggregate' ? 'track' : 'album';
}

export function isAggregateSearchMode(mode: SearchResultMode): boolean {
  return mode === 'song-aggregate' || mode === 'album-aggregate';
}

export function searchModeLabel(mode: SearchResultMode): string {
  switch (mode) {
    case 'track': return 'Track Search';
    case 'album': return 'Album Search';
    case 'song-aggregate': return 'Song Aggregate';
    case 'album-aggregate': return 'Album Aggregate';
  }
}

export function networkQuery(draft: SearchDraft): string {
  if (draft.mode === 'split') {
    return [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' ');
  }

  return draft.query.trim();
}
