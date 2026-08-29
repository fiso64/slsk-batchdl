<script lang="ts">
  import UsernameLink from '../components/UsernameLink.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import type { UserLinkActions } from '../prototype/navigation';
  import { dashboardContractFor, dashboardData, emptyDashboardData, dashboardRangeIds, dashboardRangeLabels, type DashboardPeerRow, type DashboardRangeId } from '../prototype/dashboard';
  import { resourceStateForScenario } from '../prototype/resource-state';
  import { humanizeStateValue, soulseekClientStatusLabel } from '../prototype/status';

  interface Props { scenario: PrototypeScenario; userActions: UserLinkActions; }
  let { scenario, userActions }: Props = $props();

  let range = $state<DashboardRangeId>('24h');
  let rankingTab = $state<'downloads' | 'uploads' | 'content' | 'errors'>('downloads');
  let data = $derived(scenario.id === 'empty' ? emptyDashboardData[range] : dashboardData[range]);
  let daemonReachable = $derived(scenario.connection === 'connected');
  let soulseekStatus = $derived(soulseekClientStatusLabel(scenario.soulseekClient, daemonReachable));
  let daemonStatus = $derived(humanizeStateValue(scenario.connection));
  let resourceState = $derived(resourceStateForScenario(scenario.id, 'dashboard'));
  let contract = $derived(dashboardContractFor(range, { data, partialRetention: scenario.id === 'stress' }));
  let hasDownloadHistory = $derived(data.downloadMbps.some((value) => value > 0) || data.summary.downloadFiles > 0);
  let hasUploadHistory = $derived(data.uploadMbps.some((value) => value > 0) || data.summary.uploadFiles > 0);
  let hasHistoricalTransfers = $derived(hasDownloadHistory || hasUploadHistory);

  let activeTransfers = $derived(scenario.snapshot.transfers.filter((transfer) => !transfer.status.isTerminal));
  let activeDownloads = $derived(activeTransfers.filter((transfer) => transfer.identity.direction === 'download'));
  let activeUploads = $derived(activeTransfers.filter((transfer) => transfer.identity.direction === 'upload'));
  let downloadRate = $derived(activeDownloads.reduce((sum, transfer) => sum + Number(transfer.progress.bytesPerSecond ?? 0), 0));
  let uploadRate = $derived(activeUploads.reduce((sum, transfer) => sum + Number(transfer.progress.bytesPerSecond ?? 0), 0));
  let queuedTransfers = $derived(activeTransfers.filter((transfer) => transfer.status.state === 'Queued').length);
  let shareRatioSamples = $derived(data.uploadMbps.map((upload, index) => upload / Math.max(data.downloadMbps[index] ?? 0, 0.1)));

  function metricBarHeight(values: number[], value: number): number {
    const maximum = Math.max(1, ...values);
    return Math.round(14 + (value / maximum) * 86);
  }

  function formatRate(bytesPerSecond: number): string {
    if (bytesPerSecond <= 0) return '0 B/s';
    if (bytesPerSecond >= 1_000_000) return `${(bytesPerSecond / 1_000_000).toFixed(2)} MB/s`;
    return `${Math.round(bytesPerSecond / 1_000)} KB/s`;
  }

  function chartPath(values: number[], width = 760, height = 150): string {
    const max = Math.max(12, ...data.downloadMbps, ...data.uploadMbps);
    const step = width / Math.max(1, values.length - 1);
    const points = values.map((value, index) => ({
      x: index * step,
      y: height - (value / max) * (height - 12),
    }));

    if (points.length === 0) return '';
    const first = points[0]!;
    if (points.length === 1) return `M ${first.x.toFixed(1)} ${first.y.toFixed(1)}`;

    // Smooth each segment with horizontal Bezier handles. This keeps every
    // sample value exact while avoiding the overshoot that a free spline can
    // introduce around sharp bandwidth peaks.
    const handle = step * 0.42;
    return points.slice(1).reduce((path, point, index) => {
      const previous = points[index]!;
      return `${path} C ${(previous.x + handle).toFixed(1)} ${previous.y.toFixed(1)}, ${(point.x - handle).toFixed(1)} ${point.y.toFixed(1)}, ${point.x.toFixed(1)} ${point.y.toFixed(1)}`;
    }, `M ${first.x.toFixed(1)} ${first.y.toFixed(1)}`);
  }

  function areaPath(values: number[], width = 760, height = 150): string {
    return `${chartPath(values, width, height)} L ${width} ${height} L 0 ${height} Z`;
  }
