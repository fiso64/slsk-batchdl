using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Tests.ClientTests;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Tests.Core;

[TestClass]
public sealed class LeafDownloadFailureTests
{
    [TestMethod]
    public async Task RemoteFileTransportCancellation_FailsOnlyItsRoot()
    {
        string output = CreateTempDirectory();
        var failedTarget = Target("cancel-peer", @"Share\Failed.bin");
        var completedTarget = Target("ok-peer", @"Share\Completed.bin");
        var failed = new RemoteFileJob(failedTarget);
        var completed = new RemoteFileJob(completedTarget);
        var client = new MockSoulseekClient([
            Response(failedTarget),
            Response(completedTarget),
        ]);
        client.BeforeDownloadStartsAsync = (username, _, _) =>
            username == failedTarget.Username
                ? Task.FromException(new OperationCanceledException("transport ended early"))
                : Task.CompletedTask;
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var settings = new DownloadSettings
        {
            Output = { ParentDir = output, NameFormat = "" },
            Transfer = { NoIncompleteExt = true, UnknownErrorRetries = 0 },
        };
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings));

        try
        {
            engine.Enqueue(failed, settings);
            engine.Enqueue(completed, settings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Failed, failed.TerminalOutcome);
            Assert.AreEqual(JobFailureReason.AllDownloadsFailed, failed.FailureReason);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, completed.TerminalOutcome);
            Assert.IsTrue(File.Exists(completed.DownloadPath));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteFileRequestedCancellation_RemainsCancelled()
    {
        string output = CreateTempDirectory();
        var target = Target("cancel-peer", @"Share\Cancelled.bin");
        var job = new RemoteFileJob(target);
        var client = new MockSoulseekClient([Response(target)]);
        client.BeforeDownloadStartsAsync = (_, _, _) =>
        {
            job.Cancel(JobCancellationSource.UserRequestedJob);
            return Task.FromException(new OperationCanceledException(job.Cts!.Token));
        };
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var settings = new DownloadSettings
        {
            Output = { ParentDir = output, NameFormat = "" },
            Transfer = { NoIncompleteExt = true, UnknownErrorRetries = 0 },
        };
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings));

        try
        {
            engine.Enqueue(job, settings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Cancelled, job.TerminalOutcome);
            Assert.AreEqual(JobCancellationSource.UserRequestedJob, job.CancellationSource);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static PeerFileTarget Target(string username, string filename)
        => new(new PeerFileIdentity(username, filename), 16, ".bin");

    private static SearchResponse Response(PeerFileTarget target)
        => new(
            target.Username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100,
            queueLength: 0,
            fileList: [new Soulseek.File(1, target.Filename, target.Size ?? 16, target.Extension)]);

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "sockseek-leaf-failure-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
