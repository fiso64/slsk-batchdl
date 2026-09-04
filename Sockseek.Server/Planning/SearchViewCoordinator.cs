using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Planning;

public sealed class SearchViewUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed class SearchViewCoordinator(
    EngineSupervisor supervisor,
    PersistenceCoordinator persistence,
    SubmissionCommitCoordinator commits,
    SearchViewCursorCodec cursors,
    ILogger<SearchViewCoordinator> logger) : IHostedService, IAsyncDisposable
{
    private const int ProjectionBatchSize = 200;
    private readonly ConcurrentDictionary<Guid, RuntimeView> runtimes = [];
    private readonly ConcurrentDictionary<Guid, byte> pending = [];
    private readonly Channel<Guid> work = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource lifetime = new();
    private SearchViewStore? store;
    private Exception? initializationFailure;
    private Task? worker;

    internal event Action<Guid, long>? ViewPublished;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SearchViewStore? candidate = persistence.SearchViews;
        if (candidate is null)
        {
            initializationFailure = new InvalidOperationException(
                "The shared persistence runtime or Search View schema is unavailable.");
            ServerLogMessages.SearchViewUnavailable(logger);
            return;
        }
        try
        {
            cursors.Initialize();
            store = candidate;
            await candidate.PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
            worker = RunWorkerAsync(lifetime.Token);
            await RestoreIncompleteAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            initializationFailure = exception;
            ServerLogMessages.SearchViewInitializationFailed(logger, exception);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (RuntimeView runtime in runtimes.Values)
            runtime.Detach();
        work.Writer.TryComplete();
        lifetime.Cancel();
        if (worker != null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    public async Task<SearchViewSummaryDto?> CreateAsync(
        Guid sourceJobId,
        CreateSearchViewRequestDto request,
        CancellationToken cancellationToken)
    {
        SearchViewStore repository = RequiredStore();
        await repository.PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
        RuntimeSource? source;
        SearchDefinition definition;
        SearchViewProjectionDefinition projection;
        SearchSettings settings;
        IReadOnlyDictionary<string, int> workflowUserSuccessCounts;

        Job? live = supervisor.GetRuntimeJob<Job>(sourceJobId);
        if (live?.Config != null && live.SearchObservationSession is { } liveSession)
        {
            definition = AuthoritativeDefinition(live);
            projection = ResolveProjection(definition, request);
            settings = SettingsCloner.Clone(live.Config.Search);
            source = new LiveSource(liveSession);
            // TODO [V4]: This preserves the current in-memory engine/workflow
            // behavior only. Do not persist or broaden reputation ownership
            // until its daemon lifetime semantics are defined.
            workflowUserSuccessCounts = supervisor.GetUserSuccessCountSnapshot();
        }
        else
        {
            source = await CreateHistoricalSourceAsync(
                sourceJobId,
                request,
                cancellationToken).ConfigureAwait(false);
            if (source is not HistoricalSource historical)
                return null;
            definition = historical.Definition;
            projection = historical.Projection;
            settings = definition.ProjectionSettings.ToSettings();
            workflowUserSuccessCounts = new Dictionary<string, int>(
                StringComparer.Ordinal);
        }

        JsonSerializerOptions jsonOptions = SockseekApiJson.CreateSerializerOptions();
        var durableDefinitionNode = new JsonObject
        {
            ["searchDefinitionJson"] = SearchDefinitionCodec.Serialize(definition),
            ["projection"] = JsonSerializer.SerializeToNode(ToDto(projection), jsonOptions),
        };
        string durableDefinition = durableDefinitionNode.ToJsonString();
        StoredSearchView stored = await repository.CreateAsync(
            sourceJobId,
            projection.Kind,
            durableDefinition,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var runtime = new RuntimeView(
            stored.Id,
            source,
            new SearchViewKernel(
                projection,
                settings,
                workflowUserSuccessCounts,
                retainProjectedRows: false,
                trackPeerIdentities: false),
            () => Schedule(stored.Id));
        if (!runtimes.TryAdd(stored.Id, runtime))
            throw new InvalidOperationException("The search view already exists in runtime state.");
        runtime.Attach();
        Schedule(stored.Id);
        ServerLogMessages.SearchViewCreated(logger, stored.Id, sourceJobId);
        return ToDto(stored);
    }

    public async Task<SearchViewSummaryDto?> GetAsync(
        Guid viewId,
        CancellationToken cancellationToken)
    {
        StoredSearchView? view = await RequiredStore().GetAsync(viewId, cancellationToken)
            .ConfigureAwait(false);
        return view == null || view.ExpiresAtUtc <= DateTimeOffset.UtcNow
            ? null
            : ToDto(view);
    }

    public async Task<SearchViewFilePageDto?> GetFilesAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewFilePosition? after = cursor == null
            ? null
            : cursors.DecodeFiles(cursor, viewId, revision);
        StoredSearchViewFilePage? page = await RequiredStore().GetFilesAsync(
            viewId,
            revision,
            after,
            limit,
            cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeFiles(viewId, revision, page.NextPosition));
    }

    public async Task<SearchViewDirectoryPageDto?> GetDirectoriesAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewDirectoryPosition? after = cursor == null
            ? null
            : cursors.DecodeDirectories(cursor, viewId, revision);
        StoredSearchViewDirectoryPage? page = await RequiredStore().GetDirectoriesAsync(
            viewId,
            revision,
            after,
            limit,
            cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeDirectories(viewId, revision, page.NextPosition));
    }

    public async Task<SearchViewDirectoryFilePageDto?> GetDirectoryFilesAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewDirectoryFilePosition? after = cursor == null
            ? null
            : cursors.DecodeDirectoryFiles(
                cursor,
                viewId,
                directoryRef,
                revision);
        StoredSearchViewDirectoryFilePage? page = await RequiredStore()
            .GetDirectoryFilesAsync(
                viewId,
                directoryRef,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        var directory = new PeerDirectoryRefDto(
            page.DirectoryRef,
            page.Username,
            page.FolderPath);
        return new(
            ToDto(page.Revision),
            directory,
            page.Items.Select(item => new SearchViewDirectoryFileDto(
                item.Ref,
                item.RelativePath,
                ToDto(item.File))).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeDirectoryFiles(
                    viewId,
                    directoryRef,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewAggregateTrackPageDto?> GetAggregateTracksAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewAggregateTrackPosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateTracks(cursor, viewId, revision);
        StoredSearchViewAggregateTrackPage? page = await RequiredStore()
            .GetAggregateTracksAsync(
                viewId,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAggregateTracks(
                    viewId,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewAggregateTrackOptionPageDto?> GetAggregateTrackOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewFilePosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateTrackOptions(
                cursor,
                viewId,
                groupRef,
                revision);
        StoredSearchViewAggregateTrackOptionPage? page = await RequiredStore()
            .GetAggregateTrackOptionsAsync(
                viewId,
                groupRef,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.GroupRef,
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAggregateTrackOptions(
                    viewId,
                    groupRef,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewAggregateAlbumPageDto?> GetAggregateAlbumsAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewAggregateAlbumPosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateAlbums(cursor, viewId, revision);
        StoredSearchViewAggregateAlbumPage? page = await RequiredStore()
            .GetAggregateAlbumsAsync(
                viewId,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAggregateAlbums(
                    viewId,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewAggregateAlbumOptionPageDto?> GetAggregateAlbumOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewDirectoryPosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateAlbumOptions(
                cursor,
                viewId,
                groupRef,
                revision);
        StoredSearchViewAggregateAlbumOptionPage? page = await RequiredStore()
            .GetAggregateAlbumOptionsAsync(
                viewId,
                groupRef,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.GroupRef,
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAggregateAlbumOptions(
                    viewId,
                    groupRef,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewUpdateDto?> GetUpdatesAsync(
        Guid viewId,
        long afterRevision,
        CancellationToken cancellationToken)
    {
        if (afterRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(afterRevision));
        StoredSearchView? latest = await RequiredStore().GetAsync(
            viewId,
            cancellationToken).ConfigureAwait(false);
        if (latest == null)
            return null;
        if (afterRevision > latest.Revision)
            throw new ArgumentOutOfRangeException(
                nameof(afterRevision),
                "The requested revision is newer than the Search View.");
        return new(ToDto(latest), latest.Revision > afterRevision);
    }

    public async Task<JobSummaryDto?> StartDirectoryRetrievalAsync(
        Guid viewId,
        RetrieveSearchViewDirectoryRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request.Revision));
        ArgumentNullException.ThrowIfNull(request.Directory);
        if (string.IsNullOrEmpty(request.Directory.Ref))
            throw new ArgumentException("The search-view directory ref is required.");

        var exactIdentity = new PeerDirectoryIdentity(
            request.Directory.Username,
            request.Directory.FolderPath);
        SearchViewStore repository = RequiredStore();
        StoredSearchView? sourceView = await repository.GetAsync(
            viewId,
            cancellationToken).ConfigureAwait(false);
        if (sourceView == null)
            return null;

        StoredSearchViewDirectoryFilePage? membership = await repository
            .GetDirectoryFilesAsync(
                viewId,
                request.Directory.Ref,
                request.Revision,
                after: null,
                limit: 1,
                cancellationToken).ConfigureAwait(false);
        if (membership == null)
            throw new KeyNotFoundException("The directory is not in the issuing Search View revision.");
        if (!StringComparer.Ordinal.Equals(membership.Username, exactIdentity.Username)
            || !StringComparer.Ordinal.Equals(membership.FolderPath, exactIdentity.FolderPath))
        {
            throw new ArgumentException(
                "The exact peer-directory identity does not match the search-view ref.");
        }
        if (membership.Items.Count == 0)
        {
            throw new InvalidDataException(
                "The search-view directory has no retained child observation.");
        }

        (SearchDefinition definition, SearchViewProjectionDefinition projection) =
            ParseDurableDefinition(sourceView.DefinitionJson);
        SearchProjectionInput peerObservation = membership.Items[0].File.Input;
        return await supervisor.StartSearchViewDirectoryRetrievalAsync(
            sourceView.SourceJobId,
            exactIdentity,
            async (snapshot, operationToken) =>
            {
                ProjectedFileCandidate[] projected = ProjectRetrievedDirectoryFiles(
                    snapshot,
                    peerObservation,
                    sourceView.ConsumedSequence,
                    definition,
                    projection);
                StoredSearchViewDirectoryPublishResult published = await repository
                    .PublishRetrievedDirectoryAsync(
                        viewId,
                        request.Directory.Ref,
                        snapshot,
                        projected,
                        operationToken).ConfigureAwait(false);
                NotifyPublished(viewId, published.View.Revision);
                return published.NewFileCount;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommitSearchViewSelectionResponseDto?> CommitSelectionAsync(
        Guid viewId,
        CommitSearchViewSelectionRequestDto request,
        CancellationToken cancellationToken)
    {
        ValidateCommitRequest(request);
        SearchViewStore repository = RequiredStore();
        StoredSearchView? sourceView = await repository.GetAsync(
            viewId,
            cancellationToken).ConfigureAwait(false);
        if (sourceView == null)
            return null;
        string fingerprint = SubmissionCommitCoordinator.Fingerprint(
            "search-view",
            viewId,
            request.Revision,
            request.Selection);
        return await commits.ExecuteAsync(
            request.IdempotencyKey,
            fingerprint,
            async operationToken =>
            {
                CommitSearchViewSelectionResponseDto receipt = await CommitSelectionCoreAsync(
                    viewId,
                    sourceView,
                    request,
                    fingerprint,
                    operationToken).ConfigureAwait(false);
                return new SubmissionCommitExecution<CommitSearchViewSelectionResponseDto>(
                    receipt,
                    receipt.SubmissionId);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommitSearchViewSelectionResponseDto> CommitSelectionCoreAsync(
        Guid viewId,
        StoredSearchView sourceView,
        CommitSearchViewSelectionRequestDto request,
        string commitFingerprint,
        CancellationToken cancellationToken)
    {
        SearchViewStore repository = RequiredStore();
        if (await repository.GetRevisionAsync(viewId, request.Revision, cancellationToken)
                .ConfigureAwait(false) == null)
            throw new InvalidOperationException("The search-view revision is stale or unavailable.");

        var started = Stopwatch.StartNew();
        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        var foundRefs = request.Selection.Mode == RefSelectionMode.Only
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        long requested = 0;
        long resolved = 0;
        long submitted = 0;
        long skipped = 0;
        long rejected = 0;
        var reasons = new Dictionary<string, long>(StringComparer.Ordinal);
        var root = new JobList("Search View selection");
        var selectedContainers = new HashSet<string>(StringComparer.Ordinal);
        await foreach (StoredSearchViewCommitItem item in repository
            .ReadCommitItemsAsync(
                viewId,
                request.Revision,
                sourceView.ProjectionKind,
                request.Selection.Mode.ToString(),
                refs,
                cancellationToken)
            .ConfigureAwait(false))
        {
            requested = checked(requested + 1);
            resolved = checked(resolved + 1);
            foundRefs?.Add(item.Ref);
            if ((item.ParentRef != null && selectedContainers.Contains(item.ParentRef))
                || item.ContainerRefs?.Any(selectedContainers.Contains) == true)
            {
                skipped = checked(skipped + 1);
                continue;
            }
            try
            {
                (Job? job, string? rejectionReason) = await ResolveSelectionJobAsync(
                    repository,
                    viewId,
                    request.Revision,
                    item,
                    cancellationToken).ConfigureAwait(false);
                if (job == null)
                {
                    rejected = checked(rejected + 1);
                    AddReason(reasons, rejectionReason ?? "unavailable-item");
                    continue;
                }
                supervisor.PrepareSearchViewSelectionJob(job);
                root.Add(job);
                submitted = checked(submitted + 1);
                if (item.Directory != null
                    || item.AggregateTrackGroup != null
                    || item.AggregateAlbumGroup != null)
                    selectedContainers.Add(item.Ref);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                rejected = checked(rejected + 1);
                AddReason(reasons, "invalid-item");
            }
        }

        if (foundRefs != null)
        {
            long missing = refs.Count - foundRefs.Count;
            if (missing > 0)
            {
                requested = checked(requested + missing);
                rejected = checked(rejected + missing);
                AddReason(reasons, "missing-ref", missing);
            }
        }

        JobSummaryDto? summary = null;
        if (root.Count > 0)
        {
            summary = await supervisor.QueueSearchViewSelectionAsync(
                root,
                sourceView.SourceJobId,
                request.IdempotencyKey,
                commitFingerprint,
                cancellationToken).ConfigureAwait(false);
        }

        var receipt = new CommitSearchViewSelectionResponseDto(
            viewId,
            request.Revision,
            summary == null ? null : request.IdempotencyKey,
            summary?.WorkflowId,
            requested,
            resolved,
            submitted,
            skipped,
            rejected,
            reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new SubmissionReasonCountDto(pair.Key, pair.Value))
                .ToArray());
        ServerLogMessages.SearchViewCommitted(
            logger,
            viewId,
            request.Revision,
            started.ElapsedMilliseconds,
            requested,
            submitted,
            rejected);
        return receipt;
    }

    private static async Task<(Job? Job, string? RejectionReason)> ResolveSelectionJobAsync(
        SearchViewStore repository,
        Guid viewId,
        long viewRevision,
        StoredSearchViewCommitItem item,
        CancellationToken cancellationToken)
    {
        if (item.File is { } file)
        {
            if (file.Input.Visibility == SearchResultVisibility.Locked)
                return (null, "locked");
            return (new RemoteFileJob(file.Input.ToFileCandidate().Target), null);
        }
        if (item.Directory is { } directory)
        {
            if (directory.PublicMatchingFileCount == 0)
                return (null, "locked");
            return (new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                new PeerDirectoryIdentity(directory.Username, directory.FolderPath))), null);
        }
        if (item.AggregateTrackGroup is { } aggregate)
        {
            var candidates = new List<FileCandidate>();
            await foreach (StoredSearchViewFile option in repository
                .ReadPublicAggregateTrackOptionsAsync(
                    viewId,
                    aggregate.Ref,
                    viewRevision,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                candidates.Add(option.Input.ToFileCandidate());
            }
            if (candidates.Count == 0)
                return (null, "locked");
            SongQuery query = JsonSerializer.Deserialize<SongQuery>(aggregate.QueryJson)
                ?? throw new InvalidDataException(
                    "A selected aggregate-track query is invalid.");
            return (new SongJob(query) { Candidates = candidates }, null);
        }
        if (item.AggregateAlbumGroup is { } aggregateAlbum)
        {
            AlbumQuery query = JsonSerializer.Deserialize<AlbumQuery>(
                    aggregateAlbum.QueryJson)
                ?? throw new InvalidDataException(
                    "A selected aggregate-album query is invalid.");
            var defaultTrackQuery = new SongQuery
            {
                Artist = query.Artist,
                Album = query.Album,
                URI = query.URI,
                ArtistMaybeWrong = query.ArtistMaybeWrong,
            };
            var folders = new List<AlbumFolder>();
            await foreach (StoredSearchViewDirectory option in repository
                .ReadPublicAggregateAlbumOptionsAsync(
                    viewId,
                    aggregateAlbum.Ref,
                    viewRevision,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                var files = new List<AlbumFile>();
                await foreach (StoredSearchViewDirectoryFile child in repository
                    .ReadPublicDirectoryFilesAsync(
                        viewId,
                        option.Ref,
                        viewRevision,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    FileCandidate candidate = child.File.Input.ToFileCandidate();
                    files.Add(AlbumFile.WithLazyQuery(
                        () => Searcher.InferSongQuery(
                            candidate.Filename,
                            defaultTrackQuery),
                        candidate));
                }
                if (files.Count == 0)
                    continue;
                folders.Add(new AlbumFolder(
                    option.Username,
                    option.FolderPath,
                    files)
                {
                    IsFullyRetrieved = option.IsFullyRetrieved,
                });
            }
            if (folders.Count == 0)
                return (null, "locked");
            return (new AlbumJob(query) { Results = folders }, null);
        }
        return (null, "unsupported-item");
    }

    private static void AddReason(
        IDictionary<string, long> reasons,
        string reason,
        long amount = 1)
    {
        if (amount <= 0)
            return;
        reasons[reason] = reasons.TryGetValue(reason, out long count)
            ? checked(count + amount)
            : amount;
    }

    private static void ValidateCommitRequest(CommitSearchViewSelectionRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.Selection.Refs);
        if (request.IdempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.");
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Search View revision cannot be negative.");
        if (request.Selection.Refs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Selection refs cannot be empty.");
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (Guid viewId in work.Reader.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            RuntimeView? runtime = null;
            try
            {
                if (runtimes.TryGetValue(viewId, out runtime))
                    await ProcessAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (runtime != null)
                    await FailAsync(runtime, exception, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                pending.TryRemove(viewId, out _);
                if (runtimes.TryGetValue(viewId, out RuntimeView? remaining)
                    && remaining.Source.HasPending(
                        remaining.Kernel.ConsumedSequence,
                        remaining.Kernel.IsComplete))
                    Schedule(viewId);
            }
        }
    }

    private async Task ProcessAsync(
        RuntimeView runtime,
        CancellationToken cancellationToken)
    {
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SourceBatch batch = await runtime.Source.ReadAsync(
                runtime.Kernel.ConsumedSequence,
                ProjectionBatchSize,
                cancellationToken).ConfigureAwait(false);
            bool complete = batch.IsComplete && batch.Items.Count < ProjectionBatchSize;
            if (batch.Items.Count == 0 && !complete)
                return;
            SearchViewKernelUpdate update = runtime.Kernel.Apply(
                batch.Items,
                batch.SourceRevision,
                complete);
            StoredSearchView published;
            try
            {
                published = await RequiredStore().PublishAsync(
                    runtime.ViewId,
                    update,
                    batch.RetentionState,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SearchViewPublicationException(update, exception);
            }
            NotifyPublished(runtime.ViewId, published.Revision);
            if (complete)
            {
                runtime.Detach();
                runtimes.TryRemove(runtime.ViewId, out _);
                ServerLogMessages.SearchViewCompleted(
                    logger,
                    runtime.ViewId,
                    published.RetentionState,
                    published.Revision,
                    published.Counters.PublicFileCount,
                    published.Counters.LockedFileCount,
                    published.Counters.ProjectedFileCount);
            }
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async Task FailAsync(
        RuntimeView runtime,
        Exception exception,
        CancellationToken cancellationToken)
    {
        runtime.Detach();
        runtimes.TryRemove(runtime.ViewId, out _);
        ServerLogMessages.SearchViewProjectionFailed(logger, exception, runtime.ViewId);
        try
        {
            SearchViewKernelUpdate terminal;
            if (exception is SearchViewPublicationException publication)
            {
                terminal = publication.Update with { IsComplete = true };
            }
            else
            {
                StoredSearchView current = await RequiredStore().GetAsync(
                    runtime.ViewId,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The search view was not found.");
                terminal = new SearchViewKernelUpdate(
                    current.SourceRevision,
                    current.ConsumedSequence,
                    true,
                    current.Counters,
                    []);
            }
            StoredSearchView published = await RequiredStore().PublishAsync(
                runtime.ViewId,
                terminal,
                "Incomplete",
                cancellationToken).ConfigureAwait(false);
            NotifyPublished(runtime.ViewId, published.Revision);
        }
        catch (Exception terminalException) when (!cancellationToken.IsCancellationRequested)
        {
            ServerLogMessages.SearchViewIncompleteStateFailed(
                logger,
                terminalException,
                runtime.ViewId);
        }
    }

    private void NotifyPublished(Guid viewId, long revision)
    {
        Delegate[] observers = ViewPublished?.GetInvocationList() ?? [];
        foreach (Action<Guid, long> observer in observers.Cast<Action<Guid, long>>())
        {
            try
            {
                observer(viewId, revision);
            }
            catch (Exception exception)
            {
                ServerLogMessages.SearchViewObserverFailed(
                    logger,
                    exception,
                    viewId,
                    revision);
            }
        }
    }

    private void Schedule(Guid viewId)
    {
        if (pending.TryAdd(viewId, 0))
            work.Writer.TryWrite(viewId);
    }

    private async Task RestoreIncompleteAsync(
        SearchViewStore repository,
        CancellationToken cancellationToken)
    {
        int restored = 0;
        try
        {
            await foreach (StoredSearchView stored in repository.ReadIncompleteAsync(
                cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    (SearchDefinition definition, SearchViewProjectionDefinition projection) =
                        ParseDurableDefinition(stored.DefinitionJson);
                    RuntimeSource? source = await CreateStoredSourceAsync(
                        stored.SourceJobId,
                        definition,
                        projection,
                        cancellationToken).ConfigureAwait(false);
                    if (source == null)
                    {
                        await CompleteUnrestorableAsync(
                            repository,
                            stored,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    SearchViewKernel kernel = await RebuildKernelAsync(
                        source,
                        projection,
                        definition.ProjectionSettings.ToSettings(),
                        stored,
                        cancellationToken).ConfigureAwait(false);
                    var runtime = new RuntimeView(
                        stored.Id,
                        source,
                        kernel,
                        () => Schedule(stored.Id));
                    if (!runtimes.TryAdd(stored.Id, runtime))
                        continue;
                    runtime.Attach();
                    Schedule(stored.Id);
                    restored++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ServerLogMessages.SearchViewRecoveryFailed(
                        logger,
                        exception,
                        stored.Id);
                    await CompleteUnrestorableAsync(
                        repository,
                        stored,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ServerLogMessages.SearchViewRecoveryEnumerationDegraded(logger, exception);
        }
        if (restored > 0)
        {
            ServerLogMessages.SearchViewRecoveryScheduled(logger, restored);
        }
    }

    private async Task CompleteUnrestorableAsync(
        SearchViewStore repository,
        StoredSearchView stored,
        CancellationToken cancellationToken)
    {
        try
        {
            var terminal = new SearchViewKernelUpdate(
                stored.SourceRevision,
                stored.ConsumedSequence,
                true,
                stored.Counters,
                []);
            StoredSearchView published = await repository.PublishAsync(
                stored.Id,
                terminal,
                "Incomplete",
                cancellationToken).ConfigureAwait(false);
            NotifyPublished(stored.Id, published.Revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ServerLogMessages.SearchViewRecoveryOutcomeFailed(
                logger,
                exception,
                stored.Id);
        }
    }

    private async Task<RuntimeSource?> CreateStoredSourceAsync(
        Guid sourceJobId,
        SearchDefinition definition,
        SearchViewProjectionDefinition projection,
        CancellationToken cancellationToken)
    {
        Job? live = supervisor.GetRuntimeJob<Job>(sourceJobId);
        if (live?.Config != null && live.SearchObservationSession is { } liveSession)
            return new LiveSource(liveSession);
        if (persistence.SearchHistory == null)
            return null;
        await persistence.WaitForJobHandoffAsync(sourceJobId, cancellationToken)
            .ConfigureAwait(false);
        PersistedSearchMetadata? metadata = await persistence.SearchHistory.GetMetadataAsync(
            sourceJobId,
            cancellationToken).ConfigureAwait(false);
        return metadata == null
            ? null
            : new HistoricalSource(
                persistence.SearchHistory,
                metadata,
                definition,
                projection);
    }

    private static (SearchDefinition Definition, SearchViewProjectionDefinition Projection)
        ParseDurableDefinition(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string definitionJson = root.GetProperty("searchDefinitionJson").GetString()
            ?? throw new InvalidDataException("The search-view definition is missing.");
        CreateSearchViewRequestDto projectionDto =
            root.GetProperty("projection").Deserialize<CreateSearchViewRequestDto>(
                SockseekApiJson.CreateSerializerOptions())
            ?? throw new InvalidDataException("The search-view projection is missing.");
        SearchDefinition definition = SearchDefinitionCodec.Deserialize(definitionJson);
        SearchViewProjectionDefinition projection = ResolveProjection(
            definition,
            projectionDto);
        return (definition, projection);
    }

    private static async Task<SearchViewKernel> RebuildKernelAsync(
        RuntimeSource source,
        SearchViewProjectionDefinition projection,
        SearchSettings settings,
        StoredSearchView stored,
        CancellationToken cancellationToken)
    {
        var kernel = new SearchViewKernel(
            projection,
            settings,
            retainProjectedRows: false,
            trackPeerIdentities: false);
        while (kernel.ConsumedSequence < stored.ConsumedSequence)
        {
            SourceBatch batch = await source.ReadAsync(
                kernel.ConsumedSequence,
                ProjectionBatchSize,
                cancellationToken).ConfigureAwait(false);
            SearchProjectionInput[] prefix = batch.Items
                .Where(item => item.Sequence <= stored.ConsumedSequence)
                .ToArray();
            if (prefix.Length == 0)
            {
                throw new InvalidDataException(
                    "The retained Search View source no longer contains its durable observed prefix.");
            }
            kernel.Apply(prefix, stored.SourceRevision, isComplete: false);
        }
        if (kernel.ConsumedSequence != stored.ConsumedSequence)
            throw new InvalidDataException("The Search View durable sequence boundary is invalid.");
        return kernel;
    }

    private async Task<RuntimeSource?> CreateHistoricalSourceAsync(
        Guid sourceJobId,
        CreateSearchViewRequestDto request,
        CancellationToken cancellationToken)
    {
        if (persistence.SearchHistory == null || persistence.JobHistory == null)
            return null;
        await persistence.WaitForJobHandoffAsync(sourceJobId, cancellationToken)
            .ConfigureAwait(false);
        PersistedSearchMetadata? metadata = await persistence.SearchHistory
            .GetMetadataAsync(sourceJobId, cancellationToken).ConfigureAwait(false);
        PersistedJob? job = await persistence.JobHistory.GetJobAsync(
            sourceJobId,
            cancellationToken).ConfigureAwait(false);
        if (metadata == null || job == null)
            return null;
        SearchDefinition definition = await HistoricalJobDtoMapper.SearchDefinitionAsync(
            persistence.Submissions,
            job,
            cancellationToken).ConfigureAwait(false);
        SearchViewProjectionDefinition projection = ResolveProjection(
            definition,
            request);
        return new HistoricalSource(
            persistence.SearchHistory,
            metadata,
            definition,
            projection);
    }

    private static SearchViewProjectionDefinition ResolveProjection(
        SearchDefinition definition,
        CreateSearchViewRequestDto request,
        SongQuery? defaultSongQuery = null,
        AlbumQuery? defaultAlbumQuery = null)
    {
        SearchViewProjectionKind kind = request.Kind ?? definition.DefaultProjection switch
        {
            SearchDefaultProjectionKind.GenericFile => SearchViewProjectionKind.GenericDirectories,
            SearchDefaultProjectionKind.Track => SearchViewProjectionKind.Files,
            SearchDefaultProjectionKind.Album => SearchViewProjectionKind.AlbumDirectories,
            _ => throw new ArgumentOutOfRangeException(),
        };
        SongQuery? song = request.SongQuery == null
            ? defaultSongQuery ?? definition.FileQuery?.ToQuery()
            : JobRequestMapper.ToSongQuery(request.SongQuery);
        AlbumQuery? album = request.AlbumQuery == null
            ? defaultAlbumQuery ?? definition.AlbumQuery?.ToQuery()
            : JobRequestMapper.ToAlbumQuery(request.AlbumQuery);
        if (kind is SearchViewProjectionKind.Files
            or SearchViewProjectionKind.GenericDirectories
            or SearchViewProjectionKind.AggregateTracks)
            song ??= new SongQuery { Title = definition.NetworkQuery };
        var resolved = new SearchViewProjectionDefinition(
            kind,
            song,
            album,
            request.IncludeFullResults);
        resolved.Validate();
        return resolved;
    }

    private static ProjectedFileCandidate[] ProjectRetrievedDirectoryFiles(
        PeerDirectorySnapshot snapshot,
        SearchProjectionInput peerObservation,
        long sequenceBase,
        SearchDefinition definition,
        SearchViewProjectionDefinition projection)
    {
        AlbumQuery album = projection.AlbumQuery
            ?? definition.AlbumQuery?.ToQuery()
            ?? new AlbumQuery();
        SongQuery query = projection.SongQuery ?? new SongQuery
        {
            Artist = album.Artist,
            Album = album.Album,
            URI = album.URI,
            ArtistMaybeWrong = album.ArtistMaybeWrong,
        };
        var sorter = new IncrementalResultSorter(
            query,
            definition.ProjectionSettings.ToSettings(),
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal),
            albumMode: projection.Kind is SearchViewProjectionKind.AlbumDirectories
                or SearchViewProjectionKind.AggregateAlbums,
            ignoreStringSortConditions:
                projection.Kind == SearchViewProjectionKind.AggregateAlbums,
            retainProjectedRows: false);
        SearchProjectionInput[] inputs = snapshot.Files
            .Select((target, index) => new SearchProjectionInput(
                checked(sequenceBase + index + 1),
                peerObservation.Revision,
                target.Username,
                snapshot.Files.Count,
                target.Filename,
                target.Size ?? -1,
                target.BitRate,
                target.BitDepth,
                target.SampleRate,
                target.Length,
                target.Extension ?? string.Empty,
                peerObservation.UploadSpeed,
                peerObservation.HasFreeUploadSlot,
                target.Attributes,
                peerObservation.ObservedAtUtc,
                peerObservation.QueueLength,
                SearchResultVisibility.Public))
            .ToArray();
        ProjectedFileCandidate[] projected = sorter.AddRangeAndGetProjected(inputs).ToArray();
        if (projected.Length != snapshot.Files.Count)
        {
            throw new InvalidDataException(
                "The retrieved directory could not be represented completely in the Search View.");
        }
        return projected;
    }

    private static SearchDefinition AuthoritativeDefinition(Job job)
    {
        if (string.IsNullOrWhiteSpace(job.SubmissionSpecificationJson))
        {
            return job.ExecutedSearchDefinition
                ?? (job as SearchJob)?.Definition
                ?? throw new InvalidOperationException(
                    "The live search has no accepted search definition.");
        }
        SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(
            job.SubmissionSpecificationJson);
        return specification.Search
            ?? specification.Command.SearchDefinition
            ?? job.ExecutedSearchDefinition
            ?? throw new InvalidOperationException(
                "The accepted search submission has no search definition.");
    }

    private SearchViewStore RequiredStore()
        => store ?? throw new SearchViewUnavailableException(
            "Search Views are unavailable; search execution remains available.",
            initializationFailure);

    private static SearchViewSummaryDto ToDto(StoredSearchView view)
        => new(
            view.Id,
            view.SourceJobId,
            view.CreatedAtUtc,
            view.ExpiresAtUtc,
            view.Revision,
            view.SourceRevision,
            view.ConsumedSequence,
            view.IsComplete,
            ParseRetention(view.RetentionState),
            ToDto(view.Counters));

    private static SearchViewRevisionDto ToDto(StoredSearchViewRevision revision)
        => new(
            revision.ViewId,
            revision.Revision,
            revision.SourceRevision,
            revision.ConsumedSequence,
            revision.IsComplete,
            ParseRetention(revision.RetentionState),
            ToDto(revision.Counters));

    private static SearchViewCountersDto ToDto(SearchViewCounters counters)
        => new(
            counters.PublicFileCount,
            counters.LockedFileCount,
            counters.PublicBytes,
            counters.LockedBytes,
            counters.ObservedPeerCount,
            counters.ProjectedFileCount,
            counters.ProjectedPublicFileCount,
            counters.ProjectedLockedFileCount,
            counters.PreferredFileCount,
            counters.OtherFileCount,
            counters.TopLevelItemCount,
            counters.SelectableOptionCount);

    private static SearchViewFileDto ToDto(StoredSearchViewFile row)
        => new(
            row.Ref,
            row.Input.Visibility,
            row.PreferenceTier,
            row.NecessaryConditionsSatisfied,
            row.SatisfiedPreferredConditions,
            row.ConfiguredPreferredConditions
                .Except(row.SatisfiedPreferredConditions)
                .ToArray(),
            row.Input.Filename,
            new PeerInfoDto(
                row.Input.Username,
                row.Input.HasFreeUploadSlot,
                row.Input.UploadSpeed,
                row.Input.QueueLength,
                row.Input.ObservedAtUtc),
            new FileMetadataDto(
                Utils.GetFileNameSlsk(row.Input.Filename),
                row.Input.Size,
                row.Input.Extension,
                row.Input.BitRate,
                row.Input.BitDepth,
                row.Input.SampleRate,
                row.Input.Length,
                row.Input.Attributes?.Select(attribute => new FileAttributeDto(
                    attribute.Type,
                    attribute.Value)).ToArray()));

    private static SearchViewDirectoryDto ToDto(StoredSearchViewDirectory row)
    {
        SearchViewFileDto best = ToDto(row.BestChild);
        SearchViewDirectoryVisibility visibility = row switch
        {
            { PublicMatchingFileCount: > 0, LockedMatchingFileCount: > 0 }
                => SearchViewDirectoryVisibility.Mixed,
            { PublicMatchingFileCount: > 0 } => SearchViewDirectoryVisibility.Public,
            _ => SearchViewDirectoryVisibility.Locked,
        };
        return new(
            new PeerDirectoryRefDto(row.Ref, row.Username, row.FolderPath),
            visibility,
            row.BestChild.PreferenceTier,
            row.BestChild.SatisfiedPreferredConditions,
            row.PublicMatchingFileCount,
            row.LockedMatchingFileCount,
            row.PublicMatchingBytes,
            row.LockedMatchingBytes,
            row.RetrievedFileCount,
            row.RetrievedBytes,
            row.IsFullyRetrieved
                ? SearchViewDirectoryRetrievalState.Complete
                : row.RetrievedFileCount != null
                    ? SearchViewDirectoryRetrievalState.Incomplete
                    : SearchViewDirectoryRetrievalState.SearchResultsOnly,
            best.Peer,
            best);
    }

    private static SearchViewAggregateTrackGroupDto ToDto(
        StoredSearchViewAggregateTrackGroup row)
    {
        SongQuery query = JsonSerializer.Deserialize<SongQuery>(row.QueryJson)
            ?? throw new InvalidDataException(
                "A retained aggregate-track query is invalid.");
        return new(
            row.Ref,
            ServerSnapshotMapper.ToSongQueryDto(query),
            row.ShareCount,
            row.SelectableOptionCount,
            ToDto(row.Representative));
    }

    private static SearchViewAggregateAlbumGroupDto ToDto(
        StoredSearchViewAggregateAlbumGroup row)
    {
        AlbumQuery query = JsonSerializer.Deserialize<AlbumQuery>(row.QueryJson)
            ?? throw new InvalidDataException(
                "A retained aggregate-album query is invalid.");
        return new(
            row.Ref,
            ServerSnapshotMapper.ToAlbumQueryDto(query),
            row.ShareCount,
            row.SelectableOptionCount,
            ToDto(row.Representative));
    }

    private static SearchViewRetentionState ParseRetention(string value)
        => Enum.TryParse(value, ignoreCase: true, out SearchViewRetentionState parsed)
            ? parsed
            : SearchViewRetentionState.Incomplete;

    private static CreateSearchViewRequestDto ToDto(
        SearchViewProjectionDefinition projection)
        => new(
            projection.Kind,
            projection.SongQuery == null
                ? null
                : ServerSnapshotMapper.ToSongQueryDto(projection.SongQuery),
            projection.AlbumQuery == null
                ? null
                : ServerSnapshotMapper.ToAlbumQueryDto(projection.AlbumQuery),
            projection.IncludeFullResults);

    private sealed class RuntimeView(
        Guid viewId,
        RuntimeSource source,
        SearchViewKernel kernel,
        Action signal)
    {
        public Guid ViewId { get; } = viewId;
        public RuntimeSource Source { get; } = source;
        public SearchViewKernel Kernel { get; } = kernel;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public void Attach() => Source.Attach(signal);
        public void Detach() => Source.Detach(signal);
    }

    private abstract class RuntimeSource
    {
        public abstract Task<SourceBatch> ReadAsync(
            long afterSequence,
            int limit,
            CancellationToken cancellationToken);
        public abstract bool HasPending(long consumedSequence, bool kernelComplete);
        public virtual void Attach(Action signal) { }
        public virtual void Detach(Action signal) { }
    }

    private sealed class LiveSource(SearchSession session) : RuntimeSource
    {
        private Action? attachedSignal;
        private void OnResult(SearchRawResult _) => attachedSignal?.Invoke();
        private void OnCompleted() => attachedSignal?.Invoke();

        public override Task<SourceBatch> ReadAsync(
            long afterSequence,
            int limit,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<SearchProjectionInput> rows = session.RawSnapshot(afterSequence, limit)
                .Select(row => row.ProjectionInput)
                .ToArray();
            return Task.FromResult(new SourceBatch(
                rows,
                session.Revision,
                session.IsComplete,
                session.IsComplete ? "Complete" : "Live"));
        }

        public override bool HasPending(long consumedSequence, bool kernelComplete)
            => session.RawSnapshot(consumedSequence, 1).Count > 0
                || session.IsComplete && !kernelComplete;

        public override void Attach(Action signal)
        {
            attachedSignal = signal;
            session.RawResultAdded += OnResult;
            session.Completed += OnCompleted;
        }

        public override void Detach(Action signal)
        {
            session.RawResultAdded -= OnResult;
            session.Completed -= OnCompleted;
            attachedSignal = null;
        }
    }

    private sealed class HistoricalSource(
        ISearchHistoryReader reader,
        PersistedSearchMetadata metadata,
        SearchDefinition definition,
        SearchViewProjectionDefinition projection) : RuntimeSource
    {
        public SearchDefinition Definition { get; } = definition;
        public SearchViewProjectionDefinition Projection { get; } = projection;

        public override async Task<SourceBatch> ReadAsync(
            long afterSequence,
            int limit,
            CancellationToken cancellationToken)
        {
            PersistedSearchResultPage page = await reader.GetRawResultsAsync(
                metadata.JobId,
                afterSequence,
                limit,
                cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The retained search source was not found.");
            return new(
                page.Items.Select(item => item.ToProjectionInput()).ToArray(),
                checked((int)metadata.Revision),
                page.NextSequence == null,
                metadata.ResultPersistenceState == "Complete"
                    ? "Complete"
                    : metadata.ResultPersistenceState);
        }

        public override bool HasPending(long consumedSequence, bool kernelComplete)
            => !kernelComplete;
    }

    private sealed record SourceBatch(
        IReadOnlyList<SearchProjectionInput> Items,
        int SourceRevision,
        bool IsComplete,
        string RetentionState);

    private sealed class SearchViewPublicationException(
        SearchViewKernelUpdate update,
        Exception inner) : InvalidOperationException(
            "The search-view revision could not be published.",
            inner)
    {
        public SearchViewKernelUpdate Update { get; } = update;
    }

    public ValueTask DisposeAsync()
    {
        lifetime.Dispose();
        foreach (RuntimeView runtime in runtimes.Values)
        {
            runtime.Detach();
            runtime.Gate.Dispose();
        }
        runtimes.Clear();
        store = null;
        return ValueTask.CompletedTask;
    }
}
