import type { components } from '../../api/generated';

export type BrowseSearchPageDto = components['schemas']['BrowseSearchPageDto'];
export type UserRestrictionsDto = components['schemas']['UserRestrictionsDto'];
export type SetUserRestrictionOverrideRequestDto = components['schemas']['SetUserRestrictionOverrideRequestDto'];

/** Query state for the prototype's in-memory stand-in for the paged browse-search API. */
export interface PrototypeShareTreeQuery {
  query: string | null;
  cursor: string | null;
  limit: number;
}
