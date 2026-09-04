namespace Sockseek.Core.Settings;

/// <summary>
/// Independent inbound restrictions. Upload access controls what a remote peer
/// may search, browse, and download from this daemon. Private-message access
/// controls only incoming direct messages; room messages and outbound actions
/// are deliberately separate.
/// </summary>
public sealed class PeerRestrictionSettings
{
    internal PeerRestrictionSettings ShallowClone() => (PeerRestrictionSettings)MemberwiseClone();

    public UploadAccessSettings UploadAccess { get; set; } = new();

    public PrivateMessageAccessSettings PrivateMessages { get; set; } = new();
}

public sealed class UploadAccessSettings
{
    internal UploadAccessSettings ShallowClone() => (UploadAccessSettings)MemberwiseClone();

    public List<string> BlockedUsernames { get; set; } = [];

    public List<string> BlockedIpAddresses { get; set; } = [];
}

public sealed class PrivateMessageAccessSettings
{
    internal PrivateMessageAccessSettings ShallowClone()
        => (PrivateMessageAccessSettings)MemberwiseClone();

    public List<string> BlockedUsernames { get; set; } = [];
}
