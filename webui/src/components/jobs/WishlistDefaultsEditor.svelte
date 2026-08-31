<script lang="ts">
  import ExpandableSettingsPanel from '../ExpandableSettingsPanel.svelte';
  import SearchConfigPanel from '../SearchConfigPanel.svelte';
  import {
    downloadOptionsCustomized,
    importOptionsCustomized,
    type DownloadOptionCapabilities,
  } from '../../prototype/download-options';
  import { searchFilteringCustomized, searchRankingCustomized } from '../../prototype/search-config';
  import type { WishlistDefaults } from '../../prototype/wishlists';

  interface Props {
    value: WishlistDefaults;
  }

  let { value = $bindable() }: Props = $props();
  let importOpen = $state(false);
  let filteringOpen = $state(false);
  let rankingOpen = $state(false);
  let downloadOpen = $state(false);

  const downloadCapabilities: DownloadOptionCapabilities = {
    albumFolderEnrichment: true,
    playlistOutput: true,
    nameFormat: true,
  };

  let importChanged = $derived(importOptionsCustomized(value.importOptions));
  let filteringChanged = $derived(searchFilteringCustomized(value.conditions, 'track'));
  let rankingChanged = $derived(searchRankingCustomized(value.conditions, 'track'));
  let downloadChanged = $derived(downloadOptionsCustomized(value.downloadOptions, downloadCapabilities));
</script>

<div class="wishlist-default-settings">
  <ExpandableSettingsPanel title="Import" summary={importChanged ? 'Custom' : 'Daemon default'} customized={importChanged} bind:open={importOpen} bodyId="wishlist-default-import">
    <div class="new-job-import-options-body">
      <div class="config-grid">
        <label><span>Limit items</span><input type="number" min="1" step="1" bind:value={value.importOptions.maxTracks} placeholder="All" /></label>
        <label><span>Offset</span><input type="number" min="0" step="1" bind:value={value.importOptions.offset} /></label>
      </div>
      <label class="config-check"><input type="checkbox" bind:checked={value.importOptions.upgradeToAlbum} /> Upgrade song items to albums</label>
      <p class="wishlist-default-note">Applied only to sources that support collection import options.</p>
    </div>
  </ExpandableSettingsPanel>

  <ExpandableSettingsPanel title="Filtering" summary={filteringChanged ? 'Custom' : 'Daemon default'} customized={filteringChanged} bind:open={filteringOpen} bodyId="wishlist-default-filtering">
    <SearchConfigPanel mode="track" configurationFamily="mixed" conditions={value.conditions} section="conditions" embedded />
  </ExpandableSettingsPanel>

  <ExpandableSettingsPanel title="Ranking" summary={rankingChanged ? 'Custom' : 'Daemon default'} customized={rankingChanged} bind:open={rankingOpen} bodyId="wishlist-default-ranking">
    <SearchConfigPanel mode="track" configurationFamily="mixed" conditions={value.conditions} section="ranking" embedded />
  </ExpandableSettingsPanel>

  <ExpandableSettingsPanel title="Download" summary={downloadChanged ? 'Custom' : 'Daemon default'} customized={downloadChanged} bind:open={downloadOpen} bodyId="wishlist-default-download">
    <SearchConfigPanel
      mode="track"
      configurationFamily="mixed"
      conditions={value.conditions}
      downloadOptions={value.downloadOptions}
      {downloadCapabilities}
      section="download"
      embedded
    />
  </ExpandableSettingsPanel>
</div>
