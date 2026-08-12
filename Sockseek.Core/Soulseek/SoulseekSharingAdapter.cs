using System.Net;
using System.Text;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Soulseek;
using SlDirectory = Soulseek.Directory;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public interface ISoulseekInboundRequestRouter
{
    bool TryUpdateExcludedSearchPhrases(IReadOnlyCollection<string> phrases);

    Task<SearchResponse?> ResolveSearchAsync(
        string username,
        int token,
        SearchQuery query);

    Task<BrowseResponse> ResolveBrowseAsync(
        string username,
        IPEndPoint endpoint);

    Task<IEnumerable<SlDirectory>> ResolveDirectoryAsync(
        string username,
        IPEndPoint endpoint,
        int token,
        string remotePath);

    Task<UserInfo> ResolveUserInfoAsync(
        string username,
        IPEndPoint endpoint);

    Task EnqueueUploadAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath);

    Task<int?> ResolvePlaceInQueueAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath);
}

/// <summary>
/// Safe callback boundary for all inbound sharing protocol operations.
/// </summary>
public sealed class SoulseekSharingAdapter : ISoulseekInboundRequestRouter, IDisposable
{
    public const int MaximumSearchTerms = 64;
    public const int MaximumSearchExclusions = 64;
    public const int MaximumSearchUtf8Bytes = 4_096;
    public const int MaximumDirectoryFiles = 10_000;
    public const int MaximumDirectoryEncodedBytes = 8 * 1_024 * 1_024;
    public const int MaximumExcludedPhraseCount = 256;
    public const int MaximumExcludedPhraseUtf8Bytes = 1_024;
    public const int MaximumExcludedPhraseSetUtf8Bytes = 64 * 1_024;
    public const int MaximumSearchCandidates = 2_000;
    public const int SearchResponseFileLimit = 500;
    public const int IncomingSearchConcurrency = 10;
    public const int IncomingSearchQueueCapacity = 500;
    public const int InboundUploadConcurrency = 10;
    public const int InboundUploadQueueCapacity = 500;
    public const int InboundDirectoryConcurrency = 10;
    public const int InboundDirectoryQueueCapacity = 500;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BrowseIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly IShareCatalogProvider catalogs;
    private readonly UploadCoordinator uploads;
    private readonly PeerAccessPolicy accessPolicy;
    private readonly UploadSettings uploadSettings;
    private readonly Func<ISoulseekClient?> clientProvider;
    private readonly string userDescription;
    private readonly bool uploadServingEnabled;
    private readonly SemaphoreSlim searchConcurrency =
        new(IncomingSearchConcurrency, IncomingSearchConcurrency);
    private readonly SemaphoreSlim browseConcurrency = new(10, 10);
    private readonly BoundedCallbackGate directoryGate =
        new(InboundDirectoryConcurrency, InboundDirectoryQueueCapacity);
    private readonly BoundedCallbackGate uploadGate =
        new(InboundUploadConcurrency, InboundUploadQueueCapacity);
    private string[] excludedPhrases = [];
    private int pendingSearches;
    private bool disposed;

    public SoulseekSharingAdapter(
        IShareCatalogProvider catalogs,
        UploadCoordinator uploads,
        PeerAccessPolicy accessPolicy,
        UploadSettings uploadSettings,
        Func<ISoulseekClient?> clientProvider,
        string? userDescription = null,
        bool uploadServingEnabled = true)
    {
        this.catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        this.uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        this.accessPolicy = accessPolicy ?? throw new ArgumentNullException(nameof(accessPolicy));
        this.uploadSettings = uploadSettings ?? throw new ArgumentNullException(nameof(uploadSettings));
        this.clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        this.userDescription = userDescription ?? "";
        this.uploadServingEnabled = uploadServingEnabled;
    }

    public bool TryUpdateExcludedSearchPhrases(IReadOnlyCollection<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        if (phrases.Count > MaximumExcludedPhraseCount)
            return false;

        int bytes = 0;
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string phraseValue in phrases)
        {
            if (phraseValue is null)
                return false;
            string phrase;
            try
            {
                phrase = phraseValue.Trim().Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return false;
            }
            int phraseBytes = Encoding.UTF8.GetByteCount(phrase);
            if (phrase.Length == 0 || phraseBytes > MaximumExcludedPhraseUtf8Bytes)
                return false;
            bytes = checked(bytes + phraseBytes);
            if (bytes > MaximumExcludedPhraseSetUtf8Bytes)
                return false;
            normalized.Add(phrase);
        }

        Volatile.Write(
            ref excludedPhrases,
            normalized.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        return true;
    }

