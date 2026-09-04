using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Server;
using Sockseek.Server.Persistence;
using Sockseek.Server.Planning;
using Soulseek;

namespace Tests.Server;

[TestClass]
public sealed class SearchViewCoordinatorTests
{
    [TestMethod]
    public async Task UnavailableSearchViewStorageDoesNotDisableDirectSearchSubmission()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-search-view-unavailable-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions
        {
            Engine = new EngineSettings { MockFilesDir = directory },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = false,
                DataDirectory = Path.Combine(directory, "data"),
            },
        });
        var supervisor = new EngineSupervisor(options);
        var persistence = new PersistenceCoordinator(options);
        var commits = new SubmissionCommitCoordinator(persistence);
        await using var views = new SearchViewCoordinator(
            supervisor,
            persistence,
            commits,
            new SearchViewCursorCodec(options),
            NullLogger<SearchViewCoordinator>.Instance);
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);

            await Assert.ThrowsExactlyAsync<SearchViewUnavailableException>(() =>
                views.CreateAsync(
                    Guid.NewGuid(),
                    new CreateSearchViewRequestDto(SearchViewProjectionKind.Files),
                    CancellationToken.None));

            JobSummaryDto direct = await supervisor.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("still accepted"),
                CancellationToken.None);
            Assert.AreEqual(ServerJobKind.Search, direct.Kind);
            Assert.IsNotNull(direct.SubmissionId);
        }
        finally
        {
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task IncompleteViewResumesFromDurableSequenceAfterDaemonRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-search-view-restart-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        Guid viewId;
        long prefixRevision;
        try
        {
            var firstClient = ControllableSearchClientProxy.Create();
            var searchStarted = new TaskCompletionSource<Action<SearchResponse>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var holdSearch = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            firstClient.Search = async (handler, cancellationToken) =>
            {
                searchStarted.TrySetResult(handler);
                await holdSearch.Task.WaitAsync(cancellationToken);
            };
            await using (WebApplication first = ServerHost.Build(
                [],
                Options(directory, firstClient.Client),
                "http://127.0.0.1:0"))
            {
                await first.StartAsync();
                EngineSupervisor supervisor = first.Services.GetRequiredService<EngineSupervisor>();
                JobSummaryDto job = await supervisor.SubmitSearchJobAsync(
                    new SubmitSearchJobRequestDto("Restart Track"),
                    CancellationToken.None);
                Action<SearchResponse> receive = await searchStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                SearchViewCoordinator views = first.Services.GetRequiredService<SearchViewCoordinator>();
                SearchViewSummaryDto created = (await views.CreateAsync(
                    job.JobId,
                    new CreateSearchViewRequestDto(
                        SearchViewProjectionKind.GenericDirectories,
                        new SongQueryDto(Title: "Restart Track"),
                        IncludeFullResults: true),
                    CancellationToken.None))!;
                viewId = created.ViewId;
                prefixRevision = await PublishAsync(views, viewId, () => receive(Response(
                    "restart-peer",
                    [File(@"Music\Restart Track.flac")])));
                await first.StopAsync();
            }

            var secondClient = ControllableSearchClientProxy.Create();
            await using (WebApplication second = ServerHost.Build(
                [],
                Options(directory, secondClient.Client),
                "http://127.0.0.1:0"))
            {
                SearchViewCoordinator views = second.Services.GetRequiredService<SearchViewCoordinator>();
                var recovered = new TaskCompletionSource<long>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void OnPublished(Guid id, long revision)
                {
                    if (id == viewId && revision > prefixRevision)
                        recovered.TrySetResult(revision);
                }
                views.ViewPublished += OnPublished;
                try
                {
                    await second.StartAsync();
                    long recoveredRevision = await recovered.Task.WaitAsync(
                        TimeSpan.FromSeconds(5));
                    SearchViewSummaryDto summary = (await views.GetAsync(
                        viewId,
                        CancellationToken.None))!;
                    Assert.AreEqual(recoveredRevision, summary.Revision);
                    Assert.IsTrue(summary.IsComplete);
                    Assert.IsTrue(summary.RetentionState is
                        SearchViewRetentionState.Interrupted or SearchViewRetentionState.Incomplete);
                    Assert.AreEqual(1L, summary.Counters.PublicFileCount);
                    Assert.AreEqual(1L, summary.Counters.ProjectedFileCount);
                    SearchViewDirectoryPageDto recoveredDirectories = (await views.GetDirectoriesAsync(
                        viewId,
                        prefixRevision,
                        null,
                        10,
                        CancellationToken.None))!;
                    Assert.AreEqual(1, recoveredDirectories.Items.Count);
                    Assert.AreEqual(@"Music", recoveredDirectories.Items[0].Ref.FolderPath);
                }
                finally
                {
                    views.ViewPublished -= OnPublished;
                    await second.StopAsync();
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LiveRevisionsExposeExactPrefixAndCompletionUsesTheSameProjection()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-live-search-view-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var client = ControllableSearchClientProxy.Create();
        var searchStarted = new TaskCompletionSource<Action<SearchResponse>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSearch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (handler, cancellationToken) =>
        {
            searchStarted.TrySetResult(handler);
            await releaseSearch.Task.WaitAsync(cancellationToken);
        };
        client.Browse = _ => new BrowseResponse(
        [
            new Soulseek.Directory(
                "Music",
                [
                    File("Track one.flac"),
                    File("Cover.jpg"),
                ]),
        ]);
        await using WebApplication app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                Username = "local",
                Password = "password",
                ListenPort = null,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
            ClientFactory = _ => client.Client,
        }, "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();
            EngineSupervisor supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            JobSummaryDto job = await supervisor.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("Track"),
                CancellationToken.None);
            Action<SearchResponse> receive = await searchStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            SearchViewCoordinator views = app.Services.GetRequiredService<SearchViewCoordinator>();
            SearchViewSummaryDto view = (await views.CreateAsync(
                job.JobId,
                new CreateSearchViewRequestDto(SearchViewProjectionKind.Files),
                CancellationToken.None))!;
            SearchViewSummaryDto directoryView = (await views.CreateAsync(
                job.JobId,
                new CreateSearchViewRequestDto(
                    SearchViewProjectionKind.GenericDirectories,
                    new SongQueryDto(Title: "Track"),
                    IncludeFullResults: true),
                CancellationToken.None))!;

            var firstDirectoryPublished = ObserveNext(views, directoryView.ViewId);
            long firstRevision;
            try
            {
                firstRevision = await PublishAsync(views, view.ViewId, () => receive(Response(
                    "first",
                    [File(@"Music\Track one.flac")])));
                _ = await firstDirectoryPublished.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                views.ViewPublished -= firstDirectoryPublished.Handler;
            }
            SearchViewSummaryDto first = (await views.GetAsync(
                view.ViewId,
                CancellationToken.None))!;
            Assert.IsFalse(first.IsComplete);
            Assert.AreEqual(1L, first.Counters.PublicFileCount);
            Assert.AreEqual(1L, first.Counters.ProjectedFileCount);
            SearchViewFilePageDto firstPage = (await views.GetFilesAsync(
                view.ViewId,
                firstRevision,
                cursor: null,
                limit: 10,
                CancellationToken.None))!;
            Assert.AreEqual(1, firstPage.Items.Count);
            Assert.AreEqual("first", firstPage.Items[0].Peer.Username);
            Assert.IsTrue(firstPage.Items[0].NecessaryConditionsSatisfied);
            SearchViewSummaryDto firstDirectorySummary = (await views.GetAsync(
                directoryView.ViewId,
                CancellationToken.None))!;
            Assert.AreEqual(1L, firstDirectorySummary.Counters.TopLevelItemCount);
            SearchViewDirectoryPageDto firstDirectories = (await views.GetDirectoriesAsync(
                directoryView.ViewId,
                firstDirectorySummary.Revision,
                null,
                10,
                CancellationToken.None))!;
            Assert.AreEqual(1, firstDirectories.Items.Count);
            Assert.AreEqual(@"Music", firstDirectories.Items[0].Ref.FolderPath);
            SearchViewDirectoryFilePageDto firstChildren = (await views.GetDirectoryFilesAsync(
                directoryView.ViewId,
                firstDirectories.Items[0].Ref.Ref,
                firstDirectorySummary.Revision,
                null,
                10,
                CancellationToken.None))!;
            Assert.AreEqual("Track one.flac", firstChildren.Items.Single().RelativePath);

            var retrievalPublished = ObserveNext(views, directoryView.ViewId);
            JobSummaryDto? retrieval;
            try
            {
                retrieval = await views.StartDirectoryRetrievalAsync(
                    directoryView.ViewId,
                    new RetrieveSearchViewDirectoryRequestDto(
                        firstDirectorySummary.Revision,
                        firstDirectories.Items[0].Ref),
                    CancellationToken.None);
                _ = await retrievalPublished.Completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }
            finally
            {
                views.ViewPublished -= retrievalPublished.Handler;
            }
            Assert.IsNotNull(retrieval);
            SearchViewSummaryDto retrievedSummary = (await views.GetAsync(
                directoryView.ViewId,
                CancellationToken.None))!;
            Assert.IsTrue(retrievedSummary.Revision > firstDirectorySummary.Revision);
            SearchViewDirectoryDto retrieved = (await views.GetDirectoriesAsync(
                directoryView.ViewId,
                retrievedSummary.Revision,
                null,
                10,
                CancellationToken.None))!.Items.Single();
            Assert.AreEqual(SearchViewDirectoryRetrievalState.Complete, retrieved.RetrievalState);
            Assert.AreEqual(2L, retrieved.RetrievedFileCount);
            Assert.AreEqual(2, (await views.GetDirectoryFilesAsync(
                directoryView.ViewId,
                retrieved.Ref.Ref,
                retrievedSummary.Revision,
                null,
                10,
                CancellationToken.None))!.Items.Count);
            Assert.AreEqual(1, (await views.GetDirectoryFilesAsync(
                directoryView.ViewId,
                retrieved.Ref.Ref,
                firstDirectorySummary.Revision,
                null,
                10,
                CancellationToken.None))!.Items.Count,
                "Retrieval must not mutate the issuing revision.");

            var secondDirectoryPublished = ObserveNext(views, directoryView.ViewId);
            long secondRevision;
            try
            {
                secondRevision = await PublishAsync(views, view.ViewId, () => receive(Response(
                    "second",
                    [File(@"Music\Track two.flac")],
                    [File(@"Private\Track locked.flac")])));
                _ = await secondDirectoryPublished.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                views.ViewPublished -= secondDirectoryPublished.Handler;
            }
            SearchViewSummaryDto second = (await views.GetAsync(
                view.ViewId,
                CancellationToken.None))!;
            Assert.IsFalse(second.IsComplete);
            Assert.AreEqual(2L, second.Counters.PublicFileCount);
            Assert.AreEqual(1L, second.Counters.LockedFileCount);
            Assert.AreEqual(3L, second.Counters.ProjectedFileCount);
            Assert.AreEqual(2, second.Counters.ObservedPeerCount);
            Assert.AreEqual(1, (await views.GetFilesAsync(
                view.ViewId,
                firstRevision,
                null,
                10,
                CancellationToken.None))!.Items.Count,
                "An older revision must remain an exact prefix after new rows arrive.");

            SearchViewUpdateDto update = (await views.GetUpdatesAsync(
                view.ViewId,
                afterRevision: firstRevision,
                CancellationToken.None))!;
            Assert.AreEqual(secondRevision, update.Summary.Revision);
            Assert.IsTrue(update.HasNewRevision);

            SearchViewSummaryDto secondDirectorySummary = (await views.GetAsync(
                directoryView.ViewId,
                CancellationToken.None))!;
            Assert.AreEqual(3L, secondDirectorySummary.Counters.TopLevelItemCount);
            Assert.AreEqual(2L, secondDirectorySummary.Counters.SelectableOptionCount);
            Assert.AreEqual(1, (await views.GetDirectoriesAsync(
                directoryView.ViewId,
                firstDirectorySummary.Revision,
                null,
                10,
                CancellationToken.None))!.Items.Count,
                "Older directory pages remain bound to their exact observed prefix.");

            Guid idempotencyKey = Guid.NewGuid();
            var commitRequest = new CommitSearchViewSelectionRequestDto(
                secondRevision,
                new RefSelectionExpressionDto(RefSelectionMode.AllExcept, []),
                idempotencyKey);
            CommitSearchViewSelectionResponseDto receipt = (await views.CommitSelectionAsync(
                view.ViewId,
                commitRequest,
                CancellationToken.None))!;
            Assert.AreEqual(secondRevision, receipt.ViewRevision);
            Assert.AreEqual(3L, receipt.RequestedCount);
            Assert.AreEqual(3L, receipt.ResolvedCount);
            Assert.AreEqual(2L, receipt.SubmittedCount);
            Assert.AreEqual(1L, receipt.RejectedCount);
            Assert.AreEqual("locked", receipt.RejectionReasons.Single().Reason);
            Assert.AreEqual(1L, receipt.RejectionReasons.Single().Count);
            Assert.IsNotNull(receipt.SubmissionId);
            CommitSearchViewSelectionResponseDto repeated = (await views.CommitSelectionAsync(
                view.ViewId,
                commitRequest,
                CancellationToken.None))!;
            Assert.AreEqual(receipt.SubmissionId, repeated.SubmissionId);
            Assert.AreEqual(receipt.RequestedCount, repeated.RequestedCount);
            await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                views.CommitSelectionAsync(
                    view.ViewId,
                    commitRequest with
                    {
                        Selection = new RefSelectionExpressionDto(
                            RefSelectionMode.Only,
                            []),
                    },
                    CancellationToken.None));

            CommitSearchViewSelectionResponseDto onlyReceipt = (await views.CommitSelectionAsync(
                view.ViewId,
                new CommitSearchViewSelectionRequestDto(
                    secondRevision,
                    new RefSelectionExpressionDto(
                        RefSelectionMode.Only,
                        [firstPage.Items[0].Ref, "missing-ref"]),
                    Guid.NewGuid()),
                CancellationToken.None))!;
            Assert.AreEqual(2L, onlyReceipt.RequestedCount);
            Assert.AreEqual(1L, onlyReceipt.ResolvedCount);
            Assert.AreEqual(1L, onlyReceipt.SubmittedCount);
            Assert.AreEqual(1L, onlyReceipt.RejectedCount);
            Assert.AreEqual("missing-ref", onlyReceipt.RejectionReasons.Single().Reason);

            var completedDirectoryPublished = ObserveNext(views, directoryView.ViewId);
            long completedRevision = await PublishAsync(
                views,
                view.ViewId,
                () => releaseSearch.TrySetResult());
            try
            {
                _ = await completedDirectoryPublished.Completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }
            finally
            {
                views.ViewPublished -= completedDirectoryPublished.Handler;
            }
            SearchViewSummaryDto completed = (await views.GetAsync(
                view.ViewId,
                CancellationToken.None))!;
            Assert.AreEqual(completedRevision, completed.Revision);
            Assert.IsTrue(completed.IsComplete);
            Assert.AreEqual(SearchViewRetentionState.Complete, completed.RetentionState);
            Assert.AreEqual(second.Counters, completed.Counters);
            SearchViewUpdateDto completionUpdate = (await views.GetUpdatesAsync(
                view.ViewId,
                secondRevision,
                CancellationToken.None))!;
            Assert.IsTrue(completionUpdate.HasNewRevision);
            Assert.AreEqual(completedRevision, completionUpdate.Summary.Revision);
            Assert.IsFalse((await views.GetUpdatesAsync(
                view.ViewId,
                completedRevision,
                CancellationToken.None))!.HasNewRevision);
        }
        finally
        {
            releaseSearch.TrySetResult();
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<long> PublishAsync(
        SearchViewCoordinator views,
        Guid viewId,
        Action publish)
    {
        var observed = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(Guid id, long revision)
        {
            if (id == viewId)
                observed.TrySetResult(revision);
        }
        views.ViewPublished += OnPublished;
        try
        {
            publish();
            return await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            views.ViewPublished -= OnPublished;
        }
    }

    private static (
        TaskCompletionSource<long> Completion,
        Action<Guid, long> Handler) ObserveNext(
        SearchViewCoordinator views,
        Guid viewId)
    {
        var completion = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(Guid id, long revision)
        {
            if (id == viewId)
                completion.TrySetResult(revision);
        }
        views.ViewPublished += Handler;
        return (completion, Handler);
    }

    private static SearchResponse Response(
        string username,
        IReadOnlyCollection<Soulseek.File> files,
        IReadOnlyCollection<Soulseek.File>? locked = null)
        => new(
            username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100_000,
            queueLength: 2,
            fileList: files,
            lockedFileList: locked ?? []);

    private static Soulseek.File File(string path)
        => new(1, path, 1_000, Path.GetExtension(path), []);

    private static ServerOptions Options(string directory, ISoulseekClient client)
        => new()
        {
            Engine = new EngineSettings
            {
                Username = "local",
                Password = "password",
                ListenPort = null,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
            ClientFactory = _ => client,
        };
}

internal class ControllableSearchClientProxy : DispatchProxy
{
    private readonly Dictionary<string, Delegate?> handlers = new(StringComparer.Ordinal);

    public ISoulseekClient Client { get; private set; } = null!;
    public Func<Action<SearchResponse>, CancellationToken, Task>? Search { get; set; }
    public Func<string, BrowseResponse>? Browse { get; set; }

    public static ControllableSearchClientProxy Create()
    {
        ISoulseekClient client = DispatchProxy.Create<ISoulseekClient, ControllableSearchClientProxy>();
        var proxy = (ControllableSearchClientProxy)(object)client;
        proxy.Client = client;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal))
        {
            string name = targetMethod.Name[4..];
            handlers[name] = Delegate.Combine(handlers.GetValueOrDefault(name), (Delegate)args[0]!);
            return null;
        }
        if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal))
        {
            string name = targetMethod.Name[7..];
            handlers[name] = Delegate.Remove(handlers.GetValueOrDefault(name), (Delegate)args[0]!);
            return null;
        }
        return targetMethod.Name switch
        {
            "get_State" => SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn,
            "get_MajorVersion" => 170,
            "get_MinorVersion" => 800850000,
            "SearchAsync" when targetMethod.ReturnType == typeof(Task<Search>)
                => SearchAsync(args),
            "BrowseAsync" when targetMethod.ReturnType == typeof(Task<BrowseResponse>)
                => Task.FromResult(Browse?.Invoke((string)args[0]!) ?? new BrowseResponse()),
            "ConnectAsync" => Task.CompletedTask,
            "Dispose" => null,
            _ => DefaultReturn(targetMethod.ReturnType),
        };
    }

    private async Task<Search> SearchAsync(object?[] args)
    {
        Action<SearchResponse> handler = args.OfType<Action<SearchResponse>>().Single();
        CancellationToken cancellationToken = args.OfType<CancellationToken>().LastOrDefault();
        if (Search != null)
            await Search(handler, cancellationToken);
        return null!;
    }

    private static object? DefaultReturn(Type type)
    {
        if (type == typeof(void))
            return null;
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            Type resultType = type.GetGenericArguments()[0];
            object? value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [value]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
