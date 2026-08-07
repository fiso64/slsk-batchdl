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

  let activePage = $state<PageId>('dashboard');
  let scenarioId = $state<ScenarioId>('normal');
  let scenario = $derived(getScenario(scenarioId));
  let search = $state<SearchDraft>({ ...emptySearchDraft });

  function useSearch(next: SearchDraft): void {
    search = { ...next };
  }

  function submitSearch(next: SearchDraft): void {
    useSearch(next);
    activePage = 'search';
  }
</script>

<AppShell
  {activePage}
  {scenarioId}
  {scenario}
  {search}
  onnavigate={(page) => (activePage = page)}
  onscenariochange={(nextScenario) => (scenarioId = nextScenario)}
  onsearchchange={useSearch}
  onsearchsubmit={submitSearch}
>
  {#if activePage === 'dashboard'}
    <Dashboard {scenario} />
  {:else if activePage === 'search'}
    <Search {search} onusequery={useSearch} />
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
