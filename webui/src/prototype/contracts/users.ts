/** Proposed mixed-tree browse query; filtering occurs before cursor pagination. */
export interface ProposedShareTreeFilterRequestDto {
  query: string | null;
  cursor: string | null;
  limit: number;
}

/** Proposed peer-access mutation needed by the prototype's chat block control. */
export interface ProposedPeerAccessMutationDto {
  username: string;
  blocked: boolean;
  semantics: 'chat-and-peer-access';
}
