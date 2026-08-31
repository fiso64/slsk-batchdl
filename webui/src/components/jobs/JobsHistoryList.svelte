<script lang="ts">
  import { tick } from 'svelte';
  import Icon from '../Icon.svelte';
  import LoadMoreButton from '../LoadMoreButton.svelte';
  import MutationStatus from '../MutationStatus.svelte';
  import ResourceStateNotice from '../ResourceStateNotice.svelte';
  import JobCompactRow from './JobCompactRow.svelte';
  import JobTypeBadge from './JobTypeBadge.svelte';
  import type { ScenarioId } from '../../mock/types';
  import type { PrototypeMutationState } from '../../prototype/state';
  import { isAggregateSearchMode, searchModeLabel } from '../../prototype/search';
  import type { SearchRecord } from '../../prototype/search-results';
  import { extractSourceLabel, isSemanticRoot, jobStatusClass, jobStatusLabel, presentationTarget, type AutomaticJobRecord } from '../../prototype/jobs';
  import { resourceStateForScenario } from '../../prototype/resource-state';
  import { blockingKeyboardSurfaceOpen, focusKeyboardItem, keyboardShortcutHasModifier, keyboardTargetIsEditing } from '../../lib/keyboard';

  interface Props {
    scenarioId: ScenarioId;
    searches: SearchRecord[];
    automaticJobs: AutomaticJobRecord[];
    mutation: PrototypeMutationState;
    onopenrecord: (record: SearchRecord) => void;
    onopenjob: (job: AutomaticJobRecord) => void;
    onsearchaction: (record: SearchRecord) => void;
    onautomaticjobaction: (job: AutomaticJobRecord) => void;
  }

  let { scenarioId, searches, automaticJobs, mutation, onopenrecord, onopenjob, onsearchaction, onautomaticjobaction }: Props = $props();

  type HistoryLane = 'search' | 'automatic';

  const PAGE_SIZE = 8;
  let searchLimit = $state(PAGE_SIZE);
  let automaticLimit = $state(PAGE_SIZE);
  let resourceState = $derived(resourceStateForScenario(scenarioId, 'search-list'));
  let roots = $derived(automaticJobs.filter((job) => isSemanticRoot(job, automaticJobs)));
  let searchEntries = $derived([...searches].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)));
  let automaticEntries = $derived(roots
    .map((root) => ({ root, job: presentationTarget(root, automaticJobs) }))
    .sort((a, b) => b.root.createdAtUtc.localeCompare(a.root.createdAtUtc)));
  let hasSearchEntries = $derived(searchEntries.length > 0);
  let hasAutomaticEntries = $derived(automaticEntries.length > 0);
  let splitHistory = $derived(hasSearchEntries && hasAutomaticEntries);
  let visibleSearchEntries = $derived(searchEntries.slice(0, searchLimit));
  let visibleAutomaticEntries = $derived(automaticEntries.slice(0, automaticLimit));
  let keyboardJobKey = $state<string | null>(null);
  let keyboardPageAdvanceLane = $state<HistoryLane | null>(null);

  $effect(() => {
    scenarioId;
    searchLimit = PAGE_SIZE;
    automaticLimit = PAGE_SIZE;
    keyboardJobKey = null;
    keyboardPageAdvanceLane = null;
  });

  function statusLabel(record: SearchRecord): string {
    if (record.status === 'skipped') return jobStatusLabel('skipped', record.skipReason);
    const labels: Record<Exclude<SearchRecord['status'], 'skipped'>, string> = {
      pending: 'Pending', searching: 'Searching', receiving: 'Receiving', complete: 'Complete',
      failed: 'Failed', cancelled: 'Cancelled', interrupted: 'Interrupted',
    };
    return labels[record.status];
  }

  function statusClass(record: SearchRecord): string {
    return record.status === 'skipped' ? jobStatusClass('skipped', record.skipReason) : record.status;
  }

  function isActive(record: SearchRecord): boolean {
    return record.status === 'pending' || record.status === 'searching' || record.status === 'receiving';
  }


  function historyKey(lane: HistoryLane, id: string): string {
    return `${lane}:${id}`;
  }

  function laneKeys(lane: HistoryLane): string[] {
    return lane === 'search'
      ? visibleSearchEntries.map((record) => historyKey('search', record.id))
      : visibleAutomaticEntries.map((entry) => historyKey('automatic', entry.root.id));
  }

  function keyboardJobElement(key: string): HTMLElement | null {
    if (typeof document === 'undefined') return null;
    return Array.from(document.querySelectorAll<HTMLElement>('[data-keyboard-job-focus-key]'))
      .find((element) => element.dataset.keyboardJobFocusKey === key) ?? null;
  }

  function setKeyboardJob(key: string, focus = false, revealViewStart = false): void {
    keyboardJobKey = key;
    if (focus) focusKeyboardItem(keyboardJobElement(key), { revealViewStart });
  }

  function handleWindowFocusIn(event: FocusEvent): void {
    if (!(event.target instanceof Element)) return;
    const row = event.target.closest<HTMLElement>('[data-keyboard-job-key]');
    if (row?.dataset.keyboardJobKey) keyboardJobKey = row.dataset.keyboardJobKey;
  }

  function currentLane(): HistoryLane | null {
    if (keyboardJobKey?.startsWith('search:')) return 'search';
    if (keyboardJobKey?.startsWith('automatic:')) return 'automatic';
    return null;
  }

  function allLaneKeys(lane: HistoryLane): string[] {
    return lane === 'search'
      ? searchEntries.map((record) => historyKey('search', record.id))
      : automaticEntries.map((entry) => historyKey('automatic', entry.root.id));
  }

  async function loadNextHistoryPageAndMove(lane: HistoryLane, previousKey: string): Promise<void> {
    if (keyboardPageAdvanceLane) return;
    const allKeys = allLaneKeys(lane);
    const previousIndex = allKeys.indexOf(previousKey);
    const next = previousIndex >= 0 ? allKeys[previousIndex + 1] : null;
    if (!next) return;
    keyboardPageAdvanceLane = lane;
    if (lane === 'search') searchLimit = Math.min(searchEntries.length, searchLimit + PAGE_SIZE);
    else automaticLimit = Math.min(automaticEntries.length, automaticLimit + PAGE_SIZE);
    await tick();
    keyboardPageAdvanceLane = null;
    if (keyboardJobKey !== previousKey) return;
    setKeyboardJob(next, true);
  }

  function moveHistoryVertical(direction: -1 | 1): boolean {
    const lane = currentLane() ?? (visibleSearchEntries.length ? 'search' : visibleAutomaticEntries.length ? 'automatic' : null);
    if (!lane) return false;
    const keys = laneKeys(lane);
    if (!keys.length) return false;
    const currentIndex = keyboardJobKey ? keys.indexOf(keyboardJobKey) : -1;
    if (direction > 0 && currentIndex === keys.length - 1 && keyboardJobKey && allLaneKeys(lane).length > keys.length) {
      void loadNextHistoryPageAndMove(lane, keyboardJobKey);
      return true;
    }
    const nextIndex = currentIndex < 0
      ? (direction > 0 ? 0 : keys.length - 1)
      : Math.min(keys.length - 1, Math.max(0, currentIndex + direction));
    const next = keys[nextIndex];
    if (!next) return false;
    setKeyboardJob(next, true, nextIndex === 0);
    return true;
  }

  function moveHistoryHorizontal(direction: -1 | 1): boolean {
    if (!splitHistory) return false;
    const lane = currentLane();
    const targetLane: HistoryLane = direction > 0 ? 'automatic' : 'search';
    if (lane === targetLane) return false;
    const targetKeys = laneKeys(targetLane);
    if (!targetKeys.length) return false;
    const sourceKeys = lane ? laneKeys(lane) : [];
    const sourceIndex = keyboardJobKey ? sourceKeys.indexOf(keyboardJobKey) : 0;
    const targetIndex = Math.min(targetKeys.length - 1, Math.max(0, sourceIndex));
    const next = targetKeys[targetIndex];
    if (!next) return false;
    setKeyboardJob(next, true, targetIndex === 0);
    return true;
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (resourceState.blocking || event.defaultPrevented || keyboardShortcutHasModifier(event)) return;
    if (keyboardTargetIsEditing(event.target) || blockingKeyboardSurfaceOpen()) return;
    if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
      if (moveHistoryVertical(event.key === 'ArrowDown' ? 1 : -1)) event.preventDefault();
      return;
    }
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      if (moveHistoryHorizontal(event.key === 'ArrowRight' ? 1 : -1)) event.preventDefault();
    }
  }
