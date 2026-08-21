<script lang="ts">
  import TransferBulkActions, { type BulkCancelMode } from '../components/TransferBulkActions.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import TransferItemActionButton from '../components/items/TransferItemActionButton.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import { downloadsForScenario, type AlbumDownloadItem, type DownloadItem } from '../prototype/downloads';
  import { groupAdjacentBy } from '../prototype/grouping';
  import { basename, type FolderItemFile, type TransferPresentation } from '../prototype/items';

  interface Props { scenario: PrototypeScenario; onopenuser: (username: string) => void; }
  let { scenario, onopenuser }: Props = $props();

  let cancelledItems = $state<Set<string>>(new Set());
  let cancelledFiles = $state<Set<string>>(new Set());
  let removedItems = $state<Set<string>>(new Set());
  let removedFiles = $state<Set<string>>(new Set());
  let bulkCancelMode = $state<BulkCancelMode>('all');
  let downloads = $derived(downloadsForScenario(scenario.id));
  let visibleDownloads = $derived(downloads.filter((item) => !removedItems.has(item.id)));
  let groups = $derived(
    groupAdjacentBy(visibleDownloads, (item) => item.peer, `${scenario.id}:downloads:`).map((group) => ({
      key: group.key,
      peer: group.identity,
      items: group.items,
    })),
  );

  $effect(() => {
    scenario.id;
    cancelledItems = new Set();
    cancelledFiles = new Set();
    removedItems = new Set();
    removedFiles = new Set();
    bulkCancelMode = 'all';
  });

  function isCancellable(transfer?: TransferPresentation): boolean {
    return transfer?.cancellable === true;
  }

  function isTerminal(transfer?: TransferPresentation): boolean {
    return transfer?.tone === 'complete' || transfer?.tone === 'failed' || transfer?.tone === 'cancelled';
  }

  function cancelledPresentation(transfer: TransferPresentation): TransferPresentation {
    return {
      ...transfer,
      state: 'Cancelled',
      tone: 'cancelled',
      cancellable: false,
      progressText: 'Cancelled',
      speed: undefined,
      eta: undefined,
    };
  }

  function presentationFor(item: DownloadItem): TransferPresentation {
    const transfer = { ...item.transfer, direction: 'download' as const };
    return cancelledItems.has(item.id) ? cancelledPresentation(transfer) : transfer;
  }

  function fileCancellationKey(albumId: string, fileId: string): string {
    return `${albumId}:${fileId}`;
  }

  function filesFor(album: AlbumDownloadItem): FolderItemFile[] {
    return album.files
      .filter((file) => !removedFiles.has(fileCancellationKey(album.id, file.id)))
      .map((file) => {
        if (!file.transfer) return file;
        const transfer = { ...file.transfer, direction: 'download' as const };
        const cancelledByAlbum = cancelledItems.has(album.id) && isCancellable(file.transfer);
        const cancelledIndividually = cancelledFiles.has(fileCancellationKey(album.id, file.id));
        if (!cancelledByAlbum && !cancelledIndividually) return { ...file, transfer };
        return { ...file, transfer: cancelledPresentation(transfer) };
      });
  }

  function cancelItem(item: DownloadItem): void {
    if (!isCancellable(presentationFor(item))) return;
    const next = new Set(cancelledItems);
    next.add(item.id);
    cancelledItems = next;
  }

  function cancelAlbumFile(album: AlbumDownloadItem, file: FolderItemFile): void {
    if (cancelledItems.has(album.id) || !isCancellable(file.transfer)) return;
    const next = new Set(cancelledFiles);
    next.add(fileCancellationKey(album.id, file.id));
    cancelledFiles = next;
  }

  function removeItem(item: DownloadItem): void {
    if (!isTerminal(presentationFor(item))) return;
    const next = new Set(removedItems);
    next.add(item.id);
    removedItems = next;
  }

  function removeAlbumFile(album: AlbumDownloadItem, file: FolderItemFile): void {
    if (!isTerminal(file.transfer)) return;
    const next = new Set(removedFiles);
    next.add(fileCancellationKey(album.id, file.id));
    removedFiles = next;
  }

  function cancelModeMatches(transfer: TransferPresentation): boolean {
    if (!isCancellable(transfer)) return false;
    if (bulkCancelMode === 'all') return true;
    if (bulkCancelMode === 'queued') return transfer.tone === 'queued';
    return transfer.tone === 'active';
  }

  function cancelBulk(): void {
    const next = new Set(cancelledItems);
    for (const item of visibleDownloads) {
      if (cancelModeMatches(presentationFor(item))) next.add(item.id);
    }
    cancelledItems = next;
  }

  function removeCompleted(): void {
    const next = new Set(removedItems);
    for (const item of visibleDownloads) {
      // Albums are removed as one logical download; never prune completed child files here.
      if (isTerminal(presentationFor(item))) next.add(item.id);
    }
    removedItems = next;
  }

  let canBulkCancel = $derived(visibleDownloads.some((item) => cancelModeMatches(presentationFor(item))));
  let canRemoveCompleted = $derived(visibleDownloads.some((item) => isTerminal(presentationFor(item))));
</script>

<section class="page downloads-page">
  <header class="page-heading transfers-heading">
    <div>
      <p class="eyebrow">Transfers</p>
      <h1>Downloads</h1>
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
      <p>Current download state cannot be loaded while the daemon is offline.</p>
    </div>
  {:else if visibleDownloads.length === 0}
    <div class="empty-state">
      <strong>No downloads</strong>
      <p>Downloaded tracks and albums will appear here in creation order.</p>
    </div>
  {:else}
    <div class="transfer-peer-list">
      {#each groups as group (group.key)}
        <PeerItemGroup peer={{ username: group.peer }} itemCount={group.items.length} itemNoun="download" {onopenuser}>
          {#each group.items as item (item.id)}
            {@const itemTransfer = presentationFor(item)}
            {#if item.kind === 'track'}
              <FileItemCard
                path={item.path}
                sizeBytes={item.sizeBytes}
                audio={item.audio}
                transfer={itemTransfer}
              >
                {#snippet actions()}
                  {#if isCancellable(itemTransfer)}
                    <TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {:else if isTerminal(itemTransfer)}
                    <TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => removeItem(item)} />
                  {/if}
                {/snippet}
              </FileItemCard>
            {:else}
              {@const albumFiles = filesFor(item)}
              <FolderItemCard
                path={item.path}
                sizeBytes={albumFiles.reduce((total, file) => total + file.sizeBytes, 0)}
                files={albumFiles}
                transfer={itemTransfer}
              >
                {#snippet actions()}
                  {#if isCancellable(itemTransfer)}
                    <TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {:else if isTerminal(itemTransfer)}
                    <TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => removeItem(item)} />
                  {/if}
                {/snippet}
                {#snippet fileActions(file)}
                  {#if !cancelledItems.has(item.id) && !cancelledFiles.has(fileCancellationKey(item.id, file.id)) && isCancellable(file.transfer)}
                    <TransferItemActionButton label={`Cancel ${file.relativePath}`} onclick={() => cancelAlbumFile(item, file)} />
                  {:else if isTerminal(file.transfer)}
                    <TransferItemActionButton kind="remove" label={`Remove ${file.relativePath}`} onclick={() => removeAlbumFile(item, file)} />
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
