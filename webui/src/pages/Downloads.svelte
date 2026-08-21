<script lang="ts">
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import TransferCancelButton from '../components/items/TransferCancelButton.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import { downloadsForScenario, type AlbumDownloadItem, type DownloadItem } from '../prototype/downloads';
  import { groupAdjacentBy } from '../prototype/grouping';
  import { basename, type FolderItemFile, type TransferPresentation } from '../prototype/items';

  interface Props { scenario: PrototypeScenario; onopenuser: (username: string) => void; }
  let { scenario, onopenuser }: Props = $props();

  let cancelledItems = $state<Set<string>>(new Set());
  let cancelledFiles = $state<Set<string>>(new Set());
  let downloads = $derived(downloadsForScenario(scenario.id));
  let groups = $derived(
    groupAdjacentBy(downloads, (item) => item.peer, `${scenario.id}:downloads:`).map((group) => ({
      key: group.key,
      peer: group.identity,
      items: group.items,
    })),
  );

  $effect(() => {
    scenario.id;
    cancelledItems = new Set();
    cancelledFiles = new Set();
  });

  function isCancellable(transfer?: TransferPresentation): boolean {
    return transfer?.cancellable === true;
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
    return album.files.map((file) => {
      if (!file.transfer) return file;
      const transfer = { ...file.transfer, direction: 'download' as const };
      const cancelledByAlbum = cancelledItems.has(album.id) && isCancellable(file.transfer);
      const cancelledIndividually = cancelledFiles.has(fileCancellationKey(album.id, file.id));
      if (!cancelledByAlbum && !cancelledIndividually) return { ...file, transfer };
      return { ...file, transfer: cancelledPresentation(transfer) };
    });
  }

  function cancelItem(item: DownloadItem): void {
    if (!isCancellable(item.transfer)) return;
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
</script>

<section class="page downloads-page">
  <header class="page-heading transfers-heading">
    <p class="eyebrow">Transfers</p>
    <h1>Downloads</h1>
  </header>

  {#if scenario.connection === 'offline'}
    <div class="empty-state">
      <strong>Daemon unavailable</strong>
      <p>Current download state cannot be loaded while the daemon is offline.</p>
    </div>
  {:else if downloads.length === 0}
    <div class="empty-state">
      <strong>No downloads</strong>
      <p>Downloaded tracks and albums will appear here in creation order.</p>
    </div>
  {:else}
    <div class="transfer-peer-list">
      {#each groups as group (group.key)}
        <PeerItemGroup peer={{ username: group.peer }} itemCount={group.items.length} itemNoun="download" {onopenuser}>
          {#each group.items as item (item.id)}
            {#if item.kind === 'track'}
              <FileItemCard
                path={item.path}
                sizeBytes={item.sizeBytes}
                audio={item.audio}
                transfer={presentationFor(item)}
              >
                {#snippet actions()}
                  {#if !cancelledItems.has(item.id) && isCancellable(item.transfer)}
                    <TransferCancelButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {/if}
                {/snippet}
              </FileItemCard>
            {:else}
              <FolderItemCard
                path={item.path}
                sizeBytes={item.sizeBytes}
                files={filesFor(item)}
                transfer={presentationFor(item)}
              >
                {#snippet actions()}
                  {#if !cancelledItems.has(item.id) && isCancellable(item.transfer)}
                    <TransferCancelButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />
                  {/if}
                {/snippet}
                {#snippet fileActions(file)}
                  {#if !cancelledItems.has(item.id) && !cancelledFiles.has(fileCancellationKey(item.id, file.id)) && isCancellable(file.transfer)}
                    <TransferCancelButton label={`Cancel ${file.relativePath}`} onclick={() => cancelAlbumFile(item, file)} />
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
