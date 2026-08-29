<script lang="ts">
  import LoadMoreButton from '../LoadMoreButton.svelte';
  import MutationStatus from '../MutationStatus.svelte';
  import ResourceStateNotice from '../ResourceStateNotice.svelte';
  import JobCompactRow from './JobCompactRow.svelte';
  import JobTypeBadge from './JobTypeBadge.svelte';
  import type { ScenarioId } from '../../mock/types';
  import type { PrototypeMutationState } from '../../prototype/state';
  import { isAggregateSearchMode, searchModeLabel } from '../../prototype/search';
  import type { SearchRecord } from '../../prototype/search-results';
  import { extractSourceLabel, isSemanticRoot, presentationTarget, type AutomaticJobRecord } from '../../prototype/jobs';
  import { resourceStateForScenario } from '../../prototype/resource-state';

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

  const PAGE_SIZE = 8;
  let limit = $state(PAGE_SIZE);
  let resourceState = $derived(resourceStateForScenario(scenarioId, 'search-list'));
  let roots = $derived(automaticJobs.filter((job) => isSemanticRoot(job, automaticJobs)));
  let entries = $derived([
    ...searches.map((record) => ({ type: 'search' as const, id: record.id, createdAtUtc: record.createdAtUtc, record })),
    ...roots.map((root) => ({ type: 'automatic' as const, id: root.id, createdAtUtc: root.createdAtUtc, root, job: presentationTarget(root, automaticJobs) })),
  ].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)));

  $effect(() => {
    scenarioId;
    limit = PAGE_SIZE;
  });

  function statusLabel(status: SearchRecord['status']): string {
    const labels: Record<SearchRecord['status'], string> = {
      pending: 'Pending', searching: 'Searching', receiving: 'Receiving', complete: 'Complete',
      failed: 'Failed', cancelled: 'Cancelled', skipped: 'Skipped', interrupted: 'Interrupted',
    };
    return labels[status];
  }

  function isActive(record: SearchRecord): boolean {
    return record.status === 'pending' || record.status === 'searching' || record.status === 'receiving';
  }
</script>

{#if resourceState.blocking}
  <ResourceStateNotice state={resourceState} />
{:else}
  <ResourceStateNotice state={resourceState} />
  <MutationStatus state={mutation} />
  <div class="search-history-list mixed-job-history-list">
    {#each entries.slice(0, limit) as entry (entry.id)}
      {#if entry.type === 'search'}
        {@const record = entry.record}
        <div class="search-history-row">
          <button type="button" class="search-history-open" onclick={() => onopenrecord(record)}>
            <span class="search-history-query">{record.displayQuery}</span>
            <span class={`search-status-badge ${record.status}`}><i></i>{statusLabel(record.status)}</span>
            <span class="search-history-context">
              <JobTypeBadge icon={record.draft.resultMode} label={searchModeLabel(record.draft.resultMode)} tone="search" />
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
            <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg>
          </button>
        </div>
      {:else}
        <JobCompactRow
          job={entry.job}
          allJobs={automaticJobs}
          titleOverride={entry.root.title}
          contextOverride={entry.root.kind === 'extract' ? `${extractSourceLabel(entry.root.payload.sourceType)} import` : undefined}
          typeToneOverride={entry.root.kind === 'extract' ? 'import' : undefined}
          whenOverride={entry.root.when}
          onclick={() => onopenjob(entry.job)}
          onaction={() => onautomaticjobaction(entry.job)}
        />
      {/if}
    {:else}
      <div class="empty-state">No jobs yet.</div>
    {/each}
  </div>
  {#if entries.length > limit}
    <LoadMoreButton label="Load earlier jobs" onclick={() => (limit = Math.min(entries.length, limit + PAGE_SIZE))} />
  {/if}
{/if}
