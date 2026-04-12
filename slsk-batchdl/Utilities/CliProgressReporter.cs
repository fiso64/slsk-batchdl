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
    ///   ReportDownloadStart       → set base display text (for album files, bar is pre-allocated)
    ///   ReportDownloadStart       → set base display text (for album files, bar is pre-allocated)
    ///   ReportDownloadStateChanged → update state label + advance spinner frame on InProgress
    ///   ReportDownloadProgress    → update percentage fill
    ///   ReportStateChanged        → final label (Succeeded/Failed), then drop reference
    ///
    /// Album block lifecycle:
    ///   ReportJobStarted          → allocate header bar, "AlbumJob: {name}, searching.."
    ///   ReportAlbumDownloadStarted → reserve N lines, allocate N song bars, map SongJob→bar
    ///   (downloads proceed using per-song events above, looking up pre-allocated bars)
    ///   tick loop                 → refresh header with "N/M done" aggregate
    ///   ReportJobCompleted        → finalize header (not found / done)
    ///   ReportStateChanged        → final label (Succeeded/Failed), then drop reference
    ///
    /// Album block lifecycle:
    ///   ReportJobStarted          → allocate header bar, "AlbumJob: {name}, searching.."
    ///   ReportAlbumDownloadStarted → reserve N lines, allocate N song bars, map SongJob→bar
    ///   (downloads proceed using per-song events above, looking up pre-allocated bars)
    ///   tick loop                 → refresh header with "N/M done" aggregate
    ///   ReportJobCompleted        → finalize header (not found / done)
    /// </summary>
    public class CliProgressReporter : IProgressReporter
    {
        private readonly Config _config;

        // Per-song bar state.
        private readonly ConcurrentDictionary<SongJob, BarData> _bars = new();

        // Per-job (album/aggregate) header bar.
        // Per-job (album/aggregate) header bar.
        private readonly ConcurrentDictionary<Job, ProgressBar?> _jobBars = new();

        // Album blocks: job → all song bars for that album's current folder.
        private readonly ConcurrentDictionary<AlbumJob, AlbumBlock> _albumBlocks = new();

        // Album blocks: job → all song bars for that album's current folder.
        private readonly ConcurrentDictionary<AlbumJob, AlbumBlock> _albumBlocks = new();

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

        sealed class AlbumBlock
        {
            public List<SongJob> Songs = new();
        }

        sealed class AlbumBlock
        {
            public List<SongJob> Songs = new();
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

                    // Advance spinner on all in-progress song bars.
                    // Advance spinner on all in-progress song bars.
                    foreach (var (_, d) in _bars)
                    {
                        if (d.StateLabel != "InProgress" || d.Bar == null) continue;
                        d.SpinIndex++;
                        try { d.Bar.Refresh(d.Pct, BuildText(d)); } catch { }
                    }

                    // Refresh album header bars with current done/total count.
                    foreach (var (job, block) in _albumBlocks)
                    {
                        if (!_jobBars.TryGetValue(job, out var headerBar) || headerBar == null) continue;
                        int done  = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                        int total = block.Songs.Count;
                        try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job, done, total)); } catch { }
                    }

                    // Refresh album header bars with current done/total count.
                    foreach (var (job, block) in _albumBlocks)
                    {
                        if (!_jobBars.TryGetValue(job, out var headerBar) || headerBar == null) continue;
                        int done  = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                        int total = block.Songs.Count;
                        try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job, done, total)); } catch { }
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
            else
            {
                prefix = "  ";
            }

            string label = (d.StateLabel + ":").PadRight(12);
            return $"{prefix}{label} {d.BaseText}";
        }

        static string AlbumHeaderText(AlbumJob job, int done, int total)
            => $"{job.ToString(true)}  [{done}/{total}]";

        static string AlbumHeaderText(AlbumJob job, int done, int total)
            => $"{job.ToString(true)}  [{done}/{total}]";


        // ── structured events — no-ops ───────────────────────────────────────

        public void ReportTrackList(IEnumerable<SongJob> songs, int listIndex = 0) { }
        public void ReportSearchStart(SongJob song) { }
        public void ReportSearchResult(SongJob song, int resultCount, FileCandidate? chosen = null) { }
        public void ReportOverallProgress(int downloaded, int failed, int total) { }
        public void ReportListProgress(JobList list, int downloaded, int failed, int total) { }


        // ── structured events — with rendering ──────────────────────────────

        public void ReportDownloadStart(SongJob song, FileCandidate candidate)
        {
            // For album files the bar is pre-allocated by ReportAlbumDownloadStarted;
            // for standalone songs it may not exist yet (no search phase).
            // For album files the bar is pre-allocated by ReportAlbumDownloadStarted;
            // for standalone songs it may not exist yet (no search phase).
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

        public void ReportStateChanged(SongJob song, FileCandidate? chosen = null)
        {
            if (_bars.TryGetValue(song, out var d) && d.Bar != null)
            {
                bool succeeded = chosen != null || song.State == JobState.Done;
                d.StateLabel = succeeded ? "Succeeded" : "Failed";
                if (succeeded) d.Pct = 100;
                Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
            }
            _bars.TryRemove(song, out _);
            _savedState.TryRemove(song, out _);
        }


        // ── display events ───────────────────────────────────────────────────

        public void ReportExtractionStarted(ExtractJob job) { }
        public void ReportExtractionCompleted(ExtractJob job, Job result) { }

        public void ReportJobStarted(Job job)
        public void ReportJobStarted(Job job)
        {
            var bar = Printing.GetProgressBar(_config);
            var bar = Printing.GetProgressBar(_config);
            _jobBars[job] = bar;
            Printing.RefreshOrPrint(bar, 0, $"{job.GetType().Name}: {job.ToString(true)}, searching..", print: true);
        }

        public void ReportAlbumDownloadStarted(AlbumJob job, AlbumFolder folder)
        {
            if (Console.IsOutputRedirected || _config.noProgress)
            {
                // No progress bars — just print the album info as before.
                Console.WriteLine();
                Printing.PrintAlbum(folder);
                return;
            }

            int total = folder.Files.Count;

            string ancestor = Utils.GreatestCommonDirectorySlsk(folder.Files
                .Where(f => f.ResolvedTarget != null)
                .Select(f => f.ResolvedTarget!.Filename));

            lock (Printing.ConsoleLock)
            {
                if (_jobBars.TryGetValue(job, out var headerBar) && headerBar != null)
                    try { headerBar.Refresh(0, AlbumHeaderText(job, 0, total)); } catch { }

                Printing.PrintAlbumHeader(folder);

                // Pre-allocate one progress bar per file, atomically reserving N lines.
                var block = new AlbumBlock { Songs = folder.Files.ToList() };

                foreach (var song in block.Songs)
                {
                    string filename  = song.ResolvedTarget?.Filename ?? song.Query.ToString();
                    string shortName = ancestor.Length > 0
                        ? filename.Replace(ancestor, "").TrimStart('\\')
                        : System.IO.Path.GetFileName(filename);
                    string baseText = song.ResolvedTarget != null
                        ? Printing.DisplayString(song.Query, song.ResolvedTarget.File, song.ResolvedTarget.Response, customPath: shortName, showUser: false)
                        : shortName;

                    var bar = new ProgressBar(PbStyle.SingleLine, 100, Console.WindowWidth - 10, character: ' ');
                    var d   = new BarData { Bar = bar, BaseText = baseText, StateLabel = "Pending", Pct = 0 };
                    _bars[song] = d;
                    try { bar.Refresh(0, BuildText(d)); } catch { }
                }

                _albumBlocks[job] = block;
            }
        }

        public void ReportAlbumDownloadCompleted(AlbumJob job)
        {
            if (_albumBlocks.TryGetValue(job, out var block))
            {
                int total = block.Songs.Count;
                int done  = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                if (_jobBars.TryGetValue(job, out var headerBar) && headerBar != null)
                    try { headerBar.Refresh(100, AlbumHeaderText(job, done, total)); } catch { }
                _albumBlocks.TryRemove(job, out _);
            }
            _jobBars.TryRemove(job, out _);
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
                Printing.RefreshOrPrint(bar, 0, $"No results: {job.ToString(true)}{lockedMsg}", print: true);
                _jobBars.TryRemove(job, out _);
                if (job is AlbumJob aj) _albumBlocks.TryRemove(aj, out _);
                Printing.RefreshOrPrint(bar, 0, $"No results: {job.ToString(true)}{lockedMsg}", print: true);
                _jobBars.TryRemove(job, out _);
                if (job is AlbumJob aj) _albumBlocks.TryRemove(aj, out _);
            }
            // If found and it's an AlbumJob, leave the header bar in _jobBars so
            // ReportAlbumDownloadStarted can update it. It will be removed there.
            else if (job is not AlbumJob)
            // If found and it's an AlbumJob, leave the header bar in _jobBars so
            // ReportAlbumDownloadStarted can update it. It will be removed there.
            else if (job is not AlbumJob)
            {
                if (bar != null)
                    Printing.RefreshOrPrint(bar, 0, $"Found results: {job.ToString(true)}", print: true);
                _jobBars.TryRemove(job, out _);
                if (bar != null)
                    Printing.RefreshOrPrint(bar, 0, $"Found results: {job.ToString(true)}", print: true);
                _jobBars.TryRemove(job, out _);
            }
        }

        public void ReportSongSearching(SongJob song)
        {
            // If this song already has a pre-allocated bar (album block), update it in-place.
            if (_bars.TryGetValue(song, out var existing))
            {
                existing.StateLabel = "Searching";
                existing.BaseText   = song.ToString();
                Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
                return;
            }

            // If this song already has a pre-allocated bar (album block), update it in-place.
            if (_bars.TryGetValue(song, out var existing))
            {
                existing.StateLabel = "Searching";
                existing.BaseText   = song.ToString();
                Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
                return;
            }

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
