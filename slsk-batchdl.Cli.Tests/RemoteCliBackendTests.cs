using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Cli;
using Sldl.Core;
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

    [TestMethod]
    public async Task RemoteCliBackend_SubmitExtract_UsesClientDownloadSettingsDelta()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-remote-backend-album-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "sldl-remote-backend-album-out-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

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
                    NameFormat = "{foldername}/{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        TextWriter originalOut = Console.Out;
        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var summary = await backend.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "extract",
                        Input = "Artist Album",
                        InputType = "String",
                    },
                    new SubmissionOptionsDto(
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsDelta(["-a", "--no-browse-folder"]))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, "completed");

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Track One.mp3", "02. Track Two.mp3" }, downloaded);

            using var output = new StringWriter();
            Console.SetOut(output);
            Logger.AddConsole(writer: (message, _) => Console.WriteLine(message));
            Logger.SetConsoleLogLevel(Logger.LogLevel.Info);
            await Sldl.Cli.Program.PrintRemoteCompleteAsync(backend, summary.WorkflowId, CancellationToken.None);
            StringAssert.Contains(output.ToString(), "Completed: 2 succeeded, 0 failed.");
        }
        finally
        {
            Console.SetOut(originalOut);
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_PrintResults_RendersCompletedSearchPayloadWithoutDownloading()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-remote-print-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "sldl-remote-print-out-" + Guid.NewGuid());
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

        TextWriter originalOut = Console.Out;
        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var printSettings = new DownloadSettings
            {
                PrintOption = PrintOption.Results,
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            };

            var summary = await backend.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "extract",
                        Input = "Artist - Track One",
                        InputType = "String",
                    },
                    new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsDelta(["Artist - Track One", "--print-results"]))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, "completed");

            using var output = new StringWriter();
            Console.SetOut(output);
            await Sldl.Cli.Program.PrintRemoteResultsAsync(backend, summary.WorkflowId, printSettings, CancellationToken.None);

            string rendered = output.ToString();
            StringAssert.Contains(rendered, "Results for Artist - Track One");
            StringAssert.Contains(rendered, "Artist - Track One.mp3");
            Assert.AreEqual(0, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                "Remote print-results mode should not download files.");
        }
        finally
        {
            Console.SetOut(originalOut);
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task RemoteCliBackend_PrintTracks_RendersPlannedTracksFromWorkflowSnapshot()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "sldl-remote-print-tracks-" + Guid.NewGuid() + ".txt");
        string outputDir = Path.Combine(Path.GetTempPath(), "sldl-remote-print-tracks-out-" + Guid.NewGuid());
        Directory.CreateDirectory(outputDir);
        string existingAlbumDir = Path.Combine(outputDir, "Artist Two", "Album Two");
        Directory.CreateDirectory(existingAlbumDir);
        File.WriteAllText(Path.Combine(outputDir, "Artist One - Track One.mp3"), "already here");
        File.WriteAllText(Path.Combine(existingAlbumDir, "01. Artist Two - Album Track.mp3"), "already here");
        File.WriteAllLines(inputPath, ["\"Artist One - Track One\"", "a:\"Artist Two - Album Two\""]);

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings(),
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
                Skip =
                {
                    SkipMode = SkipMode.Name,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        TextWriter originalOut = Console.Out;
        try
        {
            await app.StartAsync();
            await using var backend = new RemoteCliBackend(url);
            await backend.StartAsync();

            var printSettings = new DownloadSettings
            {
                PrintOption = PrintOption.Tracks,
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            };

            var summary = await backend.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "extract",
                        Input = inputPath,
                        InputType = "List",
                    },
                    new SubmissionOptionsDto(
                        OutputParentDir: outputDir,
                        DownloadSettings: ConfigManager.CreateCliDownloadSettingsDelta([inputPath, "--input-type", "list", "--print-tracks"]))));

            await WaitForWorkflowStateAsync(backend, summary.WorkflowId, "completed");

            using var output = new StringWriter();
            Console.SetOut(output);
            await Sldl.Cli.Program.PrintRemotePlannedOutputAsync(backend, summary.WorkflowId, printSettings, CancellationToken.None);

            string rendered = output.ToString();
            StringAssert.Contains(rendered, "Artist One - Track One");
            StringAssert.Contains(rendered, "Artist Two - Album Two");
            StringAssert.Contains(rendered, "already exist");
            Assert.AreEqual(2, Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length,
                "Remote print-tracks mode should not download files.");
        }
        finally
        {
            Console.SetOut(originalOut);
            await app.StopAsync();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
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

    private static async Task WaitForWorkflowStateAsync(ICliBackend backend, Guid workflowId, string expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var detail = await backend.GetWorkflowAsync(workflowId, CancellationToken.None);
            if (detail?.Summary.State == expectedState)
                return;

            await Task.Delay(50, CancellationToken.None);
        }

        var finalDetail = await backend.GetWorkflowAsync(workflowId, CancellationToken.None);
        string jobs = finalDetail == null
            ? "<missing>"
            : string.Join(", ", finalDetail.Jobs.Select(job => $"[{job.DisplayId}] {job.Kind}:{job.State} parent={job.ParentJobId?.ToString() ?? "-"} result={job.ResultJobId?.ToString() ?? "-"}"));
        Assert.Fail($"Timed out waiting for workflow {workflowId} to reach state '{expectedState}'. Jobs: {jobs}");
    }
}
