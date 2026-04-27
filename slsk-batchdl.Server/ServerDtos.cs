using System.Text.Json.Serialization;
using Sldl.Core;

namespace Sldl.Server;

/// <summary>
/// Basic daemon identity.
/// </summary>
public sealed record ServerInfoDto(
    string Name,
    string Version,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// Current daemon and engine activity counters.
/// </summary>
public sealed record ServerStatusDto(
    bool IsConnectedAndLoggedIn,
    int TotalJobCount,
    int ActiveJobCount,
    int TotalWorkflowCount,
    int ActiveWorkflowCount,
    int RestartCount);

/// <summary>
/// Request body for submitting a new root job.
/// </summary>
public sealed record SubmitJobRequestDto(
    JobSpecDto Job,
    SubmissionOptionsDto? Options = null);

/// <summary>
/// Server job creation request. Kind selects which query fields are required. Prefer follow-up
/// endpoints when starting work from an existing job result.
/// </summary>
public sealed record JobSpecDto
{
    /// <summary>
    /// Job kind. Use values from ServerProtocol.JobKinds plus search-track/search-album.
    /// </summary>
    public string Kind { get; init; } = "";

    public string? Name { get; init; }

    /// <summary>
    /// Extract input, such as a URL, list path, CSV path, or free-text query.
    /// </summary>
    public string? Input { get; init; }

    /// <summary>
    /// Optional explicit input type for extract jobs; normally omitted for auto-detection.
    /// </summary>
    public string? InputType { get; init; }

    /// <summary>
    /// Overrides whether an extract job should immediately run its produced result.
    /// </summary>
    public bool? AutoStartExtractedResult { get; init; }

    public SongQueryDto? SongQuery { get; init; }
    public AlbumQueryDto? AlbumQuery { get; init; }

    /// <summary>
    /// For track searches, keeps the full result set instead of only projected candidates.
    /// </summary>
    public bool IncludeFullResults { get; init; }

    /// <summary>
    /// Child job specs for job-list submissions.
    /// </summary>
    public IReadOnlyList<JobSpecDto>? Jobs { get; init; }
}

/// <summary>
/// Starts the result produced by a completed extract job. Mode "normal" runs the extracted job
/// as-is; mode "album-search" turns extracted album jobs into selectable album searches.
/// </summary>
public sealed record StartExtractedResultRequestDto(
    string Mode = ServerProtocol.ExtractedResultStartModes.Normal);

/// <summary>
/// Submission-time settings layered over the daemon defaults.
/// </summary>
public sealed record SubmissionOptionsDto(
    Guid? WorkflowId = null,
    string? OutputParentDir = null,
    IReadOnlyList<string>? ProfileNames = null,
    IReadOnlyDictionary<string, bool>? ProfileContext = null,
    DownloadSettingsDeltaDto? DownloadSettings = null);

/// <summary>
/// User-visible summary of a configured profile.
/// </summary>
public sealed record ProfileSummaryDto(
    string Name,
    string? Condition,
    bool IsAutoProfile,
    bool HasEngineSettings,
    bool HasDownloadSettings);

/// <summary>
/// Starts a folder retrieval job for an album result folder.
/// </summary>
public sealed record RetrieveFolderRequestDto(
    AlbumFolderRefDto Folder);

/// <summary>
/// Starts a song download from a selected search result candidate.
/// </summary>
public sealed record StartSongDownloadRequestDto(
    FileCandidateRefDto Candidate,
    string? OutputParentDir = null);

/// <summary>
/// Starts an album download from a selected album result folder.
/// </summary>
/// <param name="AllowBrowseResolvedTarget">
/// When true, the album download may browse the selected folder for additional files.
/// </param>
public sealed record StartAlbumDownloadRequestDto(
    AlbumFolderRefDto Folder,
    bool AllowBrowseResolvedTarget = true,
    string? OutputParentDir = null);

/// <summary>
/// Song query shape used by search, download, and embedded song payloads.
/// </summary>
public sealed record SongQueryDto(
    string Artist,
    string Title,
    string Album,
    string Uri,
    int Length,
    bool ArtistMaybeWrong,
    bool IsDirectLink);

/// <summary>
/// Album query shape used by album search/download jobs.
/// </summary>
public sealed record AlbumQueryDto(
    string Artist,
    string Album,
    string SearchHint,
    string Uri,
    bool ArtistMaybeWrong,
    bool IsDirectLink,
    int MinTrackCount,
    int MaxTrackCount);

/// <summary>
/// Stable identity for a file candidate within a search result.
/// </summary>
public sealed record FileCandidateRefDto(
    string Username,
    string Filename);

/// <summary>
/// Stable identity for an album folder within an album result view.
/// </summary>
public sealed record AlbumFolderRefDto(
    string Username,
    string FolderPath);

/// <summary>
/// Raw search result row, primarily for diagnostics or advanced clients.
/// </summary>
public sealed record SearchRawResultDto(
    long Sequence,
    int Revision,
    string Username,
    string Filename,
    long Size,
    int? BitRate,
    int? Length);

/// <summary>
/// Revisioned search result view. Clients can use search.updated events to decide when to refetch.
/// </summary>
/// <param name="Revision">Monotonic revision for this result view.</param>
/// <param name="IsComplete">True when the underlying search job has finished collecting results.</param>
public sealed record SearchResultSnapshotDto<T>(
    int Revision,
    bool IsComplete,
    IReadOnlyList<T> Items);

/// <summary>
/// Downloadable file candidate shown in track search results.
/// </summary>
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

/// <summary>
/// Album folder candidate shown in album search results.
/// </summary>
/// <param name="Files">
/// Optional file list. Present only when requested with includeFiles=true.
/// </param>
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

/// <summary>
/// Aggregate track candidate produced by aggregate search result views.
/// </summary>
public sealed record AggregateTrackCandidateDto(
    SongQueryDto Query,
    string? ItemName);

/// <summary>
/// Aggregate album candidate produced by album-aggregate search result views.
/// </summary>
public sealed record AggregateAlbumCandidateDto(
    AlbumQueryDto Query,
    string? ItemName);

/// <summary>
/// GUI presentation hints. These do not replace canonical parent/result links on JobSummaryDto.
/// </summary>
/// <param name="DisplayMode">
/// One of ServerProtocol.PresentationDisplayModes: node, embedded, or replaced.
/// </param>
/// <param name="DisplayParentJobId">
/// Visual parent for presentation trees. May differ from ParentJobId.
/// </param>
/// <param name="ReplaceWithJobId">
/// For replaced jobs, the job that should appear instead in presentation views.
/// </param>
public sealed record PresentationHintsDto(
    string DisplayMode,
    Guid? DisplayParentJobId,
    int DisplayOrder,
    Guid? ReplaceWithJobId);

/// <summary>
/// Discoverable mutation affordance. Clients should prefer this over hard-coding job states.
/// </summary>
/// <param name="Kind">Action kind, for example ServerProtocol.ResourceActionKinds.Cancel.</param>
/// <param name="Method">HTTP method to invoke.</param>
/// <param name="Href">Server-relative URL for the action.</param>
public sealed record ResourceActionDto(
    string Kind,
    string Method,
    string Href);

/// <summary>
/// Lightweight job list item. Fetch JobDetailDto for a selected job's typed payload.
/// </summary>
/// <param name="Kind">Stable job kind string. Use ServerProtocol.JobKinds constants in .NET clients.</param>
/// <param name="State">Core job state name as a string.</param>
/// <param name="ParentJobId">Canonical execution parent, not necessarily the presentation parent.</param>
/// <param name="ResultJobId">For extract jobs, the semantic result job produced by extraction.</param>
/// <param name="AvailableActions">Actions currently valid for this job.</param>
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
    PresentationHintsDto Presentation,
    IReadOnlyList<ResourceActionDto> AvailableActions);

