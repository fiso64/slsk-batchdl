<script lang="ts">
  import type { SearchDraft } from '../prototype/search';
  import {
    createEmptySearchConditions,
    createPrototypeSearchConditions,
    type PrototypeSearchConditions,
  } from '../prototype/search-config';

  interface Props {
    value: SearchDraft;
    onchange: (value: SearchDraft) => void;
    onsubmit: (value: SearchDraft) => void;
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

  let { value, onchange, onsubmit }: Props = $props();
  let suppressAutoSplit = $state(false);
  let settingsOpen = $state(false);
  let searchEngaged = $state(false);
  let searchRoot: HTMLDivElement;
  let searchControlsRow: HTMLDivElement;
  let conditionOverlayHeight = $state(0);
  let formatView = $state<'buttons' | 'custom'>('buttons');
  let customFormats = $state('FLAC');
  let requiredTrackTitle = $state('');
  let conditions = $state<PrototypeSearchConditions>(createPrototypeSearchConditions());

  function findDelimiter(input: string): { index: number; length: number } | null {
    const candidates = [' — ', ' - ']
      .map((delimiter) => ({ index: input.indexOf(delimiter), length: delimiter.length }))
      .filter((candidate) => candidate.index >= 0)
      .sort((a, b) => a.index - b.index);

    return candidates[0] ?? null;
  }

  function setSimple(query: string): void {
    onchange({ ...value, mode: 'simple', query, artist: '', title: '' });
  }

  function setSplit(artist: string, title: string): void {
    onchange({ ...value, mode: 'split', query: '', artist, title });
  }

  function toggleResultMode(): void {
    onchange({ ...value, resultMode: value.resultMode === 'album' ? 'track' : 'album' });
  }

  function toggleSettings(): void {
    searchEngaged = true;
    settingsOpen = !settingsOpen;
  }

  function handleSimpleInput(event: Event): void {
    const query = (event.currentTarget as HTMLInputElement).value;
    const delimiter = findDelimiter(query);

    if (!delimiter) {
      suppressAutoSplit = false;
      setSimple(query);
      return;
    }

    if (suppressAutoSplit) {
      setSimple(query);
      return;
    }

    const artist = query.slice(0, delimiter.index);
    const title = query.slice(delimiter.index + delimiter.length);
    setSplit(artist, title);
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>('#global-search-title')?.focus());
  }

  function manualSplit(): void {
    const delimiter = findDelimiter(value.query);
    if (delimiter) {
      setSplit(
        value.query.slice(0, delimiter.index),
        value.query.slice(delimiter.index + delimiter.length),
      );
    } else {
      setSplit(value.query, '');
    }

    requestAnimationFrame(() => document.querySelector<HTMLInputElement>('#global-search-title')?.focus());
  }

  function merge(): void {
    const combined = value.artist && value.title
      ? `${value.artist} - ${value.title}`
      : value.artist || value.title;

    suppressAutoSplit = true;
    setSimple(combined);
    requestAnimationFrame(() => {
      const input = document.querySelector<HTMLInputElement>('#global-search-simple');
      if (!input) return;
      input.focus();
      input.setSelectionRange(combined.length, combined.length);
    });
  }

  function handleArtistInput(event: Event): void {
    setSplit((event.currentTarget as HTMLInputElement).value, value.title);
  }

  function handleTitleInput(event: Event): void {
    setSplit(value.artist, (event.currentTarget as HTMLInputElement).value);
  }

  function handleArtistKeydown(event: KeyboardEvent): void {
    if (event.key === 'Backspace' && value.artist === '') {
      event.preventDefault();
      merge();
    }
  }

  function handleTitleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Backspace' && value.title === '') {
      event.preventDefault();
      document.querySelector<HTMLInputElement>('#global-search-artist')?.focus();
    }
  }

  function submitOnEnter(event: KeyboardEvent): void {
    if (event.key === 'Enter') onsubmit(value);
  }

  function focusSearch(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    const editing = target?.matches('input, textarea, select, [contenteditable="true"]');

    if (event.key === 'Escape' && searchEngaged && searchControlsRow?.contains(document.activeElement)) {
      event.preventDefault();
      dismissFocusedSearch();
      return;
    }

    if (event.key !== '/' || editing) return;

    event.preventDefault();
    const selector = value.mode === 'split' ? '#global-search-artist' : '#global-search-simple';
    document.querySelector<HTMLInputElement>(selector)?.focus();
  }

  function toggleFormat(format: string): void {
    conditions.common.formats = conditions.common.formats.includes(format)
      ? conditions.common.formats.filter((item) => item !== format)
      : [...conditions.common.formats, format];
    syncCustomFormats();
  }

  function syncCustomFormats(): void {
    customFormats = conditions.common.formats.join(', ');
  }

  function showCustomFormats(): void {
    syncCustomFormats();
    formatView = 'custom';
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>('#custom-formats')?.focus());
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

  function sampleRateLabel(value: string): string {
    return sampleRates.find((option) => option.value === value)?.label ?? value;
  }

  function removeFormat(format: string): void {
    conditions.common.formats = conditions.common.formats.filter((item) => item !== format);
    syncCustomFormats();
  }

  function clearConditions(): void {
    conditions = createEmptySearchConditions();
    customFormats = '';
    requiredTrackTitle = '';
  }

  function addRequiredTrackTitle(): void {
    const title = requiredTrackTitle.trim();
    if (!title || conditions.album.requiredTrackTitles.includes(title)) return;
    conditions.album.requiredTrackTitles = [...conditions.album.requiredTrackTitles, title];
    requiredTrackTitle = '';
  }

  function removeRequiredTrackTitle(title: string): void {
    conditions.album.requiredTrackTitles = conditions.album.requiredTrackTitles.filter((item) => item !== title);
  }


  let hasAppliedConditions = $derived.by(() => Boolean(
    conditions.common.formats.length
      || conditions.common.minBitrate
      || conditions.common.maxBitrate
      || conditions.common.sampleRate
      || conditions.common.bitDepth
      || conditions.common.strictArtist
      || conditions.common.rejectUnknownMetadata
      || conditions.common.allowedUsers.trim()
      || conditions.common.bannedUsers.trim()
      || (value.resultMode === 'track'
        ? conditions.track.strictTitle || conditions.track.expectedLength
        : conditions.album.strictAlbum
          || conditions.album.minTrackCount
          || conditions.album.maxTrackCount
          || conditions.album.requiredTrackTitles.length
          || conditions.album.strictAlbumQuality),
  ));

  let conditionOverlayVisible = $derived((searchEngaged || settingsOpen) && hasAppliedConditions);

  function handleSearchFocusIn(): void {
    searchEngaged = true;
  }

  function handleWindowPointerDown(event: PointerEvent): void {
    if (!searchEngaged && !settingsOpen) return;

    const target = event.target as Element | null;
    if (!target) return;

    if (target.closest('.search-focus-backdrop')) {
      dismissFocusedSearch();
      return;
    }

    if (searchRoot?.contains(target)) return;
    dismissFocusedSearch();
  }

  function dismissFocusedSearch(): void {
    searchEngaged = false;
    settingsOpen = false;
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  }
</script>

