<script lang="ts">
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
  let searchLimit = $state(PAGE_SIZE);
  let automaticLimit = $state(PAGE_SIZE);
  let resourceState = $derived(resourceStateForScenario(scenarioId, 'search-list'));
  let roots = $derived(automaticJobs.filter((job) => !job.wishlist && isSemanticRoot(job, automaticJobs)));
  let searchEntries = $derived([...searches].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)));
  let automaticEntries = $derived(roots
    .map((root) => ({ root, job: presentationTarget(root, automaticJobs) }))
    .sort((a, b) => b.root.createdAtUtc.localeCompare(a.root.createdAtUtc)));
  let hasSearchEntries = $derived(searchEntries.length > 0);
  let hasAutomaticEntries = $derived(automaticEntries.length > 0);
  let splitHistory = $derived(hasSearchEntries && hasAutomaticEntries);

  $effect(() => {
    scenarioId;
    searchLimit = PAGE_SIZE;
    automaticLimit = PAGE_SIZE;
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
</script>

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
            {#each searchEntries.slice(0, searchLimit) as record (record.id)}
              <div class="search-history-row">
                <button type="button" class="search-history-open" onclick={() => onopenrecord(record)}>
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
            {#each automaticEntries.slice(0, automaticLimit) as entry (entry.root.id)}
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
