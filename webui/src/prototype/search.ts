export type SearchInputMode = 'simple' | 'split';
export type SearchResultMode = 'track' | 'album';

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

export function networkQuery(draft: SearchDraft): string {
  if (draft.mode === 'split') {
    return [draft.artist.trim(), draft.title.trim()].filter(Boolean).join(' ');
  }

  return draft.query.trim();
}
