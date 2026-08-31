<script lang="ts">
  import Icon from '../Icon.svelte';
  import JobCompactRow from './JobCompactRow.svelte';
  import WishlistDefaultsEditor from './WishlistDefaultsEditor.svelte';
  import { presentationTarget, type AutomaticJobRecord } from '../../prototype/jobs';
  import type { WishlistItem, WishlistRecord } from '../../prototype/wishlists';
  import {
    wishlistItemOverrideLabels,
    wishlistItemTitle,
    wishlistItemTypeLabel,
    wishlistNextRunLabel,
    wishlistRunDetail,
  } from '../../prototype/wishlists';
  import type { AppIconName } from '../../prototype/icons';
  import type { UserLinkActions } from '../../prototype/navigation';

  interface Props {
    wishlist: WishlistRecord;
    automaticJobs: AutomaticJobRecord[];
    userActions: UserLinkActions;
    onback: () => void;
    onadditem: () => void;
    onedititem: (item: WishlistItem) => void;
    onremoveitem: (item: WishlistItem) => void;
    onopenjob: (job: AutomaticJobRecord) => void;
    onjobaction: (job: AutomaticJobRecord) => void;
    onrun: () => void;
    oncancel: () => void;
    ondelete: () => void;
  }

  let {
    wishlist,
    automaticJobs,
    userActions,
    onback,
    onadditem,
    onedititem,
    onremoveitem,
    onopenjob,
    onjobaction,
    onrun,
    oncancel,
    ondelete,
  }: Props = $props();

  function itemIcon(item: WishlistItem): AppIconName {
    const choice = item.draft.choice;
    if (choice === 'song') return 'track';
    if (choice === 'album') return 'album';
    if (choice === 'csv' || choice === 'list') return 'upload-file';
    return choice;
  }

  function latestJob(item: WishlistItem): AutomaticJobRecord | null {
    if (!item.lastJobId) return null;
    const job = automaticJobs.find((candidate) => candidate.id === item.lastJobId);
    return job ? presentationTarget(job, automaticJobs) : null;
  }

  function setSchedule<K extends keyof WishlistRecord['schedule']>(key: K, value: WishlistRecord['schedule'][K]): void {
    wishlist.schedule[key] = value;
    wishlist.nextRun = wishlist.schedule.enabled ? wishlistNextRunLabel(wishlist.schedule) : null;
  }
</script>

