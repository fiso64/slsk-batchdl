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
    fileLayout?: 'flat' | 'tree';
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
    onselectfiles?: (files: FolderItemFile[], selected: boolean) => void;
  }

  let {
    path,
    sizeBytes,
    files,
    totalFileCount = files.length,
    filesComplete = true,
    fileLayout = 'flat',
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
    onselectfiles,
  }: Props = $props();

  interface FolderTreeFile {
    file: FolderItemFile;
    name: string;
  }

  interface FolderTreeNode {
    name: string;
    relativePath: string;
    folders: Map<string, FolderTreeNode>;
    files: FolderTreeFile[];
  }

  type FolderDisplayRow =
    | { kind: 'folder'; key: string; name: string; relativePath: string; depth: number; files: FolderItemFile[]; sizeBytes: number }
    | { kind: 'file'; key: string; depth: number; displayName: string; file: FolderItemFile };

  const naturalName = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

  function buildTreeRows(source: FolderItemFile[]): FolderDisplayRow[] {
    if (fileLayout === 'flat') {
      return source.map((file) => ({ kind: 'file', key: `file:${file.id}`, depth: 0, displayName: file.relativePath, file }));
    }

    const root: FolderTreeNode = { name: '', relativePath: '', folders: new Map(), files: [] };
    for (const file of source) {
      const parts = file.relativePath.split(/[\\/]+/).filter(Boolean);
      const filename = parts.pop() ?? file.relativePath;
      let node = root;
      for (const part of parts) {
        const relativePath = node.relativePath ? `${node.relativePath}/${part}` : part;
        let child = node.folders.get(part);
        if (!child) {
          child = { name: part, relativePath, folders: new Map(), files: [] };
          node.folders.set(part, child);
        }
        node = child;
      }
      node.files.push({ file, name: filename });
    }

    const rows: FolderDisplayRow[] = [];
    const descendants = (node: FolderTreeNode): FolderItemFile[] => [
      ...node.files.map((entry) => entry.file),
      ...Array.from(node.folders.values()).flatMap(descendants),
    ];
    const append = (node: FolderTreeNode, depth: number): void => {
      const folders = Array.from(node.folders.values()).sort((a, b) => naturalName.compare(a.name, b.name));
      const files = [...node.files].sort((a, b) => naturalName.compare(a.name, b.name));
      for (const folder of folders) {
        const folderFiles = descendants(folder);
        rows.push({
          kind: 'folder',
          key: `folder:${folder.relativePath}`,
          name: folder.name,
          relativePath: folder.relativePath,
          depth,
          files: folderFiles,
          sizeBytes: folderFiles.reduce((total, file) => total + file.sizeBytes, 0),
        });
        append(folder, depth + 1);
      }
      for (const entry of files) {
        rows.push({ kind: 'file', key: `file:${entry.file.id}`, depth, displayName: entry.name, file: entry.file });
      }
    };
    append(root, 0);
    return rows;
  }

  function folderSelected(folderFiles: FolderItemFile[]): boolean {
    return folderFiles.length > 0 && folderFiles.every((file) => selectedFileIds.has(file.id));
  }

  function folderPartial(folderFiles: FolderItemFile[]): boolean {
    const selectedCount = folderFiles.filter((file) => selectedFileIds.has(file.id)).length;
    return selectedCount > 0 && selectedCount < folderFiles.length;
  }

  function selectFolderFiles(folderFiles: FolderItemFile[], checked: boolean): void {
    if (onselectfiles) {
      onselectfiles(folderFiles, checked);
      return;
    }
    for (const file of folderFiles) onselectfile?.(file, checked);
  }

  let transferDirectionClass = $derived(transfer?.direction ? `transfer-${transfer.direction}` : '');

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }

  function transferIcon(value?: TransferPresentation): AppIconName {
    if (value?.tone === 'complete') return 'check';
    if (value?.tone === 'active') return value.direction === 'upload' ? 'upload' : 'download';
    if (value?.tone === 'failed' || value?.tone === 'cancelled') return 'x';
    return value ? 'clock' : 'file';
  }

  function fileTransferPrimary(value: TransferPresentation): string {
    if (value.tone === 'failed' || value.tone === 'cancelled') return value.state;
    return value.progressText ?? value.state;
  }

  function fileTransferSecondary(value: TransferPresentation): string {
    return [value.detail, value.speed, value.eta].filter(Boolean).join(' · ');
  }

  function parentPath(value: string): string {
    const normalized = value.replace(/\\/g, '/');
    const splitAt = normalized.lastIndexOf('/');
    return splitAt >= 0 ? normalized.slice(0, splitAt + 1) : '';
  }
</script>

<article
  class="item-card result-card folder-result-block folder-item-card"
  class:locked
  class:selected
  class:partial
  class:preferred
  class:selectable
  class:tree-layout={fileLayout === 'tree'}
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

    <div class="item-path-block result-path-block" title={fileLayout === 'tree' ? path : undefined}>
      <div class="item-name-line result-name-line">
        {#if fileLayout === 'tree'}
          <strong class="tree-root-path"><span>{parentPath(path)}</span><b>{basename(path)}</b></strong>
        {:else}
          <strong>{basename(path)}</strong>
        {/if}
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

  <div class="folder-files-table" class:tree-layout={fileLayout === 'tree'}>
    {#each buildTreeRows(files) as row (row.key)}
      {#if row.kind === 'folder'}
        {@const rowSelected = folderSelected(row.files)}
        {@const rowPartial = folderPartial(row.files)}
        <svelte:element
          this={selectable ? 'label' : 'div'}
          class="folder-subfolder-row"
          class:clickable={selectable}
          style={`--folder-tree-depth:${row.depth}`}
        >
          {#if selectable}
            <input
              type="checkbox"
              checked={rowSelected}
              aria-label={`Select all matching files in ${row.name}`}
              use:indeterminate={rowPartial}
              onchange={(event) => selectFolderFiles(row.files, (event.currentTarget as HTMLInputElement).checked)}
            />
          {:else}
            <span class="folder-subfolder-icon"><Icon name="folder" /></span>
          {/if}
          <span class="folder-subfolder-path">
            <strong>{row.name}</strong>
            <small>{row.relativePath}</small>
          </span>
          <span class="folder-subfolder-count">{row.files.length} {row.files.length === 1 ? 'file' : 'files'}</span>
          <span class="folder-subfolder-size">{formatBytes(row.sizeBytes)}</span>
        </svelte:element>
      {:else}
        {@const file = row.file}
        {@const fileDirectionClass = file.transfer?.direction ? `transfer-${file.transfer.direction}` : ''}
        <svelte:element
          this={selectable ? 'label' : 'div'}
          class:locked={file.locked}
          class:has-transfer={Boolean(file.transfer)}
          class:has-audio={Boolean(file.audio)}
          class:tree-row={fileLayout === 'tree'}
          class="folder-file-row"
          style={`--folder-tree-depth:${row.depth}`}
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
          <span class="folder-file-path" title={file.relativePath}>
            <span class="folder-file-name-line">
              <strong>{row.displayName}</strong>
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
      {/if}
    {/each}
  </div>
</article>
