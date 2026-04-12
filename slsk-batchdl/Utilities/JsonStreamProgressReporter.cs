using System.Text.Json;
using System.Text.Json.Serialization;
using Jobs;
using Models;

namespace Utilities
{
    /// <summary>
    /// Writes NDJSON (newline-delimited JSON) progress events to a TextWriter (typically stdout).
    /// Each line is a JSON object with { type, timestamp, data }.
    /// </summary>
    public class JsonStreamProgressReporter : IProgressReporter
    {
        private readonly TextWriter _writer;
        private readonly object _lock = new();
        private readonly JsonSerializerOptions _jsonOptions;
        private DateTime _lastDownloadProgressReport = DateTime.MinValue;
        private readonly TimeSpan _downloadProgressThrottle = TimeSpan.FromMilliseconds(500);

        public JsonStreamProgressReporter(TextWriter writer)
        {
            _writer = writer;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false,
            };
        }

        public void ReportTrackList(IEnumerable<SongJob> songs, int listIndex = 0)
        {
            var list = songs.ToList();
            var data = new
            {
                listIndex,
                total = list.Count,
                tracks = list.Select((s, i) => new
                {
                    index  = i,
                    artist = s.Query.Artist,
                    title  = s.Query.Title,
                    album  = s.Query.Album,
                    length = s.Query.Length,
                    state  = s.State.ToString(),
                }).ToList(),
            };
            WriteEvent("track_list", data);
        }

        public void ReportSearchStart(SongJob song)
        {
            WriteEvent("search_start", new
            {
                artist = song.Query.Artist,
                title  = song.Query.Title,
                album  = song.Query.Album,
            });
        }

        public void ReportSearchResult(SongJob song, int resultCount, FileCandidate? chosen = null)
        {
            WriteEvent("search_result", new
            {
                artist      = song.Query.Artist,
                title       = song.Query.Title,
                resultCount,
                chosenUser  = chosen?.Username,
                chosenFile  = chosen != null ? new
                {
                    filename   = chosen.File.Filename,
                    size       = chosen.File.Size,
                    bitRate    = chosen.File.BitRate,
                    sampleRate = chosen.File.SampleRate,
                    bitDepth   = chosen.File.BitDepth,
                    length     = chosen.File.Length,
                    extension  = GetExtension(chosen.File.Filename),
                } : null,
            });
        }

        public void ReportDownloadStart(SongJob song, FileCandidate candidate)
        {
            WriteEvent("download_start", new
            {
                artist    = song.Query.Artist,
                title     = song.Query.Title,
                username  = candidate.Username,
                filename  = candidate.Filename,
                size      = candidate.File.Size,
                extension = GetExtension(candidate.Filename),
            });
        }

        public void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes)
        {
            var now = DateTime.UtcNow;
            if (now - _lastDownloadProgressReport < _downloadProgressThrottle)
                return;
            _lastDownloadProgressReport = now;

            WriteEvent("download_progress", new
            {
                artist           = song.Query.Artist,
                title            = song.Query.Title,
                bytesTransferred,
                totalBytes,
                percent = totalBytes > 0 ? Math.Round((double)bytesTransferred / totalBytes * 100, 1) : 0,
            });
        }

        public void ReportStateChanged(SongJob song, FileCandidate? chosen = null)
        {
            WriteEvent("track_state", new
            {
                artist        = song.Query.Artist,
                title         = song.Query.Title,
                state         = song.State.ToString(),
                failureReason = song.FailureReason != Enums.FailureReason.None ? song.FailureReason.ToString() : null,
                downloadPath  = !string.IsNullOrEmpty(song.DownloadPath) ? song.DownloadPath : null,
                username      = chosen?.Username,
                filename      = chosen?.Filename,
                size          = chosen?.File.Size,
                bitRate       = chosen?.File.BitRate,
                extension     = chosen != null ? GetExtension(chosen.Filename) : null,
            });
        }

        public void ReportOverallProgress(int downloaded, int failed, int total)
        {
            WriteEvent("progress", new
            {
                downloaded,
                failed,
                total,
                percent = total > 0 ? Math.Round((double)(downloaded + failed) / total * 100, 1) : 0,
            });
        }

        public void ReportListProgress(JobList list, int downloaded, int failed, int total)
        {
            WriteEvent("list_progress", new { name = list.ItemName, downloaded, failed, total });
        }

        // Display-only events — no-ops for JSON output.
        public void ReportExtractionStarted(ExtractJob job) { }
        public void ReportExtractionCompleted(ExtractJob job, Job result) { }

        public void ReportExtractionFailed(ExtractJob job, string reason)
        {
            WriteEvent("extraction_failed", new
            {
                input  = job.Input,
                reason,
            });
        }
        public void ReportJobStarted(Job job) { }
        public void ReportAlbumDownloadStarted(AlbumJob job, AlbumFolder folder) { }
        public void ReportAlbumDownloadCompleted(AlbumJob job) { }
        public void ReportJobFolderRetrieving(Job job) { }
        public void ReportJobCompleted(Job job, bool found, int lockedFiles) { }
        public void ReportSongSearching(SongJob song) { }
        public void ReportSongNotFound(SongJob song) { }
        public void ReportSongFailed(SongJob song) { }
        public void ReportDownloadStateChanged(SongJob song, string stateLabel) { }
        public void ReportOnCompleteStart(SongJob song) { }
        public void ReportOnCompleteEnd(SongJob song) { }
        public void ReportJobStatus(Job job, string status) { }

        private void WriteEvent(string type, object data)
        {
            var envelope = new
            {
                type,
                timestamp = DateTime.UtcNow.ToString("O"),
                data,
            };

            var json = JsonSerializer.Serialize(envelope, _jsonOptions);

            lock (_lock)
            {
                _writer.WriteLine(json);
                _writer.Flush();
            }
        }

        private static string? GetExtension(string filename)
        {
            var ext = Path.GetExtension(filename);
            return string.IsNullOrEmpty(ext) ? null : ext.TrimStart('.').ToLower();
        }
    }
}
