import type { components } from '../../api/generated';

export type TransferTimelinePageDto = components['schemas']['TransferTimelinePageDto'];
export type BulkCancelTransfersRequestDto = components['schemas']['BulkCancelTransfersRequestDto'];
export type ArchiveTransfersRequestDto = components['schemas']['ArchiveTransfersRequestDto'];
export type SetTransferArchivedRequestDto = components['schemas']['SetTransferArchivedRequestDto'];
export type TransferCommandReceiptDto = components['schemas']['TransferCommandReceiptDto'];

export interface TransferArchiveCommand {
  transferId: string;
  request: SetTransferArchivedRequestDto;
}
