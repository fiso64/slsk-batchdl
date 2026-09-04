import type { components } from '../../api/generated';

export type CreateSearchViewRequestDto = components['schemas']['CreateSearchViewRequestDto'];
export type SearchViewSummaryDto = components['schemas']['SearchViewSummaryDto'];
export type SearchViewUpdateDto = components['schemas']['SearchViewUpdateDto'];
export type SearchViewRevisionDto = components['schemas']['SearchViewRevisionDto'];
export type SearchViewFilePageDto = components['schemas']['SearchViewFilePageDto'];
export type SearchViewDirectoryPageDto = components['schemas']['SearchViewDirectoryPageDto'];
export type SearchViewDirectoryFilePageDto = components['schemas']['SearchViewDirectoryFilePageDto'];
export type SearchViewAggregateTrackPageDto = components['schemas']['SearchViewAggregateTrackPageDto'];
export type SearchViewAggregateTrackOptionPageDto = components['schemas']['SearchViewAggregateTrackOptionPageDto'];
export type SearchViewAggregateAlbumPageDto = components['schemas']['SearchViewAggregateAlbumPageDto'];
export type SearchViewAggregateAlbumOptionPageDto = components['schemas']['SearchViewAggregateAlbumOptionPageDto'];
export type CommitSearchViewSelectionRequestDto = components['schemas']['CommitSearchViewSelectionRequestDto'];
export type CommitSearchViewSelectionResponseDto = components['schemas']['CommitSearchViewSelectionResponseDto'];
export type PeerDirectoryRefDto = components['schemas']['PeerDirectoryRefDto'];

export type SearchViewPageDto =
  | SearchViewFilePageDto
  | SearchViewDirectoryPageDto
  | SearchViewDirectoryFilePageDto
  | SearchViewAggregateTrackPageDto
  | SearchViewAggregateTrackOptionPageDto
  | SearchViewAggregateAlbumPageDto
  | SearchViewAggregateAlbumOptionPageDto;

export interface VisibleSearchViewPage {
  key: string;
  load(revision: number | string): Promise<SearchViewPageDto>;
}

export interface SearchViewRefreshResult {
  summary: SearchViewSummaryDto;
  pages: ReadonlyMap<string, SearchViewPageDto>;
  changed: boolean;
}

/**
 * Polls only the fixed-size revision summary. When it advances, reloads the
 * pages and expanded groups the user can currently see, all at one revision.
 */
export async function refreshVisibleSearchView(
  current: SearchViewSummaryDto,
  visible: readonly VisibleSearchViewPage[],
  getUpdate: (afterRevision: number | string) => Promise<SearchViewUpdateDto>,
): Promise<SearchViewRefreshResult> {
  const update = await getUpdate(current.revision);
  if (!update.hasNewRevision) {
    return { summary: update.summary, pages: new Map(), changed: false };
  }

  const loaded = await Promise.all(visible.map(async (page) => {
    const value = await page.load(update.summary.revision);
    if (String(value.revision.revision) !== String(update.summary.revision)) {
      throw new Error('Search View page revision does not match the refreshed summary.');
    }
    return [page.key, value] as const;
  }));
  return { summary: update.summary, pages: new Map(loaded), changed: true };
}
