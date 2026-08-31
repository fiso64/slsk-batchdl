<script lang="ts">
  import type { PrototypeMutationState } from '../prototype/state';
  interface Props { state: PrototypeMutationState; }
  let { state }: Props = $props();
</script>

{#if state.phase !== 'idle'}
  <span class={`mutation-status ${state.phase}`} role="status">
    {state.label ?? state.phase}
    {#if state.detail}<small>{state.detail}</small>{/if}
  </span>
{/if}
