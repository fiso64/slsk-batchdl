using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Tests.ClientTests;
using System.Net;

namespace Tests.Core;

[TestClass]
public class SoulseekClientManagerTests
{
    [TestMethod]
    public void CreateRandomStartingToken_ReturnsPositiveToken()
    {
        for (int i = 0; i < 20; i++)
        {
            int token = SoulseekClientManager.CreateRandomStartingToken();

            Assert.IsTrue(token > 0);
            Assert.IsTrue(token < int.MaxValue);
        }
    }

    [TestMethod]
    public void CreateClientOptions_UsesProvidedStartingToken_WithListener()
    {
        const int token = 123_456_789;
        var options = SoulseekClientManager.CreateClientOptions(
            new EngineSettings { ListenPort = 49998 },
            token);

        Assert.AreEqual(token, options.StartingToken);
    }

    [TestMethod]
    public void CreateClientOptions_UsesProvidedStartingToken_WithoutListener()
    {
        const int token = 987_654_321;
        var options = SoulseekClientManager.CreateClientOptions(
            new EngineSettings { ListenPort = null },
            token);

        Assert.AreEqual(token, options.StartingToken);
    }

    [TestMethod]
    public void CreateClientOptions_WiresSharingCallbacksSlotsAndSpeed()
    {
        var settings = new EngineSettings
        {
            ListenPort = 49998,
            Uploads = new UploadSettings
            {
                Slots = 7,
                SpeedLimitKiBPerSecond = 123,
            },
        };
        var router = new FakeInboundRouter();

        SoulseekClientOptions options =
            SoulseekClientManager.CreateClientOptions(settings, 1, router);

        Assert.AreEqual(7, options.MaximumConcurrentUploads);
        Assert.AreEqual(123 * 1_024, options.MaximumUploadSpeed);
        Assert.AreEqual(
            SoulseekClientManager.PeerConnectionInactivityTimeoutMilliseconds,
            options.TransferConnectionOptions.InactivityTimeout);
        Assert.IsNotNull(options.SearchResponseResolver);
        Assert.IsNotNull(options.BrowseResponseResolver);
        Assert.IsNotNull(options.DirectoryContentsResolver);
        Assert.IsNotNull(options.EnqueueDownload);
        Assert.IsNotNull(options.PlaceInQueueResolver);
        Assert.IsFalse(options.AutoAcknowledgePrivateMessages);
        Assert.IsTrue(options.AcceptPrivateRoomInvitations);
    }

    [TestMethod]
    public void ClientManager_ForwardsServerExcludedPhraseUpdates()
    {
        var client = new MockSoulseekClient([]);
        var router = new FakeInboundRouter();
        using var manager = new SoulseekClientManager(
            new EngineSettings(),
            client,
            router);

        client.RaiseExcludedSearchPhrases("forbidden", "blocked");

        CollectionAssert.AreEqual(
            new[] { "forbidden", "blocked" },
            router.ExcludedPhrases.ToArray());
    }

    [TestMethod]
    public void ClientManager_IsolatesStateObservers()
    {
        var client = new MockSoulseekClient([]);
        using var manager = new SoulseekClientManager(
            new EngineSettings(),
            client);
        SoulseekClientStates observed = SoulseekClientStates.None;
        manager.StateChanged += _ => throw new InvalidOperationException("observer");
        manager.StateChanged += state => observed = state;

        client.RaiseStateChanged(
            SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn);

        Assert.AreEqual(
            SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn,
            observed);
    }

    [TestMethod]
    public void Dispose_DisposesUnderlyingClient()
    {
        var mockClient = new MockSoulseekClient(new());
        var manager = new SoulseekClientManager(new EngineSettings(), mockClient);

        // Before the fix, Dispose did not exist/do anything. 
        // Now it should tear down the monitor loop and invoke Dispose on the client.
        manager.Dispose();

        Assert.IsTrue(mockClient.IsDisposed, "Underlying ISoulseekClient should be disposed.");
    }

    [TestMethod]
    public async Task WaitUntilReadyAsync_FaultsAfterPermanentLoginFailure()
    {
        var settings = new EngineSettings
        {
            Username = "user",
            Password = "pass",
        };
        var mockClient = new MockSoulseekClient(
            new(),
            initialState: SoulseekClientStates.None)
        {
            ConnectException = new InvalidOperationException("listener port unavailable"),
        };
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            await Assert.ThrowsExceptionAsync<SoulseekConnectionUnavailableException>(
                () => manager.EnsureConnectedAndLoggedInAsync(settings));

            var waitTask = manager.WaitUntilReadyAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

            Assert.AreSame(waitTask, completed, "Permanent login failures must wake readiness waiters instead of leaving the engine stuck forever.");
            await Assert.ThrowsExceptionAsync<SoulseekConnectionUnavailableException>(() => waitTask);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task FatalLoginWithoutReadinessWaiter_DoesNotPublishUnobservedTaskException()
    {
        const string marker = "fatal-login-unobserved-regression";
        var unobserved = new List<AggregateException>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception.ToString().Contains(marker, StringComparison.Ordinal))
            {
                lock (unobserved)
                    unobserved.Add(args.Exception);
                args.SetObserved();
            }
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await CreateAndDisposeFatalManager(marker);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Yield();
            }