<section class="wishlist-detail-page">
  <header class="wishlist-detail-heading">
    <button type="button" class="icon-button back-button" aria-label="Back to jobs" onclick={onback}>
      <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
    </button>
    <div class="wishlist-detail-title">
      <input aria-label="Wishlist name" bind:value={wishlist.name} />
      <div class="wishlist-detail-runline">
        <span>{wishlist.items.length} {wishlist.items.length === 1 ? 'item' : 'items'}</span>
        <span class="stat-separator">·</span>
        <span class={`wishlist-run-dot ${wishlist.lastRun.status}`}><i></i>{wishlist.lastRun.status === 'running' ? 'Running now' : `Last run ${wishlist.lastRun.when}`}</span>
        <span class="stat-separator">·</span>
        <strong>{wishlistRunDetail(wishlist, automaticJobs)}</strong>
      </div>
    </div>
    <div class="wishlist-detail-actions">
      {#if wishlist.lastRun.status === 'running'}
        <button type="button" class="resource-cancel-button" onclick={oncancel}><Icon name="x" /> Cancel</button>
      {:else}
        <button type="button" class="wishlist-run-now" disabled={!wishlist.items.length} onclick={onrun}><Icon name="clock" /> Run now</button>
      {/if}
      {#if wishlist.lastRun.status !== 'running'}
        <button type="button" class="resource-remove-button" aria-label={`Delete ${wishlist.name}`} title="Delete wishlist" onclick={ondelete}><Icon name="trash" /></button>
      {/if}
    </div>
  </header>

  <section class="wishlist-schedule-panel" aria-labelledby="wishlist-schedule-title">
    <header>
      <h2 id="wishlist-schedule-title">Schedule</h2>
      <div class="wishlist-schedule-header-meta">
        <span>Next: <strong>{wishlistNextRunLabel(wishlist.schedule)}</strong></span>
        <label class="wishlist-enabled-toggle"><input type="checkbox" checked={wishlist.schedule.enabled} onchange={(event) => setSchedule('enabled', (event.currentTarget as HTMLInputElement).checked)} /> Enabled</label>
      </div>
    </header>
    <div class:disabled={!wishlist.schedule.enabled} class="wishlist-schedule-grid">
      <label class="wishlist-field"><span>Repeat</span><select disabled={!wishlist.schedule.enabled} value={wishlist.schedule.cadence} onchange={(event) => setSchedule('cadence', (event.currentTarget as HTMLSelectElement).value as WishlistRecord['schedule']['cadence'])}><option value="daily">Daily</option><option value="weekly">Weekly</option><option value="monthly">Monthly</option><option value="interval">Every…</option></select></label>
      {#if wishlist.schedule.cadence === 'weekly'}
        <label class="wishlist-field"><span>Day</span><select disabled={!wishlist.schedule.enabled} value={wishlist.schedule.weekday} onchange={(event) => setSchedule('weekday', (event.currentTarget as HTMLSelectElement).value)}>{#each ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as day}<option value={day}>{day}</option>{/each}</select></label>
      {:else if wishlist.schedule.cadence === 'monthly'}
        <label class="wishlist-field"><span>Day</span><select disabled={!wishlist.schedule.enabled} value={wishlist.schedule.monthDay} onchange={(event) => setSchedule('monthDay', Number((event.currentTarget as HTMLSelectElement).value))}>{#each Array.from({ length: 28 }, (_, index) => index + 1) as day}<option value={day}>{day}</option>{/each}</select></label>
      {:else if wishlist.schedule.cadence === 'interval'}
        <label class="wishlist-field wishlist-interval-field"><span>Every</span><span class="wishlist-interval-control"><input type="number" min="1" value={wishlist.schedule.intervalValue} disabled={!wishlist.schedule.enabled} onchange={(event) => setSchedule('intervalValue', Math.max(1, Number((event.currentTarget as HTMLInputElement).value) || 1))} /><select value={wishlist.schedule.intervalUnit} disabled={!wishlist.schedule.enabled} onchange={(event) => setSchedule('intervalUnit', (event.currentTarget as HTMLSelectElement).value as WishlistRecord['schedule']['intervalUnit'])}><option value="minutes">minutes</option><option value="hours">hours</option></select></span></label>
      {/if}
      {#if wishlist.schedule.cadence !== 'interval'}
        <label class="wishlist-field wishlist-time-field"><span>Time</span><input type="time" disabled={!wishlist.schedule.enabled} value={wishlist.schedule.time} onchange={(event) => setSchedule('time', (event.currentTarget as HTMLInputElement).value)} /></label>
      {/if}
    </div>
  </section>

  <section class="wishlist-editor-section" aria-labelledby="wishlist-defaults-title">
    <header class="wishlist-editor-section-heading"><h2 id="wishlist-defaults-title">Defaults</h2></header>
    <WishlistDefaultsEditor value={wishlist.defaults} />
  </section>

  <section class="wishlist-editor-section wishlist-items-section" aria-labelledby="wishlist-items-title">
    <header class="wishlist-editor-section-heading">
      <h2 id="wishlist-items-title">Items</h2>
      <button type="button" class="wishlist-add-item" onclick={onadditem}><span>+</span> Add job</button>
    </header>

    {#if wishlist.items.length}
      <div class="wishlist-item-list">
        {#each wishlist.items as item (item.id)}
          {@const overrides = wishlistItemOverrideLabels(item)}
          {@const job = latestJob(item)}
          <article class:has-runtime={Boolean(job)} class="wishlist-item-entry">
            <div class="wishlist-item-definition">
              <span class="wishlist-item-kind-icon" title={wishlistItemTypeLabel(item)}><Icon name={itemIcon(item)} /></span>
              <div class="wishlist-item-copy">
                <div class="wishlist-item-primary">
                  <strong>{wishlistItemTitle(item)}</strong>
                </div>
                <span class="wishlist-item-definition-label" title={overrides.length ? `Overrides: ${overrides.join(', ')}` : undefined}>{wishlistItemTypeLabel(item)}{#if overrides.length}<span> · Custom options</span>{/if}</span>
              </div>
              <div class="wishlist-item-actions">
                <button type="button" class="wishlist-item-edit" onclick={() => onedititem(item)}>Edit</button>
                <button type="button" class="wishlist-item-remove" aria-label={`Remove ${wishlistItemTitle(item)}`} title="Remove item" onclick={() => onremoveitem(item)}><Icon name="x" /></button>
              </div>
            </div>
            {#if job}
              <div class="wishlist-item-runtime">
                <JobCompactRow job={job} allJobs={automaticJobs} {userActions} compact onclick={() => onopenjob(job)} onaction={() => onjobaction(job)} />
              </div>
            {/if}
          </article>
        {/each}
      </div>
    {:else}
      <div class="wishlist-empty-items"><Icon name="job-list" /><strong>No items yet</strong><span>Add any job or import source using the same New Job controls.</span></div>
    {/if}
  </section>
</section>
