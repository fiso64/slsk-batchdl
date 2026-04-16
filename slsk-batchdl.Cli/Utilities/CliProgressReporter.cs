using System.Collections.Concurrent;
using Soulseek;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Konsole;
using ProgressBar = Sldl.Cli.IProgressBar;
using Sldl.Core.Settings;

namespace Sldl.Cli;

public class CliProgressReporter
{
    private readonly CliSettings _cli;

    private readonly ConcurrentDictionary<SongJob, BarData> _bars = new();
    private readonly ConcurrentDictionary<Job, ProgressBar?> _jobBars = new();
    private readonly ConcurrentDictionary<AlbumJob, AlbumBlock> _albumBlocks = new();
    private readonly ConcurrentDictionary<Job, string> _jobStatuses = new();
    private readonly ConcurrentDictionary<Job, int> _jobSpinIndexes = new();
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

    private readonly CancellationTokenSource _tickCts = new();

    public bool IsPaused { get; set; } = false;

    public CliProgressReporter(CliSettings cli)
    {
        _cli = cli;
        _ = TickLoopAsync(_tickCts.Token);
    }

    public void Stop() => _tickCts.Cancel();

    public void Attach(EngineEvents events)
    {
        events.SongSearching          += ReportSongSearching;
        events.DownloadStarted        += ReportDownloadStart;
        events.DownloadProgress       += ReportDownloadProgress;
        events.StateChanged           += song => ReportStateChanged(song);
        events.ExtractionStarted      += ReportExtractionStarted;
        events.ExtractionCompleted    += ReportExtractionCompleted;
        events.ExtractionFailed       += ReportExtractionFailed;
        events.JobStarted             += ReportJobStarted;
        events.AlbumDownloadStarted   += (job, folder) => ReportAlbumDownloadStarted((AlbumJob)job, folder);
        events.AlbumDownloadCompleted += job => ReportAlbumDownloadCompleted((AlbumJob)job);
        events.JobFolderRetrieving    += ReportJobFolderRetrieving;
        events.JobCompleted           += ReportJobCompleted;
        events.SongNotFound           += ReportSongNotFound;
        events.SongFailed             += ReportSongFailed;
        events.DownloadStateChanged   += (song, state) => ReportDownloadStateChanged(song, GetStateLabel(state));
        events.OnCompleteStart        += ReportOnCompleteStart;
        events.OnCompleteEnd          += ReportOnCompleteEnd;
        events.JobStatus              += ReportJobStatus;
    }

    async Task TickLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                if (IsPaused) continue;

                foreach (var (_, d) in _bars)
                {
                    if (d.StateLabel != "InProgress" || d.Bar == null) continue;
                    d.SpinIndex++;
                    try { d.Bar.Refresh(d.Pct, BuildText(d)); } catch { }
                }

