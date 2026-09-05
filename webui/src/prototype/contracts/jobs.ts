import type { components } from '../../api/generated';

export type CreateJobPreviewRequestDto = components['schemas']['CreateJobPreviewRequestDto'];
export type JobPreviewSummaryDto = components['schemas']['JobPreviewSummaryDto'];
export type JobPreviewNodeDto = components['schemas']['JobPreviewNodeDto'];
export type CommitJobPreviewRequestDto = components['schemas']['CommitJobPreviewRequestDto'];
export type CommitJobPreviewResponseDto = components['schemas']['CommitJobPreviewResponseDto'];
export type InputArtifactDto = components['schemas']['InputArtifactDto'];
export type SetSubmissionArchivedRequestDto = components['schemas']['SetSubmissionArchivedRequestDto'];
export type SubmissionArchiveResponseDto = components['schemas']['SubmissionArchiveResponseDto'];

export interface SubmissionArchiveCommand {
  submissionId: string;
  request: SetSubmissionArchivedRequestDto;
}
