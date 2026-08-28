<script lang="ts">
  import Icon from '../Icon.svelte';
  import { formatBytes } from '../../prototype/items';
  import {
    effectiveJobProgress,
    effectiveJobStatus,
    extractSourceLabel,
    isAutomaticJobActive,
    jobKindIcon,
    jobKindLabel,
    jobStatusClass,
    jobStatusLabel,
    type AutomaticJobRecord,
  } from '../../prototype/jobs';

  interface Props {
    job: AutomaticJobRecord;
    allJobs?: AutomaticJobRecord[];
    onclick: () => void;
    onaction?: () => void;
    compact?: boolean;
    titleOverride?: string;
    contextOverride?: string;
    whenOverride?: string;
  }

  let {
    job,
    allJobs = [],
    onclick,
    onaction,
    compact = false,
    titleOverride,
    contextOverride,
    whenOverride,
  }: Props = $props();
  let jobSet = $derived(allJobs.length ? allJobs : [job]);
  let displayStatus = $derived(effectiveJobStatus(job, jobSet));
  let displayProgress = $derived(effectiveJobProgress(job, jobSet));
  let active = $derived(isAutomaticJobActive(job, jobSet));
  let actionLabel = $derived(active ? 'Cancel' : 'Remove');

  function contextLabel(): string {
    if (contextOverride) return contextOverride;
    if (job.kind === 'extract') return `${extractSourceLabel(job.payload.sourceType)} import`;
    return jobKindLabel(job.kind);
  }

  function stats(): string[] {
    const values: string[] = [];
    if (displayProgress?.total) values.push(`${displayProgress.completed}/${displayProgress.total} complete`);

    if (job.kind === 'song') {
      if (job.payload.candidateCount) values.push(`${job.payload.candidateCount} candidates`);
    } else if (job.kind === 'album') {
      if (job.payload.resultCount) values.push(`${job.payload.resultCount} folders`);
      if (!displayProgress && job.payload.files.length) values.push(`${job.payload.files.length} files`);
    } else if (job.kind === 'aggregate') {
      if (!displayProgress) values.push(`${job.payload.songCount} songs`);
      if (job.payload.failed) values.push(`${job.payload.failed} failed`);
    } else if (job.kind === 'album-aggregate') {
      if (!displayProgress) values.push(`${job.payload.albumCount} albums`);
      if (job.payload.failed) values.push(`${job.payload.failed} failed`);
    } else if (job.kind === 'job-list') {
      if (!displayProgress) values.push(`${job.payload.childCount} jobs`);
      if (job.payload.failed) values.push(`${job.payload.failed} failed`);
    } else if (job.kind === 'remote-file') {
      values.push(formatBytes(job.payload.sizeBytes));
    } else if (job.kind === 'remote-directory') {
      values.push(`${job.payload.files.length} files`);
    } else if (job.kind === 'retrieve-folder') {
      values.push(`${job.payload.newFilesFoundCount} new files`);
    }
    return values.slice(0, compact ? 1 : 3);
  }

  function progressPercent(): number | null {
    if (displayProgress?.total) return Math.max(0, Math.min(100, displayProgress.completed / displayProgress.total * 100));
    if (job.kind === 'song' || job.kind === 'album' || job.kind === 'remote-file' || job.kind === 'remote-directory') {
      return job.payload.transfer?.progressPercent ?? null;
    }
    return null;
  }
</script>

{#if compact}
  {@const percent = progressPercent()}
  {@const rowStats = stats()}
  <div class:has-progress={percent !== null && active} class="job-child-row">
    <button type="button" class="job-child-open" {onclick}>
      <span class="job-child-icon"><Icon name={jobKindIcon(job.kind)} /></span>
      <span class="job-child-copy">
        <strong>{titleOverride ?? job.title}</strong>
        <small>{contextLabel()} · {whenOverride ?? job.when}</small>
      </span>
      <span class="job-child-meta">
        {#if rowStats.length}
          <span class="job-child-stat">{rowStats[0]}</span>
          <span class="stat-separator">·</span>
        {/if}
        <span class={`search-status-badge ${jobStatusClass(displayStatus)}`}><i></i>{jobStatusLabel(displayStatus)}</span>
      </span>
      {#if percent !== null && active}
        <span class="job-child-progress" aria-label={`${Math.round(percent)}% complete`}><i style={`width:${percent}%`}></i></span>
      {/if}
    </button>
    {#if onaction}
      <button type="button" class="job-row-action" aria-label={`${actionLabel} ${titleOverride ?? job.title}`} title={actionLabel} onclick={onaction}><Icon name="x" /></button>
    {/if}
  </div>
{:else}
  <div class="search-history-row automatic-history-row">
    <button type="button" class="search-history-open automatic-history-open" {onclick}>
      <span class="search-history-query">{titleOverride ?? job.title}</span>
      <span class={`search-status-badge ${jobStatusClass(displayStatus)}`}><i></i>{jobStatusLabel(displayStatus)}</span>
      <span class="search-history-context">
        <span class="automatic-history-icon"><Icon name={jobKindIcon(job.kind)} /></span>
        <span>{contextLabel()}</span>
        <span class="stat-separator">·</span>
        <span>{whenOverride ?? job.when}</span>
      </span>
      {#if stats().length}
        <span class="search-history-stats">
          {#each stats() as stat, index}
            {#if index}<span class="stat-separator">·</span>{/if}
            <span>{stat}</span>
          {/each}
        </span>
      {/if}
    </button>
    {#if onaction}
      <button type="button" class="search-history-remove" aria-label={`${actionLabel} ${titleOverride ?? job.title}`} title={actionLabel} onclick={onaction}>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg>
      </button>
    {:else}
      <span class="automatic-history-action-space" aria-hidden="true"></span>
    {/if}
  </div>
{/if}
