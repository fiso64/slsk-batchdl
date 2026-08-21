<script lang="ts">
  import type { AppIconName } from '../prototype/icons';

  interface Props {
    name: AppIconName;
    class?: string;
  }

  let { name, class: className = '' }: Props = $props();
</script>

<svg class={`app-icon ${className}`} viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
  {#if name === 'dashboard'}
    <rect x="4" y="4" width="6" height="6" rx="1" />
    <rect x="14" y="4" width="6" height="6" rx="1" />
    <rect x="4" y="14" width="6" height="6" rx="1" />
    <rect x="14" y="14" width="6" height="6" rx="1" />
  {:else if name === 'search'}
    <circle cx="10.5" cy="10.5" r="5.5" />
    <path d="m15 15 4.5 4.5" />
  {:else if name === 'user'}
    <circle cx="12" cy="8.5" r="3.2" />
    <path d="M5.8 20a6.2 6.2 0 0 1 12.4 0" />
  {:else if name === 'folder'}
    <path d="M3.5 7h6l1.8 2h9.2v8.5A2.5 2.5 0 0 1 18 20H6a2.5 2.5 0 0 1-2.5-2.5z" />
    <path d="M3.5 7V5.5A1.5 1.5 0 0 1 5 4h4l1.5 1.5" />
  {:else if name === 'file'}
    <path d="M7 3.5h6l4 4V20H7z" />
    <path d="M13 3.5V8h4" />
  {:else if name === 'download'}
    <path d="M12 4v11" />
    <path d="m7.5 10.5 4.5 4.5 4.5-4.5" />
    <path d="M5 20h14" />
  {:else if name === 'upload'}
    <path d="M12 20V9" />
    <path d="m7.5 13.5 4.5-4.5 4.5 4.5" />
    <path d="M5 4h14" />
  {:else if name === 'chat'}
    <path d="M5 6.5A3.5 3.5 0 0 1 8.5 3h7A3.5 3.5 0 0 1 19 6.5v5a3.5 3.5 0 0 1-3.5 3.5H11l-4.5 3v-3.2A3.5 3.5 0 0 1 5 11.5z" />
  {:else if name === 'settings'}
    <circle cx="12" cy="12" r="3" />
    <path d="M19 12a7.5 7.5 0 0 0-.1-1l2-1.5-2-3.4-2.5 1a8 8 0 0 0-1.8-1L14.2 3h-4.4l-.4 3.1a8 8 0 0 0-1.8 1l-2.5-1-2 3.4 2 1.5a7.5 7.5 0 0 0 0 2l-2 1.5 2 3.4 2.5-1a8 8 0 0 0 1.8 1l.4 3.1h4.4l.4-3.1a8 8 0 0 0 1.8-1l2.5 1 2-3.4-2-1.5a7.5 7.5 0 0 0 .1-1z" />
  {:else if name === 'trash'}
    <path d="M5 7h14" />
    <path d="M9 7V4.5h6V7" />
    <path d="M7 7l.8 12h8.4L17 7" />
    <path d="M10 10.5v5M14 10.5v5" />
  {:else if name === 'check'}
    <path d="m5.5 12.5 4 4 9-9" />
  {:else if name === 'clock'}
    <circle cx="12" cy="12" r="8" />
    <path d="M12 7.5V12l3 2" />
  {:else if name === 'x'}
    <path d="m7.5 7.5 9 9" />
    <path d="m16.5 7.5-9 9" />
  {:else if name === 'more'}
    <circle cx="5" cy="12" r="1" fill="currentColor" stroke="none" />
    <circle cx="12" cy="12" r="1" fill="currentColor" stroke="none" />
    <circle cx="19" cy="12" r="1" fill="currentColor" stroke="none" />
  {:else if name === 'track'}
    <path d="M9 17.5V6.5l9-2v10.8" />
    <circle cx="6.5" cy="17.5" r="2.5" />
    <circle cx="15.5" cy="15.5" r="2.5" />
  {:else if name === 'album'}
    <rect x="4" y="4" width="16" height="16" rx="2.5" />
    <circle cx="12" cy="12" r="4.2" />
    <circle cx="12" cy="12" r="1" />
  {/if}
</svg>