</script>

<section class="page dashboard-page">
  <div class="dashboard-heading">
    <div class="page-heading dashboard-title">
      <p class="eyebrow">Overview</p>
      <h1>Dashboard</h1>
    </div>

    <div class="dashboard-range-control" aria-label="Dashboard history range">
      <span>History range</span>
      <div class="range-buttons">
        {#each dashboardRangeIds as id}
          <button type="button" class:active={range === id} onclick={() => (range = id)}>{dashboardRangeLabels[id]}</button>
        {/each}
      </div>
    </div>
  </div>

  <ResourceStateNotice state={resourceState} />

  <div class="dashboard-metrics">
    <article class="dashboard-metric-card">
      <div class="metric-label download"><span aria-hidden="true">↓</span> Downloaded</div>
      <strong>{data.summary.downloaded}</strong>
      <small>{data.summary.downloadFiles} files in range</small>
      <div class="metric-sparkline" aria-hidden="true">
        {#if hasDownloadHistory}
          {#each data.downloadMbps.slice(-8) as point}
            <i style={`height:${metricBarHeight(data.downloadMbps, point)}%`}></i>
          {/each}
        {/if}
      </div>
    </article>

    <article class="dashboard-metric-card">
      <div class="metric-label upload"><span aria-hidden="true">↑</span> Uploaded</div>
      <strong>{data.summary.uploaded}</strong>
      <small>{data.summary.uploadFiles} files in range</small>
      <div class="metric-sparkline upload" aria-hidden="true">
        {#if hasUploadHistory}
          {#each data.uploadMbps.slice(-8) as point}
            <i style={`height:${metricBarHeight(data.uploadMbps, point)}%`}></i>
          {/each}
        {/if}
      </div>
    </article>

    <article class="dashboard-metric-card">
      <div class="metric-label"><span aria-hidden="true">⇵</span> Share ratio</div>
      <strong>{data.summary.shareRatio}</strong>
      <small>{hasHistoricalTransfers ? (range === 'all' ? 'All retained history' : `${data.summary.ratioDelta} vs previous range`) : '—'}</small>
      <div class="metric-sparkline ratio" aria-hidden="true">
        {#if hasHistoricalTransfers}
          {#each shareRatioSamples.slice(-8) as point}
            <i style={`height:${metricBarHeight(shareRatioSamples, point)}%`}></i>
          {/each}
        {/if}
      </div>
    </article>

    <article class="dashboard-metric-card">
      <div class="metric-label"><span aria-hidden="true">◎</span> Distinct peers</div>
      <strong>{data.summary.distinctPeers}</strong>
      <small>across transfers in range</small>
    </article>
  </div>

  <section class="dashboard-panel activity-panel" data-bucket-seconds={contract.range.bucketSeconds}>
    <div class="dashboard-panel-heading">
      <h2>Transfer activity</h2>
      <span class="panel-context">{data.label}</span>
    </div>

    <div class="activity-chart">
      <div class="chart-y-labels"><span>12 MB/s</span><span>8 MB/s</span><span>4 MB/s</span><span>0</span></div>
      <div class="chart-canvas">
        <svg viewBox="0 0 760 150" preserveAspectRatio="none" role="img" aria-label={`Transfer activity for ${data.label}`}>
          <g class="chart-grid">
            <line x1="0" y1="12" x2="760" y2="12" />
            <line x1="0" y1="58" x2="760" y2="58" />
            <line x1="0" y1="104" x2="760" y2="104" />
            <line x1="0" y1="149" x2="760" y2="149" />
            <line x1="126" y1="0" x2="126" y2="150" />
            <line x1="253" y1="0" x2="253" y2="150" />
            <line x1="380" y1="0" x2="380" y2="150" />
            <line x1="506" y1="0" x2="506" y2="150" />
            <line x1="633" y1="0" x2="633" y2="150" />
          </g>
          {#if hasHistoricalTransfers}
            <path class="chart-area download" d={areaPath(data.downloadMbps)} />
            <path class="chart-line download" d={chartPath(data.downloadMbps)} />
            <path class="chart-area upload" d={areaPath(data.uploadMbps)} />
            <path class="chart-line upload" d={chartPath(data.uploadMbps)} />
          {/if}
        </svg>
        <div class="chart-x-labels">
          {#each data.chartLabels as label}<span>{label}</span>{/each}
        </div>
      </div>
    </div>
    <div class="chart-legend"><span><i class="download"></i>Download</span><span><i class="upload"></i>Upload</span></div>
  </section>

  <div class="dashboard-lower-area">
    <section class="dashboard-panel ranking-panel">
      <div class="ranking-tabs" role="tablist" aria-label="Dashboard ranking">
        <button type="button" class:active={rankingTab === 'downloads'} onclick={() => (rankingTab = 'downloads')}>Downloads</button>
        <button type="button" class:active={rankingTab === 'uploads'} onclick={() => (rankingTab = 'uploads')}>Uploads</button>
        <button type="button" class:active={rankingTab === 'content'} onclick={() => (rankingTab = 'content')}>Content</button>
        <button type="button" class:active={rankingTab === 'errors'} onclick={() => (rankingTab = 'errors')}>Errors</button>
        <span>{data.label}</span>
      </div>

      {#if rankingTab === 'downloads' || rankingTab === 'uploads'}
        {@const rows: DashboardPeerRow[] = rankingTab === 'downloads' ? data.downloadUsers : data.uploadUsers}
        <div class="dashboard-ranking-table peers-table">
          <div class="dashboard-ranking-row heading"><span>#</span><span>User</span><span>{rankingTab === 'downloads' ? 'Downloaded' : 'Uploaded'}</span><span>Files</span></div>
          {#each rows as peer, index}
            <div class="dashboard-ranking-row"><span>{index + 1}</span><span class="dashboard-peer-name"><UsernameLink username={peer.peer} actions={userActions} /></span><span>{peer.transferred}</span><span>{peer.files}</span></div>
          {/each}
        </div>
      {:else if rankingTab === 'content'}
        <div class="dashboard-ranking-table content-table">
          <div class="dashboard-ranking-row heading"><span>#</span><span>Folder</span><span>Downloads</span><span>Peers</span></div>
          {#each data.content as item, index}
            <div class="dashboard-ranking-row"><span>{index + 1}</span><span title={item.folder}>{item.folder}</span><span>{item.downloads}</span><span>{item.peers}</span></div>
          {/each}
        </div>
      {:else}
        <div class="dashboard-ranking-table errors-table">
          <div class="dashboard-ranking-row heading"><span>#</span><span>Error</span><span>Count</span><span>Last seen</span></div>
          {#each data.errors as error, index}
            <div class="dashboard-ranking-row"><span>{index + 1}</span><span title={error.error}>{error.error}</span><span>{error.count}</span><span>{error.lastSeen}</span></div>
          {/each}
        </div>
      {/if}
    </section>

    <div class="dashboard-bottom-grid">
      <section class="dashboard-panel current-transfer-panel">
        <div class="dashboard-panel-heading compact">
          <h2>Transfer rates</h2>
        </div>
        <div class="summary-metrics">
          <div><span>Download</span><strong class="rate-value download"><i aria-hidden="true">↓</i>{formatRate(downloadRate)}</strong><small>{activeDownloads.length} active download{activeDownloads.length === 1 ? '' : 's'}</small></div>
          <div><span>Upload</span><strong class="rate-value upload"><i aria-hidden="true">↑</i>{formatRate(uploadRate)}</strong><small>{activeUploads.length} active upload{activeUploads.length === 1 ? '' : 's'}</small></div>
          <div><span>Active transfers</span><strong>{activeTransfers.length}</strong><small>{queuedTransfers} queued transfer row{queuedTransfers === 1 ? '' : 's'}</small></div>
        </div>
      </section>

      <section class="dashboard-panel health-panel">
        <div class="dashboard-panel-heading compact"><h2>Daemon health</h2><span class:offline={scenario.connection === 'offline'} class="health-badge"><i></i>{scenario.connection === 'connected' ? 'Healthy' : 'Offline'}</span></div>
        <div class="health-list">
          <div><span>Soulseek</span><strong>{soulseekStatus}</strong></div>
          <div><span>Daemon</span><strong>{daemonStatus}</strong></div>
          <div><span>Transfer errors</span><strong>{scenario.snapshot.transfers.filter((transfer) => transfer.status.terminalOutcome === 'Failed').length} recent</strong></div>
          <div><span>API latency</span><strong>{scenario.connection === 'connected' ? '14 ms' : '—'}</strong></div>
        </div>
      </section>
    </div>
  </div>
</section>
