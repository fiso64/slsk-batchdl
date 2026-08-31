using System.Text.Json.Serialization;

namespace Sockseek.Api;

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
[JsonDerivedType(typeof(RemoteFileJobPayloadDto), ServerProtocol.JobKinds.RemoteFile)]
[JsonDerivedType(typeof(RemoteDirectoryJobPayloadDto), ServerProtocol.JobKinds.RemoteDirectory)]
[JsonDerivedType(typeof(GenericJobPayloadDto), ServerProtocol.JobKinds.Generic)]
public abstract record JobPayloadDto;

public sealed record PeerFileTargetDto(
    string Username,
    string Filename,
    long? Size,
    string? Extension,
    int? BitRate = null,
    int? BitDepth = null,
    int? SampleRate = null,
    int? Length = null,
    IReadOnlyList<FileAttributeDto>? Attributes = null);

public sealed record DirectoryTransferEntryDto(
    PeerFileTargetDto Target,
    IReadOnlyList<string> RelativeDirectoryComponents);

public sealed record DirectoryTransferPlanDto(
    string DisplayRoot,
    IReadOnlyList<DirectoryTransferEntryDto> Entries,
    long TotalKnownBytes);

public sealed record FileDownloadStateDto(
    string? DownloadPath,
    long BytesTransferred,
    long? FileSize,
    double? ProgressPercent);

public sealed record DirectoryDownloadStateDto(
    string Phase,
    int? AttemptNumber,
    string? DownloadPath,
    int FileCount,
    int TerminalFileCount,
    int SuccessfulFileCount,
    int FailedFileCount,
    long BytesTransferred,
    long TotalKnownBytes,
    double? ProgressPercent);

/// <summary>
/// Payload for extract jobs.
/// </summary>
/// <param name="ResultJobId">Semantic job produced and automatically processed by extraction.</param>
public sealed record ExtractJobPayloadDto(
    string Input,
    string? InputType,
    Guid? ResultJobId) : JobPayloadDto;

/// <summary>
/// Payload for search jobs. Use the matching /results endpoint for the actual result items.
/// </summary>
/// <param name="QueryText">Raw text submitted to Soulseek.</param>
/// <param name="DefaultFileProjection">Default file projection used by compatibility file-results endpoints.</param>
/// <param name="DefaultFolderProjection">Default folder projection used by compatibility folder-results endpoints.</param>
/// <param name="Revision">Current result revision for matching SearchResultSnapshotDto views.</param>
public sealed record SearchJobPayloadDto(
    string QueryText,
    FileSearchProjectionRequestDto? DefaultFileProjection,
    FolderSearchProjectionRequestDto? DefaultFolderProjection,
    int ResultCount,
    int Revision,
    bool IsComplete) : JobPayloadDto;

/// <summary>
/// Payload for song jobs, including child song rows owned by album or aggregate jobs.
/// </summary>
/// <param name="JobId">
/// Present when the row corresponds to a registered job and can be addressed directly.
/// </param>
/// <param name="AvailableActions">
/// Actions currently valid for this song.
/// </param>
/// <param name="BytesTransferred">Current downloaded byte count for in-flight downloads.</param>
/// <param name="TotalBytes">Expected total byte count for the selected file, when known.</param>
/// <param name="ProgressPercent">Download progress from 0 to 100, when TotalBytes is known.</param>
public sealed record SongJobPayloadDto(
    SongQueryDto Query,
    int? CandidateCount,
    FileDownloadStateDto File,
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
    ServerJobLifecycleState? LifecycleState = null,
    ServerJobActivityPhase? ActivityPhase = null,
    DateTimeOffset? ActivityUntilUtc = null,
    ServerJobTerminalOutcome? TerminalOutcome = null,
    ServerJobSkipReason? SkipReason = null,
    ServerJobFailureReason? FailureReason = null,
    string? FailureMessage = null,
    IReadOnlyList<ResourceActionDto>? AvailableActions = null,
    string? TransferState = null,
    ServerJobCancellationSource CancellationSource = ServerJobCancellationSource.None,
    ServerSongDownloadSource DownloadSource = ServerSongDownloadSource.None,
    PeerFileTargetDto? ExactTarget = null) : JobPayloadDto;

/// <summary>
/// Payload for album search/download jobs.
/// </summary>
/// <param name="ResolvedFolderUsername">
/// Username of the folder selected/downloaded by the album job, when known.
/// </param>
/// <param name="ResolvedFolderPath">
/// Folder path selected/downloaded by the album job, when known.
/// </param>
public sealed record AlbumJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    DirectoryDownloadStateDto Directory,
    string? ResolvedFolderUsername,
    string? ResolvedFolderPath) : JobPayloadDto;

public sealed record RemoteFileJobPayloadDto(
    PeerFileTargetDto Target,
    IReadOnlyList<string> OutputPathComponents,
    FileDownloadStateDto File) : JobPayloadDto;

[JsonConverter(typeof(JsonStringEnumConverter<RemoteDirectorySourceKindDto>))]
public enum RemoteDirectorySourceKindDto
{
    PeerDirectory,
    Resolved,
}

public sealed record RemoteDirectoryJobPayloadDto(
    RemoteDirectorySourceKindDto SourceKind,
    string? SourceUsername,
    string? SourceFolderPath,
    DirectoryDownloadStateDto Directory) : JobPayloadDto;

/// <summary>
/// Payload for aggregate track download jobs.
/// </summary>
public sealed record AggregateJobPayloadDto(
    SongQueryDto Query,
    int SongCount,
    int CompletedSongCount,
    int SucceededSongCount,
    int FailedSongCount) : JobPayloadDto;

/// <summary>
/// Payload for album-aggregate jobs, which search for distinct album candidates.
/// </summary>
public sealed record AlbumAggregateJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount) : JobPayloadDto;

/// <summary>
/// Payload for job-list jobs. Direct children are listed through the jobs collection.
/// </summary>
public sealed record JobListPayloadDto(
    int Count,
    int ActiveJobCount,
    int CompletedJobCount,
    int SucceededJobCount,
    int FailedJobCount) : JobPayloadDto;

/// <summary>
/// Payload for full-folder retrieval jobs started from album result views.
/// </summary>
public sealed record RetrieveFolderJobPayloadDto(
    string FolderPath,
    string Username,
    int NewFilesFoundCount,
    ServerFolderRetrievalOutcome RetrievalOutcome,
    bool RetrievalCancelled) : JobPayloadDto;

/// <summary>
/// Fallback payload for job kinds without a specialized DTO.
/// </summary>
public sealed record GenericJobPayloadDto(
    string Text) : JobPayloadDto;
