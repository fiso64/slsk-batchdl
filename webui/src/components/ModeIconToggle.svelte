<script lang="ts">
  import Icon from './Icon.svelte';
  import type { AppIconName } from '../prototype/icons';

  export type ModeIconOption = {
    value: string;
    label: string;
    icon: AppIconName;
  };

  interface Props {
    value: string;
    options: ModeIconOption[];
    ariaLabel: string;
    onchange: (value: string) => void;
  }

  let { value, options, ariaLabel, onchange }: Props = $props();
  let menuOpen = $state(false);
  let root: HTMLDivElement;

  let current = $derived(options.find((option) => option.value === value) ?? options[0]);

  function choose(nextValue: string): void {
    onchange(nextValue);
    menuOpen = false;
  }

  function toggleMenu(): void {
    menuOpen = !menuOpen;
  }

  function handleWindowPointerDown(event: PointerEvent): void {
    if (!menuOpen) return;
    const target = event.target as Node | null;
    if (target && root?.contains(target)) return;
    menuOpen = false;
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && menuOpen) menuOpen = false;
  }
</script>

<svelte:window onpointerdown={handleWindowPointerDown} onkeydown={handleWindowKeydown} />

<div class="mode-icon-toggle" bind:this={root}>
  <button
    type="button"
    class="search-result-mode-button mode-icon-button"
    aria-label={`${ariaLabel}: ${current?.label ?? value}. Choose mode.`}
    aria-haspopup="menu"
    aria-expanded={menuOpen}
    title={`${current?.label ?? value} · choose mode`}
    onpointerdown={(event) => event.preventDefault()}
    onclick={toggleMenu}
  >
    {#if current}
      <Icon name={current.icon} />
    {/if}
  </button>

  {#if menuOpen}
    <div class="mode-icon-menu" role="menu" aria-label={ariaLabel}>
      {#each options as option}
        <button
          type="button"
          role="menuitemradio"
          aria-checked={option.value === value}
          class:active={option.value === value}
          onclick={() => choose(option.value)}
        >
          <span class="mode-menu-check" aria-hidden="true">{option.value === value ? '✓' : ''}</span>
          <span class="mode-menu-icon" aria-hidden="true"><Icon name={option.icon} /></span>
          <span>{option.label}</span>
        </button>
      {/each}
    </div>
  {/if}
</div>