                foreach (var (job, block) in _albumBlocks)
                {
                    if (!_jobBars.TryGetValue(job, out var headerBar) || headerBar == null) continue;
                    int done  = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                    int total = block.Songs.Count;
                    _jobStatuses.TryGetValue(job, out var status);
                    try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job, done, total, status)); } catch { }
                }

            }
        }
        catch (OperationCanceledException) { }
    }


    // ── bar text construction ────────────────────────────────────────────

    static string BuildText(BarData d)
    {
        string prefix = d.StateLabel == "InProgress"
            ? $"{SpinFrames[d.SpinIndex % SpinFrames.Length]} "
            : "  ";
        string label = (d.StateLabel + ":").PadRight(12);
        return $"{prefix}{label} {d.BaseText}";
    }

    private string AlbumHeaderText(AlbumJob job, int done, int total, string? status = null)
    {
        string statusStr = !string.IsNullOrEmpty(status) ? $" ({status})" : "";
        return $"[{job.DisplayId}] AlbumJob: {job.ToString(true)}{statusStr}  [{done}/{total}]";
    }

    private static string GetJobTypePrefix(Job job) => job switch
    {
        AlbumJob            => "AlbumJob: ",
        ExtractJob          => "ExtractJob: ",
        SongJob             => "SongJob: ",
        RetrieveFolderJob   => "RetrieveFolderJob: ",
        JobList             => "JobList: ",
        AggregateJob        => "AggregateJob: ",
        _                   => ""
    };

    static string FailureReasonLabel(FailureReason reason) => reason switch
    {
        FailureReason.NoSuitableFileFound  => "No suitable file found",
        FailureReason.InvalidSearchString  => "Invalid search string",
        FailureReason.OutOfDownloadRetries => "Out of download retries",
        FailureReason.AllDownloadsFailed   => "All downloads failed",
        FailureReason.ExtractionFailed     => "Extraction failed",
        FailureReason.Cancelled            => "Cancelled",
        FailureReason.Other                => "Unknown error",
        _                                  => "",
    };

    private static string GetStateLabel(TransferStates s)
    {
        if (s.HasFlag(TransferStates.InProgress))   return "InProgress";
        if (s.HasFlag(TransferStates.Queued))
            return s.HasFlag(TransferStates.Remotely) ? "Queued (R)" :
                   s.HasFlag(TransferStates.Locally)  ? "Queued (L)" : "Queued";
        if (s.HasFlag(TransferStates.Initializing)) return "Initialising";
        return "Requested";
    }


    // ── event handlers ───────────────────────────────────────────────────

    private void ReportDownloadStart(SongJob song, FileCandidate candidate)
    {
        var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.StateLabel = "Queued";
        d.BaseText   = Printing.DisplayString(song.Query, candidate.File, candidate.Response, infoFirst: false);
        d.Pct        = 0;
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: true);
    }

    private void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes)
    {
        if (!_bars.TryGetValue(song, out var d)) return;
        d.Pct = totalBytes > 0 ? (int)(bytesTransferred * 100 / totalBytes) : 0;
    }

    private void ReportStateChanged(SongJob song)
    {
        var chosen = song.ChosenCandidate;
        if (_bars.TryGetValue(song, out var d) && d.Bar != null)
        {
            bool succeeded = chosen != null || song.State == JobState.Done;
            d.StateLabel = succeeded ? "Succeeded" : "Failed";
            if (succeeded)
                d.Pct = 100;
            else
            {
                var reason = FailureReasonLabel(song.FailureReason);
                if (reason.Length > 0) d.BaseText += $" [{reason}]";
            }
            Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
        }
        _bars.TryRemove(song, out _);
        _savedState.TryRemove(song, out _);
    }


    // ── display event handlers ───────────────────────────────────────────

    private void ReportExtractionStarted(ExtractJob job)
    {
        if (job.InputType.HasValue)
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine();
                Logger.Info($"[{job.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}");
            }
        }
    }

    private void ReportExtractionCompleted(ExtractJob job, Job result) { }

    private void ReportExtractionFailed(ExtractJob job, string reason)
    {
        Logger.Error($"[{job.DisplayId}] ExtractJob: Failed: {job.Input}\n  Reason:    {reason}");
        _jobBars.TryRemove(job, out _);
    }

    private void ReportJobStarted(Job job)
    {
        var bar = Printing.GetProgressBar();
        _jobBars[job] = bar;
        string status = job is RetrieveFolderJob ? "retrieving folder" : "searching";
        _jobStatuses[job] = status;
        Printing.RefreshOrPrint(bar, 0, $"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {job.ToString(true)}", print: true);
    }

    private void ReportAlbumDownloadStarted(AlbumJob job, AlbumFolder folder)
    {
        if (Console.IsOutputRedirected || _cli.NoProgress)
        {
            Printing.WriteLine();
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
            {
                _jobStatuses[job] = "downloading";
                try { headerBar.Refresh(0, AlbumHeaderText(job, 0, total, "downloading")); } catch { }
            }

            Printing.PrintAlbumHeader(folder);

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

                var bar = Printing.GetProgressBar();
                var d   = new BarData { Bar = bar, BaseText = baseText, StateLabel = "Pending", Pct = 0 };
                _bars[song] = d;
                if (bar != null)
                    try { bar.Refresh(0, BuildText(d)); } catch { }
            }

            _albumBlocks[job] = block;
        }
    }

    private void ReportAlbumDownloadCompleted(AlbumJob job)
    {
        if (_albumBlocks.TryGetValue(job, out var block))
        {
            int total = block.Songs.Count;
            int done  = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
            if (_jobBars.TryGetValue(job, out var headerBar) && headerBar != null)
            {
                _jobStatuses.TryGetValue(job, out var status);
                try { headerBar.Refresh(100, AlbumHeaderText(job, done, total, status)); } catch { }
            }
            _albumBlocks.TryRemove(job, out _);
        }
        _jobBars.TryRemove(job, out _);
        _jobStatuses.TryRemove(job, out _);
        _jobSpinIndexes.TryRemove(job, out _);

        if (!Console.IsOutputRedirected && !_cli.NoProgress)
        {
            Printing.WriteLine();
        }
    }

    private void ReportJobFolderRetrieving(Job job)
    {
        _jobBars.TryGetValue(job, out var bar);
        Printing.RefreshOrPrint(bar, 0, "Getting all files in folder..", print: true);
    }

    private void ReportJobCompleted(Job job, bool found, int lockedFiles)
    {
        _jobBars.TryGetValue(job, out var bar);
        if (!found)
        {
            string lockedMsg = lockedFiles > 0 ? $" (Found {lockedFiles} locked files)" : "";
            string prefix    = job is RetrieveFolderJob ? "no additional files found" : "no results found";
            _jobStatuses[job] = prefix;
            Printing.RefreshOrPrint(bar, 0, $"[{job.DisplayId}] {GetJobTypePrefix(job)}{prefix}: {job.ToString(true)}{lockedMsg}", print: true);
            _jobBars.TryRemove(job, out _);
            _jobStatuses.TryRemove(job, out _);
            if (job is AlbumJob aj) _albumBlocks.TryRemove(aj, out _);
        }
        // If found and it's an AlbumJob, leave the header bar in _jobBars so
        // ReportAlbumDownloadStarted can update it. Removed by ReportAlbumDownloadCompleted.
        else if (job is not AlbumJob)
        {
            if (bar != null)
            {
                string prefix = job is RetrieveFolderJob ? "found additional files in" : "found results";
                _jobStatuses[job] = prefix;
                Printing.RefreshOrPrint(bar, 0, $"[{job.DisplayId}] {GetJobTypePrefix(job)}{prefix}: {job.ToString(true)}", print: true);
            }
            _jobBars.TryRemove(job, out _);
            _jobStatuses.TryRemove(job, out _);
        }
    }

    private void ReportSongSearching(SongJob song)
    {
        if (_bars.TryGetValue(song, out var existing))
        {
            existing.StateLabel = "Searching";
            existing.BaseText   = $"[{song.DisplayId}] {song.ToString()}";
            Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
            return;
        }

        bool isFirst = !_bars.ContainsKey(song);
        var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.StateLabel = "Searching";
        d.BaseText   = $"[{song.DisplayId}] {song.ToString()}";
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: isFirst);
    }

    private void ReportSongNotFound(SongJob song)
    {
        if (!_bars.TryGetValue(song, out var d)) return;
        d.StateLabel = "Not found";
        var reason = FailureReasonLabel(song.FailureReason);
        if (reason.Length > 0) d.BaseText += $" [{reason}]";
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: true);
    }

    private void ReportSongFailed(SongJob song)
    {
        if (!_bars.TryGetValue(song, out var d)) return;
        d.StateLabel = "Failed";
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: true);
    }

    private void ReportDownloadStateChanged(SongJob song, string stateLabel)
    {
        if (!_bars.TryGetValue(song, out var d)) return;
        d.StateLabel = stateLabel;
        Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
    }

    private void ReportOnCompleteStart(SongJob song)
    {
        if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
        _savedState[song] = (d.Bar.Line1 ?? "", d.Bar.Current);
        Printing.RefreshOrPrint(d.Bar, d.Bar.Current, "  OnComplete:  " + $"{song}");
    }

    private void ReportOnCompleteEnd(SongJob song)
    {
        if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
        if (_savedState.TryGetValue(song, out var saved))
            Printing.RefreshOrPrint(d.Bar, saved.pos, saved.text);
    }

    private void ReportJobStatus(Job job, string status)
    {
        _jobStatuses[job] = status;
        if (_jobBars.TryGetValue(job, out var bar) && bar != null)
        {
            if (job is AlbumJob aj && _albumBlocks.TryGetValue(aj, out var block))
            {
                int done = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                int total = block.Songs.Count;
                try { bar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(aj, done, total, status)); } catch { }
            }
            else
            {
                Printing.RefreshOrPrint(bar, 0, $"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {job.ToString(true)}", print: false);
            }
        }
    }
}
