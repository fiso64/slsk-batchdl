using System.Collections.Concurrent;
using Enums;
using Jobs;
using Models;
using Konsole;
using ProgressBar = Konsole.ProgressBar;

namespace Utilities
{
    /// <summary>
    /// IProgressReporter implementation that renders Konsole progress bars in the terminal.
    ///
    /// Structured events (ReportSearchStart, …) are no-ops; use JsonStreamProgressReporter
    /// alongside this if machine-readable output is also needed.
    ///
    /// Bar lifecycle per song:
    ///   ReportSongSearching       → create bar, "Searching: {song}"
    ///   ReportDownloadStart       → set base display text (for album files, also creates bar)
    ///   ReportDownloadStateChanged → update state label + advance spinner frame on InProgress
    ///   ReportDownloadProgress    → update percentage fill
    ///   ReportTrackStateChanged   → final label (Succeeded/Failed), then drop reference
    /// </summary>
    public class CliProgressReporter : IProgressReporter
    {
        private readonly Config _config;

        // Per-song bar state.
        private readonly ConcurrentDictionary<SongJob, BarData> _bars = new();

        // Per-job bar for parallel album/aggregate source searches.
        private readonly ConcurrentDictionary<Job, ProgressBar?> _jobBars = new();

        // Saved bar text/pos for restoring after OnComplete execution.
        private readonly ConcurrentDictionary<SongJob, (string text, int pos)> _savedState = new();

        static readonly char[] SpinFrames = { '|', '/', '—', '\\' };

        sealed class BarData
        {
            public ProgressBar? Bar;
            public string       BaseText   = "";
            public string       StateLabel = "";
            public int          SpinIndex  = 0;
            public int          Pct        = 0;
        }

        private readonly CancellationTokenSource _tickCts = new();

        // Wired up by the CLI entry point so key events reach engine handlers
        // without racing against console I/O from the tick loop.
        public Action<ConsoleKey>? OnKeyPressed { get; set; }

        public CliProgressReporter(Config config)
        {
            _config = config;
            _ = TickLoopAsync(_tickCts.Token);
        }

        public void Stop() => _tickCts.Cancel();

        async Task TickLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct);

                    // Advance spinner on all in-progress bars.
                    foreach (var (_, d) in _bars)
                    {
                        if (d.StateLabel != "InProgress" || d.Bar == null) continue;
                        d.SpinIndex++;
                        try { d.Bar.Refresh(d.Pct, BuildText(d)); } catch { }
                    }

                    // Poll keyboard — done here so it doesn't race with bar.Refresh above.
                    if (OnKeyPressed != null && !Console.IsInputRedirected && Console.KeyAvailable)
                        OnKeyPressed(Console.ReadKey(intercept: true).Key);
                }
            }
            catch (OperationCanceledException) { }
        }


        // ── bar text construction ────────────────────────────────────────────

        static string BuildText(BarData d)
        {
            string prefix;
            if (d.StateLabel == "InProgress")
            {
                char frame = SpinFrames[d.SpinIndex % SpinFrames.Length];
                prefix = $"{frame} ";
            }
            else if (d.StateLabel is "Searching" or "Not found" or "Failed" or "Succeeded")
            {
                prefix = "  ";
            }
            else
            {
                // Queued (R), Queued (L), Queued, Initialising, Stalled, etc.
                prefix = "  ";
            }

            string label = (d.StateLabel + ":").PadRight(12);
            return $"{prefix}{label} {d.BaseText}";
        }


        // ── structured events — no-ops ───────────────────────────────────────

        public void ReportTrackList(IEnumerable<SongJob> songs, int listIndex = 0) { }
        public void ReportSearchStart(SongJob song) { }
        public void ReportSearchResult(SongJob song, int resultCount, FileCandidate? chosen = null) { }
        public void ReportOverallProgress(int downloaded, int failed, int total) { }
        public void ReportJobComplete(int downloaded, int failed, int total) { }


        // ── structured events — with rendering ──────────────────────────────

        public void ReportDownloadStart(SongJob song, FileCandidate candidate)
        {
            // Bar may not exist yet for album files (no search phase precedes the download).
            var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar(_config) });
            d.StateLabel = "Queued";
            d.BaseText   = Printing.DisplayString(song.Query, candidate.File, candidate.Response, infoFirst: false);
            d.Pct        = 0;
            Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: true);
        }

        public void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes)
        {
            if (!_bars.TryGetValue(song, out var d)) return;
            d.Pct = totalBytes > 0 ? (int)(bytesTransferred * 100 / totalBytes) : 0;
            // No Refresh here — the tick loop renders at 100ms intervals.
        }

        public void ReportTrackStateChanged(SongJob song, FileCandidate? chosen = null)
        {
            if (_bars.TryGetValue(song, out var d) && d.Bar != null)
            {
                bool succeeded = chosen != null || song.State == TrackState.Downloaded;
                d.StateLabel = succeeded ? "Succeeded" : "Failed";
                if (succeeded) d.Pct = 100;
                Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
            }
            _bars.TryRemove(song, out _);
            _savedState.TryRemove(song, out _);
        }


        // ── display events ───────────────────────────────────────────────────

        public void ReportJobStarted(Job job, bool parallel)
        {
            var bar = parallel ? Printing.GetProgressBar(_config) : null;
            _jobBars[job] = bar;
            string prefix = bar != null ? "  " : "";
            Printing.RefreshOrPrint(bar, 0, $"{prefix}{job.GetType().Name}: {job.ToString(true)}, searching..", print: true);
        }

        public void ReportJobFolderRetrieving(Job job)
        {
            _jobBars.TryGetValue(job, out var bar);
            Printing.RefreshOrPrint(bar, 0, "Getting files in folder..", print: true);
        }

        public void ReportJobCompleted(Job job, bool found, int lockedFiles)
        {
            _jobBars.TryGetValue(job, out var bar);
            if (!found)
            {
                string lockedMsg = lockedFiles > 0 ? $" (Found {lockedFiles} locked files)" : "";
                Printing.RefreshOrPrint(bar, 0, $"No results: {job}{lockedMsg}", print: true);
            }
            else if (bar != null)
            {
                Printing.RefreshOrPrint(bar, 0, $"Found results: {job}", print: true);
            }
            _jobBars.TryRemove(job, out _);
        }

        public void ReportSongSearching(SongJob song)
        {
            bool isFirst = !_bars.ContainsKey(song);
            var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar(_config) });
            d.StateLabel = "Searching";
            d.BaseText   = song.ToString();
            Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: isFirst);
        }

        public void ReportSongNotFound(SongJob song)
        {
            if (!_bars.TryGetValue(song, out var d)) return;
            d.StateLabel = "Not found";
            Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: true);
        }

        public void ReportSongFailed(SongJob song)
        {
            if (!_bars.TryGetValue(song, out var d)) return;
            d.StateLabel = "Failed";
            Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: true);
        }

        public void ReportDownloadStateChanged(SongJob song, string stateLabel)
        {
            if (!_bars.TryGetValue(song, out var d)) return;
            d.StateLabel = stateLabel;
            Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
        }

        public void ReportOnCompleteStart(SongJob song)
        {
            if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
            _savedState[song] = (d.Bar.Line1 ?? "", d.Bar.Current);
            Printing.RefreshOrPrint(d.Bar, d.Bar.Current, "  OnComplete:  " + $"{song}");
        }

        public void ReportOnCompleteEnd(SongJob song)
        {
            if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
            if (_savedState.TryGetValue(song, out var saved))
                Printing.RefreshOrPrint(d.Bar, saved.pos, saved.text);
        }
    }
}
