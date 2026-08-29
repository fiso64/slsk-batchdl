<script lang="ts">
  import type { PrototypeResourceState } from '../prototype/resource-state';

  interface Props {
    state: PrototypeResourceState;
    actionLabel?: string;
    onaction?: () => void;
  }

  let { state, actionLabel, onaction }: Props = $props();
</script>

{#if state.phase !== 'ready'}
  <div class:blocking={state.blocking} class={`resource-state-notice ${state.phase}`} role="status">
    <span class="resource-state-dot" aria-hidden="true"></span>
    <span class="resource-state-copy">
      <strong>{state.title ?? state.phase}</strong>
      {#if state.detail}<small>{state.detail}</small>{/if}
    </span>
    {#if actionLabel && onaction}<button type="button" onclick={onaction}>{actionLabel}</button>{/if}
    {#if state.blocking && state.phase === 'loading'}
      <span class="resource-state-skeleton" aria-hidden="true">
        <i></i><i></i><i></i><i></i>
      </span>
    {/if}
  </div>
{/if}
