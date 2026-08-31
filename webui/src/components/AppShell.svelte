<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { PrototypeScenario, ScenarioId } from '../mock/types';
  import { navigationItems, type PageId } from '../prototype/navigation';
  import type { SearchDraft } from '../prototype/search';
  import type { PrototypeSearchConditions } from '../prototype/search-config';
  import type { UserBrowseDraft } from '../prototype/users';
  import { chatRuntimeForScenario } from '../prototype/chat';
  import { humanizeStateValue, soulseekClientStatusLabel } from '../prototype/status';
  import { blockingKeyboardSurfaceOpen, keyboardShortcutHasModifier, keyboardTargetIsEditing } from '../lib/keyboard';
  import GlobalSearch from './GlobalSearch.svelte';
  import PrototypeScenarioPicker from './PrototypeScenarioPicker.svelte';
  import Sidebar from './Sidebar.svelte';

  interface Props {
    activePage: PageId;
    scenarioId: ScenarioId;
    scenario: PrototypeScenario;
    search: SearchDraft;
    userBrowse: UserBrowseDraft;
    onnavigate: (page: PageId) => void;
    onscenariochange: (scenario: ScenarioId) => void;
    onsearchchange: (search: SearchDraft) => void;
    onsearchsubmit: (search: SearchDraft, conditions: PrototypeSearchConditions) => void;
    onuserbrowsechange: (browse: UserBrowseDraft) => void;
    onuserbrowsesubmit: (browse: UserBrowseDraft) => void;
    children: Snippet;
  }

  let {
    activePage,
    scenarioId,
    scenario,
    search,
    userBrowse,
    onnavigate,
    onscenariochange,
    onsearchchange,
    onsearchsubmit,
    onuserbrowsechange,
    onuserbrowsesubmit,
    children,
  }: Props = $props();

  let currentTransfers = $derived(scenario.snapshot.transfers.filter((transfer) => !transfer.status.isTerminal));
  let downloadCount = $derived(currentTransfers.filter((transfer) => transfer.identity.direction === 'download').length);
  let uploadCount = $derived(currentTransfers.filter((transfer) => transfer.identity.direction === 'upload').length);
  let chatRuntime = $derived(chatRuntimeForScenario(scenarioId));
  let unreadChats = $derived(Number(chatRuntime.unreadPrivateMessageCount) + Number(chatRuntime.unreadRoomMessageCount));
  let daemonReachable = $derived(scenario.connection === 'connected');
  let soulseekStatus = $derived(soulseekClientStatusLabel(scenario.soulseekClient, daemonReachable));
  let daemonStatus = $derived(humanizeStateValue(scenario.connection));

  function handleGlobalNavigationShortcut(event: KeyboardEvent): void {
    if (event.defaultPrevented || event.repeat || keyboardShortcutHasModifier(event)) return;
    if (keyboardTargetIsEditing(event.target) || blockingKeyboardSurfaceOpen()) return;
    if (!/^[1-9]$/.test(event.key)) return;
    const target = navigationItems[Number(event.key) - 1];
    if (!target) return;
    event.preventDefault();
    onnavigate(target.id);
  }
</script>

<svelte:window onkeydown={handleGlobalNavigationShortcut} />

<div class="app-shell">
  <aside class="sidebar">
    <div class="brand">
      <div class="brand-mark" aria-hidden="true">S</div>
      <div class="brand-copy">
        <strong>sockseek</strong>
        <span>prototype</span>
      </div>
    </div>

    <Sidebar
      {activePage}
      {downloadCount}
      {uploadCount}
      {unreadChats}
      {onnavigate}
    />

    <div class="sidebar-spacer"></div>

    <div class="prototype-tools">
      <PrototypeScenarioPicker value={scenarioId} onchange={onscenariochange} />
    </div>

    <div class="connection-status" aria-label="Connection status">
      <div>
        <span class:warning={daemonReachable && !scenario.soulseekClient.isReady} class:offline={!daemonReachable} class="status-dot"></span>
        <span>Soulseek</span>
        <strong>{soulseekStatus}</strong>
      </div>
      <div>
        <span class:offline={!daemonReachable} class="status-dot"></span>
        <span>Daemon</span>
        <strong>{daemonStatus}</strong>
      </div>
    </div>

    <Sidebar
      {activePage}
      {downloadCount}
      {uploadCount}
      {unreadChats}
      placement="secondary"
      {onnavigate}
    />
  </aside>

  <div class="workspace">
    <header class="topbar">
      <div class="topbar-inner">
        <GlobalSearch
          variant={activePage === 'users' ? 'user' : 'content'}
          value={search}
          userValue={userBrowse}
          onchange={onsearchchange}
          onsubmit={onsearchsubmit}
          onuserchange={onuserbrowsechange}
          onusersubmit={onuserbrowsesubmit}
        />
      </div>
    </header>

    <main class:chat-content={activePage === 'chat'} class="page-content">
      {@render children()}
    </main>
  </div>
</div>
