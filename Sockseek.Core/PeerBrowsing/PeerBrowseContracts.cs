using Sockseek.Core.Models;

namespace Sockseek.Core.PeerBrowsing;

public enum PeerShareVisibility
{
    Public,
    Locked,
}

public sealed record PeerBrowseWireFile(
    int Code,
    string Filename,
    long Size,
    string Extension,
    int AttributeCount);

public sealed record PeerBrowseWireAttribute(int Type, int Value);

/// <summary>
/// Receives one materialized browse-response row at a time. Implementations are
/// expected to retain rows in bounded transactions.
/// </summary>
public interface IPeerBrowseRowSink : IAsyncDisposable
{
    ValueTask BeginDirectoryAsync(
        string wirePath,
        PeerShareVisibility visibility,
        int fileCount,
        CancellationToken cancellationToken = default);

    ValueTask BeginFileAsync(
        PeerBrowseWireFile file,
        CancellationToken cancellationToken = default);

    ValueTask AddAttributeAsync(
        PeerBrowseWireAttribute attribute,
        CancellationToken cancellationToken = default);

    ValueTask EndFileAsync(CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}

public sealed record PeerBrowseIndexProgress(
    long DirectoryCount,
    long FileCount,
    long TotalFileBytes);

public sealed record PeerBrowseTransportProgress(
    long CompressedBytesReceived,
    long? CompressedBytesExpected);

/// <summary>
/// The narrow transport boundary between peer browse acquisition and Sockseek's
/// disk-backed artifact writer.
/// </summary>
public interface IPeerBrowseTransport
{
    Task ReceiveAsync(
        string username,
        IPeerBrowseRowSink sink,
        Action<PeerBrowseTransportProgress>? transportProgress = null,
        Action<PeerBrowseIndexProgress>? indexProgress = null,
        CancellationToken cancellationToken = default);
}

public sealed class PeerBrowseProtocolException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// Resolves one exact peer directory subtree. Daemon execution supplies the
/// shared, artifact-backed implementation; local and test clients may implement
/// this seam directly without emulating network framing.
/// </summary>
public interface IPeerDirectorySource
{
    Task<PeerDirectorySnapshot> RetrieveDirectoryAsync(
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken = default);
}

internal sealed class MissingPeerDirectorySource : IPeerDirectorySource
{
    public static MissingPeerDirectorySource Instance { get; } = new();

    public Task<PeerDirectorySnapshot> RetrieveDirectoryAsync(
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken = default)
        => Task.FromException<PeerDirectorySnapshot>(new InvalidOperationException(
            "Peer directory retrieval requires the shared peer-browse service."));
}
