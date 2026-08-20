using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.Core;

[TestClass]
public sealed class DirectoryDownloadLifecycleTests
{
    [TestMethod]
    public void PeerSource_StartsUnresolved()
    {
        var source = new RemoteDirectorySource.PeerDirectory(
            new PeerDirectoryIdentity("Peer", "Root"));
        var job = new RemoteDirectoryJob(source);

        Assert.IsInstanceOfType<DirectoryExecutionState.Unresolved>(job.DirectoryState);
        Assert.IsNull(job.ActiveAttempt);
        Assert.AreSame(source, job.Source);
    }

    [TestMethod]
    public void ResolvedSource_StartsPlannedAndOwnsTheSourcePlanOnce()
    {
        var plan = Plan();
        var source = new RemoteDirectorySource.Resolved(plan);
        var job = new RemoteDirectoryJob(source);

        var state = (DirectoryExecutionState.Planned)job.DirectoryState;
        Assert.AreEqual(1, state.AttemptNumber);
        Assert.AreSame(plan, job.ActiveAttempt!.Plan);
        Assert.AreSame(plan, source.Plan);
        Assert.AreEqual(0, job.FileJobs.Count);
    }

    [TestMethod]
    public void Attempt_RequiresExactlyOneChildPerEntryBeforeTransfer()
    {
        var job = new TestDirectoryJob();
        job.BeginDirectoryAttempt(Plan());
        Assert.ThrowsExactly<InvalidOperationException>(job.BeginDirectoryTransfer);
        Assert.ThrowsExactly<ArgumentException>(() =>
            job.MaterializeDirectoryChildren([new RemoteFileJob(Target(@"Root\A.bin"))]));

        job.MaterializeDirectoryChildren([
            new RemoteFileJob(Target(@"Root\A.bin")),
            new RemoteFileJob(Target(@"Root\B.bin")),
        ]);
        job.BeginDirectoryTransfer();

        var state = (DirectoryExecutionState.Transferring)job.DirectoryState;
        Assert.AreEqual(1, state.AttemptNumber);
        Assert.AreEqual(2, job.FileJobs.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => job.BeginDirectoryAttempt(Plan()));
        Assert.ThrowsExactly<InvalidOperationException>(job.ClearChildren);
    }

    [TestMethod]
    public void Retry_CreatesMonotonicallyNumberedImmutablePlanAttempts()
    {
        var job = new TestDirectoryJob();
        job.BeginDirectoryResolution();
        var firstPlan = Plan();
        var first = job.BeginDirectoryAttempt(firstPlan);
        var secondPlan = new DirectoryTransferPlan("Second", [
            new DirectoryTransferEntry(Target(@"Root\C.bin"), []),
        ]);
        var second = job.BeginDirectoryAttempt(secondPlan);

        Assert.AreEqual(1, first.AttemptNumber);
        Assert.AreSame(firstPlan, first.Plan);
        Assert.AreEqual(2, second.AttemptNumber);
        Assert.AreSame(secondPlan, second.Plan);
        Assert.AreSame(second, job.ActiveAttempt);
    }

    [TestMethod]
    public void SupplementalWork_IsOwnedByTheDirectoryButDoesNotMutateTheAttemptPlan()
    {
        var job = new TestDirectoryJob();
        var attempt = job.BeginDirectoryAttempt(Plan());
        var planned = new[]
        {
            new RemoteFileJob(Target(@"Root\B.bin")),
            new RemoteFileJob(Target(@"Root\A.bin")),
        };
        job.MaterializeDirectoryChildren(planned);
        job.BeginDirectoryTransfer();

        var supplemental = new RemoteFileJob(Target(@"Artwork\Cover.jpg"));
        job.AddSupplemental(supplemental);

        CollectionAssert.AreEqual(planned, attempt.FileJobs.ToArray());
        CollectionAssert.AreEqual(
            new FileDownloadJob[] { planned[0], planned[1], supplemental },
            job.FileJobs.ToArray());
        Assert.AreEqual(30, job.TotalKnownBytes);
    }

    [TestMethod]
    public void AlbumAndRemoteDirectoryJobs_AreSiblingDirectoryJobs()
    {
        var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        var remote = new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
            new PeerDirectoryIdentity("Peer", "Root")));

        Assert.IsInstanceOfType<DirectoryDownloadJob>(album);
        Assert.IsInstanceOfType<DirectoryDownloadJob>(remote);
        Assert.AreEqual(typeof(DirectoryDownloadJob), typeof(AlbumJob).BaseType);
        Assert.AreEqual(typeof(DirectoryDownloadJob), typeof(RemoteDirectoryJob).BaseType);
    }

    private static DirectoryTransferPlan Plan()
        => new("Root", [
            new DirectoryTransferEntry(Target(@"Root\B.bin"), []),
            new DirectoryTransferEntry(Target(@"Root\A.bin"), []),
        ]);

    private static PeerFileTarget Target(string filename)
        => new(new PeerFileIdentity("Peer", filename), 10, Path.GetExtension(filename));

    private sealed class TestDirectoryJob : DirectoryDownloadJob
    {
        protected override bool DefaultCanBeSkipped => false;

        public void AddSupplemental(FileDownloadJob child)
            => AddSupplementalDirectoryChild(child);

        public void ClearChildren()
            => ClearDirectoryChildren();
    }
}
