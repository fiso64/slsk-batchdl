namespace Sldl.Server;

/// <summary>
/// Stable string values used by the server wire protocol. Keep DTOs string-based for simple JSON
/// clients, but use these constants inside .NET consumers instead of scattering literals.
/// </summary>
public static class ServerProtocol
{
    /// <summary>
    /// Runtime job kinds returned by job and workflow endpoints.
    /// </summary>
    public static class JobKinds
    {
        /// <summary>Input extraction job.</summary>
        public const string Extract = "extract";
        /// <summary>Search job, including track and album searches.</summary>
        public const string Search = "search";
        /// <summary>Single-file download job.</summary>
        public const string Song = "song";
        /// <summary>Folder or album download job.</summary>
        public const string Album = "album";
        /// <summary>Aggregate track search job.</summary>
        public const string Aggregate = "aggregate";
        /// <summary>Aggregate album search job.</summary>
        public const string AlbumAggregate = "album-aggregate";
        /// <summary>Container job that owns child jobs.</summary>
        public const string JobList = "job-list";
        /// <summary>Folder-browse job used to fully load a search result folder.</summary>
        public const string RetrieveFolder = "retrieve-folder";
        /// <summary>Fallback kind for unknown or unmapped core jobs.</summary>
        public const string Generic = "generic";
    }

    /// <summary>
    /// Submission-only discriminators used by job-list draft payloads.
    /// Some draft kinds map to the same runtime kind.
    /// </summary>
    public static class JobDraftKinds
    {
        /// <summary>Draft for <see cref="SubmitExtractJobRequestDto"/>.</summary>
        public const string Extract = "extract";
        /// <summary>Draft for a track-oriented search job.</summary>
        public const string TrackSearch = "search-track";
        /// <summary>Draft for an album-oriented search job.</summary>
        public const string AlbumSearch = "search-album";
        /// <summary>Draft for a single-file download job.</summary>
        public const string Song = "song";
        /// <summary>Draft for a folder or album download job.</summary>
        public const string Album = "album";
        /// <summary>Draft for an aggregate track search job.</summary>
        public const string Aggregate = "aggregate";
        /// <summary>Draft for an aggregate album search job.</summary>
        public const string AlbumAggregate = "album-aggregate";
        /// <summary>Draft for a nested job list.</summary>
        public const string JobList = "job-list";
    }

    /// <summary>
    /// Stable string values used by JobSummaryDto.State and job/event payload state fields.
    /// </summary>
    public static class JobStates
    {
        /// <summary>Job has not started running yet.</summary>
        public const string Pending = "Pending";
        /// <summary>Job completed successfully.</summary>
        public const string Done = "Done";
        /// <summary>Job completed unsuccessfully.</summary>
        public const string Failed = "Failed";
        /// <summary>Job was skipped because the target already exists.</summary>
        public const string AlreadyExists = "AlreadyExists";
        /// <summary>Job was skipped because the item was previously marked not found.</summary>
        public const string NotFoundLastTime = "NotFoundLastTime";
        /// <summary>Job was skipped by user/config decision.</summary>
        public const string Skipped = "Skipped";
        /// <summary>Job is searching Soulseek results.</summary>
        public const string Searching = "Searching";
        /// <summary>Job is downloading.</summary>
        public const string Downloading = "Downloading";
        /// <summary>Job is extracting input into follow-up jobs.</summary>
        public const string Extracting = "Extracting";
    }

    /// <summary>
    /// Stable string values used by JobSummaryDto.FailureReason and job/event payload failure reason fields.
    /// </summary>
    public static class FailureReasons
    {
        /// <summary>No failure reason.</summary>
        public const string None = "None";
        /// <summary>The job could not form a valid search string.</summary>
        public const string InvalidSearchString = "InvalidSearchString";
        /// <summary>The job exhausted download retry attempts.</summary>
        public const string OutOfDownloadRetries = "OutOfDownloadRetries";
        /// <summary>No acceptable candidate file or folder was found.</summary>
        public const string NoSuitableFileFound = "NoSuitableFileFound";
        /// <summary>All attempted downloads failed.</summary>
        public const string AllDownloadsFailed = "AllDownloadsFailed";
        /// <summary>Failure did not match a more specific reason.</summary>
        public const string Other = "Other";
        /// <summary>Input extraction failed.</summary>
        public const string ExtractionFailed = "ExtractionFailed";
        /// <summary>Job was cancelled.</summary>
        public const string Cancelled = "Cancelled";
    }

    /// <summary>
    /// Action identifiers used by <see cref="ResourceActionDto"/>.
    /// </summary>
    public static class ResourceActionKinds
    {
        /// <summary>Resource can be cancelled through the supplied HTTP method and URL.</summary>
        public const string Cancel = "cancel";
    }
}
