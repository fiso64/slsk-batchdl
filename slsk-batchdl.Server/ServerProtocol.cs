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

    public static class JobListItemKinds
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

    public static class PresentationDisplayModes
    {
        public const string Node = "node";
        public const string Embedded = "embedded";
        public const string Replaced = "replaced";
    }

    public static class ResourceActionKinds
    {
        public const string Cancel = "cancel";
    }

    public static class ExtractedResultStartModes
    {
        public const string Normal = "normal";
        public const string AlbumSearch = "album-search";
    }
}
