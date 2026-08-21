<script lang="ts">
  import TransferBulkActions, { type BulkCancelMode } from '../components/TransferBulkActions.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import TransferItemActionButton from '../components/items/TransferItemActionButton.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import { basename, type FolderItemFile, type TransferPresentation } from '../prototype/items';
  import { uploadsForScenario, type UploadEntry, type UploadFolderEntry } from '../prototype/uploads';

  interface Props { scenario: PrototypeScenario; onopenuser: (username: string) => void; }
  let { scenario, onopenuser }: Props = $props();

  let cancelledTransfers = $state<Set<string>>(new Set());
  let removedTransfers = $state<Set<string>>(new Set());
  let bulkCancelMode = $state<BulkCancelMode>('all');
  let groups = $derived(uploadsForScenario(scenario, cancelledTransfers, removedTransfers));
  let uploadCount = $derived(groups.reduce((total, group) => total + group.transferCount, 0));

  $effect(() => {
    scenario.id;
    cancelledTransfers = new Set();
    removedTransfers = new Set();
    bulkCancelMode = 'all';
  });

  function isCancellable(transfer?: TransferPresentation): boolean {
    return transfer?.cancellable === true;
  }

  function isTerminal(transfer?: TransferPresentation): boolean {
    return transfer?.tone === 'complete' || transfer?.tone === 'failed' || transfer?.tone === 'cancelled';
  }

  function modeMatches(transfer?: TransferPresentation): boolean {
    if (!isCancellable(transfer)) return false;
    if (bulkCancelMode === 'all') return true;
    if (bulkCancelMode === 'queued') return transfer?.tone === 'queued';
    return transfer?.tone === 'active';
  }

  function cancelTransfer(id: string): void {
    const next = new Set(cancelledTransfers);
    next.add(id);
    cancelledTransfers = next;
  }

  function removeTransfer(id: string): void {
    const next = new Set(removedTransfers);
    next.add(id);
    removedTransfers = next;
  }

  function cancelItem(item: UploadEntry): void {
    if (item.kind === 'file') {
      if (isCancellable(item.transfer)) cancelTransfer(item.id);
      return;
    }

    const next = new Set(cancelledTransfers);
    for (const file of item.files) {
      if (isCancellable(file.transfer)) next.add(file.id);
    }
    cancelledTransfers = next;
  }

  function removeItem(item: UploadEntry): void {
    if (item.kind === 'file') {
      if (isTerminal(item.transfer)) removeTransfer(item.id);
      return;
    }
    if (!isTerminal(item.transfer)) return;
    const next = new Set(removedTransfers);
    for (const file of item.files) next.add(file.id);
    removedTransfers = next;
  }

  function cancelFolderFile(folder: UploadFolderEntry, file: FolderItemFile): void {
    if (!folder.files.some((candidate) => candidate.id === file.id) || !isCancellable(file.transfer)) return;
    cancelTransfer(file.id);
  }

  function removeFolderFile(folder: UploadFolderEntry, file: FolderItemFile): void {
    if (!folder.files.some((candidate) => candidate.id === file.id) || !isTerminal(file.transfer)) return;
    removeTransfer(file.id);
  }

  function cancelBulk(): void {
    const next = new Set(cancelledTransfers);
    for (const group of groups) {
      for (const item of group.items) {
        if (item.kind === 'file') {
          if (modeMatches(item.transfer)) next.add(item.id);
          continue;
        }
        for (const file of item.files) {
          if (modeMatches(file.transfer)) next.add(file.id);
        }
      }
    }
    cancelledTransfers = next;
  }

  function removeCompleted(): void {
    const next = new Set(removedTransfers);
    for (const group of groups) {
      for (const item of group.items) {
        if (item.kind === 'file') {
          if (isTerminal(item.transfer)) next.add(item.id);
          continue;
        }
        for (const file of item.files) {
          if (isTerminal(file.transfer)) next.add(file.id);
        }
      }
    }
    removedTransfers = next;
  }

  let canBulkCancel = $derived(groups.some((group) => group.items.some((item) =>
    item.kind === 'file' ? modeMatches(item.transfer) : item.files.some((file) => modeMatches(file.transfer)),
  )));
  let canRemoveCompleted = $derived(groups.some((group) => group.items.some((item) =>
    item.kind === 'file' ? isTerminal(item.transfer) : item.files.some((file) => isTerminal(file.transfer)),
  )));
</script>

<section class="page uploads-page">
  <header class="page-heading transfers-heading">
    <div>
      <p class="eyebrow">Transfers</p>
      <h1>Uploads</h1>
    </div>
    <TransferBulkActions
      mode={bulkCancelMode}
      canCancel={canBulkCancel}
      {canRemoveCompleted}
      onmodechange={(mode) => (bulkCancelMode = mode)}
      oncancel={cancelBulk}
      onremovecompleted={removeCompleted}
    />
  </header>

  {#if scenario.connection === 'offline'}
    <div class="empty-state">
      <strong>Daemon unavailable</strong>
      <p>Current upload state cannot be loaded while the daemon is offline.</p>
    </div>
  {:else if uploadCount === 0}
    <div class="empty-state">
      <strong>No uploads</strong>
      <p>Uploads will appear here as peers request shared files.</p>
    </div>
  {:else}
    <div class="transfer-peer-list">
      {#each groups as group (group.key)}
        <PeerItemGroup peer={{ username: group.peer }} itemCount={group.transferCount} itemNoun="upload" {onopenuser}>
          {#each group.items as item (item.id)}
            {#if item.kind === 'file'}
              <FileItemCard path={item.path} sizeBytes={item.sizeBytes} transfer={item.transfer}>
                {#snippet actions()}
                  {#if isCancellable(item.transfer)}
                    <TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {:else if isTerminal(item.transfer)}
                    <TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => removeItem(item)} />
                  {/if}
                {/snippet}
              </FileItemCard>
            {:else}
              <FolderItemCard path={item.path} sizeBytes={item.sizeBytes} files={item.files} transfer={item.transfer}>
                {#snippet actions()}
                  {#if isCancellable(item.transfer)}
                    <TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {:else if isTerminal(item.transfer)}
                    <TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => removeItem(item)} />
                  {/if}
                {/snippet}
                {#snippet fileActions(file)}
                  {#if isCancellable(file.transfer)}
                    <TransferItemActionButton label={`Cancel ${file.relativePath}`} onclick={() => cancelFolderFile(item, file)} />
                  {:else if isTerminal(file.transfer)}
                    <TransferItemActionButton kind="remove" label={`Remove ${file.relativePath}`} onclick={() => removeFolderFile(item, file)} />
                  {/if}
                {/snippet}
              </FolderItemCard>
            {/if}
          {/each}
        </PeerItemGroup>
      {/each}
    </div>
  {/if}
</section>
