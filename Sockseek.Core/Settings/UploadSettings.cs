namespace Sockseek.Core.Settings;

/// <summary>
/// Daemon-lifetime policy for uploads served to Soulseek peers.
/// </summary>
public sealed class UploadSettings
{
    internal UploadSettings ShallowClone() => (UploadSettings)MemberwiseClone();

    public int Slots { get; set; } = 10;

    /// <summary>
    /// Null means unlimited. A configured value is an aggregate KiB/s limit.
    /// </summary>
    public int? SpeedLimitKiBPerSecond { get; set; }
}
