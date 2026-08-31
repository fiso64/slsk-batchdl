<script lang="ts">
  import Icon from '../Icon.svelte';
  import UsernameLink from '../UsernameLink.svelte';
  import JobTypeBadge, { type JobTypeBadgeTone } from './JobTypeBadge.svelte';
  import { formatBytes } from '../../prototype/items';
  import type { UserLinkActions } from '../../prototype/navigation';
  import {
    effectiveJobProgress,
    effectiveJobSkipReason,
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
    typeToneOverride?: JobTypeBadgeTone;
    userActions?: UserLinkActions;
    keyboardKey?: string;
    keyboardCurrent?: boolean;
    onkeyboardfocus?: () => void;
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
    typeToneOverride,
    userActions,
    keyboardKey,
    keyboardCurrent = false,
    onkeyboardfocus,
  }: Props = $props();
  let jobSet = $derived(allJobs.length ? allJobs : [job]);
  let displayStatus = $derived(effectiveJobStatus(job, jobSet));
  let displaySkipReason = $derived(effectiveJobSkipReason(job, jobSet));
  let displayProgress = $derived(effectiveJobProgress(job, jobSet));
  let active = $derived(isAutomaticJobActive(job, jobSet));
  let actionLabel = $derived(active ? 'Cancel' : 'Remove');

  function contextLabel(): string {
    if (contextOverride) return contextOverride;
    if (job.kind === 'extract') return `${extractSourceLabel(job.payload.sourceType)} import`;
    return jobKindLabel(job.kind);
  }

  function typeTone(): JobTypeBadgeTone {
    if (typeToneOverride) return typeToneOverride;
    return job.kind === 'extract' ? 'import' : 'automatic';
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


  function remoteContext(): { username: string; path: string } | null {
    if (job.kind === 'song' && job.payload.resolved) {
      return { username: job.payload.resolved.username, path: job.payload.resolved.filename };
    }
    if (job.kind === 'album' && job.payload.resolved) {
      return { username: job.payload.resolved.username, path: job.payload.resolved.folderPath };
    }
    if (job.kind === 'remote-file') {
      return { username: job.payload.username, path: job.payload.path };
    }
    if (job.kind === 'remote-directory') {
      return { username: job.payload.username, path: job.payload.folderPath };
    }
    return null;
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
  {@const remote = remoteContext()}
  <div class:has-progress={percent !== null && active} class="job-child-row">
    <div class="job-child-open">
      <button type="button" class="job-child-open-target" aria-label={`Open ${titleOverride ?? job.title}`} onclick={onclick}></button>
      <span class="job-child-icon"><Icon name={jobKindIcon(job.kind)} /></span>
      <span class="job-child-copy">
        <strong>{titleOverride ?? job.title}</strong>
        <small class="job-child-context-line">
          <span>{contextLabel()}</span><span class="stat-separator">·</span><span>{whenOverride ?? job.when}</span>
          {#if remote}
            <span class="stat-separator">·</span>
            {#if userActions}
              <UsernameLink username={remote.username} actions={userActions} />
            {:else}
              <span>{remote.username}</span>
            {/if}
            <span class="stat-separator">·</span><span class="job-child-remote-path" title={remote.path}>{remote.path}</span>
          {/if}
        </small>
      </span>
      <span class="job-child-meta">
        {#if rowStats.length}
          <span class="job-child-stat">{rowStats[0]}</span>
          <span class="stat-separator">·</span>
        {/if}
        <span class={`search-status-badge ${jobStatusClass(displayStatus, displaySkipReason)}`}><i></i>{jobStatusLabel(displayStatus, displaySkipReason)}</span>
      </span>
      {#if percent !== null && active}
        <span class="job-child-progress" aria-label={`${Math.round(percent)}% complete`}><i style={`width:${percent}%`}></i></span>
      {/if}
    </div>
    {#if onaction}
      <button type="button" class="job-row-action" aria-label={`${actionLabel} ${titleOverride ?? job.title}`} title={actionLabel} onclick={onaction}><Icon name={active ? 'x' : 'trash'} /></button>
    {/if}
  </div>
{:else}
  <div
    class="search-history-row automatic-history-row"
    class:keyboard-current={keyboardCurrent}
    data-keyboard-job-key={keyboardKey}
    aria-current={keyboardCurrent ? 'true' : undefined}
  >
    <button
      type="button"
      class="search-history-open automatic-history-open"
      data-keyboard-job-focus-key={keyboardKey}
      tabindex={keyboardKey ? -1 : undefined}
      onfocus={onkeyboardfocus}
      {onclick}
    >
      <span class="search-history-query">{titleOverride ?? job.title}</span>
      <span class={`search-status-badge ${jobStatusClass(displayStatus, displaySkipReason)}`}><i></i>{jobStatusLabel(displayStatus, displaySkipReason)}</span>
      <span class="search-history-context">
        <JobTypeBadge icon={jobKindIcon(job.kind)} label={contextLabel()} tone={typeTone()} />
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
        <Icon name={active ? 'x' : 'trash'} />
      </button>
    {:else}
      <span class="automatic-history-action-space" aria-hidden="true"></span>
    {/if}
  </div>
{/if}