    public async Task<SearchResponse?> ResolveSearchAsync(
        string username,
        int token,
        SearchQuery query)
    {
        if (disposed
            || !uploadServingEnabled
            || !IsValidUsername(username)
            || !IsValidSearch(query)
            || accessPolicy.IsUsernameBlocked(username))
        {
            return null;
        }
        if (Interlocked.Increment(ref pendingSearches)
            > IncomingSearchQueueCapacity)
        {
            Interlocked.Decrement(ref pendingSearches);
            SharingTelemetry.RecordDroppedRequest("search");
            return null;
        }

        bool entered = false;
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await searchConcurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;

            if (!catalogs.TryAcquire(out IShareCatalogLease? lease) || lease is null)
                return null;
            await using (lease.ConfigureAwait(false))
            {
                const int responseLimit = SearchResponseFileLimit;
                int candidateLimit = Math.Min(
                    MaximumSearchCandidates,
                    checked(responseLimit * 4));
                IReadOnlyList<ShareCatalogFile> rows = await lease.Reader.SearchAsync(
                    query.Query,
                    query.Exclusions,
                    candidateLimit,
                    timeout.Token).ConfigureAwait(false);
                string[] exclusions = query.Exclusions
                    .Concat(Volatile.Read(ref excludedPhrases))
                    .ToArray();
                var files = rows
                    .Where(file => !ContainsExcluded(file.RemotePath, exclusions))
                    .Take(responseLimit)
                    .Select(ToSearchFile)
                    .ToArray();
                if (files.Length == 0)
                    return null;

                if (accessPolicy.HasBlockedIpAddresses)
                {
                    ISoulseekClient? client = clientProvider();
                    if (client is null)
                        return null;
                    IPEndPoint endpoint = await client
                        .GetUserEndPointAsync(username, cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                    if (accessPolicy.IsIpAddressBlocked(endpoint.Address))
                        return null;
                }

                UploadQueueRuntimeSnapshot capacity = uploads.GetQueueSnapshot();
                return new SearchResponse(
                    clientProvider()?.Username ?? "",
                    token,
                    uploadServingEnabled && uploads.CouldStartImmediately(username),
                    EffectiveUploadSpeedBytesPerSecond,
                    capacity.QueuedFiles,
                    files);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (entered)
                searchConcurrency.Release();
            Interlocked.Decrement(ref pendingSearches);
        }
    }

    public Task<BrowseResponse> ResolveBrowseAsync(
        string username,
        IPEndPoint endpoint)
    {
        if (disposed
            || !IsValidUsername(username)
            || accessPolicy.IsBlocked(username, endpoint))
        {
            return Task.FromResult<BrowseResponse>(new BrowseResponse());
        }
        if (!browseConcurrency.Wait(0))
        {
            SharingTelemetry.RecordDroppedRequest("browse");
            return Task.FromResult<BrowseResponse>(new BrowseResponse());
        }

        IShareCatalogLease? lease = null;
        try
        {
            if (!catalogs.TryAcquire(out lease) || lease is null)
            {
                browseConcurrency.Release();
                return Task.FromResult<BrowseResponse>(new BrowseResponse());
            }

            ShareBrowseStream browse = lease.OpenBrowseStream(
                BrowseIdleTimeout,
                SafeReleaseBrowsePermit);
            lease = null; // ownership moved into the stream wrapper
            return Task.FromResult<BrowseResponse>(
                new RawBrowseResponse(browse.Length, browse.Stream));
        }
        catch
        {
            lease?.Dispose();
            browseConcurrency.Release();
            return Task.FromResult<BrowseResponse>(new BrowseResponse());
        }
    }

    public async Task<IEnumerable<SlDirectory>> ResolveDirectoryAsync(
        string username,
        IPEndPoint endpoint,
        int token,
        string remotePath)
    {
        if (disposed
            || !IsValidUsername(username)
            || accessPolicy.IsBlocked(username, endpoint)
            || !IsValidRemotePath(remotePath))
        {
            return [];
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await using BoundedCallbackGate.Lease? gate =
                await directoryGate.TryEnterAsync(timeout.Token).ConfigureAwait(false);
            if (gate is null)
                return [];

            var key = RemotePathKey.Create(remotePath);
            if (!catalogs.TryAcquire(out IShareCatalogLease? lease) || lease is null)
                return [];
            await using (lease.ConfigureAwait(false))
            {
                ShareCatalogBrowseDirectory? directory =
                    await lease.Reader.GetDirectoryAsync(
                        key,
                        MaximumDirectoryFiles,
                        timeout.Token).ConfigureAwait(false);
                if (directory is null
                    || EstimateDirectoryBytes(directory) > MaximumDirectoryEncodedBytes)
                {
                    return [];
                }
                return
                [
                    new SlDirectory(
                        directory.Directory.RemotePath,
                        directory.Files.Select(ToBrowseFile)),
                ];
            }
        }
        catch
        {
            return [];
        }
    }

    public Task<UserInfo> ResolveUserInfoAsync(
        string username,
        IPEndPoint endpoint)
    {
        if (disposed
            || !IsValidUsername(username)
            || accessPolicy.IsBlocked(username, endpoint))
        {
            return Task.FromResult(new UserInfo("", 0, 0, false));
        }
        if (!catalogs.TryAcquire(out IShareCatalogLease? lease) || lease is null)
            return Task.FromResult(new UserInfo(userDescription, 0, 0, false));
        lease.Dispose();

        UploadQueueRuntimeSnapshot capacity = uploads.GetQueueSnapshot();
        return Task.FromResult(new UserInfo(
            userDescription,
            uploadServingEnabled ? capacity.TotalSlots : 0,
            capacity.QueuedFiles,
            uploadServingEnabled && uploads.CouldStartImmediately(username)));
    }

    public async Task EnqueueUploadAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath)
    {
        if (disposed
            || !uploadServingEnabled
            || !IsValidUsername(username)
            || !IsValidRemotePath(remotePath)
            || accessPolicy.IsBlocked(username, endpoint))
        {
            throw new DownloadEnqueueException("File not shared");
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await using BoundedCallbackGate.Lease? global =
                await uploadGate.TryEnterAsync(timeout.Token).ConfigureAwait(false);
            if (global is null)
            {
                SharingTelemetry.RecordDroppedRequest("upload");
                throw new DownloadEnqueueException("File not shared");
            }

            string exactUsername = PeerUsername.Validate(username);

            UploadCoordinatorAdmission result = await uploads.AdmitAsync(
                exactUsername,
                endpoint,
                remotePath,
                timeout.Token).ConfigureAwait(false);
            if (result.Kind is UploadAdmissionResultKind.Accepted
                or UploadAdmissionResultKind.Duplicate)
            {
                return;
            }

            throw new DownloadEnqueueException(result.Rejection switch
            {
                _ => "File not shared",
            });
        }
        catch (DownloadEnqueueException)
        {
            throw;
        }
        catch
        {
            throw new DownloadEnqueueException("File not shared");
        }
    }

