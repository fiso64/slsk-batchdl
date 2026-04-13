namespace Settings;

/// Controls file transfer behaviour: retries and incomplete-file handling.
public class TransferSettings
{
    /// Maximum number of times to retry downloading a track before giving up.
    public int MaxRetriesPerTrack { get; set; } = 30;

    /// Number of extra attempts when an unknown/transient error occurs during download.
    public int UnknownErrorRetries { get; set; } = 2;

    /// When true, the file is written directly to the final path during download
    /// rather than to a temporary ".incomplete" path.
    public bool NoIncompleteExt { get; set; }

    public int AlbumTrackCountMaxRetries { get; set; } = 5;
}
