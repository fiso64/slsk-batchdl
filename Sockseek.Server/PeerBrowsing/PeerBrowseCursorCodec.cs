using System.Security.Cryptography;
using System.Text.Json;
using Sockseek.Api;

namespace Sockseek.Server.PeerBrowsing;

/// <summary>
/// Process-local authenticated cursors. A row cursor carries only the immutable
/// row ID; the corresponding sort value is recovered from the immutable artifact,
/// keeping cursors bounded even when a peer advertises a very long path.
/// </summary>
public sealed class PeerBrowseCursorCodec
{
    private const int SignatureLength = 32;
    private readonly byte[] key;

    public PeerBrowseCursorCodec()
        : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    internal PeerBrowseCursorCodec(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
            throw new ArgumentException("The cursor signing key must contain at least 32 bytes.", nameof(key));
        this.key = key.ToArray();
    }

    public string EncodeRows(
        PeerBrowseCursorKind kind,
        Guid browseId,
        long? parentId,
        bool recursive,
        string? query,
        long lastId)
    {
        if (kind is not (PeerBrowseCursorKind.Directories or PeerBrowseCursorKind.Files))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return Encode(new CursorPayload(1, kind, browseId, parentId, recursive, query, lastId));
    }

    public long DecodeRows(
        string cursor,
        PeerBrowseCursorKind expectedKind,
        Guid expectedBrowseId,
        long? expectedParentId,
        bool expectedRecursive,
        string? expectedQuery)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Version != 1
            || payload.Kind != expectedKind
            || payload.BrowseId != expectedBrowseId
            || payload.ParentId != expectedParentId
            || payload.Recursive != expectedRecursive
            || !string.Equals(payload.Query, expectedQuery, StringComparison.Ordinal)
            || payload.LastId is null or <= 0
            || payload.CreatedAt is not null
            || payload.ResourceId is not null
            || payload.Username is not null
            || payload.State is not null)
        {
            throw InvalidCursor();
        }
        return payload.LastId.Value;
    }

    public string EncodeResources(
        string? username,
        UserBrowseState? state,
        DateTimeOffset createdAt,
        Guid browseId)
        => Encode(new CursorPayload(
            1,
            PeerBrowseCursorKind.Resources,
            Username: username,
            State: state,
            CreatedAt: createdAt,
            ResourceId: browseId));

    public PeerBrowseResourceCursor DecodeResources(
        string cursor,
        string? expectedUsername,
        UserBrowseState? expectedState)
    {
        CursorPayload payload = Decode(cursor);
        if (payload.Version != 1
            || payload.Kind != PeerBrowseCursorKind.Resources
            || !string.Equals(payload.Username, expectedUsername, StringComparison.Ordinal)
            || payload.State != expectedState
            || payload.CreatedAt is null
            || payload.ResourceId is null
            || payload.ResourceId == Guid.Empty
            || payload.BrowseId is not null
            || payload.ParentId is not null
            || payload.LastId is not null
            || payload.Query is not null
            || payload.Recursive)
        {
            throw InvalidCursor();
        }
        return new PeerBrowseResourceCursor(payload.CreatedAt.Value, payload.ResourceId.Value);
    }

    private string Encode(CursorPayload payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] signature = HMACSHA256.HashData(key, body);
        byte[] signed = new byte[body.Length + signature.Length];
        body.CopyTo(signed, 0);
        signature.CopyTo(signed, body.Length);
        return Base64UrlEncode(signed);
    }

    private CursorPayload Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096)
            throw InvalidCursor();
        try
        {
            byte[] signed = Base64UrlDecode(value);
            if (signed.Length <= SignatureLength)
                throw InvalidCursor();
            ReadOnlySpan<byte> body = signed.AsSpan(0, signed.Length - SignatureLength);
            ReadOnlySpan<byte> signature = signed.AsSpan(signed.Length - SignatureLength);
            byte[] expected = HMACSHA256.HashData(key, body);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
                throw InvalidCursor();
            return JsonSerializer.Deserialize<CursorPayload>(body)
                   ?? throw InvalidCursor();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw InvalidCursor(exception);
        }
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static ArgumentException InvalidCursor(Exception? inner = null)
        => new("The peer browse cursor is invalid.", "cursor", inner);

    private sealed record CursorPayload(
        int Version,
        PeerBrowseCursorKind Kind,
        Guid? BrowseId = null,
        long? ParentId = null,
        bool Recursive = false,
        string? Query = null,
        long? LastId = null,
        string? Username = null,
        UserBrowseState? State = null,
        DateTimeOffset? CreatedAt = null,
        Guid? ResourceId = null);
}

public enum PeerBrowseCursorKind
{
    Resources,
    Directories,
    Files,
}

public sealed record PeerBrowseResourceCursor(DateTimeOffset CreatedAt, Guid BrowseId);
