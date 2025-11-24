using Soulseek;
using System.Net.Sockets;

public class SoulseekClientManager
{
    private readonly Config _initialConfig;
    private ISoulseekClient? _client;
    private bool _isInitialized = false; // Tracks if initialization attempt was made
    private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);

    public ISoulseekClient? Client => _client;

    public bool IsConnectedAndLoggedIn =>
        _client != null &&
        _client.State.HasFlag(SoulseekClientStates.Connected) &&
        _client.State.HasFlag(SoulseekClientStates.LoggedIn);

    public SoulseekClientManager(Config initialConfig, ISoulseekClient? client = null)
    {
        _initialConfig = initialConfig ?? throw new ArgumentNullException(nameof(initialConfig));
        if (client != null)
        {
            _client = client;
            _isInitialized = true;
        }
    }

    /// <summary>
    /// Ensures the Soulseek client is created, connected, and logged in.
    /// Uses the provided config for login credentials if login is needed.
    /// </summary>
    /// <param name="loginConfig">Configuration containing potentially updated credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if login fails after retries.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public async Task EnsureConnectedAndLoggedInAsync(Config loginConfig, CancellationToken cancellationToken = default)
    {
        if (IsConnectedAndLoggedIn) return;

        // Use semaphore to prevent concurrent initialization attempts
        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring semaphore
            if (IsConnectedAndLoggedIn) return;
            cancellationToken.ThrowIfCancellationRequested();

            // --- Create Client if it doesn't exist ---
            if (_client == null)
            {
                _client = CreateClientInstance(_initialConfig);
                _isInitialized = false; // Reset initialized state as we have a new client instance
            }

            // --- Connect and Login ---
            if (!_isInitialized || !IsConnectedAndLoggedIn)
            {
                // Validate necessary config for login before attempting
                if (!loginConfig.useRandomLogin && (string.IsNullOrEmpty(loginConfig.username) || string.IsNullOrEmpty(loginConfig.password)))
                {
                    Config.InputError("No soulseek username or password provided for login."); // Or throw specific exception
                }

                await LoginInternalAsync(_client, loginConfig, cancellationToken);
                _isInitialized = true; // Mark that initialization (including login) has been attempted/completed
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the error, reset initialization state if login failed catastrophically?
            Logger.Error($"Failed to ensure Soulseek connection and login: {ex.Message}");
            _isInitialized = false; // Allow retry on next call
            // Propagate the exception so the caller knows it failed
            throw new InvalidOperationException($"Soulseek login failed: {ex.Message}", ex);
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }


    private ISoulseekClient CreateClientInstance(Config config)
    {
        Logger.Debug("Creating Soulseek client instance...");
        if (!string.IsNullOrEmpty(config.mockFilesDir))
        {
            Logger.Info("Using Mock Soulseek Client.");
            return Tests.ClientTests.MockSoulseekClient.FromLocalPaths(config.mockFilesReadTags, config.mockFilesDir);
        }
        else
        {
            Logger.Debug("Configuring real Soulseek Client connection options.");
            var serverConnectionOptions = new ConnectionOptions(
            connectTimeout: config.connectTimeout,
            configureSocket: (socket) =>
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
            });

            var transferConnectionOptions = new ConnectionOptions(
                inactivityTimeout: int.MaxValue, // this is handled by --max-stale-time
                configureSocket: (socket) =>
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
                });

            Task<UserInfo> userInfoResolver(string username, System.Net.IPEndPoint ip) => Task.FromResult(new UserInfo(
                description: config.userDescription ?? "",
                uploadSlots: 1,
                queueLength: 0,
                hasFreeUploadSlot: true
            ));

            var clientOptions = new SoulseekClientOptions(
                transferConnectionOptions: transferConnectionOptions,
                serverConnectionOptions: serverConnectionOptions,
                listenPort: config.listenPort,
                maximumConcurrentSearches: int.MaxValue, // this is limited later in the searcher code
                userInfoResolver: userInfoResolver
            );

            return new SoulseekClient(clientOptions);
        }
    }

    /// <summary>
    /// Internal login logic extracted from DownloaderApplication.
    /// </summary>
    private async Task LoginInternalAsync(ISoulseekClient client, Config config, CancellationToken cancellationToken, int tries = 3)
    {
        string user = config.username;
        string pass = config.password;

        if (config.useRandomLogin)
        {
            var r = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            pass = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            Logger.Debug($"Generated random username: {user}");
        }

        string displayUser = config.useRandomLogin ? "[Random]" : user;
        Logger.Info($"Login {displayUser}");

        int attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                Logger.Debug($"Connecting {displayUser} (Attempt {attempt}/{tries})");
                // Pass cancellation token to ConnectAsync if the library supports it (check Soulseek library version)
                // Assuming it doesn't directly, we rely on the loop's cancellation check.
                await client.ConnectAsync(user, pass);

                if (!config.noModifyShareCount)
                {
                    Logger.Debug($"Setting share count for {displayUser}");
                    // Pass cancellation token if supported
                    await client.SetSharedCountsAsync(50, 1000);
                }
                Logger.Debug($"Logged in {displayUser}");
                break;
            }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check again after potential long operation
                Logger.DebugError($"Exception during login attempt {attempt}/{tries} for {displayUser}: {e}");
                if (!(e is Soulseek.AddressException || e is System.TimeoutException || e is System.Net.Sockets.SocketException) || attempt >= tries)
                {
                    Logger.Error($"Login failed definitively for {displayUser} after {attempt} attempts.");
                    throw; // Retries exhausted or non-transient error
                }

                Logger.Warn($"Login attempt {attempt}/{tries} failed for {displayUser}, retrying...");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }
}