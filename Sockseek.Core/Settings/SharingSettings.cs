namespace Sockseek.Core.Settings;

/// <summary>
/// Daemon-lifetime settings for the local public share catalog.
/// </summary>
public sealed class SharingSettings
{
    internal SharingSettings ShallowClone() => (SharingSettings)MemberwiseClone();

    public List<ShareRootSettings> Roots { get; set; } = [];

    public List<string> ExcludedDirectories { get; set; } = [];

    public List<string> Filters { get; set; } = [];

    public bool ScanOnStart { get; set; } = true;

    public TimeSpan? RescanInterval { get; set; }
}

/// <summary>
/// One configured local root and the public alias under which peers see it.
/// </summary>
public sealed class ShareRootSettings
{
    public required string LocalPath { get; set; }

    public string? Alias { get; set; }

    /// <summary>
    /// Populated by settings normalization from <see cref="Alias"/> or the final
    /// local path segment.
    /// </summary>
    public string EffectiveAlias { get; set; } = "";
}
