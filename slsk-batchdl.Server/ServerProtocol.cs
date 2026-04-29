namespace Sldl.Server;

/// <summary>
/// Stable string values used by the server wire protocol. Keep DTOs string-based for simple JSON
/// clients, but use these constants inside .NET consumers instead of scattering literals.
/// </summary>
public static class ServerProtocol
{
    public static class JobKinds
    {
        public const string Extract = "extract";
        public const string Search = "search";
        public const string Song = "song";
        public const string Album = "album";
        public const string Aggregate = "aggregate";
        public const string AlbumAggregate = "album-aggregate";
        public const string JobList = "job-list";
        public const string RetrieveFolder = "retrieve-folder";
        public const string Generic = "generic";
    }

    public static class JobDraftKinds
    {
        public const string Extract = "extract";
        public const string TrackSearch = "search-track";
        public const string AlbumSearch = "search-album";
        public const string Song = "song";
        public const string Album = "album";
        public const string Aggregate = "aggregate";
        public const string AlbumAggregate = "album-aggregate";
        public const string JobList = "job-list";
    }

    public static class JobPresentationModes
    {
        public const string Node = "node";
        public const string Embedded = "embedded";
        public const string Replaced = "replaced";
    }

    /// <summary>
    /// Stable string values used by JobSummaryDto.State and job/event payload state fields.
    /// </summary>
    public static class JobStates
    {
        public const string Pending = "Pending";
        public const string Done = "Done";
        public const string Failed = "Failed";
        public const string AlreadyExists = "AlreadyExists";
        public const string NotFoundLastTime = "NotFoundLastTime";
        public const string Skipped = "Skipped";
        public const string Searching = "Searching";
        public const string Downloading = "Downloading";
        public const string Extracting = "Extracting";
    }

    /// <summary>
    /// Stable string values used by JobSummaryDto.FailureReason and job/event payload failure reason fields.
    /// </summary>
    public static class FailureReasons
    {
        public const string None = "None";
        public const string InvalidSearchString = "InvalidSearchString";
        public const string OutOfDownloadRetries = "OutOfDownloadRetries";
        public const string NoSuitableFileFound = "NoSuitableFileFound";
        public const string AllDownloadsFailed = "AllDownloadsFailed";
        public const string Other = "Other";
        public const string ExtractionFailed = "ExtractionFailed";
        public const string Cancelled = "Cancelled";
    }

    public static class ResourceActionKinds
    {
        public const string Cancel = "cancel";
    }

}
