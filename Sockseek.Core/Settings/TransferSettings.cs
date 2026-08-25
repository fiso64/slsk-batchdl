namespace Sockseek.Core.Settings;

/// Controls file transfer behaviour: retries and incomplete-file handling.
public class TransferSettings
{
    internal TransferSettings ShallowClone() => (TransferSettings)MemberwiseClone();

    /// Maximum inactivity window for an established peer transfer.
    public int MaxStaleTime { get; set; } = 30_000;

    /// Maximum number of times to retry downloading before giving up on the item.
    public int MaxDownloadRetries { get; set; } = 10;

    /// Number of extra attempts when an unknown/transient error occurs during download.
    public int UnknownErrorRetries { get; set; } = 2;

    /// When true, the file is written directly to the final path during download
    /// rather than to a temporary ".incomplete" path.
    public bool NoIncompleteExt { get; set; }

    public int AlbumTrackCountMaxRetries { get; set; } = 5;
}
