<script lang="ts">
  import FileItemCard from '../items/FileItemCard.svelte';
  import FolderItemCard from '../items/FolderItemCard.svelte';
  import UsernameLink from '../UsernameLink.svelte';
  import Icon from '../Icon.svelte';
  import JobCompactRow from './JobCompactRow.svelte';
  import type { UserLinkActions } from '../../prototype/navigation';
  import {
    extractSourceLabel,
    isAutomaticJobActive,
    jobKindLabel,
    jobStatusClass,
    jobStatusLabel,
    presentationAncestors,
    presentationChildren,
    presentationParent,
    type AutomaticJobRecord,
  } from '../../prototype/jobs';
  import { formatBytes } from '../../prototype/items';

  interface Props {
    job: AutomaticJobRecord;
    allJobs: AutomaticJobRecord[];
    userActions: UserLinkActions;
    onopenjob: (job: AutomaticJobRecord) => void;
    onjobaction: (job: AutomaticJobRecord) => void;
    onback: () => void;
  }

  let { job, allJobs, userActions, onopenjob, onjobaction, onback }: Props = $props();
  let childLimit = $state(8);
  let children = $derived(presentationChildren(job, allJobs));
  let visibleChildren = $derived(children.slice(0, childLimit));
  let ancestors = $derived(presentationAncestors(job, allJobs));
  let parent = $derived(presentationParent(job, allJobs));
  let active = $derived(isAutomaticJobActive(job, allJobs));
  $effect(() => { job.id; childLimit = 8; });

  function headerStats(): string[] {
    if (job.kind === 'song') return job.payload.candidateCount ? [`${job.payload.candidateCount} candidates`] : [];
    if (job.kind === 'album') return [job.payload.resultCount ? `${job.payload.resultCount} folders` : '', job.payload.files.length ? `${job.payload.files.length} files` : ''].filter(Boolean);
    if (job.kind === 'aggregate') return [`${job.payload.songCount} songs`, `${job.payload.succeeded} complete`, ...(job.payload.failed ? [`${job.payload.failed} failed`] : [])];
    if (job.kind === 'album-aggregate') return [`${job.payload.albumCount} albums`, `${job.payload.succeeded} complete`, ...(job.payload.failed ? [`${job.payload.failed} failed`] : [])];
    if (job.kind === 'extract') return [extractSourceLabel(job.payload.sourceType)];
    if (job.kind === 'job-list') return [`${job.payload.childCount} jobs`, `${job.payload.succeeded} complete`, ...(job.payload.failed ? [`${job.payload.failed} failed`] : [])];
    if (job.kind === 'remote-file') return [formatBytes(job.payload.sizeBytes)];
    if (job.kind === 'remote-directory') return [`${job.payload.files.length} files`];
    if (job.kind === 'retrieve-folder') return [`${job.payload.newFilesFoundCount} new files`];
    return [];
  }

  function progressPercent(completed: number, total: number): number {
    return total > 0 ? Math.max(0, Math.min(100, completed / total * 100)) : 0;
  }

  function goBack(): void {
    if (parent) onopenjob(parent);
    else onback();
  }
</script>

