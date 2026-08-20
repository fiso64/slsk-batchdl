using System.Threading.Channels;
using Sockseek.Api;
using Sockseek.Core.Models;
using Sockseek.Core.Sharing;
using Sockseek.Core.UserProfiles;
using Soulseek;
using ApiPresence = Sockseek.Api.UserProfilePresence;
using SoulseekPresence = Soulseek.UserPresence;

namespace Sockseek.Server.UserProfiles;

public sealed class UserProfileAccessDeniedException : Exception;

public sealed class UserProfileUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public interface IUserProfileTransport
{
    Task<UserStatus> GetStatusAsync(string username, CancellationToken cancellationToken);
    Task<UserInfo> GetInfoAsync(string username, CancellationToken cancellationToken);
    Task<UserStatistics> GetStatisticsAsync(string username, CancellationToken cancellationToken);
}

public sealed class SoulseekUserProfileTransport(Func<ISoulseekClient?> clientProvider)
    : IUserProfileTransport
{
    public Task<UserStatus> GetStatusAsync(string username, CancellationToken cancellationToken)
        => Client().GetUserStatusAsync(username, cancellationToken);

    public Task<UserInfo> GetInfoAsync(string username, CancellationToken cancellationToken)
        => Client().GetUserInfoAsync(username, cancellationToken);

    public Task<UserStatistics> GetStatisticsAsync(string username, CancellationToken cancellationToken)
        => Client().GetUserStatisticsAsync(username, cancellationToken);

    private ISoulseekClient Client()
        => clientProvider() ?? throw new UserProfileUnavailableException("Soulseek is unavailable.");
}