/// <summary>
/// Canonical selected-job snapshot: summary, typed payload, and canonical child summaries.
/// </summary>
public sealed record JobDetailDto(
    JobSummaryDto Summary,
    JobPayloadDto? Payload,
    IReadOnlyList<JobSummaryDto> Children);

/// <summary>
/// Workflow list item summarizing related jobs submitted under one workflow id.
/// </summary>
public sealed record WorkflowSummaryDto(
    Guid WorkflowId,
    string Title,
    string State,
    IReadOnlyList<Guid> RootJobIds,
    int ActiveJobCount,
    int FailedJobCount,
    int CompletedJobCount);

/// <summary>
/// Canonical workflow snapshot containing all job summaries in the workflow.
/// </summary>
public sealed record WorkflowDetailDto(
    WorkflowSummaryDto Summary,
    IReadOnlyList<JobSummaryDto> Jobs);

/// <summary>
/// GUI-oriented recursive job tree node. Presentation may omit or replace jobs that still exist
/// in WorkflowDetailDto.
/// </summary>
public sealed record PresentedJobNodeDto(
    JobSummaryDto Summary,
    IReadOnlyList<PresentedJobNodeDto> Children);

/// <summary>
/// GUI-oriented workflow snapshot. Prefer this for navigation trees and job lists in the UI.
/// </summary>
public sealed record PresentedWorkflowDto(
    WorkflowSummaryDto Summary,
    IReadOnlyList<PresentedJobNodeDto> Jobs);