            lock (unobserved)
                Assert.AreEqual(0, unobserved.Count, "A handled fatal login must not leave a second faulted readiness task unobserved.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task CreateAndDisposeFatalManager(string marker)
    {
        var settings = new EngineSettings
        {
            Username = "user",
            Password = "pass",
        };
        var mockClient = new MockSoulseekClient(
            new(),
            initialState: SoulseekClientStates.None)
        {
            ConnectException = new InvalidOperationException(marker),
        };
        using var manager = new SoulseekClientManager(settings, mockClient);

        await Assert.ThrowsExactlyAsync<SoulseekConnectionUnavailableException>(
            () => manager.EnsureConnectedAndLoggedInAsync(settings));
    }

    [DataTestMethod]
    [DataRow(null, null, "Missing Soulseek username and password.")]
    [DataRow("bbbbbbb", null, "Missing Soulseek password.")]
    public async Task EnsureConnectedAndLoggedInAsync_FailsCleanly_WhenCredentialsAreMissing(
        string? username,
        string? password,
        string expectedMessage)
    {
        var settings = new EngineSettings
        {
            Username = username,
            Password = password,
        };
        var mockClient = new MockSoulseekClient(
            new(),
            initialState: SoulseekClientStates.None);
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            var ex = await Assert.ThrowsExceptionAsync<SoulseekConnectionUnavailableException>(
                () => manager.EnsureConnectedAndLoggedInAsync(settings));

            StringAssert.Contains(ex.Message, expectedMessage);
            Assert.IsFalse(ex.Message.Contains("--random-login", StringComparison.Ordinal), "Missing credential errors should not advertise --random-login.");
            Assert.AreEqual(0, mockClient.ConnectCallCount, "Missing credentials should fail before ConnectAsync.");
        }
        finally
        {
            manager.Dispose();
        }
    }

    [TestMethod]
    public async Task LoginAccountIsVisibleBeforeProtocolConnectCallbacks()
    {
        var settings = new EngineSettings
        {
            Username = "chat-account",
            Password = "pass",
        };
        var mockClient = new MockSoulseekClient(
            [],
            initialState: SoulseekClientStates.None);
        using var manager = new SoulseekClientManager(settings, mockClient);
        string? accountObservedDuringConnect = null;
        mockClient.Connecting = () =>
            accountObservedDuringConnect = manager.LoggedInUsername;

        await manager.EnsureConnectedAndLoggedInAsync(settings);

        Assert.AreEqual("chat-account", accountObservedDuringConnect);
        Assert.AreEqual("chat-account", manager.LoggedInUsername);
    }

    [TestMethod]
    public async Task KickedFromServer_MarksFatal_WhenAutoReconnectDisabled()
    {
        var settings = new EngineSettings();
        var mockClient = new MockSoulseekClient(new());
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            mockClient.RaiseKickedFromServer(disconnect: false);

            Assert.IsTrue(manager.HasFatalError);
            await Assert.ThrowsExceptionAsync<SoulseekConnectionUnavailableException>(
                () => manager.WaitUntilReadyAsync());
        }
        finally
        {
            manager.Dispose();
        }
    }

    [TestMethod]
    public void KickedFromServer_DoesNotMarkFatal_WhenAutoReconnectEnabled()
    {
        var settings = new EngineSettings
        {
            AutoReconnectAfterKickedFromServer = true,
        };
        var mockClient = new MockSoulseekClient(new());
        var manager = new SoulseekClientManager(settings, mockClient);

        try
        {
            mockClient.RaiseKickedFromServer();

            Assert.IsFalse(manager.HasFatalError);
        }
        finally
        {
            manager.Dispose();
        }
    }

    private sealed class FakeInboundRouter : ISoulseekInboundRequestRouter
    {
        public IReadOnlyCollection<string> ExcludedPhrases { get; private set; } = [];

        public bool TryUpdateExcludedSearchPhrases(IReadOnlyCollection<string> phrases)
        {
            ExcludedPhrases = phrases.ToArray();
            return true;
        }

        public Task<SearchResponse?> ResolveSearchAsync(
            string username,
            int token,
            SearchQuery query) => Task.FromResult<SearchResponse?>(null);

        public Task<BrowseResponse> ResolveBrowseAsync(
            string username,
            IPEndPoint endpoint) => Task.FromResult(new BrowseResponse());

        public Task<IEnumerable<Soulseek.Directory>> ResolveDirectoryAsync(
            string username,
            IPEndPoint endpoint,
            int token,
            string remotePath)
            => Task.FromResult<IEnumerable<Soulseek.Directory>>([]);

        public Task<UserInfo> ResolveUserInfoAsync(
            string username,
            IPEndPoint endpoint)
            => Task.FromResult(new UserInfo("", 0, 0, false));

        public Task EnqueueUploadAsync(
            string username,
            IPEndPoint endpoint,
            string remotePath) => Task.CompletedTask;

        public Task<int?> ResolvePlaceInQueueAsync(
            string username,
            IPEndPoint endpoint,
            string remotePath) => Task.FromResult<int?>(null);
    }
}
