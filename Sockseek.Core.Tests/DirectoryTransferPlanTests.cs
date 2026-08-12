using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.Core;

[TestClass]
public sealed class DirectoryTransferPlanTests
{
    [TestMethod]
    public void Plan_CopiesAndDeterministicallyOrdersEntries()
    {
        var components = new List<string> { "Disc 2" };
        var entries = new List<DirectoryTransferEntry>
        {
            Entry("Peer", @"Root\Disc 2\02.flac", 2, components),
            Entry("Peer", @"Root\Disc 1\01.flac", 1, ["Disc 1"]),
        };

        var plan = new DirectoryTransferPlan("Selection", entries);
        components[0] = "Changed";
        entries.Clear();

        Assert.AreEqual(2, plan.Entries.Count);
        Assert.AreEqual("Disc 1", plan.Entries[0].RelativeDirectoryComponents[0]);
        Assert.AreEqual("Disc 2", plan.Entries[1].RelativeDirectoryComponents[0]);
        Assert.AreEqual(3L, plan.TotalKnownBytes);
    }

    [TestMethod]
    public void Plan_RejectsMixedPeersAndDeduplicatesExactTargets()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 1),
            Entry("Other", @"Root\B.flac", 1),
        ]));

        var deduplicated = new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 1),
            Entry("Peer", @"Root\A.flac", 2),
        ]);

        Assert.AreEqual(1, deduplicated.Entries.Count);
        Assert.AreEqual(1L, deduplicated.TotalKnownBytes);
    }

    [TestMethod]
    public void Entry_RejectsRootedEmptyTraversalControlAndSeparatorComponents()
    {
        var target = Target("Peer", @"Root\A.flac", 1);

        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, [""]));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, [".."]));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, ["A/B"]));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, ["A\\B"]));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, ["A\nB"]));
    }

    [TestMethod]
    public void PeerDirectorySnapshot_CopiesTargetsAndRejectsMixedPeers()
    {
        var files = new List<PeerFileTarget> { Target("Peer", @"Root\A.flac", 1) };
        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", "Root"),
            files,
            isComplete: true);
        files.Clear();

        Assert.AreEqual(1, snapshot.Files.Count);
        Assert.IsTrue(snapshot.IsComplete);
        Assert.ThrowsExactly<ArgumentException>(() => new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", "Root"),
            [Target("Other", @"Root\B.flac", 1)],
            isComplete: true));
    }

    [TestMethod]
    public void SnapshotPlanning_UsesExactCaseForRemoteDirectoryContainment()
    {
        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Root\Selected"),
            [Target("Peer", @"Root\selected\A.flac", 1)],
            isComplete: true);

        Assert.ThrowsExactly<ArgumentException>(() => DirectoryTransferPlanner.FromSnapshot(snapshot));
    }

    [TestMethod]
    public void SnapshotPlanning_SkipsInvalidEntryWithoutRejectingValidSibling()
    {
        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Root\Selected"),
            [
                Target("Peer", @"Root\Selected\Good.flac", 1),
                Target("Peer", @"Root\Other\Outside.flac", 2),
            ],
            isComplete: true);

        var plan = DirectoryTransferPlanner.FromSnapshot(snapshot);

        Assert.AreEqual(1, plan.Entries.Count);
        Assert.AreEqual(@"Root\Selected\Good.flac", plan.Entries[0].Target.Filename);
    }

    private static DirectoryTransferEntry Entry(
        string username,
        string filename,
        long size,
        IReadOnlyList<string>? components = null)
        => new(Target(username, filename, size), components ?? Array.Empty<string>());

    private static PeerFileTarget Target(string username, string filename, long size)
        => new(new PeerFileIdentity(username, filename), size, Path.GetExtension(filename));
}
