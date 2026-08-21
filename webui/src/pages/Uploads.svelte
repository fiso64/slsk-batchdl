<script lang="ts">
  import TransferBulkActions, { type BulkCancelMode } from '../components/TransferBulkActions.svelte';
  import TransferTimeline from '../components/TransferTimeline.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import MutationStatus from '../components/MutationStatus.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import type { UserLinkActions } from '../prototype/navigation';
  import type { PrototypeMutationState, ProposedBulkActionRequestDto, ProposedHistoryDeleteRequestDto } from '../prototype/backend-contracts';
  import type { FolderItemFile, TransferPresentation } from '../prototype/items';
  import { resourceStateForScenario } from '../prototype/resource-state';
  import { uploadsForScenario, type UploadEntry, type UploadFolderEntry } from '../prototype/uploads';
  import { isTerminalTransfer, limitTransferGroups, transferGroupItemCount, type TransferTimelineEntry, type TransferTimelineFolderEntry } from '../prototype/transfers';

  interface Props { scenario: PrototypeScenario; userActions: UserLinkActions; }
  let { scenario, userActions }: Props = $props();

  let cancelledTransfers = $state<Set<string>>(new Set());
  let removedTransfers = $state<Set<string>>(new Set());
  let bulkCancelMode = $state<BulkCancelMode>('all');
  let pageLimit = $state(10);
  let mutation = $state<PrototypeMutationState>({ phase: 'idle' });
  let resourceState = $derived(resourceStateForScenario(scenario.id, 'uploads'));
  let allGroups = $derived(uploadsForScenario(scenario, cancelledTransfers, removedTransfers));
  let groups = $derived(limitTransferGroups(allGroups, pageLimit));
  let totalPresentationItems = $derived(transferGroupItemCount(allGroups));
  let hasMore = $derived(pageLimit < totalPresentationItems);
  let uploadCount = $derived(allGroups.reduce((total, group) => total + group.transferCount, 0));

  $effect(() => {
    scenario.id;
    cancelledTransfers = new Set(); removedTransfers = new Set(); bulkCancelMode = 'all'; pageLimit = 10; mutation = { phase: 'idle' };
  });

  function isCancellable(transfer?: TransferPresentation): boolean { return transfer?.cancellable === true; }
  function modeMatches(transfer?: TransferPresentation): boolean {
    if (!isCancellable(transfer)) return false;
    if (bulkCancelMode === 'all') return true;
    if (bulkCancelMode === 'queued') return transfer?.tone === 'queued';
    return transfer?.tone === 'active';
  }
  function cancelTransfer(id: string): void { cancelledTransfers = new Set(cancelledTransfers).add(id); }
  function removeTransfer(id: string): void { removedTransfers = new Set(removedTransfers).add(id); }

  function cancelItem(entry: TransferTimelineEntry): void {
    const item = entry as UploadEntry;
    mutation = { phase: 'pending', label: 'Cancelling upload…' };
    if (item.kind === 'file') {
      if (!isCancellable(item.transfer)) { mutation = { phase: 'rejected', label: 'Cancel rejected' }; return; }
      cancelTransfer(item.id);
    } else {
      const next = new Set(cancelledTransfers); let count = 0;
      for (const file of item.files) if (isCancellable(file.transfer)) { next.add(file.id); count += 1; }
      if (!count) { mutation = { phase: 'rejected', label: 'Nothing cancellable in folder' }; return; }
      cancelledTransfers = next;
    }
    mutation = { phase: 'succeeded', label: 'Upload cancelled' };
  }

  function removeItem(entry: TransferTimelineEntry): void {
    const item = entry as UploadEntry;
    mutation = { phase: 'pending', label: 'Removing upload history…' };
    if (item.kind === 'file') {
      if (!isTerminalTransfer(item.transfer)) return;
      const request: ProposedHistoryDeleteRequestDto = { resourceKind: 'upload-transfer', resourceIds: [item.id], semantics: 'archive-from-history' }; void request;
      removeTransfer(item.id);
    } else {
      if (!isTerminalTransfer(item.transfer)) return;
      const next = new Set(removedTransfers); for (const file of item.files) next.add(file.id); removedTransfers = next;
    }
    mutation = { phase: 'succeeded', label: 'Removed from history' };
  }

  function cancelFolderFile(entry: TransferTimelineFolderEntry, file: FolderItemFile): void {
    const folder = entry as UploadFolderEntry;
    if (!folder.files.some((candidate) => candidate.id === file.id) || !isCancellable(file.transfer)) return;
    mutation = { phase: 'pending', label: 'Cancelling upload…' }; cancelTransfer(file.id); mutation = { phase: 'succeeded', label: 'Upload cancelled' };
  }

  function removeFolderFile(entry: TransferTimelineFolderEntry, file: FolderItemFile): void {
    const folder = entry as UploadFolderEntry;
    if (!folder.files.some((candidate) => candidate.id === file.id) || !isTerminalTransfer(file.transfer)) return;
    mutation = { phase: 'pending', label: 'Removing upload history…' }; removeTransfer(file.id); mutation = { phase: 'succeeded', label: 'Removed from history' };
  }

  function cancelBulk(): void {
    const request: ProposedBulkActionRequestDto = { direction: 'upload', scope: 'current-view', action: 'cancel', filter: bulkCancelMode === 'active' ? 'in-progress' : bulkCancelMode };
    void request;
    const next = new Set(cancelledTransfers); let requested = 0;
    for (const group of groups) for (const item of group.items) {
      if (item.kind === 'file') { if (modeMatches(item.transfer)) { next.add(item.id); requested += 1; } }
      else for (const file of item.files) if (modeMatches(file.transfer)) { next.add(file.id); requested += 1; }
    }
    mutation = { phase: 'pending', label: `Cancelling ${requested} upload${requested === 1 ? '' : 's'}…` };
    cancelledTransfers = next;
    mutation = { phase: requested ? 'succeeded' : 'rejected', label: requested ? 'Bulk cancel complete' : 'Nothing cancellable in this view' };
  }

  function removeCompleted(): void {
    const request: ProposedBulkActionRequestDto = { direction: 'upload', scope: 'current-view', action: 'archive-terminal', filter: 'terminal' }; void request;
    const next = new Set(removedTransfers); let requested = 0;
    for (const group of groups) for (const item of group.items) {
      if (item.kind === 'file') { if (isTerminalTransfer(item.transfer)) { next.add(item.id); requested += 1; } }
      else for (const file of item.files) if (isTerminalTransfer(file.transfer)) { next.add(file.id); requested += 1; }
    }
    mutation = { phase: 'pending', label: `Removing ${requested} terminal upload${requested === 1 ? '' : 's'}…` };
    removedTransfers = next;
    mutation = { phase: requested ? 'succeeded' : 'rejected', label: requested ? 'Terminal history removed' : 'Nothing terminal in this view' };
  }

  let canBulkCancel = $derived(groups.some((group) => group.items.some((item) => item.kind === 'file' ? modeMatches(item.transfer) : item.files.some((file) => modeMatches(file.transfer)))));
  let canRemoveCompleted = $derived(groups.some((group) => group.items.some((item) => item.kind === 'file' ? isTerminalTransfer(item.transfer) : item.files.some((file) => isTerminalTransfer(file.transfer)))));
