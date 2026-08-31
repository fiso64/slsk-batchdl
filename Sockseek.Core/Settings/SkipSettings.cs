using Sockseek.Core;

namespace Sockseek.Core.Settings;

/// Controls when and how already-downloaded tracks are skipped.
public class SkipSettings
{
    internal SkipSettings ShallowClone() => (SkipSettings)MemberwiseClone();

    public bool SkipExisting { get; set; } = true;

    public bool SkipNotFound { get; set; }

    public SkipMode SkipMode { get; set; } = SkipMode.Index;

    public string? SkipMusicDir { get; set; }

    public SkipMode SkipModeMusicDir { get; set; } = SkipMode.Name;

    public bool SkipCheckCond { get; set; }

    public bool SkipCheckPrefCond { get; set; }
}
