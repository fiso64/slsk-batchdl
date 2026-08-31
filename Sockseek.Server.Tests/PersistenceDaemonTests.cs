using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server.Persistence;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PersistenceDaemonTests
{
    [TestMethod]
    public async Task CorruptDatabase_AbortsStartupWithoutReplacement_AndReleasesOwnership()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-corrupt-persistence", Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "sockseek.db");
        Directory.CreateDirectory(directory);
        byte[] corruption = "this is not a sqlite database"u8.ToArray();
        await File.WriteAllBytesAsync(databasePath, corruption);
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions { Enabled = true, DataDirectory = directory },
        });
        try
        {
            var coordinator = new PersistenceCoordinator(options);
            await Assert.ThrowsExceptionAsync<PersistenceDatabaseCorruptionException>(
                () => coordinator.StartAsync(CancellationToken.None));
            CollectionAssert.AreEqual(corruption, await File.ReadAllBytesAsync(databasePath));
            using var owner = SqliteDatabaseOwner.Acquire(new SockseekSqliteOptions(databasePath));
            Assert.AreEqual(Path.GetFullPath(databasePath), owner.DatabasePath);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DrainTimeout_IsFinite_ReportsFailure_AndLeavesRuntimeForReconciliation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-drain-timeout", Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "sockseek.db");
        Directory.CreateDirectory(directory);
        var serverOptions = new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
                DrainTimeout = TimeSpan.FromMilliseconds(100),
            },
        };
        var options = Options.Create(serverOptions);
        try
        {
            var first = new PersistenceCoordinator(options);
            await first.StartAsync(CancellationToken.None);
            Guid runtimeId = first.Runtime!.RuntimeId;
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
            await using (var lockConnection = new SqliteConnection(connectionString))
            {
                await lockConnection.OpenAsync();
                await using (var begin = lockConnection.CreateCommand())
                {
                    begin.CommandText = "BEGIN IMMEDIATE;";
                    await begin.ExecuteNonQueryAsync();
                }
                Guid jobId = Guid.NewGuid();
                Assert.IsTrue(first.MutationSink!.TryEnqueue(new JobPersistenceMutation(
                    runtimeId, 1, DateTimeOffset.UtcNow, jobId, 1, PersistenceMutationPriority.Structural,
                    Guid.NewGuid(), null, null, null, 1, "Song", "Pending", "None", null,
                    "None", "None", "None", "None", null, null, null, "query", 1, null)));

                await first.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(first.IsStarted);
                Assert.AreEqual(PersistenceHealthState.Unhealthy, first.HealthSnapshot!.State);
                await using var rollback = lockConnection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync();
            }

            var second = new PersistenceCoordinator(options);
            await second.StartAsync(CancellationToken.None);
            try
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT shutdown_kind FROM runtime_sessions ORDER BY started_at_utc LIMIT 1;";
                Assert.AreEqual("Unclean", Convert.ToString(await command.ExecuteScalarAsync()));
            }
            finally
            {
                await second.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task Daemon_StartsPersistenceBeforeEngine_ReportsHealth_AndStopsRuntimeCleanly()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-daemon-persistence", Guid.NewGuid().ToString("N"));
        string mockFiles = Path.Combine(directory, "mock-files");
        string databasePath = Path.Combine(directory, "data", "sockseek.db");
        Directory.CreateDirectory(mockFiles);

        try
        {
            var options = new ServerOptions
            {
                ConfigDir = directory,
                Engine = new EngineSettings { MockFilesDir = mockFiles },
                Persistence = new ServerPersistenceOptions
                {
                    Enabled = true,
                    DataDirectory = Path.GetDirectoryName(databasePath),
                },
            };
            var app = ServerHost.Build([], options, "http://127.0.0.1:0");
            try
            {
                await app.StartAsync();
                var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
                var status = supervisor.GetStatus();

                Assert.IsNotNull(status.Persistence);
                Assert.IsTrue(status.Persistence.Enabled);
                Assert.IsTrue(status.Persistence.Initialized);
                Assert.AreEqual("Healthy", status.Persistence.State);
                Assert.IsNotNull(status.Persistence.RuntimeId);
                Assert.IsNotNull(status.Persistence.RuntimeStartedAtUtc);
                StringAssert.Contains(status.Persistence.SchemaVersion, "AddJobNavigationIndexes");
                Assert.AreEqual(0, status.Persistence.ReconciledUnfinishedRuntimeCount);

                var coordinator = app.Services.GetRequiredService<PersistenceCoordinator>();
                var integrity = await coordinator.CheckIntegrityAsync(CancellationToken.None);
                Assert.IsTrue(integrity.IsHealthy);
                var backup = await coordinator.BackupAsync(
                    Path.Combine(directory, "backups", "daemon-test.db"),
                    CancellationToken.None);
                Assert.IsTrue(backup.IntegrityHealthy);
                Assert.IsTrue(File.Exists(backup.BackupPath));
                var checkpoint = await coordinator.CheckpointAsync(CancellationToken.None);
                Assert.IsTrue(checkpoint.Busy is 0 or 1);
                var retention = await coordinator.RunRetentionAsync(CancellationToken.None);
                Assert.AreEqual(0, retention.PrunedJobs);
                var statusAfterMaintenance = supervisor.GetStatus().Persistence!;
                Assert.IsNotNull(statusAfterMaintenance.LastRetentionAtUtc);
                Assert.AreEqual(0, statusAfterMaintenance.LastRetentionPrunedJobs);

                await app.StopAsync();
                Assert.IsFalse(
                    coordinator.IsStarted,
                    "The hosted persistence runtime must release database ownership during host stop.");

                var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT shutdown_kind FROM runtime_sessions ORDER BY started_at_utc DESC LIMIT 1;";
                Assert.AreEqual("Clean", Convert.ToString(await command.ExecuteScalarAsync()));
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(directory);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string directory)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (!Directory.Exists(directory))
                return;
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(25);
            }
        }
    }

    [TestMethod]
    public async Task HistoricalSearchResult_StartsNewDownloadAfterFullDaemonRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-daemon-history", Guid.NewGuid().ToString("N"));
        string mockFiles = Path.Combine(directory, "mock-files");
        string albumDirectory = Path.Combine(mockFiles, "Artist", "Album");
        string outputDirectory = Path.Combine(directory, "output");
        string databasePath = Path.Combine(directory, "data", "sockseek.db");
        Directory.CreateDirectory(albumDirectory);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(albumDirectory, "01. Artist - Track One.mp3"), "audio");

        var options = new ServerOptions
        {
            ConfigDir = directory,
            Engine = new EngineSettings { MockFilesDir = mockFiles, MockFilesReadTags = false },
            DefaultDownload = new DownloadSettings
            {
                Output = { ParentDir = outputDirectory },
                Search = { MinSharesAggregate = 1 },
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = Path.GetDirectoryName(databasePath),
            },
        };

        try
        {
            Guid sourceJobId;
            Guid workflowId;
            int sourceDisplayId;
            FileCandidateRefDto candidate;
            FileCandidateDto expectedCandidate;
            AggregateTrackCandidateDto expectedAggregateTrack;
            Guid albumSearchJobId;
            Guid albumWorkflowId;
            AlbumFolderDto expectedFolder;
            AggregateAlbumCandidateDto expectedAggregateAlbum;

            await using (var first = ServerHost.Build([], options, "http://127.0.0.1:0"))
            {
                await first.StartAsync();
                try
                {
                    var supervisor = first.Services.GetRequiredService<EngineSupervisor>();
                    var persistence = first.Services.GetRequiredService<PersistenceCoordinator>();
                    var facade = first.Services.GetRequiredService<HistoricalQueryFacade>();
                    var search = await supervisor.SubmitTrackSearchJobAsync(
                        new SubmitTrackSearchJobRequestDto(new SongQueryDto("Artist", "Track One")),
                        CancellationToken.None);
                    await WaitForTerminalSuccessAsync(persistence, search.JobId);
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(search.JobId));
                    var results = await facade.GetFileResultsAsync(search.JobId, null);
                    Assert.IsNotNull(results);
                    Assert.AreEqual(1, results.Items.Count);

                    sourceJobId = search.JobId;
                    workflowId = search.WorkflowId;
                    sourceDisplayId = search.DisplayId;
                    candidate = results.Items[0].Ref;
                    expectedCandidate = results.Items[0];

                    var aggregateTracks = await facade.GetAggregateTrackResultsAsync(
                        search.JobId,
                        new AggregateTrackProjectionRequestDto(IncludeCandidates: true));
                    Assert.IsNotNull(aggregateTracks);
                    Assert.AreEqual(1, aggregateTracks.Items.Count);
                    expectedAggregateTrack = aggregateTracks.Items[0];

                    var albumSearch = await supervisor.SubmitAlbumSearchJobAsync(
                        new SubmitAlbumSearchJobRequestDto(new AlbumQueryDto("Artist", "Album")),
                        CancellationToken.None);
                    await WaitForTerminalSuccessAsync(persistence, albumSearch.JobId);
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(albumSearch.JobId));
                    albumSearchJobId = albumSearch.JobId;
                    albumWorkflowId = albumSearch.WorkflowId;
                    var folders = await facade.GetFolderResultsAsync(
                        albumSearch.JobId,
                        request: null,
                        includeFiles: true);
                    Assert.IsNotNull(folders);
                    Assert.AreEqual(1, folders.Items.Count);
                    expectedFolder = folders.Items[0];
                    var aggregateAlbums = await facade.GetAggregateAlbumResultsAsync(
                        albumSearch.JobId,
                        new AggregateAlbumProjectionRequestDto(IncludeFolders: true));
                    Assert.IsNotNull(aggregateAlbums);
                    Assert.AreEqual(1, aggregateAlbums.Items.Count);
                    expectedAggregateAlbum = aggregateAlbums.Items[0];
                }
                finally
                {
                    await first.StopAsync();
                }
            }

            SqliteConnection.ClearAllPools();

            await using (var second = ServerHost.Build([], options, "http://127.0.0.1:0"))
            {
                await second.StartAsync();
                try
                {
                    var supervisor = second.Services.GetRequiredService<EngineSupervisor>();
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(sourceJobId));

                    var historicalProjection = await second.Services
                        .GetRequiredService<HistoricalQueryFacade>()
                        .GetFileResultsAsync(sourceJobId, null);
                    Assert.IsNotNull(historicalProjection);
                    Assert.AreEqual("Complete", historicalProjection.PersistenceState);
                    Assert.AreEqual(1, historicalProjection.Items.Count);
                    Assert.AreEqual(expectedCandidate.Ref, historicalProjection.Items[0].Ref);
                    Assert.AreEqual(expectedCandidate.File.Size, historicalProjection.Items[0].File.Size);
                    Assert.AreEqual(expectedCandidate.File.BitRate, historicalProjection.Items[0].File.BitRate);
                    Assert.AreEqual(expectedCandidate.File.SampleRate, historicalProjection.Items[0].File.SampleRate);
                    Assert.AreEqual(expectedCandidate.File.Length, historicalProjection.Items[0].File.Length);

                    var facade = second.Services.GetRequiredService<HistoricalQueryFacade>();
                    var workflowPage = await facade.GetWorkflowsAsync(null, 100);
                    Assert.IsTrue(workflowPage.Items.Any(workflow => workflow.WorkflowId == workflowId));
                    Assert.IsTrue(workflowPage.Items.Any(workflow => workflow.WorkflowId == albumWorkflowId));
                    var historicalWorkflow = await facade.GetWorkflowAsync(workflowId);
                    Assert.IsNotNull(historicalWorkflow);
                    Assert.AreEqual(1, historicalWorkflow.Summary.RootJobCount);
                    var historicalJobs = await facade.GetJobsAsync(
                        new JobQuery(null, null, null, workflowId, IncludeAll: true),
                        cursor: null,
                        limit: 100);
                    CollectionAssert.Contains(historicalJobs.Items.Select(job => job.JobId).ToArray(), sourceJobId);
                    Assert.IsNull(historicalJobs.NextCursor);
                    var byDisplay = await facade.GetJobByDisplayIdAsync(workflowId, sourceDisplayId);
                    Assert.IsNotNull(byDisplay);
                    Assert.AreEqual(sourceJobId, byDisplay.Summary.JobId);
                    Assert.IsFalse(supervisor.CancelJob(sourceJobId));
                    Assert.IsFalse(supervisor.TryNextCandidate(sourceJobId));
                    Assert.IsFalse(await supervisor.CompleteManualSelectionAsync(sourceJobId));
                    Assert.IsFalse(await supervisor.SkipManualSelectionAsync(sourceJobId));

                    var historicalTracks = await second.Services
                        .GetRequiredService<HistoricalQueryFacade>()
                        .GetAggregateTrackResultsAsync(
                            sourceJobId,
                            new AggregateTrackProjectionRequestDto(IncludeCandidates: true));
                    Assert.IsNotNull(historicalTracks);
                    Assert.AreEqual("Complete", historicalTracks.PersistenceState);
                    Assert.AreEqual(1, historicalTracks.Items.Count);
                    Assert.AreEqual(expectedAggregateTrack.Query, historicalTracks.Items[0].Query);
                    Assert.AreEqual(expectedAggregateTrack.Candidates?[0].Ref, historicalTracks.Items[0].Candidates?[0].Ref);

                    var historicalFolders = await second.Services
                        .GetRequiredService<HistoricalQueryFacade>()
                        .GetFolderResultsAsync(albumSearchJobId, null, includeFiles: true);
                    Assert.IsNotNull(historicalFolders);
                    Assert.AreEqual("Complete", historicalFolders.PersistenceState);
                    Assert.AreEqual(1, historicalFolders.Items.Count);
                    Assert.AreEqual(expectedFolder.Ref, historicalFolders.Items[0].Ref);
                    Assert.AreEqual(expectedFolder.Files?[0].Ref, historicalFolders.Items[0].Files?[0].Ref);

                    var historicalAlbums = await second.Services
                        .GetRequiredService<HistoricalQueryFacade>()
                        .GetAggregateAlbumResultsAsync(
                            albumSearchJobId,
                            new AggregateAlbumProjectionRequestDto(IncludeFolders: true));
                    Assert.IsNotNull(historicalAlbums);
                    Assert.AreEqual("Complete", historicalAlbums.PersistenceState);
                    Assert.AreEqual(1, historicalAlbums.Items.Count);
                    Assert.AreEqual(expectedAggregateAlbum.Query, historicalAlbums.Items[0].Query);
                    Assert.AreEqual(expectedAggregateAlbum.Folders?[0].Ref, historicalAlbums.Items[0].Folders?[0].Ref);

                    var retrieved = await supervisor.StartRetrieveFolderAsync(
                        albumSearchJobId,
                        new RetrieveFolderRequestDto(expectedFolder.Ref),
                        CancellationToken.None);
                    Assert.IsNotNull(retrieved);
                    Assert.AreEqual(albumSearchJobId, retrieved.SourceJobId);
                    Assert.AreEqual(albumWorkflowId, retrieved.WorkflowId);
                    Assert.IsTrue(retrieved.DisplayId > 2);
                    await WaitForTerminalSuccessAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        retrieved.JobId);

                    var albumDownload = await supervisor.StartFolderDownloadAsync(
                        albumSearchJobId,
                        new StartFolderDownloadRequestDto(expectedFolder.Ref),
                        CancellationToken.None);
                    Assert.IsNotNull(albumDownload);
                    Assert.AreEqual(albumSearchJobId, albumDownload.SourceJobId);
                    Assert.AreEqual(albumWorkflowId, albumDownload.WorkflowId);
                    Assert.IsTrue(albumDownload.DisplayId > 2);
                    await WaitForTerminalSuccessAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        albumDownload.JobId);

                    var downloads = await supervisor.StartFileDownloadsAsync(
                        sourceJobId,
                        new StartFileDownloadsRequestDto([candidate]),
                        CancellationToken.None);

                    Assert.IsNotNull(downloads);
                    Assert.AreEqual(1, downloads.Count);
                    Assert.AreNotEqual(sourceJobId, downloads[0].JobId);
                    Assert.AreEqual(sourceJobId, downloads[0].SourceJobId);
                    Assert.AreEqual(workflowId, downloads[0].WorkflowId);
                    Assert.IsTrue(downloads[0].DisplayId > 2);
                    Assert.AreEqual(3, new[] { retrieved.DisplayId, albumDownload.DisplayId, downloads[0].DisplayId }.Distinct().Count());
                    await WaitForTerminalSuccessAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        downloads[0].JobId);
                    var persistedDownload = await WaitForHistoricalJobAsync(
                        second.Services.GetRequiredService<PersistenceCoordinator>(),
                        downloads[0].JobId);
                    Assert.IsNull(supervisor.GetRuntimeJob<Sockseek.Core.Jobs.Job>(downloads[0].JobId));
                    var retainedDetail = await facade.GetJobAsync(downloads[0].JobId);
                    Assert.IsNotNull(retainedDetail);
                    Assert.AreEqual(persistedDownload.SourceJobId, retainedDetail.Summary.SourceJobId);
                    var combinedWorkflow = await facade.GetWorkflowAsync(workflowId);
                    Assert.IsNotNull(combinedWorkflow);
                    var combinedJobs = await facade.GetJobsAsync(
                        new JobQuery(null, null, null, workflowId, IncludeAll: true),
                        cursor: null,
                        limit: 100);
                    CollectionAssert.Contains(combinedJobs.Items.Select(job => job.JobId).ToArray(), sourceJobId);
                    CollectionAssert.Contains(combinedJobs.Items.Select(job => job.JobId).ToArray(), downloads[0].JobId);
                    Assert.IsTrue(Directory.GetFiles(outputDirectory, "*.mp3", SearchOption.AllDirectories).Length >= 1);
                }
                finally
                {
                    await second.StopAsync();
                }
            }

            SqliteConnection.ClearAllPools();

            await using (var third = ServerHost.Build([], options, "http://127.0.0.1:0"))
            {
                await third.StartAsync();
                try
                {
                    var facade = third.Services.GetRequiredService<HistoricalQueryFacade>();
                    var transfers = await facade.GetTransfersAsync(
                        cursor: null,
                        limit: 100,
                        jobId: null,
                        workflowId: null,
                        direction: null,
                        source: null,
                        state: null,
                        terminalOutcome: null,
                        username: null,
                        fromUtc: null,
                        toUtc: null);
                    Assert.IsTrue(transfers.Items.Count > 0);

                    var completedTransfer = transfers.Items.First(transfer =>
                        transfer.TerminalOutcome == "Succeeded" && transfer.AttemptCount > 0);
                    Assert.IsNotNull(completedTransfer.JobId);
                    Assert.IsNotNull(completedTransfer.WorkflowId);

                    var detail = await facade.GetTransferDetailAsync(completedTransfer.TransferId);
                    Assert.IsNotNull(detail);
                    Assert.AreEqual(completedTransfer.TransferId, detail.History!.TransferId);
                    Assert.AreEqual(completedTransfer.AttemptCount, detail.AttemptCount);
                    Assert.IsNotNull(detail.LatestAttempt);
                    Assert.AreEqual(completedTransfer.TransferId, detail.LatestAttempt.TransferId);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(detail.LatestAttempt.SourceUsername));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(detail.LatestAttempt.SourcePath));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(detail.LatestAttempt.OutputPath));

                    var attemptPage = await facade.GetTransferAttemptsAsync(
                        completedTransfer.TransferId,
                        afterAttemptNumber: 0,
                        limit: 1);
                    Assert.IsNotNull(attemptPage);
                    Assert.AreEqual(1, attemptPage.Items.Count);
                    Assert.AreEqual(completedTransfer.TransferId, attemptPage.Items[0].TransferId);
                }
                finally
                {
                    await third.StopAsync();
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task WaitForTerminalSuccessAsync(
        PersistenceCoordinator persistence,
        Guid jobId)
    {
        var history = persistence.JobHistory
            ?? throw new AssertFailedException("Persistence job history is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        PersistedJob? job = null;
        while (!timeout.IsCancellationRequested)
        {
            job = await history.GetJobAsync(jobId, timeout.Token);
            if (job?.LifecycleState == "Terminal")
                break;
            try { await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }

        Assert.IsNotNull(job, $"Timed out waiting for persisted job {jobId}.");
        Assert.AreEqual("Terminal", job.LifecycleState);
        Assert.AreEqual("Succeeded", job.TerminalOutcome,
            $"Job terminated with {job.TerminalOutcome}: {job.FailureMessage}");
    }

    private static async Task<PersistedJob> WaitForHistoricalJobAsync(
        PersistenceCoordinator coordinator,
        Guid jobId)
    {
        var history = coordinator.JobHistory ?? throw new AssertFailedException("Persistence job history is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var job = await history.GetJobAsync(jobId, timeout.Token);
            if (job != null)
                return job;
            try { await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }

        Assert.Fail($"Timed out waiting for persisted job {jobId}.");
        throw new InvalidOperationException("Assert.Fail did not throw.");
    }
}