    public Task<int?> ResolvePlaceInQueueAsync(
        string username,
        IPEndPoint endpoint,
        string remotePath)
    {
        if (disposed
            || !uploadServingEnabled
            || !IsValidUsername(username)
            || accessPolicy.IsBlocked(username, endpoint))
            return Task.FromResult<int?>(null);

        try
        {
            UploadQueueEstimate estimate = uploads.GetQueueEstimate(username, remotePath);
            return Task.FromResult(estimate.AheadCount);
        }
        catch
        {
            return Task.FromResult<int?>(null);
        }
    }

    private int EffectiveUploadSpeedBytesPerSecond
        => uploadSettings.SpeedLimitKiBPerSecond is { } kib
            ? checked(kib * 1_024)
            : int.MaxValue;

    private void SafeReleaseBrowsePermit()
    {
        try
        {
            browseConcurrency.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsValidSearch(SearchQuery? query)
        => query is not null
           && query.Terms.Count is > 0 and <= MaximumSearchTerms
           && query.Exclusions.Count <= MaximumSearchExclusions
           && Encoding.UTF8.GetByteCount(query.SearchText) <= MaximumSearchUtf8Bytes;

    private static bool IsValidUsername(string username)
    {
        try
        {
            return Encoding.UTF8.GetByteCount(username)
                       <= UploadCoordinator.MaximumUsernameUtf8Bytes
                   && PeerUsername.Validate(username).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidRemotePath(string remotePath)
        => !string.IsNullOrWhiteSpace(remotePath)
           && Encoding.UTF8.GetByteCount(remotePath)
           <= UploadCoordinator.MaximumRemotePathUtf8Bytes;

    private static bool ContainsExcluded(string remotePath, IEnumerable<string> exclusions)
        => exclusions.Any(exclusion =>
            remotePath.Contains(exclusion, StringComparison.OrdinalIgnoreCase));

    private static SlFile ToSearchFile(ShareCatalogFile file)
        => new(
            file.ProtocolCode,
            file.RemotePath,
            file.SizeBytes,
            file.Extension,
            file.Attributes.Select(
                attribute => new FileAttribute(
                    (FileAttributeType)attribute.Type,
                    attribute.Value)));

    private static SlFile ToBrowseFile(ShareCatalogFile file)
        => new(
            file.ProtocolCode,
            RemoteFileName(file.RemotePath),
            file.SizeBytes,
            file.Extension,
            file.Attributes.Select(
                attribute => new FileAttribute(
                    (FileAttributeType)attribute.Type,
                    attribute.Value)));

    private static string RemoteFileName(string remotePath)
    {
        int separator = remotePath.LastIndexOf('\\');
        return separator < 0 ? remotePath : remotePath[(separator + 1)..];
    }

    private static long EstimateDirectoryBytes(ShareCatalogBrowseDirectory directory)
    {
        long total = Encoding.UTF8.GetByteCount(directory.Directory.RemotePath) + 32;
        foreach (var file in directory.Files)
        {
            total = checked(total
                            + Encoding.UTF8.GetByteCount(RemoteFileName(file.RemotePath))
                            + Encoding.UTF8.GetByteCount(file.Extension)
                            + 32L
                            + file.Attributes.Count * 8L);
        }
        return total;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        // In-flight callback and browse-stream owners can release after the
        // session begins shutting down. These bounded semaphores are therefore
        // left for GC instead of being disposed underneath those owners.
    }
}
