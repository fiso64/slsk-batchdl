<script lang="ts">
  import type { PrototypeSearchConditions } from '../prototype/search-config';
  import type { SearchResultMode } from '../prototype/search';

  interface Props {
    mode: SearchResultMode;
    conditions: PrototypeSearchConditions;
  }

  const sampleRates: Record<string, string> = {
    '44100': '44.1 kHz',
    '48000': '48 kHz',
    '88200': '88.2 kHz',
    '96000': '96 kHz',
    '176400': '176.4 kHz',
    '192000': '192 kHz',
  };

  let { mode, conditions = $bindable() }: Props = $props();

  function removeFormat(format: string): void {
    conditions.common.formats = conditions.common.formats.filter((item) => item !== format);
  }
</script>

{#each conditions.common.formats as format}
  <span class="search-condition-pill">format: {format}<button type="button" aria-label={`Remove ${format} format`} onclick={() => removeFormat(format)}>×</button></span>
{/each}
{#if conditions.common.minBitrate}
  <span class="search-condition-pill">bitrate ≥ {conditions.common.minBitrate} kbps<button type="button" onclick={() => (conditions.common.minBitrate = '')}>×</button></span>
{/if}
{#if conditions.common.maxBitrate}
  <span class="search-condition-pill">bitrate ≤ {conditions.common.maxBitrate} kbps<button type="button" onclick={() => (conditions.common.maxBitrate = '')}>×</button></span>
{/if}
{#if conditions.common.sampleRate}
  <span class="search-condition-pill">sample rate: {sampleRates[conditions.common.sampleRate] ?? conditions.common.sampleRate}<button type="button" onclick={() => (conditions.common.sampleRate = '')}>×</button></span>
{/if}
{#if conditions.common.bitDepth}
  <span class="search-condition-pill">bit depth: {conditions.common.bitDepth}-bit<button type="button" onclick={() => (conditions.common.bitDepth = '')}>×</button></span>
{/if}
{#if conditions.common.strictArtist}
  <span class="search-condition-pill">strict artist<button type="button" onclick={() => (conditions.common.strictArtist = false)}>×</button></span>
{/if}
{#if conditions.common.rejectUnknownMetadata}
  <span class="search-condition-pill">reject unknown metadata<button type="button" onclick={() => (conditions.common.rejectUnknownMetadata = false)}>×</button></span>
{/if}

{#if mode === 'track'}
  {#if conditions.track.strictTitle}
    <span class="search-condition-pill">strict title<button type="button" onclick={() => (conditions.track.strictTitle = false)}>×</button></span>
  {/if}
  {#if conditions.track.expectedLength}
    <span class="search-condition-pill">length {conditions.track.expectedLength}s ±{conditions.track.lengthTolerance || '0'}s<button type="button" onclick={() => (conditions.track.expectedLength = '')}>×</button></span>
  {/if}
{:else}
  {#if conditions.album.strictAlbum}
    <span class="search-condition-pill">strict album<button type="button" onclick={() => (conditions.album.strictAlbum = false)}>×</button></span>
  {/if}
  {#if conditions.album.minTrackCount}
    <span class="search-condition-pill">tracks ≥ {conditions.album.minTrackCount}<button type="button" onclick={() => (conditions.album.minTrackCount = '')}>×</button></span>
  {/if}
  {#if conditions.album.maxTrackCount}
    <span class="search-condition-pill">tracks ≤ {conditions.album.maxTrackCount}<button type="button" onclick={() => (conditions.album.maxTrackCount = '')}>×</button></span>
  {/if}
  {#each conditions.album.requiredTrackTitles as title}
    <span class="search-condition-pill">contains: {title}<button type="button" onclick={() => (conditions.album.requiredTrackTitles = conditions.album.requiredTrackTitles.filter((item) => item !== title))}>×</button></span>
  {/each}
  {#if conditions.album.strictAlbumQuality}
    <span class="search-condition-pill">all tracks meet quality<button type="button" onclick={() => (conditions.album.strictAlbumQuality = false)}>×</button></span>
  {/if}
{/if}

{#if conditions.common.allowedUsers.trim()}
  <span class="search-condition-pill">allow: {conditions.common.allowedUsers.trim()}<button type="button" onclick={() => (conditions.common.allowedUsers = '')}>×</button></span>
{/if}
{#if conditions.common.bannedUsers.trim()}
  <span class="search-condition-pill">ban: {conditions.common.bannedUsers.trim()}<button type="button" onclick={() => (conditions.common.bannedUsers = '')}>×</button></span>
{/if}
