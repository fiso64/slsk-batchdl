using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;
using System.Collections.Concurrent;

namespace Tests.Server;

[TestClass]
public class EngineSupervisorTests
{
    [TestMethod]
    public async Task SubmitSearchJobAsync_RejectsOversizedQueryWithoutCreatingWorkflow()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        var outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(outputDir);

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var oversized = new string('x', JobRequestMapper.MaxSearchTextLength + 1);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.SubmitSearchJobAsync(new SubmitSearchJobRequestDto(oversized), CancellationToken.None));

            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFileDownloadsAsync_ReusesWorkflowAndSetsSourceJob()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var tracks = supervisor.GetFileResults(searchSummary.JobId);
            Assert.IsNotNull(tracks);
            Assert.AreEqual(1, tracks.Items.Count);

            var downloadSummary = await supervisor.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto([tracks.Items[0].Ref]),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(1, downloadSummary.Count);
            var downloadedSummary = downloadSummary[0];
            Assert.AreEqual(searchSummary.WorkflowId, downloadedSummary.WorkflowId);
            Assert.IsNull(downloadedSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadedSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, downloadedSummary.JobId, ExpectedJobStatus.Succeeded);

            var detail = supervisor.StateStore.GetJobDetail(downloadedSummary.JobId);
            Assert.IsNotNull(detail);
            Assert.IsNull(detail.Summary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, detail.Summary.SourceJobId);

            var downloaded = Directory.GetFiles(outputDir, "*.mp3", SearchOption.AllDirectories);
            Assert.AreEqual(1, downloaded.Length);
            Assert.IsTrue(downloaded[0].EndsWith("01. Artist - Track One.mp3", StringComparison.OrdinalIgnoreCase));

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_ReusesWorkflowAndFindsAlbumByFolderPath()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var albums = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);
            Assert.AreEqual("local", albums.Items[0].Username);
            Assert.AreEqual(@"Artist\Album", albums.Items[0].FolderPath);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(albums.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            Assert.AreEqual(searchSummary.WorkflowId, downloadSummary.WorkflowId);
            Assert.IsNull(downloadSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, downloadSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Succeeded);

            var detail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(detail);
            Assert.IsNull(detail.Summary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, detail.Summary.SourceJobId);

            var workflowTree = supervisor.StateStore.GetWorkflowTree(searchSummary.WorkflowId);
            Assert.IsNotNull(workflowTree);
            Assert.AreEqual(2, workflowTree.Jobs.Count);
            CollectionAssert.AreEquivalent(
                new[] { searchSummary.JobId, downloadSummary.JobId },
                workflowTree.Jobs.Select(job => job.Summary.JobId).ToArray());

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { "01. Track One.mp3", "02. Track Two.mp3" },
                downloaded,
                "Actual output files: " + string.Join(", ", downloaded));

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFileDownloadsAsync_CanDownloadSingleFileFromAlbumSearch()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var files = supervisor.GetFileResults(searchSummary.JobId);
            Assert.IsNotNull(files);
            var selected = files.Items.Single(file => file.Filename.EndsWith("02. Track Two.mp3", StringComparison.OrdinalIgnoreCase));

            var downloads = await supervisor.StartFileDownloadsAsync(
                searchSummary.JobId,
                new StartFileDownloadsRequestDto([selected.Ref]),
                CancellationToken.None);

            Assert.IsNotNull(downloads);
            Assert.AreEqual(1, downloads.Count);
            Assert.AreEqual(searchSummary.WorkflowId, downloads[0].WorkflowId);

            await WaitForJobStateAsync(supervisor, downloads[0].JobId, ExpectedJobStatus.Succeeded);

            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(x => x)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "02. Track Two.mp3" }, downloaded);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_DoesNotInheritSearchSubmissionSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Search.NoBrowseFolder = false;
            });
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false),
                    Options: new SubmissionOptionsDto(
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Search: new SearchSettingsPatchDto(NoBrowseFolder: true)))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);
            var searchJob = supervisor.GetRuntimeJob<SearchJob>(searchSummary.JobId);
            Assert.IsNotNull(searchJob);
            Assert.IsTrue(searchJob.Config?.Search.NoBrowseFolder);

            var folders = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(folders);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(folders.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            await WaitForConditionAsync(
                () => supervisor.GetRuntimeJob<AlbumJob>(downloadSummary.JobId)?.Config != null,
                "Timed out waiting for album download settings.");

            var albumJob = supervisor.GetRuntimeJob<AlbumJob>(downloadSummary.JobId);
            Assert.IsNotNull(albumJob);
            Assert.IsFalse(albumJob.Config?.Search.NoBrowseFolder, "Download should use default settings, not the search submission delta.");

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Succeeded);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobListAsync_AppliesChildDraftDownloadSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(trackDir, "Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "configured children",
                    [
                        new TrackSearchJobDraftDto(
                            new SongQueryDto("Artist", "Track One", "", "", -1, false),
                            DownloadSettings: new DownloadSettingsPatchDto(
                                Search: new SearchSettingsPatchDto(NoBrowseFolder: true),
                                Transfer: new TransferSettingsPatchDto(MaxStaleTime: 111))),
                        new TrackSearchJobDraftDto(
                            new SongQueryDto("Artist", "Track Two", "", "", -1, false),
                            DownloadSettings: new DownloadSettingsPatchDto(
                                Search: new SearchSettingsPatchDto(NoBrowseFolder: false),
                                Transfer: new TransferSettingsPatchDto(MaxStaleTime: 222))),
                    ]),
                CancellationToken.None);

            await WaitForConditionAsync(
                () =>
                {
                    var children = SearchChildren(supervisor, summary.WorkflowId);
                    return children.Count == 2
                        && children.All(child => supervisor.GetRuntimeJob<SearchJob>(child.JobId)?.Config != null);
                },
                "Timed out waiting for job-list child settings.");

            var children = SearchChildren(supervisor, summary.WorkflowId).ToList();
            Assert.AreEqual(2, children.Count);

            var first = SearchChildByQuery(supervisor, children, "Track One");
            var second = SearchChildByQuery(supervisor, children, "Track Two");

            Assert.AreEqual(111, first.Config.Transfer.MaxStaleTime);
            Assert.IsTrue(first.Config.Search.NoBrowseFolder);
            Assert.AreEqual(222, second.Config.Transfer.MaxStaleTime);
            Assert.IsFalse(second.Config.Search.NoBrowseFolder);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_CancelWorkflowMarksUnfinishedPayloadFilesCancelled()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        for (int i = 1; i <= 12; i++)
            File.WriteAllBytes(Path.Combine(albumDir, $"{i:00}. Artist - Track {i:00}.mp3"), new byte[1024]);

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(
                musicRoot,
                outputDir,
                configureDownload: settings => settings.Search.NoBrowseFolder = true,
                configureEngine: settings => settings.MockFilesSlow = true);
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var albums = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: false);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(albums.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);

            await WaitForConditionAsync(
                () =>
                {
                    return GetChildSongPayloads(supervisor, downloadSummary.JobId)
                        .Any(file => ProjectState(file) == ExpectedJobStatus.Downloading) == true;
                },
                "Timed out waiting for album file downloads to start.");

            var activeDetail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(activeDetail);
            var activeFiles = GetChildSongPayloads(supervisor, downloadSummary.JobId);
            var activeAlbumPayload = activeDetail.Payload as AlbumJobPayloadDto;
            Assert.IsNotNull(activeAlbumPayload);
            Assert.IsTrue(activeAlbumPayload.Directory.FileCount >= activeFiles.Count);
            var cancellableFile = activeFiles.FirstOrDefault(file =>
                file.AvailableActions?.Any(action => action.Kind == ServerResourceActionKind.Cancel) == true);
            Assert.IsNotNull(cancellableFile, "Active album payload files should expose cancel actions.");

            var childSongJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, null, ServerJobKind.Song, downloadSummary.WorkflowId, IncludeAll: true))
                .Where(summary => summary.ParentJobId == downloadSummary.JobId)
                .ToList();
            Assert.IsTrue(childSongJobs.Count > 0, "Album payload songs should be registered jobs.");
            Assert.IsFalse(
                supervisor.StateStore.GetJobs(new JobQuery(null, null, ServerJobKind.Song, downloadSummary.WorkflowId, IncludeAll: false))
                    .Any(summary => summary.ParentJobId == downloadSummary.JobId),
                "Album payload songs should stay out of the default job list.");

            Assert.IsNotNull(cancellableFile.JobId);
            Assert.IsTrue(supervisor.CancelJob(cancellableFile.JobId.Value), "Album payload file should be cancellable by job id.");
            await WaitForConditionAsync(
                () =>
                {
                    return GetChildSongPayloads(supervisor, downloadSummary.JobId)
                        .Any(file => file.JobId == cancellableFile.JobId
                            && file.TerminalOutcome == ServerJobTerminalOutcome.Cancelled
                            && file.FailureReason == ServerProtocol.FailureReasons.Cancelled) == true;
                },
                "Timed out waiting for album file cancellation.");

            var cancelledAlbumPayload = supervisor.StateStore.GetJobDetail(downloadSummary.JobId)?.Payload as AlbumJobPayloadDto;
            Assert.IsNotNull(cancelledAlbumPayload);
            Assert.IsTrue(cancelledAlbumPayload.Directory.FileCount >= activeFiles.Count);
            Assert.IsTrue(cancelledAlbumPayload.Directory.TerminalFileCount >= 1);
            Assert.IsTrue(cancelledAlbumPayload.Directory.FailedFileCount >= 1);

            var cancelled = supervisor.CancelWorkflow(downloadSummary.WorkflowId);
            Assert.IsTrue(cancelled > 0, "CancelWorkflow should cancel the active album download job.");

            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Failed);

            var cancelledDetail = supervisor.StateStore.GetJobDetail(downloadSummary.JobId);
            Assert.IsNotNull(cancelledDetail);
            var files = GetChildSongPayloads(supervisor, downloadSummary.JobId);
            Assert.AreEqual(12, files.Count);
            Assert.IsFalse(
                files.Any(IsActive),
                "Cancelled album payload should not expose stale active file states.");
            Assert.IsTrue(
                files.Any(file => file.TerminalOutcome == ServerJobTerminalOutcome.Cancelled
                    && file.FailureReason == ServerProtocol.FailureReasons.Cancelled),
                "Cancelled album payload should mark unfinished files as cancelled.");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesJobAndWorkflowUpserts_ForSubmittedJobs()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var seenJobIds = new ConcurrentBag<Guid>();
            var seenWorkflowIds = new ConcurrentBag<Guid>();
            supervisor.StateStore.JobUpserted += summary => seenJobIds.Add(summary.JobId);
            supervisor.StateStore.WorkflowUpserted += summary => seenWorkflowIds.Add(summary.WorkflowId);

            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            CollectionAssert.Contains(seenJobIds.ToList(), searchSummary.JobId);
            CollectionAssert.Contains(seenWorkflowIds.ToList(), searchSummary.WorkflowId);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StateStore_RaisesSearchUpdated_ForSearchJobResultsAndCompletion()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var updates = new ConcurrentBag<SearchStateDto>();
            supervisor.StateStore.SearchUpdated += update => updates.Add(update);

            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);
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
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartRetrieveFolderAsync_CompletesQueuedRetrieveJobAndPreservesWorkflow()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Artist - Track One.mp3"), "a");
        File.WriteAllText(Path.Combine(albumDir, "02. Artist - Track Two.mp3"), "b");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Search.NecessaryCond.StrictTitle = true;
            });
            runTask = supervisor.RunAsync(cts.Token);

            var searchSummary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "Track One", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, searchSummary.JobId, ExpectedJobStatus.Succeeded);

            var beforeRetrieve = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: true);
            Assert.IsNotNull(beforeRetrieve);
            Assert.AreEqual(1, beforeRetrieve.Items.Count);
            Assert.AreEqual(1, beforeRetrieve.Items[0].Files?.Count);

            var retrieveSummary = await supervisor.StartRetrieveFolderAsync(
                searchSummary.JobId,
                new RetrieveFolderRequestDto(beforeRetrieve.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(retrieveSummary);
            Assert.AreEqual(searchSummary.WorkflowId, retrieveSummary.WorkflowId);
            Assert.IsNull(retrieveSummary.ParentJobId);
            Assert.AreEqual(searchSummary.JobId, retrieveSummary.SourceJobId);

            await WaitForJobStateAsync(supervisor, retrieveSummary.JobId, ExpectedJobStatus.Succeeded);

            var retrieveDetail = supervisor.StateStore.GetJobDetail(retrieveSummary.JobId);
            Assert.IsNotNull(retrieveDetail);
            var payload = retrieveDetail.Payload as RetrieveFolderJobPayloadDto;
            Assert.IsNotNull(payload);
            Assert.AreEqual(
                1,
                payload.NewFilesFoundCount,
                "Retrieved folder files: " + string.Join(", ", payload.Folder?.Files?.Select(file => file.Filename) ?? []));

            var workflowTree = supervisor.StateStore.GetWorkflowTree(searchSummary.WorkflowId);
            Assert.IsNotNull(workflowTree);
            Assert.AreEqual(2, workflowTree.Jobs.Count);
            CollectionAssert.AreEquivalent(
                new[] { searchSummary.JobId, retrieveSummary.JobId },
                workflowTree.Jobs.Select(job => job.Summary.JobId).ToArray());

            var afterRetrieve = supervisor.GetFolderResults(searchSummary.JobId, includeFiles: true);
            Assert.IsNotNull(afterRetrieve);
            Assert.AreEqual(2, afterRetrieve.Items[0].Files?.Count);
            Assert.IsTrue(afterRetrieve.Items[0].IsFullyRetrieved);

            var downloadSummary = await supervisor.StartFolderDownloadAsync(
                searchSummary.JobId,
                new StartFolderDownloadRequestDto(afterRetrieve.Items[0].Ref),
                CancellationToken.None);

            Assert.IsNotNull(downloadSummary);
            await WaitForJobStateAsync(supervisor, downloadSummary.JobId, ExpectedJobStatus.Succeeded);

            var retrieveJobs = supervisor.StateStore.GetJobs(
                new JobQuery(null, null, ServerJobKind.RetrieveFolder, searchSummary.WorkflowId, IncludeAll: true));
            Assert.AreEqual(1, retrieveJobs.Count, "Starting a download from a fully retrieved folder should not browse it again.");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task ExtractJobPayload_ExposesResultDraftForTypedResubmission()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, settings =>
            {
                settings.Extraction.IsAlbum = true;
                settings.Search.NoBrowseFolder = true;
            });
            runTask = supervisor.RunAsync(cts.Token);

            var extractSummary = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    "Artist Album",
                    "String",
                    AutoStartExtractedResult: false),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, extractSummary.JobId, ExpectedJobStatus.Succeeded);

            var extractDetail = supervisor.StateStore.GetJobDetail(extractSummary.JobId);
            Assert.IsNotNull(extractDetail);
            var extractPayload = extractDetail.Payload as ExtractJobPayloadDto;
            Assert.IsNotNull(extractPayload);
            var albumDraft = extractPayload.ResultDraft as AlbumJobDraftDto;
            Assert.IsNotNull(albumDraft);

            var started = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    albumDraft.AlbumQuery,
                    new SubmissionOptionsDto(WorkflowId: extractSummary.WorkflowId)),
                CancellationToken.None);

            Assert.AreEqual(ServerJobKind.Search, started.Kind);
            Assert.AreEqual(extractSummary.WorkflowId, started.WorkflowId);

            await WaitForJobStateAsync(supervisor, started.JobId, ExpectedJobStatus.Succeeded);

            var workflowTree = supervisor.StateStore.GetWorkflowTree(extractSummary.WorkflowId);
            Assert.IsNotNull(workflowTree);
            Assert.AreEqual(2, workflowTree.Jobs.Count);
            Assert.IsTrue(workflowTree.Jobs.Any(job => job.Summary.JobId == extractSummary.JobId));
            Assert.IsTrue(workflowTree.Jobs.Any(job => job.Summary.JobId == started.JobId));

            var albums = supervisor.GetFolderResults(started.JobId, includeFiles: true);
            Assert.IsNotNull(albums);
            Assert.AreEqual(1, albums.Items.Count);
            Assert.AreEqual(@"Artist\Album", albums.Items[0].FolderPath);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesServerAutoProfileFromClientContext()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var profile = CreateProfile("my-interactive", settings => settings.Transfer.MaxStaleTime = 9999999)
                with
            { Condition = "interactive && album" };
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false),
                    new SubmissionOptionsDto(ProfileContext: new Dictionary<string, bool>
                    {
                        ["interactive"] = true,
                    })),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(9999999, job.Config?.Transfer.MaxStaleTime);
            CollectionAssert.Contains(job.Config?.AppliedAutoProfiles?.ToList(), "my-interactive");

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ExpandsServerProfileOutputPaths()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var profile = CreateProfile("album-inbox", settings => settings.Output.ParentDir = "~/Music/Inbox")
                with
            { Condition = "album" };
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                AutoProfiles = [profile],
                NamedProfiles = [profile],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Inbox")),
                job.Config?.Output.ParentDir);
            Assert.IsFalse(job.Config?.Output.ParentDir?.Contains('~') == true);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_LaunchDownloadSettingsOverrideServerAutoProfiles()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string albumDir = Path.Combine(musicRoot, "Artist", "Album");
        string outputDir = Path.Combine(musicRoot, "out");
        string launchOutputDir = Path.Combine(musicRoot, "launch-out");
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(launchOutputDir);

        File.WriteAllText(Path.Combine(albumDir, "01. Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var profile = CreateProfile("album-profile", settings =>
                {
                    settings.Output.ParentDir = "~/Music/Inbox";
                    settings.Transfer.MaxStaleTime = 999999;
                })
                with
            { Condition = "album" };
            var supervisor = CreateSupervisor(
                musicRoot,
                outputDir,
                configureDownload: settings => settings.Transfer.MaxStaleTime = 111,
                profiles: new ProfileCatalog
                {
                    AutoProfiles = [profile],
                    NamedProfiles = [profile],
                },
                launchDownloadSettings: new DownloadSettingsPatchDto(
                    Output: new OutputSettingsPatchDto(ParentDir: launchOutputDir),
                    Transfer: new TransferSettingsPatchDto(MaxStaleTime: 222)));
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Artist", "Album", "", "", false)),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(Path.GetFullPath(launchOutputDir), job.Config?.Output.ParentDir);
            Assert.AreEqual(222, job.Config?.Transfer.MaxStaleTime);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesServerNamedProfile()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("long-search", settings => settings.Transfer.MaxStaleTime = 123456);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var profiles = supervisor.GetProfiles();
            Assert.AreEqual(1, profiles.Count);
            Assert.AreEqual("long-search", profiles[0].Name);

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(ProfileNames: ["long-search"])),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(123456, job.Config?.Transfer.MaxStaleTime);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_AppliesClientDownloadSettingsPatchAfterProfiles()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("short-search", settings => settings.Transfer.MaxStaleTime = 111);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var baseline = new DownloadSettings();
            var cliSettings = SettingsCloner.Clone(baseline);
            cliSettings.Transfer.MaxStaleTime = 222;
            cliSettings.Search.NecessaryCond.Formats = ["flac"];

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["short-search"],
                        DownloadSettings: DownloadSettingsPatchDtoMapper.FromDifference(baseline, cliSettings))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(222, job.Config?.Transfer.MaxStaleTime);
            CollectionAssert.AreEqual(new[] { "flac" }, job.Config?.Search.NecessaryCond.Formats);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ClientDeltaCanSetBuiltInDefaultValueOverProfile()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("no-skip", settings => settings.Skip.SkipExisting = false);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["no-skip"],
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Skip: new SkipSettingsPatchDto(SkipExisting: true)))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            Assert.IsTrue(job.Config?.Skip.SkipExisting);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task SubmitJobAsync_ClientDeltaCanAppendToProfileListSettings()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-server-test-" + Guid.NewGuid());
        string trackDir = Path.Combine(musicRoot, "Artist");
        string outputDir = Path.Combine(musicRoot, "out");
        Directory.CreateDirectory(trackDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(Path.Combine(trackDir, "Artist - Track One.mp3"), "a");

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        try
        {
            var named = CreateProfile("base-command", settings => settings.Output.OnComplete = ["-- first"]);
            var supervisor = CreateSupervisor(musicRoot, outputDir, profiles: new ProfileCatalog
            {
                NamedProfiles = [named],
            });
            runTask = supervisor.RunAsync(cts.Token);

            var summary = await supervisor.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(
                    new SongQueryDto("Artist", "Track One", "", "", -1, false),
                    Options: new SubmissionOptionsDto(
                        ProfileNames: ["base-command"],
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(
                                OnComplete: new CollectionPatchDto<string>(Append: ["-- second"]))))),
                CancellationToken.None);

            await WaitForJobStateAsync(supervisor, summary.JobId, ExpectedJobStatus.Succeeded);

            var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
            Assert.IsNotNull(job);
            CollectionAssert.AreEqual(new[] { "-- first", "-- second" }, job.Config?.Output.OnComplete);

            cts.Cancel();
            await runTask;
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, true);
        }
    }

    [TestMethod]
    public async Task StartFileDownloadsAsync_GeneralIntentCreatesRemoteFileJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-general-file-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(Path.Combine(root, "Artist"));
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(root, "Artist", "Track.mp3"), "data");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(root, output);
            runTask = supervisor.RunAsync(cts.Token);
            var search = await supervisor.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("Track.mp3"),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, search.JobId, ExpectedJobStatus.Succeeded);
            var files = supervisor.GetFileResults(search.JobId);
            Assert.IsNotNull(files);

            var downloads = await supervisor.StartFileDownloadsAsync(
                search.JobId,
                new StartFileDownloadsRequestDto(
                    [files.Items.Single().Ref],
                    RequestedMode: ExtractionMode.General),
                CancellationToken.None);

            Assert.IsNotNull(downloads);
            Assert.AreEqual(1, downloads.Count);
            Assert.AreEqual(ServerJobKind.RemoteFile, downloads[0].Kind);
            await WaitForJobStateAsync(supervisor, downloads[0].JobId, ExpectedJobStatus.Succeeded);
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartFolderDownloadAsync_OrdinaryRemoteIntentCreatesRemoteDirectoryJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-remote-directory-" + Guid.NewGuid());
        string album = Path.Combine(root, "Organization", "Folder");
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(album, "One.mp3"), "1");
        File.WriteAllText(Path.Combine(album, "Two.mp3"), "2");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(root, output);
            runTask = supervisor.RunAsync(cts.Token);
            var search = await supervisor.SubmitAlbumSearchJobAsync(
                new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto("Organization", "Folder", "", "", false)),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, search.JobId, ExpectedJobStatus.Succeeded);
            var folders = supervisor.GetFolderResults(search.JobId, includeFiles: true);
            Assert.IsNotNull(folders);

            var download = await supervisor.StartFolderDownloadAsync(
                search.JobId,
                new StartFolderDownloadRequestDto(
                    folders.Items.Single().Ref,
                    SelectedFolder: folders.Items.Single(),
                    RequestedMode: ExtractionMode.General),
                CancellationToken.None);

            Assert.IsNotNull(download);
            await WaitForJobStateAsync(supervisor, download.JobId, ExpectedJobStatus.Succeeded);
            Assert.IsNotNull(supervisor.GetRuntimeJob<RemoteDirectoryJob>(download.JobId));
            Assert.IsNull(supervisor.GetRuntimeJob<AlbumJob>(download.JobId));
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SubmitJobListAsync_RemoteDraftRejectsMusicOnlyOverrideBeforeAdmission()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-remote-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            var request = new SubmitJobListRequestDto(
                "invalid remote settings",
                [
                    new RemoteFileJobDraftDto(
                        new PeerFileTargetDto("Peer", @"Share\File.bin", 4, ".bin"),
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(WritePlaylist: true))),
                ]);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.SubmitJobListAsync(request, CancellationToken.None));
            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SubmitJobListAsync_RemoteDraftAllowsPathBasedSkipExistingOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-remote-skip-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            var target = new PeerFileTargetDto("Peer", @"Share\File.bin", 4, ".bin");

            JobSummaryDto accepted = await supervisor.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "valid remote settings",
                    [new RemoteFileJobDraftDto(
                        target,
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Skip: new SkipSettingsPatchDto(SkipExisting: false)))]),
                CancellationToken.None);

            Assert.AreEqual(ServerJobKind.JobList, accepted.Kind);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => supervisor.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "invalid remote settings",
                    [new RemoteFileJobDraftDto(
                        target,
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Skip: new SkipSettingsPatchDto(SkipMode: SkipMode.Name)))]),
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SoulseekDraftSettingsValidation_UsesTheEffectiveInterpretation()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => supervisor.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "ordinary",
                    [new ExtractJobDraftDto(
                        "slsk://Peer/Share/File.bin",
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(WritePlaylist: true)))]),
                CancellationToken.None));

            JobSummaryDto accepted = await supervisor.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "music",
                    [new ExtractJobDraftDto(
                        "slsk://Peer/Share/File.mp3",
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Output: new OutputSettingsPatchDto(
                                NameFormat: "{artist}/{title}",
                                WritePlaylist: true),
                            Extraction: new ExtractionSettingsPatchDto(RequestedMode: ExtractionMode.Song)))]),
                CancellationToken.None);

            Assert.AreEqual(ServerJobKind.JobList, accepted.Kind);
            Assert.AreNotEqual(Guid.Empty, accepted.WorkflowId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SoulseekLink_InheritedMusicNameFormatFallsBackToRemoteFilename()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-inherited-format-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        string source = Path.Combine(root, "Share", "File.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(source, "data");
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(
                root,
                output,
                settings => settings.Output.NameFormat = "{artist}/{filename}");
            runTask = supervisor.RunAsync(cts.Token);

            var submitted = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(ToSoulseekFileUri(source), "Soulseek"),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, submitted.JobId, ExpectedJobStatus.Succeeded);

            var extract = supervisor.GetRuntimeJob<ExtractJob>(submitted.JobId);
            var remote = extract?.Result as RemoteFileJob;
            Assert.IsNotNull(remote);
            await WaitForJobStateAsync(supervisor, remote.Id, ExpectedJobStatus.Succeeded);
            Assert.AreEqual("", remote.Config.Output.NameFormat);
            Assert.IsTrue(File.Exists(Path.Combine(output, "File.bin")));
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SoulseekLink_GenericAutoProfileOverridesInheritedMusicNameFormat()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-auto-format-" + Guid.NewGuid());
        string output = Path.Combine(root, "out");
        string source = Path.Combine(root, "Share", "File.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(source, "data");
        var remotePatch = new DownloadSettingsPatch();
        remotePatch.Add(settings =>
            settings.Output.NameFormat = "{peer-username}/{filename}");
        var profiles = new ProfileCatalog
        {
            AutoProfiles =
            [
                new SettingsProfile
                {
                    Name = "generic-file-layout",
                    Condition = "download-mode == \"generic-file\"",
                    Download = remotePatch,
                },
            ],
        };
        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;

        try
        {
            var supervisor = CreateSupervisor(
                root,
                output,
                settings => settings.Output.NameFormat = "{artist}/{filename}",
                profiles: profiles);
            runTask = supervisor.RunAsync(cts.Token);

            var submitted = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(ToSoulseekFileUri(source), "Soulseek"),
                CancellationToken.None);
            await WaitForJobStateAsync(supervisor, submitted.JobId, ExpectedJobStatus.Succeeded);

            var extract = supervisor.GetRuntimeJob<ExtractJob>(submitted.JobId);
            var remote = extract?.Result as RemoteFileJob;
            Assert.IsNotNull(remote);
            await WaitForJobStateAsync(supervisor, remote.Id, ExpectedJobStatus.Succeeded);
            Assert.AreEqual("{peer-username}/{filename}", remote.Config.Output.NameFormat);
            CollectionAssert.Contains(remote.Config.AppliedAutoProfiles.ToList(), "generic-file-layout");
            string expectedPath = Path.Combine(output, "local", "File.bin");
            Assert.IsTrue(
                File.Exists(expectedPath),
                $"Expected '{expectedPath}'. Actual files: {string.Join(", ", Directory.GetFiles(output, "*", SearchOption.AllDirectories))}");
        }
        finally
        {
            cts.Cancel();
            await runTask;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SoulseekLink_ExplicitMusicNameFormatIsRejectedBeforeAdmission()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-slsk-explicit-format-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);

            var exception = await Assert.ThrowsExactlyAsync<UnsupportedNameFormatVariableException>(() =>
                supervisor.SubmitExtractJobAsync(
                    new SubmitExtractJobRequestDto(
                        "slsk://Peer/Share/File.bin",
                        Options: new SubmissionOptionsDto(
                            DownloadSettings: new DownloadSettingsPatchDto(
                                Output: new OutputSettingsPatchDto(
                                    NameFormat: "{artist|filename}")))),
                    CancellationToken.None));

            Assert.AreEqual("artist", exception.Variable);
            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolvedDirectoryDraft_DoesNotRejectLargeKnownByteCount()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-directory-admission-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            const long largeKnownBytes = 3L * 1024 * 1024 * 1024 * 1024;
            var supervisor = CreateSupervisor(root, root);
            var plan = new DirectoryTransferPlanDto(
                "Root",
                [new DirectoryTransferEntryDto(
                    new PeerFileTargetDto("Peer", @"Root\Huge.bin", largeKnownBytes, ".bin"),
                    [])],
                largeKnownBytes);

            var submitted = await supervisor.SubmitJobListAsync(
                new SubmitJobListRequestDto(
                    "large directory",
                    [new RemoteDirectoryJobDraftDto(Plan: plan)]),
                CancellationToken.None);

            Assert.AreNotEqual(Guid.Empty, submitted.JobId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FollowUpSelection_RejectsIncompatibleInterpretationBeforeSourceLookup()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-server-selection-mode-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var supervisor = CreateSupervisor(root, root);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.StartFileDownloadsAsync(
                    Guid.NewGuid(),
                    new StartFileDownloadsRequestDto(
                        [new FileCandidateRefDto("Peer", @"Share\File.bin")],
                        RequestedMode: ExtractionMode.Album),
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                supervisor.StartFolderDownloadAsync(
                    Guid.NewGuid(),
                    new StartFolderDownloadRequestDto(
                        new AlbumFolderRefDto("Peer", "Share"),
                        RequestedMode: ExtractionMode.Song),
                    CancellationToken.None));

            Assert.AreEqual(0, supervisor.StateStore.GetWorkflows().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EngineSupervisor CreateSupervisor(
        string musicRoot,
        string outputDir,
        Action<DownloadSettings>? configureDownload = null,
        Action<EngineSettings>? configureEngine = null,
        ProfileCatalog? profiles = null,
        DownloadSettingsPatchDto? launchDownloadSettings = null)
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
            LaunchDownloadSettings = launchDownloadSettings,
            Profiles = profiles ?? ProfileCatalog.Empty,
            Persistence = new ServerPersistenceOptions
            {
                DataDirectory = Path.Combine(musicRoot, ".sockseek-test-data"),
            },
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

    private static string ToSoulseekFileUri(string path)
        => "slsk://local/" + path.Replace('\\', '/');

    private static async Task WaitForJobStateAsync(
        EngineSupervisor supervisor,
        Guid jobId,
        ExpectedJobStatus expectedState,
        int timeoutMs = 15000)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobSummaryDto? lastSummary = null;

        void Observe(JobSummaryDto summary)
        {
            if (summary.JobId != jobId)
                return;
            lastSummary = summary;
            if (ProjectState(summary) == expectedState)
                reached.TrySetResult();
        }

        supervisor.StateStore.JobUpserted += Observe;
        try
        {
            JobSummaryDto? current = supervisor.StateStore.GetJobSummary(jobId);
            if (current != null)
                Observe(current);
            await reached.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return;
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            supervisor.StateStore.JobUpserted -= Observe;
        }

        var last = lastSummary == null
            ? "<missing>"
            : $"{ProjectState(lastSummary)} lifecycle={lastSummary.LifecycleState} activity={lastSummary.ActivityPhase} outcome={lastSummary.TerminalOutcome} skip={lastSummary.SkipReason} failure={lastSummary.FailureReason} message={lastSummary.FailureMessage} detail={lastSummary.FailureDetail}";
        Assert.Fail($"Timed out waiting for job {jobId} to reach state '{expectedState}'. Last summary: {last}.");
    }

    private static bool IsActive(SongJobPayloadDto song)
        => song.LifecycleState != ServerJobLifecycleState.Terminal;

    private static ExpectedJobStatus ProjectState(JobSummaryDto summary)
        => ProjectState(summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason);

    private static ExpectedJobStatus ProjectState(SongJobPayloadDto song)
        => ProjectState(
            song.LifecycleState ?? ServerJobLifecycleState.Pending,
            song.ActivityPhase ?? ServerJobActivityPhase.None,
            song.TerminalOutcome ?? ServerJobTerminalOutcome.None,
            song.SkipReason ?? ServerJobSkipReason.None);

    private static ExpectedJobStatus ProjectState(
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity,
        ServerJobTerminalOutcome outcome,
        ServerJobSkipReason skipReason = ServerJobSkipReason.None)
        => lifecycle switch
        {
            ServerJobLifecycleState.Pending => ExpectedJobStatus.Pending,
            ServerJobLifecycleState.AwaitingSelection => ExpectedJobStatus.AwaitingSelection,
            ServerJobLifecycleState.Terminal => outcome switch
            {
                ServerJobTerminalOutcome.Succeeded => ExpectedJobStatus.Succeeded,
                ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.AlreadyExists => ExpectedJobStatus.AlreadyExists,
                ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.NotFoundLastTime => ExpectedJobStatus.NotFoundLastTime,
                ServerJobTerminalOutcome.Skipped => ExpectedJobStatus.Skipped,
                _ => ExpectedJobStatus.Failed,
            },
            _ => activity switch
            {
                ServerJobActivityPhase.Extracting => ExpectedJobStatus.Extracting,
                ServerJobActivityPhase.Downloading => ExpectedJobStatus.Downloading,
                ServerJobActivityPhase.RunningChildren => ExpectedJobStatus.RunningChildren,
                ServerJobActivityPhase.None => ExpectedJobStatus.RunningChildren,
                _ => ExpectedJobStatus.Searching,
            },
        };

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

    private static List<SongJobPayloadDto> GetChildSongPayloads(EngineSupervisor supervisor, Guid parentJobId)
    {
        var parent = supervisor.StateStore.GetJobDetail(parentJobId);
        return parent?.Children
            .Select(child => supervisor.StateStore.GetJobDetail(child.JobId)?.Payload)
            .OfType<SongJobPayloadDto>()
            .ToList() ?? [];
    }

    private static List<JobSummaryDto> SearchChildren(EngineSupervisor supervisor, Guid workflowId)
        => supervisor.StateStore.GetJobs(new JobQuery(null, null, ServerJobKind.Search, workflowId, IncludeAll: true))
            .Where(summary => summary.ParentJobId != null)
            .OrderBy(summary => summary.DisplayId)
            .ToList();

    private static SearchJob SearchChildByQuery(EngineSupervisor supervisor, IReadOnlyList<JobSummaryDto> children, string queryText)
    {
        var summary = children.Single(child => child.QueryText?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true);
        var job = supervisor.GetRuntimeJob<SearchJob>(summary.JobId);
        Assert.IsNotNull(job);
        return job;
    }
}
