<script lang="ts">
  import TransferBulkActions, { type BulkCancelMode } from '../components/TransferBulkActions.svelte';
  import TransferTimeline from '../components/TransferTimeline.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import MutationStatus from '../components/MutationStatus.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import type { UserLinkActions } from '../prototype/navigation';
  import type { PrototypeMutationState, ProposedBulkActionRequestDto, ProposedHistoryDeleteRequestDto } from '../prototype/backend-contracts';
  import { downloadsForScenario, type DownloadFolderEntry, type DownloadItem } from '../prototype/downloads';
  import { groupAdjacentBy } from '../prototype/grouping';
  import type { FolderItemFile, TransferPresentation } from '../prototype/items';
  import { resourceStateForScenario } from '../prototype/resource-state';
  import { isTerminalTransfer, limitTransferGroups, transferGroupItemCount, type TransferTimelineEntry, type TransferTimelineFolderEntry, type TransferTimelinePeerGroup } from '../prototype/transfers';

  interface Props { scenario: PrototypeScenario; userActions: UserLinkActions; }
  let { scenario, userActions }: Props = $props();

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

  $effect(() => {
    scenario.id;
    cancelledItems = new Set(); cancelledFiles = new Set(); removedItems = new Set(); removedFiles = new Set();
    bulkCancelMode = 'all'; pageLimit = 10; mutation = { phase: 'idle' };
  });

  function itemCanCancel(item: DownloadItem): boolean {
    return !cancelledItems.has(item.id) && item.availableActions.some((action) => action.kind === 'cancel');
  }

  function cancelledPresentation(transfer: TransferPresentation): TransferPresentation {
    return { ...transfer, state: 'Cancelled', tone: 'cancelled', cancellable: false, progressText: 'Cancelled', speed: undefined, eta: undefined };
  }

  function presentationFor(item: DownloadItem): TransferPresentation {
    const transfer = { ...item.transfer, direction: 'download' as const, cancellable: itemCanCancel(item) };
    return cancelledItems.has(item.id) ? cancelledPresentation(transfer) : transfer;
  }

  function fileCancellationKey(folderId: string, fileId: string): string { return `${folderId}:${fileId}`; }

  function filesFor(folder: DownloadFolderEntry): FolderItemFile[] {
    return folder.files
      .filter((file) => !removedFiles.has(fileCancellationKey(folder.id, file.id)))
      .map((file) => {
        if (!file.transfer) return file;
        const transfer = { ...file.transfer, direction: 'download' as const };
        const cancelledByFolder = cancelledItems.has(folder.id) && file.transfer.cancellable === true;
        const cancelledIndividually = cancelledFiles.has(fileCancellationKey(folder.id, file.id));
        return cancelledByFolder || cancelledIndividually ? { ...file, transfer: cancelledPresentation(transfer) } : { ...file, transfer };
      });
  }

  function displayItem(item: DownloadItem): TransferTimelineEntry {
    if (item.kind === 'file') return { ...item, transfer: presentationFor(item) };
    return { ...item, files: filesFor(item), transfer: presentationFor(item) };
  }

  let allGroups = $derived<TransferTimelinePeerGroup[]>(
    groupAdjacentBy(allVisibleDownloads, (item) => item.peer, `${scenario.id}:downloads:`).map((group) => ({
      key: group.key,
      peer: group.identity,
      transferCount: group.items.reduce((total, item) => total + (item.kind === 'folder' ? (item.totalFileCount ?? item.files.length) : 1), 0),
      items: group.items.map(displayItem),
    })),
  );
  let groups = $derived(limitTransferGroups(allGroups, pageLimit));
  let totalPresentationItems = $derived(transferGroupItemCount(allGroups));
  let hasMore = $derived(pageLimit < totalPresentationItems);
  let visibleIds = $derived(new Set(groups.flatMap((group) => group.items.map((item) => item.id))));
  let visibleDownloads = $derived(allVisibleDownloads.filter((item) => visibleIds.has(item.id)));

  function sourceItem(id: string): DownloadItem | undefined { return allVisibleDownloads.find((item) => item.id === id); }
  function sourceFolder(id: string): DownloadFolderEntry | undefined {
    const item = sourceItem(id);
    return item?.kind === 'folder' ? item : undefined;
  }

  function cancelItem(entry: TransferTimelineEntry): void {
    const item = sourceItem(entry.id);
    if (!item || !itemCanCancel(item)) { mutation = { phase: 'rejected', label: 'Cancel unavailable' }; return; }
    mutation = { phase: 'pending', label: 'Cancelling download…' };
    cancelledItems = new Set(cancelledItems).add(item.id);
    mutation = { phase: 'succeeded', label: 'Download cancelled' };
  }

  function cancelFolderFile(entry: TransferTimelineFolderEntry, file: FolderItemFile): void {
    const folder = sourceFolder(entry.id);
    const sourceFile = folder?.files.find((candidate) => candidate.id === file.id);
    if (!folder || !sourceFile || cancelledItems.has(folder.id) || sourceFile.transfer?.cancellable !== true) return;
    mutation = { phase: 'pending', label: 'Cancelling file…' };
    cancelledFiles = new Set(cancelledFiles).add(fileCancellationKey(folder.id, file.id));
    mutation = { phase: 'succeeded', label: 'File cancelled' };
  }

  function removeItem(entry: TransferTimelineEntry): void {
    const item = sourceItem(entry.id);
    if (!item || !isTerminalTransfer(presentationFor(item))) return;
    const request: ProposedHistoryDeleteRequestDto = { resourceKind: 'download-job', resourceIds: [item.id], semantics: 'archive-from-history' };
    void request;
    mutation = { phase: 'pending', label: 'Removing from history…' };
    removedItems = new Set(removedItems).add(item.id);
    mutation = { phase: 'succeeded', label: 'Removed from history' };
  }

  function removeFolderFile(entry: TransferTimelineFolderEntry, file: FolderItemFile): void {
    const folder = sourceFolder(entry.id);
    const sourceFile = folder?.files.find((candidate) => candidate.id === file.id);
    if (!folder || !sourceFile || !isTerminalTransfer(sourceFile.transfer)) return;
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
    const request: ProposedBulkActionRequestDto = { direction: 'download', scope: 'current-view', action: 'cancel', filter: bulkCancelMode === 'active' ? 'in-progress' : bulkCancelMode };
    void request;
    const targets = visibleDownloads.filter(cancelModeMatches);
    mutation = { phase: 'pending', label: `Cancelling ${targets.length} download${targets.length === 1 ? '' : 's'}…` };
    const next = new Set(cancelledItems); for (const item of targets) next.add(item.id); cancelledItems = next;
    mutation = { phase: targets.length ? 'succeeded' : 'rejected', label: targets.length ? 'Bulk cancel complete' : 'Nothing cancellable in this view' };
  }

  function removeCompleted(): void {
    const targets = visibleDownloads.filter((item) => isTerminalTransfer(presentationFor(item)));
    const request: ProposedBulkActionRequestDto = { direction: 'download', scope: 'current-view', action: 'archive-terminal', filter: 'terminal' };
    void request;
    mutation = { phase: 'pending', label: `Removing ${targets.length} download${targets.length === 1 ? '' : 's'}…` };
    const next = new Set(removedItems); for (const item of targets) next.add(item.id); removedItems = next;
    mutation = { phase: targets.length ? 'succeeded' : 'rejected', label: targets.length ? 'Terminal history removed' : 'Nothing terminal in this view' };
  }

  let canBulkCancel = $derived(visibleDownloads.some(cancelModeMatches));
  let canRemoveCompleted = $derived(visibleDownloads.some((item) => isTerminalTransfer(presentationFor(item))));
</script>

<section class="page downloads-page">
  <header class="page-heading transfers-heading">
    <div><p class="eyebrow">Transfers</p><h1>Downloads</h1></div>
    <TransferBulkActions mode={bulkCancelMode} canCancel={canBulkCancel} {canRemoveCompleted} onmodechange={(mode) => (bulkCancelMode = mode)} oncancel={cancelBulk} onremovecompleted={removeCompleted} />
  </header>

  <div class="transfer-resource-state">{#if !resourceState.blocking}<ResourceStateNotice state={resourceState} />{/if}<MutationStatus state={mutation} /></div>

  {#if resourceState.blocking}
    <ResourceStateNotice state={resourceState} />
  {:else if allVisibleDownloads.length === 0}
    <div class="empty-state"><strong>No downloads</strong><p>Downloaded files and folders will appear here in creation order.</p></div>
  {:else}
    <TransferTimeline
      {groups}
      itemNoun="download"
      {userActions}
      oncancelitem={cancelItem}
      onremoveitem={removeItem}
      oncancelfile={cancelFolderFile}
      onremovefile={removeFolderFile}
    />
    {#if hasMore}<LoadMoreButton label="Load earlier downloads" onclick={() => (pageLimit += 10)} />{/if}
  {/if}
</section>
