using Microsoft.AspNetCore.SignalR;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Server;

public sealed class ServerEventBroadcaster
{
    private readonly IHubContext<ServerEventHub> hubContext;
    private readonly EngineStateStore stateStore;
    private long nextSequence;

    public ServerEventBroadcaster(EngineStateStore stateStore, EngineSupervisor supervisor, IHubContext<ServerEventHub> hubContext)
    {
        this.stateStore = stateStore;
        this.hubContext = hubContext;
        stateStore.JobUpserted += summary => Publish("job.upserted", summary);
        stateStore.WorkflowUpserted += summary => Publish("workflow.upserted", summary);
        stateStore.SearchUpdated += update => Publish("search.updated", update);
        supervisor.EngineCreated += AttachEngine;
    }

    private void AttachEngine(DownloadEngine engine)
    {
        engine.Events.ExtractionStarted += job => Publish("extraction.started", new ExtractionStartedEventDto(GetSummary(job), job.Input, job.InputType?.ToString()));
        engine.Events.ExtractionFailed += (job, reason) => Publish("extraction.failed", new ExtractionFailedEventDto(GetSummary(job), reason));
        engine.Events.JobStarted += job => Publish("job.started", new JobStartedEventDto(GetSummary(job)));
        engine.Events.JobCompleted += (job, found, locked) => Publish("job.completed", new JobCompletedEventDto(GetSummary(job), found, locked));
        engine.Events.JobStatus += (job, status) => Publish("job.status", new JobStatusEventDto(GetSummary(job), status));
        engine.Events.JobFolderRetrieving += job => Publish("job.folder-retrieving", new JobFolderRetrievingEventDto(GetSummary(job)));
        engine.Events.SongSearching += song => Publish("song.searching", new SongSearchingEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        engine.Events.SongNotFound += song => Publish("song.not-found", new SongNotFoundEventDto(
            song.Id,
            song.DisplayId,
            song.WorkflowId,
            ToSongQueryDto(song.Query),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null));
        engine.Events.SongFailed += song => Publish("song.failed", new SongFailedEventDto(
            song.Id,
            song.DisplayId,
            song.WorkflowId,
            ToSongQueryDto(song.Query),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null));
        engine.Events.DownloadStarted += (song, candidate) => Publish("download.started", new DownloadStartedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query), ToFileCandidateDto(candidate)));
        engine.Events.DownloadProgress += (song, transferred, total) => Publish("download.progress", new DownloadProgressEventDto(song.Id, transferred, total));
        engine.Events.DownloadStateChanged += (song, state) => Publish("download.state-changed", new DownloadStateChangedEventDto(song.Id, state.ToString()));
        engine.Events.StateChanged += song => Publish("song.state-changed", new SongStateChangedEventDto(
            song.Id,
            song.DisplayId,
            song.WorkflowId,
            ToSongQueryDto(song.Query),
            song.State.ToString(),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null,
            song.DownloadPath,
            song.ChosenCandidate != null ? ToFileCandidateDto(song.ChosenCandidate) : null));
        engine.Events.AlbumDownloadStarted += (job, folder) => Publish("album.download-started", new AlbumDownloadStartedEventDto(GetSummary(job), ToAlbumFolderDto(folder, includeFiles: true)));
        engine.Events.AlbumTrackDownloadStarted += (job, folder) => Publish("album.track-download-started", new AlbumTrackDownloadStartedEventDto(GetSummary(job), ToAlbumFolderDto(folder, includeFiles: true)));
        engine.Events.AlbumDownloadCompleted += job => Publish("album.download-completed", new AlbumDownloadCompletedEventDto(GetSummary(job)));
        engine.Events.OnCompleteStart += song => Publish("on-complete.started", new OnCompleteStartedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        engine.Events.OnCompleteEnd += song => Publish("on-complete.ended", new OnCompleteEndedEventDto(song.Id, song.DisplayId, song.WorkflowId, ToSongQueryDto(song.Query)));
        engine.Events.TrackBatchResolved += (job, pending, existing, notFound) => Publish("track-batch.resolved", new TrackBatchResolvedEventDto(
            GetSummary(job),
            job is JobList,
            job.Config.PrintOption,
            pending.Select(ToSongJobPayloadDto).ToList(),
            existing.Select(ToSongJobPayloadDto).ToList(),
            notFound.Select(ToSongJobPayloadDto).ToList()));
    }

    private void Publish(string type, object payload)
    {
        var envelope = new ServerEventEnvelopeDto(
            Interlocked.Increment(ref nextSequence),
            type,
            DateTimeOffset.UtcNow,
            payload);

        _ = hubContext.Clients.All.SendAsync("serverEvent", envelope);
    }

    private JobSummaryDto GetSummary(Job job)
        => stateStore.GetJobSummary(job.Id) ?? new JobSummaryDto(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            EngineStateStore.GetJobKind(job),
            job.State.ToString(),
            job.ItemName,
            job.ToString(noInfo: true),
            job.FailureReason != FailureReason.None ? job.FailureReason.ToString() : null,
            job.FailureMessage,
            null,
            null,
            job.Config?.AppliedAutoProfiles?.ToList() ?? [],
            new PresentationHintsDto(false, null, job.DisplayId, null));

    private static SongQueryDto ToSongQueryDto(SongQuery query)
        => new(query.Artist, query.Title, query.Album, query.URI, query.Length, query.ArtistMaybeWrong, query.IsDirectLink);

    private static FileCandidateDto ToFileCandidateDto(FileCandidate candidate)
        => new(
            new FileCandidateRefDto(candidate.Username, candidate.Filename),
            candidate.Username,
            candidate.Filename,
            candidate.File.Size,
            candidate.File.BitRate,
            candidate.File.Length,
            candidate.Response.HasFreeUploadSlot,
            candidate.Response.UploadSpeed,
            candidate.File.Extension,
            candidate.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList());

    private static SongJobPayloadDto ToSongJobPayloadDto(SongJob song)
        => new(
            ToSongQueryDto(song.Query),
            song.Candidates?.Count,
            song.DownloadPath,
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.Response.HasFreeUploadSlot,
            song.ResolvedTarget?.Response.UploadSpeed,
            song.ResolvedTarget?.File.Size,
            song.ResolvedTarget?.File.Extension,
            song.ResolvedTarget?.File.Attributes?.Select(x => new FileAttributeDto(x.Type.ToString(), x.Value)).ToList(),
            song.Id,
            song.DisplayId,
            song.Candidates?.Select(ToFileCandidateDto).ToList(),
            song.State.ToString(),
            song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null,
            song.FailureMessage);

    private static AlbumFolderDto ToAlbumFolderDto(AlbumFolder folder, bool includeFiles)
        => new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            folder.SearchSortedAudioLengths.ToList(),
            folder.SearchRepresentativeAudioFilename,
            folder.HasSearchMetadata,
            includeFiles ? folder.Files.Select(ToSongJobPayloadDto).ToList() : null);
}
