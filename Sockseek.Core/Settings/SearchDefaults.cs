namespace Sockseek.Core.Settings;

public static class SearchDefaults
{
    public static IReadOnlyList<string> Formats { get; } = Array.AsReadOnly(
        ["mp3", "flac", "ogg", "m4a", "opus", "wav", "aac", "alac"]);
}
