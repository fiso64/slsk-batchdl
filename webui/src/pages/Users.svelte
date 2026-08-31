<script lang="ts">
  import { tick } from 'svelte';
  import Icon from '../components/Icon.svelte';
  import LinkifiedText from '../components/LinkifiedText.svelte';
  import ResultFilterControl from '../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../components/SelectionToolbar.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import { blockingKeyboardSurfaceOpen, focusFirstKeyboardItemControl, focusKeyboardItem, keyboardShortcutHasModifier, keyboardTargetIsEditing, keyboardTargetUsesNativeActivation } from '../lib/keyboard';
  import MutationStatus from '../components/MutationStatus.svelte';
  import type { ScenarioId } from '../mock/types';
  import type { PrototypeDownloadSelectionSummary, PrototypeMutationState } from '../prototype/state';
  import { formatSpeed } from '../prototype/transfers';
  import { resourceStateForScenario } from '../prototype/resource-state';
  import { fileTypeIcon } from '../prototype/file-types';
  import {
    flattenShareTree,
    formatShareSize,
    getUserBrowseFixtureForUsername,
    requestShareTreeProjection,
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
  let sharePagesRequested = $state(1);
  let shareRequestKey = '';
  let mutation = $state<PrototypeMutationState>({ phase: 'idle' });
  let shareKeyboardKey = $state<string | null>(null);
  let shareKeyboardPageAdvancePending = $state(false);

  let fixture = $derived(getUserBrowseFixtureForUsername(scenarioId, username));
  let rows = $derived(flattenShareTree(fixture.shares));
  let shareProjection = $derived(currentShareProjection());
  let displayUsername = $derived(fixture.profileDto.username);
  let profileState = $derived(resourceStateForScenario(scenarioId, 'profile'));
  let sharesState = $derived.by(() => {
    const state = resourceStateForScenario(scenarioId, 'shares');
    // Entering Shares represents issuing/reissuing the browse request. The
    // production adapter should reacquire expired browse artifacts automatically.
    return view === 'shares' && state.phase === 'expired' ? { phase: 'ready' as const } : state;
  });
  let profile = $derived(fixture.profileDto);
  let selectedSummary = $derived(shareSelectionSummary());

  $effect(() => {
    const key = fixture.profile.username;
    if (key === fixtureKey) return;
    fixtureKey = key;
    filterText = '';
    selected = new Set();
    sharePagesRequested = 1;
    mutation = { phase: 'idle' };
    shareKeyboardKey = null;
    shareKeyboardPageAdvancePending = false;
    expandedFolders = new Set(rows.filter((row) => row.kind === 'folder' && row.depth === 0).map((row) => row.id));
  });

  $effect(() => {
    const key = `${fixture.profile.username}\u0000${filterText.trim()}`;
    if (key === shareRequestKey) return;
    shareRequestKey = key;
    sharePagesRequested = 1;
    shareKeyboardKey = null;
    shareKeyboardPageAdvancePending = false;
  });

  function currentShareProjection() {
    const query = filterText.trim() || null;
    let page = requestShareTreeProjection(fixture.shares, { query, cursor: null, limit: 18 });
    const rows = [...page.rows];
    for (let pageIndex = 1; pageIndex < sharePagesRequested && page.nextCursor; pageIndex += 1) {
      page = requestShareTreeProjection(fixture.shares, { query, cursor: page.nextCursor, limit: 18 });
      rows.push(...page.rows);
    }
    return { ...page, rows };
  }

  function shareKeyboardRowKey(row: ShareTreeRow): string {
    return `share:${row.kind}:${row.id}`;
  }

  function shareKeyboardRows(): ShareTreeRow[] {
    return visibleShareRows(shareProjection.rows);
  }

  function shareKeyboardElement(key: string): HTMLElement | null {
    if (typeof document === 'undefined') return null;
    return Array.from(document.querySelectorAll<HTMLElement>('[data-keyboard-share-key]'))
      .find((element) => element.dataset.keyboardShareKey === key) ?? null;
  }

  function handleWindowFocusIn(event: FocusEvent): void {
    if (view !== 'shares' || !(event.target instanceof Element)) return;
    const row = event.target.closest<HTMLElement>('[data-keyboard-share-key]');
    if (row?.dataset.keyboardShareKey) shareKeyboardKey = row.dataset.keyboardShareKey;
  }

  function currentShareKeyboardRow(): ShareTreeRow | null {
    if (!shareKeyboardKey) return null;
    return shareKeyboardRows().find((candidate) => shareKeyboardRowKey(candidate) === shareKeyboardKey) ?? null;
  }

  async function loadNextShareKeyboardPage(previousKey: string): Promise<void> {
    if (!shareProjection.nextCursor || shareKeyboardPageAdvancePending) return;
    shareKeyboardPageAdvancePending = true;
    sharePagesRequested += 1;
    await tick();
    shareKeyboardPageAdvancePending = false;
    if (shareKeyboardKey !== previousKey) return;
    const items = shareKeyboardRows();
    const previousIndex = items.findIndex((row) => shareKeyboardRowKey(row) === previousKey);
    const next = previousIndex >= 0 ? items[previousIndex + 1] : null;
    if (!next) return;
    shareKeyboardKey = shareKeyboardRowKey(next);
    focusKeyboardItem(shareKeyboardElement(shareKeyboardKey));
  }

  function moveShareKeyboardRow(direction: -1 | 1): boolean {
    const items = shareKeyboardRows();
    if (!items.length) return false;
    const currentIndex = shareKeyboardKey ? items.findIndex((row) => shareKeyboardRowKey(row) === shareKeyboardKey) : -1;
    if (direction > 0 && currentIndex === items.length - 1 && shareKeyboardKey && shareProjection.nextCursor) {
      void loadNextShareKeyboardPage(shareKeyboardKey);
      return true;
    }
    const nextIndex = currentIndex < 0
      ? (direction > 0 ? 0 : items.length - 1)
      : Math.min(items.length - 1, Math.max(0, currentIndex + direction));
    const next = items[nextIndex];
    if (!next) return false;
    shareKeyboardKey = shareKeyboardRowKey(next);
    focusKeyboardItem(shareKeyboardElement(shareKeyboardKey), { revealViewStart: nextIndex === 0 });
    return true;
  }

  function toggleCurrentShareKeyboardRow(): boolean {
    const row = currentShareKeyboardRow();
    if (!row) return false;
    const ids = row.kind === 'folder' ? row.fileIds : [row.id];
    toggleIds(ids, !allSelected(ids));
    return true;
  }

  function toggleCurrentShareFolder(): boolean {
    const row = currentShareKeyboardRow();
    if (!row || row.kind !== 'folder' || Boolean(filterText.trim())) return false;
    toggleFolder(row.id);
    return true;
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && profilePictureOpen) {
      profilePictureOpen = false;
      return;
    }
    if (view !== 'shares' || sharesState.blocking || event.defaultPrevented || keyboardShortcutHasModifier(event)) return;
    if (keyboardTargetIsEditing(event.target) || blockingKeyboardSurfaceOpen()) return;
    if (event.key === 'Tab' && !event.shiftKey && shareKeyboardKey) {
      const currentElement = shareKeyboardElement(shareKeyboardKey);
      if (event.target === currentElement && focusFirstKeyboardItemControl(currentElement)) event.preventDefault();
      return;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (moveShareKeyboardRow(event.key === 'ArrowDown' ? 1 : -1)) event.preventDefault();
      return;
    }
    if (event.key === 'ArrowRight') {
      if (toggleCurrentShareFolder()) event.preventDefault();
      return;
    }
    if (event.repeat) return;
    if (event.key === ' ' && !keyboardTargetUsesNativeActivation(event.target) && toggleCurrentShareKeyboardRow()) event.preventDefault();
  }

  function presenceLabel(presence: UserPresence): string {
    if (presence === 'away') return 'Away';
    if (presence === 'offline') return 'Offline';
    if (presence === 'unknown') return 'Unknown';
    return 'Online';
  }

  function initials(value: string): string {
    const pieces = value.split(/[^a-z0-9]+/i).filter(Boolean);
    if (pieces.length === 0) return '?';
    return pieces.slice(0, 2).map((piece) => piece[0]!.toUpperCase()).join('');
  }

  function formatInteger(value: number | string | null | undefined): string {
    if (value === null || value === undefined) return '—';
    const numeric = typeof value === 'number' ? value : Number(value);
    return Number.isFinite(numeric) ? new Intl.NumberFormat('en-US').format(numeric) : '—';
  }

  function visibleShareRows(projectedRows: ShareTreeRow[]): ShareTreeRow[] {
    if (filterText.trim()) return projectedRows;
    return projectedRows.filter((row) => row.parentFolderIds.every((id) => expandedFolders.has(id)));
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

  function toggleFolder(folderId: string): void {
    const next = new Set(expandedFolders);
    if (next.has(folderId)) next.delete(folderId);
    else next.add(folderId);
    expandedFolders = next;
  }


  function shareSelectionSummary(): PrototypeDownloadSelectionSummary {
    const selectedRows = rows.filter((row) => row.kind === 'file' && selected.has(row.id));
    const requestedCount = selectedRows.length;
    const lockedCount = selectedRows.filter((row) => row.visibility !== 'public').length;
    return { requestedCount, uniqueFileCount: requestedCount, resolvablePublicCount: requestedCount - lockedCount, lockedCount, skippedCount: lockedCount };
  }

  function requestSelectedSharesDownload(): void {
    const summary = shareSelectionSummary();
    if (!summary.resolvablePublicCount) {
      mutation = { phase: 'rejected', label: 'Nothing downloadable', detail: `${summary.lockedCount} selected file${summary.lockedCount === 1 ? '' : 's'} unavailable.` };
      return;
    }
    mutation = { phase: 'pending', label: `Requesting ${summary.resolvablePublicCount} shared file${summary.resolvablePublicCount === 1 ? '' : 's'}…` };
    mutation = summary.skippedCount
      ? { phase: 'partially-succeeded', label: `${summary.resolvablePublicCount} requested`, detail: `${summary.skippedCount} locked file${summary.skippedCount === 1 ? '' : 's'} skipped.` }
      : { phase: 'succeeded', label: `${summary.resolvablePublicCount} download${summary.resolvablePublicCount === 1 ? '' : 's'} requested` };
  }

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }
</script>

