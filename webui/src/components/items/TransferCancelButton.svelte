<script lang="ts">
  import Icon from '../Icon.svelte';

  interface Props {
    label: string;
    onclick: () => void;
  }

  let { label, onclick }: Props = $props();
</script>

<button
  type="button"
  class="transfer-cancel-button"
  aria-label={label}
  title={label}
  onclick={(event) => {
    event.preventDefault();
    event.stopPropagation();
    onclick();
  }}
>
  <Icon name="x" />
</button>
