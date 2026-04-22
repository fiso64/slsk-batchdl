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
    string? OutputParentDir = null);

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
    int? Length);

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
    string? ResolvedFilename = null);

public sealed record AlbumJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    string? DownloadPath,
    string? ResolvedFolderUsername,
    string? ResolvedFolderPath);

public sealed record JobListPayloadDto(
    int Count);

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
