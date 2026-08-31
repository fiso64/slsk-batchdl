using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Tests.ClientTests;

namespace Tests.Core;

[TestClass]
public sealed class RemoteDirectoryJobTests
{
    [TestMethod]
    public async Task PeerDirectorySource_RetrievesExactlyOnceThenTransfersItsSnapshot()
    {
        string sourceRoot = CreateTempDirectory("source");
        string selected = Path.Combine(sourceRoot, "Selected");
        string nested = Path.Combine(selected, "Nested");
        string output = CreateTempDirectory("output");
        Directory.CreateDirectory(nested);
        string sourceFile = Path.Combine(nested, "File.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3, 4]);
        var client = MockSoulseekClient.FromLocalPaths(useTags: false, sourceRoot);
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
            new PeerDirectoryIdentity("local", selected.Replace('/', '\\'))));

        try
        {
            await Run(job, client, output);

            Assert.AreEqual(1, client.BrowseCallCount);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            Assert.IsNotNull(job.ResolvedDirectory);
            Assert.AreEqual(1, job.ActiveAttempt!.AttemptNumber);
            Assert.AreEqual(1, job.FileJobs.Count);
            Assert.IsTrue(File.Exists(Path.Combine(output, "Selected", "Nested", "File.bin")));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolvedSource_NeverBrowsesAndUsesTheOwnedPlanAsAttemptOne()
    {
        string sourceRoot = CreateTempDirectory("source");
        string output = CreateTempDirectory("output");
        string sourceFile = Path.Combine(sourceRoot, "Exact.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3, 4]);
        var target = new PeerFileTarget(
            new PeerFileIdentity("local", sourceFile.Replace('/', '\\')),
            4,
            ".bin");
        var plan = new DirectoryTransferPlan("Selection", [
            new DirectoryTransferEntry(target, ["Nested"]),
        ]);
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));
        var client = MockSoulseekClient.FromLocalPaths(useTags: false, sourceRoot);

        try
        {
            await Run(job, client, output);

            Assert.AreEqual(0, client.BrowseCallCount);
            Assert.AreSame(plan, job.ActiveAttempt!.Plan);
            Assert.AreEqual(1, job.ActiveAttempt.AttemptNumber);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            Assert.IsTrue(File.Exists(Path.Combine(output, "Selection", "Nested", "Exact.bin")));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    public void InheritedMusicNameFormat_FallsBackToOrdinaryTreePlacement()
    {
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Root\Nested\File.bin"),
            4,
            ".bin");
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Root", [
                new DirectoryTransferEntry(target, ["Nested"]),
            ])));
        var settings = new DownloadSettings
        {
            Output = { NameFormat = "{albumartist}/{album}/{filename}" },
        };

        JobPreparer.PrepareSubtree(job, settings);

        Assert.AreEqual("", job.Config.Output.NameFormat);
    }

    private static async Task Run(
        RemoteDirectoryJob job,
        MockSoulseekClient client,
        string output)
    {
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var settings = new DownloadSettings
        {
            Output =
            {
                ParentDir = output,
                NameFormat = "",
            },
        };
        var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings));
        engine.Enqueue(job, settings);
        engine.CompleteEnqueue();
        await engine.RunAsync(CancellationToken.None);
    }

    private static string CreateTempDirectory(string suffix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sockseek-remote-directory-{suffix}-{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        return path;
    }
}
