<script lang="ts">
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import TransferCancelButton from '../components/items/TransferCancelButton.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import { basename, type FolderItemFile, type TransferPresentation } from '../prototype/items';
  import { uploadsForScenario, type UploadEntry, type UploadFolderEntry } from '../prototype/uploads';

  interface Props { scenario: PrototypeScenario; onopenuser: (username: string) => void; }
  let { scenario, onopenuser }: Props = $props();

  let cancelledTransfers = $state<Set<string>>(new Set());
  let groups = $derived(uploadsForScenario(scenario, cancelledTransfers));
  let uploadCount = $derived(groups.reduce((total, group) => total + group.transferCount, 0));

  $effect(() => {
    scenario.id;
    cancelledTransfers = new Set();
  });

  function isCancellable(transfer?: TransferPresentation): boolean {
    return transfer?.cancellable === true;
  }

  function cancelTransfer(id: string): void {
    const next = new Set(cancelledTransfers);
    next.add(id);
    cancelledTransfers = next;
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

  function cancelFolderFile(folder: UploadFolderEntry, file: FolderItemFile): void {
    if (!folder.files.some((candidate) => candidate.id === file.id) || !isCancellable(file.transfer)) return;
    cancelTransfer(file.id);
  }
</script>

<section class="page uploads-page">
  <header class="page-heading transfers-heading">
    <p class="eyebrow">Transfers</p>
    <h1>Uploads</h1>
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
              <FileItemCard
                path={item.path}
                sizeBytes={item.sizeBytes}
                transfer={item.transfer}
              >
                {#snippet actions()}
                  {#if isCancellable(item.transfer)}
                    <TransferCancelButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {/if}
                {/snippet}
              </FileItemCard>
            {:else}
              <FolderItemCard
                path={item.path}
                sizeBytes={item.sizeBytes}
                files={item.files}
                transfer={item.transfer}
              >
                {#snippet actions()}
                  {#if isCancellable(item.transfer)}
                    <TransferCancelButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {/if}
                {/snippet}
                {#snippet fileActions(file)}
                  {#if isCancellable(file.transfer)}
                    <TransferCancelButton label={`Cancel ${file.relativePath}`} onclick={() => cancelFolderFile(item, file)} />
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
