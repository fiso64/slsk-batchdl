<script lang="ts">
  interface Props {
    text: string;
  }

  interface TextSegment {
    kind: 'text';
    value: string;
  }

  interface LinkSegment {
    kind: 'link';
    value: string;
    href: string;
  }

  type Segment = TextSegment | LinkSegment;

  let { text }: Props = $props();

  function linkify(value: string): Segment[] {
    const segments: Segment[] = [];
    const matcher = /\b(?:https?:\/\/|www\.)[^\s<>"']+/gi;
    let cursor = 0;

    for (const match of value.matchAll(matcher)) {
      const index = match.index ?? 0;
      if (index > cursor) segments.push({ kind: 'text', value: value.slice(cursor, index) });

      const raw = match[0];
      const trimmed = raw.replace(/[.,!?;:]+$/, '');
      const trailing = raw.slice(trimmed.length);
      const href = trimmed.startsWith('www.') ? `https://${trimmed}` : trimmed;

      segments.push({ kind: 'link', value: trimmed, href });
      if (trailing) segments.push({ kind: 'text', value: trailing });
      cursor = index + raw.length;
    }

    if (cursor < value.length) segments.push({ kind: 'text', value: value.slice(cursor) });
    return segments;
  }

  let segments = $derived(linkify(text));
</script>

{#each segments as segment}
  {#if segment.kind === 'link'}
    <a href={segment.href} target="_blank" rel="noopener noreferrer">{segment.value}</a>
  {:else}
    {segment.value}
  {/if}
{/each}
