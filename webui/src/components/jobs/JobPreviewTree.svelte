<script lang="ts">
  import Icon from '../Icon.svelte';
  import { descendantLeafRefs, jobKindIcon, jobKindLabel, type JobPreviewNode } from '../../prototype/jobs';

  interface Props {
    roots: JobPreviewNode[];
    selectedLeaves: Set<string>;
    onselectionchange: (next: Set<string>) => void;
  }

  let { roots, selectedLeaves, onselectionchange }: Props = $props();

  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return { update(next: boolean) { node.indeterminate = next; } };
  }

  function selectionState(node: JobPreviewNode): 'all' | 'some' | 'none' {
    const leaves = descendantLeafRefs(node);
    const count = leaves.filter((ref) => selectedLeaves.has(ref)).length;
    if (count === 0) return 'none';
    if (count === leaves.length) return 'all';
    return 'some';
  }

  function toggleNode(node: JobPreviewNode, checked: boolean): void {
    const next = new Set(selectedLeaves);
    for (const ref of descendantLeafRefs(node)) {
      if (checked) next.add(ref);
      else next.delete(ref);
    }
    onselectionchange(next);
  }
</script>

<div class="job-preview-tree">
  {#each roots as root (root.ref)}
    {@render previewNode(root, 0)}
  {/each}
</div>

{#snippet previewNode(node: JobPreviewNode, depth: number)}
  {@const state = selectionState(node)}
  <div class="job-preview-node" style={`--job-preview-depth:${depth}`}>
    <label class="job-preview-row">
      <input
        type="checkbox"
        checked={state === 'all'}
        use:indeterminate={state === 'some'}
        onchange={(event) => toggleNode(node, (event.currentTarget as HTMLInputElement).checked)}
      />
      <span class="job-preview-icon"><Icon name={jobKindIcon(node.kind)} /></span>
      <span class="job-preview-copy">
        <strong>{node.title}</strong>
        <small>{jobKindLabel(node.kind)}{node.detail ? ` · ${node.detail}` : ''}</small>
      </span>
      {#if node.children.length}
        <span class="job-preview-count">{descendantLeafRefs(node).length} {descendantLeafRefs(node).length === 1 ? 'job' : 'jobs'}</span>
      {/if}
    </label>
    {#if node.children.length}
      <div class="job-preview-children">
        {#each node.children as child (child.ref)}
          {@render previewNode(child, depth + 1)}
        {/each}
      </div>
    {/if}
  </div>
{/snippet}
