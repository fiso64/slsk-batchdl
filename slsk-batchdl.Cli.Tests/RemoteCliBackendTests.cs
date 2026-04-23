using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Cli;
using Sldl.Core.Settings;
using Sldl.Server;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Tests.Cli;

[TestClass]
public class RemoteCliBackendTests
{
    [TestMethod]
    public async Task RemoteCliBackend_SearchProjectionAndDownloadFollowUp_Work()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-remote-backend-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "sldl-remote-backend-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "01. Artist - Track One.mp3"), "a");

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            var seenTypes = new ConcurrentBag<string>();
            backend.EventReceived += envelope => seenTypes.Add(envelope.Type);
            await backend.StartAsync();

            var searchSummary = await backend.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    }));

            await WaitForJobStateAsync(backend, searchSummary.JobId, "Done");

            var projection = await backend.GetTrackProjectionAsync(searchSummary.JobId);
            Assert.IsNotNull(projection);
            Assert.AreEqual(1, projection.Items.Count);

            var downloadSummary = await backend.StartSongDownloadAsync(
                searchSummary.JobId,
                new StartSongDownloadRequestDto(projection.Items[0].Ref, outputDir));

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.WorkflowId, downloadSummary.WorkflowId);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.Presentation.VisualParentJobId);

            await WaitForJobStateAsync(backend, downloadSummary.JobId, "Done");

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray();
            CollectionAssert.Contains(downloaded, "01. Artist - Track One.mp3");

            Assert.IsTrue(seenTypes.Contains("job.upserted"));
            Assert.IsTrue(seenTypes.Contains("search.updated"));
            Assert.IsTrue(seenTypes.Contains("download.started"));
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitForJobStateAsync(ICliBackend backend, Guid jobId, string expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var detail = await backend.GetJobDetailAsync(jobId, CancellationToken.None);
            if (detail?.Summary.State == expectedState)
                return;

            await Task.Delay(50, CancellationToken.None);
        }

        Assert.Fail($"Timed out waiting for job {jobId} to reach state '{expectedState}'.");
    }
}
