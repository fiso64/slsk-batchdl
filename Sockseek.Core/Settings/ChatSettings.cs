namespace Sockseek.Core.Settings;

/// <summary>Daemon-lifetime chat settings.</summary>
public sealed class ChatSettings
{
    internal ChatSettings ShallowClone() => (ChatSettings)MemberwiseClone();

    /// <summary>Rooms requested after each successful Soulseek login.</summary>
    public List<string> AutoJoinRooms { get; set; } = [];
}
