<script lang="ts">
  interface Props {
    selectedCount: number;
    floatingLabel?: string;
    onclear: () => void;
    onaction?: () => void;
  }

  let {
    selectedCount,
    floatingLabel = `Download ${selectedCount}`,
    onclear,
    onaction = () => {},
  }: Props = $props();
</script>

{#if selectedCount}
  <div class="floating-selection-actions" aria-label="Selection actions">
    <button type="button" class="floating-deselect-action" onclick={onclear}>Deselect all</button>
    <button type="button" class="floating-download-action" aria-label={floatingLabel} onclick={onaction}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9M6.5 8.5L10 12l3.5-3.5M4 15.5h12" /></svg>
      <span>{floatingLabel}</span>
    </button>
  </div>
{/if}
