<script lang="ts">
  import SearchConditionPills from '../components/SearchConditionPills.svelte';
  import ResultFilterControl from '../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../components/SelectionToolbar.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import SearchConfigPanel from '../components/SearchConfigPanel.svelte';
  import { hasAppliedConditions, type PrototypeSearchConditions } from '../prototype/search-config';
  import Icon from '../components/Icon.svelte';
  import { groupAdjacentBy } from '../prototype/grouping';
  import { basename, extension } from '../prototype/items';
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
    onopenuser: (username: string) => void;
  }

  let {
    search,
    searches = $bindable(),
    view = $bindable(),
    activeSearchId = $bindable(),
    onusequery,
    onopenuser,
  }: Props = $props();

  let filterText = $state('');
  let sort = $state<SearchSort>('relevance');
  let sizeDirection = $state<SizeSortDirection>('desc');
  let selected = $state<Set<string>>(new Set());
  let conditionsOpen = $state(false);

  let activeRecord = $derived(searches.find((item) => item.id === activeSearchId) ?? null);
  let activeMode = $derived(activeRecord?.draft.resultMode ?? search.resultMode);

  function openSearch(record: SearchRecord): void {
    activeSearchId = record.id;
    view = 'results';
    filterText = '';
    sort = 'relevance';
    selected = new Set();
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
    return groupAdjacentBy(
      results,
      (result) => `${sort === 'relevance' ? (result.preferred ? 'preferred' : 'other') : 'all'}:${result.peer.username}`,
      `${activeSearchId ?? 'search'}:`,
    ).map((group) => ({
      key: group.key,
      peer: group.items[0]!.peer,
      preferred: group.items[0]!.preferred,
      items: group.items,
    }));
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


  function selectedFileIdsForAlbum(album: AlbumSearchResult): Set<string> {
    return new Set(album.files.filter((file) => selected.has(selectedAlbumFileKey(album, file))).map((file) => file.id));
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
      <button type="button" class="delete-search-button" aria-label={`Delete ${activeRecord.displayQuery}`} title="Delete search" onclick={() => removeSearch(activeRecord.id)}>
        <Icon name="trash" />
        <span>Delete</span>
      </button>
    </header>

    <div class="result-refine-wrap">
      <div class="result-refine-row">
        <ResultFilterControl bind:value={filterText} placeholder="Filter results…" ariaLabel="Filter search results" />

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

        <button type="button" class:active={conditionsOpen} class="edit-conditions-button" aria-expanded={conditionsOpen} onclick={() => (conditionsOpen = !conditionsOpen)}>
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
          Conditions
        </button>
      </div>

      {#if hasAppliedConditions(activeMode, activeRecord.conditions)}
        <div class="result-condition-pills">
          <SearchConditionPills mode={activeMode} bind:conditions={activeRecord.conditions} />
        </div>
      {/if}

      {#if conditionsOpen}
        <button type="button" class="results-config-backdrop" aria-label="Close search configuration" onclick={() => (conditionsOpen = false)}></button>
        <section class="search-config-popover results-config-popover" aria-label="Result search configuration">
          <SearchConfigPanel mode={activeMode} bind:conditions={activeRecord.conditions} title="Search configuration" initialTab="conditions" onclose={() => (conditionsOpen = false)} />
        </section>
      {/if}
    </div>

    <SelectionToolbar
      visibleLabel={`${visibleResults.length} visible`}
      selectedCount={selected.size}
      allVisibleSelected={allVisibleSelected(visibleResults)}
      ontogglevisible={(checked) => toggleVisible(visibleResults, checked)}
      onselectvisible={() => selectVisible(visibleResults)}
      onclear={() => (selected = new Set())}
    />

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
  <PeerItemGroup peer={group.peer} itemCount={group.items.length} {onopenuser}>
    {#each group.items as result (result.id)}
      {#if result.kind === 'track'}
        <FileItemCard
          path={result.path}
          sizeBytes={result.sizeBytes}
          audio={result.audio}
          locked={result.locked}
          selected={selected.has(selectedKey(result))}
          preferred={group.preferred && sort === 'relevance'}
          selectable
          onselect={(checked) => toggleSelection(selectedKey(result), checked)}
        />
      {:else}
        <FolderItemCard
          path={result.path}
          sizeBytes={result.sizeBytes}
          files={result.files}
          locked={result.locked}
          selected={isAlbumFullySelected(result)}
          partial={isAlbumPartiallySelected(result)}
          preferred={group.preferred && sort === 'relevance'}
          selectable
          selectedFileIds={selectedFileIdsForAlbum(result)}
          onselectall={(checked) => toggleAlbum(result, checked)}
          onselectfile={(file, checked) => { const original = result.files.find((candidate) => candidate.id === file.id); if (original) toggleSelection(selectedAlbumFileKey(result, original), checked); }}
        />
      {/if}
    {/each}
  </PeerItemGroup>
{/snippet}
