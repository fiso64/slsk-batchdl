namespace Sockseek.Core.Settings;

/// <summary>
/// Daemon-wide inbound peer deny policy. This is distinct from the download
/// source-selection banned-user setting.
/// </summary>
public sealed class PeerAccessSettings
{
    public List<string> BlockedUsernames { get; set; } = [];

    public List<string> BlockedIpAddresses { get; set; } = [];
}
