<script lang="ts">
  import Icon from '../Icon.svelte';
  import type { AutomaticJobRecord } from '../../prototype/jobs';
  import type { WishlistRecord } from '../../prototype/wishlists';
  import { wishlistNextRunLabel, wishlistRunMetrics, wishlistScheduleLabel } from '../../prototype/wishlists';

  interface Props {
    wishlists: WishlistRecord[];
    automaticJobs: AutomaticJobRecord[];
    onopen: (wishlist: WishlistRecord) => void;
    oncancel: (wishlist: WishlistRecord) => void;
    onrun: (wishlist: WishlistRecord) => void;
  }

  let { wishlists, automaticJobs, onopen, oncancel, onrun }: Props = $props();
</script>

<section class="wishlist-overview" aria-labelledby="wishlist-overview-title">
  <header class="jobs-section-heading">
    <div>
      <h2 id="wishlist-overview-title">Wishlists</h2>
      <span>{wishlists.length}</span>
    </div>
  </header>

  <div class="wishlist-overview-list">
    {#each wishlists as wishlist (wishlist.id)}
      {@const runMetrics = wishlistRunMetrics(wishlist, automaticJobs)}
      <article class:running={wishlist.lastRun.status === 'running'} class="wishlist-overview-card">
        <button type="button" class="wishlist-overview-open" onclick={() => onopen(wishlist)}>
          <span class="wishlist-card-heading">
            <span class="wishlist-row-icon"><Icon name="clock" /></span>
            <span class="wishlist-row-main">
              <span class="wishlist-card-titleline">
                <strong>{wishlist.name}</strong>
                <small>{wishlist.items.length} {wishlist.items.length === 1 ? 'item' : 'items'}</small>
              </span>
              <span class="wishlist-card-schedule">
                <span>{wishlistScheduleLabel(wishlist.schedule)}</span>
                {#if wishlist.schedule.enabled}
                  <i aria-hidden="true">·</i>
                  <span>next {wishlistNextRunLabel(wishlist.schedule).toLowerCase()}</span>
                {/if}
              </span>
            </span>
          </span>

          <span class={`wishlist-run-state ${wishlist.lastRun.status}`}>
            <span class="wishlist-run-state-label"><i></i>{wishlist.lastRun.status === 'running' ? 'Running now' : `Last run · ${wishlist.lastRun.when}`}</span>
            <span class="wishlist-run-metrics">
              {#each runMetrics as metric}
                <span>
                  {#if metric.value !== undefined}<strong>{metric.value}</strong>{/if}
                  <small>{metric.label}</small>
                </span>
              {/each}
            </span>
          </span>
        </button>
        {#if wishlist.lastRun.status === 'running'}
          <button type="button" class="wishlist-overview-action cancel" aria-label={`Cancel ${wishlist.name}`} title="Cancel current run" onclick={() => oncancel(wishlist)}><Icon name="x" /></button>
        {:else}
          <button type="button" class="wishlist-overview-action run" aria-label={`Run ${wishlist.name}`} title="Run wishlist now" disabled={!wishlist.items.length} onclick={() => onrun(wishlist)}><Icon name="play" /></button>
        {/if}
      </article>
    {/each}
  </div>
</section>
