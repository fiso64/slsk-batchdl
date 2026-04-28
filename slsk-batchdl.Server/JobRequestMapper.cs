using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Server;

public static class JobRequestMapper
{
    public static ExtractJob CreateExtractJob(SubmitExtractJobRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new ArgumentException("input is required for extract jobs");

        InputType? inputType = null;
        if (!string.IsNullOrWhiteSpace(request.InputType))
        {
            if (!Enum.TryParse<InputType>(request.InputType, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unsupported inputType '{request.InputType}'");
            inputType = parsed;
        }

        var job = new ExtractJob(request.Input, inputType);
        if (request.AutoStartExtractedResult.HasValue)
            job.AutoProcessResult = request.AutoStartExtractedResult.Value;

        return job;
    }

    public static SearchJob CreateTrackSearchJob(SubmitTrackSearchJobRequestDto request)
        => new(ToSongQuery(request.SongQuery), request.IncludeFullResults);

    public static SearchJob CreateAlbumSearchJob(SubmitAlbumSearchJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static SongJob CreateSongJob(SubmitSongJobRequestDto request)
        => new(ToSongQuery(request.SongQuery));

    public static AlbumJob CreateAlbumJob(SubmitAlbumJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static AggregateJob CreateAggregateJob(SubmitAggregateJobRequestDto request)
        => new(ToSongQuery(request.SongQuery));

    public static AlbumAggregateJob CreateAlbumAggregateJob(SubmitAlbumAggregateJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static JobList CreateJobList(SubmitJobListRequestDto request)
        => CreateJobList(request.Name, request.Jobs);

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

    private static Job CreateJob(JobListItemDto item)
        => item switch
        {
            ExtractJobListItemDto extract => CreateExtractJob(new SubmitExtractJobRequestDto(
                extract.Input,
                extract.InputType,
                extract.AutoStartExtractedResult)),
            TrackSearchJobListItemDto search => new SearchJob(ToSongQuery(search.SongQuery), search.IncludeFullResults),
            AlbumSearchJobListItemDto search => new SearchJob(ToAlbumQuery(search.AlbumQuery)),
            SongJobListItemDto song => new SongJob(ToSongQuery(song.SongQuery)),
            AlbumJobListItemDto album => new AlbumJob(ToAlbumQuery(album.AlbumQuery)),
            AggregateJobListItemDto aggregate => new AggregateJob(ToSongQuery(aggregate.SongQuery)),
            AlbumAggregateJobListItemDto aggregate => new AlbumAggregateJob(ToAlbumQuery(aggregate.AlbumQuery)),
            JobListJobListItemDto list => CreateJobList(list.Name, list.Jobs),
            _ => throw new ArgumentException($"Unsupported job-list item type '{item.GetType().Name}'")
        };

    private static JobList CreateJobList(string? name, IReadOnlyList<JobListItemDto> jobs)
    {
        if (jobs.Count == 0)
            throw new ArgumentException("job-list must contain at least one child job");

        return new JobList(name, jobs.Select(CreateJob));
    }
}
