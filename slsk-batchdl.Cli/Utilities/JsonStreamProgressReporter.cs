using System.Text.Json;
using System.Text.Json.Serialization;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Cli;

/// <summary>
/// Writes NDJSON (newline-delimited JSON) progress events to a TextWriter (typically stdout).
/// Each line is a JSON object with { type, timestamp, data }.
/// </summary>
public class JsonStreamProgressReporter
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

    public void Attach(EngineEvents events)
    {
        events.TrackListReady     += songs => ReportTrackList(songs);
        events.SongSearching      += ReportSearchStart;
        events.SearchCompleted    += (song, count) => ReportSearchResult(song, count);
        events.DownloadStarted    += ReportDownloadStart;
        events.DownloadProgress   += ReportDownloadProgress;
        events.StateChanged       += ReportStateChanged;
        events.OverallProgress    += ReportOverallProgress;
        events.ListProgress       += ReportListProgress;
        events.ExtractionFailed   += ReportExtractionFailed;
    }

    private void ReportTrackList(IEnumerable<SongJob> songs)
    {
        var list = songs.ToList();
        var data = new
        {
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

    private void ReportSearchStart(SongJob song)
    {
        WriteEvent("search_start", new
        {
            artist = song.Query.Artist,
            title  = song.Query.Title,
            album  = song.Query.Album,
        });
    }

    private void ReportSearchResult(SongJob song, int resultCount)
    {
        WriteEvent("search_result", new
        {
            artist      = song.Query.Artist,
            title       = song.Query.Title,
            resultCount,
        });
    }

    private void ReportDownloadStart(SongJob song, FileCandidate candidate)
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

    private void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes)
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

    private void ReportStateChanged(SongJob song)
    {
        var chosen = song.ChosenCandidate;
        WriteEvent("track_state", new
        {
            artist        = song.Query.Artist,
            title         = song.Query.Title,
            state         = song.State.ToString(),
            failureReason = song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null,
            downloadPath  = !string.IsNullOrEmpty(song.DownloadPath) ? song.DownloadPath : null,
            username      = chosen?.Username,
            filename      = chosen?.Filename,
            size          = chosen?.File.Size,
            bitRate       = chosen?.File.BitRate,
            extension     = chosen != null ? GetExtension(chosen.Filename) : null,
        });
    }

    private void ReportOverallProgress(int downloaded, int failed, int total)
    {
        WriteEvent("progress", new
        {
            downloaded,
            failed,
            total,
            percent = total > 0 ? Math.Round((double)(downloaded + failed) / total * 100, 1) : 0,
        });
    }

    private void ReportListProgress(JobList list, int downloaded, int failed, int total)
    {
        WriteEvent("list_progress", new { name = list.ItemName, downloaded, failed, total });
    }

    private void ReportExtractionFailed(ExtractJob job, string reason)
    {
        WriteEvent("extraction_failed", new
        {
            input  = job.Input,
            reason,
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
