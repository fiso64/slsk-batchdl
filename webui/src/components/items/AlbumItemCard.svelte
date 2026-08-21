<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from '../Icon.svelte';
  import type { AppIconName } from '../../prototype/icons';
  import type { AlbumItemFile, TransferPresentation } from '../../prototype/items';
  import { audioSummary, basename, formatBytes, formatLength } from '../../prototype/items';

  interface Props {
    path: string;
    sizeBytes: number;
    files: AlbumItemFile[];
    locked?: boolean;
    selected?: boolean;
    partial?: boolean;
    preferred?: boolean;
    selectable?: boolean;
    transfer?: TransferPresentation;
    selectedFileIds?: Set<string>;
    actions?: Snippet;
    fileActions?: Snippet<[AlbumItemFile]>;
    onselectall?: (selected: boolean) => void;
    onselectfile?: (file: AlbumItemFile, selected: boolean) => void;
  }

  let {
    path,
    sizeBytes,
    files,
    locked = false,
    selected = false,
    partial = false,
    preferred = false,
    selectable = false,
    transfer,
    selectedFileIds = new Set<string>(),
    actions,
    fileActions,
    onselectall,
    onselectfile,
  }: Props = $props();

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }

  function transferIcon(value?: TransferPresentation): AppIconName {
    if (value?.tone === 'complete') return 'check';
    if (value?.tone === 'active') return 'download';
    if (value?.tone === 'failed' || value?.tone === 'cancelled') return 'x';
    return 'clock';
  }
</script>

<article
  class="item-card result-card album-result-block album-item-card"
  class:locked
  class:selected
  class:partial
  class:preferred
  class:transfer-card={Boolean(transfer)}
>
  <svelte:element this={selectable ? 'label' : 'div'} class:clickable={selectable} class="album-item-summary album-result-summary">
    {#if selectable}
      <input
        type="checkbox"
        checked={selected}
        aria-label={`Select all files in ${basename(path)}`}
        use:indeterminate={partial}
        onchange={(event) => onselectall?.((event.currentTarget as HTMLInputElement).checked)}
      />
    {/if}

    <div class="item-path-block result-path-block">
      <div class="item-name-line result-name-line">
        <strong>{basename(path)}</strong>
        {#if locked}<span class="locked-badge">Locked</span>{/if}
        {#if transfer}<span class={`transfer-state ${transfer.tone ?? ''}`}>{transfer.state}</span>{/if}
      </div>
      <small>{path}</small>
      {#if transfer}
        <div class="transfer-subline">
          {#if transfer.peer}<span>from {transfer.peer}</span>{/if}
          {#if transfer.created}<span>{transfer.created}</span>{/if}
          {#if transfer.detail}<span>{transfer.detail}</span>{/if}
        </div>
      {/if}
    </div>

    <div class="album-summary-stat album-file-count"><strong>{files.length}</strong><small>files</small></div>
    <div class="item-detail result-detail item-size-detail"><strong>{formatBytes(sizeBytes)}</strong></div>
    {#if actions}
      <div class="item-card-action">{@render actions()}</div>
    {/if}
  </svelte:element>

  {#if transfer}
    <div class={`transfer-card-footer album-transfer-footer ${transfer.tone ?? ''}`}>
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

  <div class="album-files-table">
    {#each files as file (file.id)}
      <svelte:element
        this={selectable ? 'label' : 'div'}
        class:locked={file.locked}
        class:has-transfer={Boolean(file.transfer)}
        class="album-file-row"
      >
        {#if selectable}
          <input
            type="checkbox"
            checked={selectedFileIds.has(file.id)}
            onchange={(event) => onselectfile?.(file, (event.currentTarget as HTMLInputElement).checked)}
          />
        {:else}
          <span
            class="album-file-state"
            class:active={file.transfer?.tone === 'active'}
            class:queued={file.transfer?.tone === 'queued'}
            class:complete={file.transfer?.tone === 'complete'}
            class:failed={file.transfer?.tone === 'failed'}
            class:cancelled={file.transfer?.tone === 'cancelled'}
          >
            <Icon name={transferIcon(file.transfer)} />
          </span>
        {/if}
        <span class="album-file-path">
          <span class="album-file-name-line">
            <strong>{file.relativePath}</strong>
            {#if file.locked}<small>Locked</small>{/if}
          </span>
          {#if file.transfer?.progressPercent !== undefined}
            <span
              class={`album-file-progress ${file.transfer.tone ?? ''}`}
              aria-label={`${file.transfer.progressPercent.toFixed(0)}% complete`}
            >
              <i style={`width:${file.transfer.progressPercent}%`}></i>
            </span>
          {/if}
        </span>
        <span class="album-file-audio">{audioSummary(file.audio)}</span>
        <span class="album-file-size">{formatBytes(file.sizeBytes)}</span>
        <span class="album-file-length">{formatLength(file.audio?.lengthSeconds)}</span>
        {#if file.transfer}
          <span class="album-file-transfer-meta">
            {#if file.transfer.progressText}<strong>{file.transfer.progressText}</strong>{/if}
            {#if file.transfer.speed || file.transfer.eta}<small>{[file.transfer.speed, file.transfer.eta].filter(Boolean).join(' · ')}</small>{/if}
          </span>
        {/if}
        {#if fileActions}
          <span class="album-file-action">{@render fileActions(file)}</span>
        {/if}
      </svelte:element>
    {/each}
  </div>
</article>
