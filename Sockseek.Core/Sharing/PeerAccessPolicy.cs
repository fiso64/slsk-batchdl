using System.Net;
using System.Text;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Sharing;

public static class PeerUsername
{
    public static string Normalize(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        string normalized = username.Trim().Normalize(NormalizationForm.FormC);

        if (normalized.Length == 0)
            throw new ArgumentException("Input error: Peer username cannot be empty.");
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Input error: Peer username cannot contain control characters.");

        return normalized.ToUpperInvariant();
    }
}

/// <summary>
/// Immutable exact-match inbound peer deny policy.
/// </summary>
public sealed class PeerAccessPolicy
{
    private readonly HashSet<string> blockedUsernames;
    private readonly HashSet<IPAddress> blockedIpAddresses;

    public PeerAccessPolicy(PeerAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        blockedUsernames = settings.BlockedUsernames
            .Select(PeerUsername.Normalize)
            .ToHashSet(StringComparer.Ordinal);

        blockedIpAddresses = settings.BlockedIpAddresses
            .Select(ParseIpAddress)
            .ToHashSet();
    }

    public bool HasBlockedUsernames => blockedUsernames.Count > 0;

    public bool HasBlockedIpAddresses => blockedIpAddresses.Count > 0;

    public bool IsUsernameBlocked(string username)
        => blockedUsernames.Contains(PeerUsername.Normalize(username));

    public bool IsIpAddressBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return blockedIpAddresses.Contains(NormalizeIpAddress(address));
    }

    public bool IsBlocked(string username, IPEndPoint? endpoint)
        => IsUsernameBlocked(username)
           || endpoint is not null && IsIpAddressBlocked(endpoint.Address);

    public static IPAddress ParseIpAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();

        if (value.Contains('/') || !IPAddress.TryParse(value, out var address))
        {
            throw new ArgumentException(
                $"Input error: Peer blocked IP '{value}' must be one exact IPv4 or IPv6 address.");
        }

        return NormalizeIpAddress(address);
    }

    public static IPAddress NormalizeIpAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
