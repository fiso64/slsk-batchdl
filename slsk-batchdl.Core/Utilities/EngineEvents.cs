using Soulseek;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Core;

/// <summary>
/// Multicast event bus for the download engine. Subscribe to any subset of events;
/// unsubscribed events are no-ops (null-conditional invocation).
///
/// CLI reporters and the future Server/SignalR hub both subscribe here.
/// </summary>
public class EngineEvents
{
    // ── Extraction ───────────────────────────────────────────────────────────
    public event Action<ExtractJob>?         ExtractionStarted;
    public event Action<ExtractJob, Job>?    ExtractionCompleted;
    public event Action<ExtractJob, string>? ExtractionFailed;

    // ── Job-level ────────────────────────────────────────────────────────────
    public event Action<Job>?              JobStarted;
    public event Action<Job, AlbumFolder>? AlbumDownloadStarted;
    public event Action<Job, AlbumFolder>? AlbumTrackDownloadStarted;
    public event Action<Job>?              AlbumDownloadCompleted;
    public event Action<Job>?              JobFolderRetrieving;
    public event Action<Job, bool, int>?   JobCompleted;     // job, found, lockedFileCount
    public event Action<Job, string>?      JobStatus;        // job, short status label

    // ── Song-level ───────────────────────────────────────────────────────────
    public event Action<SongJob>?      SongSearching;
    public event Action<SongJob, int>? SearchCompleted; // song, resultCount
    public event Action<SongJob>?      SongNotFound;
    public event Action<SongJob>? SongFailed;
    public event Action<SongJob>? StateChanged;
    public event Action<SongJob>? OnCompleteStart;
    public event Action<SongJob>? OnCompleteEnd;

    // ── Download ─────────────────────────────────────────────────────────────
    public event Action<SongJob, FileCandidate>?    DownloadStarted;
    public event Action<SongJob, long, long>?       DownloadProgress;      // transferred, total
    public event Action<SongJob, TransferStates>?   DownloadStateChanged;  // raw state, not string

    // ── List / overall ───────────────────────────────────────────────────────
    public event Action<IEnumerable<SongJob>>?    TrackListReady;
    public event Action<JobList, int, int, int>?  ListProgress;    // list, done, failed, total
    public event Action<int, int, int>?           OverallProgress; // done, failed, total

    // ── Internal raise methods (same assembly only) ──────────────────────────
    internal void RaiseExtractionStarted(ExtractJob job)              => ExtractionStarted?.Invoke(job);
    internal void RaiseExtractionCompleted(ExtractJob job, Job result) => ExtractionCompleted?.Invoke(job, result);
    internal void RaiseExtractionFailed(ExtractJob job, string reason) => ExtractionFailed?.Invoke(job, reason);

    internal void RaiseJobStarted(Job job)                            => JobStarted?.Invoke(job);
    internal void RaiseAlbumDownloadStarted(Job job, AlbumFolder f)   => AlbumDownloadStarted?.Invoke(job, f);
    internal void RaiseAlbumTrackDownloadStarted(Job job, AlbumFolder f) => AlbumTrackDownloadStarted?.Invoke(job, f);
    internal void RaiseAlbumDownloadCompleted(Job job)                => AlbumDownloadCompleted?.Invoke(job);
    internal void RaiseJobFolderRetrieving(Job job)                   => JobFolderRetrieving?.Invoke(job);
    internal void RaiseJobCompleted(Job job, bool found, int locked)  => JobCompleted?.Invoke(job, found, locked);
    internal void RaiseJobStatus(Job job, string status)              => JobStatus?.Invoke(job, status);

    internal void RaiseSongSearching(SongJob song)                        => SongSearching?.Invoke(song);
    internal void RaiseSearchCompleted(SongJob song, int resultCount)     => SearchCompleted?.Invoke(song, resultCount);
    internal void RaiseSongNotFound(SongJob song)                     => SongNotFound?.Invoke(song);
    internal void RaiseSongFailed(SongJob song)                       => SongFailed?.Invoke(song);
    internal void RaiseStateChanged(SongJob song)                     => StateChanged?.Invoke(song);
    internal void RaiseOnCompleteStart(SongJob song)                  => OnCompleteStart?.Invoke(song);
    internal void RaiseOnCompleteEnd(SongJob song)                    => OnCompleteEnd?.Invoke(song);

    internal void RaiseDownloadStarted(SongJob song, FileCandidate c)        => DownloadStarted?.Invoke(song, c);
    internal void RaiseDownloadProgress(SongJob song, long xfer, long total) => DownloadProgress?.Invoke(song, xfer, total);
    internal void RaiseDownloadStateChanged(SongJob song, TransferStates s)  => DownloadStateChanged?.Invoke(song, s);

    internal void RaiseTrackListReady(IEnumerable<SongJob> songs)            => TrackListReady?.Invoke(songs);
    internal void RaiseListProgress(JobList list, int dl, int fl, int total) => ListProgress?.Invoke(list, dl, fl, total);
    internal void RaiseOverallProgress(int dl, int fl, int total)            => OverallProgress?.Invoke(dl, fl, total);
}
