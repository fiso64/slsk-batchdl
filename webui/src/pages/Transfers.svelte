<script lang="ts">
  import type { PrototypeScenario, TransferStateDto } from '../mock/types';

  interface Props {
    scenario: PrototypeScenario;
  }

  let { scenario }: Props = $props();

  const activeStates = new Set(['Transferring', 'Downloading', 'Uploading']);

  let transfers = $derived(scenario.snapshot.transfers);
  let activeCount = $derived(transfers.filter((transfer) => activeStates.has(transfer.status.state)).length);
  let queuedCount = $derived(transfers.filter((transfer) => transfer.status.state === 'Queued').length);
  let failedCount = $derived(transfers.filter((transfer) => transfer.status.terminalOutcome === 'Failed').length);

  function displayPath(transfer: TransferStateDto): string {
    return transfer.identity.remotePath ?? transfer.status.localPath ?? '(unknown path)';
  }

  function displayUser(transfer: TransferStateDto): string {
    return transfer.identity.username ?? '(unknown user)';
  }
</script>

<section class="transfer-placeholder">
  <div class="placeholder-intro">
    <div>
      <p class="eyebrow">First UX target</p>
      <h2>Transfers</h2>
    </div>
    <p>
      This is deliberately not a transfer design yet. It only proves that a real OpenAPI-backed mock
      scenario can drive the page before we start deciding grouping, density, actions, and hierarchy.
    </p>
  </div>

  <div class="prototype-metrics" aria-label="Transfer scenario summary">
    <div><span>Total</span><strong>{transfers.length}</strong></div>
    <div><span>Active</span><strong>{activeCount}</strong></div>
    <div><span>Queued</span><strong>{queuedCount}</strong></div>
    <div><span>Failed</span><strong>{failedCount}</strong></div>
  </div>

  {#if scenario.connection === 'offline'}
    <div class="empty-state">
      <strong>Daemon unavailable</strong>
      <p>The offline scenario intentionally exposes no current transfer state.</p>
    </div>
  {:else if transfers.length === 0}
    <div class="empty-state">
      <strong>No current transfers</strong>
      <p>The empty scenario is useful for designing a meaningful zero-state later.</p>
    </div>
  {:else}
    <div class="data-probe">
      <div class="data-probe-heading">
        <strong>Raw data probe</strong>
        <span>Showing {Math.min(transfers.length, 6)} of {transfers.length}</span>
      </div>

      <ul>
        {#each transfers.slice(0, 6) as transfer (transfer.transferId)}
          <li>
            <div class="probe-primary">
              <span class="probe-path">{displayPath(transfer)}</span>
              <span class="probe-state">{transfer.status.state}</span>
            </div>
            <span class="probe-secondary">{displayUser(transfer)}</span>
          </li>
        {/each}
      </ul>
    </div>
  {/if}
</section>
