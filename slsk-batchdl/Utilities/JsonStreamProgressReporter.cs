using System.Text.Json;
using System.Text.Json.Serialization;
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

        public void ReportTrackList(List<Track> tracks, int listIndex = 0)
        {
            var data = new
            {
                listIndex,
                total = tracks.Count,
                tracks = tracks.Select((t, i) => new
                {
                    index = i,
                    artist = t.Artist,
                    title = t.Title,
                    album = t.Album,
                    length = t.Length,
                    state = t.State.ToString(),
                }).ToList(),
            };
            WriteEvent("track_list", data);
        }

        public void ReportSearchStart(Track track)
        {
            WriteEvent("search_start", new
            {
                artist = track.Artist,
                title = track.Title,
                album = track.Album,
            });
        }

        public void ReportSearchResult(Track track, int resultCount, string? chosenUser = null, Soulseek.File? chosenFile = null)
        {
            WriteEvent("search_result", new
            {
                artist = track.Artist,
                title = track.Title,
                resultCount,
                chosenUser,
                chosenFile = chosenFile != null ? new
                {
                    filename = chosenFile.Filename,
                    size = chosenFile.Size,
                    bitRate = chosenFile.BitRate,
                    sampleRate = chosenFile.SampleRate,
                    bitDepth = chosenFile.BitDepth,
                    length = chosenFile.Length,
                    extension = GetExtension(chosenFile.Filename),
                } : null,
            });
        }

        public void ReportDownloadStart(Track track, string username, Soulseek.File file)
        {
            WriteEvent("download_start", new
            {
                artist = track.Artist,
                title = track.Title,
                username,
                filename = file.Filename,
                size = file.Size,
                extension = GetExtension(file.Filename),
            });
        }

        public void ReportDownloadProgress(Track track, long bytesTransferred, long totalBytes)
        {
            // Throttle download progress events to avoid flooding
            var now = DateTime.UtcNow;
            if (now - _lastDownloadProgressReport < _downloadProgressThrottle)
                return;
            _lastDownloadProgressReport = now;

            WriteEvent("download_progress", new
            {
                artist = track.Artist,
                title = track.Title,
                bytesTransferred,
                totalBytes,
                percent = totalBytes > 0 ? Math.Round((double)bytesTransferred / totalBytes * 100, 1) : 0,
            });
        }

        public void ReportTrackStateChanged(Track track, string? username = null, Soulseek.File? chosenFile = null)
        {
            WriteEvent("track_state", new
            {
                artist = track.Artist,
                title = track.Title,
                state = track.State.ToString(),
                failureReason = track.FailureReason != Enums.FailureReason.None ? track.FailureReason.ToString() : null,
                downloadPath = !string.IsNullOrEmpty(track.DownloadPath) ? track.DownloadPath : null,
                username,
                filename = chosenFile?.Filename,
                size = chosenFile?.Size,
                bitRate = chosenFile?.BitRate,
                extension = chosenFile != null ? GetExtension(chosenFile.Filename) : null,
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

        public void ReportJobComplete(int downloaded, int failed, int total)
        {
            WriteEvent("job_complete", new
            {
                downloaded,
                failed,
                total,
            });
        }

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
