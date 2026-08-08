<script lang="ts">
  import type { PrototypeScenario } from '../mock/types';
  import { dashboardData, dashboardRangeIds, type DashboardRangeId } from '../prototype/dashboard';

  interface Props { scenario: PrototypeScenario; }
  let { scenario }: Props = $props();

  let range = $state<DashboardRangeId>('24h');
  let rankingTab = $state<'peers' | 'content' | 'errors'>('peers');
  let data = $derived(dashboardData[range]);

  let activeTransfers = $derived(scenario.snapshot.transfers.filter((transfer) => !transfer.status.isTerminal));
  let activeDownloads = $derived(activeTransfers.filter((transfer) => transfer.identity.direction === 'download'));
  let activeUploads = $derived(activeTransfers.filter((transfer) => transfer.identity.direction === 'upload'));
  let downloadRate = $derived(activeDownloads.reduce((sum, transfer) => sum + Number(transfer.progress.bytesPerSecond ?? 0), 0));
  let uploadRate = $derived(activeUploads.reduce((sum, transfer) => sum + Number(transfer.progress.bytesPerSecond ?? 0), 0));
  let queuedTransfers = $derived(activeTransfers.filter((transfer) => transfer.status.state === 'Queued').length);
  let distinctPeers = $derived(new Set(scenario.snapshot.transfers.map((transfer) => transfer.identity.username)).size);

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
          <button type="button" class:active={range === id} onclick={() => (range = id)}>{id}</button>
        {/each}
      </div>
    </div>
  </div>

  <div class="dashboard-metrics">
    <article class="dashboard-metric-card">
      <div class="metric-label download"><span aria-hidden="true">↓</span> Download</div>
      <strong>{formatRate(downloadRate)}</strong>
      <small>{activeDownloads.length} active · {queuedTransfers} queued total</small>
      <div class="metric-sparkline" aria-hidden="true">
        {#each data.downloadMbps.slice(-8) as point}
          <i style={`height:${Math.max(12, point * 9)}%`}></i>
        {/each}
      </div>
    </article>

    <article class="dashboard-metric-card">
      <div class="metric-label upload"><span aria-hidden="true">↑</span> Upload</div>
      <strong>{formatRate(uploadRate)}</strong>
      <small>{activeUploads.length} active upload{activeUploads.length === 1 ? '' : 's'}</small>
      <div class="metric-sparkline upload" aria-hidden="true">
        {#each data.uploadMbps.slice(-8) as point}
          <i style={`height:${Math.max(12, point * 11)}%`}></i>
        {/each}
      </div>
    </article>

    <article class="dashboard-metric-card">
      <div class="metric-label"><span aria-hidden="true">⇵</span> Transfers</div>
      <strong>{activeTransfers.length} active</strong>
      <small>{queuedTransfers} queued · {scenario.snapshot.transfers.filter((transfer) => transfer.status.isTerminal).length} recent terminal</small>
      <div class="transfer-mini-bars" aria-hidden="true"><i></i><i></i><i></i><i></i><i></i><i></i><i></i></div>
    </article>

    <article class="dashboard-metric-card">
      <div class="metric-label"><span aria-hidden="true">◎</span> Peers</div>
      <strong>{distinctPeers}</strong>
      <small>visible in current snapshot</small>
      <div class="dashboard-connection"><span class:offline={scenario.soulseek === 'disconnected'}></span>{scenario.soulseek === 'ready' ? 'Soulseek connected' : scenario.soulseek}</div>
    </article>
  </div>

  <section class="dashboard-panel activity-panel">
    <div class="dashboard-panel-heading">
      <div>
        <h2>Transfer activity</h2>
        <p>Bandwidth over time · {data.label}</p>
      </div>
      <span class="panel-context">download + upload</span>
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
          <path class="chart-area download" d={areaPath(data.downloadMbps)} />
          <path class="chart-line download" d={chartPath(data.downloadMbps)} />
          <path class="chart-area upload" d={areaPath(data.uploadMbps)} />
          <path class="chart-line upload" d={chartPath(data.uploadMbps)} />
        </svg>
        <div class="chart-x-labels">
          {#each data.chartLabels as label}<span>{label}</span>{/each}
        </div>
      </div>
    </div>
    <div class="chart-legend"><span><i class="download"></i>Download</span><span><i class="upload"></i>Upload</span></div>
  </section>

  <div class="dashboard-lower-area">
    <div class="dashboard-column">
      <section class="dashboard-panel ranking-panel">
        <div class="ranking-tabs" role="tablist" aria-label="Dashboard ranking">
          <button type="button" class:active={rankingTab === 'peers'} onclick={() => (rankingTab = 'peers')}>Peers</button>
          <button type="button" class:active={rankingTab === 'content'} onclick={() => (rankingTab = 'content')}>Content</button>
          <button type="button" class:active={rankingTab === 'errors'} onclick={() => (rankingTab = 'errors')}>Errors</button>
          <span>{data.label}</span>
        </div>

        {#if rankingTab === 'peers'}
          <div class="dashboard-ranking-table peers-table">
            <div class="dashboard-ranking-row heading"><span>#</span><span>Peer</span><span>Transferred</span><span>Files</span></div>
            {#each data.peers as peer, index}
              <div class="dashboard-ranking-row"><span>{index + 1}</span><span title={peer.peer}>{peer.peer}</span><span>{peer.transferred}</span><span>{peer.files}</span></div>
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
            <div class="dashboard-ranking-row heading"><span>Error</span><span>Count</span><span>Last seen</span></div>
            {#each data.errors as error}
              <div class="dashboard-ranking-row"><span title={error.error}>{error.error}</span><span>{error.count}</span><span>{error.lastSeen}</span></div>
            {/each}
          </div>
        {/if}
      </section>

      <section class="dashboard-panel transfer-summary-panel">
        <div class="dashboard-panel-heading compact">
          <div><h2>Transfer summary</h2><p>{data.label}</p></div>
        </div>
        <div class="summary-metrics">
          <div><span>Downloaded</span><strong>{data.summary.downloaded}</strong><small>{data.summary.downloadFiles} files</small></div>
          <div><span>Uploaded</span><strong>{data.summary.uploaded}</strong><small>{data.summary.uploadFiles} files</small></div>
          <div><span>Share ratio</span><strong>{data.summary.shareRatio}</strong><small>{data.summary.ratioDelta} over range</small></div>
        </div>
      </section>
    </div>

    <div class="dashboard-column">
      <section class="dashboard-panel recent-activity-panel">
        <div class="dashboard-panel-heading compact"><div><h2>Recent activity</h2><p>What the daemon has been doing</p></div><span class="live-indicator"><i></i>Live</span></div>
        <div class="activity-feed">
          <div><i class="feed-icon download">↓</i><p><strong>Music Is Math.flac</strong><span>downloaded from nightshift</span></p><small>2m</small></div>
          <div><i class="feed-icon upload">↑</i><p><strong>27 files uploaded</strong><span>to cassetteculture</span></p><small>8m</small></div>
          <div><i class="feed-icon chat">○</i><p><strong>New message</strong><span>from tape_loop</span></p><small>14m</small></div>
          <div><i class="feed-icon download">↓</i><p><strong>Xtal.flac</strong><span>download completed</span></p><small>31m</small></div>
        </div>
      </section>

      <section class="dashboard-panel health-panel">
        <div class="dashboard-panel-heading compact"><div><h2>Daemon health</h2><p>Current runtime state</p></div><span class:offline={scenario.connection === 'offline'} class="health-badge"><i></i>{scenario.connection === 'connected' ? 'Healthy' : 'Offline'}</span></div>
        <div class="health-list">
          <div><span>Soulseek</span><strong>{scenario.soulseek}</strong></div>
          <div><span>Daemon</span><strong>{scenario.connection}</strong></div>
          <div><span>Transfer errors</span><strong>{scenario.snapshot.transfers.filter((transfer) => transfer.status.terminalOutcome === 'Failed').length} recent</strong></div>
          <div><span>API latency</span><strong>{scenario.connection === 'connected' ? '14 ms' : '—'}</strong></div>
        </div>
      </section>
    </div>
  </div>
</section>
