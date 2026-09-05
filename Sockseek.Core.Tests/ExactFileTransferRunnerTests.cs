using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;
using Tests.ClientTests;
using Directory = System.IO.Directory;

namespace Tests.Core;

[TestClass]
public sealed class ExactFileTransferRunnerTests
{
    [TestMethod]
    public async Task SongAndRemoteFile_UseTheSameExactWireIdentityAndOutcome()
    {
        const string username = " Peer Case ";
        const string filename = "Share\\Cafe\u0301\\File\u001B\n.bin ";
        var response = Response(username, filename);
        var client = new MockSoulseekClient([response]);
        var (runner, _, _) = CreateRunner(client);
        var target = Target(username, filename);
        var song = new SongJob(new SongQuery { Title = "File" }) { ExactTarget = target };
        var remote = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        var requests = new List<(string Username, string Filename)>();
        client.BeforeDownloadStartsAsync = (peer, path, _) =>
        {
            requests.Add((peer, path));
            return Task.CompletedTask;
        };

        try
        {
            var settings = new TransferSettings { NoIncompleteExt = true };
            var songOutcome = await runner.DownloadFile(
                target, Path.Combine(root, "song.bin"), song, settings, root,
                settings.MaxStaleTime, publishToDuplicateCache: false);
            var remoteOutcome = await runner.DownloadFile(
                target, Path.Combine(root, "remote.bin"), remote, settings, root,
                settings.MaxStaleTime, publishToDuplicateCache: false);

            Assert.AreEqual(ExactFileTransferStatus.Completed, songOutcome.Status);
            Assert.AreEqual(ExactFileTransferStatus.Completed, remoteOutcome.Status);
            Assert.AreEqual(target.Identity, songOutcome.Result!.Target.Identity);
            Assert.AreEqual(target.Identity, remoteOutcome.Result!.Target.Identity);
            Assert.AreEqual(2, client.DownloadCallCount);
            Assert.AreEqual(0, client.SearchCallCount);
            Assert.IsTrue(requests.All(request => request == (username, filename)));
            Assert.AreEqual(target.Size, song.BytesTransferred);
            Assert.AreEqual(target.Size, remote.BytesTransferred);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnknownFailure_RetriesOnlyTheConfiguredExtraAttempts()
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        var client = new MockSoulseekClient([Response(username, filename)], failingUsers: [username]);
        var (runner, active, _) = CreateRunner(client);
        var target = Target(username, filename);
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        var settings = new TransferSettings { UnknownErrorRetries = 2, NoIncompleteExt = true };

        try
        {
            await Assert.ThrowsExactlyAsync<SoulseekClientException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner, settings, root,
                settings.MaxStaleTime, publishToDuplicateCache: false));

            Assert.AreEqual(3, client.DownloadCallCount);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingPeer_IsNotRetriedAndLeavesNoLiveTransfer()
    {
        var client = new MockSoulseekClient([]);
        var (runner, active, _) = CreateRunner(client);
        var target = Target("Missing", @"Share\File.bin");
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        var settings = new TransferSettings { UnknownErrorRetries = 5, NoIncompleteExt = true };

        try
        {
            await Assert.ThrowsExactlyAsync<UserNotFoundException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner, settings, root,
                settings.MaxStaleTime, publishToDuplicateCache: false));

            Assert.AreEqual(1, client.DownloadCallCount);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransportCancellationWithoutRequestedToken_IsTransferFailure()
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        var client = new MockSoulseekClient([Response(username, filename)]);
        client.BeforeDownloadStartsAsync = (_, _, _) =>
            throw new OperationCanceledException("transport ended early");
        var (runner, active, events) = CreateRunner(client);
        var target = Target(username, filename);
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        TransferFailedChange? failed = null;
        TransferCancelledChange? cancelled = null;
        TransferAttemptFailedChange? failedAttempt = null;
        events.TransferFailed += change => failed = change;
        events.TransferCancelled += change => cancelled = change;
        events.TransferAttemptFailed += change => failedAttempt = change;

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner,
                new TransferSettings { NoIncompleteExt = true }, root,
                new TransferSettings().MaxStaleTime, publishToDuplicateCache: false));

            Assert.IsNotNull(failed);
            Assert.AreEqual(TransferFailureReason.PeerFailure, failed.Reason);
            Assert.IsNotNull(failedAttempt);
            Assert.IsNull(cancelled);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RequestedTokenCancellation_IsTransferCancellation()
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        using var cancellation = new CancellationTokenSource();
        var client = new MockSoulseekClient([Response(username, filename)]);
        client.BeforeDownloadStartsAsync = (_, _, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        var (runner, active, events) = CreateRunner(client);
        var target = Target(username, filename);
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        TransferFailedChange? failed = null;
        TransferCancelledChange? cancelled = null;
        events.TransferFailed += change => failed = change;
        events.TransferCancelled += change => cancelled = change;

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner,
                new TransferSettings { NoIncompleteExt = true }, root,
                new TransferSettings().MaxStaleTime, cancellation.Token,
                publishToDuplicateCache: false));

            Assert.IsNotNull(cancelled);
            Assert.AreEqual(TransferCancellationReason.Requested, cancelled.Reason);
            Assert.IsNull(failed);
            Assert.AreEqual(0, active.ActiveDownloads.Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailureAfterProgress_ReportsTheCurrentTransferredByteCount()
    {
        const string username = "Peer";
        const string filename = @"Share\File.bin";
        var client = new MockSoulseekClient([Response(username, filename)]);
        client.AfterDownloadProgress = (_, _) => throw new IOException("failed after progress");
        var (runner, _, events) = CreateRunner(client);
        var target = Target(username, filename);
        var owner = new RemoteFileJob(target);
        string root = CreateTempDirectory();
        TransferFailedChange? failed = null;
        events.TransferFailed += change => failed = change;

        try
        {
            var settings = new TransferSettings { NoIncompleteExt = true, UnknownErrorRetries = 0 };
            await Assert.ThrowsExactlyAsync<IOException>(() => runner.DownloadFile(
                target, Path.Combine(root, "file.bin"), owner,
                settings, root,
                new TransferSettings().MaxStaleTime, publishToDuplicateCache: false));

            Assert.IsNotNull(failed);
            Assert.AreEqual(target.Size, owner.BytesTransferred);
            Assert.AreEqual(target.Size, failed.Transfer.BytesTransferred);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PublishingReplacement_InvalidatesEveryPriorIdentityForThatPath()
    {
        string root = CreateTempDirectory();
        string output = Path.Combine(root, "file.bin");
        var cache = new DownloadedFileCache();
        var first = Target("first-peer", @"Share\First.bin");
        var second = Target("second-peer", @"Share\Second.bin");
        try
        {
            System.IO.File.WriteAllBytes(output, new byte[16]);
            cache.Publish(output, first);
            System.IO.File.WriteAllBytes(output, Enumerable.Repeat((byte)1, 16).ToArray());
            cache.Publish(output, second);

            Assert.IsFalse(cache.TryGetReusable(first, out _));
            Assert.IsTrue(cache.TryGetReusable(second, out var retained));
            Assert.AreEqual(Path.GetFullPath(output), retained.OutputPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConcurrentDifferentTargets_SharingOutput_DoNotOverwriteWhenSkipExistingApplies()
    {
        var firstTarget = Target("first-peer", @"Share\First.bin");
        var secondTarget = Target("second-peer", @"Share\Second.bin");
        var client = new MockSoulseekClient(
        [
            Response(firstTarget.Username, firstTarget.Filename),
            Response(secondTarget.Username, secondTarget.Filename),
        ]);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeDownloadStartsAsync = async (_, remotePath, cancellationToken) =>
        {
            if (remotePath != firstTarget.Filename)
                return;
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        };
        var cache = new DownloadedFileCache();
        var (runner, _, _) = CreateRunner(client, cache);
        string root = CreateTempDirectory();
        string output = Path.Combine(root, "file.bin");
        var settings = new TransferSettings { NoIncompleteExt = true };

        try
        {
            Task<ExactFileTransferOutcome> first = runner.DownloadFile(
                firstTarget,
                output,
                new RemoteFileJob(firstTarget),
                settings,
                root,
                settings.MaxStaleTime,
                allowOverwrite: true,
                protectPublishedOutput: true);
            await firstEntered.Task;
            Task<ExactFileTransferOutcome> second = runner.DownloadFile(
                secondTarget,
                output,
                new RemoteFileJob(secondTarget),
                settings,
                root,
                settings.MaxStaleTime,
                allowOverwrite: true,
                protectPublishedOutput: true);

            releaseFirst.TrySetResult();
            Assert.AreEqual(ExactFileTransferStatus.Completed, (await first).Status);
            Assert.AreEqual(ExactFileTransferStatus.AlreadyExists, (await second).Status);
            Assert.AreEqual(1, client.DownloadCallCount);
            Assert.IsTrue(cache.TryGetReusable(firstTarget, out _));
            Assert.IsFalse(cache.TryGetReusable(secondTarget, out _));
        }
        finally
        {
            releaseFirst.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    private static (ExactPeerFileTransferRunner Runner, ActiveDownloadTracker Active, DownloadEvents Events)
        CreateRunner(MockSoulseekClient client, DownloadedFileCache? downloadedFiles = null)
    {
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var active = new ActiveDownloadTracker();
        var events = new DownloadEvents();
        return (
            new ExactPeerFileTransferRunner(
                client,
                TestHelpers.CreateMockClientManager(client, engineSettings),
                active,
                downloadedFiles ?? new DownloadedFileCache(),
                events,
                new StaleDownloadCoordinator(active)),
            active,
            events);
    }

    private static SearchResponse Response(string username, string filename)
        => new(
            username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100,
            queueLength: 0,
            fileList: [new Soulseek.File(1, filename, 16, ".bin")]);

    private static PeerFileTarget Target(string username, string filename)
        => new(new PeerFileIdentity(username, filename), 16, ".bin");

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "sockseek-exact-runner-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
