<script lang="ts">
  import Icon from '../Icon.svelte';
  import type { AutomaticJobRecord } from '../../prototype/jobs';
  import type { WishlistRecord } from '../../prototype/wishlists';
  import { wishlistNextRunLabel, wishlistRunMetrics, wishlistScheduleLabel } from '../../prototype/wishlists';

  interface Props {
    wishlists: WishlistRecord[];
    automaticJobs: AutomaticJobRecord[];
    onopen: (wishlist: WishlistRecord) => void;
    onrun: (wishlist: WishlistRecord) => void;
    oncancel: (wishlist: WishlistRecord) => void;
  }

  let { wishlists, automaticJobs, onopen, onrun, oncancel }: Props = $props();
</script>

<section class="dashboard-panel dashboard-wishlist-panel" aria-labelledby="dashboard-wishlists-title">
  <div class="dashboard-panel-heading compact dashboard-wishlist-heading">
    <div>
      <h2 id="dashboard-wishlists-title">Wishlists</h2>
      <span>{wishlists.length}</span>
    </div>
  </div>

  <div class="dashboard-wishlist-list">
    {#each wishlists as wishlist (wishlist.id)}
      {@const runMetrics = wishlistRunMetrics(wishlist, automaticJobs)}
      {@const running = wishlist.lastRun.status === 'running'}
      <div class="dashboard-wishlist-row">
        <button type="button" class="dashboard-wishlist-row-open" onclick={() => onopen(wishlist)}>
          <span class="dashboard-wishlist-icon"><Icon name="clock" /></span>
          <span class="dashboard-wishlist-copy">
            <span class="dashboard-wishlist-top">
              <span class="dashboard-wishlist-identity">
                <span class="dashboard-wishlist-primary">
                  <strong>{wishlist.name}</strong>
                  <small>{wishlist.items.length} {wishlist.items.length === 1 ? 'item' : 'items'}</small>
                </span>
                <span class="dashboard-wishlist-schedule">
                  <span>{wishlistScheduleLabel(wishlist.schedule)}</span>
                  {#if wishlist.schedule.enabled}
                    <i aria-hidden="true">·</i>
                    <span>{wishlistNextRunLabel(wishlist.schedule)}</span>
                  {/if}
                </span>
              </span>
              <span class={`dashboard-wishlist-run-label ${wishlist.lastRun.status}`}>
                <i aria-hidden="true"></i>
                {wishlist.lastRun.status === 'running' ? 'Running now' : wishlist.lastRun.status === 'never' ? 'Not run yet' : `Last run · ${wishlist.lastRun.when}`}
              </span>
            </span>
            {#if wishlist.lastRun.status !== 'never'}
              <span class="dashboard-wishlist-metrics">
                {#each runMetrics as metric}
                  <span>{#if metric.value !== undefined}<strong>{metric.value}</strong>{/if} {metric.label}</span>
                {/each}
              </span>
            {/if}
          </span>
        </button>

        <button
          type="button"
          class:cancel={running}
          class="dashboard-wishlist-lifecycle"
          disabled={!running && wishlist.items.length === 0}
          aria-label={running ? `Cancel ${wishlist.name}` : `Run ${wishlist.name}`}
          title={running ? 'Cancel current run' : 'Run wishlist now'}
          onclick={() => running ? oncancel(wishlist) : onrun(wishlist)}
        ><Icon name={running ? 'x' : 'play'} /></button>
      </div>
    {/each}
  </div>
</section>