/// <summary>
/// SignalR event envelope. Correct UI state must be reconstructable from HTTP snapshots alone;
/// events are for invalidation, progress, and live activity.
/// </summary>
/// <param name="Category">state, activity, or progress.</param>
/// <param name="SnapshotInvalidation">
/// True when clients should refresh relevant HTTP snapshots.
/// </param>
public sealed record ServerEventEnvelopeDto(
    long Sequence,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string Category,
    bool SnapshotInvalidation,
    object Payload);

/// <summary>
/// Machine-readable event catalog entry. SnapshotInvalidation=true means refresh the relevant
/// HTTP snapshot instead of maintaining state from activity events.
/// </summary>
public sealed record ServerEventDescriptorDto(
    string Type,
    string Category,
    bool SnapshotInvalidation,
    string PayloadDto);

/// <summary>
/// State event emitted when a search result view revision changes.
/// </summary>
public sealed record SearchUpdatedDto(
    Guid JobId,
    int Revision,
    int ResultCount,
    bool IsComplete);

/// <summary>
/// Activity event emitted when extraction starts.
/// </summary>
public sealed record ExtractionStartedEventDto(
    JobSummaryDto Summary,
    string Input,
    string? InputType);

/// <summary>
/// Activity event emitted when extraction fails before producing a result job.
/// </summary>
public sealed record ExtractionFailedEventDto(
    JobSummaryDto Summary,
    string Reason);

/// <summary>
/// Activity event emitted when a job begins engine execution.
/// </summary>
public sealed record JobStartedEventDto(
    JobSummaryDto Summary);

/// <summary>
/// Activity event emitted when job execution completes.
/// </summary>
public sealed record JobCompletedEventDto(
    JobSummaryDto Summary,
    bool Found,
    int LockedFileCount);

/// <summary>
/// Activity event carrying transient human-readable job status text.
/// </summary>
public sealed record JobStatusEventDto(
    JobSummaryDto Summary,
    string Status);

/// <summary>
/// Activity event emitted when a song search begins.
/// </summary>
public sealed record SongSearchingEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

/// <summary>
/// Activity event emitted when no suitable song candidate is found.
/// </summary>
public sealed record SongNotFoundEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    string? FailureReason);

/// <summary>
/// Activity event emitted when a song job fails.
/// </summary>
public sealed record SongFailedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    string? FailureReason);

/// <summary>
/// Activity event emitted when a file transfer starts.
/// </summary>
public sealed record DownloadStartedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    FileCandidateDto Candidate);

/// <summary>
/// Coalesced progress event for an active file transfer.
/// </summary>
public sealed record DownloadProgressEventDto(
    Guid JobId,
    long BytesTransferred,
    long TotalBytes);

