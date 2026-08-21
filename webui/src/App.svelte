<script lang="ts">
  import { onMount } from 'svelte';
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
    defaultSearchId,
    rerunSearchRecord,
    type SearchRecord,
    type SearchView,
  } from './prototype/search-results';

  let activePage = $state<PageId>('dashboard');
  let scenarioId = $state<ScenarioId>('normal');
  let scenario = $derived(getScenario(scenarioId));
  let search = $state<SearchDraft>({ ...emptySearchDraft });
  let searches = $state<SearchRecord[]>(createInitialSearches('normal'));
  let searchView = $state<SearchView>('list');
  let activeSearchId = $state<string | null>(defaultSearchId);
  let userBrowse = $state<UserBrowseDraft>({ query: getUserBrowseFixture('normal').profile.username, mode: 'user' });
  let userView = $state<UserBrowseView>('user');
  let chatInitialUsername = $state<string | null>(null);

  function useSearch(next: SearchDraft): void {
    search = { ...next };
  }

  function pagePath(page: Exclude<PageId, 'search' | 'users'>): string {
    return `/${page}`;
  }

  function searchResultPath(id: string): string {
    return `/searches/${encodeURIComponent(id)}`;
  }

  function userPath(username: string, view: UserBrowseView): string {
    const base = username.trim() ? `/users/${encodeURIComponent(username.trim())}` : '/users';
    return view === 'shares' ? `${base}/shares` : base;
  }

  function setBrowserPath(path: string, replace = false): void {
    if (typeof window === 'undefined') return;
    if (window.location.pathname === path) return;
    if (replace) window.history.replaceState(null, '', path);
    else window.history.pushState(null, '', path);
  }

  function normalizePath(pathname: string): string {
    const withoutTrailing = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
    return withoutTrailing || '/';
  }

  function decodeSegment(value: string | undefined): string {
    if (!value) return '';
    try { return decodeURIComponent(value); }
    catch { return value; }
  }

  function applyBrowserRoute(pathname: string): string {
    const path = normalizePath(pathname);
    const segments = path.split('/').filter(Boolean);
    const root = segments[0] ?? '';

    if (root === 'searches' && segments[1]) {
      const id = decodeSegment(segments[1]);
      const record = searches.find((candidate) => candidate.id === id);
      if (record) {
        activePage = 'search';
        searchView = 'results';
        activeSearchId = record.id;
        search = { ...record.draft };
        return searchResultPath(record.id);
      }
      activePage = 'search';
      searchView = 'list';
      return '/search';
    }

    if (root === 'search') {
      activePage = 'search';
      searchView = 'list';
      return '/search';
    }

    if (root === 'users') {
      const username = decodeSegment(segments[1]) || userBrowse.query || getUserBrowseFixture(scenarioId).profile.username;
      const nextView: UserBrowseView = segments[2] === 'shares' ? 'shares' : 'user';
      activePage = 'users';
      userView = nextView;
      userBrowse = { query: username, mode: nextView };
      return segments[1] ? userPath(username, nextView) : '/users';
    }

    if (root === 'downloads' || root === 'uploads' || root === 'chat' || root === 'settings' || root === 'dashboard') {
      activePage = root;
      return pagePath(root);
    }

    activePage = 'dashboard';
    return '/dashboard';
  }

  onMount(() => {
    const canonical = applyBrowserRoute(window.location.pathname);
    if (canonical !== normalizePath(window.location.pathname)) window.history.replaceState(null, '', canonical);

    const handlePopState = () => {
      const nextCanonical = applyBrowserRoute(window.location.pathname);
      if (nextCanonical !== normalizePath(window.location.pathname)) window.history.replaceState(null, '', nextCanonical);
    };

    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  });

  function navigate(page: PageId): void {
    if (page === 'search') {
      if (activePage === 'search' && searchView === 'results') {
        searchView = 'list';
        setBrowserPath('/search');
        return;
      }

      activePage = 'search';
      if (searchView === 'results' && activeSearchId && searches.some((record) => record.id === activeSearchId)) {
        setBrowserPath(searchResultPath(activeSearchId));
      } else {
        searchView = 'list';
        setBrowserPath('/search');
      }
      return;
    }

    if (page === 'users') {
      activePage = 'users';
      setBrowserPath(userPath(userBrowse.query, userView));
      return;
    }

    activePage = page;
    setBrowserPath(pagePath(page));
  }

  function changeScenario(nextScenario: ScenarioId): void {
    scenarioId = nextScenario;
    searches = createInitialSearches(nextScenario);
    activeSearchId = searches.find((record) => record.fixture === 'boards')?.id ?? searches[0]?.id ?? null;
    if (searchView === 'results' && activeSearchId) search = { ...searches.find((record) => record.id === activeSearchId)!.draft };
    if (searchView === 'results' && !activeSearchId) searchView = 'list';
    const nextUsername = getUserBrowseFixture(nextScenario).profile.username;
    userBrowse = { ...userBrowse, query: nextUsername };
    if (activePage === 'users') setBrowserPath(userPath(nextUsername, userView), true);
    if (activePage === 'search') setBrowserPath(searchView === 'results' && activeSearchId ? searchResultPath(activeSearchId) : '/search', true);
  }

  function useUserBrowse(next: UserBrowseDraft): void {
    userBrowse = { ...next };
  }

  function openUser(username: string): void {
    userBrowse = { query: username, mode: 'user' };
    userView = 'user';
    activePage = 'users';
    setBrowserPath(userPath(username, 'user'));
  }

  function openChatUser(username: string): void {
    chatInitialUsername = username;
    activePage = 'chat';
    setBrowserPath('/chat');
  }

  function submitUserBrowse(next: UserBrowseDraft): void {
    useUserBrowse(next);
    userView = next.mode;
    activePage = 'users';
    setBrowserPath(userPath(next.query, next.mode));
  }

  function changeUserView(next: UserBrowseView): void {
    userView = next;
    userBrowse = { ...userBrowse, mode: next };
    activePage = 'users';
    setBrowserPath(userPath(userBrowse.query, next));
  }

  function openSearchRecord(record: SearchRecord): void {
    activeSearchId = record.id;
    searchView = 'results';
    search = { ...record.draft };
    activePage = 'search';
    setBrowserPath(searchResultPath(record.id));
  }

  function showSearchList(): void {
    searchView = 'list';
    activePage = 'search';
    setBrowserPath('/search');
  }

  function submitSearch(next: SearchDraft, conditions: PrototypeSearchConditions): void {
    useSearch(next);
    const record = createSearchRecord(next, conditions);
    searches = [record, ...searches];
    openSearchRecord(record);
  }

  function searchAgain(record: SearchRecord): void {
    // Jobs are immutable in the daemon. A rerun creates a new job identity, but
    // replaces the old row in-place so the user stays in the same logical slot.
    const rerun = rerunSearchRecord(record);
    searches = searches.map((item) => item.id === record.id ? rerun : item);
    activeSearchId = rerun.id;
    searchView = 'results';
    search = { ...rerun.draft };
    activePage = 'search';
    setBrowserPath(searchResultPath(rerun.id), true);
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
      {scenarioId}
      bind:searches
      bind:view={searchView}
      bind:activeSearchId
      onopenuser={openUser}
      onopenrecord={openSearchRecord}
      onshowlist={showSearchList}
      onsearchagain={searchAgain}
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
