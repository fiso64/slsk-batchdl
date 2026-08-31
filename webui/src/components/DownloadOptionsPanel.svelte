<script lang="ts">
  import type { DownloadOptionCapabilities, PrototypeDownloadOptions } from '../prototype/download-options';

  interface Props {
    value: PrototypeDownloadOptions;
    capabilities: DownloadOptionCapabilities;
    compact?: boolean;
    separators?: boolean;
  }

  let { value = $bindable(), capabilities, compact = false, separators = true }: Props = $props();
</script>

<div class:compact class:seamless={!separators} class="download-options-panel">
  <section class="download-options-section">
    <h3>Behavior</h3>
    <label class="config-check"><input type="checkbox" bind:checked={value.skipExisting} /> Skip existing downloads</label>
    {#if capabilities.albumFolderEnrichment}
      <label class="config-check"><input type="checkbox" bind:checked={value.loadFullAlbumFolder} /> Load full album folder before downloading</label>
      <p class="download-option-note">Loads files the search result may have omitted. Required album verification may still retrieve a folder when necessary.</p>
    {/if}
  </section>

  <section class="download-options-section">
    <h3>Output</h3>
    <div class="download-options-fields">
      <label>
        <span>Output directory</span>
        <input type="text" bind:value={value.outputParentDir} placeholder="Inherit daemon default" />
      </label>
      {#if capabilities.nameFormat ?? true}
        <label>
          <span>Name format</span>
          <input type="text" bind:value={value.nameFormat} placeholder="Inherit daemon default" />
        </label>
      {/if}
    </div>
  </section>

  {#if capabilities.playlistOutput}
    <section class="download-options-section collection-output">
      <h3>Collection output</h3>
      <label class="config-check"><input type="checkbox" bind:checked={value.writePlaylist} /> Create .m3u playlist</label>
    </section>
  {/if}
</div>
