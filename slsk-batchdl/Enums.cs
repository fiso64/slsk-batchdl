
namespace Enums
{
    public enum FailureReason
    {
        None = 0,
        InvalidSearchString = 1,
        OutOfDownloadRetries = 2,
        NoSuitableFileFound = 3,
        AllDownloadsFailed = 4,
    }

    public enum TrackState
    {
        Initial = 0,
        Downloaded = 1,
        Failed = 2,
        AlreadyExists = 3,
        NotFoundLastTime = 4
    }

    public enum SkipMode
    {
        Name = 0,
        NameCond = 1,
        Tag = 2,
        TagCond = 3, 
        // non file-based skip modes are >= 4
        M3u = 4,
        M3uCond = 5,
    }

    public enum InputType
    {
        CSV,
        YouTube,
        Spotify,
        Bandcamp,
        String,
        None,
    }

    public enum TrackType
    {
        Normal = 0,
        Album = 1,
        Aggregate = 2,
        AlbumAggregate = 3,
    }

    public enum M3uOption
    {
        None,
        Index,
        All,
    }

    public enum PrintOption
    {
        None = 0,
        Tracks = 1,
        Results = 2,
        Full = 4,
    }

    public enum AlbumArtOption
    {
        Default,
        Most,
        Largest,
    }

    public enum DisplayMode
    {
        Single,
        Double,
        Simple,
    }

    public enum Verbosity
    {
        Silent,
        Error,
        Warning,
        Normal,
        Verbose
    }
}