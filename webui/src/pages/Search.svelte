<script lang="ts">
  import SearchConditionPills from '../components/SearchConditionPills.svelte';
  import SearchConfigPanel from '../components/SearchConfigPanel.svelte';
  import type { PrototypeSearchConditions } from '../prototype/search-config';
  import type { SearchDraft } from '../prototype/search';
  import {
    albumResults,
    trackResults,
    type AlbumFileResult,
    type AlbumSearchResult,
    type AudioAttributes,
    type ProjectedSearchResult,
    type SearchRecord,
    type SearchSort,
    type SearchView,
    type SizeSortDirection,
    type TrackSearchResult,
  } from '../prototype/search-results';

  interface Props {
    search: SearchDraft;
    searches: SearchRecord[];
    view: SearchView;
    activeSearchId: string | null;
    onusequery: (search: SearchDraft) => void;
  }

  let {
    search,
    searches = $bindable(),
    view = $bindable(),
    activeSearchId = $bindable(),
    onusequery,
  }: Props = $props();

  let filterText = $state('');
  let sort = $state<SearchSort>('relevance');
  let sizeDirection = $state<SizeSortDirection>('desc');
  let selected = $state<Set<string>>(new Set());
  let collapsedPeers = $state<Set<string>>(new Set());
  let conditionsOpen = $state(false);

  let activeRecord = $derived(searches.find((item) => item.id === activeSearchId) ?? null);
  let activeMode = $derived(activeRecord?.draft.resultMode ?? search.resultMode);

  function openSearch(record: SearchRecord): void {
    activeSearchId = record.id;
    view = 'results';
    filterText = '';
    sort = 'relevance';
    selected = new Set();
    collapsedPeers = new Set();
    conditionsOpen = false;
    onusequery(record.draft);
  }

  function removeSearch(id: string): void {
    searches = searches.filter((item) => item.id !== id);
    if (activeSearchId !== id) return;
    activeSearchId = searches[0]?.id ?? null;
    view = 'list';
  }

  function statusLabel(status: SearchRecord['status']): string {
    if (status === 'searching') return 'Searching';
    if (status === 'receiving') return 'Receiving';
    return 'Complete';
  }

  function basename(path: string): string {
    return path.split('/').at(-1) ?? path;
  }

  function extension(path: string): string {
    const name = basename(path);
    const dot = name.lastIndexOf('.');
    return dot >= 0 ? name.slice(dot + 1).toUpperCase() : '';
  }

  function formatBytes(bytes: number): string {
    if (bytes >= 1_000_000_000) return `${(bytes / 1_000_000_000).toFixed(2)} GB`;
    return `${(bytes / 1_000_000).toFixed(bytes >= 100_000_000 ? 0 : 1)} MB`;
  }

  function formatLength(seconds?: number): string {
    if (seconds === undefined) return '—';
    const minutes = Math.floor(seconds / 60);
    return `${minutes}:${String(seconds % 60).padStart(2, '0')}`;
  }

  function sampleRateLabel(hz?: number): string {
    if (!hz) return '—';
    return hz % 1000 === 0 ? `${hz / 1000} kHz` : `${(hz / 1000).toFixed(1)} kHz`;
  }

  function audioSummary(audio?: AudioAttributes): string {
    if (!audio) return '—';
    const parts: string[] = [];
    if (audio.bitDepth) parts.push(`${audio.bitDepth}-bit`);
    if (audio.sampleRateHz) parts.push(sampleRateLabel(audio.sampleRateHz));
    if (audio.bitrateKbps) parts.push(`${audio.bitrateKbps} kbps`);
    return parts.length ? parts.join(' · ') : '—';
  }

  function csv(value: string): string[] {
    return value.split(',').map((item) => item.trim()).filter(Boolean);
  }

  function isAudioPath(path: string): boolean {
    return ['FLAC', 'MP3', 'OGG', 'OPUS', 'M4A', 'WAV', 'AAC', 'APE'].includes(extension(path));
  }

  function fileMatchesConditions(
    path: string,
    audio: AudioAttributes | undefined,
    conditions: PrototypeSearchConditions,
  ): boolean {
    const formats = conditions.common.formats;
    if (formats.length && !formats.includes(extension(path))) return false;
    if (conditions.common.rejectUnknownMetadata && isAudioPath(path) && !audio) return false;
    if (conditions.common.minBitrate && (audio?.bitrateKbps ?? 0) < Number(conditions.common.minBitrate)) return false;
    if (conditions.common.maxBitrate && (audio?.bitrateKbps ?? Infinity) > Number(conditions.common.maxBitrate)) return false;
    if (conditions.common.sampleRate && audio?.sampleRateHz !== Number(conditions.common.sampleRate)) return false;
    if (conditions.common.bitDepth && audio?.bitDepth !== Number(conditions.common.bitDepth)) return false;
    return true;
  }

  function peerMatches(record: SearchRecord, result: ProjectedSearchResult): boolean {
    const allowed = csv(record.conditions.common.allowedUsers);
    const banned = csv(record.conditions.common.bannedUsers);
    if (allowed.length && !allowed.includes(result.peer.username)) return false;
    if (banned.includes(result.peer.username)) return false;
    return true;
  }

  function trackMatches(record: SearchRecord, result: TrackSearchResult): boolean {
    if (!peerMatches(record, result)) return false;
    if (!fileMatchesConditions(result.path, result.audio, record.conditions)) return false;
    const normalized = result.path.toLowerCase();
    if (filterText && !`${result.peer.username} ${result.path}`.toLowerCase().includes(filterText.toLowerCase())) return false;
    if (record.conditions.common.strictArtist && record.draft.mode === 'split' && record.draft.artist && !normalized.includes(record.draft.artist.toLowerCase())) return false;
    if (record.conditions.track.strictTitle && record.draft.mode === 'split' && record.draft.title && !basename(result.path).toLowerCase().includes(record.draft.title.toLowerCase())) return false;
    if (!record.conditions.track.acceptNoLength && result.audio?.lengthSeconds === undefined) return false;
    if (record.conditions.track.expectedLength) {
      const expected = Number(record.conditions.track.expectedLength);
      const tolerance = Number(record.conditions.track.lengthTolerance || 0);
      const length = result.audio?.lengthSeconds;
      if (length === undefined || Math.abs(length - expected) > tolerance) return false;
    }
    return true;
  }

  function albumMatches(record: SearchRecord, result: AlbumSearchResult): boolean {
    if (!peerMatches(record, result)) return false;
    const haystack = `${result.peer.username} ${result.path} ${result.files.map((file) => file.relativePath).join(' ')}`.toLowerCase();
    if (filterText && !haystack.includes(filterText.toLowerCase())) return false;
    if (record.conditions.common.strictArtist && record.draft.mode === 'split' && record.draft.artist && !result.path.toLowerCase().includes(record.draft.artist.toLowerCase())) return false;
    if (record.conditions.album.strictAlbum && record.draft.mode === 'split' && record.draft.title && !result.path.toLowerCase().includes(record.draft.title.toLowerCase())) return false;
    if (record.conditions.album.minTrackCount && result.files.filter((file) => isAudioPath(file.relativePath)).length < Number(record.conditions.album.minTrackCount)) return false;
    if (record.conditions.album.maxTrackCount && result.files.filter((file) => isAudioPath(file.relativePath)).length > Number(record.conditions.album.maxTrackCount)) return false;
    for (const title of record.conditions.album.requiredTrackTitles) {
      if (!result.files.some((file) => file.relativePath.toLowerCase().includes(title.toLowerCase()))) return false;
    }
    const audioFiles = result.files.filter((file) => isAudioPath(file.relativePath));
    const qualityMatches = audioFiles.filter((file) => fileMatchesConditions(file.relativePath, file.audio, record.conditions));
    if (record.conditions.album.strictAlbumQuality) return qualityMatches.length === audioFiles.length;
    if (record.conditions.common.formats.length || record.conditions.common.minBitrate || record.conditions.common.maxBitrate || record.conditions.common.sampleRate || record.conditions.common.bitDepth) {
      return qualityMatches.length > 0;
    }
    return true;
  }

  function itemSize(result: ProjectedSearchResult): number {
    return result.sizeBytes;
  }

  function sortedVisibleResults(record: SearchRecord): ProjectedSearchResult[] {
    const source: ProjectedSearchResult[] = record.draft.resultMode === 'album' ? [...albumResults] : [...trackResults];
    const filtered = source.filter((result) => result.kind === 'album' ? albumMatches(record, result) : trackMatches(record, result));
    if (sort === 'relevance') return filtered;
    const sorted = [...filtered];
    if (sort === 'speed') return sorted.sort((a, b) => b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps);
    if (sort === 'queue') return sorted.sort((a, b) => a.peer.queueLength - b.peer.queueLength || b.peer.uploadSpeedMbps - a.peer.uploadSpeedMbps);
    return sorted.sort((a, b) => sizeDirection === 'desc' ? itemSize(b) - itemSize(a) : itemSize(a) - itemSize(b));
  }

  interface PeerGroup {
    key: string;
    peer: ProjectedSearchResult['peer'];
    preferred: boolean;
    items: ProjectedSearchResult[];
  }

  function groupAdjacent(results: ProjectedSearchResult[]): PeerGroup[] {
    const groups: PeerGroup[] = [];
    for (const result of results) {
      const previous = groups.at(-1);
      if (previous && previous.peer.username === result.peer.username && (sort !== 'relevance' || previous.preferred === result.preferred)) {
        previous.items.push(result);
      } else {
        groups.push({ key: `${result.preferred ? 'preferred' : 'other'}-${result.peer.username}-${groups.length}`, peer: result.peer, preferred: result.preferred, items: [result] });
      }
    }
    return groups;
  }

  function selectedKey(result: TrackSearchResult): string {
    return `track:${result.id}`;
  }

  function selectedAlbumFileKey(album: AlbumSearchResult, file: AlbumFileResult): string {
    return `album:${album.id}:${file.id}`;
  }

  function isAlbumFullySelected(album: AlbumSearchResult): boolean {
    return album.files.length > 0 && album.files.every((file) => selected.has(selectedAlbumFileKey(album, file)));
  }

  function isAlbumPartiallySelected(album: AlbumSearchResult): boolean {
    const count = album.files.filter((file) => selected.has(selectedAlbumFileKey(album, file))).length;
    return count > 0 && count < album.files.length;
  }

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return {
      update(next: boolean) { node.indeterminate = next; },
    };
  }

  function toggleSelection(key: string, checked: boolean): void {
    const next = new Set(selected);
    if (checked) next.add(key);
    else next.delete(key);
    selected = next;
  }

  function toggleAlbum(album: AlbumSearchResult, checked: boolean): void {
    const next = new Set(selected);
    for (const file of album.files) {
      const key = selectedAlbumFileKey(album, file);
      if (checked) next.add(key);
      else next.delete(key);
    }
    selected = next;
  }

  function visibleSelectionKeys(results: ProjectedSearchResult[]): string[] {
    return results.flatMap((result) => result.kind === 'track'
      ? [selectedKey(result)]
      : result.files.map((file) => selectedAlbumFileKey(result, file)));
  }

  function selectVisible(results: ProjectedSearchResult[]): void {
    selected = new Set([...selected, ...visibleSelectionKeys(results)]);
  }

  function allVisibleSelected(results: ProjectedSearchResult[]): boolean {
    const keys = visibleSelectionKeys(results);
    return keys.length > 0 && keys.every((key) => selected.has(key));
  }

  function toggleVisible(results: ProjectedSearchResult[], checked: boolean): void {
    const next = new Set(selected);
    for (const key of visibleSelectionKeys(results)) {
      if (checked) next.add(key);
      else next.delete(key);
    }
    selected = next;
  }

  function togglePeer(key: string): void {
    const next = new Set(collapsedPeers);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    collapsedPeers = next;
  }

  function peerGroupFileCount(group: PeerGroup): number {
    return group.items.reduce((total, item) => total + (item.kind === 'album' ? item.files.length : 1), 0);
  }

  function tierGroups(groups: PeerGroup[], preferred: boolean): PeerGroup[] {
    return groups.filter((group) => group.preferred === preferred);
  }

  function tierItemCount(groups: PeerGroup[]): number {
    return groups.reduce((total, group) => total + group.items.length, 0);
  }
