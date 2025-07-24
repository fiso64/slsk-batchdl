using Enums;
using Models;
using Soulseek;
using System.Text.Json;
using System.Text.Json.Serialization;


public class UserInfoJson
{
    public string Username { get; set; }
    public float UploadSpeed { get; set; }
    public bool HasFreeUploadSlot { get; set; }

    public UserInfoJson() { }

    public UserInfoJson(SearchResponse response)
    {
        Username = response.Username;
        UploadSpeed = response.UploadSpeed / (1024f * 1024f);
        HasFreeUploadSlot = response.HasFreeUploadSlot;
    }
}

public class FileInfoJson
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Length { get; set; }
    public string Filename { get; set; }
    public long Size { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Bitrate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? SampleRate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? BitDepth { get; set; }

    public FileInfoJson() { }

    public FileInfoJson(Soulseek.File file)
    {
        Length = file.Length;
        Filename = file.Filename;
        Size = file.Size;
        Bitrate = file.BitRate;
        SampleRate = file.SampleRate;
        BitDepth = file.BitDepth;
    }
}

public class AlbumResultJson
{
    public UserInfoJson User { get; set; }
    public List<FileInfoJson> Files { get; set; } = new();
}

public class TrackResultJson
{
    public UserInfoJson User { get; set; }
    public FileInfoJson File { get; set; }
}

public class TrackJson
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Artist { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Album { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Length { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TrackType Type { get; set; } = TrackType.Normal;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FailureReason? FailureReason { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TrackState State { get; set; } = TrackState.Initial;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    public TrackJson() { }

    public TrackJson(Track track)
    {
        Title = string.IsNullOrEmpty(track.Title) ? null : track.Title;
        Artist = string.IsNullOrEmpty(track.Artist) ? null : track.Artist;
        Album = string.IsNullOrEmpty(track.Album) ? null : track.Album;
        Path = string.IsNullOrEmpty(track.DownloadPath) ? null : track.DownloadPath.Replace('\\', '/');
        Length = track.Length == -1 ? null : (int?)track.Length;
        Type = track.Type;
        FailureReason = track.FailureReason == Enums.FailureReason.None ? null : track.FailureReason;
        State = track.State;
    }
}

public class AggregateTrackJson
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Artist { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Title { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Album { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Length { get; set; }

    public List<TrackResultJson> Results { get; set; } = new();
}

public static class JsonPrinter
{
    private static readonly JsonSerializerOptions _options = new()
    {
        //WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void PrintTrackResultJson(Track track, IEnumerable<(SearchResponse, Soulseek.File)> results, bool printAll = false)
    {
        if (results == null || !results.Any())
        {
            Console.WriteLine("[]");
            return;
        }

        var trackResults = results.Select(x => new TrackResultJson
        {
            User = new UserInfoJson(x.Item1),
            File = new FileInfoJson(x.Item2)
        });

        if (!printAll)
        {
            trackResults = trackResults.Take(1);
        }

        var json = JsonSerializer.Serialize(trackResults, _options);
        Console.WriteLine(json);
    }

    public static void PrintAggregateJson(List<Track> tracks)
    {
        if (tracks.Count == 0)
        {
            Console.WriteLine("[]");
            return;
        }

        var aggregateTracks = tracks.Select(t => new AggregateTrackJson
        {
            Artist = t.Artist,
            Title = t.Title,
            Album = t.Album,
            Length = t.Length,
            Results = t.Downloads?
                .Select(d => new TrackResultJson
                {
                    User = new UserInfoJson(d.Item1),
                    File = new FileInfoJson(d.Item2)
                })
                .ToList() ?? new List<TrackResultJson>()
        }).ToList();

        var json = JsonSerializer.Serialize(aggregateTracks, _options);
        Console.WriteLine(json);
    }

    public static void PrintAlbumJson(List<List<Track>> albumTracksList, Track sourceTrack)
    {
        if (albumTracksList.Count == 0)
        {
            Console.WriteLine("[]");
            return;
        }

        var albumResults = albumTracksList
            .Where(albumTracks => albumTracks.Count > 0)
            .Select(albumTracks => new AlbumResultJson
            {
                User = new UserInfoJson(albumTracks[0].FirstResponse),
                Files = albumTracks.Select(t => new FileInfoJson(t.Downloads[0].Item2)).ToList()
            });

        var json = JsonSerializer.Serialize(albumResults, _options);
        Console.WriteLine(json);
    }

    public static void PrintIndexJson(IEnumerable<Track> tracks)
    {
        var trackJsons = tracks.Select(t => new TrackJson(t));
        var options = new JsonSerializerOptions(_options)
        {
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(trackJsons, options);
        Console.WriteLine(json);
    }
}
