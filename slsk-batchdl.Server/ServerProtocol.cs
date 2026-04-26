namespace Sldl.Server;

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
