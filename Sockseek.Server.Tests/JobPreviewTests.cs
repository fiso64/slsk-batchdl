using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Planning;
using Sockseek.Core.Settings;
using Sockseek.Server.Planning;
using System.Text;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class JobPreviewTests
{
    [TestMethod]
    public async Task DaemonSessionRestartInvalidatesUncommittedPreview()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-preview-restart-tests",
            Guid.NewGuid().ToString("N"));
        Guid previewId;
        try
        {
            var options = Options.Create(new ServerOptions
            {
                DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
                Persistence = new ServerPersistenceOptions { DataDirectory = directory },
            });
            await using (var first = NewCoordinator(options))
            {
                await first.StartAsync(CancellationToken.None);
                var completed = new TaskCompletionSource<Guid>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                first.PreviewCompleted += id => completed.TrySetResult(id);
                CreateJobPreviewResponseDto created = await first.CreateAsync(
                    new CreateJobPreviewRequestDto(
                        new SongJobDraftDto(
                            new SongQueryDto("Restart Artist", "Restart Title"))),
                    CancellationToken.None);
                previewId = created.Preview.PreviewId;
                Assert.AreEqual(
                    previewId,
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsNotNull(await first.GetAsync(previewId, CancellationToken.None));
                await first.StopAsync(CancellationToken.None);
            }

            await using (var restarted = NewCoordinator(options))
            {
                await restarted.StartAsync(CancellationToken.None);
                Assert.IsNull(await restarted.GetAsync(previewId, CancellationToken.None));
                await restarted.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PartialPreviewCommitsValidSiblingAndReportsFailedEntry()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-preview-partial-tests",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions { DataDirectory = directory },
        });
        var supervisor = new EngineSupervisor(options);
        var commits = new Persistence.SubmissionCommitCoordinator(
            new Persistence.PersistenceCoordinator(options));
        await using var coordinator = new JobPreviewCoordinator(
            options,
            supervisor,
            commits,
            new JobPreviewCursorCodec(),
            NullLogger<JobPreviewCoordinator>.Instance);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);
            var completed = new TaskCompletionSource<Guid>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.PreviewCompleted += id => completed.TrySetResult(id);
            CreateJobPreviewResponseDto created = await coordinator.CreateAsync(
                new CreateJobPreviewRequestDto(new JobListJobDraftDto(
                    "mixed",
                    [
                        new ExtractJobDraftDto(
                            Path.Combine(directory, "missing.csv"),
                            InputType.CSV.ToString()),
                        new SongJobDraftDto(new SongQueryDto("Valid Artist", "Valid Title")),
                    ])),
                CancellationToken.None);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            JobPreviewSummaryDto preview = (await coordinator.GetAsync(
                created.Preview.PreviewId,
                CancellationToken.None))!;
            Assert.AreEqual(JobPreviewState.PartiallyReady, preview.State);
            Assert.AreEqual(1, preview.FailedNodeCount);

            CommitJobPreviewResponseDto receipt = (await coordinator.CommitAsync(
                preview.PreviewId,
                CommitAll(preview),
                CancellationToken.None))!;
            Assert.AreEqual(1, receipt.SubmittedCount);
            Assert.AreEqual(1, receipt.RejectedCount);
            Assert.AreEqual("planning-failed", receipt.RejectionReasons.Single().Reason);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UploadedArtifactPreviewCommitsDigestWithoutSourceMutationOrPathTrust()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-artifact-preview-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "mock"));
        var options = new ServerOptions
        {
            ConfigDir = directory,
            Engine = new EngineSettings
            {
                MockFilesDir = Path.Combine(directory, "mock"),
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = Path.Combine(directory, "data"),
            },
        };
        try
        {
            await using var app = ServerHost.Build([], options, "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var artifacts = app.Services.GetRequiredService<InputArtifactCoordinator>();
                InputArtifactDto artifact = await artifacts.UploadAsync(
                    new MemoryStream(Encoding.UTF8.GetBytes(
                        "artist,title\nArtifact Artist,Artifact Title\n")),
                    "../../browser-name.csv",
                    CancellationToken.None);
                Assert.AreEqual("browser-name.csv", artifact.OriginalName);

                var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
                JobSummaryDto direct = await supervisor.SubmitExtractJobAsync(
                    new SubmitExtractJobRequestDto(
                        Input: "browser-path-is-not-used",
                        ArtifactId: artifact.ArtifactId),
                    CancellationToken.None);
                var persistence = app.Services.GetRequiredService<Persistence.PersistenceCoordinator>();
                var directRetained = await persistence.Submissions!.GetSubmissionAsync(
                    direct.SubmissionId!.Value,
                    CancellationToken.None);
                SubmissionSpecification directSpecification = SubmissionSpecificationCodec.Deserialize(
                    directRetained!.SpecificationJson);
                Assert.AreEqual("input-artifact", directSpecification.SourceRevision?.Kind);
                Assert.AreEqual(artifact.Sha256, directSpecification.SourceRevision?.Digest);

                var previews = app.Services.GetRequiredService<JobPreviewCoordinator>();
                var completed = new TaskCompletionSource<Guid>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(Guid id) => completed.TrySetResult(id);
                previews.PreviewCompleted += OnCompleted;
                CreateJobPreviewResponseDto created;
                try
                {
                    created = await previews.CreateAsync(
                        new CreateJobPreviewRequestDto(
                            new ExtractJobDraftDto(
                                Input: "browser-path-is-not-used",
                                ArtifactId: artifact.ArtifactId,
                                DownloadSettings: new DownloadSettingsPatchDto(
                                    Extraction: new ExtractionSettingsPatchDto(
                                        RemoveTracksFromSource: true)))),
                        CancellationToken.None);
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    previews.PreviewCompleted -= OnCompleted;
                }

                CommitJobPreviewResponseDto receipt = (await previews.CommitAsync(
                    created.Preview.PreviewId,
                    CommitAll((await previews.GetAsync(
                        created.Preview.PreviewId,
                        CancellationToken.None))!),
                    CancellationToken.None))!;
                var retained = await persistence.Submissions!.GetSubmissionAsync(
                    receipt.SubmissionId!.Value,
                    CancellationToken.None);
                SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(
                    retained!.SpecificationJson);
                Assert.AreEqual(artifact.ArtifactId, specification.Command.ArtifactId);
                Assert.AreEqual("input-artifact", specification.SourceRevision?.Kind);
                Assert.AreEqual(artifact.ArtifactId, specification.SourceRevision?.Identity);
                Assert.AreEqual(artifact.Sha256, specification.SourceRevision?.Digest);
                Assert.IsNull(specification.Command.SourceMutation);
                Assert.AreEqual("Artifact Artist", specification.Command.SongQuery?.Artist);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PreviewCommitUsesStoredPlanAfterMutableSourceChanges()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-job-preview-server-tests",
            Guid.NewGuid().ToString("N"));
        string input = Path.Combine(directory, "input.csv");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(input, "artist,title\nOriginal Artist,Original Title\n");
        var options = new ServerOptions
        {
            ConfigDir = directory,
            Engine = new EngineSettings
            {
                MockFilesDir = Path.Combine(directory, "mock"),
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = Path.Combine(directory, "data"),
            },
        };
        Directory.CreateDirectory(options.Engine.MockFilesDir!);

        try
        {
            await using var app = ServerHost.Build([], options, "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var coordinator = app.Services.GetRequiredService<JobPreviewCoordinator>();
                var completed = new TaskCompletionSource<Guid>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(Guid id) => completed.TrySetResult(id);
                coordinator.PreviewCompleted += OnCompleted;
                CreateJobPreviewResponseDto created;
                try
                {
                    created = await coordinator.CreateAsync(
                        new CreateJobPreviewRequestDto(
                            new ExtractJobDraftDto(input, InputType.CSV.ToString())),
                        CancellationToken.None);
                    Assert.AreEqual(JobPreviewState.Planning, created.Preview.State);
                    Assert.AreEqual(
                        created.Preview.PreviewId,
                        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    coordinator.PreviewCompleted -= OnCompleted;
                }

                JobPreviewSummaryDto preview = (await coordinator.GetAsync(
                    created.Preview.PreviewId,
                    CancellationToken.None))!;
                Assert.IsTrue(preview.State is JobPreviewState.Ready or JobPreviewState.PartiallyReady);
                Assert.IsTrue(preview.SelectableNodeCount > 0);

                await File.WriteAllTextAsync(input, "artist,title\nChanged Artist,Changed Title\n");
                CommitJobPreviewRequestDto commit = CommitAll(preview);
                CommitJobPreviewResponseDto receipt = (await coordinator.CommitAsync(
                    preview.PreviewId,
                    commit,
                    CancellationToken.None))!;
                Assert.AreEqual(1, receipt.SubmittedCount);
                Assert.IsNotNull(receipt.SubmissionId);
                CommitJobPreviewResponseDto repeated = (await coordinator.CommitAsync(
                    preview.PreviewId,
                    commit,
                    CancellationToken.None))!;
                Assert.AreEqual(receipt.SubmissionId, repeated.SubmissionId);

                var persistence = app.Services.GetRequiredService<Persistence.PersistenceCoordinator>();
                var retained = await persistence.Submissions!.GetSubmissionAsync(
                    receipt.SubmissionId.Value,
                    CancellationToken.None);
                SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(
                    retained!.SpecificationJson);
                Assert.AreEqual("Original Artist", specification.Command.SongQuery?.Artist);
                Assert.AreEqual("Original Title", specification.Command.SongQuery?.Title);
                Assert.AreNotEqual("Changed Artist", specification.Command.SongQuery?.Artist);

                Assert.IsNull(await coordinator.GetAsync(
                    preview.PreviewId,
                    CancellationToken.None));
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnavailablePreviewStoreDoesNotDisableDirectStart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-job-preview-unavailable-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string unavailableDataPath = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(unavailableDataPath, "occupied");
        var options = Options.Create(new ServerOptions
        {
            DefaultDownload = new DownloadSettings { PrintOption = PrintOption.Jobs },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = false,
                DataDirectory = unavailableDataPath,
            },
        });
        var supervisor = new EngineSupervisor(options);
        var commits = new Persistence.SubmissionCommitCoordinator(
            new Persistence.PersistenceCoordinator(options));
        await using var coordinator = new JobPreviewCoordinator(
            options,
            supervisor,
            commits,
            new JobPreviewCursorCodec(),
            NullLogger<JobPreviewCoordinator>.Instance);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);
            await Assert.ThrowsExactlyAsync<JobPreviewUnavailableException>(() =>
                coordinator.CreateAsync(
                    new CreateJobPreviewRequestDto(
                        new ExtractJobDraftDto("Artist - Title", InputType.String.ToString())),
                    CancellationToken.None));

            JobSummaryDto direct = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    "Artist - Title",
                    InputType.String.ToString()),
                CancellationToken.None);
            Assert.AreEqual(ServerJobKind.Extract, direct.Kind);
            Assert.IsNotNull(direct.SubmissionId);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static CommitJobPreviewRequestDto CommitAll(JobPreviewSummaryDto preview)
        => new(
            preview.Revision,
            new RefSelectionExpressionDto(RefSelectionMode.AllExcept, []),
            Guid.NewGuid());

    private static JobPreviewCoordinator NewCoordinator(
        IOptions<ServerOptions> options)
    {
        var supervisor = new EngineSupervisor(options);
        var commits = new Persistence.SubmissionCommitCoordinator(
            new Persistence.PersistenceCoordinator(options));
        return new JobPreviewCoordinator(
            options,
            supervisor,
            commits,
            new JobPreviewCursorCodec(),
            NullLogger<JobPreviewCoordinator>.Instance);
    }
}
