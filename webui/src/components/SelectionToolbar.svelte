<script lang="ts">
  import Icon from './Icon.svelte';
  import { blockingKeyboardSurfaceOpen, keyboardShortcutHasModifier, keyboardTargetIsEditing, keyboardTargetUsesNativeActivation } from '../lib/keyboard';
  interface Props {
    selectedCount: number;
    floatingLabel?: string;
    detail?: string;
    actionDisabled?: boolean;
    optionsLabel?: string;
    optionsOpen?: boolean;
    optionsCustomized?: boolean;
    onclear: () => void;
    onoptions?: () => void;
    onaction?: () => void;
  }

  let {
    selectedCount,
    floatingLabel = `Download ${selectedCount}`,
    detail,
    actionDisabled = false,
    optionsLabel = 'Download options',
    optionsOpen = false,
    optionsCustomized = false,
    onclear,
    onoptions,
    onaction = () => {},
  }: Props = $props();

  function handleDownloadShortcut(event: KeyboardEvent): void {
    if (event.defaultPrevented || event.repeat || event.key !== 'Enter') return;
    if (!selectedCount || actionDisabled || keyboardShortcutHasModifier(event)) return;
    if (keyboardTargetIsEditing(event.target) || keyboardTargetUsesNativeActivation(event.target, event.key) || blockingKeyboardSurfaceOpen()) return;
    event.preventDefault();
    onaction();
  }
</script>

<svelte:window onkeydown={handleDownloadShortcut} />

{#if selectedCount}
  <div class="floating-selection-actions" aria-label="Selection actions">
    {#if detail}<span class="floating-selection-detail">{detail}</span>{/if}
    <button type="button" class="floating-deselect-action" onclick={onclear}>Deselect all</button>
    {#if onoptions}
      <button
        type="button"
        class:active={optionsOpen}
        class:customized={optionsCustomized}
        class="floating-options-action"
        aria-label={optionsLabel}
        title={optionsLabel}
        aria-expanded={optionsOpen}
        onclick={onoptions}
      >
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
        {#if optionsCustomized}<i aria-hidden="true"></i>{/if}
      </button>
    {/if}
    <button type="button" class="floating-download-action" aria-label={floatingLabel} aria-keyshortcuts="Enter" disabled={actionDisabled} onclick={onaction}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9M6.5 8.5L10 12l3.5-3.5M4 15.5h12" /></svg>
      <span>{floatingLabel}</span>
      <span class="floating-download-shortcut" aria-hidden="true"><Icon name="enter-key" /></span>
    </button>
  </div>
{/if}