<section class="automatic-job-detail">
  <header class="job-detail-heading">
    <button type="button" class="icon-button back-button" aria-label={parent ? `Back to ${parent.title}` : 'Back to jobs'} onclick={goBack}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
    </button>
    <div class="job-detail-title">
      <div class="job-detail-kicker">
        {#each ancestors as ancestor (ancestor.id)}
          <button type="button" onclick={() => onopenjob(ancestor)}>{ancestor.title}</button><span>/</span>
        {/each}
        <span>{jobKindLabel(job.kind)}</span>
      </div>
      <h1>{job.title}</h1>
      {#if job.subtitle}<small>{job.subtitle}</small>{/if}
    </div>
    <div class="job-detail-heading-summary">
      <span class={`search-status-badge ${jobStatusClass(job.status)}`}><i></i>{jobStatusLabel(job.status)}</span>
      {#each headerStats() as stat}<span>{stat}</span>{/each}
      <span>{job.when}</span>
      <button type="button" class="job-detail-lifecycle-action" title={active ? 'Cancel job' : 'Remove job'} onclick={() => onjobaction(job)}><Icon name="x" /><span>{active ? 'Cancel' : 'Remove'}</span></button>
    </div>
  </header>

  {#if job.kind === 'song'}
    {#if job.payload.album}
      <div class="job-detail-context-line"><span>Album</span><strong>{job.payload.album}</strong></div>
    {/if}
    {#if job.payload.resolved}
      <section class="job-content-section">
        <header class="job-section-heading">
          <h2>Selected file</h2>
          <div class="job-source-summary">
            <UsernameLink username={job.payload.resolved.username} actions={userActions} />
            <span>{job.payload.resolved.uploadSpeedMbps.toFixed(1)} MB/s</span>
            <span class:available={job.payload.resolved.freeUploadSlot}>{job.payload.resolved.freeUploadSlot ? 'Free slot' : 'No free slot'}</span>
          </div>
        </header>
        <FileItemCard path={job.payload.resolved.filename} sizeBytes={job.payload.resolved.sizeBytes} audio={job.payload.resolved.audio} transfer={job.payload.transfer} />
      </section>
    {:else}
      <div class="job-detail-empty"><strong>{job.status === 'failed' ? 'No matching file' : 'Finding a file'}</strong><span>{job.status === 'failed' ? 'No candidate satisfied this job.' : 'The job will select the best matching candidate when discovery completes.'}</span></div>
    {/if}

  {:else if job.kind === 'album'}
    {#if job.payload.resolved}
      <section class="job-content-section">
        <header class="job-section-heading">
          <h2>Selected folder</h2>
          <div class="job-source-summary"><UsernameLink username={job.payload.resolved.username} actions={userActions} /></div>
        </header>
        <FolderItemCard path={job.payload.resolved.folderPath} sizeBytes={job.payload.files.reduce((total, file) => total + file.sizeBytes, 0)} files={job.payload.files} totalFileCount={job.payload.files.length} filesComplete transfer={job.payload.transfer} />
      </section>
    {:else}
      <div class="job-detail-empty"><strong>Finding an album</strong><span>The job will select the best matching folder after discovery.</span></div>
    {/if}
    {#if children.length}{@render childJobs('Related jobs')}{/if}

  {:else if job.kind === 'aggregate'}
    {@render aggregateProgress(job.payload.succeeded, job.payload.failed, job.payload.songCount, 'songs')}
    {@render childJobs('Generated songs')}

  {:else if job.kind === 'album-aggregate'}
    {@render aggregateProgress(job.payload.succeeded, job.payload.failed, job.payload.albumCount, 'albums')}
    {@render childJobs('Generated albums')}

  {:else if job.kind === 'extract'}
    <dl class="job-detail-facts">
      <div><dt>Source</dt><dd>{extractSourceLabel(job.payload.sourceType)}</dd></div>
      <div><dt>Input</dt><dd title={job.payload.input}>{job.payload.input}</dd></div>
    </dl>
    {#if children.length}
      {@render childJobs('Result')}
    {:else}
      <div class="job-detail-empty"><strong>{job.status === 'failed' ? 'Extraction failed' : 'Extracting source'}</strong><span>{job.status === 'failed' ? 'The source did not produce a job.' : 'The extracted job will appear here when it is ready.'}</span></div>
    {/if}

  {:else if job.kind === 'job-list'}
    {@render aggregateProgress(job.payload.succeeded, job.payload.failed, job.payload.childCount, 'jobs')}
    {@render childJobs('Jobs')}

  {:else if job.kind === 'remote-file'}
    <div class="job-detail-context-line"><span>User</span><strong><UsernameLink username={job.payload.username} actions={userActions} /></strong></div>
    <section class="job-content-section">
      <header class="job-section-heading"><h2>Remote file</h2></header>
      <FileItemCard path={job.payload.path} sizeBytes={job.payload.sizeBytes} audio={job.payload.audio} transfer={job.payload.transfer} />
    </section>

  {:else if job.kind === 'remote-directory'}
    <div class="job-detail-context-line"><span>User</span><strong><UsernameLink username={job.payload.username} actions={userActions} /></strong></div>
    <section class="job-content-section">
      <header class="job-section-heading"><h2>Remote directory</h2></header>
      <FolderItemCard path={job.payload.folderPath} sizeBytes={job.payload.files.reduce((total, file) => total + file.sizeBytes, 0)} files={job.payload.files} totalFileCount={job.payload.files.length} filesComplete transfer={job.payload.transfer} />
    </section>

  {:else if job.kind === 'retrieve-folder'}
    <dl class="job-detail-facts">
      <div><dt>User</dt><dd><UsernameLink username={job.payload.username} actions={userActions} /></dd></div>
      <div><dt>Folder</dt><dd title={job.payload.folderPath}>{job.payload.folderPath}</dd></div>
      <div><dt>Outcome</dt><dd>{job.payload.outcome}</dd></div>
      <div><dt>New files</dt><dd>{job.payload.newFilesFoundCount}</dd></div>
    </dl>

  {:else}
    <dl class="job-detail-facts"><div><dt>Detail</dt><dd>{job.payload.text}</dd></div></dl>
  {/if}
</section>

{#snippet aggregateProgress(completed: number, failed: number, total: number, noun: string)}
  <section class="job-run-progress" aria-label={`${completed} of ${total} ${noun} complete`}>
    <div class="job-run-progress-copy">
      <strong>{completed} of {total} complete</strong>
      <span>{failed ? `${failed} failed · ` : ''}{Math.max(0, total - completed - failed)} remaining</span>
    </div>
    <div class="job-run-progress-track"><i style={`width:${progressPercent(completed, total)}%`}></i></div>
  </section>
{/snippet}

{#snippet childJobs(title: string)}
  <section class="job-related-section">
    <header class="job-section-heading">
      <h2>{title}</h2>
      <span>{children.length} {children.length === 1 ? 'job' : 'jobs'}</span>
    </header>
    <div class="job-child-list">
      {#each visibleChildren as child (child.id)}
        <JobCompactRow job={child} allJobs={allJobs} compact onclick={() => onopenjob(child)} onaction={() => onjobaction(child)} />
      {:else}
        <div class="job-detail-empty"><strong>No child jobs</strong></div>
      {/each}
    </div>
    {#if children.length > childLimit}
      <button type="button" class="job-child-load-more" onclick={() => (childLimit += 8)}>Load more jobs</button>
    {/if}
  </section>
{/snippet}
