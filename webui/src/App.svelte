<script lang="ts">
  import AppShell from './components/AppShell.svelte';
  import { getScenario } from './mock/scenarios';
  import type { ScenarioId } from './mock/types';
  import Chat from './pages/Chat.svelte';
  import Dashboard from './pages/Dashboard.svelte';
  import Search from './pages/Search.svelte';
  import Settings from './pages/Settings.svelte';
  import TransferPage from './pages/TransferPage.svelte';
  import type { PageId } from './prototype/navigation';
  import { emptySearchDraft, type SearchDraft } from './prototype/search';
  import type { PrototypeSearchConditions } from './prototype/search-config';
  import {
    createInitialSearches,
    createSearchRecord,
    type SearchRecord,
    type SearchView,
  } from './prototype/search-results';

  let activePage = $state<PageId>('dashboard');
  let scenarioId = $state<ScenarioId>('normal');
  let scenario = $derived(getScenario(scenarioId));
  let search = $state<SearchDraft>({ ...emptySearchDraft });
  let searches = $state<SearchRecord[]>(createInitialSearches());
  let searchView = $state<SearchView>('list');
  let activeSearchId = $state<string | null>('search-boards');

  function useSearch(next: SearchDraft): void {
    search = { ...next };
  }

  function navigate(page: PageId): void {
    if (page === 'search' && activePage === 'search' && searchView === 'results') {
      searchView = 'list';
      return;
    }
    activePage = page;
  }

  function submitSearch(next: SearchDraft, conditions: PrototypeSearchConditions): void {
    useSearch(next);
    const record = createSearchRecord(next, conditions);
    searches = [record, ...searches];
    activeSearchId = record.id;
    searchView = 'results';
    activePage = 'search';
  }
</script>

<AppShell
  {activePage}
  {scenarioId}
  {scenario}
  {search}
  onnavigate={navigate}
  onscenariochange={(nextScenario) => (scenarioId = nextScenario)}
  onsearchchange={useSearch}
  onsearchsubmit={submitSearch}
>
  {#if activePage === 'dashboard'}
    <Dashboard {scenario} />
  {:else if activePage === 'search'}
    <Search
      {search}
      bind:searches
      bind:view={searchView}
      bind:activeSearchId
      onusequery={useSearch}
    />
  {:else if activePage === 'downloads'}
    <TransferPage {scenario} direction="download" />
  {:else if activePage === 'uploads'}
    <TransferPage {scenario} direction="upload" />
  {:else if activePage === 'chat'}
    <Chat {scenario} />
  {:else}
    <Settings />
  {/if}
</AppShell>
