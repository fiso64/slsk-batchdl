using Jobs;
using Models;

namespace Utilities
{
    /// <summary>
    /// Interface for reporting progress events from the download engine.
    ///
    /// Two families of methods:
    ///   - "Structured" methods (ReportSearchStart, ReportDownloadStart, …) carry
    ///     machine-readable data; used by JsonStreamProgressReporter.
    ///   - "Display" methods (ReportJobSearching, ReportSongSearching, …) carry
    ///     human-readable text for terminal rendering; used by CliProgressReporter.
    ///
    /// Implementations that don't care about a family leave those methods as no-ops.
    /// </summary>
    public interface IProgressReporter
    {
        // ── structured events (machine-readable) ────────────────────────────
        void ReportTrackList(IEnumerable<SongJob> songs, int listIndex = 0);
        void ReportSearchStart(SongJob song);
        void ReportSearchResult(SongJob song, int resultCount, FileCandidate? chosen = null);
        void ReportDownloadStart(SongJob song, FileCandidate candidate);
        void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes);
        void ReportTrackStateChanged(SongJob song, FileCandidate? chosen = null);
        void ReportOverallProgress(int downloaded, int failed, int total);
        void ReportJobComplete(int downloaded, int failed, int total);

        // ── display events (terminal rendering) ─────────────────────────────
        // Job-level
        void ReportJobStarted(Job job, bool parallel);
        void ReportJobFolderRetrieving(Job job);
        void ReportJobCompleted(Job job, bool found, int lockedFiles);

        // Song-level
        void ReportSongSearching(SongJob song);              // each search attempt (first + retries)
        void ReportSongNotFound(SongJob song);
        void ReportSongFailed(SongJob song);
        void ReportDownloadStateChanged(SongJob song, string stateLabel);  // Soulseek transfer state

        // OnComplete around a song download
        void ReportOnCompleteStart(SongJob song);
        void ReportOnCompleteEnd(SongJob song);
    }

    /// <summary>
    /// No-op implementation — used when neither CLI bars nor JSON output is wanted.
    /// </summary>
    public class NullProgressReporter : IProgressReporter
    {
        public static readonly NullProgressReporter Instance = new();
        public void ReportTrackList(IEnumerable<SongJob> songs, int listIndex = 0) { }
        public void ReportSearchStart(SongJob song) { }
        public void ReportSearchResult(SongJob song, int resultCount, FileCandidate? chosen = null) { }
        public void ReportDownloadStart(SongJob song, FileCandidate candidate) { }
        public void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes) { }
        public void ReportTrackStateChanged(SongJob song, FileCandidate? chosen = null) { }
        public void ReportOverallProgress(int downloaded, int failed, int total) { }
        public void ReportJobComplete(int downloaded, int failed, int total) { }
        public void ReportJobStarted(Job job, bool parallel) { }
        public void ReportJobFolderRetrieving(Job job) { }
        public void ReportJobCompleted(Job job, bool found, int lockedFiles) { }
        public void ReportSongSearching(SongJob song) { }
        public void ReportSongNotFound(SongJob song) { }
        public void ReportSongFailed(SongJob song) { }
        public void ReportDownloadStateChanged(SongJob song, string stateLabel) { }
        public void ReportOnCompleteStart(SongJob song) { }
        public void ReportOnCompleteEnd(SongJob song) { }
    }
}
