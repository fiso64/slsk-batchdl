<script lang="ts">
  import type { UserLinkActions } from '../prototype/navigation';
  import { basename, type FolderItemFile } from '../prototype/items';
  import { isTerminalTransfer, type TransferTimelineEntry, type TransferTimelineFolderEntry, type TransferTimelinePeerGroup } from '../prototype/transfers';
  import FileItemCard from './items/FileItemCard.svelte';
  import FolderItemCard from './items/FolderItemCard.svelte';
  import PeerItemGroup from './items/PeerItemGroup.svelte';
  import TransferItemActionButton from './items/TransferItemActionButton.svelte';

  interface Props {
    groups: TransferTimelinePeerGroup[];
    itemNoun: string;
    userActions: UserLinkActions;
    oncancelitem: (item: TransferTimelineEntry) => void;
    onremoveitem: (item: TransferTimelineEntry) => void;
    oncancelfile: (folder: TransferTimelineFolderEntry, file: FolderItemFile) => void;
    onremovefile: (folder: TransferTimelineFolderEntry, file: FolderItemFile) => void;
  }

  let { groups, itemNoun, userActions, oncancelitem, onremoveitem, oncancelfile, onremovefile }: Props = $props();
</script>

<div class="transfer-peer-list">
  {#each groups as group (group.key)}
    <PeerItemGroup peer={{ username: group.peer }} itemCount={group.transferCount} {itemNoun} {userActions}>
      {#each group.items as item (item.id)}
        {#if item.kind === 'file'}
          <FileItemCard path={item.path} sizeBytes={item.sizeBytes} audio={item.audio} transfer={item.transfer}>
            {#snippet actions()}
              {#if item.transfer.cancellable}
                <TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => oncancelitem(item)} />
              {:else if isTerminalTransfer(item.transfer)}
                <TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => onremoveitem(item)} />
              {/if}
            {/snippet}
          </FileItemCard>
        {:else}
          <FolderItemCard path={item.path} sizeBytes={item.sizeBytes} files={item.files} totalFileCount={item.totalFileCount ?? item.files.length} transfer={item.transfer}>
            {#snippet actions()}
              {#if item.transfer.cancellable}
                <TransferItemActionButton label={`Cancel ${basename(item.path)}`} onclick={() => oncancelitem(item)} />
              {:else if isTerminalTransfer(item.transfer)}
                <TransferItemActionButton kind="remove" label={`Remove ${basename(item.path)}`} onclick={() => onremoveitem(item)} />
              {/if}
            {/snippet}
            {#snippet fileActions(file)}
              {#if file.transfer?.cancellable}
                <TransferItemActionButton label={`Cancel ${file.relativePath}`} onclick={() => oncancelfile(item, file)} />
              {:else if isTerminalTransfer(file.transfer)}
                <TransferItemActionButton kind="remove" label={`Remove ${file.relativePath}`} onclick={() => onremovefile(item, file)} />
              {/if}
            {/snippet}
          </FolderItemCard>
        {/if}
      {/each}
    </PeerItemGroup>
  {/each}
</div>
