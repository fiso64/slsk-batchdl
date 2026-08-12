using System.Buffers;
using System.Text;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;

/// <summary>
/// Validates exact outbound peer identity without trimming, case folding, or
/// Unicode normalization.
/// </summary>
public static class PeerIdentityValidator
{
    public static string ValidateUsername(string username)
        => Validate(username, "Peer username");

    public static string ValidateRemotePath(string remotePath)
        => Validate(remotePath, "Remote path");

    private static string Validate(string value, string label)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            throw Invalid($"{label} cannot be empty.");

        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
                throw Invalid($"{label} contains invalid Unicode.");
            if (Rune.IsControl(rune))
                throw Invalid($"{label} cannot contain control characters.");
            remaining = remaining[consumed..];
        }

        return value;
    }

    private static ArgumentException Invalid(string message)
        => new($"Input error: {message}");
}

/// <summary>The exact peer and wire filename used by a Soulseek download.</summary>
public sealed record PeerFileIdentity
{
    public PeerFileIdentity(string username, string filename)
    {
        Username = PeerIdentityValidator.ValidateUsername(username);
        Filename = PeerIdentityValidator.ValidateRemotePath(filename);
    }

    public string Username { get; }
    public string Filename { get; }
}

/// <summary>
/// Sockseek-owned metadata for one exact peer file. Search availability and
/// ranking evidence deliberately live outside this value.
/// </summary>
public sealed record PeerFileTarget
{
    public PeerFileTarget(
        PeerFileIdentity identity,
        long? size,
        string? extension,
        int? bitRate = null,
        int? bitDepth = null,
        int? sampleRate = null,
        int? length = null,
        IReadOnlyList<FileAttributeSnapshot>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Known file size cannot be negative.");

        Identity = identity;
        Size = size;
        Extension = extension;
        BitRate = bitRate;
        BitDepth = bitDepth;
        SampleRate = sampleRate;
        Length = length;
        Attributes = attributes is null
            ? null
            : Array.AsReadOnly(attributes.ToArray());
    }

    public PeerFileIdentity Identity { get; }
    public long? Size { get; }
    public string? Extension { get; }
    public int? BitRate { get; }
    public int? BitDepth { get; }
    public int? SampleRate { get; }
    public int? Length { get; }
    public IReadOnlyList<FileAttributeSnapshot>? Attributes { get; }

    public string Username => Identity.Username;
    public string Filename => Identity.Filename;
}

/// <summary>Response-time peer facts used by search presentation and ranking.</summary>
public sealed record SearchPeerSnapshot
{
    public SearchPeerSnapshot(
        string username,
        int responseFileCount,
        int? uploadSpeed,
        bool? hasFreeUploadSlot)
    {
        if (responseFileCount < 0)
            throw new ArgumentOutOfRangeException(nameof(responseFileCount));

        Username = PeerIdentityValidator.ValidateUsername(username);
        ResponseFileCount = responseFileCount;
        UploadSpeed = uploadSpeed;
        HasFreeUploadSlot = hasFreeUploadSlot;
    }

    public string Username { get; }
    public int ResponseFileCount { get; }
    public int? UploadSpeed { get; }
    public bool? HasFreeUploadSlot { get; }
}

/// <summary>Search ordering/revision evidence that is not a remote-file fact.</summary>
public sealed record FileSearchEvidence(
    long Sequence,
    int Revision,
    DateTimeOffset ObservedAtUtc)
{
    public static FileSearchEvidence Unspecified { get; } = new(0, 0, DateTimeOffset.UnixEpoch);
}
