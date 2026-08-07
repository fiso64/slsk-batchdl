<script lang="ts">
  import { scenarios } from '../mock/scenarios';
  import type { ScenarioId } from '../mock/types';

  interface Props {
    value: ScenarioId;
    onchange: (value: ScenarioId) => void;
  }

  let { value, onchange }: Props = $props();
</script>

<label class="scenario-picker">
  <span>Prototype scenario</span>
  <select value={value} onchange={(event) => onchange(event.currentTarget.value as ScenarioId)}>
    {#each scenarios as scenario}
      <option value={scenario.id}>{scenario.label}</option>
    {/each}
  </select>
</label>
