using Models;
using Soulseek;

namespace Utilities
{
    /// <summary>
    /// Interface for reporting structured progress events from sldl.
    /// Used by the web server to get real-time, machine-readable progress updates.
    /// </summary>
    public interface IProgressReporter
    {
        void ReportTrackList(List<Track> tracks, int listIndex = 0);
        void ReportSearchStart(Track track);
        void ReportSearchResult(Track track, int resultCount, string? chosenUser = null, Soulseek.File? chosenFile = null);
        void ReportDownloadStart(Track track, string username, Soulseek.File file);
        void ReportDownloadProgress(Track track, long bytesTransferred, long totalBytes);
        void ReportTrackStateChanged(Track track, string? username = null, Soulseek.File? chosenFile = null);
        void ReportOverallProgress(int downloaded, int failed, int total);
        void ReportJobComplete(int downloaded, int failed, int total);
    }

    /// <summary>
    /// No-op implementation of IProgressReporter for CLI use (no structured progress output).
    /// </summary>
    public class NullProgressReporter : IProgressReporter
    {
        public static readonly NullProgressReporter Instance = new();
        public void ReportTrackList(List<Track> tracks, int listIndex = 0) { }
        public void ReportSearchStart(Track track) { }
        public void ReportSearchResult(Track track, int resultCount, string? chosenUser = null, Soulseek.File? chosenFile = null) { }
        public void ReportDownloadStart(Track track, string username, Soulseek.File file) { }
        public void ReportDownloadProgress(Track track, long bytesTransferred, long totalBytes) { }
        public void ReportTrackStateChanged(Track track, string? username = null, Soulseek.File? chosenFile = null) { }
        public void ReportOverallProgress(int downloaded, int failed, int total) { }
        public void ReportJobComplete(int downloaded, int failed, int total) { }
    }
}
