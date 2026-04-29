using System.Text.Json.Serialization;

namespace Sldl.Server;

/// <summary>
/// Typed job-specific payload carried by JobDetailDto. Switch on the JSON "kind" discriminator
/// or the concrete DTO type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExtractJobPayloadDto), ServerProtocol.JobKinds.Extract)]
[JsonDerivedType(typeof(SearchJobPayloadDto), ServerProtocol.JobKinds.Search)]
[JsonDerivedType(typeof(SongJobPayloadDto), ServerProtocol.JobKinds.Song)]
[JsonDerivedType(typeof(AlbumJobPayloadDto), ServerProtocol.JobKinds.Album)]
[JsonDerivedType(typeof(AggregateJobPayloadDto), ServerProtocol.JobKinds.Aggregate)]
[JsonDerivedType(typeof(AlbumAggregateJobPayloadDto), ServerProtocol.JobKinds.AlbumAggregate)]
[JsonDerivedType(typeof(JobListPayloadDto), ServerProtocol.JobKinds.JobList)]
[JsonDerivedType(typeof(RetrieveFolderJobPayloadDto), ServerProtocol.JobKinds.RetrieveFolder)]
[JsonDerivedType(typeof(GenericJobPayloadDto), ServerProtocol.JobKinds.Generic)]
public abstract record JobPayloadDto;

/// <summary>
/// Payload for extract jobs.
/// </summary>
/// <param name="ResultJobId">
/// Semantic job produced by extraction. It may be started separately when auto-processing is off.
/// </param>
/// <param name="ResultDraft">
/// Typed draft for the produced job. Submit it through the normal job submission endpoints to
/// continue manually.
/// </param>
public sealed record ExtractJobPayloadDto(
    string Input,
    string? InputType,
    Guid? ResultJobId,
    JobDraftDto? ResultDraft) : JobPayloadDto;

/// <summary>
/// Payload for search jobs. Use the matching /results endpoint for the actual result items.
/// </summary>
/// <param name="Intent">Search intent, such as track or album.</param>
/// <param name="Revision">Current result revision for matching SearchResultSnapshotDto views.</param>
public sealed record SearchJobPayloadDto(
    string Intent,
    SongQueryDto Query,
    AlbumQueryDto? AlbumQuery,
    int ResultCount,
    int Revision,
    bool IsComplete) : JobPayloadDto;

/// <summary>
/// Payload for standalone song jobs and embedded album/aggregate song rows.
/// </summary>
/// <param name="JobId">
/// Present when the row corresponds to a registered job and can be addressed directly.
/// </param>
/// <param name="Candidates">
/// Reserved for compatibility. General job detail payloads do not inline candidates;
/// use /api/jobs/{jobId}/results/files for full result rows.
/// </param>
/// <param name="AvailableActions">
/// Actions currently valid for this embedded or standalone song.
/// </param>
public sealed record SongJobPayloadDto(
    SongQueryDto Query,
    int? CandidateCount,
    string? DownloadPath,
    string? ResolvedUsername = null,
    string? ResolvedFilename = null,
    bool? ResolvedHasFreeUploadSlot = null,
    int? ResolvedUploadSpeed = null,
    long? ResolvedSize = null,
    int? ResolvedSampleRate = null,
    string? ResolvedExtension = null,
    IReadOnlyList<FileAttributeDto>? ResolvedAttributes = null,
    Guid? JobId = null,
    int? DisplayId = null,
    IReadOnlyList<FileCandidateDto>? Candidates = null,
    string? State = null,
    string? FailureReason = null,
    string? FailureMessage = null,
    IReadOnlyList<ResourceActionDto>? AvailableActions = null) : JobPayloadDto;

/// <summary>
/// Payload for album search/download jobs.
/// </summary>
/// <param name="ResolvedFolderUsername">
/// Username of the folder selected/downloaded by the album job, when known.
/// </param>
/// <param name="ResolvedFolderPath">
/// Folder path selected/downloaded by the album job, when known.
/// </param>
/// <param name="Results">
/// Reserved for compatibility. General job detail payloads do not inline album results;
/// use /api/jobs/{jobId}/results/folders for full folder rows.
/// </param>
/// <param name="Tracks">
/// Reserved for compatibility. General job detail payloads do not inline child tracks;
/// use JobDetailDto.Children or /api/jobs with the parent id to inspect child jobs.
/// </param>
public sealed record AlbumJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    string? DownloadPath,
    string? ResolvedFolderUsername,
    string? ResolvedFolderPath,
    IReadOnlyList<AlbumFolderDto>? Results = null,
    IReadOnlyList<SongJobPayloadDto>? Tracks = null) : JobPayloadDto;

/// <summary>
/// Payload for aggregate track download jobs.
/// </summary>
public sealed record AggregateJobPayloadDto(
    SongQueryDto Query,
    int SongCount,
    IReadOnlyList<SongJobPayloadDto>? Songs = null) : JobPayloadDto;

/// <summary>
/// Payload for album-aggregate jobs, which search for distinct album candidates.
/// </summary>
public sealed record AlbumAggregateJobPayloadDto(
    AlbumQueryDto Query) : JobPayloadDto;

/// <summary>
/// Payload for job-list jobs. DirectSongs is reserved for compatibility; use
/// JobDetailDto.Children or /api/jobs with the parent id to inspect direct children.
/// </summary>
public sealed record JobListPayloadDto(
    int Count,
    IReadOnlyList<SongJobPayloadDto>? DirectSongs = null) : JobPayloadDto;

/// <summary>
/// Payload for full-folder retrieval jobs started from album result views.
/// </summary>
public sealed record RetrieveFolderJobPayloadDto(
    string FolderPath,
    string Username,
    int NewFilesFoundCount) : JobPayloadDto;

/// <summary>
/// Fallback payload for job kinds without a specialized DTO.
/// </summary>
public sealed record GenericJobPayloadDto(
    string Text) : JobPayloadDto;