</script>

<section class="page uploads-page">
  <header class="page-heading transfers-heading">
    <div><p class="eyebrow">Transfers</p><h1>Uploads</h1></div>
    <TransferBulkActions mode={bulkCancelMode} canCancel={canBulkCancel} {canRemoveCompleted} onmodechange={(mode) => (bulkCancelMode = mode)} oncancel={cancelBulk} onremovecompleted={removeCompleted} />
  </header>

  <div class="transfer-resource-state">{#if !resourceState.blocking}<ResourceStateNotice state={resourceState} />{/if}<MutationStatus state={mutation} /></div>

  {#if scenario.connection === 'offline'}
    <div class="empty-state"><strong>Daemon unavailable</strong><p>Current upload state cannot be loaded while the daemon is offline.</p></div>
  {:else if uploadCount === 0}
    <div class="empty-state"><strong>No uploads</strong><p>Uploads will appear here as peers request shared files.</p></div>
  {:else}
    <TransferTimeline
      {groups}
      itemNoun="upload"
      {userActions}
      oncancelitem={cancelItem}
      onremoveitem={removeItem}
      oncancelfile={cancelFolderFile}
      onremovefile={removeFolderFile}
    />
    {#if hasMore}<LoadMoreButton label="Load earlier uploads" onclick={() => (pageLimit += 10)} />{/if}
  {/if}
</section>
