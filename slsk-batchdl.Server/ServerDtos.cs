using Sldl.Core;

namespace Sldl.Server;

public sealed record ServerInfoDto(
    string Name,
    string Version,
    DateTimeOffset StartedAtUtc);

public sealed record ServerStatusDto(
    bool IsConnectedAndLoggedIn,
    int TotalJobCount,
    int ActiveJobCount,
    int TotalWorkflowCount,
    int ActiveWorkflowCount,
    int RestartCount);

public sealed record SubmitJobRequestDto(
    JobSpecDto Job,
    SubmissionOptionsDto? Options = null);

public sealed record JobSpecDto
{
    public string Kind { get; init; } = "";
    public string? Name { get; init; }
    public string? Input { get; init; }
    public string? InputType { get; init; }
    public SongQueryDto? SongQuery { get; init; }
    public AlbumQueryDto? AlbumQuery { get; init; }
    public bool IncludeFullResults { get; init; }
    public IReadOnlyList<JobSpecDto>? Jobs { get; init; }
}

public sealed record SubmissionOptionsDto(
    Guid? WorkflowId = null,
    string? OutputParentDir = null,
    IReadOnlyList<string>? ProfileNames = null,
    IReadOnlyDictionary<string, bool>? ProfileContext = null,
    DownloadSettingsDeltaDto? DownloadSettings = null);

public sealed record ProfileSummaryDto(
    string Name,
    string? Condition,
    bool IsAutoProfile,
    bool HasEngineSettings,
    bool HasDownloadSettings);

public sealed record RetrieveFolderRequestDto(
    AlbumFolderRefDto Folder);

public sealed record StartSongDownloadRequestDto(
    FileCandidateRefDto Candidate,
    string? OutputParentDir = null);

public sealed record StartAlbumDownloadRequestDto(
    AlbumFolderRefDto Folder,
    bool AllowBrowseResolvedTarget = true,
    string? OutputParentDir = null);

public sealed record SongQueryDto(
    string Artist,
    string Title,
    string Album,
    string Uri,
    int Length,
    bool ArtistMaybeWrong,
    bool IsDirectLink);

public sealed record AlbumQueryDto(
    string Artist,
    string Album,
    string SearchHint,
    string Uri,
    bool ArtistMaybeWrong,
    bool IsDirectLink,
    int MinTrackCount,
    int MaxTrackCount);

public sealed record FileCandidateRefDto(
    string Username,
    string Filename);

public sealed record AlbumFolderRefDto(
    string Username,
    string FolderPath);

public sealed record SearchRawResultDto(
    long Sequence,
    int Revision,
    string Username,
    string Filename,
    long Size,
    int? BitRate,
    int? Length);

public sealed record SearchProjectionSnapshotDto<T>(
    int Revision,
    bool IsComplete,
    IReadOnlyList<T> Items);

public sealed record FileCandidateDto(
    FileCandidateRefDto Ref,
    string Username,
    string Filename,
    long Size,
    int? BitRate,
    int? Length,
    bool? HasFreeUploadSlot = null,
    int? UploadSpeed = null,
    string? Extension = null,
    IReadOnlyList<FileAttributeDto>? Attributes = null);

public sealed record AlbumFolderDto(
    AlbumFolderRefDto Ref,
    string Username,
    string FolderPath,
    int SearchFileCount,
    int SearchAudioFileCount,
    IReadOnlyList<int> SearchSortedAudioLengths,
    string? SearchRepresentativeAudioFilename,
    bool HasSearchMetadata,
    IReadOnlyList<SongJobPayloadDto>? Files = null);

public sealed record AggregateTrackCandidateDto(
    SongQueryDto Query,
    string? ItemName);

public sealed record AggregateAlbumCandidateDto(
    AlbumQueryDto Query,
    string? ItemName);

public sealed record PresentationHintsDto(
    bool IsHiddenFromRoot,
    Guid? VisualParentJobId,
    int VisualOrder,
    Guid? ReplaceWithJobId);

public sealed record JobSummaryDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    string Kind,
    string State,
    string? ItemName,
    string? QueryText,
    string? FailureReason,
    string? FailureMessage,
    Guid? ParentJobId,
    Guid? ResultJobId,
    IReadOnlyList<string> AppliedAutoProfiles,
    PresentationHintsDto Presentation);

public sealed record JobDetailDto(
    JobSummaryDto Summary,
    object? Payload,
    IReadOnlyList<JobSummaryDto> Children);

