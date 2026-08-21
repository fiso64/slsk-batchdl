<script lang="ts">
  import Icon from '../components/Icon.svelte';
  import LinkifiedText from '../components/LinkifiedText.svelte';
  import ResultFilterControl from '../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../components/SelectionToolbar.svelte';
  import type { ScenarioId } from '../mock/types';
  import { formatSpeed } from '../prototype/transfers';
  import {
    flattenShareTree,
    formatShareSize,
    getUserBrowseFixtureForUsername,
    shareMetrics,
    type ShareTreeRow,
    type UserBrowseView,
    type UserPresence,
  } from '../prototype/users';

  interface Props {
    scenarioId: ScenarioId;
    username: string;
    view: UserBrowseView;
    onviewchange: (view: UserBrowseView) => void;
    onmessageuser: (username: string) => void;
  }

  let { scenarioId, username, view, onviewchange, onmessageuser }: Props = $props();

  let filterText = $state('');
  let profilePictureOpen = $state(false);
  let selected = $state<Set<string>>(new Set());
  let expandedFolders = $state<Set<string>>(new Set());
  let fixtureKey = '';

  let fixture = $derived(getUserBrowseFixtureForUsername(scenarioId, username));
  let rows = $derived(flattenShareTree(fixture.shares));
  let metrics = $derived(shareMetrics(fixture.shares));
  let displayUsername = $derived(fixture.profile.username);

  $effect(() => {
    const key = fixture.profile.username;
    if (key === fixtureKey) return;
    fixtureKey = key;
    filterText = '';
    selected = new Set();
    expandedFolders = new Set(rows.filter((row) => row.kind === 'folder').map((row) => row.id));
  });

  function handleProfilePictureKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && profilePictureOpen) profilePictureOpen = false;
  }

  function presenceLabel(presence: UserPresence): string {
    if (presence === 'away') return 'Away';
    if (presence === 'offline') return 'Offline';
    return 'Online';
  }

  function initials(value: string): string {
    const pieces = value.split(/[^a-z0-9]+/i).filter(Boolean);
    if (pieces.length === 0) return '?';
    return pieces.slice(0, 2).map((piece) => piece[0]!.toUpperCase()).join('');
  }

  function formatInteger(value: number): string {
    return new Intl.NumberFormat('en-US').format(value);
  }

  function visibleShareRows(): ShareTreeRow[] {
    const query = filterText.trim().toLowerCase();
    if (query) {
      const included = new Set<string>();
      for (const row of rows) {
        if (!row.path.toLowerCase().includes(query)) continue;
        included.add(row.id);
        for (const parent of row.parentFolderIds) included.add(parent);
        if (row.kind === 'folder') {
          for (const candidate of rows) {
            if (candidate.parentFolderIds.includes(row.id)) included.add(candidate.id);
          }
        }
      }
      return rows.filter((row) => included.has(row.id));
    }

    return rows.filter((row) => row.parentFolderIds.every((id) => expandedFolders.has(id)));
  }

  function visibleFileIds(visibleRows: ShareTreeRow[]): string[] {
    return visibleRows.filter((row) => row.kind === 'file').map((row) => row.id);
  }

  function allSelected(ids: readonly string[]): boolean {
    return ids.length > 0 && ids.every((id) => selected.has(id));
  }

  function partiallySelected(ids: readonly string[]): boolean {
    const count = ids.filter((id) => selected.has(id)).length;
    return count > 0 && count < ids.length;
  }

  function toggleIds(ids: readonly string[], checked: boolean): void {
    const next = new Set(selected);
    for (const id of ids) {
      if (checked) next.add(id);
      else next.delete(id);
    }
    selected = next;
  }

  function selectVisible(visibleRows: ShareTreeRow[]): void {
    toggleIds(visibleFileIds(visibleRows), true);
  }

  function toggleFolder(folderId: string): void {
    const next = new Set(expandedFolders);
    if (next.has(folderId)) next.delete(folderId);
    else next.add(folderId);
    expandedFolders = next;
  }

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }
</script>

<svelte:window onkeydown={handleProfilePictureKeydown} />

