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
    public void Plan_RejectsMixedPeersAndDuplicateExactTargets()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 1),
            Entry("Other", @"Root\B.flac", 1),
        ]));

        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 1),
            Entry("Peer", @"Root\A.flac", 2),
        ]));
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
    public void Admission_RejectsEachBoundBeforeExecution()
    {
        var plan = new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 10),
            Entry("Peer", @"Root\B.flac", 20),
        ]);

        Assert.ThrowsExactly<DirectoryTransferAdmissionException>(() =>
            new DirectoryTransferAdmissionPolicy(1, 100, 100).Validate(plan, 50));
        Assert.ThrowsExactly<DirectoryTransferAdmissionException>(() =>
            new DirectoryTransferAdmissionPolicy(10, 29, 100).Validate(plan, 50));
        Assert.ThrowsExactly<DirectoryTransferAdmissionException>(() =>
            new DirectoryTransferAdmissionPolicy(10, 100, 49).Validate(plan, 50));
        new DirectoryTransferAdmissionPolicy(2, 30, 50).Validate(plan, 50);
    }

    [TestMethod]
    public void MemoryEstimate_CoversPlanChildrenTextAndAttributesDeterministically()
    {
        var small = new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 10),
        ]);
        var larger = new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 10),
            new DirectoryTransferEntry(
                new PeerFileTarget(
                    new PeerFileIdentity("Peer", @"Root\Long Folder\Long Name.flac"),
                    20,
                    ".flac",
                    attributes: [new Sockseek.Core.Snapshots.FileAttributeSnapshot("Length", 30, 1)]),
                ["Long Folder"]),
        ]);

        long first = DirectoryTransferMemoryEstimator.EstimatePlanAndChildren(small);
        long second = DirectoryTransferMemoryEstimator.EstimatePlanAndChildren(larger);

        Assert.IsTrue(first >= 1_536);
        Assert.IsTrue(second > first);
        Assert.AreEqual(second, DirectoryTransferMemoryEstimator.EstimatePlanAndChildren(larger));
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

    private static DirectoryTransferEntry Entry(
        string username,
        string filename,
        long size,
        IReadOnlyList<string>? components = null)
        => new(Target(username, filename, size), components ?? Array.Empty<string>());

    private static PeerFileTarget Target(string username, string filename, long size)
        => new(new PeerFileIdentity(username, filename), size, Path.GetExtension(filename));
}
