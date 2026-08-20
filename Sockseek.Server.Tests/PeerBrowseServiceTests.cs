using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.PeerBrowsing;
using Sockseek.Server.PeerBrowsing;
using SoulseekClientStates = Soulseek.SoulseekClientStates;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PeerBrowseServiceTests
{
    [TestMethod]
    public async Task OrdinaryAndRefreshCallers_JoinOneInFlightGeneration()
    {
        await using var fixture = await Fixture.CreateAsync(gated: true);

        PeerBrowseResource first = await fixture.Service.StartAsync("Peer");
        await fixture.Transport.Started.Task;
        PeerBrowseResource joinedRefresh = await fixture.Service.StartAsync("Peer", refresh: true);

        Assert.AreEqual(first.BrowseId, joinedRefresh.BrowseId);
        Assert.AreEqual(1, fixture.Transport.CallCount);
        fixture.Transport.Release.TrySetResult();
        PeerBrowseResource completed = await WaitForTerminalAsync(fixture.Service, first.BrowseId);
        PeerBrowseResource reused = await fixture.Service.StartAsync("Peer");

        Assert.AreEqual(PeerBrowseState.Complete, completed.State);
        Assert.AreEqual(first.BrowseId, reused.BrowseId);
        Assert.AreEqual(1, fixture.Transport.CallCount);
    }

    [TestMethod]
    public async Task RefreshAfterCompletion_CreatesNewGeneration()
    {
        await using var fixture = await Fixture.CreateAsync();
        PeerBrowseResource first = await fixture.Service.WaitForCompletionAsync("Peer");

        PeerBrowseResource refresh = await fixture.Service.StartAsync("Peer", refresh: true);
        PeerBrowseResource second = await WaitForTerminalAsync(fixture.Service, refresh.BrowseId);

        Assert.AreNotEqual(first.BrowseId, second.BrowseId);
        Assert.AreEqual(2, fixture.Transport.CallCount);
    }

    [TestMethod]
    public async Task WaiterCancellation_DetachesWithoutCancellingAcquisition()
    {
        await using var fixture = await Fixture.CreateAsync(gated: true);
        using var waiterCancellation = new CancellationTokenSource();
        Task<PeerBrowseResource> waiter = fixture.Service.WaitForCompletionAsync(
            "Peer",
            cancellationToken: waiterCancellation.Token);
        await fixture.Transport.Started.Task;

        waiterCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiter);
        Assert.IsFalse(fixture.Transport.CancellationObserved);

        fixture.Transport.Release.TrySetResult();
        PeerBrowseResource completed = await fixture.Service.WaitForCompletionAsync("Peer");
        Assert.AreEqual(PeerBrowseState.Complete, completed.State);
        Assert.AreEqual(1, fixture.Transport.CallCount);
    }

    [TestMethod]
    public async Task ExplicitCancel_CancelsSharedAcquisitionForAllWaiters()
    {
        await using var fixture = await Fixture.CreateAsync(gated: true);
        PeerBrowseResource started = await fixture.Service.StartAsync("Peer");
        await fixture.Transport.Started.Task;
        Task<PeerBrowseResource> joinedWaiter = fixture.Service.WaitForCompletionAsync("Peer");

        PeerBrowseResource? cancelled = await fixture.Service.CancelAsync(started.BrowseId);

        Assert.AreEqual(PeerBrowseState.Cancelled, cancelled!.State);
        Assert.IsTrue(fixture.Transport.CancellationObserved);
        await Assert.ThrowsExactlyAsync<PeerBrowseAcquisitionException>(
            async () => await joinedWaiter);
    }

    [TestMethod]
    public async Task FailedRefresh_PreservesFreshPreviousGenerationForNextOrdinaryCall()
    {
        await using var fixture = await Fixture.CreateAsync();
        PeerBrowseResource first = await fixture.Service.WaitForCompletionAsync("Peer");
        fixture.Transport.FailNext = true;

        PeerBrowseResource refresh = await fixture.Service.StartAsync("Peer", refresh: true);
        PeerBrowseResource failed = await WaitForTerminalAsync(fixture.Service, refresh.BrowseId);
        PeerBrowseResource reused = await fixture.Service.StartAsync("Peer");

        Assert.AreEqual(PeerBrowseState.Failed, failed.State);
        Assert.AreEqual(first.BrowseId, reused.BrowseId);
        Assert.AreEqual(2, fixture.Transport.CallCount);
    }

    [TestMethod]
    public async Task StartWithoutLoggedInAccountReportsUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.LocalAccount = null;

        PeerBrowseUnavailableException exception =
            await Assert.ThrowsExactlyAsync<PeerBrowseUnavailableException>(
                () => fixture.Service.StartAsync("Peer"));

        Assert.AreEqual("Soulseek is not logged in.", exception.Message);
        Assert.AreEqual(0, fixture.Transport.CallCount);
    }

    [TestMethod]
    public async Task CancellationAfterAtomicPromotion_DoesNotReplaceCompletedState()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Transport.GateAfterComplete = true;
        PeerBrowseResource started = await fixture.Service.StartAsync("Peer");
        await fixture.Transport.CompletedArtifact.Task.WaitAsync(TimeSpan.FromSeconds(2));

        PeerBrowseResource? result = await fixture.Service.CancelAsync(started.BrowseId);

        Assert.AreEqual(PeerBrowseState.Complete, result!.State);
        Assert.AreEqual(PeerBrowsePhase.Ready, result.Phase);
    }

    [TestMethod]
    public async Task LifecyclePublishesIndexingBeforeReady()
    {
        await using var fixture = await Fixture.CreateAsync();
        var phases = new List<PeerBrowsePhase>();
        fixture.Service.Changed += resource => phases.Add(resource.Phase);

        await fixture.Service.WaitForCompletionAsync("Peer");

        CollectionAssert.Contains(phases, PeerBrowsePhase.Indexing);
        Assert.AreEqual(PeerBrowsePhase.Ready, phases[^1]);
    }

    [TestMethod]
    public async Task AcquisitionKey_IncludesExactAccountAndUsername()
    {
        await using var fixture = await Fixture.CreateAsync();
        PeerBrowseResource first = await fixture.Service.WaitForCompletionAsync("Peer");
        fixture.LocalAccount = "other-local";

        PeerBrowseResource second = await fixture.Service.WaitForCompletionAsync("Peer");
        PeerBrowseResource differentCase = await fixture.Service.WaitForCompletionAsync("peer");

        Assert.AreNotEqual(first.BrowseId, second.BrowseId);
        Assert.AreNotEqual(second.BrowseId, differentCase.BrowseId);
        Assert.AreEqual(3, fixture.Transport.CallCount);
    }

    [TestMethod]
    public async Task UsernamesPreserveSpacesAndUnicodeNormalizationForms()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Service.WaitForCompletionAsync(" Peer ");
        await fixture.Service.WaitForCompletionAsync("é");
        await fixture.Service.WaitForCompletionAsync("e\u0301");

        CollectionAssert.AreEqual(
            new[] { " Peer ", "é", "e\u0301" },
            fixture.Transport.Usernames.ToArray());
    }

    [TestMethod]
    public async Task LearnedArtifactId_IsRecheckedAgainstCurrentPeerPolicy()
    {
        await using var fixture = await Fixture.CreateAsync();
        PeerBrowseResource completed = await fixture.Service.WaitForCompletionAsync("Peer");
        await using var denied = new PeerBrowseService(
            fixture.Store,
            new FakeTransport(gated: false),
            () => "local",
            new PeerAccessPolicy(new PeerAccessSettings
            {
                BlockedUsernames = ["Peer"],
            }));

        await Assert.ThrowsExactlyAsync<PeerBrowseAccessDeniedException>(() =>
            denied.GetAccessibleAsync(completed.BrowseId));
        Assert.AreEqual(
            0,
            (await denied.ListAsync(null, null, null, null, 20)).Items.Count);
    }

    [TestMethod]
    public async Task WrappedTimeoutHasStableTerminalFailureCode()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Transport.Exception = new InvalidOperationException("wrapper", new TimeoutException());

        PeerBrowseResource started = await fixture.Service.StartAsync("Peer");
        PeerBrowseResource terminal = await WaitForTerminalAsync(fixture.Service, started.BrowseId);

        Assert.AreEqual(PeerBrowseState.Failed, terminal.State);
        Assert.AreEqual("peer-timeout", terminal.Failure!.Code);
    }

    [TestMethod]
    public async Task ConnectionLossFailsActiveBrowseWithStableCodeAndAllowsReconnection()
    {
        await using var fixture = await Fixture.CreateAsync(gated: true);
        fixture.Service.OnSoulseekStateChanged(
            SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);
        PeerBrowseResource started = await fixture.Service.StartAsync("Peer");
        await fixture.Transport.Started.Task;

        fixture.Service.OnSoulseekStateChanged(SoulseekClientStates.None);
        PeerBrowseResource failed = await WaitForTerminalAsync(fixture.Service, started.BrowseId);

        Assert.AreEqual(PeerBrowseState.Failed, failed.State);
        Assert.AreEqual("connection-lost", failed.Failure!.Code);
        Assert.IsTrue(fixture.Transport.CancellationObserved);

        fixture.Service.OnSoulseekStateChanged(
            SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);
        fixture.Transport.Release.TrySetResult();
        PeerBrowseResource recovered = await fixture.Service.WaitForCompletionAsync("Peer");

        Assert.AreEqual(PeerBrowseState.Complete, recovered.State);
        Assert.AreEqual(2, fixture.Transport.CallCount);
    }

    [TestMethod]
    public async Task PreLoginStateTransitionsDoNotCancelBrowseThatIsStartingTheSession()
    {
        await using var fixture = await Fixture.CreateAsync(gated: true);
        PeerBrowseResource started = await fixture.Service.StartAsync("Peer");
        await fixture.Transport.Started.Task;

        fixture.Service.OnSoulseekStateChanged(SoulseekClientStates.Connecting);
        fixture.Service.OnSoulseekStateChanged(SoulseekClientStates.Connected);

        Assert.IsFalse(fixture.Transport.CancellationObserved);
        Assert.AreEqual(PeerBrowseState.Running, (await fixture.Service.GetAsync(started.BrowseId))!.State);
        fixture.Transport.Release.TrySetResult();
        PeerBrowseResource complete = await WaitForTerminalAsync(fixture.Service, started.BrowseId);
        Assert.AreEqual(PeerBrowseState.Complete, complete.State);
    }

    private static async Task<PeerBrowseResource> WaitForTerminalAsync(
        PeerBrowseService service,
        Guid browseId)
    {
        for (int attempt = 0; attempt < 1_000; attempt++)
        {
            PeerBrowseResource? resource = await service.GetAsync(browseId);
            if (resource?.State is PeerBrowseState.Complete
                or PeerBrowseState.Failed
                or PeerBrowseState.Cancelled)
            {
                return resource;
            }
            await Task.Yield();
        }
        Assert.Fail("Peer browse did not reach a terminal state.");
        throw new InvalidOperationException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;

        private Fixture(string directory, FakeTransport transport)
        {
            this.directory = directory;
            Transport = transport;
            Store = new PeerBrowseArtifactStore(directory);
            Service = new PeerBrowseService(
                Store,
                Transport,
                () => LocalAccount,
                new PeerAccessPolicy(new PeerAccessSettings()));
        }

        public string? LocalAccount { get; set; } = "local";
        public FakeTransport Transport { get; }
        public PeerBrowseArtifactStore Store { get; }
        public PeerBrowseService Service { get; }

        public static async Task<Fixture> CreateAsync(bool gated = false)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "sockseek-peer-service-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fixture = new Fixture(directory, new FakeTransport(gated));
            await fixture.Store.InitializeAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult();
            await Service.DisposeAsync();
            for (int attempt = 0; Directory.Exists(directory); attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException) when (attempt < 9)
                {
                    // Windows can briefly retain a just-closed SQLite file handle.
                    await Task.Delay(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 9)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    private sealed class FakeTransport(bool gated) : IPeerBrowseTransport
    {
        private int callCount;
        public List<string> Usernames { get; } = [];
        public int CallCount => Volatile.Read(ref callCount);
        public bool FailNext { get; set; }
        public Exception? Exception { get; set; }
        public bool GateAfterComplete { get; set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompletedArtifact { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ReceiveAsync(
            string username,
            IPeerBrowseRowSink sink,
            Action<PeerBrowseTransportProgress>? transportProgress = null,
            Action<PeerBrowseIndexProgress>? indexProgress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            lock (Usernames)
                Usernames.Add(username);
            Started.TrySetResult();
            if (gated)
            {
                try
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            if (FailNext)
            {
                FailNext = false;
                throw new PeerBrowseProtocolException("Invalid fixture response.");
            }
            if (Exception is { } exception)
                throw exception;

            transportProgress?.Invoke(new PeerBrowseTransportProgress(10, 10));
            await sink.BeginDirectoryAsync("Music", PeerShareVisibility.Public, 1, cancellationToken);
            await sink.BeginFileAsync(new PeerBrowseWireFile(1, username + ".mp3", 10, "mp3", 0), cancellationToken);
            await sink.EndFileAsync(cancellationToken);
            indexProgress?.Invoke(new PeerBrowseIndexProgress(1, 1, 10));
            await sink.CompleteAsync(cancellationToken);
            CompletedArtifact.TrySetResult();
            if (GateAfterComplete)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
