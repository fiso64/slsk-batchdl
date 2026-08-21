<script lang="ts">
  import TransferBulkActions, { type BulkCancelMode } from '../components/TransferBulkActions.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import MutationStatus from '../components/MutationStatus.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import TransferItemActionButton from '../components/items/TransferItemActionButton.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import type { PrototypeMutationState, ProposedBulkActionRequestDto, ProposedHistoryDeleteRequestDto } from '../prototype/backend-contracts';
  import { downloadsForScenario, type DownloadItem, type FolderDownloadItem } from '../prototype/downloads';
  import { groupAdjacentBy } from '../prototype/grouping';
  import { basename, type FolderItemFile, type TransferPresentation } from '../prototype/items';
  import { resourceStateForScenario } from '../prototype/resource-state';

  interface Props { scenario: PrototypeScenario; onopenuser: (username: string) => void; }
  let { scenario, onopenuser }: Props = $props();

  let cancelledItems = $state<Set<string>>(new Set());
  let cancelledFiles = $state<Set<string>>(new Set());
  let removedItems = $state<Set<string>>(new Set());
  let removedFiles = $state<Set<string>>(new Set());
  let bulkCancelMode = $state<BulkCancelMode>('all');
  let pageLimit = $state(10);
  let mutation = $state<PrototypeMutationState>({ phase: 'idle' });
  let resourceState = $derived(resourceStateForScenario(scenario.id, 'downloads'));
  let downloads = $derived(downloadsForScenario(scenario.id));
  let allVisibleDownloads = $derived(downloads.filter((item) => !removedItems.has(item.id)));
  let visibleDownloads = $derived(allVisibleDownloads.slice(0, pageLimit));
  let hasMore = $derived(pageLimit < allVisibleDownloads.length);
  let groups = $derived(groupAdjacentBy(visibleDownloads, (item) => item.peer, `${scenario.id}:downloads:`).map((group) => ({ key: group.key, peer: group.identity, items: group.items })));

  $effect(() => {
    scenario.id;
    cancelledItems = new Set(); cancelledFiles = new Set(); removedItems = new Set(); removedFiles = new Set();
    bulkCancelMode = 'all'; pageLimit = 10; mutation = { phase: 'idle' };
  });

  function isTerminal(transfer?: TransferPresentation): boolean { return transfer?.tone === 'complete' || transfer?.tone === 'failed' || transfer?.tone === 'cancelled'; }
  function itemCanCancel(item: DownloadItem): boolean { return !cancelledItems.has(item.id) && item.availableActions.some((action) => action.kind === 'cancel'); }
  function cancelledPresentation(transfer: TransferPresentation): TransferPresentation { return { ...transfer, state: 'Cancelled', tone: 'cancelled', cancellable: false, progressText: 'Cancelled', speed: undefined, eta: undefined }; }
  function presentationFor(item: DownloadItem): TransferPresentation {
    const transfer = { ...item.transfer, direction: 'download' as const, cancellable: itemCanCancel(item) };
    return cancelledItems.has(item.id) ? cancelledPresentation(transfer) : transfer;
  }
  function fileCancellationKey(folderId: string, fileId: string): string { return `${folderId}:${fileId}`; }
  function asFolder(item: DownloadItem): FolderDownloadItem { if ('files' in item) return item; throw new Error('Expected folder download'); }
  function filesFor(folder: FolderDownloadItem): FolderItemFile[] {
    return folder.files.filter((file) => !removedFiles.has(fileCancellationKey(folder.id, file.id))).map((file) => {
      if (!file.transfer) return file;
      const transfer = { ...file.transfer, direction: 'download' as const };
      const cancelledByFolder = cancelledItems.has(folder.id) && file.transfer.cancellable === true;
      const cancelledIndividually = cancelledFiles.has(fileCancellationKey(folder.id, file.id));
      return cancelledByFolder || cancelledIndividually ? { ...file, transfer: cancelledPresentation(transfer) } : { ...file, transfer };
    });
  }

  function cancelItem(item: DownloadItem): void {
    if (!itemCanCancel(item)) { mutation = { phase: 'rejected', label: 'Cancel unavailable' }; return; }
    mutation = { phase: 'pending', label: 'Cancelling download…' };
    cancelledItems = new Set(cancelledItems).add(item.id);
    mutation = { phase: 'succeeded', label: 'Download cancelled' };
  }
  function cancelFolderFile(folder: FolderDownloadItem, file: FolderItemFile): void {
    if (cancelledItems.has(folder.id) || file.transfer?.cancellable !== true) return;
    mutation = { phase: 'pending', label: 'Cancelling file…' };
    cancelledFiles = new Set(cancelledFiles).add(fileCancellationKey(folder.id, file.id));
    mutation = { phase: 'succeeded', label: 'File cancelled' };
  }
  function removeItem(item: DownloadItem): void {
    if (!isTerminal(presentationFor(item))) return;
    const request: ProposedHistoryDeleteRequestDto = { resourceKind: 'download-job', resourceIds: [item.id], semantics: 'archive-from-history' };
    void request;
    mutation = { phase: 'pending', label: 'Removing from history…' };
    removedItems = new Set(removedItems).add(item.id);
    mutation = { phase: 'succeeded', label: 'Removed from history' };
  }
  function removeFolderFile(folder: FolderDownloadItem, file: FolderItemFile): void {
    if (!isTerminal(file.transfer)) return;
    mutation = { phase: 'pending', label: 'Removing file history…' };
    removedFiles = new Set(removedFiles).add(fileCancellationKey(folder.id, file.id));
    mutation = { phase: 'succeeded', label: 'File removed' };
  }
  function cancelModeMatches(item: DownloadItem): boolean {
    if (!itemCanCancel(item)) return false;
    const transfer = presentationFor(item);
    if (bulkCancelMode === 'all') return true;
    if (bulkCancelMode === 'queued') return transfer.tone === 'queued';
    return transfer.tone === 'active';
  }
  function cancelBulk(): void {
    const request: ProposedBulkActionRequestDto = { direction: 'download', scope: 'current-view', action: 'cancel', filter: bulkCancelMode === 'active' ? 'in-progress' : bulkCancelMode, logicalItems: true };
    const targets = visibleDownloads.filter(cancelModeMatches);
    mutation = { phase: 'pending', label: `Cancelling ${targets.length} download${targets.length === 1 ? '' : 's'}…` };
    const next = new Set(cancelledItems); for (const item of targets) next.add(item.id); cancelledItems = next;
    mutation = { phase: targets.length ? 'succeeded' : 'rejected', label: targets.length ? 'Bulk cancel complete' : 'Nothing cancellable in this view' };
  }
  function removeCompleted(): void {
    const targets = visibleDownloads.filter((item) => isTerminal(presentationFor(item)));
    const request: ProposedBulkActionRequestDto = { direction: 'download', scope: 'current-view', action: 'archive-terminal', filter: 'terminal', logicalItems: true };
    void request;
    mutation = { phase: 'pending', label: `Removing ${targets.length} download${targets.length === 1 ? '' : 's'}…` };
    const next = new Set(removedItems); for (const item of targets) next.add(item.id); removedItems = next;
    mutation = { phase: targets.length ? 'succeeded' : 'rejected', label: targets.length ? 'Terminal history removed' : 'Nothing terminal in this view' };
  }

  let canBulkCancel = $derived(visibleDownloads.some(cancelModeMatches));
  let canRemoveCompleted = $derived(visibleDownloads.some((item) => isTerminal(presentationFor(item))));