<svelte:window onkeydown={handleWindowKeydown} onfocusin={handleWindowFocusIn} />

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
    <ResourceStateNotice state={profileState} />
    <MutationStatus state={mutation} />
    {#if !profileState.blocking}
    <article class="user-profile-card">
      <div class="user-profile-primary">
        {#if profile.picture?.url}
          <button
            type="button"
            class="user-profile-picture-button"
            aria-label={`View ${displayUsername}'s profile picture`}
            onclick={() => (profilePictureOpen = true)}
          >
            <img class="user-profile-picture" src={profile.picture.url} alt="" />
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
            <span class={`user-presence ${profile.presence}`}><i></i>{presenceLabel(profile.presence)}</span>
          </div>
          {#if profile.description}
            <p class="user-profile-description"><LinkifiedText text={profile.description} /></p>
          {/if}
          <small class="user-profile-observed">Observed {new Date(profile.observedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</small>
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
            <span class:available={profile.hasFreeUploadSlot === true} class:unknown={profile.hasFreeUploadSlot === null} class="slot-state">
              <i></i>{profile.hasFreeUploadSlot === null ? 'Unknown' : profile.hasFreeUploadSlot ? 'Free slot' : 'No free slot'}
            </span>
          </div>
          <div class="user-capacity-values">
            <div><strong>{profile.uploadSlots ?? '—'}</strong><span>slots</span></div>
            <div><strong>{profile.queueLength ?? '—'}</strong><span>queued</span></div>
          </div>
        </div>
      </div>

      {#if profile.info.state !== 'available' || profile.statistics.state !== 'available' || profile.pictureSection.state !== 'available'}
        <div class="profile-section-states" aria-label="Profile section availability">
          {#if profile.info.state !== 'available'}<span>Info {profile.info.state}{profile.info.reason ? ` · ${profile.info.reason}` : ''}</span>{/if}
          {#if profile.statistics.state !== 'available'}<span>Statistics {profile.statistics.state}{profile.statistics.reason ? ` · ${profile.statistics.reason}` : ''}</span>{/if}
          {#if profile.pictureSection.state !== 'available'}<span>Picture {profile.pictureSection.state}{profile.pictureSection.reason ? ` · ${profile.pictureSection.reason}` : ''}</span>{/if}
        </div>
      {/if}

      <div class="user-profile-stats">
        <div>
          <span>Shared files</span>
          <strong>{formatInteger(profile.sharedFileCount)}</strong>
        </div>
        <div>
          <span>Shared folders</span>
          <strong>{formatInteger(profile.sharedDirectoryCount)}</strong>
        </div>
        <div>
          <span>Average upload speed</span>
          <strong>{formatSpeed(profile.averageUploadSpeed) ?? '—'}</strong>
        </div>
        <div>
          <span>Uploads</span>
          <strong>{formatInteger(profile.uploadCount)}</strong>
        </div>
      </div>
    </article>
    {/if}
  {:else}
    {@const visibleRows = visibleShareRows(shareProjection.rows)}
    {#if sharesState.blocking}
      <ResourceStateNotice state={sharesState} />
    {:else}
      <ResourceStateNotice state={sharesState} />
      <MutationStatus state={mutation} />
    <div class="shares-overview">
      <div class="shares-title-row">
        <div>
          <span class="shares-username">{displayUsername}</span>
          <strong>{formatShareSize(Number(fixture.browseDto.totalFileBytes))} shared</strong>
        </div>
      </div>

      <div class="shares-refine-row">
        <ResultFilterControl bind:value={filterText} placeholder="Filter shared files…" ariaLabel="Filter user shares" />
        {#if filterText}
          <span class="shares-filter-count">{shareProjection.matchingFileCount} matching {shareProjection.matchingFileCount === 1 ? 'file' : 'files'}</span>
        {/if}
      </div>
      <SelectionToolbar
        selectedCount={selectedSummary.requestedCount}
        floatingLabel={`Download ${selectedSummary.resolvablePublicCount}`}
        detail={selectedSummary.lockedCount ? `${selectedSummary.requestedCount} selected · ${selectedSummary.lockedCount} locked` : undefined}
        actionDisabled={selectedSummary.resolvablePublicCount === 0}
        onclear={() => (selected = new Set())}
        onaction={requestSelectedSharesDownload}
      />

      {#if visibleRows.length}
        <div class="share-tree" aria-label={`${displayUsername} shared files`}>
          {#each visibleRows as row (row.id)}
            {#if row.kind === 'folder'}
              <div
                class="share-tree-row folder"
                class:keyboard-current={shareKeyboardKey === shareKeyboardRowKey(row)}
                data-keyboard-share-key={shareKeyboardRowKey(row)}
                tabindex="-1"
                aria-current={shareKeyboardKey === shareKeyboardRowKey(row) ? 'true' : undefined}
                style={`--share-depth:${row.depth}`}
              >
                <input
                  type="checkbox"
                  checked={allSelected(row.fileIds)}
                  use:indeterminate={partiallySelected(row.fileIds)}
                  aria-label={`Select ${row.path}`}
                  onchange={(event) => toggleIds(row.fileIds, (event.currentTarget as HTMLInputElement).checked)}
                />
                <button
                  type="button"
                  class="share-tree-folder-button"
                  aria-label={`${Boolean(filterText) || expandedFolders.has(row.id) ? 'Collapse' : 'Expand'} ${row.name}`}
                  aria-expanded={Boolean(filterText) || expandedFolders.has(row.id)}
                  onclick={() => toggleFolder(row.id)}
                  disabled={Boolean(filterText)}
                >
                  <span class:expanded={Boolean(filterText) || expandedFolders.has(row.id)} class="share-tree-toggle" aria-hidden="true">
                    <svg viewBox="0 0 16 16"><path d="m6 3.5 4.5 4.5L6 12.5" /></svg>
                  </span>
                  <span class="share-tree-icon"><Icon name="folder" /></span>
                  <strong class="share-tree-name" title={row.path}>{row.name}</strong>
                  {#if row.visibility !== 'public'}<span class={`share-visibility ${row.visibility}`}>{row.visibility === 'locked' ? 'Locked' : 'Mixed'}</span>{/if}
                  <span class="share-tree-folder-meta">{row.fileIds.length} {row.fileIds.length === 1 ? 'file' : 'files'}</span>
                  <span class="share-tree-size">{formatShareSize(row.sizeBytes)}</span>
                </button>
              </div>
            {:else}
              <label
                class="share-tree-row file"
                class:keyboard-current={shareKeyboardKey === shareKeyboardRowKey(row)}
                data-keyboard-share-key={shareKeyboardRowKey(row)}
                tabindex="-1"
                aria-current={shareKeyboardKey === shareKeyboardRowKey(row) ? 'true' : undefined}
                style={`--share-depth:${row.depth}`}
              >
                <input
                  type="checkbox"
                  checked={selected.has(row.id)}
                  onchange={(event) => toggleIds([row.id], (event.currentTarget as HTMLInputElement).checked)}
                />
                <span class="share-tree-toggle-spacer"></span>
                <span class="share-tree-icon"><Icon name={fileTypeIcon({ extension: row.extension, filename: row.name })} /></span>
                <strong class="share-tree-name" title={row.path}>{row.name}</strong>
                {#if row.visibility !== 'public'}<span class={`share-visibility ${row.visibility}`}>{row.visibility === 'locked' ? 'Locked' : 'Mixed'}</span>{/if}
                <span class="share-tree-size">{formatShareSize(row.sizeBytes)}</span>
              </label>
            {/if}
          {/each}
        </div>
        {#if shareProjection.nextCursor}
          <LoadMoreButton label="Load more shared entries" onclick={() => (sharePagesRequested += 1)} />
        {/if}
      {:else}
        <div class="search-results-empty">
          <strong>No matching shared files</strong>
          <span>Try a different filter.</span>
        </div>
      {/if}
    </div>
    {/if}
  {/if}
</section>

{#if profilePictureOpen && profile.picture?.url}
  <div class="profile-picture-lightbox">
    <button
      type="button"
      class="profile-picture-lightbox-backdrop"
      aria-label="Close profile picture"
      onclick={() => (profilePictureOpen = false)}
    ></button>
    <div class="profile-picture-lightbox-dialog" role="dialog" aria-modal="true" aria-label={`${displayUsername} profile picture`}>
      <img src={profile.picture.url} alt={`${displayUsername} profile picture`} />
      <button type="button" class="profile-picture-lightbox-close" aria-label="Close profile picture" onclick={() => (profilePictureOpen = false)}>×</button>
    </div>
  </div>
{/if}