<svelte:window onkeydown={focusSearch} onpointerdown={handleWindowPointerDown} />

<div class="global-search" aria-label="Global Sockseek search" bind:this={searchRoot}>
  <div class="search-controls-row" bind:this={searchControlsRow}>
    <div class="search-entry" onfocusin={handleSearchFocusIn}>
      {#if value.mode === 'simple'}
        <div class="search-bar simple">
          <span class="search-glyph" aria-hidden="true">⌕</span>
          <input
            id="global-search-simple"
            value={value.query}
            placeholder="Search Soulseek…"
            autocomplete="off"
            spellcheck="false"
            aria-label="Search Soulseek"
            oninput={handleSimpleInput}
            onkeydown={submitOnEnter}
          />
          <span class="search-shortcut" aria-hidden="true">/</span>
          <button type="button" class="search-mode-button" onclick={manualSplit}>split</button>
        </div>
      {:else}
        <div class="search-bar split">
          <label class="search-field">
            <span>artist</span>
            <input
              id="global-search-artist"
              value={value.artist}
              placeholder="artist…"
              autocomplete="off"
              spellcheck="false"
              oninput={handleArtistInput}
              onkeydown={(event) => {
                handleArtistKeydown(event);
                submitOnEnter(event);
              }}
            />
          </label>
          <span class="search-divider" aria-hidden="true"></span>
          <label class="search-field">
            <span>{value.resultMode === 'album' ? 'album' : 'track'}</span>
            <input
              id="global-search-title"
              value={value.title}
              placeholder={value.resultMode === 'album' ? 'album…' : 'track…'}
              autocomplete="off"
              spellcheck="false"
              oninput={handleTitleInput}
              onkeydown={(event) => {
                handleTitleKeydown(event);
                submitOnEnter(event);
              }}
            />
          </label>
          <button type="button" class="search-mode-button" onclick={merge}>merge</button>
        </div>
      {/if}

      {#if conditionOverlayVisible}
        <section
          class="search-focus-overlay"
          aria-label="Applied search conditions"
          bind:clientHeight={conditionOverlayHeight}
        >
            <div class="search-condition-pills">
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
                <span class="search-condition-pill">sample rate: {sampleRateLabel(conditions.common.sampleRate)}<button type="button" onclick={() => (conditions.common.sampleRate = '')}>×</button></span>
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

              {#if value.resultMode === 'track'}
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
                  <span class="search-condition-pill">contains: {title}<button type="button" onclick={() => removeRequiredTrackTitle(title)}>×</button></span>
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
            </div>

          <!-- Future online metadata suggestions (for example MusicBrainz album/track matches)
               will render here beneath the condition pills when that backend capability exists. -->
        </section>
      {/if}
    </div>

    <button
      type="button"
      class="search-result-mode-button"
      aria-label={`Switch to ${value.resultMode === 'album' ? 'track' : 'album'} search`}
      onclick={toggleResultMode}
    >
      {value.resultMode === 'album' ? 'Album' : 'Track'}
    </button>

    <button
      type="button"
      class:active={settingsOpen}
      class="search-settings-button"
      aria-label="Search configuration"
      aria-expanded={settingsOpen}
      onclick={toggleSettings}
    >•••</button>
  </div>

  {#if conditionOverlayVisible || settingsOpen}
    <button
      class="search-focus-backdrop"
      type="button"
      tabindex="-1"
      aria-label="Close focused search"
      onclick={dismissFocusedSearch}
    ></button>
  {/if}

  {#if settingsOpen}
    <section
      class="search-config-popover"
      aria-label="Search configuration"
      style={`top: ${54 + 8 + (conditionOverlayVisible ? conditionOverlayHeight + 8 : 0)}px`}
    >
      <header class="search-config-header">
        <div>
          <strong>Search configuration</strong>
        </div>
        <button type="button" aria-label="Close search configuration" onclick={() => (settingsOpen = false)}>×</button>
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
                  id="custom-formats"
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
            {#if value.resultMode === 'album'}
              <label class="config-check"><input type="checkbox" bind:checked={conditions.album.strictAlbumQuality} /> Every album track must satisfy quality</label>
            {/if}
          </section>

          <section class="search-config-section">
            <h3>Matching</h3>
            <label class="config-check"><input type="checkbox" bind:checked={conditions.common.strictArtist} /> Require artist in path</label>
            {#if value.resultMode === 'track'}
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
          {#if value.resultMode === 'album'}
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
                  {#each conditions.album.requiredTrackTitles as title}
                    <button type="button" onclick={() => removeRequiredTrackTitle(title)}>{title} ×</button>
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
    </section>
  {/if}
</div>
