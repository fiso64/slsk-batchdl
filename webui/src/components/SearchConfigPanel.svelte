<script lang="ts">
  import {
    createEmptySearchConditions,
    type PrototypeSearchConditions,
  } from '../prototype/search-config';
  import type { SearchResultMode } from '../prototype/search';

  interface Props {
    mode: SearchResultMode;
    conditions: PrototypeSearchConditions;
    title?: string;
    onclose?: () => void;
  }

  const knownFormats = ['FLAC', 'MP3', 'OGG', 'OPUS', 'M4A', 'WAV'];
  const sampleRates = [
    { value: '44100', label: '44.1 kHz' },
    { value: '48000', label: '48 kHz' },
    { value: '88200', label: '88.2 kHz' },
    { value: '96000', label: '96 kHz' },
    { value: '176400', label: '176.4 kHz' },
    { value: '192000', label: '192 kHz' },
  ];
  const bitDepths = ['16', '24', '32'];

  let {
    mode,
    conditions = $bindable(),
    title = 'Search configuration',
    onclose,
  }: Props = $props();

  let formatView = $state<'buttons' | 'custom'>('buttons');
  let customFormats = $state('');
  let requiredTrackTitle = $state('');

  $effect(() => {
    if (formatView === 'buttons') customFormats = conditions.common.formats.join(', ');
  });

  function toggleFormat(format: string): void {
    conditions.common.formats = conditions.common.formats.includes(format)
      ? conditions.common.formats.filter((item) => item !== format)
      : [...conditions.common.formats, format];
    customFormats = conditions.common.formats.join(', ');
  }

  function showCustomFormats(): void {
    customFormats = conditions.common.formats.join(', ');
    formatView = 'custom';
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>('#search-config-custom-formats')?.focus());
  }

  function parseCustomFormats(): void {
    conditions.common.formats = [...new Set(
      customFormats
        .split(',')
        .map((format) => format.trim().toUpperCase())
        .filter(Boolean),
    )];
  }

  function showFormatButtons(): void {
    parseCustomFormats();
    formatView = 'buttons';
  }

  function clearConditions(): void {
    conditions = createEmptySearchConditions();
    customFormats = '';
    requiredTrackTitle = '';
  }

  function addRequiredTrackTitle(): void {
    const trackTitle = requiredTrackTitle.trim();
    if (!trackTitle || conditions.album.requiredTrackTitles.includes(trackTitle)) return;
    conditions.album.requiredTrackTitles = [...conditions.album.requiredTrackTitles, trackTitle];
    requiredTrackTitle = '';
  }

  function removeRequiredTrackTitle(trackTitle: string): void {
    conditions.album.requiredTrackTitles = conditions.album.requiredTrackTitles.filter((item) => item !== trackTitle);
  }
</script>

