using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;

namespace Tests;

[TestClass]
public sealed class OneShotPeerDirectorySourceTests
{
    [TestMethod]
    public async Task RetainsOnlyRequestedPublicSubtreeWithExactWireFilenames()
    {
        var source = new OneShotPeerDirectorySource(new FixtureTransport());

        PeerDirectorySnapshot snapshot = await source.RetrieveDirectoryAsync(
            new PeerDirectoryIdentity("Peer", "Root/Music"));

        Assert.IsTrue(snapshot.IsComplete);
        Assert.AreEqual(2, snapshot.Files.Count);
        PeerFileTarget first = snapshot.Files[0];
        Assert.AreEqual(@"Root\Music\one.mp3", first.Filename);
        Assert.AreEqual(320, first.BitRate);
        Assert.AreEqual(180, first.Length);
        CollectionAssert.AreEqual(
            new[] { "BitRate", "Length" },
            first.Attributes!.Select(attribute => attribute.Type).ToArray());
        Assert.AreEqual("Root/Music/Sub/two.flac", snapshot.Files[1].Filename);
    }

    [TestMethod]
    public async Task PreservesControlBearingWirePaths()
    {
        var source = new OneShotPeerDirectorySource(new ControlPathTransport());

        PeerDirectorySnapshot snapshot = await source.RetrieveDirectoryAsync(
            new PeerDirectoryIdentity("Peer", "Root/Mu\0sic"));

        Assert.AreEqual("Root/Mu\0sic/Sub\n\\song\u001B.mp3", snapshot.Files.Single().Filename);
    }

    private sealed class FixtureTransport : IPeerBrowseTransport
    {
        public async Task ReceiveAsync(
            string username,
            IPeerBrowseRowSink sink,
            Action<PeerBrowseTransportProgress>? transportProgress = null,
            Action<PeerBrowseIndexProgress>? indexProgress = null,
            CancellationToken cancellationToken = default)
        {
            await DirectoryAsync("Elsewhere", PeerShareVisibility.Public, "ignored.bin");

            await sink.BeginDirectoryAsync(@"Root\Music", PeerShareVisibility.Public, 1, cancellationToken);
            await sink.BeginFileAsync(new PeerBrowseWireFile(1, "one.mp3", 12, "mp3", 2), cancellationToken);
            await sink.AddAttributeAsync(new PeerBrowseWireAttribute(0, 320), cancellationToken);
            await sink.AddAttributeAsync(new PeerBrowseWireAttribute(1, 180), cancellationToken);
            await sink.EndFileAsync(cancellationToken);

            await sink.BeginDirectoryAsync("Root/Music/Sub", PeerShareVisibility.Public, 1, cancellationToken);
            await sink.BeginFileAsync(
                new PeerBrowseWireFile(1, "Root/Music/Sub/two.flac", 34, "flac", 0),
                cancellationToken);
            await sink.EndFileAsync(cancellationToken);

            await DirectoryAsync(@"Root\Music\Secret", PeerShareVisibility.Locked, "locked.bin");
            await sink.CompleteAsync(cancellationToken);

            async Task DirectoryAsync(string path, PeerShareVisibility visibility, string filename)
            {
                await sink.BeginDirectoryAsync(path, visibility, 1, cancellationToken);
                await sink.BeginFileAsync(new PeerBrowseWireFile(1, filename, 1, "bin", 0), cancellationToken);
                await sink.EndFileAsync(cancellationToken);
            }
        }
    }

    private sealed class ControlPathTransport : IPeerBrowseTransport
    {
        public async Task ReceiveAsync(
            string username,
            IPeerBrowseRowSink sink,
            Action<PeerBrowseTransportProgress>? transportProgress = null,
            Action<PeerBrowseIndexProgress>? indexProgress = null,
            CancellationToken cancellationToken = default)
        {
            await sink.BeginDirectoryAsync("Root/Mu\0sic/Sub\n", PeerShareVisibility.Public, 1, cancellationToken);
            await sink.BeginFileAsync(new PeerBrowseWireFile(
                1, "song\u001B.mp3", 1, "mp3", 0), cancellationToken);
            await sink.EndFileAsync(cancellationToken);
            await sink.CompleteAsync(cancellationToken);
        }
    }
}
