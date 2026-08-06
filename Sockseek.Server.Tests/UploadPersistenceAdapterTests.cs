using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class UploadPersistenceAdapterTests
{
    [TestMethod]
    public async Task QueuedCancellationPersistsZeroAttemptsAndDispatchPersistsExactlyOne()
    {
        var catalog = new FakeCatalog();
        var protocol = new BlockingProtocol();
        await using var coordinator = new UploadCoordinator(
            catalog,
            protocol,
            new PeerAccessPolicy(new PeerAccessSettings()),
            new UploadScheduler(new UploadSettings { Slots = 1 }));
        var sink = new RecordingSink();
        var adapter = new UploadPersistenceAdapter(Guid.NewGuid(), sink);
        adapter.Attach(coordinator);

        UploadCoordinatorAdmission active = await coordinator.AdmitAsync(
            "alice",
            null,
            @"Public\One.bin");
        await protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        UploadCoordinatorAdmission queued = await coordinator.AdmitAsync(
            "bob",
            null,
            @"Public\Two.bin");

        Assert.IsTrue(coordinator.Cancel(queued.TransferId!.Value));
        TransferTerminalPersistenceMutation queuedTerminal = await WaitForTerminalAsync(
            sink,
            queued.TransferId.Value);
        Assert.AreEqual(0, queuedTerminal.Transfer.AttemptCount);
        Assert.IsNull(queuedTerminal.FinalAttempt);

        protocol.Release.TrySetResult();
        TransferTerminalPersistenceMutation activeTerminal = await WaitForTerminalAsync(
            sink,
            active.TransferId!.Value);
        Assert.AreEqual(1, activeTerminal.Transfer.AttemptCount);
        Assert.IsNotNull(activeTerminal.FinalAttempt);
        Assert.AreEqual(1, activeTerminal.FinalAttempt.AttemptNumber);

        adapter.Detach(coordinator);
    }

    private static async Task<TransferTerminalPersistenceMutation> WaitForTerminalAsync(
        RecordingSink sink,
        Guid transferId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            TransferTerminalPersistenceMutation? terminal = sink.Items
                .OfType<TransferTerminalPersistenceMutation>()
                .LastOrDefault(item => item.Transfer.TransferId == transferId);
            if (terminal is not null)
                return terminal;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingSink : IPersistenceMutationSink
    {
        private readonly ConcurrentQueue<PersistenceMutation> items = [];
        public IReadOnlyCollection<PersistenceMutation> Items => items.ToArray();

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            items.Enqueue(mutation);
            return true;
        }
    }

    private sealed class BlockingProtocol : IUploadProtocolInvoker
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UploadProtocolOutcome> UploadAsync(
            string username,
            string remotePath,
            long sizeBytes,
            Func<long, Task<Stream>> streamFactory,
            Action initializingCompleted,
            Action<long, double> progress,
            CancellationToken cancellationToken)
        {
            initializingCompleted();
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return UploadProtocolOutcome.Cancelled;
        }
    }

    private sealed class FakeCatalog : IShareCatalogProvider, IShareCatalogReader
    {
        private readonly Dictionary<RemotePathKey, ShareCatalogResolvedFile> files;

        public FakeCatalog()
        {
            var root = new ShareCatalogRoot(
                1,
                "Public",
                Path.GetTempPath(),
                RemotePathKey.CreateAlias("Public"));
            files = new[]
                {
                    CreateFile(root, 1, @"Public\One.bin"),
                    CreateFile(root, 2, @"Public\Two.bin"),
                }
                .ToDictionary(item => item.File.ComparisonPath);
        }

        public ShareCatalogMetadata Metadata { get; } = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "settings",
            1,
            2,
            200,
            ShareBrowseStatus.Ready,
            ShareCatalogVersions.BrowseWire,
            1,
            new string('A', 64));

        public bool TryAcquire(out IShareCatalogLease? lease)
        {
            lease = new FakeLease(this);
            return true;
        }

        public ValueTask<ShareCatalogResolvedFile?> ResolveFileAsync(
            RemotePathKey remotePath,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                files.TryGetValue(remotePath, out ShareCatalogResolvedFile? file)
                    ? file
                    : null);

        public ValueTask<IReadOnlyList<ShareCatalogFile>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ShareCatalogFile>>([]);

        public ValueTask<ShareCatalogBrowseDirectory?> GetDirectoryAsync(
            RemotePathKey remotePath,
            int fileLimit,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ShareCatalogBrowseDirectory?>(null);

        public async IAsyncEnumerable<ShareCatalogBrowseDirectory> EnumerateBrowseAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ShareCatalogResolvedFile CreateFile(
            ShareCatalogRoot root,
            long id,
            string remotePath)
        {
            string name = remotePath[(remotePath.LastIndexOf('\\') + 1)..];
            var file = new ShareCatalogFile(
                id,
                root.RootId,
                1,
                name,
                remotePath,
                RemotePathKey.Create(remotePath),
                name,
                100,
                DateTimeOffset.UnixEpoch,
                1,
                "bin",
                []);
            return new ShareCatalogResolvedFile(root, file);
        }

        private sealed class FakeLease(IShareCatalogReader reader) : IShareCatalogLease
        {
            public IShareCatalogReader Reader { get; } = reader;
            public ShareCatalogMetadata Metadata => Reader.Metadata;
            public ShareBrowseStream OpenBrowseStream(
                TimeSpan idleTimeout,
                Action? releasePermit = null) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