<header class="search-config-header">
  <div><strong>{title}</strong></div>
  {#if onclose}
    <button type="button" aria-label="Close search configuration" onclick={onclose}>×</button>
  {/if}
</header>

<div class="search-config-columns">
  <div>
    <section class="search-config-section">
      <h3>Audio quality</h3>
      <div class="config-label">Formats</div>

      {#if formatView === 'buttons'}
        <div class="format-control-row">
          <div class="format-buttons">
            {#each knownFormats as format}
              <button
                type="button"
                class:active={conditions.common.formats.includes(format)}
                onclick={() => toggleFormat(format)}
              >{format}</button>
            {/each}
          </div>
          <button type="button" class="format-view-button" onclick={showCustomFormats}>custom…</button>
        </div>
      {:else}
        <div class="custom-format-row">
          <input
            id="search-config-custom-formats"
            value={customFormats}
            placeholder="flac, mp3, aac, ape…"
            aria-label="Comma-separated formats"
            oninput={(event) => {
              customFormats = (event.currentTarget as HTMLInputElement).value;
              parseCustomFormats();
            }}
          />
          <button type="button" onclick={showFormatButtons}>buttons</button>
        </div>
      {/if}

      <div class="config-grid">
        <label>
          <span>Min bitrate <small>kbps</small></span>
          <input type="number" min="0" step="1" bind:value={conditions.common.minBitrate} placeholder="Any" />
        </label>
        <label>
          <span>Max bitrate <small>kbps</small></span>
          <input type="number" min="0" step="1" bind:value={conditions.common.maxBitrate} placeholder="Any" />
        </label>
      </div>

      <div class="config-grid">
        <label>
          <span>Sample rate</span>
          <select bind:value={conditions.common.sampleRate}>
            <option value="">Any</option>
            {#each sampleRates as rate}
              <option value={rate.value}>{rate.label}</option>
            {/each}
          </select>
        </label>
        <label>
          <span>Bit depth</span>
          <select bind:value={conditions.common.bitDepth}>
            <option value="">Any</option>
            {#each bitDepths as depth}
              <option value={depth}>{depth}-bit</option>
            {/each}
          </select>
        </label>
      </div>

      <label class="config-check"><input type="checkbox" bind:checked={conditions.common.rejectUnknownMetadata} /> Reject unknown metadata</label>
      {#if mode === 'album'}
        <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbumQuality} /> Every album track must satisfy quality</label>
      {/if}
    </section>

    <section class="search-config-section">
      <h3>Matching</h3>
      <label class="config-check"><input type="checkbox" bind:checked={conditions.common.strictArtist} /> Require artist in path</label>
      {#if mode === 'track'}
        <label class="config-check"><input type="checkbox" bind:checked={conditions.track.strictTitle} /> Require track title in filename</label>
        <div class="config-grid config-grid-spaced">
          <label>
            <span>Expected length <small>sec</small></span>
            <input type="number" min="0" bind:value={conditions.track.expectedLength} placeholder="Any" />
          </label>
          <label>
            <span>Tolerance <small>sec</small></span>
            <input type="number" min="0" bind:value={conditions.track.lengthTolerance} />
          </label>
        </div>
        <label class="config-check"><input type="checkbox" bind:checked={conditions.track.acceptNoLength} /> Accept unknown length</label>
      {:else}
        <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbum} /> Require album in folder path</label>
      {/if}
    </section>
  </div>

  <div>
    {#if mode === 'album'}
      <section class="search-config-section">
        <h3>Album structure</h3>
        <div class="config-grid">
          <label>
            <span>Min tracks</span>
            <input type="number" min="0" bind:value={conditions.album.minTrackCount} placeholder="Any" />
          </label>
          <label>
            <span>Max tracks</span>
            <input type="number" min="0" bind:value={conditions.album.maxTrackCount} placeholder="Any" />
          </label>
        </div>

        <div class="config-label config-label-spaced">Required track title</div>
        <div class="required-track-row">
          <input
            value={requiredTrackTitle}
            placeholder="e.g. Music Is Math"
            oninput={(event) => (requiredTrackTitle = (event.currentTarget as HTMLInputElement).value)}
            onkeydown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                addRequiredTrackTitle();
              }
            }}
          />
          <button type="button" onclick={addRequiredTrackTitle}>Add</button>
        </div>
        {#if conditions.album.requiredTrackTitles.length}
          <div class="required-track-pills">
            {#each conditions.album.requiredTrackTitles as trackTitle}
              <button type="button" onclick={() => removeRequiredTrackTitle(trackTitle)}>{trackTitle} ×</button>
            {/each}
          </div>
        {/if}
      </section>
    {/if}

    <section class="search-config-section">
      <h3>Peers</h3>
      <div class="config-stack">
        <label>
          <span>Allowed users</span>
          <input type="text" bind:value={conditions.common.allowedUsers} placeholder="user1, user2" />
        </label>
        <label>
          <span>Banned users</span>
          <input type="text" bind:value={conditions.common.bannedUsers} placeholder="user1, user2" />
        </label>
      </div>
    </section>
  </div>
</div>

<footer class="search-config-footer">
  <button type="button" onclick={clearConditions}>Clear conditions</button>
</footer>
