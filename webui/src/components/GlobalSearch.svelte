<script lang="ts">
  import type { SearchDraft } from '../prototype/search';
  import {
    cloneSearchConditions,
    createPrototypeSearchConditions,
    type PrototypeSearchConditions,
  } from '../prototype/search-config';
  import SearchConditionPills from './SearchConditionPills.svelte';
  import SearchConfigPanel from './SearchConfigPanel.svelte';

  interface Props {
    value: SearchDraft;
    onchange: (value: SearchDraft) => void;
    onsubmit: (value: SearchDraft, conditions: PrototypeSearchConditions) => void;
  }

  let { value, onchange, onsubmit }: Props = $props();
  let suppressAutoSplit = $state(false);
  let settingsOpen = $state(false);
  let searchEngaged = $state(false);
  let searchRoot: HTMLDivElement;
  let searchControlsRow: HTMLDivElement;
  let conditionOverlayHeight = $state(0);
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
    setSplit(query.slice(0, delimiter.index), query.slice(delimiter.index + delimiter.length));
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>('#global-search-title')?.focus());
  }

  function manualSplit(): void {
    const delimiter = findDelimiter(value.query);
    if (delimiter) setSplit(value.query.slice(0, delimiter.index), value.query.slice(delimiter.index + delimiter.length));
    else setSplit(value.query, '');
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>('#global-search-title')?.focus());
  }

  function merge(): void {
    const combined = value.artist && value.title ? `${value.artist} - ${value.title}` : value.artist || value.title;
    suppressAutoSplit = true;
    setSimple(combined);
    requestAnimationFrame(() => {
      const input = document.querySelector<HTMLInputElement>('#global-search-simple');
      input?.focus();
      input?.setSelectionRange(combined.length, combined.length);
    });
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
    if (event.key === 'Enter') onsubmit(value, cloneSearchConditions(conditions));
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
    document.querySelector<HTMLInputElement>(value.mode === 'split' ? '#global-search-artist' : '#global-search-simple')?.focus();
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
    <div class="search-entry" onfocusin={() => (searchEngaged = true)}>
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
              oninput={(event) => setSplit((event.currentTarget as HTMLInputElement).value, value.title)}
              onkeydown={(event) => { handleArtistKeydown(event); submitOnEnter(event); }}
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
              oninput={(event) => setSplit(value.artist, (event.currentTarget as HTMLInputElement).value)}
              onkeydown={(event) => { handleTitleKeydown(event); submitOnEnter(event); }}
            />
          </label>
          <button type="button" class="search-mode-button" onclick={merge}>merge</button>
        </div>
      {/if}

      {#if conditionOverlayVisible}
        <section class="search-focus-overlay" aria-label="Applied search conditions" bind:clientHeight={conditionOverlayHeight}>
          <div class="search-condition-pills">
            <SearchConditionPills mode={value.resultMode} bind:conditions />
          </div>
          <!-- Future online metadata suggestions render here beneath condition pills. -->
        </section>
      {/if}
    </div>

    <button type="button" class="search-result-mode-button" aria-label={`Switch to ${value.resultMode === 'album' ? 'track' : 'album'} search`} onclick={toggleResultMode}>
      {value.resultMode === 'album' ? 'Album' : 'Track'}
    </button>

    <button type="button" class:active={settingsOpen} class="search-settings-button" aria-label="Search configuration" aria-expanded={settingsOpen} onclick={toggleSettings}>•••</button>
  </div>

  {#if conditionOverlayVisible || settingsOpen}
    <button class="search-focus-backdrop" type="button" tabindex="-1" aria-label="Close focused search" onclick={dismissFocusedSearch}></button>
  {/if}

  {#if settingsOpen}
    <section class="search-config-popover" aria-label="Search configuration" style={`top: ${54 + 8 + (conditionOverlayVisible ? conditionOverlayHeight + 8 : 0)}px`}>
      <SearchConfigPanel mode={value.resultMode} bind:conditions onclose={() => (settingsOpen = false)} />
    </section>
  {/if}
</div>
