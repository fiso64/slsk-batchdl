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
        TransferTerminalPersistenceMutation queuedTerminal = await sink.WaitForTerminalAsync(
            queued.TransferId.Value);
        Assert.AreEqual(0, queuedTerminal.Transfer.AttemptCount);
        Assert.IsNull(queuedTerminal.FinalAttempt);

        protocol.Release.TrySetResult();
        TransferTerminalPersistenceMutation activeTerminal = await sink.WaitForTerminalAsync(
            active.TransferId!.Value);
        Assert.AreEqual(1, activeTerminal.Transfer.AttemptCount);
        Assert.IsNotNull(activeTerminal.FinalAttempt);
        Assert.AreEqual(1, activeTerminal.FinalAttempt.AttemptNumber);

        adapter.Detach(coordinator);
    }

    private sealed class RecordingSink : IPersistenceMutationSink
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<TransferTerminalPersistenceMutation>> terminals = [];

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            if (mutation is TransferTerminalPersistenceMutation terminal)
            {
                terminals.GetOrAdd(
                        terminal.Transfer.TransferId,
                        static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult(terminal);
            }
            return true;
        }

        public Task<TransferTerminalPersistenceMutation> WaitForTerminalAsync(Guid transferId)
            => terminals.GetOrAdd(
                    transferId,
                    static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task
                .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class BlockingProtocol : IUploadProtocolInvoker
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Completion is deliberately synchronous: this fake controls the exact
        // handoff being asserted and must not depend on a saturated CI thread pool
        // scheduling the protocol continuation within an arbitrary timeout.
        public TaskCompletionSource Release { get; } = new();

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