</script>

<section class="page page-search redesigned-search-page">
  {#if view === 'list'}
    <header class="page-heading search-list-heading">
      <p class="eyebrow">Discover</p>
      <h1>Searches</h1>
    </header>

    <div class="search-history-list">
      {#each searches as record (record.id)}
        <div class="search-history-row">
          <button type="button" class="search-history-open" onclick={() => openSearch(record)}>
            <span class="search-history-query">{record.displayQuery}</span>
            <span class={`search-status-badge ${record.status}`}><i></i>{statusLabel(record.status)}</span>
            <span class="search-history-context">
              {#if record.draft.resultMode === 'album'}
                <svg class="search-kind-icon" viewBox="0 0 20 20" aria-hidden="true"><path d="M3 6h5l1.6 2H17v7H3zM3 6V4.5h5l1.3 1.5" /></svg>
                <span>Album</span>
              {:else}
                <svg class="search-kind-icon" viewBox="0 0 20 20" aria-hidden="true"><path d="M8 4v9M8 6l7-2v8M8 13c0 1.1-1.1 2-2.5 2S3 14.1 3 13s1.1-2 2.5-2S8 11.9 8 13zm7-1c0 1.1-1.1 2-2.5 2s-2.5-.9-2.5-2 1.1-2 2.5-2 2.5.9 2.5 2z" /></svg>
                <span>Track</span>
              {/if}
              <span class="stat-separator">·</span>
              <span>{record.when}</span>
            </span>
            <span class="search-history-stats">
              <span><strong>{record.foundFiles}</strong> files</span>
              <span class="stat-separator">·</span>
              <span><strong>{record.lockedFiles}</strong> locked</span>
              <span class="stat-separator">·</span>
              <span><strong>{record.distinctPeers}</strong> peers</span>
            </span>
          </button>
          <button type="button" class="search-history-remove" aria-label={`Remove ${record.displayQuery}`} onclick={() => removeSearch(record.id)}>
            <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg>
          </button>
        </div>
      {:else}
        <div class="empty-state">No searches yet.</div>
      {/each}
    </div>
  {:else if activeRecord}
    {@const visibleResults = sortedVisibleResults(activeRecord)}
    {@const groups = groupAdjacent(visibleResults)}
    <header class="search-results-heading">
      <button type="button" class="icon-button back-button" aria-label="Back to searches" onclick={() => (view = 'list')}>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
      </button>
      <div class="search-results-title">
        <p class="eyebrow">{activeMode === 'album' ? 'Album search' : 'Track search'}</p>
        <h1>{activeRecord.displayQuery}</h1>
      </div>
      <div class="search-results-summary">
        <span class={`search-status-badge ${activeRecord.status}`}><i></i>{statusLabel(activeRecord.status)}</span>
        <span>{activeRecord.foundFiles} files</span>
        <span>{activeRecord.lockedFiles} locked</span>
        <span>{activeRecord.distinctPeers} peers</span>
      </div>
    </header>

    <div class="result-refine-row">
      <label class="result-filter-control">
        <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="4.5"/><path d="M12 12l4 4"/></svg>
        <input bind:value={filterText} placeholder="Filter results…" aria-label="Filter search results" />
        {#if filterText}
          <button type="button" aria-label="Clear result filter" onclick={() => (filterText = '')}>×</button>
        {/if}
      </label>

      <div class="result-sort-control">
        <label for="result-sort">Sort</label>
        <select id="result-sort" bind:value={sort}>
          <option value="relevance">Relevance</option>
          <option value="speed">Upload speed</option>
          <option value="queue">Queue depth</option>
          <option value="size">Item size</option>
        </select>
        {#if sort === 'size'}
          <button type="button" class="size-direction-button" aria-label="Reverse item size sort" onclick={() => (sizeDirection = sizeDirection === 'desc' ? 'asc' : 'desc')}>
            <svg class:ascending={sizeDirection === 'asc'} viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M6 8l4-4 4 4" /></svg>
          </button>
        {/if}
      </div>
    </div>

    <div class="result-conditions-wrap">
      <div class="result-conditions-bar">
        <span class="result-conditions-label">Conditions</span>
        <div class="result-condition-pills">
          <SearchConditionPills mode={activeMode} bind:conditions={activeRecord.conditions} />
        </div>
        <button type="button" class:active={conditionsOpen} class="edit-conditions-button" aria-expanded={conditionsOpen} onclick={() => (conditionsOpen = !conditionsOpen)}>
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
          Edit
        </button>
      </div>

      {#if conditionsOpen}
        <button type="button" class="results-config-backdrop" aria-label="Close search configuration" onclick={() => (conditionsOpen = false)}></button>
        <section class="search-config-popover results-config-popover" aria-label="Result conditions">
          <SearchConfigPanel mode={activeMode} bind:conditions={activeRecord.conditions} title="Result conditions" onclose={() => (conditionsOpen = false)} />
        </section>
      {/if}
    </div>

    <div class="results-list-toolbar">
      <label class="select-visible-control">
        <input
          type="checkbox"
          checked={allVisibleSelected(visibleResults)}
          onchange={(event) => toggleVisible(visibleResults, (event.currentTarget as HTMLInputElement).checked)}
        />
        <span>{visibleResults.length} visible</span>
      </label>

      {#if selected.size}
        <div class="selection-actions">
          <strong>{selected.size} selected</strong>
          <button type="button" class="primary-selection-action">
            <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 3v9M6.5 8.5L10 12l3.5-3.5M4 15.5h12" /></svg>
            Download selected
          </button>
          <button type="button" class="icon-button" aria-label="Clear selection" onclick={() => (selected = new Set())}>
            <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg>
          </button>
        </div>
      {:else}
        <button type="button" class="quiet-action" onclick={() => selectVisible(visibleResults)}>Select visible</button>
      {/if}
    </div>

    {#if visibleResults.length === 0}
      <div class="search-results-empty">
        <strong>No matching results</strong>
        <span>Adjust the text filter or result conditions.</span>
      </div>
    {:else if sort === 'relevance'}
      {@const preferredGroups = tierGroups(groups, true)}
      {@const otherGroups = tierGroups(groups, false)}
      {#if preferredGroups.length}
        <div class="result-tier-heading preferred">
          <span>Preferred matches</span>
          <small>{tierItemCount(preferredGroups)}</small>
        </div>
        <div class="result-tier preferred-tier">
          {#each preferredGroups as group (group.key)}
            {@render peerGroup(group)}
          {/each}
        </div>
      {/if}
      {#if otherGroups.length}
        <div class="result-tier-heading other">
          <span>Other matches</span>
          <small>{tierItemCount(otherGroups)}</small>
        </div>
        <div class="result-tier">
          {#each otherGroups as group (group.key)}
            {@render peerGroup(group)}
          {/each}
        </div>
      {/if}
    {:else}
      <div class="result-tier">
        {#each groups as group (group.key)}
          {@render peerGroup(group)}
        {/each}
      </div>
    {/if}
  {/if}
</section>

{#snippet peerGroup(group: PeerGroup)}
  <section class:preferred={group.preferred && sort === 'relevance'} class="result-peer-group">
    <button type="button" class="result-peer-header" aria-expanded={!collapsedPeers.has(group.key)} onclick={() => togglePeer(group.key)}>
      <svg class:collapsed={collapsedPeers.has(group.key)} class="peer-chevron" viewBox="0 0 20 20" aria-hidden="true"><path d="M7 5l6 5-6 5" /></svg>
      <span class="peer-identity">
        <strong>{group.peer.username}</strong>
        <span class:available={group.peer.freeUploadSlot} class="peer-slot"><i></i>{group.peer.freeUploadSlot ? 'Free slot' : 'No free slot'}</span>
      </span>
      <span class="peer-stat peer-upload-stat"><b>{group.peer.uploadSpeedMbps.toFixed(1)} MB/s</b><small>upload</small></span>
      <span class="peer-stat"><b>{group.peer.queueLength}</b><small>queued</small></span>
      <span class="peer-stat peer-count-stat"><b>{peerGroupFileCount(group)}</b><small>{peerGroupFileCount(group) === 1 ? 'file' : 'files'}</small></span>
    </button>

    {#if !collapsedPeers.has(group.key)}
      <div class="result-peer-items">
        {#each group.items as result (result.id)}
          {#if result.kind === 'track'}
            <label class:locked={result.locked} class="track-result-row">
              <input type="checkbox" checked={selected.has(selectedKey(result))} onchange={(event) => toggleSelection(selectedKey(result), (event.currentTarget as HTMLInputElement).checked)} aria-label={`Select ${basename(result.path)}`} />
              <div class="result-path-block">
                <div class="result-name-line">
                  <strong>{basename(result.path)}</strong>
                  {#if result.locked}<span class="locked-badge"><svg viewBox="0 0 20 20" aria-hidden="true"><rect x="5" y="9" width="10" height="8" rx="2"/><path d="M7 9V7a3 3 0 016 0v2"/></svg>Locked</span>{/if}
                </div>
                <small>{result.path}</small>
              </div>
              <div class="result-detail"><strong>{formatBytes(result.sizeBytes)}</strong></div>
              <div class="result-detail audio-detail"><strong>{audioSummary(result.audio)}</strong></div>
              <div class="result-detail"><strong>{formatLength(result.audio?.lengthSeconds)}</strong></div>
            </label>
          {:else}
            <div class:locked={result.locked} class="album-result-block">
              <label class="album-result-summary">
                <input
                  type="checkbox"
                  checked={isAlbumFullySelected(result)}
                  aria-label={`Select all files in ${basename(result.path)}`}
                  use:indeterminate={isAlbumPartiallySelected(result)}
                  onchange={(event) => toggleAlbum(result, (event.currentTarget as HTMLInputElement).checked)}
                />
                <div class="result-path-block">
                  <div class="result-name-line">
                    <strong>{basename(result.path)}</strong>
                    {#if result.locked}<span class="locked-badge"><svg viewBox="0 0 20 20" aria-hidden="true"><rect x="5" y="9" width="10" height="8" rx="2"/><path d="M7 9V7a3 3 0 016 0v2"/></svg>Locked</span>{/if}
                  </div>
                  <small>{result.path}</small>
                </div>
                <div class="album-summary-stat"><strong>{result.files.length}</strong><small>files</small></div>
                <div class="album-summary-stat"><strong>{formatBytes(result.sizeBytes)}</strong><small>total</small></div>
              </label>

              <div class="album-files-table">
                {#each result.files as file (file.id)}
                  <label class:locked={file.locked} class="album-file-row">
                    <input type="checkbox" checked={selected.has(selectedAlbumFileKey(result, file))} onchange={(event) => toggleSelection(selectedAlbumFileKey(result, file), (event.currentTarget as HTMLInputElement).checked)} />
                    <span class="album-file-path">
                      <strong>{file.relativePath}</strong>
                      {#if file.locked}<small>Locked</small>{/if}
                    </span>
                    <span class="album-file-audio">{audioSummary(file.audio)}</span>
                    <span>{formatBytes(file.sizeBytes)}</span>
                    <span>{formatLength(file.audio?.lengthSeconds)}</span>
                  </label>
                {/each}
              </div>
            </div>
          {/if}
        {/each}
      </div>
    {/if}
  </section>
{/snippet}
