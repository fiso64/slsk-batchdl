<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { ItemPeerInfo } from '../../prototype/items';
  import type { UserLinkActions } from '../../prototype/navigation';
  import UsernameLink from '../UsernameLink.svelte';

  interface Props {
    peer: ItemPeerInfo;
    itemCount: number;
    itemNoun?: string;
    itemNounPlural?: string;
    userActions: UserLinkActions;
    children: Snippet;
  }

  let {
    peer,
    itemCount,
    itemNoun = 'result',
    itemNounPlural = `${itemNoun}s`,
    userActions,
    children,
  }: Props = $props();

  let collapsed = $state(false);
  let hasPeerStats = $derived(
    peer.freeUploadSlot !== undefined ||
    peer.uploadSpeedMbps !== undefined ||
    peer.queueLength !== undefined,
  );
</script>

<section class="result-peer-group peer-item-group">
  <div class:compact={!hasPeerStats} class="result-peer-header">
    <button
      type="button"
      class="peer-header-toggle"
      aria-label={`${collapsed ? 'Expand' : 'Collapse'} ${peer.username} ${itemNounPlural}`}
      aria-expanded={!collapsed}
      onclick={() => { collapsed = !collapsed; }}
    ></button>
    <svg class:collapsed class="peer-chevron" viewBox="0 0 20 20" aria-hidden="true"><path d="M7 5l6 5-6 5" /></svg>
    <span class="peer-identity">
      <UsernameLink username={peer.username} actions={userActions} />
      {#if peer.freeUploadSlot !== undefined}
        <span class:available={peer.freeUploadSlot} class="peer-slot"><i></i>{peer.freeUploadSlot ? 'Free slot' : 'No free slot'}</span>
      {/if}
    </span>
    {#if peer.uploadSpeedMbps !== undefined}
      <span class="peer-stat peer-upload-stat"><b>{peer.uploadSpeedMbps.toFixed(1)} MB/s</b><small>upload</small></span>
    {/if}
    {#if peer.queueLength !== undefined}
      <span class="peer-stat"><b>{peer.queueLength}</b><small>queued</small></span>
    {/if}
    <span class="peer-result-count">{itemCount} {itemCount === 1 ? itemNoun : itemNounPlural}</span>
  </div>

  {#if !collapsed}
    <div class="result-peer-items">
      {@render children()}
    </div>
  {/if}
</section>
