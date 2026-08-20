using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Server.PeerBrowsing;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PeerBrowseCursorCodecTests
{
    [TestMethod]
    public void RowCursor_RoundTripsAndBindsGenerationAndFilters()
    {
        var codec = new PeerBrowseCursorCodec(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray());
        Guid browseId = Guid.NewGuid();
        string cursor = codec.EncodeRows(
            PeerBrowseCursorKind.Directories, browseId, 42, true, "lossless", 99);

        Assert.AreEqual(
            99,
            codec.DecodeRows(
                cursor, PeerBrowseCursorKind.Directories, browseId, 42, true, "lossless"));
        Assert.ThrowsExactly<ArgumentException>(() => codec.DecodeRows(
            cursor, PeerBrowseCursorKind.Directories, Guid.NewGuid(), 42, true, "lossless"));
        Assert.ThrowsExactly<ArgumentException>(() => codec.DecodeRows(
            cursor, PeerBrowseCursorKind.Directories, browseId, 42, true, "different"));
    }

    [TestMethod]
    public void Cursor_RejectsTamperingAndAnotherProcessKey()
    {
        var codec = new PeerBrowseCursorCodec(new byte[32]);
        string cursor = codec.EncodeResources(
            "Peer", UserBrowseState.Complete, DateTimeOffset.UtcNow, Guid.NewGuid());
        int tamperIndex = cursor.Length - 8;
        char replacement = cursor[tamperIndex] == 'a' ? 'b' : 'a';
        string tampered = cursor[..tamperIndex] + replacement + cursor[(tamperIndex + 1)..];

        Assert.ThrowsExactly<ArgumentException>(() => codec.DecodeResources(
            tampered, "Peer", UserBrowseState.Complete));
        Assert.ThrowsExactly<ArgumentException>(() => new PeerBrowseCursorCodec(
            Enumerable.Repeat((byte)1, 32).ToArray()).DecodeResources(
                cursor, "Peer", UserBrowseState.Complete));
    }
}
