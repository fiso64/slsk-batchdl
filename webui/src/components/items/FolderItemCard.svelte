<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from '../Icon.svelte';
  import type { AppIconName } from '../../prototype/icons';
  import type { FolderItemFile, TransferPresentation } from '../../prototype/items';
  import { audioSummary, basename, formatBytes, formatLength } from '../../prototype/items';

  interface Props {
    path: string;
    sizeBytes: number;
    files: FolderItemFile[];
    totalFileCount?: number;
    filesComplete?: boolean;
    dataStateLabel?: string;
    locked?: boolean;
    selected?: boolean;
    partial?: boolean;
    preferred?: boolean;
    selectable?: boolean;
    transfer?: TransferPresentation;
    selectedFileIds?: Set<string>;
    actions?: Snippet;
    fileActions?: Snippet<[FolderItemFile]>;
    onselectall?: (selected: boolean) => void;
    onselectfile?: (file: FolderItemFile, selected: boolean) => void;
  }

  let {
    path,
    sizeBytes,
    files,
    totalFileCount = files.length,
    filesComplete = true,
    dataStateLabel,
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

  let transferDirectionClass = $derived(transfer?.direction ? `transfer-${transfer.direction}` : '');

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }

  function transferIcon(value?: TransferPresentation): AppIconName {
    if (value?.tone === 'complete') return 'check';
    if (value?.tone === 'active') return value.direction === 'upload' ? 'upload' : 'download';
    if (value?.tone === 'failed' || value?.tone === 'cancelled') return 'x';
    return 'clock';
  }

  function fileTransferPrimary(value: TransferPresentation): string {
    if (value.tone === 'failed' || value.tone === 'cancelled') return value.state;
    return value.progressText ?? value.state;
  }

  function fileTransferSecondary(value: TransferPresentation): string {
    return [value.detail, value.speed, value.eta].filter(Boolean).join(' · ');
  }
</script>

<article
  class="item-card result-card folder-result-block folder-item-card"
  class:locked
  class:selected
  class:partial
  class:preferred
  class:transfer-card={Boolean(transfer)}
>
  <svelte:element this={selectable ? 'label' : 'div'} class:clickable={selectable} class="folder-item-summary folder-result-summary">
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
        {#if transfer}<span class={`transfer-state ${transfer.tone ?? ''} ${transferDirectionClass}`}>{transfer.state}</span>{/if}
      </div>
      <small>{path}</small>
      {#if dataStateLabel}
        <div class="item-data-state"><span>{dataStateLabel}</span>{#if !filesComplete}<small>{files.length} of {totalFileCount} files loaded</small>{/if}</div>
      {/if}
      {#if transfer}
        <div class="transfer-subline">
          {#if transfer.created}<span>{transfer.created}</span>{/if}
          {#if transfer.detail}<span>{transfer.detail}</span>{/if}
        </div>
      {/if}
    </div>

    <div class="folder-summary-stat folder-file-count"><strong>{filesComplete ? totalFileCount : `${files.length}/${totalFileCount}`}</strong><small>files</small></div>
    <div class="item-detail result-detail item-size-detail"><strong>{formatBytes(sizeBytes)}</strong></div>
    {#if actions}
      <div class="item-card-action">{@render actions()}</div>
    {/if}
  </svelte:element>

  {#if transfer}
    <div class={`transfer-card-footer folder-transfer-footer ${transfer.tone ?? ''} ${transferDirectionClass}`}>
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

  <div class="folder-files-table">
    {#each files as file (file.id)}
      {@const fileDirectionClass = file.transfer?.direction ? `transfer-${file.transfer.direction}` : ''}
      <svelte:element
        this={selectable ? 'label' : 'div'}
        class:locked={file.locked}
        class:has-transfer={Boolean(file.transfer)}
        class:has-audio={Boolean(file.audio)}
        class="folder-file-row"
      >
        {#if selectable}
          <input
            type="checkbox"
            checked={selectedFileIds.has(file.id)}
            onchange={(event) => onselectfile?.(file, (event.currentTarget as HTMLInputElement).checked)}
          />
        {:else}
          <span
            class={`folder-file-state ${fileDirectionClass}`}
            class:active={file.transfer?.tone === 'active'}
            class:queued={file.transfer?.tone === 'queued'}
            class:complete={file.transfer?.tone === 'complete'}
            class:failed={file.transfer?.tone === 'failed'}
            class:cancelled={file.transfer?.tone === 'cancelled'}
          >
            <Icon name={transferIcon(file.transfer)} />
          </span>
        {/if}
        <span class="folder-file-path">
          <span class="folder-file-name-line">
            <strong>{file.relativePath}</strong>
            {#if file.locked}<small>Locked</small>{/if}
          </span>
          {#if file.transfer?.progressPercent !== undefined}
            <span
              class={`folder-file-progress ${file.transfer.tone ?? ''} ${fileDirectionClass}`}
              aria-label={`${file.transfer.progressPercent.toFixed(0)}% complete`}
            >
              <i style={`width:${file.transfer.progressPercent}%`}></i>
            </span>
          {/if}
        </span>
        {#if file.audio}
          <span class="folder-file-audio">{audioSummary(file.audio)}</span>
        {/if}
        <span class="folder-file-size">{formatBytes(file.sizeBytes)}</span>
        {#if file.audio}
          <span class="folder-file-length">{formatLength(file.audio.lengthSeconds)}</span>
        {/if}
        {#if file.transfer}
          {@const secondaryTransferText = fileTransferSecondary(file.transfer)}
          <span
            class="folder-file-transfer-meta"
            class:failed={file.transfer.tone === 'failed'}
            class:cancelled={file.transfer.tone === 'cancelled'}
          >
            <strong>{fileTransferPrimary(file.transfer)}</strong>
            {#if secondaryTransferText}<small title={secondaryTransferText}>{secondaryTransferText}</small>{/if}
          </span>
        {/if}
        {#if fileActions}
          <span class="folder-file-action">{@render fileActions(file)}</span>
        {/if}
      </svelte:element>
    {/each}
  </div>
</article>
