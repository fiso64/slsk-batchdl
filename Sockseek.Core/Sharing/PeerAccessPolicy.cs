using System.Net;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Sharing;

public static class PeerUsername
{
    /// <summary>
    /// Validates a Soulseek username while retaining the exact spelling supplied
    /// by the protocol or caller. Soulseek.NET receives this same value.
    /// </summary>
    public static string Validate(string username)
        => PeerIdentityValidator.ValidateUsername(username);
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
            .Select(PeerUsername.Validate)
            .ToHashSet(StringComparer.Ordinal);

        blockedIpAddresses = settings.BlockedIpAddresses
            .Select(ParseIpAddress)
            .ToHashSet();
    }

    public bool HasBlockedUsernames => blockedUsernames.Count > 0;

    public bool HasBlockedIpAddresses => blockedIpAddresses.Count > 0;

    public bool IsUsernameBlocked(string username)
        => blockedUsernames.Contains(PeerUsername.Validate(username));

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
