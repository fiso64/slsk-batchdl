<script lang="ts">
  import Icon from '../Icon.svelte';
  import {
    createWishlist,
    wishlistNextRunLabel,
    type WishlistCadence,
    type WishlistIntervalUnit,
    type WishlistRecord,
  } from '../../prototype/wishlists';

  interface Props {
    onclose: () => void;
    oncreate: (wishlist: WishlistRecord) => void;
  }

  let { onclose, oncreate }: Props = $props();
  let name = $state('');
  let enabled = $state(true);
  let cadence = $state<WishlistCadence>('daily');
  let time = $state('03:00');
  let weekday = $state('Monday');
  let monthDay = $state(1);
  let intervalValue = $state(6);
  let intervalUnit = $state<WishlistIntervalUnit>('hours');
  let valid = $derived(Boolean(name.trim()));

  function create(): void {
    if (!valid) return;
    const wishlist = createWishlist(name.trim());
    wishlist.schedule = {
      enabled,
      cadence,
      time,
      weekday,
      monthDay,
      intervalValue: Math.max(1, Math.round(intervalValue || 1)),
      intervalUnit,
    };
    wishlist.nextRun = enabled ? wishlistNextRunLabel(wishlist.schedule) : null;
    oncreate(wishlist);
  }

  function handleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') onclose();
    if (event.key === 'Enter' && valid && !(event.target instanceof HTMLButtonElement)) create();
  }
</script>

<svelte:window onkeydown={handleKeydown} />
<div class="new-job-modal">
  <button type="button" class="new-job-modal-backdrop" aria-label="Close new wishlist" onclick={onclose}></button>
  <div class="new-wishlist-dialog" role="dialog" aria-modal="true" aria-label="New wishlist">
    <header>
      <h2>New wishlist</h2>
      <button type="button" aria-label="Close new wishlist" onclick={onclose}><Icon name="x" /></button>
    </header>

    <div class="new-wishlist-body">
      <label class="wishlist-field wishlist-name-field">
        <span>Name</span>
        <input bind:value={name} placeholder="Wishlist name…" />
      </label>

      <section class="new-wishlist-schedule" aria-labelledby="new-wishlist-schedule-title">
        <header>
          <strong id="new-wishlist-schedule-title">Schedule</strong>
          <label class="wishlist-enabled-toggle"><input type="checkbox" bind:checked={enabled} /> Enabled</label>
        </header>
        <div class:disabled={!enabled} class="new-wishlist-schedule-fields">
          <label class="wishlist-field">
            <span>Repeat</span>
            <select bind:value={cadence} disabled={!enabled}>
              <option value="daily">Daily</option>
              <option value="weekly">Weekly</option>
              <option value="monthly">Monthly</option>
              <option value="interval">Every…</option>
            </select>
          </label>

          {#if cadence === 'weekly'}
            <label class="wishlist-field">
              <span>Day</span>
              <select bind:value={weekday} disabled={!enabled}>
                {#each ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as day}<option value={day}>{day}</option>{/each}
              </select>
            </label>
          {:else if cadence === 'monthly'}
            <label class="wishlist-field">
              <span>Day</span>
              <select bind:value={monthDay} disabled={!enabled}>
                {#each Array.from({ length: 28 }, (_, index) => index + 1) as day}<option value={day}>{day}</option>{/each}
              </select>
            </label>
          {:else if cadence === 'interval'}
            <label class="wishlist-field wishlist-interval-field">
              <span>Every</span>
              <span class="wishlist-interval-control">
                <input type="number" min="1" max={intervalUnit === 'minutes' ? 1440 : 168} bind:value={intervalValue} disabled={!enabled} />
                <select bind:value={intervalUnit} disabled={!enabled}><option value="minutes">minutes</option><option value="hours">hours</option></select>
              </span>
            </label>
          {/if}

          {#if cadence !== 'interval'}
            <label class="wishlist-field wishlist-time-field">
              <span>Time</span>
              <input type="time" bind:value={time} disabled={!enabled} />
            </label>
          {/if}
        </div>
      </section>
    </div>

    <footer>
      <button type="button" class="new-job-review-button" onclick={onclose}>Cancel</button>
      <button type="button" class="new-job-start-button" disabled={!valid} onclick={create}>Create wishlist</button>
    </footer>
  </div>
</div>