/// <summary>
/// Daemon-lifetime composite profile coordinator. Identical requests share one
/// set of protocol calls; retained pictures are bounded by count and byte-budget
/// eviction, never by rejecting otherwise valid peer data.
/// </summary>
public sealed class UserProfileService : IAsyncDisposable
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SectionTimeout = TimeSpan.FromSeconds(10);
    public const int DefaultNetworkConcurrency = 4;
    public const int MaximumCachedProfiles = 128;
    public const long PictureCacheByteTarget = 32L * 1024 * 1024;

    private readonly IUserProfileTransport transport;
    private readonly Func<CancellationToken, Task> ensureSessionStarted;
    private readonly Func<string?> localAccountProvider;
    private readonly PeerAccessPolicy accessPolicy;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeSpan sectionTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ProfileKey, Task<CachedProfile>> active = [];
    private readonly Dictionary<ProfileKey, CachedProfile> cache = [];
    private readonly Channel<int> networkSlots;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sessionSync = new();
    private readonly object connectionSync = new();
    private CancellationTokenSource connectionLifetime = new();
    private Task? sessionStartup;
    private int observedLoggedInSession;
    private int disposeState;

    public UserProfileService(
        IUserProfileTransport transport,
        Func<CancellationToken, Task> ensureSessionStarted,
        Func<string?> localAccountProvider,
        PeerAccessPolicy accessPolicy,
        int networkConcurrency = DefaultNetworkConcurrency,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? sectionTimeout = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.ensureSessionStarted = ensureSessionStarted ?? throw new ArgumentNullException(nameof(ensureSessionStarted));
        this.localAccountProvider = localAccountProvider ?? throw new ArgumentNullException(nameof(localAccountProvider));
        this.accessPolicy = accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.sectionTimeout = sectionTimeout ?? SectionTimeout;
        if (networkConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(networkConcurrency));
        if (this.sectionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sectionTimeout));

        networkSlots = Channel.CreateBounded<int>(new BoundedChannelOptions(networkConcurrency)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        for (int index = 0; index < networkConcurrency; index++)
            networkSlots.Writer.TryWrite(index);
    }

    public async Task<UserProfileDto> GetAsync(
        string username,
        bool refresh = false,
        CancellationToken cancellationToken = default)
        => (await GetEntryAsync(username, refresh, cancellationToken).ConfigureAwait(false)).Profile;

    public async Task<UserPicture?> GetPictureAsync(
        string username,
        CancellationToken cancellationToken = default)
        => (await GetEntryAsync(username, refresh: false, cancellationToken).ConfigureAwait(false)).Picture;

    public void OnSoulseekStateChanged(SoulseekClientStates state)
    {
        if (Volatile.Read(ref disposeState) != 0)
            return;
        if (state.HasFlag(SoulseekClientStates.Connected)
            && state.HasFlag(SoulseekClientStates.LoggedIn))
        {
            Volatile.Write(ref observedLoggedInSession, 1);
            return;
        }
        if (Interlocked.Exchange(ref observedLoggedInSession, 0) == 0)
            return;

        CancellationTokenSource previous;
        lock (connectionSync)
        {
            previous = connectionLifetime;
            connectionLifetime = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
        lock (sessionSync)
            sessionStartup = null;
    }

    private async Task<CachedProfile> GetEntryAsync(
        string username,
        bool refresh,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        username = PeerUsername.Validate(username);
        if (accessPolicy.IsUsernameBlocked(username))
            throw new UserProfileAccessDeniedException();

        await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        string localAccount = PeerUsername.Validate(
            localAccountProvider()
            ?? throw new UserProfileUnavailableException("Soulseek is not logged in."));
        var key = new ProfileKey(localAccount, username);

        Task<CachedProfile> operation;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active.TryGetValue(key, out Task<CachedProfile>? running))
            {
                operation = running;
            }
            else if (!refresh
                && cache.TryGetValue(key, out CachedProfile? existing)
                && utcNow() - existing.Profile.ObservedAt < Freshness)
            {
                existing.LastAccess = utcNow();
                return existing;
            }
            else
            {
                CancellationToken connectionToken;
                lock (connectionSync)
                    connectionToken = connectionLifetime.Token;
                operation = RunAndStoreAsync(key, connectionToken);
                active.Add(key, operation);
            }
        }
        finally
        {
            gate.Release();
        }

        // The daemon owns the operation; cancelling one HTTP/CLI waiter detaches.
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        Task startup;
        lock (sessionSync)
            startup = sessionStartup ??= ensureSessionStarted(lifetime.Token);

        try
        {
            await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (sessionSync)
            {
                if (ReferenceEquals(sessionStartup, startup))
                    sessionStartup = null;
            }
            throw new UserProfileUnavailableException("Soulseek is unavailable.", ex);
        }
    }

    private async Task<CachedProfile> RunAndStoreAsync(
        ProfileKey key,
        CancellationToken connectionToken)
    {
        int slot = -1;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            connectionToken);
        try
        {
            slot = await networkSlots.Reader.ReadAsync(operation.Token).ConfigureAwait(false);
            Task<SectionResult<UserStatus>> statusTask = CaptureAsync(
                token => transport.GetStatusAsync(key.Username, token), operation.Token, sectionTimeout);
            Task<SectionResult<UserInfo>> infoTask = CaptureAsync(
                token => transport.GetInfoAsync(key.Username, token), operation.Token, sectionTimeout);
            Task<SectionResult<UserStatistics>> statisticsTask = CaptureAsync(
                token => transport.GetStatisticsAsync(key.Username, token), operation.Token, sectionTimeout);
            await Task.WhenAll(statusTask, infoTask, statisticsTask).ConfigureAwait(false);

            SectionResult<UserStatus> status = await statusTask.ConfigureAwait(false);
            SectionResult<UserInfo> info = await infoTask.ConfigureAwait(false);
            SectionResult<UserStatistics> statistics = await statisticsTask.ConfigureAwait(false);

            (UserProfileSectionDto PictureSection, UserPicture? Picture) picture =
                await ResolvePictureAsync(info, operation.Token).ConfigureAwait(false);
            DateTimeOffset observedAt = utcNow();
            var profile = new UserProfileDto(
                key.Username,
                Presence(status),
                status.Section,
                info.Section,
                statistics.Section,
                picture.PictureSection,
                info.Value is { } infoValue
                    ? UserProfileText.NormalizeDescription(infoValue.Description)
                    : null,
                NonNegative(statistics.Value?.FileCount),
                NonNegative(statistics.Value?.DirectoryCount),
                NonNegative(statistics.Value?.AverageSpeed),
                UploadCount(statistics.Value?.UploadCount),
                NonNegativeInt(info.Value?.UploadSlots),
                NonNegativeInt(info.Value?.QueueLength),
                info.Value?.HasFreeUploadSlot,
                picture.Picture is { } image
                    ? new UserPictureRefDto(
                        $"/api/users/{Uri.EscapeDataString(key.Username)}/picture",
                        image.MediaType,
                        image.Bytes.Length,
                        image.ETag)
                    : null,
                observedAt);
            var cached = new CachedProfile(profile, picture.Picture, observedAt);

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                cache[key] = cached;
                EvictCache(key);
            }
            finally
            {
                gate.Release();
            }
            return cached;
        }
        catch (OperationCanceledException ex) when (!lifetime.IsCancellationRequested)
        {
            throw new UserProfileUnavailableException("Soulseek connection was lost.", ex);
        }
        finally
        {
            if (slot >= 0)
                networkSlots.Writer.TryWrite(slot);
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                active.Remove(key);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static async Task<SectionResult<T>> CaptureAsync<T>(
        Func<CancellationToken, Task<T>> request,
        CancellationToken operationToken,
        TimeSpan timeoutDuration)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        timeout.CancelAfter(timeoutDuration);
        try
        {
            T value = await request(timeout.Token).ConfigureAwait(false);
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.Available, null),
                value);
        }
        catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
        {
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.TimedOut, "timeout"),
                null);
        }
        catch (UserOfflineException)
        {
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.Unavailable, "offline"),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SectionResult<T>(
                new UserProfileSectionDto(ResourceSectionState.Unavailable, "request-failed"),
                null);
        }
    }

    private static async Task<(UserProfileSectionDto PictureSection, UserPicture? Picture)>
        ResolvePictureAsync(
            SectionResult<UserInfo> info,
            CancellationToken cancellationToken)
    {
        if (info.Value is null)
            return (info.Section, null);
        if (!info.Value.HasPicture || info.Value.Picture is null)
            return (new UserProfileSectionDto(ResourceSectionState.Available, null), null);

        try
        {
            UserPicture picture = await UserPictureCodec.ValidateRemoteAsync(
                info.Value.Picture,
                cancellationToken).ConfigureAwait(false);
            return (new UserProfileSectionDto(ResourceSectionState.Available, null), picture);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new UserProfileSectionDto(ResourceSectionState.TimedOut, "decode-timeout"), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (new UserProfileSectionDto(ResourceSectionState.Unavailable, "invalid-image"), null);
        }
    }

    private void EvictCache(ProfileKey newest)
    {
        long pictureBytes = cache.Values.Sum(value => (long)(value.Picture?.Bytes.Length ?? 0));
        while (cache.Count > MaximumCachedProfiles || pictureBytes > PictureCacheByteTarget)
        {
            KeyValuePair<ProfileKey, CachedProfile>[] victims = cache
                .Where(pair => !pair.Key.Equals(newest))
                .OrderBy(pair => pair.Value.LastAccess)
                .Take(1)
                .ToArray();
            if (victims.Length == 0)
                break;
            pictureBytes -= victims[0].Value.Picture?.Bytes.Length ?? 0;
            cache.Remove(victims[0].Key);
        }
    }

    private static ApiPresence Presence(SectionResult<UserStatus> status)
        => status.Value?.Presence switch
        {
            SoulseekPresence.Online => ApiPresence.Online,
            SoulseekPresence.Away => ApiPresence.Away,
            SoulseekPresence.Offline => ApiPresence.Offline,
            _ => ApiPresence.Unknown,
        };

    private static long? NonNegative(int? value) => value >= 0 ? value.Value : null;
    private static int? NonNegativeInt(int? value) => value >= 0 ? value : null;
    private static int? UploadCount(long? value)
        => value is >= 0 and <= int.MaxValue ? (int)value.Value : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;
        lifetime.Cancel();
        CancellationTokenSource connection;
        lock (connectionSync)
        {
            connection = connectionLifetime;
            connectionLifetime = new CancellationTokenSource();
        }
        connection.Cancel();
        Task[] pending;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            pending = active.Values.ToArray();
            cache.Clear();
        }
        finally
        {
            gate.Release();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* disposal observes and suppresses daemon-lifetime cancellation */ }
        connection.Dispose();
        connectionLifetime.Dispose();
        lifetime.Dispose();
        gate.Dispose();
    }

    private sealed record SectionResult<T>(UserProfileSectionDto Section, T? Value)
        where T : class;

    private sealed class CachedProfile(
        UserProfileDto profile,
        UserPicture? picture,
        DateTimeOffset lastAccess)
    {
        public UserProfileDto Profile { get; } = profile;
        public UserPicture? Picture { get; } = picture;
        public DateTimeOffset LastAccess { get; set; } = lastAccess;
    }

    private readonly record struct ProfileKey(string LocalAccount, string Username);
}
