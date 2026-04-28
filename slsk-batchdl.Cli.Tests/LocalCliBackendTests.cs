using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Cli;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Server;
using System.Collections.Concurrent;

namespace Tests.Cli;

[TestClass]
public class LocalCliBackendTests
{
    [TestMethod]
    public async Task LocalCliBackend_ObservesSearchJobsThroughServerShapedModel()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-cli-backend-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        Directory.CreateDirectory(trackDir);
        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = musicRoot,
                    FailedAlbumPath = Path.Combine(musicRoot, "failed"),
                },
            };
            downloadSettings.Extraction.Input = "test";
            var clientManager = new SoulseekClientManager(engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine, downloadSettings);
            var seenEvents = new ConcurrentBag<ServerEventEnvelopeDto>();
            backend.EventReceived += envelope => seenEvents.Add(envelope);

            var submitted = await backend.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false, false)),
                cts.Token);
            engine.CompleteEnqueue();

            await engine.RunAsync(cts.Token);

            var jobs = await backend.GetJobsAsync(new JobQuery(null, "search", null, CanonicalRootsOnly: false, IncludeNonDefault: true));
            Assert.AreEqual(1, jobs.Count);
            Assert.AreEqual(submitted.JobId, jobs[0].JobId);

            var projection = await backend.GetTrackResultsAsync(submitted.JobId);
            Assert.IsNotNull(projection);
            Assert.AreEqual(1, projection.Items.Count);

            Assert.IsTrue(seenEvents.Any(e => e.Type == "job.upserted"));
            Assert.IsTrue(seenEvents.Any(e => e.Type == "workflow.upserted"));
            Assert.IsTrue(seenEvents.Any(e => e.Type == "search.updated"));
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task LocalCliBackend_RetrieveFolderAndWaitAsync_ReturnsNewFilesFoundCount()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-cli-backend-retrieve-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        Directory.CreateDirectory(albumDir);
        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = musicRoot,
                    FailedAlbumPath = Path.Combine(musicRoot, "failed"),
                },
            };
            downloadSettings.Search.NecessaryCond.StrictTitle = true;

            var clientManager = new SoulseekClientManager(engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine);

            var searchJob = new SearchJob(new AlbumQuery
            {
                Artist = "Artist",
                Album = "Album",
                SearchHint = "Track One",
            });

            engine.Enqueue(searchJob, downloadSettings);
            var runTask = engine.RunAsync(cts.Token);

            await WaitForConditionAsync(
                () => searchJob.State == JobState.Done,
                "Timed out waiting for the album search to complete.");

            var initialProjection = await backend.GetAlbumResultsAsync(searchJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(initialProjection);
            Assert.AreEqual(1, initialProjection.Items.Count);
            Assert.AreEqual(1, initialProjection.Items[0].Files?.Count);

            var foundCount = await backend.RetrieveFolderAndWaitAsync(
                searchJob.Id,
                new RetrieveFolderRequestDto(initialProjection.Items[0].Ref),
                cts.Token);

            Assert.AreEqual(1, foundCount);

            var expandedProjection = await backend.GetAlbumResultsAsync(searchJob.Id, includeFiles: true, cts.Token);
            Assert.IsNotNull(expandedProjection);
            Assert.AreEqual(2, expandedProjection.Items[0].Files?.Count);

            engine.CompleteEnqueue();
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
    public async Task LocalCliBackend_PublishesSharedProgressEvents_ForSongDownload()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-cli-backend-progress-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "sldl-cli-backend-progress-out-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var engineSettings = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            };
            var downloadSettings = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                    FailedAlbumPath = Path.Combine(outputDir, "failed"),
                },
            };

            var clientManager = new SoulseekClientManager(engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var backend = new LocalCliBackend(engine, downloadSettings);
            var seenTypes = new ConcurrentBag<string>();
            backend.EventReceived += envelope => seenTypes.Add(envelope.Type);

            await backend.SubmitSongJobAsync(
                new SubmitSongJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false, false)),
                cts.Token);

            engine.CompleteEnqueue();
            await engine.RunAsync(cts.Token);

            Assert.IsTrue(seenTypes.Contains("job.upserted"));
            Assert.IsTrue(seenTypes.Contains("song.searching"));
            Assert.IsTrue(seenTypes.Contains("download.started"));
            Assert.IsTrue(seenTypes.Contains("download.state-changed"));
            Assert.IsTrue(seenTypes.Contains("song.state-changed"));
        }
        finally
        {
            cts.Cancel();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        Assert.Fail(failureMessage);
    }
}
