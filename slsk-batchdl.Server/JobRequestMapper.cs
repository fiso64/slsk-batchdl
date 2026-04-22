using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Server;

public static class JobRequestMapper
{
    public static Job CreateJob(JobSpecDto spec)
    {
        string kind = spec.Kind.Trim().ToLowerInvariant();

        return kind switch
        {
            "extract" => CreateExtractJob(spec),
            "search-track" => new SearchJob(ToSongQuery(spec.SongQuery ?? throw new ArgumentException("songQuery is required for search-track jobs")), spec.IncludeFullResults),
            "search-album" => new SearchJob(ToAlbumQuery(spec.AlbumQuery ?? throw new ArgumentException("albumQuery is required for search-album jobs"))),
            "song" => new SongJob(ToSongQuery(spec.SongQuery ?? throw new ArgumentException("songQuery is required for song jobs"))),
            "album" => new AlbumJob(ToAlbumQuery(spec.AlbumQuery ?? throw new ArgumentException("albumQuery is required for album jobs"))),
            "aggregate" => new AggregateJob(ToSongQuery(spec.SongQuery ?? throw new ArgumentException("songQuery is required for aggregate jobs"))),
            "album-aggregate" => new AlbumAggregateJob(ToAlbumQuery(spec.AlbumQuery ?? throw new ArgumentException("albumQuery is required for album-aggregate jobs"))),
            "job-list" => CreateJobList(spec),
            _ => throw new ArgumentException($"Unsupported job kind '{spec.Kind}'")
        };
    }

    public static SongQuery ToSongQuery(SongQueryDto dto) => new()
    {
        Artist = dto.Artist,
        Title = dto.Title,
        Album = dto.Album,
        URI = dto.Uri,
        Length = dto.Length,
        ArtistMaybeWrong = dto.ArtistMaybeWrong,
        IsDirectLink = dto.IsDirectLink,
    };

    public static AlbumQuery ToAlbumQuery(AlbumQueryDto dto) => new()
    {
        Artist = dto.Artist,
        Album = dto.Album,
        SearchHint = dto.SearchHint,
        URI = dto.Uri,
        ArtistMaybeWrong = dto.ArtistMaybeWrong,
        IsDirectLink = dto.IsDirectLink,
        MinTrackCount = dto.MinTrackCount,
        MaxTrackCount = dto.MaxTrackCount,
    };

    private static ExtractJob CreateExtractJob(JobSpecDto spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Input))
            throw new ArgumentException("input is required for extract jobs");

        InputType? inputType = null;
        if (!string.IsNullOrWhiteSpace(spec.InputType))
        {
            if (!Enum.TryParse<InputType>(spec.InputType, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unsupported inputType '{spec.InputType}'");
            inputType = parsed;
        }

        return new ExtractJob(spec.Input, inputType);
    }

    private static JobList CreateJobList(JobSpecDto spec)
    {
        if (spec.Jobs == null || spec.Jobs.Count == 0)
            throw new ArgumentException("job-list must contain at least one child job");

        return new JobList(spec.Name, spec.Jobs.Select(CreateJob));
    }
}