/// <summary>
/// Activity event carrying the lower-level transfer state.
/// </summary>
public sealed record DownloadStateChangedEventDto(
    Guid JobId,
    string State);

/// <summary>
/// Activity event emitted when an embedded or standalone song job changes state.
/// </summary>
public sealed record SongStateChangedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query,
    string State,
    string? FailureReason,
    string? DownloadPath,
    FileCandidateDto? ChosenCandidate);

/// <summary>
/// Activity event emitted when an album download begins for a selected folder.
/// </summary>
public sealed record AlbumDownloadStartedEventDto(
    JobSummaryDto Summary,
    AlbumFolderDto Folder);

/// <summary>
/// Activity event emitted when an album starts downloading folder tracks.
/// </summary>
public sealed record AlbumTrackDownloadStartedEventDto(
    JobSummaryDto Summary,
    AlbumFolderDto Folder);

/// <summary>
/// Activity event emitted when album download processing completes.
/// </summary>
public sealed record AlbumDownloadCompletedEventDto(
    JobSummaryDto Summary);

/// <summary>
/// Activity event emitted when a job starts retrieving full folder contents.
/// </summary>
public sealed record JobFolderRetrievingEventDto(
    JobSummaryDto Summary);

/// <summary>
/// Activity event emitted when on-complete work starts for a song.
/// </summary>
public sealed record OnCompleteStartedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

/// <summary>
/// Activity event emitted when on-complete work finishes for a song.
/// </summary>
public sealed record OnCompleteEndedEventDto(
    Guid JobId,
    int DisplayId,
    Guid WorkflowId,
    SongQueryDto Query);

/// <summary>
/// Activity event used by CLI-style track listing output after skip checks.
/// </summary>
public sealed record TrackBatchResolvedEventDto(
    JobSummaryDto Summary,
    bool IsNormal,
    PrintOption PrintOption,
    IReadOnlyList<SongJobPayloadDto> Pending,
    IReadOnlyList<SongJobPayloadDto> Existing,
    IReadOnlyList<SongJobPayloadDto> NotFound);

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
public sealed record ExtractJobPayloadDto(
    string Input,
    string? InputType,
    Guid? ResultJobId) : JobPayloadDto;

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
/// Candidate list when included by the selected snapshot/detail view.
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
/// Soulseek file attribute pair.
/// </summary>
public sealed record FileAttributeDto(
    string Type,
    int Value);

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
/// Album folder candidates or the selected/downloaded folder state, depending on job phase.
/// </param>
public sealed record AlbumJobPayloadDto(
    AlbumQueryDto Query,
    int ResultCount,
    string? DownloadPath,
    string? ResolvedFolderUsername,
    string? ResolvedFolderPath,
    IReadOnlyList<AlbumFolderDto>? Results = null) : JobPayloadDto;

/// <summary>
/// Payload for aggregate track download jobs.
/// </summary>
public sealed record AggregateJobPayloadDto(
    SongQueryDto Query,
    IReadOnlyList<SongJobPayloadDto> Songs) : JobPayloadDto;

/// <summary>
/// Payload for album-aggregate jobs, which search for distinct album candidates.
/// </summary>
public sealed record AlbumAggregateJobPayloadDto(
    AlbumQueryDto Query) : JobPayloadDto;

/// <summary>
/// Payload for job-list jobs. DirectSongs contains only direct song children, not every nested job.
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

/// <summary>
/// Query parameters for listing jobs.
/// </summary>
/// <param name="CanonicalRootsOnly">
/// When true, returns only jobs without a canonical ParentJobId. This is not presentation root filtering.
/// </param>
/// <param name="IncludeNonDefault">
/// When true, includes embedded/replaced/non-default presentation jobs that the default list hides.
/// </param>
public sealed record JobQuery(
    string? State,
    string? Kind,
    Guid? WorkflowId,
    bool CanonicalRootsOnly,
    bool IncludeNonDefault);
