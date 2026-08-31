<script lang="ts">
  import Icon from '../Icon.svelte';
  import type { AppIconName } from '../../prototype/icons';

  export type JobTypeBadgeTone = 'import' | 'automatic';

  interface Props {
    icon: AppIconName;
    label: string;
    tone: JobTypeBadgeTone;
  }

  let { icon, label, tone }: Props = $props();
</script>

<span class={`job-type-badge ${tone}`}>
  <Icon name={icon} />
  <span>{label}</span>
</span>
