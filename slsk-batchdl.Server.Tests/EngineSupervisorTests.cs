using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Jobs;
using Sldl.Core.Settings;
using Sldl.Server;
using System.Collections.Concurrent;

namespace Tests.Server;

[TestClass]
public class EngineSupervisorTests
{
    private static string ToSoulseekPath(string path) => path.Replace('/', '\\');

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
            Assert.AreEqual(ToSoulseekPath(albumDir), albums.Items[0].FolderPath);

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
    public async Task StartAlbumDownloadAsync_CancelWorkflowMarksUnfinishedPayloadFilesCancelled()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
            File.WriteAllBytes(Path.Combine(albumDir, $"{i:00}. Artist - Track {i:00}.mp3"), new byte[1024]);

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(
                musicRoot,
                outputDir,
                configureDownload: settings => settings.Search.NoBrowseFolder = true,
                configureEngine: settings => settings.MockFilesSlow = true);
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

            var downloadSummary = await supervisor.StartAlbumDownloadAsync(
                searchSummary.JobId,
                new StartAlbumDownloadRequestDto(albums.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);

            await WaitForConditionAsync(
                () =>
                {
                    var detail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
                    var payload = detail?.Payload as AlbumJobPayloadDto;
                    return payload?.Results?.SelectMany(folder => folder.Files ?? [])
                        .Any(file => file.State == "Downloading") == true;
                },
                "Timed out waiting for album file downloads to start.");

            var cancelled = supervisor.CancelWorkflow(downloadSummary.WorkflowId);
            Assert.IsTrue(cancelled > 0, "CancelWorkflow should cancel the active album download job.");

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, "Failed");

            var cancelledDetail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(cancelledDetail);
            var cancelledPayload = cancelledDetail.Payload as AlbumJobPayloadDto;
            Assert.IsNotNull(cancelledPayload);

            var files = cancelledPayload.Results?.SelectMany(folder => folder.Files ?? []).ToList() ?? [];
            Assert.AreEqual(12, files.Count);
            Assert.IsFalse(
                files.Any(file => file.State is "Pending" or "Searching" or "Downloading"),
                "Cancelled album payload should not expose stale active file states.");
            Assert.IsTrue(
                files.Any(file => file.State == "Failed" && file.FailureReason == "Cancelled"),
                "Cancelled album payload should mark unfinished files as cancelled.");

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

