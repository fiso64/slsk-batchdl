namespace Sockseek.Core.Settings;

/// Controls yt-dlp as a download fallback when a track is not found on Soulseek.
public class YtDlpSettings
{
    internal YtDlpSettings ShallowClone() => (YtDlpSettings)MemberwiseClone();

    /// When true, fall back to yt-dlp for tracks not found on Soulseek.
    public bool UseYtdlp { get; set; }

    /// Command-line arguments passed to yt-dlp. Supports {id} and {savepath-noext} placeholders.
    public string? YtdlpArgument { get; set; }
}
