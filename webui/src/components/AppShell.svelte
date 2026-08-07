<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { PrototypeScenario, ScenarioId } from '../mock/types';
  import type { PageId } from '../prototype/navigation';
  import type { SearchDraft } from '../prototype/search';
  import GlobalSearch from './GlobalSearch.svelte';
  import PrototypeScenarioPicker from './PrototypeScenarioPicker.svelte';
  import Sidebar from './Sidebar.svelte';

  interface Props {
    activePage: PageId;
    scenarioId: ScenarioId;
    scenario: PrototypeScenario;
    search: SearchDraft;
    onnavigate: (page: PageId) => void;
    onscenariochange: (scenario: ScenarioId) => void;
    onsearchchange: (search: SearchDraft) => void;
    onsearchsubmit: (search: SearchDraft) => void;
    children: Snippet;
  }

  let {
    activePage,
    scenarioId,
    scenario,
    search,
    onnavigate,
    onscenariochange,
    onsearchchange,
    onsearchsubmit,
    children,
  }: Props = $props();

  let currentTransfers = $derived(scenario.snapshot.transfers.filter((transfer) => !transfer.status.isTerminal));
  let downloadCount = $derived(currentTransfers.filter((transfer) => transfer.identity.direction === 'download').length);
  let uploadCount = $derived(currentTransfers.filter((transfer) => transfer.identity.direction === 'upload').length);
  const unreadChats = 2;
</script>

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
        <span class:warning={scenario.soulseek === 'connecting'} class:offline={scenario.soulseek === 'disconnected'} class="status-dot"></span>
        <span>Soulseek</span>
        <strong>{scenario.soulseek}</strong>
      </div>
      <div>
        <span class:offline={scenario.connection === 'offline'} class="status-dot"></span>
        <span>Daemon</span>
        <strong>{scenario.connection === 'connected' ? 'local' : 'offline'}</strong>
      </div>
    </div>

    <button
      type="button"
      class:active={activePage === 'settings'}
      class="settings-nav"
      aria-current={activePage === 'settings' ? 'page' : undefined}
      onclick={() => onnavigate('settings')}
    >
      <span class="nav-icon" aria-hidden="true">⚙</span>
      <span class="nav-label">Settings</span>
    </button>
  </aside>

  <div class="workspace">
    <header class="topbar">
      <GlobalSearch value={search} onchange={onsearchchange} onsubmit={onsearchsubmit} />
      <div class="user-badge" aria-label="Prototype user">fi</div>
    </header>

    <div class="scenario-strip">
      <strong>{scenario.label}</strong>
      <span>{scenario.description}</span>
    </div>

    <main class:chat-content={activePage === 'chat'} class="page-content">
      {@render children()}
    </main>
  </div>
</div>