    [TestMethod]
    public async Task StartExtractedResultAsync_InteractiveAlbumResultStartsSearchJob()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Extraction.IsAlbum = true;
                settings.Search.NoBrowseFolder = true;
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var extractSummary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "extract",
                        Input = "Artist Album",
                        InputType = "String",
                        AutoStartExtractedResult = false,
                    }),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, extractSummary.JobId, "Done");

            var started = await supervisor.StartExtractedResultAsync(
                extractSummary.JobId,
                new StartExtractedResultRequestDto(Interactive: true),
                CancellationToken.None);

            Assert.IsNotNull(started);
            Assert.AreEqual(1, started.Count);
            Assert.AreEqual("search", started[0].Kind);
            Assert.AreEqual(extractSummary.WorkflowId, started[0].WorkflowId);

            await WaitForJobStateAsync(supervisor, started[0].JobId, "Done");

            var albums = supervisor.GetAlbumProjection(started[0].JobId, includeFiles: true);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);
            Assert.AreEqual(ToSoulseekPath(albumDir), albums.Items[0].FolderPath);

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
    public async Task SubmitJobAsync_AppliesServerAutoProfileFromClientContext()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "sldl-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();

        try
        {
            var profile = CreateProfile("my-interactive", settings => settings.Search.MaxStaleTime = 9999999)
                with { Condition = "interactive && album" };
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-album",
                        AlbumQuery = new AlbumQueryDto("Artist", "Album", "", "", false, false, -1, -1),
                    },
                    new SubmissionOptionsDto(ProfileContext: new Dictionary<string, bool>
                    {
                        ["interactive"] = true,
                    })),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, "Done");

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(9999999, job.Config?.Search.MaxStaleTime);
            CollectionAssert.Contains(job.Config?.AppliedAutoProfiles?.ToList(), "my-interactive");

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
    public async Task SubmitJobAsync_AppliesServerNamedProfile()
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
            var named = CreateProfile("long-search", settings => settings.Search.MaxStaleTime = 123456);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var profiles = supervisor.GetProfiles();
            Assert.AreEqual(1, profiles.Count);
            Assert.AreEqual("long-search", profiles[0].Name);

            var summary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    },
                    new SubmissionOptionsDto(ProfileNames: ["long-search"])),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, "Done");

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(123456, job.Config?.Search.MaxStaleTime);

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
    public async Task SubmitJobAsync_AppliesClientDownloadSettingsDeltaAfterProfiles()
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
            var named = CreateProfile("short-search", settings => settings.Search.MaxStaleTime = 111);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var baseline = new DownloadSettings();
            var cliSettings = SettingsCloner.Clone(baseline);
            cliSettings.Search.MaxStaleTime = 222;
            cliSettings.Search.NecessaryCond.Formats = ["flac"];

            var summary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    },
                    new SubmissionOptionsDto(
                        ProfileNames: ["short-search"],
                        DownloadSettings: DownloadSettingsDeltaMapper.FromDifference(baseline, cliSettings))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, "Done");

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(222, job.Config?.Search.MaxStaleTime);
            CollectionAssert.AreEqual(new[] { "flac" }, job.Config?.Search.NecessaryCond.Formats);

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
    public async Task SubmitJobAsync_ClientDeltaCanSetBuiltInDefaultValueOverProfile()
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
            var named = CreateProfile("no-skip", settings => settings.Skip.SkipExisting = false);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    },
                    new SubmissionOptionsDto(
                        ProfileNames: ["no-skip"],
                        DownloadSettings: new DownloadSettingsDeltaDto([
                            DownloadSettingsDeltaMapper.Set("Skip.SkipExisting", true),
                        ]))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, "Done");

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.IsTrue(job.Config?.Skip.SkipExisting);

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
    public async Task SubmitJobAsync_ClientDeltaCanAppendToProfileListSettings()
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
            var named = CreateProfile("base-command", settings => settings.Output.OnComplete = ["first"]);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            var runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "search-track",
                        SongQuery = new SongQueryDto("Artist", "Track One", "", "", -1, false, false),
                    },
                    new SubmissionOptionsDto(
                        ProfileNames: ["base-command"],
                        DownloadSettings: new DownloadSettingsDeltaDto([
                            DownloadSettingsDeltaMapper.Append("Output.OnComplete", ["second"]),
                        ]))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, "Done");

            var job = supervisor.StateStore.GetJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            CollectionAssert.AreEqual(new[] { "first", "second" }, job.Config?.Output.OnComplete);

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

    private static EngineSupervisor CreateSupervisor(
        string musicRoot,
        string outputDir,
        Action<DownloadSettings>? configureDownload = null,
        Action<EngineSettings>? configureEngine = null,
        ProfileCatalog? profiles = null)
    {
        var engineSettings = new EngineSettings
        {
            MockFilesDir = musicRoot,
            MockFilesReadTags = false,
        };
        configureEngine?.Invoke(engineSettings);

        var defaultDownload = new DownloadSettings
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
            Engine = engineSettings,
            DefaultDownload = defaultDownload,
            Profiles = profiles ?? ProfileCatalog.Empty,
        });

        return new EngineSupervisor(options);
    }

    private static SettingsProfile CreateProfile(string name, Action<DownloadSettings> applyDownload)
    {
        var patch = new DownloadSettingsPatch();
        patch.Add(applyDownload);
        return new SettingsProfile
        {
            Name = name,
            Download = patch,
        };
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