<section class="page user-browser-page">
  <header class="user-browser-heading">
    <div>
      <p class="eyebrow">Network</p>
      <h1>User browser</h1>
    </div>
    <nav class="user-view-tabs" aria-label="User browser views">
      <button type="button" class:active={view === 'user'} aria-current={view === 'user' ? 'page' : undefined} onclick={() => onviewchange('user')}>User</button>
      <button type="button" class:active={view === 'shares'} aria-current={view === 'shares' ? 'page' : undefined} onclick={() => onviewchange('shares')}>Shares</button>
    </nav>
  </header>

  {#if view === 'user'}
    <article class="user-profile-card">
      <div class="user-profile-primary">
        {#if fixture.profile.imageUrl}
          <button
            type="button"
            class="user-profile-picture-button"
            aria-label={`View ${displayUsername}'s profile picture`}
            onclick={() => (profilePictureOpen = true)}
          >
            <img class="user-profile-picture" src={fixture.profile.imageUrl} alt="" />
          </button>
        {:else}
          <div class="user-profile-picture user-profile-placeholder" aria-label="No profile picture">
            <Icon name="user" />
            <span>{initials(displayUsername)}</span>
          </div>
        {/if}

        <div class="user-profile-copy">
          <div class="user-profile-name-line">
            <h2>{displayUsername}</h2>
            <span class={`user-presence ${fixture.profile.presence}`}><i></i>{presenceLabel(fixture.profile.presence)}</span>
          </div>
          {#if fixture.profile.description}
            <p class="user-profile-description"><LinkifiedText text={fixture.profile.description} /></p>
          {/if}
          <div class="user-profile-actions">
            <button type="button" class="user-message-button" onclick={() => onmessageuser(displayUsername)}>
              <Icon name="chat" />
              <span>Message</span>
            </button>
          </div>
        </div>

        <div class="user-capacity-card">
          <div class="user-capacity-heading">
            <span>Upload capacity</span>
            <span class:available={fixture.profile.hasFreeUploadSlot} class="slot-state">
              <i></i>{fixture.profile.hasFreeUploadSlot ? 'Free slot' : 'No free slot'}
            </span>
          </div>
          <div class="user-capacity-values">
            <div><strong>{fixture.profile.uploadSlots}</strong><span>slots</span></div>
            <div><strong>{fixture.profile.queuedUploads}</strong><span>queued</span></div>
          </div>
        </div>
      </div>

      <div class="user-profile-stats">
        <div>
          <span>Shared files</span>
          <strong>{formatInteger(metrics.files)}</strong>
        </div>
        <div>
          <span>Shared folders</span>
          <strong>{formatInteger(metrics.folders)}</strong>
        </div>
        <div>
          <span>Average upload speed</span>
          <strong>{formatSpeed(fixture.profile.averageUploadSpeed) ?? '—'}</strong>
        </div>
        <div>
          <span>Uploads</span>
          <strong>{formatInteger(fixture.profile.uploadCount)}</strong>
        </div>
      </div>
    </article>
  {:else}
    {@const visibleRows = visibleShareRows()}
    {@const visibleFiles = visibleFileIds(visibleRows)}
    <div class="shares-overview">
      <div class="shares-title-row">
        <div>
          <span class="shares-username">{displayUsername}</span>
          <strong>{formatShareSize(metrics.sizeBytes)} shared</strong>
        </div>
      </div>

      <div class="shares-refine-row">
        <ResultFilterControl bind:value={filterText} placeholder="Filter shared files…" ariaLabel="Filter user shares" />
        {#if filterText}
          <span class="shares-filter-count">{visibleFiles.length} matching {visibleFiles.length === 1 ? 'file' : 'files'}</span>
        {/if}
      </div>

      <SelectionToolbar
        visibleLabel={`${visibleFiles.length} ${visibleFiles.length === 1 ? 'file' : 'files'} visible`}
        selectedCount={selected.size}
        allVisibleSelected={allSelected(visibleFiles)}
        ontogglevisible={(checked) => toggleIds(visibleFiles, checked)}
        onselectvisible={() => selectVisible(visibleRows)}
        onclear={() => (selected = new Set())}
      />

      {#if visibleRows.length}
        <div class="share-tree" aria-label={`${displayUsername} shared files`}>
          {#each visibleRows as row (row.id)}
            {#if row.kind === 'folder'}
              <div class="share-tree-row folder" style={`--share-depth:${row.depth}`}>
                <input
                  type="checkbox"
                  checked={allSelected(row.fileIds)}
                  use:indeterminate={partiallySelected(row.fileIds)}
                  aria-label={`Select ${row.path}`}
                  onchange={(event) => toggleIds(row.fileIds, (event.currentTarget as HTMLInputElement).checked)}
                />
                <button type="button" class:expanded={filterText || expandedFolders.has(row.id)} class="share-tree-toggle" aria-label={`${filterText || expandedFolders.has(row.id) ? 'Collapse' : 'Expand'} ${row.name}`} onclick={() => toggleFolder(row.id)} disabled={Boolean(filterText)}>
                  <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m6 3.5 4.5 4.5L6 12.5" /></svg>
                </button>
                <span class="share-tree-icon"><Icon name="folder" /></span>
                <strong class="share-tree-name" title={row.path}>{row.name}</strong>
                <span class="share-tree-folder-meta">{row.fileIds.length} {row.fileIds.length === 1 ? 'file' : 'files'}</span>
                <span class="share-tree-size">{formatShareSize(row.sizeBytes)}</span>
              </div>
            {:else}
              <label class="share-tree-row file" style={`--share-depth:${row.depth}`}>
                <input
                  type="checkbox"
                  checked={selected.has(row.id)}
                  onchange={(event) => toggleIds([row.id], (event.currentTarget as HTMLInputElement).checked)}
                />
                <span class="share-tree-toggle-spacer"></span>
                <span class="share-tree-icon"><Icon name="file" /></span>
                <strong class="share-tree-name" title={row.path}>{row.name}</strong>
                <span class="share-tree-size">{formatShareSize(row.sizeBytes)}</span>
              </label>
            {/if}
          {/each}
        </div>
      {:else}
        <div class="search-results-empty">
          <strong>No matching shared files</strong>
          <span>Try a different filter.</span>
        </div>
      {/if}
    </div>
  {/if}
</section>

{#if profilePictureOpen && fixture.profile.imageUrl}
  <div class="profile-picture-lightbox">
    <button
      type="button"
      class="profile-picture-lightbox-backdrop"
      aria-label="Close profile picture"
      onclick={() => (profilePictureOpen = false)}
    ></button>
    <div class="profile-picture-lightbox-dialog" role="dialog" aria-modal="true" aria-label={`${displayUsername} profile picture`}>
      <img src={fixture.profile.imageUrl} alt={`${displayUsername} profile picture`} />
      <button type="button" class="profile-picture-lightbox-close" aria-label="Close profile picture" onclick={() => (profilePictureOpen = false)}>×</button>
    </div>
  </div>
{/if}
