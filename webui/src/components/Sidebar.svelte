<script lang="ts">
  import { navigationItems, type PageId } from '../prototype/navigation';
  import { displayCountDefinitions } from '../prototype/backend-contracts';
  import Icon from './Icon.svelte';

  interface Props {
    activePage: PageId;
    downloadCount: number;
    uploadCount: number;
    unreadChats: number;
    onnavigate: (page: PageId) => void;
  }

  let { activePage, downloadCount, uploadCount, unreadChats, onnavigate }: Props = $props();
  let primaryItems = navigationItems.filter((item) => item.placement === 'primary');

  function countFor(id: PageId): number | null {
    if (id === 'downloads') return downloadCount;
    if (id === 'uploads') return uploadCount;
    if (id === 'chat') return unreadChats;
    return null;
  }

  function countTitle(id: PageId): string | undefined {
    if (id === 'downloads') return displayCountDefinitions.downloadsSidebar.label;
    if (id === 'uploads') return displayCountDefinitions.uploadsSidebar.label;
    if (id === 'chat') return displayCountDefinitions.chatSidebar.label;
    return undefined;
  }

</script>

<nav class="sidebar-nav" aria-label="Primary navigation">
  {#each primaryItems as item}
    {@const count = countFor(item.id)}
    <button
      type="button"
      class:active={item.id === activePage}
      aria-current={item.id === activePage ? 'page' : undefined}
      onclick={() => onnavigate(item.id)}
    >
      <span class="nav-icon" aria-hidden="true"><Icon name={item.icon} /></span>
      <span class="nav-label">{item.label}</span>
      {#if count !== null && count > 0}
        <span class:unread={item.id === 'chat'} class="nav-count" title={countTitle(item.id)}>{count}</span>
      {/if}
    </button>
  {/each}
</nav>
