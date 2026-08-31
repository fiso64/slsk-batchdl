<script lang="ts">
  import { onMount } from 'svelte';
  import AppShell from './components/AppShell.svelte';
  import { getScenario } from './mock/scenarios';
  import type { ScenarioId } from './mock/types';
  import Chat from './pages/Chat.svelte';
  import Dashboard from './pages/Dashboard.svelte';
  import Downloads from './pages/Downloads.svelte';
  import Jobs from './pages/Jobs.svelte';
  import Settings from './pages/Settings.svelte';
  import Uploads from './pages/Uploads.svelte';
  import Users from './pages/Users.svelte';
  import type { PageId, UserLinkActions } from './prototype/navigation';
  import { emptySearchDraft, type SearchDraft } from './prototype/search';
  import type { PrototypeSearchConditions } from './prototype/search-config';
  import { createInitialWishlistJobs, createInitialWishlists, runWishlistNow, type WishlistRecord } from './prototype/wishlists';
  import { getUserBrowseFixture, type UserBrowseDraft, type UserBrowseView } from './prototype/users';
  import { createInitialAutomaticJobs, isAutomaticJobActive, type AutomaticJobRecord } from './prototype/jobs';
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
  let automaticJobs = $state<AutomaticJobRecord[]>([...createInitialAutomaticJobs('normal'), ...createInitialWishlistJobs('normal')]);
  let wishlists = $state<WishlistRecord[]>(createInitialWishlists('normal'));
  let searchView = $state<SearchView>('list');
  let activeJobId = $state<string | null>(defaultSearchId);
  let activeWishlistId = $state<string | null>(null);
  let selectedJobResultIds = $state<Set<string>>(new Set());
  let selectedAggregateGroupIds = $state<Set<string>>(new Set());
  let selectedAggregateFileIds = $state<Set<string>>(new Set());
  let userBrowse = $state<UserBrowseDraft>({ query: getUserBrowseFixture('normal').profile.username, mode: 'user' });
  let activeUsername = $state<string>(getUserBrowseFixture('normal').profile.username);
  let userView = $state<UserBrowseView>('user');
  let chatInitialUsername = $state<string | null>(null);

  function useSearch(next: SearchDraft): void {
    search = { ...next };
  }

  function clearJobSelection(): void {
    selectedJobResultIds = new Set();
    selectedAggregateGroupIds = new Set();
    selectedAggregateFileIds = new Set();
  }

  function setActiveJob(id: string | null): void {
    if (activeJobId !== id) clearJobSelection();
    activeJobId = id;
  }

  function pagePath(page: Exclude<PageId, 'jobs' | 'users'>): string {
    return `/${page}`;
  }

  function jobPath(id: string): string {
    return `/jobs/${encodeURIComponent(id)}`;
  }

  function wishlistPath(id: string): string {
    return `/jobs/wishlists/${encodeURIComponent(id)}`;
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

    if (root === 'jobs' && segments[1] === 'wishlists' && segments[2]) {
      const id = decodeSegment(segments[2]);
      const wishlist = wishlists.find((candidate) => candidate.id === id);
      if (wishlist) {
        activePage = 'jobs';
        searchView = 'wishlist';
        activeWishlistId = id;
        return wishlistPath(id);
      }
      activePage = 'jobs';
      searchView = 'list';
      activeWishlistId = null;
      return '/jobs';
    }

    if (root === 'jobs' && segments[1]) {
      const id = decodeSegment(segments[1]);
      const record = searches.find((candidate) => candidate.id === id);
      const automaticJob = automaticJobs.find((candidate) => candidate.id === id);
      if (record || automaticJob) {
        activePage = 'jobs';
        searchView = 'results';
        setActiveJob(id);
        return jobPath(id);
      }
      activePage = 'jobs';
      searchView = 'list';
      return '/jobs';
    }

    if (root === 'jobs') {
      activePage = 'jobs';
      searchView = 'list';
      activeWishlistId = null;
      return '/jobs';
    }

    if (root === 'users') {
      const username = decodeSegment(segments[1]) || activeUsername || getUserBrowseFixture(scenarioId).profile.username;
      const nextView: UserBrowseView = segments[2] === 'shares' ? 'shares' : 'user';
      activePage = 'users';
      activeUsername = username;
      userView = nextView;
      userBrowse = { ...userBrowse, mode: nextView };
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
    if (page === 'jobs') {
      if (activePage === 'jobs' && searchView !== 'list') {
        searchView = 'list';
        activeWishlistId = null;
        setBrowserPath('/jobs');
        return;
      }

      activePage = 'jobs';
      if (searchView === 'results' && activeJobId && (searches.some((record) => record.id === activeJobId) || automaticJobs.some((job) => job.id === activeJobId))) {
        setBrowserPath(jobPath(activeJobId));
      } else if (searchView === 'wishlist' && activeWishlistId && wishlists.some((wishlist) => wishlist.id === activeWishlistId)) {
        setBrowserPath(wishlistPath(activeWishlistId));
      } else {
        searchView = 'list';
        activeWishlistId = null;
        setBrowserPath('/jobs');
      }
      return;
    }

    if (page === 'users') {
      activePage = 'users';
      setBrowserPath(userPath(activeUsername, userView));
      return;
    }

    activePage = page;
    setBrowserPath(pagePath(page));
  }

  function changeScenario(nextScenario: ScenarioId): void {
    scenarioId = nextScenario;
    searches = createInitialSearches(nextScenario);
    automaticJobs = [...createInitialAutomaticJobs(nextScenario), ...createInitialWishlistJobs(nextScenario)];
    wishlists = createInitialWishlists(nextScenario);
    activeWishlistId = null;
    clearJobSelection();
    activeJobId = searches.find((record) => record.fixture === 'boards')?.id ?? searches[0]?.id ?? automaticJobs[0]?.id ?? null;
    if (searchView !== 'list') searchView = activeJobId ? 'results' : 'list';
    const nextUsername = getUserBrowseFixture(nextScenario).profile.username;
    activeUsername = nextUsername;
    if (activePage === 'users') setBrowserPath(userPath(nextUsername, userView), true);
    if (activePage === 'jobs') setBrowserPath(searchView === 'results' && activeJobId ? jobPath(activeJobId) : '/jobs', true);
  }

  function useUserBrowse(next: UserBrowseDraft): void {
    userBrowse = { ...next };
  }

  function openUser(username: string): void {
    activeUsername = username;
    userBrowse = { ...userBrowse, mode: 'user' };
    userView = 'user';
    activePage = 'users';
    setBrowserPath(userPath(username, 'user'));
  }

  function openUserShares(username: string): void {
    activeUsername = username;
    userBrowse = { ...userBrowse, mode: 'shares' };
    userView = 'shares';
    activePage = 'users';
    setBrowserPath(userPath(username, 'shares'));
  }

  function openChatUser(username: string): void {
    chatInitialUsername = username;
    activePage = 'chat';
    setBrowserPath('/chat');
  }

  const userActions: UserLinkActions = {
    profile: openUser,
    shares: openUserShares,
    message: openChatUser,
  };

  function submitUserBrowse(next: UserBrowseDraft): void {
    useUserBrowse(next);
    activeUsername = next.query.trim() || getUserBrowseFixture(scenarioId).profile.username;
    userView = next.mode;
    activePage = 'users';
    setBrowserPath(userPath(activeUsername, next.mode));
  }

  function changeUserView(next: UserBrowseView): void {
    userView = next;
    userBrowse = { ...userBrowse, mode: next };
    activePage = 'users';
    setBrowserPath(userPath(activeUsername, next));
  }

  function openSearchRecord(record: SearchRecord): void {
    setActiveJob(record.id);
    searchView = 'results';
    activePage = 'jobs';
    setBrowserPath(jobPath(record.id));
  }


  function openAutomaticJob(job: AutomaticJobRecord): void {
    setActiveJob(job.id);
    searchView = 'results';
    activePage = 'jobs';
    setBrowserPath(jobPath(job.id));
  }

  function openWishlist(wishlist: WishlistRecord): void {
    activeWishlistId = wishlist.id;
    searchView = 'wishlist';
    activePage = 'jobs';
    setBrowserPath(wishlistPath(wishlist.id));
  }

  function runWishlist(wishlist: WishlistRecord): void {
    if (wishlist.lastRun.status === 'running' || !wishlist.items.length) return;
    const started = runWishlistNow(wishlist);
    wishlists = wishlists.map((candidate) => candidate.id === wishlist.id ? started.wishlist : candidate);
    automaticJobs = [...started.jobs, ...automaticJobs];
  }

  function cancelWishlistRun(wishlist: WishlistRecord): void {
    const runId = wishlist.lastRun.runId;
    if (wishlist.lastRun.status !== 'running' || !runId) return;
    const cancelled = wishlist.lastRun.stats.active + wishlist.lastRun.stats.pending;
    automaticJobs = automaticJobs.map((job) => job.wishlist?.wishlistId === wishlist.id && job.wishlist.runId === runId && isAutomaticJobActive(job, automaticJobs)
      ? { ...job, status: 'cancelled' as const, lifetime: 'retained' as const }
      : job);
    wishlists = wishlists.map((candidate) => candidate.id === wishlist.id
      ? {
          ...candidate,
          lastRun: {
            ...candidate.lastRun,
            status: 'cancelled' as const,
            when: 'Just now',
            stats: {
              ...candidate.lastRun.stats,
              active: 0,
              pending: 0,
              cancelled: candidate.lastRun.stats.cancelled + cancelled,
            },
          },
        }
      : candidate);
  }

  function startAutomaticJobs(records: AutomaticJobRecord[], rootId: string): void {
    automaticJobs = [...records, ...automaticJobs];
    const root = records.find((job) => job.id === rootId);
    if (root) openAutomaticJob(root);
  }

  function showSearchList(): void {
    searchView = 'list';
    activeWishlistId = null;
    activePage = 'jobs';
    setBrowserPath('/jobs');
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
    setActiveJob(rerun.id);
    searchView = 'results';
    activePage = 'jobs';
    setBrowserPath(jobPath(rerun.id), true);
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
    <Dashboard {scenario} {userActions} {wishlists} {automaticJobs} onopenwishlist={openWishlist} onrunwishlist={runWishlist} oncancelwishlist={cancelWishlistRun} />
  {:else if activePage === 'jobs'}
    <Jobs
      {search}
      {scenarioId}
      bind:searches
      bind:view={searchView}
      bind:automaticJobs
      bind:wishlists
      bind:activeJobId
      bind:activeWishlistId
      bind:selected={selectedJobResultIds}
      bind:selectedAggregateGroups={selectedAggregateGroupIds}
      bind:selectedAggregateFiles={selectedAggregateFileIds}
      {userActions}
      onopenrecord={openSearchRecord}
      onshowlist={showSearchList}
      onsearchagain={searchAgain}
      onopenjob={openAutomaticJob}
      onopenwishlist={openWishlist}
      onrunwishlist={runWishlist}
      oncancelwishlist={cancelWishlistRun}
      onstartjobs={startAutomaticJobs}
    />
  {:else if activePage === 'users'}
    <Users {scenarioId} username={activeUsername} view={userView} onviewchange={changeUserView} onmessageuser={openChatUser} />
  {:else if activePage === 'downloads'}
    <Downloads {scenario} {userActions} />
  {:else if activePage === 'uploads'}
    <Uploads {scenario} {userActions} />
  {:else if activePage === 'chat'}
    <Chat {scenario} {userActions} initialUsername={chatInitialUsername} />
  {:else}
    <Settings />
  {/if}
</AppShell>