public sealed record WorkflowSummaryDto(
    Guid WorkflowId,
    string Title,
    string State,
    IReadOnlyList<Guid> RootJobIds,
    int ActiveJobCount,
    int FailedJobCount,
    int CompletedJobCount);

public sealed record WorkflowDetailDto(
    WorkflowSummaryDto Summary,
    IReadOnlyList<JobSummaryDto> Jobs);

public sealed record ServerEventEnvelopeDto(
    long Sequence,
    string Type,
    DateTimeOffset OccurredAtUtc,
    object Payload);

public sealed record SearchUpdatedDto(
    Guid JobId,
    int Revision,
    int ResultCount,
    bool IsComplete);

public sealed record ExtractionStartedEventDto(
    JobSummaryDto Summary,
    string Input,
    string? InputType);

public sealed record ExtractionFailedEventDto(
    JobSummaryDto Summary,
    string Reason);

public sealed record JobStartedEventDto(
    JobSummaryDto Summary);

public sealed record JobCompletedEventDto(
    JobSummaryDto Summary,
    bool Found,
    int LockedFileCount);

public sealed record JobStatusEventDto(
    JobSummaryDto Summary,
    string Status);

public sealed record SongSearchingEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

public sealed record SongNotFoundEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    string? FailureReason);

public sealed record SongFailedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    string? FailureReason);

public sealed record DownloadStartedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    FileCandidateDto Candidate);

public sealed record DownloadProgressEventDto(
    Guid JobId,
    long BytesTransferred,
    long TotalBytes);

public sealed record DownloadStateChangedEventDto(
    Guid JobId,
    string State);

public sealed record SongStateChangedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    string State,
    string? FailureReason,
    string? DownloadPath,
    FileCandidateDto? ChosenCandidate);

public sealed record AlbumDownloadStartedEventDto(
    JobSummaryDto Summary,
    AlbumFolderDto Folder);

public sealed record AlbumTrackDownloadStartedEventDto(
    JobSummaryDto Summary,
    AlbumFolderDto Folder);

public sealed record AlbumDownloadCompletedEventDto(
    JobSummaryDto Summary);

public sealed record JobFolderRetrievingEventDto(
    JobSummaryDto Summary);

public sealed record OnCompleteStartedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

public sealed record OnCompleteEndedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

public sealed record TrackBatchResolvedEventDto(
    JobSummaryDto Summary,
    bool IsNormal,
    PrintOption PrintOption,
    IReadOnlyList<SongJobPayloadDto> Pending,
    IReadOnlyList<SongJobPayloadDto> Existing,
    IReadOnlyList<SongJobPayloadDto> NotFound);

public sealed record ExtractJobPayloadDto(
    string Input,
    string? InputType,
    Guid? ResultJobId);

public sealed record SearchJobPayloadDto(
    string Intent,
    SongQueryDto Query,
    AlbumQueryDto? AlbumQuery,
    int ResultCount,
    int Revision,
    bool IsComplete);

public sealed record SongJobPayloadDto(
    SongQueryDto Query,
    int? CandidateCount,
    string? DownloadPath,
    string? ResolvedUsername = null,
    string? ResolvedFilename = null,
    bool? ResolvedHasFreeUploadSlot = null,
    int? ResolvedUploadSpeed = null,
    long? ResolvedSize = null,
    string? ResolvedExtension = null,
    IReadOnlyList<FileAttributeDto>? ResolvedAttributes = null,
    Guid? JobId = null,
    int? DisplayId = null,
    IReadOnlyList<FileCandidateDto>? Candidates = null);

public sealed record FileAttributeDto(
    string Type,
    int Value);

public sealed record AlbumJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    string? DownloadPath,
    string? ResolvedFolderUsername,
    string? ResolvedFolderPath,
    IReadOnlyList<AlbumFolderDto>? Results = null);

public sealed record AggregateJobPayloadDto(
    SongQueryDto Query,
    IReadOnlyList<SongJobPayloadDto> Songs);

public sealed record AlbumAggregateJobPayloadDto(
    AlbumQueryDto Query);

public sealed record JobListPayloadDto(
    int Count,
    IReadOnlyList<SongJobPayloadDto>? DirectSongs = null);

public sealed record RetrieveFolderJobPayloadDto(
    string FolderPath,
    string Username,
    int NewFilesFoundCount);

public sealed record GenericJobPayloadDto(
    string Text);

public sealed record JobQuery(
    string? State,
    string? Kind,
    Guid? WorkflowId,
    bool RootOnly,
    bool IncludeHidden);