</script>

<section class="page downloads-page">
  <header class="page-heading transfers-heading">
    <div><p class="eyebrow">Transfers</p><h1>Downloads</h1></div>
    <TransferBulkActions mode={bulkCancelMode} canCancel={canBulkCancel} {canRemoveCompleted} onmodechange={(mode) => (bulkCancelMode = mode)} oncancel={cancelBulk} onremovecompleted={removeCompleted} />
  </header>

  <div class="transfer-resource-state">{#if !resourceState.blocking}<ResourceStateNotice state={resourceState} />{/if}<MutationStatus state={mutation} /></div>

  {#if scenario.connection === 'offline'}
    <div class="empty-state"><strong>Daemon unavailable</strong><p>Current download state cannot be loaded while the daemon is offline.</p></div>
  {:else if allVisibleDownloads.length === 0}
    <div class="empty-state"><strong>No downloads</strong><p>Downloaded tracks, albums, files, and directories will appear here in creation order.</p></div>
  {:else}
    <div class="transfer-peer-list">
      {#each groups as group (group.key)}
        <PeerItemGroup peer={{ username: group.peer }} itemCount={group.items.length} itemNoun="download" {onopenuser}>
          {#each group.items as item (item.id)}
            {@const itemTransfer = presentationFor(item)}
            {#if item.kind === 'track' || item.kind === 'remote-file'}
              <FileItemCard path={item.path} sizeBytes={item.sizeBytes} audio={item.audio} transfer={itemTransfer}>
                {#snippet actions()}{#if itemCanCancel(item)}<TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => cancelItem(item)} />{:else if isTerminal(itemTransfer)}<TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => removeItem(item)} />{/if}{/snippet}
              </FileItemCard>
            {:else}
              {@const folderItem = asFolder(item)}
              {@const folderFiles = item.detailAvailability === 'summary-only' ? [] : filesFor(folderItem)}
              <FolderItemCard
                path={item.path}
                sizeBytes={item.sizeBytes}
                files={folderFiles}
                totalFileCount={folderItem.totalFileCount ?? folderItem.files.length}
                transfer={itemTransfer}
              >
                {#snippet actions()}{#if itemCanCancel(folderItem)}<TransferItemActionButton label={`Cancel ${basename(folderItem.path)}`} onclick={() => cancelItem(folderItem)} />{:else if isTerminal(itemTransfer)}<TransferItemActionButton kind="remove" label={`Remove ${basename(folderItem.path)}`} onclick={() => removeItem(folderItem)} />{/if}{/snippet}
                {#snippet fileActions(file)}{#if !cancelledItems.has(folderItem.id) && !cancelledFiles.has(fileCancellationKey(folderItem.id, file.id)) && file.transfer?.cancellable}<TransferItemActionButton label={`Cancel ${file.relativePath}`} onclick={() => cancelFolderFile(folderItem, file)} />{:else if isTerminal(file.transfer)}<TransferItemActionButton kind="remove" label={`Remove ${file.relativePath}`} onclick={() => removeFolderFile(folderItem, file)} />{/if}{/snippet}
              </FolderItemCard>
            {/if}
          {/each}
        </PeerItemGroup>
      {/each}
    </div>
    {#if hasMore}<LoadMoreButton label="Load earlier downloads" onclick={() => (pageLimit += 10)} />{/if}
  {/if}
</section>
