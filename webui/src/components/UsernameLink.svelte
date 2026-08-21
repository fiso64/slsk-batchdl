<script lang="ts">
  import Icon from './Icon.svelte';
  import type { UserLinkActions } from '../prototype/navigation';

  interface Props {
    username: string;
    actions: UserLinkActions;
    title?: string;
  }

  let { username, actions, title }: Props = $props();
  let root: HTMLSpanElement;
  let open = $state(false);
  let menuStyle = $state('');

  function positionMenu(): void {
    if (typeof window === 'undefined' || !root) return;
    const rect = root.getBoundingClientRect();
    const width = 154;
    const height = 116;
    const gap = 5;
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8));
    const top = window.innerHeight - rect.bottom >= height + gap
      ? rect.bottom + gap
      : Math.max(8, rect.top - height - gap);
    menuStyle = `left:${left}px;top:${top}px`;
  }

  function toggle(event: MouseEvent): void {
    event.stopPropagation();
    if (!open) positionMenu();
    open = !open;
  }

  function choose(action: keyof UserLinkActions, event: MouseEvent): void {
    event.stopPropagation();
    open = false;
    actions[action](username);
  }

  function handleWindowPointerDown(event: PointerEvent): void {
    if (!open || root?.contains(event.target as Node)) return;
    open = false;
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') open = false;
  }
</script>

<svelte:window onpointerdown={handleWindowPointerDown} onkeydown={handleWindowKeydown} onresize={() => (open = false)} />

<span class="username-link-control" bind:this={root}>
  <button
    type="button"
    class="username-link"
    title={title ?? `Actions for ${username}`}
    aria-label={`Actions for ${username}`}
    aria-haspopup="menu"
    aria-expanded={open}
    onclick={toggle}
  >{username}</button>

  {#if open}
    <div class="username-action-menu" role="menu" style={menuStyle}>
      <button type="button" role="menuitem" onclick={(event) => choose('profile', event)}><Icon name="user" /><span>Profile</span></button>
      <button type="button" role="menuitem" onclick={(event) => choose('shares', event)}><Icon name="folder" /><span>Shares</span></button>
      <button type="button" role="menuitem" onclick={(event) => choose('message', event)}><Icon name="chat" /><span>Message</span></button>
    </div>
  {/if}
</span>
