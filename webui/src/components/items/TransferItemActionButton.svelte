<script lang="ts">
  import Icon from '../Icon.svelte';

  interface Props {
    kind?: 'cancel' | 'remove';
    label: string;
    onclick: () => void;
  }

  let { kind = 'cancel', label, onclick }: Props = $props();
</script>

<button
  type="button"
  class:remove={kind === 'remove'}
  class="transfer-item-action-button"
  aria-label={label}
  title={label}
  {onclick}
>
  <Icon name={kind === 'remove' ? 'trash' : 'x'} />
</button>
