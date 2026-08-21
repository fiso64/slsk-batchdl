<script lang="ts">
  import TransferBulkActions, { type BulkCancelMode } from '../components/TransferBulkActions.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import MutationStatus from '../components/MutationStatus.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import TransferItemActionButton from '../components/items/TransferItemActionButton.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import type { UserLinkActions } from '../prototype/navigation';
  import type { PrototypeMutationState, ProposedBulkActionRequestDto, ProposedHistoryDeleteRequestDto } from '../prototype/backend-contracts';
  import { basename, type FolderItemFile, type TransferPresentation } from '../prototype/items';
  import { resourceStateForScenario } from '../prototype/resource-state';
  import { uploadsForScenario, type UploadEntry, type UploadFolderEntry } from '../prototype/uploads';

  interface Props { scenario: PrototypeScenario; userActions: UserLinkActions; }
  let { scenario, userActions }: Props = $props();

  let cancelledTransfers = $state<Set<string>>(new Set());
  let removedTransfers = $state<Set<string>>(new Set());
  let bulkCancelMode = $state<BulkCancelMode>('all');
  let groupLimit = $state(4);
  let mutation = $state<PrototypeMutationState>({ phase: 'idle' });
  let resourceState = $derived(resourceStateForScenario(scenario.id, 'uploads'));
  let allGroups = $derived(uploadsForScenario(scenario, cancelledTransfers, removedTransfers));
  let groups = $derived(allGroups.slice(0, groupLimit));
  let hasMore = $derived(groupLimit < allGroups.length);
  let uploadCount = $derived(allGroups.reduce((total, group) => total + group.transferCount, 0));

  $effect(() => {
    scenario.id;
    cancelledTransfers = new Set(); removedTransfers = new Set(); bulkCancelMode = 'all'; groupLimit = 4; mutation = { phase: 'idle' };
  });

  function isCancellable(transfer?: TransferPresentation): boolean { return transfer?.cancellable === true; }
  function isTerminal(transfer?: TransferPresentation): boolean { return transfer?.tone === 'complete' || transfer?.tone === 'failed' || transfer?.tone === 'cancelled'; }
  function modeMatches(transfer?: TransferPresentation): boolean {
    if (!isCancellable(transfer)) return false;
    if (bulkCancelMode === 'all') return true;
    if (bulkCancelMode === 'queued') return transfer?.tone === 'queued';
    return transfer?.tone === 'active';
  }
  function cancelTransfer(id: string): void { cancelledTransfers = new Set(cancelledTransfers).add(id); }
  function removeTransfer(id: string): void { removedTransfers = new Set(removedTransfers).add(id); }

  function cancelItem(item: UploadEntry): void {
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
  function removeItem(item: UploadEntry): void {
    mutation = { phase: 'pending', label: 'Removing upload history…' };
    if (item.kind === 'file') {
      if (!isTerminal(item.transfer)) return;
      const request: ProposedHistoryDeleteRequestDto = { resourceKind: 'upload-transfer', resourceIds: [item.id], semantics: 'archive-from-history' }; void request;
      removeTransfer(item.id);
    } else {
      if (!isTerminal(item.transfer)) return;
      const next = new Set(removedTransfers); for (const file of item.files) next.add(file.id); removedTransfers = next;
    }
    mutation = { phase: 'succeeded', label: 'Removed from history',  };
  }
  function cancelFolderFile(folder: UploadFolderEntry, file: FolderItemFile): void {
    if (!folder.files.some((candidate) => candidate.id === file.id) || !isCancellable(file.transfer)) return;
    mutation = { phase: 'pending', label: 'Cancelling upload…' }; cancelTransfer(file.id); mutation = { phase: 'succeeded', label: 'Upload cancelled' };
  }
  function removeFolderFile(folder: UploadFolderEntry, file: FolderItemFile): void {
    if (!folder.files.some((candidate) => candidate.id === file.id) || !isTerminal(file.transfer)) return;
    mutation = { phase: 'pending', label: 'Removing upload history…' }; removeTransfer(file.id); mutation = { phase: 'succeeded', label: 'Removed from history' };
  }

  function cancelBulk(): void {
    const request: ProposedBulkActionRequestDto = { direction: 'upload', scope: 'current-view', action: 'cancel', filter: bulkCancelMode === 'active' ? 'in-progress' : bulkCancelMode, logicalItems: false };
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
    const request: ProposedBulkActionRequestDto = { direction: 'upload', scope: 'current-view', action: 'archive-terminal', filter: 'terminal', logicalItems: false }; void request;
    const next = new Set(removedTransfers); let requested = 0;
    for (const group of groups) for (const item of group.items) {
      if (item.kind === 'file') { if (isTerminal(item.transfer)) { next.add(item.id); requested += 1; } }
      else for (const file of item.files) if (isTerminal(file.transfer)) { next.add(file.id); requested += 1; }
    }
    mutation = { phase: 'pending', label: `Removing ${requested} terminal upload${requested === 1 ? '' : 's'}…` };
    removedTransfers = next;
    mutation = { phase: requested ? 'succeeded' : 'rejected', label: requested ? 'Terminal history removed' : 'Nothing terminal in this view' };
  }

  let canBulkCancel = $derived(groups.some((group) => group.items.some((item) => item.kind === 'file' ? modeMatches(item.transfer) : item.files.some((file) => modeMatches(file.transfer)))));
  let canRemoveCompleted = $derived(groups.some((group) => group.items.some((item) => item.kind === 'file' ? isTerminal(item.transfer) : item.files.some((file) => isTerminal(file.transfer)))));
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
    <div class="transfer-peer-list">
      {#each groups as group (group.key)}
        <PeerItemGroup peer={{ username: group.peer }} itemCount={group.transferCount} itemNoun="upload" {userActions}>
          {#each group.items as item (item.id)}
            {#if item.kind === 'file'}
              <FileItemCard path={item.path} sizeBytes={item.sizeBytes} transfer={item.transfer}>
                {#snippet actions()}{#if isCancellable(item.transfer)}<TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />{:else if isTerminal(item.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => removeItem(item)} />{/if}{/snippet}
              </FileItemCard>
            {:else}
              <FolderItemCard path={item.path} sizeBytes={item.sizeBytes} files={item.files} transfer={item.transfer}>
                {#snippet actions()}{#if isCancellable(item.transfer)}<TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />{:else if isTerminal(item.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => removeItem(item)} />{/if}{/snippet}
                {#snippet fileActions(file)}{#if isCancellable(file.transfer)}<TransferItemActionButton label={`Cancel ${file.relativePath}`} onclick={() => cancelFolderFile(item, file)} />{:else if isTerminal(file.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${file.relativePath}`} onclick={() => removeFolderFile(item, file)} />{/if}{/snippet}
              </FolderItemCard>
            {/if}
          {/each}
        </PeerItemGroup>
      {/each}
    </div>
    {#if hasMore}<LoadMoreButton label="Load earlier uploads" onclick={() => (groupLimit += 4)} />{/if}
  {/if}
</section>
