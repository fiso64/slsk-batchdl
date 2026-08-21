<script lang="ts">
  import AppShell from './components/AppShell.svelte';
  import { getScenario } from './mock/scenarios';
  import type { ScenarioId } from './mock/types';
  import Chat from './pages/Chat.svelte';
  import Dashboard from './pages/Dashboard.svelte';
  import Downloads from './pages/Downloads.svelte';
  import Search from './pages/Search.svelte';
  import Settings from './pages/Settings.svelte';
  import Uploads from './pages/Uploads.svelte';
  import Users from './pages/Users.svelte';
  import type { PageId } from './prototype/navigation';
  import { emptySearchDraft, type SearchDraft } from './prototype/search';
  import type { PrototypeSearchConditions } from './prototype/search-config';
  import { getUserBrowseFixture, type UserBrowseDraft, type UserBrowseView } from './prototype/users';
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
  let userBrowse = $state<UserBrowseDraft>({ query: getUserBrowseFixture('normal').profile.username, mode: 'user' });
  let userView = $state<UserBrowseView>('user');
  let chatInitialUsername = $state<string | null>(null);

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

  function changeScenario(nextScenario: ScenarioId): void {
    scenarioId = nextScenario;
    userBrowse = { ...userBrowse, query: getUserBrowseFixture(nextScenario).profile.username };
  }

  function useUserBrowse(next: UserBrowseDraft): void {
    userBrowse = { ...next };
  }

  function openUser(username: string): void {
    userBrowse = { query: username, mode: 'user' };
    userView = 'user';
    activePage = 'users';
  }

  function openChatUser(username: string): void {
    chatInitialUsername = username;
    activePage = 'chat';
  }

  function submitUserBrowse(next: UserBrowseDraft): void {
    useUserBrowse(next);
    userView = next.mode;
    activePage = 'users';
  }

  function changeUserView(next: UserBrowseView): void {
    userView = next;
    userBrowse = { ...userBrowse, mode: next };
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
  {userBrowse}
  onnavigate={navigate}
  onscenariochange={changeScenario}
  onsearchchange={useSearch}
  onsearchsubmit={submitSearch}
  onuserbrowsechange={useUserBrowse}
  onuserbrowsesubmit={submitUserBrowse}
>
  {#if activePage === 'dashboard'}
    <Dashboard {scenario} onopenuser={openUser} />
  {:else if activePage === 'search'}
    <Search
      {search}
      bind:searches
      bind:view={searchView}
      bind:activeSearchId
      onusequery={useSearch}
      onopenuser={openUser}
    />
  {:else if activePage === 'users'}
    <Users {scenarioId} username={userBrowse.query} view={userView} onviewchange={changeUserView} onmessageuser={openChatUser} />
  {:else if activePage === 'downloads'}
    <Downloads {scenario} onopenuser={openUser} />
  {:else if activePage === 'uploads'}
    <Uploads {scenario} onopenuser={openUser} />
  {:else if activePage === 'chat'}
    <Chat {scenario} onopenuser={openUser} initialUsername={chatInitialUsername} />
  {:else}
    <Settings />
  {/if}
</AppShell>
