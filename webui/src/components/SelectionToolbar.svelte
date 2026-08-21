<script lang="ts">
  interface Props {
    visibleLabel: string;
    selectedCount: number;
    allVisibleSelected: boolean;
    actionLabel?: string;
    floatingLabel?: string;
    ontogglevisible: (checked: boolean) => void;
    onselectvisible: () => void;
    onclear: () => void;
    onaction?: () => void;
  }

  let {
    visibleLabel,
    selectedCount,
    allVisibleSelected,
    actionLabel = 'Download selected',
    floatingLabel = `Download ${selectedCount}`,
    ontogglevisible,
    onselectvisible,
    onclear,
    onaction = () => {},
  }: Props = $props();
</script>

<div class="results-list-toolbar">
  <label class="select-visible-control">
    <input
      type="checkbox"
      checked={allVisibleSelected}
      onchange={(event) => ontogglevisible((event.currentTarget as HTMLInputElement).checked)}
    />
    <span>{visibleLabel}</span>
  </label>

  {#if selectedCount}
    <div class="selection-actions">
      <strong>{selectedCount} selected</strong>
      <button type="button" class="primary-selection-action" onclick={onaction}>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9M6.5 8.5L10 12l3.5-3.5M4 15.5h12" /></svg>
        {actionLabel}
      </button>
      <button type="button" class="icon-button" aria-label="Clear selection" onclick={onclear}>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg>
      </button>
    </div>
  {:else}
    <button type="button" class="quiet-action" onclick={onselectvisible}>Select visible</button>
  {/if}
</div>

{#if selectedCount}
  <button type="button" class="floating-download-action" aria-label={floatingLabel} onclick={onaction}>
    <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9M6.5 8.5L10 12l3.5-3.5M4 15.5h12" /></svg>
    <span>{floatingLabel}</span>
  </button>
{/if}