</script>

<svelte:window onkeydown={handleWindowKeydown} onfocusin={handleWindowFocusIn} />

{#if resourceState.blocking}
  <ResourceStateNotice state={resourceState} />
{:else}
  <ResourceStateNotice state={resourceState} />
  <MutationStatus state={mutation} />
  {#if hasSearchEntries || hasAutomaticEntries}
    <div class:single-lane={!splitHistory} class="jobs-history-lanes">
      {#if hasSearchEntries}
        <section class="jobs-history-lane" aria-labelledby="search-history-lane-title">
          <header class="jobs-history-lane-heading">
            <h3 id="search-history-lane-title">Searches</h3>
            <span>{searchEntries.length}</span>
          </header>
          <div class="search-history-list mixed-job-history-list jobs-history-lane-list">
            {#each visibleSearchEntries as record (record.id)}
              {@const rowKey = historyKey('search', record.id)}
              <div class="search-history-row" class:keyboard-current={keyboardJobKey === rowKey} data-keyboard-job-key={rowKey} aria-current={keyboardJobKey === rowKey ? 'true' : undefined}>
                <button
                  type="button"
                  class="search-history-open"
                  data-keyboard-job-focus-key={rowKey}
                  tabindex="-1"
                  onfocus={() => setKeyboardJob(rowKey)}
                  onclick={() => onopenrecord(record)}
                >
                  <span class="search-history-query">{record.displayQuery}</span>
                  <span class={`search-status-badge ${statusClass(record)}`}><i></i>{statusLabel(record)}</span>
                  <span class="search-history-context">
                    <JobTypeBadge icon={record.draft.resultMode} label={searchModeLabel(record.draft.resultMode)} tone="automatic" />
                    <span class="stat-separator">·</span>
                    <span>{record.when}</span>
                  </span>
                  <span class="search-history-stats">
                    {#if isAggregateSearchMode(record.draft.resultMode)}
                      <span><strong>{record.aggregateGroupCount ?? 0}</strong> groups</span><span class="stat-separator">·</span>
                    {/if}
                    <span><strong>{record.foundFiles}</strong> files</span><span class="stat-separator">·</span>
                    <span><strong>{record.lockedFiles}</strong> locked</span><span class="stat-separator">·</span>
                    <span><strong>{record.distinctPeers}</strong> peers</span>
                  </span>
                </button>
                <button type="button" class="search-history-remove" aria-label={`${isActive(record) ? 'Cancel' : 'Remove'} ${record.displayQuery}`} title={isActive(record) ? 'Cancel' : 'Remove'} onclick={() => onsearchaction(record)}>
                  <Icon name={isActive(record) ? 'x' : 'trash'} />
                </button>
              </div>
            {/each}
          </div>
          {#if searchEntries.length > searchLimit}
            <LoadMoreButton label="Load earlier searches" onclick={() => (searchLimit = Math.min(searchEntries.length, searchLimit + PAGE_SIZE))} />
          {/if}
        </section>
      {/if}

      {#if hasAutomaticEntries}
        <section class="jobs-history-lane" aria-labelledby="automatic-history-lane-title">
          <header class="jobs-history-lane-heading">
            <h3 id="automatic-history-lane-title">Other jobs</h3>
            <span>{automaticEntries.length}</span>
          </header>
          <div class="search-history-list mixed-job-history-list jobs-history-lane-list">
            {#each visibleAutomaticEntries as entry (entry.root.id)}
              <JobCompactRow
                job={entry.job}
                allJobs={automaticJobs}
                titleOverride={entry.root.title}
                contextOverride={entry.root.kind === 'extract' ? `${extractSourceLabel(entry.root.payload.sourceType)} import` : undefined}
                typeToneOverride={entry.root.kind === 'extract' ? 'import' : undefined}
                whenOverride={entry.root.when}
                keyboardKey={historyKey('automatic', entry.root.id)}
                keyboardCurrent={keyboardJobKey === historyKey('automatic', entry.root.id)}
                onkeyboardfocus={() => setKeyboardJob(historyKey('automatic', entry.root.id))}
                onclick={() => onopenjob(entry.job)}
                onaction={() => onautomaticjobaction(entry.job)}
              />
            {/each}
          </div>
          {#if automaticEntries.length > automaticLimit}
            <LoadMoreButton label="Load earlier jobs" onclick={() => (automaticLimit = Math.min(automaticEntries.length, automaticLimit + PAGE_SIZE))} />
          {/if}
        </section>
      {/if}
    </div>
  {:else}
    <div class="empty-state">No jobs yet.</div>
  {/if}
{/if}
