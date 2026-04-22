using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Server;
using System.Collections.Concurrent;

namespace Tests.Server;

[TestClass]
public class EngineSupervisorTests
{
    [TestMethod]
    public async Task StartSongDownloadAsync_ReusesWorkflowAndSetsVisualParent()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    }),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, "Done");

            var tracks = supervisor.GetTrackProjection(searchSummary.JobId);
            Assert.IsNotNull(tracks);
            Assert.AreEqual(1, tracks.Items.Count);

            var downloadSummary = await supervisor.StartSongDownloadAsync(
                searchSummary.JobId,
                new StartSongDownloadRequestDto(tracks.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.WorkflowId, downloadSummary.WorkflowId);
            Assert.IsTrue(downloadSummary.Presentation.IsHiddenFromRoot);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.Presentation.VisualParentJobId);

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, "Done");

            var detail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(detail);
            Assert.AreEqual(searchSummary.JobId, detail.Summary.Presentation.VisualParentJobId);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            Assert.AreEqual(1, downloaded.Length);
            Assert.IsTrue(downloaded[0].EndsWith("01. Artist - Track One.mp3", StringComparison.OrdinalIgnoreCase));

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartAlbumDownloadAsync_ReusesWorkflowAndFindsAlbumByFolderPath()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-album",
                        AlbumQuery = new AlbumQueryDto("Artist", "Album", "", "", false, false, -1, -1),
                    }),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, "Done");

            var albums = supervisor.GetAlbumProjection(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);
            Assert.AreEqual("local", albums.Items[0].Username);
            Assert.AreEqual(albumDir, albums.Items[0].FolderPath);

            var downloadSummary = await supervisor.StartAlbumDownloadAsync(
                searchSummary.JobId,
                new StartAlbumDownloadRequestDto(albums.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.WorkflowId, downloadSummary.WorkflowId);
            Assert.IsTrue(downloadSummary.Presentation.IsHiddenFromRoot);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.Presentation.VisualParentJobId);

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, "Done");

            var detail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(detail);
            Assert.AreEqual(searchSummary.JobId, detail.Summary.Presentation.VisualParentJobId);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "01. Track One.mp3", "02. Track Two.mp3" }, downloaded);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesJobAndWorkflowUpserts_ForSubmittedJobs()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var seenJobIds = new ConcurrentBag<Guid>();
            var seenWorkflowIds = new ConcurrentBag<Guid>();
            supervisor.StateStore.JobUpserted += summary => seenJobIds.Add(summary.JobId);
            supervisor.StateStore.WorkflowUpserted += summary => seenWorkflowIds.Add(summary.WorkflowId);

            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    }),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, "Done");

            CollectionAssert.Contains(seenJobIds.ToList(), searchSummary.JobId);
            CollectionAssert.Contains(seenWorkflowIds.ToList(), searchSummary.WorkflowId);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesSearchUpdated_ForSearchJobResultsAndCompletion()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var updates = new ConcurrentBag<SearchUpdatedDto>();
            supervisor.StateStore.SearchUpdated += update => updates.Add(update);

            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    }),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, "Done");
            await WaitForConditionAsync(
                () => updates.Any(update => update.JobId == searchSummary.JobId && update.IsComplete),
                "Timed out waiting for a completed search update.");

            var matching = updates.Where(update => update.JobId == searchSummary.JobId).ToList();
            Assert.IsTrue(matching.Any(update => update.Revision > 0 && !update.IsComplete));
            Assert.IsTrue(matching.Any(update => update.IsComplete));

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartRetrieveFolderAsync_CompletesQueuedRetrieveJobAndPreservesWorkflow()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Search.NecessaryCond.StrictTitle = true;
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-album",
                        AlbumQuery = new AlbumQueryDto("Artist", "Album", "Track One", "", false, false, -1, -1),
                    }),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, "Done");

            var beforeRetrieve = supervisor.GetAlbumProjection(searchSummary.JobId, includeFiles: true);
            Assert.IsNotNull(beforeRetrieve);
            Assert.AreEqual(1, beforeRetrieve.Items.Count);
            Assert.AreEqual(1, beforeRetrieve.Items[0].Files?.Count);

            var retrieveSummary = await supervisor.StartRetrieveFolderAsync(
                searchSummary.JobId,
                new RetrieveFolderRequestDto(beforeRetrieve.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(retrieveSummary);
            Assert.AreEqual(searchSummary.WorkflowId, retrieveSummary.WorkflowId);
            Assert.IsTrue(retrieveSummary.Presentation.IsHiddenFromRoot);
            Assert.AreEqual(searchSummary.JobId, retrieveSummary.Presentation.VisualParentJobId);

            await WaitForJobStateAsync(supervisor, retrieveSummary.JobId, "Done");

            var retrieveDetail = supervisor.StateStore.GetJobDetail(retrieveSummary.JobId);
            Assert.IsNotNull(retrieveDetail);
            var payload = retrieveDetail.Payload as RetrieveFolderJobPayloadDto;
            Assert.IsNotNull(payload);
            Assert.AreEqual(1, payload.NewFilesFoundCount);

            var afterRetrieve = supervisor.GetAlbumProjection(searchSummary.JobId, includeFiles: true);
            Assert.IsNotNull(afterRetrieve);
            Assert.AreEqual(2, afterRetrieve.Items[0].Files?.Count);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    private static EngineSupervisor CreateSupervisor(string musicRoot, string outputDir, Action<Sldl.Core.Settings.DownloadSettings>? configureDownload = null)
    {
        var defaultDownload = new Sldl.Core.Settings.DownloadSettings
        {
            Output =
            {
                ParentDir = outputDir,
                NameFormat = "{foldername}/{filename}",
            },
        };
        configureDownload?.Invoke(defaultDownload);

        var options = Options.Create(new ServerOptions
        {
            Engine = new Sldl.Core.Settings.EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = defaultDownload,
        });

        return new EngineSupervisor(options);
    }

    private static async Task WaitForJobStateAsync(EngineSupervisor supervisor, Guid jobId, string expectedState, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            var summary = supervisor.StateStore.GetJobSummary(jobId);
            if (summary?.State == expectedState)
                return;

            await Task.Delay(50, timeout.Token);
        }

        Assert.Fail($"Timed out waiting for job {jobId} to reach state '{expectedState}'.");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            if (condition())
                return;

            await Task.Delay(50, timeout.Token);
        }

        Assert.Fail(failureMessage);
    }
}
