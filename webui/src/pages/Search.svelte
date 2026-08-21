<script lang="ts">
  import SearchConditionPills from '../components/SearchConditionPills.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import MutationStatus from '../components/MutationStatus.svelte';
  import ResultFilterControl from '../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../components/SelectionToolbar.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import SearchConfigPanel from '../components/SearchConfigPanel.svelte';
  import { hasAppliedConditions, type PrototypeSearchConditions } from '../prototype/search-config';
  import Icon from '../components/Icon.svelte';
  import { groupAdjacentBy } from '../prototype/grouping';
  import type { ScenarioId } from '../mock/types';
  import type { PrototypeDownloadSelectionSummary, PrototypeMutationState, ProposedHistoryDeleteRequestDto } from '../prototype/backend-contracts';
  import type { SearchDraft } from '../prototype/search';
  import { resourceStateForScenario, type PrototypeResourceState } from '../prototype/resource-state';
  import {
    buildSearchResultProjectionRequest,
    requestSearchResultProjection,
    type AlbumFileResult,
    type AlbumSearchResult,
    type ProjectedSearchResult,
    type SearchRecord,
    type SearchSort,
    type SearchView,
    type SizeSortDirection,
    type TrackSearchResult,
  } from '../prototype/search-results';

  interface Props {
    search: SearchDraft;
    scenarioId: ScenarioId;
    searches: SearchRecord[];
    view: SearchView;
    activeSearchId: string | null;
    onopenuser: (username: string) => void;
    onopenrecord: (record: SearchRecord) => void;
    onshowlist: () => void;
    onsearchagain: (record: SearchRecord) => void;
  }

  let {
    search,
    scenarioId,
    searches = $bindable(),
    view = $bindable(),
    activeSearchId = $bindable(),
    onopenuser,
    onopenrecord,
    onshowlist,
    onsearchagain,
  }: Props = $props();

  let filterText = $state('');
  let sort = $state<SearchSort>('relevance');
  let sizeDirection = $state<SizeSortDirection>('desc');
  let selected = $state<Set<string>>(new Set());
  let conditionsOpen = $state(false);
  let resultPagesRequested = $state(1);
  let projectionRequestKey = '';
  let historyLimit = $state(4);
  let mutation = $state<PrototypeMutationState>({ phase: 'idle' });

  let activeRecord = $derived(searches.find((item) => item.id === activeSearchId) ?? null);
  let activeMode = $derived(activeRecord?.draft.resultMode ?? search.resultMode);
  let listResourceState = $derived(resourceStateForScenario(scenarioId, 'search-list'));

  $effect(() => {
    scenarioId;
    historyLimit = 4;
    mutation = { phase: 'idle' };
  });

  $effect(() => {
    const key = JSON.stringify({
      activeSearchId,
      filterText,
      sort,
      sizeDirection,
      conditions: activeRecord?.conditions,
    });
    if (key === projectionRequestKey) return;
    projectionRequestKey = key;
    resultPagesRequested = 1;
  });

  function openSearch(record: SearchRecord): void {
    onopenrecord(record);
    filterText = '';
    sort = 'relevance';
    selected = new Set();
    conditionsOpen = false;
    resultPagesRequested = 1;
  }

  function removeSearch(id: string): void {
    const request: ProposedHistoryDeleteRequestDto = { resourceKind: 'search-job', resourceIds: [id], semantics: 'archive-from-history' };
    void request;
    mutation = { phase: 'pending', label: 'Removing search history…' };
    searches = searches.filter((item) => item.id !== id);
    mutation = { phase: 'succeeded', label: 'Search removed' };
    if (activeSearchId !== id) return;
    activeSearchId = searches[0]?.id ?? null;
    view = 'list';
    onshowlist();
  }

  function statusLabel(status: SearchRecord['status']): string {
    const labels: Record<SearchRecord['status'], string> = {
      pending: 'Pending',
      searching: 'Searching',
      receiving: 'Receiving',
      complete: 'Complete',
      failed: 'Failed',
      cancelled: 'Cancelled',
      skipped: 'Skipped',
      interrupted: 'Interrupted',
    };
    return labels[status];
  }

  function resultResourceState(record: SearchRecord): PrototypeResourceState {
    if (record.resultState === 'pruned') return { phase: 'pruned', title: 'Results unavailable', blocking: true };
    if (record.resultState === 'not-persisted') return { phase: 'unavailable', title: 'Results unavailable', blocking: true };
    return resourceStateForScenario(scenarioId, 'search-results');
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

  function currentResultProjection(record: SearchRecord) {
    let cursor: string | null = null;
    let items: ProjectedSearchResult[] = [];
    let page = requestSearchResultProjection(
      record,
      buildSearchResultProjectionRequest(
        record,
        filterText,
        sort,
        sizeDirection,
        cursor,
        record.pagination.resultPageSize,
      ),
    );
    items = [...page.items];
    for (let pageIndex = 1; pageIndex < resultPagesRequested && page.nextCursor; pageIndex += 1) {
      cursor = page.nextCursor;
      page = requestSearchResultProjection(
        record,
        buildSearchResultProjectionRequest(
          record,
          filterText,
          sort,
          sizeDirection,
          cursor,
          record.pagination.resultPageSize,
        ),
      );
      items.push(...page.items);
    }
    return { ...page, items };
  }

  function selectionSummary(): PrototypeDownloadSelectionSummary {
    let requestedCount = 0;
    let lockedCount = 0;
    const received = activeRecord ? currentResultProjection(activeRecord).items : [];
    if (activeMode === 'track') {
      for (const result of received) {
        if (result.kind !== 'track' || !selected.has(selectedKey(result))) continue;
        requestedCount += 1;
        if (result.locked) lockedCount += 1;
      }
    } else {
      for (const result of received) {
        if (result.kind !== 'album') continue;
        for (const file of result.files) {
          if (!selected.has(selectedAlbumFileKey(result, file))) continue;
          requestedCount += 1;
          if (file.locked) lockedCount += 1;
        }
      }
    }
    return { requestedCount, uniqueFileCount: requestedCount, resolvablePublicCount: requestedCount - lockedCount, lockedCount, skippedCount: lockedCount };
  }

  function requestSelectedDownload(): void {
    const summary = selectionSummary();
    if (!summary.resolvablePublicCount) {
      mutation = { phase: 'rejected', label: 'Nothing downloadable', detail: `${summary.lockedCount} selected file${summary.lockedCount === 1 ? '' : 's'} locked.` };
      return;
    }
    mutation = { phase: 'pending', label: `Requesting ${summary.resolvablePublicCount} file${summary.resolvablePublicCount === 1 ? '' : 's'}…` };
    mutation = summary.skippedCount
      ? { phase: 'partially-succeeded', label: `${summary.resolvablePublicCount} requested`, detail: `${summary.skippedCount} locked file${summary.skippedCount === 1 ? '' : 's'} skipped.` }
      : { phase: 'succeeded', label: `${summary.resolvablePublicCount} download${summary.resolvablePublicCount === 1 ? '' : 's'} requested` };
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

    {#if listResourceState.blocking}
      <div class="empty-state"><strong>{listResourceState.title}</strong><p>{listResourceState.detail}</p></div>
    {:else}
      <ResourceStateNotice state={listResourceState} />
      <MutationStatus state={mutation} />
    <div class="search-history-list">
      {#each searches.slice(0, historyLimit) as record (record.id)}
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
    {#if searches.length > historyLimit}
      <LoadMoreButton label="Load earlier searches" onclick={() => (historyLimit = Math.min(searches.length, historyLimit + 4))} />
    {/if}
    {/if}
  {:else if activeRecord}
    {@const resultState = resultResourceState(activeRecord)}
    {@const projection = currentResultProjection(activeRecord)}
    {@const allVisibleResults = projection.items}
    {@const groups = groupAdjacent(allVisibleResults)}
    <header class="search-results-heading">
      <button type="button" class="icon-button back-button" aria-label="Back to searches" onclick={onshowlist}>
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
      <div class="search-results-actions">
        <button type="button" class="search-again-button" title="Run this search again" onclick={() => onsearchagain(activeRecord)}>
          <Icon name="search" />
          <span>Search again</span>
        </button>
        <button type="button" class="delete-search-button" aria-label={`Delete ${activeRecord.displayQuery}`} title="Delete search" onclick={() => removeSearch(activeRecord.id)}>
          <Icon name="trash" />
          <span>Delete</span>
        </button>
      </div>
    </header>

    {#if resultState.blocking}
      <div class="empty-state"><strong>{resultState.title}</strong><p>{resultState.detail}</p></div>
    {:else}
      <ResourceStateNotice state={resultState} />
      <MutationStatus state={mutation} />

    <div class="result-refine-wrap">
      <div class="result-refine-row">
        <ResultFilterControl bind:value={filterText} placeholder="Filter results…" ariaLabel="Filter search results" />

        <button type="button" class:active={conditionsOpen} class="edit-conditions-button" aria-expanded={conditionsOpen} onclick={() => (conditionsOpen = !conditionsOpen)}>
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
          Conditions
        </button>

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

    {@const selectedSummary = selectionSummary()}
    <SelectionToolbar
      selectedCount={selectedSummary.requestedCount}
      floatingLabel={`Download ${selectedSummary.resolvablePublicCount}`}
      detail={selectedSummary.lockedCount ? `${selectedSummary.requestedCount} selected · ${selectedSummary.lockedCount} locked` : undefined}
      actionDisabled={selectedSummary.resolvablePublicCount === 0}
      onclear={() => (selected = new Set())}
      onaction={requestSelectedDownload}
    />

    {#if allVisibleResults.length === 0}
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

    {#if projection.nextCursor}
      <LoadMoreButton label="Load more results" loadingLabel="Loading results…" onclick={() => (resultPagesRequested += 1)} />
    {/if}
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
          totalFileCount={result.totalFileCount}
          filesComplete
          dataStateLabel={result.retrievalState === 'retrieving' ? 'Retrieving folder…' : result.retrievalState === 'failed' ? 'Folder retrieval failed' : undefined}
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
