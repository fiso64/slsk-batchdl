<script lang="ts">
  import type { SearchDraft } from '../prototype/search';
  import { networkQuery } from '../prototype/search';

  interface Props {
    search: SearchDraft;
    onusequery: (search: SearchDraft) => void;
  }

  let { search, onusequery }: Props = $props();
  let query = $derived(networkQuery(search));

  const recent = [
    { artist: 'Boards of Canada', title: 'Geogaddi', kind: 'album', when: '18 min ago' },
    { artist: 'Autechre', title: 'Gantz Graf', kind: 'track', when: 'yesterday' },
    { query: 'ambient dub techno', kind: 'raw query', when: '3 days ago' },
  ] as const;
</script>

<section class="page page-search">
  <header class="page-heading">
    <p class="eyebrow">Discover</p>
    <h1>Search</h1>
  </header>

  <div class="query-summary">
    <div>
      <span class="mini-label">Network query</span>
      <strong>{query || '—'}</strong>
    </div>
    <span class:structured={search.mode === 'split'} class="mode-chip">
      {search.resultMode} · {search.mode === 'split' ? 'structured' : 'raw query'}
    </span>
  </div>

  {#if search.mode === 'split'}
    <div class="structured-summary">
      <div><span>Artist filter</span><strong>{search.artist || '—'}</strong></div>
      <div><span>{search.resultMode === 'album' ? 'Album filter' : 'Track filter'}</span><strong>{search.title || '—'}</strong></div>
    </div>
  {/if}

  <div class="section-heading">
    <h2>Recent searches</h2>
    <span>prototype data</span>
  </div>

  <div class="recent-list">
    {#each recent as item}
      <button
        type="button"
        class="recent-item"
        onclick={() => {
          if ('artist' in item) {
            onusequery({ mode: 'split', resultMode: item.kind === 'track' ? 'track' : 'album', query: '', artist: item.artist, title: item.title });
          } else {
            onusequery({ mode: 'simple', resultMode: search.resultMode, query: item.query, artist: '', title: '' });
          }
        }}
      >
        <span>
          <strong>{'artist' in item ? item.artist : item.query}</strong>
          <small>{'artist' in item ? item.title : item.kind}</small>
        </span>
        <span class="recent-meta">{item.kind} · {item.when}</span>
      </button>
    {/each}
  </div>
</section>
