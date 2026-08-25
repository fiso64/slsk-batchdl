using Sockseek.Core;

namespace Sockseek.Core.Settings;

/// Controls what is extracted and how.
public class ExtractionSettings
{
    internal ExtractionSettings ShallowClone() => (ExtractionSettings)MemberwiseClone();

    public string? Input { get; set; }

    public InputType InputType { get; set; } = InputType.None;

    public int MaxTracks { get; set; } = int.MaxValue;

    public int Offset { get; set; }

    public bool Reverse { get; set; }

    public bool RemoveTracksFromSource { get; set; }

    // Null means the input source decides. Ambiguous string/list inputs default to album mode,
    // while concrete sources such as CSV, Spotify, YouTube, and Soulseek links keep their own shape.
    public ExtractionMode? RequestedMode { get; set; }

    // When true, song-shaped extractor results are converted into album jobs where possible.
    // This is deliberately separate from RequestedMode: --album controls ambiguous string
    // interpretation, while --upgrade-to-album opts into source-result conversion.
    public bool UpgradeToAlbum { get; set; }

    // Convenience bridge for older call sites. New mode decisions should use RequestedMode so
    // the 3.0 default album behavior does not accidentally force concrete sources into albums.
    public bool IsAlbum
    {
        get => RequestedMode == ExtractionMode.Album;
        set => RequestedMode = value ? ExtractionMode.Album : ExtractionMode.Song;
    }

    public bool SetAlbumMinTrackCount { get; set; } = true;

    public bool SetAlbumMaxTrackCount { get; set; }
}
