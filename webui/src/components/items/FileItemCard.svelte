<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { AudioAttributes, TransferPresentation } from '../../prototype/items';
  import { audioSummary, basename, formatBytes, formatLength } from '../../prototype/items';

  interface Props {
    path: string;
    sizeBytes: number;
    audio?: AudioAttributes;
    locked?: boolean;
    selected?: boolean;
    preferred?: boolean;
    selectable?: boolean;
    transfer?: TransferPresentation;
    actions?: Snippet;
    onselect?: (selected: boolean) => void;
  }

  let {
    path,
    sizeBytes,
    audio,
    locked = false,
    selected = false,
    preferred = false,
    selectable = false,
    transfer,
    actions,
    onselect,
  }: Props = $props();

  let transferDirectionClass = $derived(transfer?.direction ? `transfer-${transfer.direction}` : '');
</script>

<article
  class="item-card result-card file-item-card"
  class:locked
  class:selected
  class:preferred
  class:selectable
  class:transfer-card={Boolean(transfer)}
  class:has-audio={Boolean(audio)}
>
  <svelte:element this={selectable ? 'label' : 'div'} class:clickable={selectable} class:nonselectable={!selectable} class="file-item-main file-result-row">
    {#if selectable}
      <input
        type="checkbox"
        checked={selected}
        aria-label={`Select ${basename(path)}`}
        onchange={(event) => onselect?.((event.currentTarget as HTMLInputElement).checked)}
      />
    {/if}

    <div class="item-path-block result-path-block">
      <div class="item-name-line result-name-line">
        <strong>{basename(path)}</strong>
        {#if locked}<span class="locked-badge">Locked</span>{/if}
        {#if transfer}<span class={`transfer-state ${transfer.tone ?? ''} ${transferDirectionClass}`}>{transfer.state}</span>{/if}
      </div>
      <small>{path}</small>
      {#if transfer}
        <div class="transfer-subline">
          {#if transfer.created}<span>{transfer.created}</span>{/if}
          {#if transfer.detail}<span>{transfer.detail}</span>{/if}
        </div>
      {/if}
    </div>

    <div class="item-detail result-detail item-size-detail"><strong>{formatBytes(sizeBytes)}</strong></div>
    {#if audio}
      <div class="item-detail result-detail audio-detail"><strong>{audioSummary(audio)}</strong></div>
      <div class="item-detail result-detail item-length-detail"><strong>{formatLength(audio.lengthSeconds)}</strong></div>
    {/if}
    {#if actions}
      <div class="item-card-action">{@render actions()}</div>
    {/if}
  </svelte:element>

  {#if transfer}
    <div class={`transfer-card-footer ${transfer.tone ?? ''} ${transferDirectionClass}`}>
      {#if transfer.progressPercent !== undefined}
        <div class="transfer-progress-track" aria-label={`${transfer.progressPercent.toFixed(0)}% complete`}>
          <span style={`width:${transfer.progressPercent}%`}></span>
        </div>
      {/if}
      {#if transfer.progressText || transfer.speed || transfer.eta}
        <div class="transfer-progress-meta">
          <span>{transfer.progressText ?? ''}</span>
          <span>{[transfer.speed, transfer.eta].filter(Boolean).join(' · ')}</span>
        </div>
      {/if}
    </div>
  {/if}
</article>
