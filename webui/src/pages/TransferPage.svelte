<script lang="ts">
  import type { PrototypeScenario, TransferStateDto } from '../mock/types';
  import {
    formatEta,
    formatSpeed,
    progressPercent,
    transferFilename,
    transferFolder,
    transferUser,
  } from '../prototype/transfers';

  interface Props {
    scenario: PrototypeScenario;
    direction: 'download' | 'upload';
  }

  let { scenario, direction }: Props = $props();

  let transfers = $derived(
    scenario.snapshot.transfers.filter((transfer) => transfer.identity.direction === direction),
  );
  let current = $derived(transfers.filter((transfer) => !transfer.status.isTerminal));
  let recent = $derived(transfers.filter((transfer) => transfer.status.isTerminal));
  let title = $derived(direction === 'download' ? 'Downloads' : 'Uploads');

  function stateLabel(transfer: TransferStateDto): string {
    if (transfer.status.state === 'Queued') return 'Queued';
    if (transfer.status.terminalOutcome === 'Succeeded') return 'Completed';
    if (transfer.status.terminalOutcome === 'Failed') return 'Failed';
    return transfer.status.state;
  }
</script>

<section class="page transfer-page">
  <header class="page-heading">
    <p class="eyebrow">Transfers</p>
    <h1>{title}</h1>
  </header>

  {#if scenario.connection === 'offline'}
    <div class="empty-state">
      <strong>Daemon unavailable</strong>
      <p>Current transfer state cannot be loaded while the daemon is offline.</p>
    </div>
  {:else}
    <section class="transfer-section">
      <div class="section-heading">
        <h2>Active</h2>
        <span>{current.length} {current.length === 1 ? 'transfer' : 'transfers'}</span>
      </div>

      {#if current.length === 0}
        <div class="empty-state compact">
          <strong>No active {title.toLowerCase()}</strong>
          <p>The page stays quiet when there is nothing in progress or queued.</p>
        </div>
      {:else}
        <div class="transfer-list">
          {#each current as transfer (transfer.transferId)}
            {@const percent = progressPercent(transfer)}
            {@const speed = formatSpeed(transfer.progress.bytesPerSecond)}
            {@const eta = formatEta(transfer)}
            <article class="transfer-row">
              <div class="transfer-main">
                <strong title={transferFilename(transfer)}>{transferFilename(transfer)}</strong>
                <small title={transferFolder(transfer)}>
                  {transferFolder(transfer)} · {direction === 'download' ? 'from' : 'to'} {transferUser(transfer)}
                </small>
                {#if percent !== null && transfer.status.state !== 'Queued'}
                  <div class="progress" aria-label={`${percent.toFixed(0)}% complete`}>
                    <span style={`width: ${percent}%`}></span>
                  </div>
                {/if}
              </div>
              <div class="transfer-stat">
                <strong>{percent !== null && transfer.status.state !== 'Queued' ? `${percent.toFixed(0)}%` : stateLabel(transfer)}</strong>
                <small>{[speed, eta].filter(Boolean).join(' · ') || stateLabel(transfer)}</small>
              </div>
            </article>
          {/each}
        </div>
      {/if}
    </section>

    <section class="transfer-section recent-transfers">
      <div class="section-heading">
        <h2>Recent</h2>
        <span>completed + failed</span>
      </div>

      {#if recent.length === 0}
        <div class="empty-recent">No recent {title.toLowerCase()} in this scenario.</div>
      {:else}
        <div class="recent-transfer-list">
          {#each recent.slice(0, 12) as transfer (transfer.transferId)}
            <div class="recent-transfer-row">
              <span>
                <strong>{transferFilename(transfer)}</strong>
                <small>{transferUser(transfer)}</small>
              </span>
              <span class:failed={transfer.status.terminalOutcome === 'Failed'}>
                {stateLabel(transfer)}{transfer.status.failureReason ? ` · ${transfer.status.failureReason}` : ''}
              </span>
            </div>
          {/each}
        </div>
      {/if}
    </section>
  {/if}
</section>
