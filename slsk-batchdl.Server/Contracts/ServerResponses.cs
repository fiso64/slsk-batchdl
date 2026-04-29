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
    SoulseekClientStatusDto SoulseekClient,
    int TotalJobCount,
    int ActiveJobCount,
    int TotalWorkflowCount,
    int ActiveWorkflowCount,
    int RestartCount);

/// <summary>
/// Current Soulseek client connection state.
/// </summary>
/// <param name="State">Combined Soulseek.NET client state string.</param>
/// <param name="Flags">Individual Soulseek.NET state flag names.</param>
/// <param name="IsReady">True when the client is both connected and logged in.</param>
public sealed record SoulseekClientStatusDto(
    string State,
    IReadOnlyList<string> Flags,
    bool IsReady);

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
/// Error response body for rejected API requests.
/// </summary>
public sealed record ApiErrorDto(
    string Error);

/// <summary>
/// Response body returned when cancelling a workflow.
/// </summary>
public sealed record CancelWorkflowResponseDto(
    int Cancelled);

/// <summary>
/// Server-provided recommended presentation for general clients. Canonical execution relationships remain on
/// JobSummaryDto.ParentJobId and JobSummaryDto.ResultJobId.
/// </summary>
/// <param name="Mode">
/// One of ServerProtocol.JobPresentationModes: node, embedded, or replaced.
/// </param>
/// <param name="ParentJobId">
/// Recommended parent for presentation trees. May differ from JobSummaryDto.ParentJobId.
/// </param>
/// <param name="ReplacementJobId">
/// For replaced jobs, the job that should appear instead in presentation views.
/// </param>
public sealed record JobPresentationDto(
    string Mode,
    Guid? ParentJobId,
    int Order,
    Guid? ReplacementJobId);

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
/// <param name="State">Stable job state string. Use ServerProtocol.JobStates constants in .NET clients.</param>
/// <param name="FailureReason">Stable failure reason string when State is failed. Use ServerProtocol.FailureReasons constants in .NET clients.</param>
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
    JobPresentationDto Presentation,
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
