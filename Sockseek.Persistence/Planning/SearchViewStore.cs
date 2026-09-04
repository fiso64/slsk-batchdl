using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Planning;

public sealed record StoredSearchView(
    Guid Id,
    Guid SourceJobId,
    string ProjectionKind,
    string DefinitionJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    string RetentionState,
    SearchViewCounters Counters);

public sealed record StoredSearchViewRevision(
    Guid ViewId,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    string RetentionState,
    SearchViewCounters Counters);

public sealed record StoredSearchViewFile(
    string Ref,
    long AdmittedRevision,
    SearchProjectionInput Input,
    SearchPreferenceTier PreferenceTier,
    bool NecessaryConditionsSatisfied,
    IReadOnlyList<SearchPreferenceCondition> SatisfiedPreferredConditions,
    IReadOnlyList<SearchPreferenceCondition> ConfiguredPreferredConditions,
    SearchProjectionSortKey SortKey);

public sealed record SearchViewFilePosition(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker,
    long Sequence,
    string Ref);

public sealed record StoredSearchViewFilePage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewFile> Items,
    SearchViewFilePosition? NextPosition);

public sealed record StoredSearchViewDirectory(
    string Ref,
    string Username,
    string FolderPath,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    bool IsFullyRetrieved,
    long? RetrievedFileCount,
    long? RetrievedBytes,
    StoredSearchViewFile BestChild);

public sealed record SearchViewDirectoryPosition(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker,
    long Sequence,
    string Username,
    string FolderPath,
    string Ref);

public sealed record StoredSearchViewDirectoryPage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewDirectory> Items,
    SearchViewDirectoryPosition? NextPosition);

public sealed record StoredSearchViewDirectoryFile(
    string Ref,
    string RelativePath,
    StoredSearchViewFile File);

public sealed record SearchViewDirectoryFilePosition(
    string RelativePath,
    string Ref);

public sealed record StoredSearchViewDirectoryFilePage(
    StoredSearchViewRevision Revision,
    string DirectoryRef,
    string Username,
    string FolderPath,
    IReadOnlyList<StoredSearchViewDirectoryFile> Items,
    SearchViewDirectoryFilePosition? NextPosition);

public sealed record StoredSearchViewDirectoryPublishResult(
    StoredSearchView View,
    int NewFileCount);

public sealed record StoredSearchViewAggregateTrackGroup(
    string Ref,
    int Index,
    string QueryJson,
    int ShareCount,
    long SelectableOptionCount,
    StoredSearchViewFile Representative);

public sealed record SearchViewAggregateTrackPosition(
    int ShareCount,
    int Index,
    string Ref);

public sealed record StoredSearchViewAggregateTrackPage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewAggregateTrackGroup> Items,
    SearchViewAggregateTrackPosition? NextPosition);

public sealed record StoredSearchViewAggregateTrackOptionPage(
    StoredSearchViewRevision Revision,
    string GroupRef,
    IReadOnlyList<StoredSearchViewFile> Items,
    SearchViewFilePosition? NextPosition);

public sealed record StoredSearchViewAggregateAlbumGroup(
    string Ref,
    int Index,
    string QueryJson,
    int ShareCount,
    long SelectableOptionCount,
    StoredSearchViewDirectory Representative);

public sealed record SearchViewAggregateAlbumPosition(
    int ShareCount,
    int Index,
    string Ref);

public sealed record StoredSearchViewAggregateAlbumPage(
    StoredSearchViewRevision Revision,
    IReadOnlyList<StoredSearchViewAggregateAlbumGroup> Items,
    SearchViewAggregateAlbumPosition? NextPosition);

public sealed record StoredSearchViewAggregateAlbumOptionPage(
    StoredSearchViewRevision Revision,
    string GroupRef,
    IReadOnlyList<StoredSearchViewDirectory> Items,
    SearchViewDirectoryPosition? NextPosition);

public sealed record StoredSearchViewCommitItem(
    string Kind,
    string Ref,
    StoredSearchViewFile? File,
    StoredSearchViewDirectory? Directory,
    StoredSearchViewAggregateTrackGroup? AggregateTrackGroup = null,
    StoredSearchViewAggregateAlbumGroup? AggregateAlbumGroup = null,
    string? ParentRef = null,
    IReadOnlyList<string>? ContainerRefs = null);

/// <summary>
/// Disk-backed immutable search-view revisions. File rows are stored once with
/// their admission revision; revisions retain counters and query the exact
/// observed prefix through that boundary instead of copying the full view.
/// </summary>
public sealed class SearchViewStore(
    string databasePath,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public const int MaximumPageSize = 200;
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private readonly string path = Path.GetFullPath(databasePath);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int initialized;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = true,
        DefaultTimeout = 5,
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
            return;
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'search_views';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)) != 1)
            {
                throw new InvalidDataException(
                    "The main persistence schema does not contain Search View tables.");
            }
        }
        catch
        {
            Interlocked.Exchange(ref initialized, 0);
            throw;
        }
    }

    public Task<StoredSearchView> CreateAsync(
        Guid sourceJobId,
        SearchViewProjectionKind projectionKind,
        string definitionJson,
        TimeSpan? retention = null,
        CancellationToken cancellationToken = default)
        => WithWriteAsync(async (connection, transaction, ct) =>
        {
            DateTimeOffset created = clock.GetUtcNow();
            var view = new StoredSearchView(
                Guid.NewGuid(),
                sourceJobId,
                projectionKind.ToString(),
                definitionJson,
                created,
                created + (retention ?? DefaultRetention),
                0,
                0,
                0,
                false,
                "Live",
                EmptyCounters);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO search_views (
                    id, source_job_id, projection_kind, definition_json,
                    created_at_utc, expires_at_utc, revision, source_revision,
                    consumed_sequence, is_complete, retention_state,
                    public_file_count, locked_file_count, public_bytes, locked_bytes,
                    observed_peer_count, projected_file_count,
                    projected_public_file_count, projected_locked_file_count,
                    preferred_file_count, other_file_count,
                    top_level_item_count, selectable_option_count)
                VALUES (
                    $id, $source, $projection, $definition, $created, $expires,
                    0, 0, 0, 0, 'Live', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                """;
            Add(command, "$id", view.Id);
            Add(command, "$source", view.SourceJobId);
            Add(command, "$projection", view.ProjectionKind);
            Add(command, "$definition", definitionJson);
            Add(command, "$created", view.CreatedAtUtc);
            Add(command, "$expires", view.ExpiresAtUtc);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using var revision = connection.CreateCommand();
            revision.Transaction = transaction;
            revision.CommandText = """
                INSERT INTO search_view_revisions (
                    view_id, revision, source_revision, consumed_sequence,
                    is_complete, retention_state, public_file_count,
                    locked_file_count, public_bytes, locked_bytes,
                    observed_peer_count, projected_file_count,
                    projected_public_file_count, projected_locked_file_count,
                    preferred_file_count, other_file_count,
                    top_level_item_count, selectable_option_count)
                VALUES ($view, 0, 0, 0, 0, 'Live', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                """;
            Add(revision, "$view", view.Id);
            await revision.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return view;
        }, cancellationToken);

    public Task<StoredSearchView?> GetAsync(
        Guid viewId,
        CancellationToken cancellationToken = default)
        => ReadOneAsync(viewId, cancellationToken);

    public async Task<StoredSearchViewRevision?> GetRevisionAsync(
        Guid viewId,
        long revision,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StoredSearchView> ReadIncompleteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM search_views
            WHERE is_complete = 0 AND expires_at_utc > $now
            ORDER BY created_at_utc, id;
            """;
        Add(command, "$now", clock.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadView(reader);
    }

    public Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
        => WithWriteAsync(async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM search_views WHERE expires_at_utc <= $now;";
            Add(command, "$now", clock.GetUtcNow());
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<StoredSearchView> PublishAsync(
        Guid viewId,
        SearchViewKernelUpdate update,
        string retentionState,
        CancellationToken cancellationToken = default)
        => WithWriteAsync(async (connection, transaction, ct) =>
        {
            StoredSearchView current = await ReadOneAsync(
                connection,
                transaction,
                viewId,
                ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The search view was not found.");
            if (update.SourceRevision < current.SourceRevision
                || update.ConsumedSequence < current.ConsumedSequence)
                throw new InvalidOperationException("A search view publication cannot regress.");

            foreach (string username in (update.ObservedInputs ?? [])
                .Select(input => input.Username)
                .Distinct(StringComparer.Ordinal))
            {
                await using var peer = connection.CreateCommand();
                peer.Transaction = transaction;
                peer.CommandText = """
                    INSERT OR IGNORE INTO search_view_peers (view_id, username)
                    VALUES ($view, $username);
                    """;
                Add(peer, "$view", viewId);
                Add(peer, "$username", username);
                await peer.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var peerCountCommand = connection.CreateCommand();
            peerCountCommand.Transaction = transaction;
            peerCountCommand.CommandText =
                "SELECT COUNT(*) FROM search_view_peers WHERE view_id = $view;";
            Add(peerCountCommand, "$view", viewId);
            int peerCount = checked(Convert.ToInt32(
                await peerCountCommand.ExecuteScalarAsync(ct).ConfigureAwait(false)));
            SearchViewKernelUpdate effectiveUpdate = update with
            {
                Counters = update.Counters with { ObservedPeerCount = peerCount },
            };

            bool metadataChanged = effectiveUpdate.SourceRevision != current.SourceRevision
                || effectiveUpdate.ConsumedSequence != current.ConsumedSequence
                || effectiveUpdate.IsComplete != current.IsComplete
                || retentionState != current.RetentionState
                || effectiveUpdate.Counters != current.Counters;
            if (!metadataChanged
                && effectiveUpdate.ChangedFiles.Count == 0
                && (effectiveUpdate.ChangedDirectories?.Count ?? 0) == 0
                && (effectiveUpdate.RemovedDirectories?.Count ?? 0) == 0
                && (effectiveUpdate.ChangedAggregateTrackGroups?.Count ?? 0) == 0
                && (effectiveUpdate.ChangedAggregateAlbumGroups?.Count ?? 0) == 0
                && (effectiveUpdate.RemovedAggregateAlbumGroups?.Count ?? 0) == 0)
                return current;

            long revision = checked(current.Revision + 1);
            await InsertRevisionAsync(
                connection, transaction, viewId, revision, effectiveUpdate, retentionState, ct)
                .ConfigureAwait(false);
            var fileRefs = new Dictionary<
                (string Username, string Filename, SearchResultVisibility Visibility),
                string>();
            foreach (ProjectedFileCandidate file in effectiveUpdate.ChangedFiles)
            {
                string itemRef = Guid.NewGuid().ToString("N");
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = FileInsertSql;
                BindFile(insert, viewId, itemRef, revision, file);
                bool inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
                if (!inserted)
                {
                    itemRef = await ReadFileRefAsync(
                        connection,
                        transaction,
                        viewId,
                        file.Input,
                        ct).ConfigureAwait(false);
                }
                fileRefs[(file.Input.Username, file.Input.Filename, file.Input.Visibility)] = itemRef;
            }

            foreach (SearchViewProjectedDirectory directory in
                effectiveUpdate.ChangedDirectories ?? [])
            {
                string directoryRef = await GetOrCreateDirectoryRefAsync(
                    connection,
                    transaction,
                    viewId,
                    directory.Directory,
                    ct).ConfigureAwait(false);
                string bestRef = await ResolveFileRefAsync(
                    connection,
                    transaction,
                    viewId,
                    directory.BestChild.Input,
                    fileRefs,
                    ct).ConfigureAwait(false);
                await InsertDirectoryVersionAsync(
                    connection,
                    transaction,
                    viewId,
                    directoryRef,
                    revision,
                    bestRef,
                    directory,
                    ct).ConfigureAwait(false);
                foreach (ProjectedFileCandidate child in directory.NewChildren)
                {
                    string childRef = await ResolveFileRefAsync(
                        connection,
                        transaction,
                        viewId,
                        child.Input,
                        fileRefs,
                        ct).ConfigureAwait(false);
                    await InsertDirectoryChildAsync(
                        connection,
                        transaction,
                        viewId,
                        directoryRef,
                        childRef,
                        revision,
                        RelativePath(directory.Directory.FolderPath, child.Input.Filename),
                        ct).ConfigureAwait(false);
                }
            }
            foreach (PeerDirectoryIdentity directory in
                effectiveUpdate.RemovedDirectories ?? [])
            {
                string directoryRef = await ReadDirectoryRefAsync(
                    connection,
                    transaction,
                    viewId,
                    directory,
                    ct).ConfigureAwait(false);
                await InsertDirectoryRemovalAsync(
                    connection,
                    transaction,
                    viewId,
                    directoryRef,
                    revision,
                    ct).ConfigureAwait(false);
            }
            foreach (SearchViewProjectedAggregateTrackGroup group in
                effectiveUpdate.ChangedAggregateTrackGroups ?? [])
            {
                string groupRef = await GetOrCreateAggregateTrackRefAsync(
                    connection,
                    transaction,
                    viewId,
                    group,
                    ct).ConfigureAwait(false);
                string representativeRef = await ResolveFileRefAsync(
                    connection,
                    transaction,
                    viewId,
                    group.Representative.Input,
                    fileRefs,
                    ct).ConfigureAwait(false);
                await InsertAggregateTrackVersionAsync(
                    connection,
                    transaction,
                    viewId,
                    groupRef,
                    revision,
                    representativeRef,
                    group,
                    ct).ConfigureAwait(false);
                foreach (ProjectedFileCandidate option in group.NewOptions)
                {
                    string optionRef = await ResolveFileRefAsync(
                        connection,
                        transaction,
                        viewId,
                        option.Input,
                        fileRefs,
                        ct).ConfigureAwait(false);
                    await InsertAggregateTrackOptionAsync(
                        connection,
                        transaction,
                        viewId,
                        groupRef,
                        optionRef,
                        revision,
                        ct).ConfigureAwait(false);
                }
            }
            foreach (SearchViewProjectedAggregateAlbumGroup group in
                effectiveUpdate.ChangedAggregateAlbumGroups ?? [])
            {
                string groupRef = await GetOrCreateAggregateAlbumRefAsync(
                    connection,
                    transaction,
                    viewId,
                    group,
                    ct).ConfigureAwait(false);
                string representativeRef = await ReadDirectoryRefAsync(
                    connection,
                    transaction,
                    viewId,
                    group.Representative.DirectoryIdentity,
                    ct).ConfigureAwait(false);
                await InsertAggregateAlbumVersionAsync(
                    connection,
                    transaction,
                    viewId,
                    groupRef,
                    revision,
                    representativeRef,
                    group,
                    isRemoved: false,
                    ct).ConfigureAwait(false);
                var optionRefs = new HashSet<string>(StringComparer.Ordinal);
                foreach (AlbumFolder option in group.Options)
                {
                    optionRefs.Add(await ReadDirectoryRefAsync(
                        connection,
                        transaction,
                        viewId,
                        option.DirectoryIdentity,
                        ct).ConfigureAwait(false));
                }
                await ReplaceAggregateAlbumOptionsAsync(
                    connection,
                    transaction,
                    viewId,
                    groupRef,
                    revision,
                    optionRefs,
                    ct).ConfigureAwait(false);
            }
            foreach (PeerDirectoryIdentity stableIdentity in
                effectiveUpdate.RemovedAggregateAlbumGroups ?? [])
            {
                string groupRef = await ReadAggregateAlbumRefAsync(
                    connection,
                    transaction,
                    viewId,
                    stableIdentity,
                    ct).ConfigureAwait(false);
                await InsertAggregateAlbumRemovalAsync(
                    connection,
                    transaction,
                    viewId,
                    groupRef,
                    revision,
                    ct).ConfigureAwait(false);
                await ReplaceAggregateAlbumOptionsAsync(
                    connection,
                    transaction,
                    viewId,
                    groupRef,
                    revision,
                    new HashSet<string>(StringComparer.Ordinal),
                    ct).ConfigureAwait(false);
            }
            await UpdateSummaryAsync(
                connection, transaction, viewId, revision, effectiveUpdate, retentionState, ct)
                .ConfigureAwait(false);

            return current with
            {
                Revision = revision,
                SourceRevision = effectiveUpdate.SourceRevision,
                ConsumedSequence = effectiveUpdate.ConsumedSequence,
                IsComplete = effectiveUpdate.IsComplete,
                RetentionState = retentionState,
                Counters = effectiveUpdate.Counters,
            };
        }, cancellationToken);

    public Task<StoredSearchViewDirectoryPublishResult> PublishRetrievedDirectoryAsync(
        Guid viewId,
        string directoryRef,
        PeerDirectorySnapshot snapshot,
        IReadOnlyList<ProjectedFileCandidate> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count != snapshot.Files.Count)
            throw new ArgumentException(
                "Every retrieved directory file needs one projected display row.",
                nameof(files));
        return WithWriteAsync<StoredSearchViewDirectoryPublishResult>(async (connection, transaction, ct) =>
        {
            StoredSearchView current = await ReadOneAsync(
                connection,
                transaction,
                viewId,
                ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The search view was not found.");

            string username;
            string folderPath;
            await using (var identity = connection.CreateCommand())
            {
                identity.Transaction = transaction;
                identity.CommandText = """
                    SELECT username, folder_path
                    FROM search_view_directories
                    WHERE view_id = $view AND item_ref = $directory;
                    """;
                Add(identity, "$view", viewId);
                Add(identity, "$directory", directoryRef);
                await using SqliteDataReader reader = await identity.ExecuteReaderAsync(ct)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new KeyNotFoundException("The search-view directory was not found.");
                username = reader.GetString(0);
                folderPath = reader.GetString(1);
            }
            if (!StringComparer.Ordinal.Equals(username, snapshot.Identity.Username)
                || !StringComparer.Ordinal.Equals(folderPath, snapshot.Identity.FolderPath))
            {
                throw new InvalidOperationException(
                    "The retrieved directory identity does not match the search-view ref.");
            }

            long revision = checked(current.Revision + 1);
            var update = new SearchViewKernelUpdate(
                current.SourceRevision,
                current.ConsumedSequence,
                current.IsComplete,
                current.Counters,
                []);
            await InsertRevisionAsync(
                connection,
                transaction,
                viewId,
                revision,
                update,
                current.RetentionState,
                ct).ConfigureAwait(false);

            int newFileCount = 0;
            foreach (ProjectedFileCandidate file in files)
            {
                string fileRef = Guid.NewGuid().ToString("N");
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = FileInsertSql;
                BindFile(insert, viewId, fileRef, revision, file);
                if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                {
                    newFileCount++;
                }
                else
                {
                    fileRef = await ReadFileRefAsync(
                        connection,
                        transaction,
                        viewId,
                        file.Input,
                        ct).ConfigureAwait(false);
                }
                await InsertDirectoryChildAsync(
                    connection,
                    transaction,
                    viewId,
                    directoryRef,
                    fileRef,
                    revision,
                    RelativePath(folderPath, file.Input.Filename),
                    ct).ConfigureAwait(false);
            }

            long? retrievedBytes = snapshot.Files.All(file => file.Size.HasValue)
                ? snapshot.Files.Sum(file => file.Size!.Value)
                : null;
            await using (var directory = connection.CreateCommand())
            {
                directory.Transaction = transaction;
                directory.CommandText = """
                    INSERT INTO search_view_directory_versions (
                        view_id, item_ref, revision, best_file_ref,
                        public_matching_count, locked_matching_count,
                        public_matching_bytes, locked_matching_bytes,
                        is_fully_retrieved, retrieved_file_count, retrieved_bytes,
                        is_removed)
                    SELECT view_id, item_ref, $revision, best_file_ref,
                        public_matching_count, locked_matching_count,
                        public_matching_bytes, locked_matching_bytes,
                        $complete, $retrieved_count, $retrieved_bytes, 0
                    FROM search_view_directory_versions
                    WHERE view_id = $view AND item_ref = $directory
                      AND is_removed = 0
                    ORDER BY revision DESC LIMIT 1;
                    """;
                Add(directory, "$view", viewId);
                Add(directory, "$directory", directoryRef);
                Add(directory, "$revision", revision);
                Add(directory, "$complete", snapshot.IsComplete);
                Add(directory, "$retrieved_count", snapshot.Files.Count);
                Add(directory, "$retrieved_bytes", retrievedBytes);
                if (await directory.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The search-view directory is no longer available.");
                }
            }
            await UpdateSummaryAsync(
                connection,
                transaction,
                viewId,
                revision,
                update,
                current.RetentionState,
                ct).ConfigureAwait(false);
            return new(
                current with { Revision = revision },
                newFileCount);
        }, cancellationToken);
    }

    public async Task<StoredSearchViewFilePage?> GetFilesAsync(
        Guid viewId,
        long revision,
        SearchViewFilePosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        StoredSearchView? active = await GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (active == null || active.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection, viewId, revision, cancellationToken).ConfigureAwait(false);
        if (bound == null)
            return null;
        await using var command = connection.CreateCommand();
        command.CommandText = FilePageSql(after != null);
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$limit", limit + 1);
        if (after != null)
            BindPosition(command, after);
        var rows = new List<StoredSearchViewFile>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadFile(reader));
        SearchViewFilePosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = Position(rows[^1]);
        }
        return new(bound, rows, next);
    }

    public async Task<StoredSearchViewDirectoryPage?> GetDirectoriesAsync(
        Guid viewId,
        long revision,
        SearchViewDirectoryPosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        StoredSearchView? active = await GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (active == null || active.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (bound == null)
            return null;
        await using var command = connection.CreateCommand();
        command.CommandText = DirectoryPageSql(after != null);
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$limit", limit + 1);
        if (after != null)
            BindDirectoryPosition(command, after);
        var rows = new List<StoredSearchViewDirectory>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadDirectory(reader));
        SearchViewDirectoryPosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = DirectoryPosition(rows[^1]);
        }
        return new(bound, rows, next);
    }

    public async Task<StoredSearchViewDirectoryFilePage?> GetDirectoryFilesAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        SearchViewDirectoryFilePosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        StoredSearchView? active = await GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (active == null || active.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (bound == null)
            return null;

        string? username;
        string? folderPath;
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT username, folder_path FROM search_view_directories
                WHERE view_id = $view AND item_ref = $directory
                  AND COALESCE((
                    SELECT version.is_removed FROM search_view_directory_versions version
                    WHERE version.view_id = $view
                      AND version.item_ref = $directory
                      AND version.revision <= $revision
                    ORDER BY version.revision DESC LIMIT 1), 1) = 0;
                """;
            Add(identity, "$view", viewId);
            Add(identity, "$directory", directoryRef);
            Add(identity, "$revision", revision);
            await using SqliteDataReader identityReader = await identity.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            if (!await identityReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            username = identityReader.GetString(0);
            folderPath = identityReader.GetString(1);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mapping.relative_path AS child_relative_path, file.*
            FROM search_view_directory_files mapping
            JOIN search_view_files file
              ON file.view_id = mapping.view_id AND file.item_ref = mapping.file_ref
            WHERE mapping.view_id = $view AND mapping.directory_ref = $directory
              AND mapping.admitted_revision <= $revision
              AND (mapping.removed_revision IS NULL
                   OR mapping.removed_revision > $revision)
              AND ($cursor_relative IS NULL
                   OR mapping.relative_path > $cursor_relative
                   OR mapping.relative_path = $cursor_relative AND mapping.file_ref > $cursor_ref)
            ORDER BY mapping.relative_path COLLATE BINARY, mapping.file_ref
            LIMIT $limit;
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        Add(command, "$revision", revision);
        Add(command, "$cursor_relative", after?.RelativePath);
        Add(command, "$cursor_ref", after?.Ref);
        Add(command, "$limit", limit + 1);
        var rows = new List<StoredSearchViewDirectoryFile>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredSearchViewFile file = ReadFile(reader);
            rows.Add(new(file.Ref, Value<string>(reader, "child_relative_path"), file));
        }
        SearchViewDirectoryFilePosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = new(rows[^1].RelativePath, rows[^1].Ref);
        }
        return new(bound, directoryRef, username, folderPath, rows, next);
    }

    public async Task<StoredSearchViewAggregateTrackPage?> GetAggregateTracksAsync(
        Guid viewId,
        long revision,
        SearchViewAggregateTrackPosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        StoredSearchView? active = await GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (active == null || active.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (bound == null)
            return null;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest AS (
                SELECT item_ref, MAX(revision) AS revision
                FROM search_view_aggregate_track_versions
                WHERE view_id = $view AND revision <= $revision
                GROUP BY item_ref)
            SELECT aggregate.item_ref AS aggregate_ref,
                   aggregate.group_index AS aggregate_index,
                   aggregate.query_json AS aggregate_query_json,
                   version.share_count AS aggregate_share_count,
                   version.selectable_option_count AS aggregate_option_count,
                   file.*
            FROM latest
            JOIN search_view_aggregate_track_versions version
              ON version.view_id = $view
             AND version.item_ref = latest.item_ref
             AND version.revision = latest.revision
            JOIN search_view_aggregate_tracks aggregate
              ON aggregate.view_id = version.view_id
             AND aggregate.item_ref = version.item_ref
            JOIN search_view_files file
              ON file.view_id = version.view_id
             AND file.item_ref = version.representative_file_ref
            WHERE ($cursor_shares IS NULL
                   OR version.share_count < $cursor_shares
                   OR version.share_count = $cursor_shares
                      AND aggregate.group_index > $cursor_index
                   OR version.share_count = $cursor_shares
                      AND aggregate.group_index = $cursor_index
                      AND aggregate.item_ref > $cursor_ref)
            ORDER BY version.share_count DESC, aggregate.group_index, aggregate.item_ref
            LIMIT $limit;
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$cursor_shares", after?.ShareCount);
        Add(command, "$cursor_index", after?.Index);
        Add(command, "$cursor_ref", after?.Ref);
        Add(command, "$limit", limit + 1);
        var rows = new List<StoredSearchViewAggregateTrackGroup>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadAggregateTrack(reader));
        SearchViewAggregateTrackPosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            StoredSearchViewAggregateTrackGroup last = rows[^1];
            next = new(last.ShareCount, last.Index, last.Ref);
        }
        return new(bound, rows, next);
    }

    public async Task<StoredSearchViewAggregateTrackOptionPage?> GetAggregateTrackOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewFilePosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        StoredSearchView? active = await GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (active == null || active.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (bound == null)
            return null;
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = """
                SELECT COUNT(*) FROM search_view_aggregate_tracks aggregate
                WHERE aggregate.view_id = $view AND aggregate.item_ref = $group
                  AND EXISTS (
                    SELECT 1 FROM search_view_aggregate_track_versions version
                    WHERE version.view_id = aggregate.view_id
                      AND version.item_ref = aggregate.item_ref
                      AND version.revision <= $revision);
                """;
            Add(exists, "$view", viewId);
            Add(exists, "$group", groupRef);
            Add(exists, "$revision", revision);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)) != 1)
                return null;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = AggregateTrackOptionsSql(after != null);
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        Add(command, "$limit", limit + 1);
        if (after != null)
            BindPosition(command, after);
        var rows = new List<StoredSearchViewFile>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadFile(reader));
        SearchViewFilePosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = Position(rows[^1]);
        }
        return new(bound, groupRef, rows, next);
    }

    public async IAsyncEnumerable<StoredSearchViewFile> ReadPublicAggregateTrackOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file.*
            FROM search_view_aggregate_track_files mapping
            JOIN search_view_files file
              ON file.view_id = mapping.view_id AND file.item_ref = mapping.file_ref
            WHERE mapping.view_id = $view AND mapping.group_ref = $group
              AND mapping.admitted_revision <= $revision
              AND file.visibility = 'Public'
            ORDER BY file.sort_high DESC, file.sort_upload_fast DESC,
                     file.sort_mid DESC, file.sort_inferred DESC,
                     file.sort_upload_medium DESC, file.sort_bitrate DESC,
                     file.sort_tie DESC, file.sequence, file.item_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadFile(reader);
    }

    public async Task<StoredSearchViewAggregateAlbumPage?> GetAggregateAlbumsAsync(
        Guid viewId,
        long revision,
        SearchViewAggregateAlbumPosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        StoredSearchView? active = await GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (active == null || active.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (bound == null)
            return null;
        await using var command = connection.CreateCommand();
        command.CommandText = AggregateAlbumPageSql;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$cursor_shares", after?.ShareCount);
        Add(command, "$cursor_index", after?.Index);
        Add(command, "$cursor_ref", after?.Ref);
        Add(command, "$limit", limit + 1);
        var rows = new List<StoredSearchViewAggregateAlbumGroup>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadAggregateAlbum(reader));
        SearchViewAggregateAlbumPosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            StoredSearchViewAggregateAlbumGroup last = rows[^1];
            next = new(last.ShareCount, last.Index, last.Ref);
        }
        return new(bound, rows, next);
    }

    public async Task<StoredSearchViewAggregateAlbumOptionPage?> GetAggregateAlbumOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        SearchViewDirectoryPosition? after,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        StoredSearchView? active = await GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (active == null || active.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredSearchViewRevision? bound = await ReadRevisionAsync(
            connection,
            viewId,
            revision,
            cancellationToken).ConfigureAwait(false);
        if (bound == null)
            return null;
        await using var command = connection.CreateCommand();
        command.CommandText = AggregateAlbumOptionsSql(after != null);
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        Add(command, "$limit", limit + 1);
        if (after != null)
            BindDirectoryPosition(command, after);
        var rows = new List<StoredSearchViewDirectory>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadDirectory(reader));
        await reader.DisposeAsync().ConfigureAwait(false);
        if (rows.Count == 0)
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = """
                SELECT COUNT(*) FROM search_view_aggregate_album_versions
                WHERE view_id = $view AND item_ref = $group
                  AND revision <= $revision;
                """;
            Add(exists, "$view", viewId);
            Add(exists, "$group", groupRef);
            Add(exists, "$revision", revision);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)) == 0)
                return null;
        }
        SearchViewDirectoryPosition? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = DirectoryPosition(rows[^1]);
        }
        return new(bound, groupRef, rows, next);
    }

    public async IAsyncEnumerable<StoredSearchViewDirectory> ReadPublicAggregateAlbumOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest_membership AS (
                SELECT directory_ref, MAX(revision) AS revision
                FROM search_view_aggregate_album_directory_versions
                WHERE view_id = $view AND group_ref = $group
                  AND revision <= $revision
                GROUP BY directory_ref),
            latest_directory AS (
                SELECT item_ref, MAX(revision) AS revision
                FROM search_view_directory_versions
                WHERE view_id = $view AND revision <= $revision
                GROUP BY item_ref)
            SELECT directory.item_ref AS directory_ref,
                   directory.username AS directory_username,
                   directory.folder_path AS directory_folder_path,
                   directory_version.public_matching_count,
                   directory_version.locked_matching_count,
                   directory_version.public_matching_bytes,
                   directory_version.locked_matching_bytes,
                   directory_version.is_fully_retrieved,
                   directory_version.retrieved_file_count,
                   directory_version.retrieved_bytes,
                   file.*
            FROM latest_membership
            JOIN search_view_aggregate_album_directory_versions membership
              ON membership.view_id = $view AND membership.group_ref = $group
             AND membership.directory_ref = latest_membership.directory_ref
             AND membership.revision = latest_membership.revision
            JOIN latest_directory
              ON latest_directory.item_ref = membership.directory_ref
            JOIN search_view_directory_versions directory_version
              ON directory_version.view_id = $view
             AND directory_version.item_ref = latest_directory.item_ref
             AND directory_version.revision = latest_directory.revision
            JOIN search_view_directories directory
              ON directory.view_id = directory_version.view_id
             AND directory.item_ref = directory_version.item_ref
            JOIN search_view_files file
              ON file.view_id = directory_version.view_id
             AND file.item_ref = directory_version.best_file_ref
            WHERE membership.is_present = 1
              AND directory_version.is_removed = 0
              AND directory_version.public_matching_count > 0
            ORDER BY file.sort_high DESC, file.sort_upload_fast DESC,
                     file.sort_mid DESC, file.sort_inferred DESC,
                     file.sort_upload_medium DESC, file.sort_bitrate DESC,
                     file.sort_tie DESC, file.sequence,
                     directory.username COLLATE BINARY,
                     directory.folder_path COLLATE BINARY,
                     directory.item_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$revision", revision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return ReadDirectory(reader);
    }

    public async IAsyncEnumerable<StoredSearchViewDirectoryFile> ReadPublicDirectoryFilesAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mapping.relative_path AS child_relative_path, file.*
            FROM search_view_directory_files mapping
            JOIN search_view_files file
              ON file.view_id = mapping.view_id
             AND file.item_ref = mapping.file_ref
            WHERE mapping.view_id = $view AND mapping.directory_ref = $directory
              AND mapping.admitted_revision <= $revision
              AND (mapping.removed_revision IS NULL
                   OR mapping.removed_revision > $revision)
              AND file.visibility = 'Public'
            ORDER BY mapping.relative_path COLLATE BINARY, mapping.file_ref;
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        Add(command, "$revision", revision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredSearchViewFile file = ReadFile(reader);
            yield return new(
                file.Ref,
                Value<string>(reader, "child_relative_path"),
                file);
        }
    }

    public async IAsyncEnumerable<StoredSearchViewCommitItem> ReadCommitItemsAsync(
        Guid viewId,
        long viewRevision,
        string projectionKind,
        string mode,
        IReadOnlySet<string> refs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (mode is not ("Only" or "AllExcept"))
            throw new ArgumentException("Selection mode must be Only or AllExcept.", nameof(mode));
        ArgumentNullException.ThrowIfNull(refs);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (projectionKind == "Files")
        {
            command.CommandText = """
                SELECT file.* FROM search_view_files file
                WHERE file.view_id = $view
                  AND file.admitted_revision <= $revision
                ORDER BY file.item_ref COLLATE BINARY;
                """;
        }
        else if (projectionKind is "GenericDirectories" or "AlbumDirectories")
        {
            command.CommandText = """
                WITH latest AS (
                    SELECT item_ref, MAX(revision) AS revision
                    FROM search_view_directory_versions
                    WHERE view_id = $view AND revision <= $revision
                    GROUP BY item_ref)
                SELECT directory.item_ref AS directory_ref,
                       directory.username AS directory_username,
                       directory.folder_path AS directory_folder_path,
                       version.public_matching_count,
                       version.locked_matching_count,
                       version.public_matching_bytes,
                       version.locked_matching_bytes,
                       version.is_fully_retrieved,
                       version.retrieved_file_count,
                       version.retrieved_bytes,
                       file.*
                FROM latest
                JOIN search_view_directory_versions version
                  ON version.view_id = $view
                 AND version.item_ref = latest.item_ref
                 AND version.revision = latest.revision
                JOIN search_view_directories directory
                  ON directory.view_id = version.view_id
                 AND directory.item_ref = version.item_ref
                JOIN search_view_files file
                  ON file.view_id = version.view_id
                 AND file.item_ref = version.best_file_ref
                WHERE version.is_removed = 0
                ORDER BY directory.item_ref COLLATE BINARY;
                """;
        }
        else if (projectionKind == "AggregateTracks")
        {
            command.CommandText = """
                WITH latest AS (
                    SELECT item_ref, MAX(revision) AS revision
                    FROM search_view_aggregate_track_versions
                    WHERE view_id = $view AND revision <= $revision
                    GROUP BY item_ref)
                SELECT aggregate.item_ref AS aggregate_ref,
                       aggregate.group_index AS aggregate_index,
                       aggregate.query_json AS aggregate_query_json,
                       version.share_count AS aggregate_share_count,
                       version.selectable_option_count AS aggregate_option_count,
                       file.*
                FROM latest
                JOIN search_view_aggregate_track_versions version
                  ON version.view_id = $view
                 AND version.item_ref = latest.item_ref
                 AND version.revision = latest.revision
                JOIN search_view_aggregate_tracks aggregate
                  ON aggregate.view_id = version.view_id
                 AND aggregate.item_ref = version.item_ref
                JOIN search_view_files file
                  ON file.view_id = version.view_id
                 AND file.item_ref = version.representative_file_ref
                ORDER BY aggregate.item_ref COLLATE BINARY;
                """;
        }
        else if (projectionKind == "AggregateAlbums")
        {
            command.CommandText = """
                WITH latest_group AS (
                    SELECT item_ref, MAX(revision) AS revision
                    FROM search_view_aggregate_album_versions
                    WHERE view_id = $view AND revision <= $revision
                    GROUP BY item_ref),
                latest_directory AS (
                    SELECT item_ref, MAX(revision) AS revision
                    FROM search_view_directory_versions
                    WHERE view_id = $view AND revision <= $revision
                    GROUP BY item_ref)
                SELECT aggregate.item_ref AS aggregate_ref,
                       group_version.group_index AS aggregate_index,
                       aggregate.query_json AS aggregate_query_json,
                       group_version.share_count AS aggregate_share_count,
                       group_version.selectable_option_count AS aggregate_option_count,
                       directory.item_ref AS directory_ref,
                       directory.username AS directory_username,
                       directory.folder_path AS directory_folder_path,
                       directory_version.public_matching_count,
                       directory_version.locked_matching_count,
                       directory_version.public_matching_bytes,
                       directory_version.locked_matching_bytes,
                       directory_version.is_fully_retrieved,
                       directory_version.retrieved_file_count,
                       directory_version.retrieved_bytes,
                       file.*
                FROM latest_group
                JOIN search_view_aggregate_album_versions group_version
                  ON group_version.view_id = $view
                 AND group_version.item_ref = latest_group.item_ref
                 AND group_version.revision = latest_group.revision
                JOIN search_view_aggregate_albums aggregate
                  ON aggregate.view_id = group_version.view_id
                 AND aggregate.item_ref = group_version.item_ref
                JOIN latest_directory
                  ON latest_directory.item_ref = group_version.representative_directory_ref
                JOIN search_view_directory_versions directory_version
                  ON directory_version.view_id = $view
                 AND directory_version.item_ref = latest_directory.item_ref
                 AND directory_version.revision = latest_directory.revision
                JOIN search_view_directories directory
                  ON directory.view_id = directory_version.view_id
                 AND directory.item_ref = directory_version.item_ref
                JOIN search_view_files file
                  ON file.view_id = directory_version.view_id
                 AND file.item_ref = directory_version.best_file_ref
                WHERE group_version.is_removed = 0
                  AND directory_version.is_removed = 0
                ORDER BY aggregate.item_ref COLLATE BINARY;
                """;
        }
        else
        {
            throw new InvalidOperationException(
                "Selection is not implemented for this search-view projection.");
        }
        Add(command, "$view", viewId);
        Add(command, "$revision", viewRevision);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredSearchViewCommitItem item;
            if (projectionKind == "Files")
            {
                StoredSearchViewFile file = ReadFile(reader);
                item = new("File", file.Ref, file, null);
            }
            else if (projectionKind is "GenericDirectories" or "AlbumDirectories")
            {
                StoredSearchViewDirectory directory = ReadDirectory(reader);
                item = new("Directory", directory.Ref, null, directory);
            }
            else if (projectionKind == "AggregateTracks")
            {
                StoredSearchViewAggregateTrackGroup aggregate = ReadAggregateTrack(reader);
                item = new(
                    "AggregateTrack",
                    aggregate.Ref,
                    null,
                    null,
                    aggregate);
            }
            else
            {
                StoredSearchViewAggregateAlbumGroup aggregate = ReadAggregateAlbum(reader);
                item = new(
                    "AggregateAlbum",
                    aggregate.Ref,
                    null,
                    null,
                    null,
                    aggregate);
            }
            bool include = mode == "Only" ? refs.Contains(item.Ref) : !refs.Contains(item.Ref);
            if (include)
                yield return item;
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        if (mode == "Only"
            && projectionKind is "GenericDirectories" or "AlbumDirectories")
        {
            await using var children = connection.CreateCommand();
            children.CommandText = """
                SELECT mapping.directory_ref AS parent_directory_ref, file.*
                FROM search_view_directory_files mapping
                JOIN search_view_files file
                  ON file.view_id = mapping.view_id
                 AND file.item_ref = mapping.file_ref
                WHERE mapping.view_id = $view
                  AND mapping.admitted_revision <= $revision
                  AND (mapping.removed_revision IS NULL
                       OR mapping.removed_revision > $revision)
                ORDER BY mapping.file_ref COLLATE BINARY,
                         mapping.directory_ref COLLATE BINARY;
                """;
            Add(children, "$view", viewId);
            Add(children, "$revision", viewRevision);
            await using SqliteDataReader childReader = await children.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            string? lastRef = null;
            while (await childReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                StoredSearchViewFile file = ReadFile(childReader);
                if (StringComparer.Ordinal.Equals(lastRef, file.Ref))
                    continue;
                lastRef = file.Ref;
                if (!refs.Contains(file.Ref))
                    continue;
                yield return new StoredSearchViewCommitItem(
                    "DirectoryFile",
                    file.Ref,
                    file,
                    null,
                    ParentRef: Value<string>(childReader, "parent_directory_ref"));
            }
        }

        if (mode == "Only" && projectionKind == "AggregateTracks")
        {
            await using var options = connection.CreateCommand();
            options.CommandText = """
                SELECT mapping.group_ref AS parent_group_ref, file.*
                FROM search_view_aggregate_track_files mapping
                JOIN search_view_files file
                  ON file.view_id = mapping.view_id
                 AND file.item_ref = mapping.file_ref
                WHERE mapping.view_id = $view
                  AND mapping.admitted_revision <= $revision
                  AND EXISTS (
                    SELECT 1 FROM search_view_aggregate_track_versions version
                    WHERE version.view_id = mapping.view_id
                      AND version.item_ref = mapping.group_ref
                      AND version.revision <= $revision)
                ORDER BY mapping.file_ref COLLATE BINARY,
                         mapping.group_ref COLLATE BINARY;
                """;
            Add(options, "$view", viewId);
            Add(options, "$revision", viewRevision);
            await using SqliteDataReader optionReader = await options.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            StoredSearchViewFile? selectedFile = null;
            var parentGroups = new List<string>();
            while (await optionReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                StoredSearchViewFile file = ReadFile(optionReader);
                if (selectedFile != null
                    && !StringComparer.Ordinal.Equals(selectedFile.Ref, file.Ref))
                {
                    if (refs.Contains(selectedFile.Ref))
                    {
                        yield return new StoredSearchViewCommitItem(
                            "AggregateTrackFile",
                            selectedFile.Ref,
                            selectedFile,
                            null,
                            ContainerRefs: parentGroups.ToArray());
                    }
                    parentGroups.Clear();
                }
                selectedFile = file;
                parentGroups.Add(Value<string>(optionReader, "parent_group_ref"));
            }
            if (selectedFile != null && refs.Contains(selectedFile.Ref))
            {
                yield return new StoredSearchViewCommitItem(
                    "AggregateTrackFile",
                    selectedFile.Ref,
                    selectedFile,
                    null,
                    ContainerRefs: parentGroups.ToArray());
            }
        }

        if (mode == "Only" && projectionKind == "AggregateAlbums")
        {
            await using (var options = connection.CreateCommand())
            {
                options.CommandText = """
                    WITH latest_group AS (
                        SELECT item_ref, MAX(revision) AS revision
                        FROM search_view_aggregate_album_versions
                        WHERE view_id = $view AND revision <= $revision
                        GROUP BY item_ref),
                    latest_membership AS (
                        SELECT group_ref, directory_ref, MAX(revision) AS revision
                        FROM search_view_aggregate_album_directory_versions
                        WHERE view_id = $view AND revision <= $revision
                        GROUP BY group_ref, directory_ref),
                    latest_directory AS (
                        SELECT item_ref, MAX(revision) AS revision
                        FROM search_view_directory_versions
                        WHERE view_id = $view AND revision <= $revision
                        GROUP BY item_ref)
                    SELECT membership.group_ref AS parent_group_ref,
                           directory.item_ref AS directory_ref,
                           directory.username AS directory_username,
                           directory.folder_path AS directory_folder_path,
                           directory_version.public_matching_count,
                           directory_version.locked_matching_count,
                           directory_version.public_matching_bytes,
                           directory_version.locked_matching_bytes,
                           directory_version.is_fully_retrieved,
                           directory_version.retrieved_file_count,
                           directory_version.retrieved_bytes,
                           file.*
                    FROM latest_membership
                    JOIN search_view_aggregate_album_directory_versions membership
                      ON membership.view_id = $view
                     AND membership.group_ref = latest_membership.group_ref
                     AND membership.directory_ref = latest_membership.directory_ref
                     AND membership.revision = latest_membership.revision
                    JOIN latest_group
                      ON latest_group.item_ref = membership.group_ref
                    JOIN search_view_aggregate_album_versions group_version
                      ON group_version.view_id = $view
                     AND group_version.item_ref = latest_group.item_ref
                     AND group_version.revision = latest_group.revision
                    JOIN latest_directory
                      ON latest_directory.item_ref = membership.directory_ref
                    JOIN search_view_directory_versions directory_version
                      ON directory_version.view_id = $view
                     AND directory_version.item_ref = latest_directory.item_ref
                     AND directory_version.revision = latest_directory.revision
                    JOIN search_view_directories directory
                      ON directory.view_id = directory_version.view_id
                     AND directory.item_ref = directory_version.item_ref
                    JOIN search_view_files file
                      ON file.view_id = directory_version.view_id
                     AND file.item_ref = directory_version.best_file_ref
                    WHERE membership.is_present = 1
                      AND group_version.is_removed = 0
                      AND directory_version.is_removed = 0
                    ORDER BY directory.item_ref COLLATE BINARY,
                             membership.group_ref COLLATE BINARY;
                    """;
                Add(options, "$view", viewId);
                Add(options, "$revision", viewRevision);
                await using SqliteDataReader optionReader = await options.ExecuteReaderAsync(
                    cancellationToken).ConfigureAwait(false);
                StoredSearchViewDirectory? selectedDirectory = null;
                var parentGroups = new List<string>();
                while (await optionReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    StoredSearchViewDirectory directory = ReadDirectory(optionReader);
                    if (selectedDirectory != null
                        && !StringComparer.Ordinal.Equals(selectedDirectory.Ref, directory.Ref))
                    {
                        if (refs.Contains(selectedDirectory.Ref))
                        {
                            yield return new StoredSearchViewCommitItem(
                                "AggregateAlbumDirectory",
                                selectedDirectory.Ref,
                                null,
                                selectedDirectory,
                                ContainerRefs: parentGroups.ToArray());
                        }
                        parentGroups.Clear();
                    }
                    selectedDirectory = directory;
                    parentGroups.Add(Value<string>(optionReader, "parent_group_ref"));
                }
                if (selectedDirectory != null && refs.Contains(selectedDirectory.Ref))
                {
                    yield return new StoredSearchViewCommitItem(
                        "AggregateAlbumDirectory",
                        selectedDirectory.Ref,
                        null,
                        selectedDirectory,
                        ContainerRefs: parentGroups.ToArray());
                }
            }

            await using var children = connection.CreateCommand();
            children.CommandText = """
                WITH latest_group AS (
                    SELECT item_ref, MAX(revision) AS revision
                    FROM search_view_aggregate_album_versions
                    WHERE view_id = $view AND revision <= $revision
                    GROUP BY item_ref),
                latest_membership AS (
                    SELECT group_ref, directory_ref, MAX(revision) AS revision
                    FROM search_view_aggregate_album_directory_versions
                    WHERE view_id = $view AND revision <= $revision
                    GROUP BY group_ref, directory_ref),
                latest_directory AS (
                    SELECT item_ref, MAX(revision) AS revision
                    FROM search_view_directory_versions
                    WHERE view_id = $view AND revision <= $revision
                    GROUP BY item_ref)
                SELECT membership.group_ref AS parent_group_ref,
                       mapping.directory_ref AS parent_directory_ref,
                       file.*
                FROM latest_membership
                JOIN search_view_aggregate_album_directory_versions membership
                  ON membership.view_id = $view
                 AND membership.group_ref = latest_membership.group_ref
                 AND membership.directory_ref = latest_membership.directory_ref
                 AND membership.revision = latest_membership.revision
                JOIN latest_group
                  ON latest_group.item_ref = membership.group_ref
                JOIN search_view_aggregate_album_versions group_version
                  ON group_version.view_id = $view
                 AND group_version.item_ref = latest_group.item_ref
                 AND group_version.revision = latest_group.revision
                JOIN latest_directory
                  ON latest_directory.item_ref = membership.directory_ref
                JOIN search_view_directory_versions directory_version
                  ON directory_version.view_id = $view
                 AND directory_version.item_ref = latest_directory.item_ref
                 AND directory_version.revision = latest_directory.revision
                JOIN search_view_directory_files mapping
                  ON mapping.view_id = $view
                 AND mapping.directory_ref = membership.directory_ref
                 AND mapping.admitted_revision <= $revision
                 AND (mapping.removed_revision IS NULL
                      OR mapping.removed_revision > $revision)
                JOIN search_view_files file
                  ON file.view_id = mapping.view_id
                 AND file.item_ref = mapping.file_ref
                WHERE membership.is_present = 1
                  AND group_version.is_removed = 0
                  AND directory_version.is_removed = 0
                ORDER BY mapping.file_ref COLLATE BINARY,
                         mapping.directory_ref COLLATE BINARY,
                         membership.group_ref COLLATE BINARY;
                """;
            Add(children, "$view", viewId);
            Add(children, "$revision", viewRevision);
            await using SqliteDataReader childReader = await children.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            StoredSearchViewFile? selectedChild = null;
            var containerRefs = new HashSet<string>(StringComparer.Ordinal);
            while (await childReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                StoredSearchViewFile file = ReadFile(childReader);
                if (selectedChild != null
                    && !StringComparer.Ordinal.Equals(selectedChild.Ref, file.Ref))
                {
                    if (refs.Contains(selectedChild.Ref))
                    {
                        yield return new StoredSearchViewCommitItem(
                            "AggregateAlbumFile",
                            selectedChild.Ref,
                            selectedChild,
                            null,
                            ContainerRefs: containerRefs.ToArray());
                    }
                    containerRefs.Clear();
                }
                selectedChild = file;
                containerRefs.Add(Value<string>(childReader, "parent_group_ref"));
                containerRefs.Add(Value<string>(childReader, "parent_directory_ref"));
            }
            if (selectedChild != null && refs.Contains(selectedChild.Ref))
            {
                yield return new StoredSearchViewCommitItem(
                    "AggregateAlbumFile",
                    selectedChild.Ref,
                    selectedChild,
                    null,
                    ContainerRefs: containerRefs.ToArray());
            }
        }
    }

    private async Task<StoredSearchView?> ReadOneAsync(
        Guid viewId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOneAsync(connection, null, viewId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredSearchView?> ReadOneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid viewId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM search_views WHERE id = $id;";
        Add(command, "$id", viewId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadView(reader)
            : null;
    }

    private static async Task<StoredSearchViewRevision?> ReadRevisionAsync(
        SqliteConnection connection,
        Guid viewId,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM search_view_revisions
            WHERE view_id = $view AND revision = $revision;
            """;
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRevision(reader)
            : null;
    }

    private static async Task InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        long revision,
        SearchViewKernelUpdate update,
        string retentionState,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_view_revisions (
                view_id, revision, source_revision, consumed_sequence,
                is_complete, retention_state, public_file_count,
                locked_file_count, public_bytes, locked_bytes,
                observed_peer_count, projected_file_count,
                projected_public_file_count, projected_locked_file_count,
                preferred_file_count, other_file_count,
                top_level_item_count, selectable_option_count)
            VALUES (
                $view, $revision, $source_revision, $sequence,
                $complete, $retention, $public_count, $locked_count,
                $public_bytes, $locked_bytes, $peers, $projected,
                $projected_public, $projected_locked, $preferred, $other,
                $top_level, $selectable);
            """;
        BindRevision(command, viewId, revision, update, retentionState);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        long revision,
        SearchViewKernelUpdate update,
        string retentionState,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE search_views SET
                revision = $revision, source_revision = $source_revision,
                consumed_sequence = $sequence, is_complete = $complete,
                retention_state = $retention,
                public_file_count = $public_count,
                locked_file_count = $locked_count,
                public_bytes = $public_bytes, locked_bytes = $locked_bytes,
                observed_peer_count = $peers,
                projected_file_count = $projected,
                projected_public_file_count = $projected_public,
                projected_locked_file_count = $projected_locked,
                preferred_file_count = $preferred,
                other_file_count = $other,
                top_level_item_count = $top_level,
                selectable_option_count = $selectable
            WHERE id = $view;
            """;
        BindRevision(command, viewId, revision, update, retentionState);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindRevision(
        SqliteCommand command,
        Guid viewId,
        long revision,
        SearchViewKernelUpdate update,
        string retentionState)
    {
        Add(command, "$view", viewId);
        Add(command, "$revision", revision);
        Add(command, "$source_revision", update.SourceRevision);
        Add(command, "$sequence", update.ConsumedSequence);
        Add(command, "$complete", update.IsComplete);
        Add(command, "$retention", retentionState);
        AddCounters(command, update.Counters);
    }

    private static void AddCounters(SqliteCommand command, SearchViewCounters counters)
    {
        Add(command, "$public_count", counters.PublicFileCount);
        Add(command, "$locked_count", counters.LockedFileCount);
        Add(command, "$public_bytes", counters.PublicBytes);
        Add(command, "$locked_bytes", counters.LockedBytes);
        Add(command, "$peers", counters.ObservedPeerCount);
        Add(command, "$projected", counters.ProjectedFileCount);
        Add(command, "$projected_public", counters.ProjectedPublicFileCount);
        Add(command, "$projected_locked", counters.ProjectedLockedFileCount);
        Add(command, "$preferred", counters.PreferredFileCount);
        Add(command, "$other", counters.OtherFileCount);
        Add(command, "$top_level", counters.TopLevelItemCount);
        Add(command, "$selectable", counters.SelectableOptionCount);
    }

    private static void BindFile(
        SqliteCommand command,
        Guid viewId,
        string itemRef,
        long revision,
        ProjectedFileCandidate file)
    {
        SearchProjectionInput input = file.Input;
        Add(command, "$view", viewId);
        Add(command, "$ref", itemRef);
        Add(command, "$admitted", revision);
        Add(command, "$sequence", input.Sequence);
        Add(command, "$source_row_revision", input.Revision);
        Add(command, "$username", input.Username);
        Add(command, "$response_count", input.ResponseFileCount);
        Add(command, "$filename", input.Filename);
        Add(command, "$size", input.Size);
        Add(command, "$bitrate", input.BitRate);
        Add(command, "$bitdepth", input.BitDepth);
        Add(command, "$sample_rate", input.SampleRate);
        Add(command, "$length", input.Length);
        Add(command, "$extension", input.Extension);
        Add(command, "$upload_speed", input.UploadSpeed);
        Add(command, "$free_slot", input.HasFreeUploadSlot);
        Add(command, "$queue_length", input.QueueLength);
        Add(command, "$attributes", input.Attributes == null
            ? null
            : JsonSerializer.Serialize(input.Attributes));
        Add(command, "$observed", input.ObservedAtUtc);
        Add(command, "$visibility", input.Visibility.ToString());
        Add(command, "$tier", file.ConditionFacts.PreferenceTier.ToString());
        Add(command, "$necessary_satisfied", file.ConditionFacts.NecessaryConditionsSatisfied);
        Add(command, "$conditions", JsonSerializer.Serialize(
            file.ConditionFacts.SatisfiedPreferredConditions ?? []));
        Add(command, "$configured_conditions", JsonSerializer.Serialize(
            file.ConditionFacts.ConfiguredPreferredConditions ?? []));
        Add(command, "$high", (long)file.SortKey.HighFlags);
        Add(command, "$fast", file.SortKey.UploadSpeedFast);
        Add(command, "$mid", (long)file.SortKey.MidFlags);
        Add(command, "$inferred", file.SortKey.InferredTrackCount);
        Add(command, "$medium", file.SortKey.UploadSpeedMedium);
        Add(command, "$sort_bitrate", file.SortKey.BitRate);
        Add(command, "$tie", file.SortKey.StableTieBreaker);
    }

    private static async Task<string> ReadFileRefAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        SearchProjectionInput input,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_ref FROM search_view_files
            WHERE view_id = $view AND username = $username
              AND filename = $filename AND visibility = $visibility;
            """;
        Add(command, "$view", viewId);
        Add(command, "$username", input.Username);
        Add(command, "$filename", input.Filename);
        Add(command, "$visibility", input.Visibility.ToString());
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))
            ?? throw new InvalidDataException("A projected search-view file reference is missing.");
    }

    private static async Task<string> ResolveFileRefAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        SearchProjectionInput input,
        IReadOnlyDictionary<
            (string Username, string Filename, SearchResultVisibility Visibility),
            string> fileRefs,
        CancellationToken cancellationToken)
        => fileRefs.TryGetValue((input.Username, input.Filename, input.Visibility), out string? itemRef)
            ? itemRef
            : await ReadFileRefAsync(
                connection,
                transaction,
                viewId,
                input,
                cancellationToken).ConfigureAwait(false);

    private static async Task<string> GetOrCreateDirectoryRefAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken)
    {
        string itemRef = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO search_view_directories (
                    view_id, item_ref, username, folder_path)
                VALUES ($view, $ref, $username, $path);
                """;
            Add(insert, "$view", viewId);
            Add(insert, "$ref", itemRef);
            Add(insert, "$username", directory.Username);
            Add(insert, "$path", directory.FolderPath);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                return itemRef;
        }
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT item_ref FROM search_view_directories
            WHERE view_id = $view AND username = $username AND folder_path = $path;
            """;
        Add(read, "$view", viewId);
        Add(read, "$username", directory.Username);
        Add(read, "$path", directory.FolderPath);
        return Convert.ToString(await read.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))
            ?? throw new InvalidDataException("A projected directory reference is missing.");
    }

    private static async Task<string> ReadDirectoryRefAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_ref FROM search_view_directories
            WHERE view_id = $view AND username = $username AND folder_path = $path;
            """;
        Add(command, "$view", viewId);
        Add(command, "$username", directory.Username);
        Add(command, "$path", directory.FolderPath);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))
            ?? throw new InvalidDataException(
                "A removed projected directory has no retained identity.");
    }

    private static async Task InsertDirectoryVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string directoryRef,
        long revision,
        string bestFileRef,
        SearchViewProjectedDirectory directory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_view_directory_versions (
                view_id, item_ref, revision, best_file_ref,
                public_matching_count, locked_matching_count,
                public_matching_bytes, locked_matching_bytes,
                is_fully_retrieved, retrieved_file_count, retrieved_bytes,
                is_removed)
            VALUES (
                $view, $ref, $revision, $best,
                $public_count, $locked_count, $public_bytes, $locked_bytes,
                $retrieved, $retrieved_count, $retrieved_bytes, 0);
            """;
        Add(command, "$view", viewId);
        Add(command, "$ref", directoryRef);
        Add(command, "$revision", revision);
        Add(command, "$best", bestFileRef);
        Add(command, "$public_count", directory.PublicMatchingFileCount);
        Add(command, "$locked_count", directory.LockedMatchingFileCount);
        Add(command, "$public_bytes", directory.PublicMatchingBytes);
        Add(command, "$locked_bytes", directory.LockedMatchingBytes);
        Add(command, "$retrieved", directory.IsFullyRetrieved);
        Add(command, "$retrieved_count", directory.RetrievedFileCount);
        Add(command, "$retrieved_bytes", directory.RetrievedBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertDirectoryRemovalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string directoryRef,
        long revision,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO search_view_directory_versions (
                    view_id, item_ref, revision, best_file_ref,
                    public_matching_count, locked_matching_count,
                    public_matching_bytes, locked_matching_bytes,
                    is_fully_retrieved, retrieved_file_count, retrieved_bytes,
                    is_removed)
                SELECT view_id, item_ref, $revision, best_file_ref,
                    public_matching_count, locked_matching_count,
                    public_matching_bytes, locked_matching_bytes,
                    is_fully_retrieved, retrieved_file_count, retrieved_bytes, 1
                FROM search_view_directory_versions
                WHERE view_id = $view AND item_ref = $ref
                ORDER BY revision DESC LIMIT 1;
                """;
            Add(command, "$view", viewId);
            Add(command, "$ref", directoryRef);
            Add(command, "$revision", revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException(
                    "A removed projected directory has no prior version.");
        }
        await using var memberships = connection.CreateCommand();
        memberships.Transaction = transaction;
        memberships.CommandText = """
            UPDATE search_view_directory_files
            SET removed_revision = $revision
            WHERE view_id = $view AND directory_ref = $ref
              AND removed_revision IS NULL;
            """;
        Add(memberships, "$view", viewId);
        Add(memberships, "$ref", directoryRef);
        Add(memberships, "$revision", revision);
        await memberships.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertDirectoryChildAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string directoryRef,
        string fileRef,
        long revision,
        string relativePath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO search_view_directory_files (
                view_id, directory_ref, file_ref, admitted_revision, relative_path)
            VALUES ($view, $directory, $file, $revision, $relative);
            """;
        Add(command, "$view", viewId);
        Add(command, "$directory", directoryRef);
        Add(command, "$file", fileRef);
        Add(command, "$revision", revision);
        Add(command, "$relative", relativePath);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> GetOrCreateAggregateTrackRefAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        SearchViewProjectedAggregateTrackGroup group,
        CancellationToken cancellationToken)
    {
        string itemRef = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO search_view_aggregate_tracks (
                    view_id, item_ref, group_index, query_json)
                VALUES ($view, $ref, $index, $query);
                """;
            Add(insert, "$view", viewId);
            Add(insert, "$ref", itemRef);
            Add(insert, "$index", group.Index);
            Add(insert, "$query", JsonSerializer.Serialize(group.Query));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                return itemRef;
        }
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT item_ref FROM search_view_aggregate_tracks
            WHERE view_id = $view AND group_index = $index;
            """;
        Add(read, "$view", viewId);
        Add(read, "$index", group.Index);
        return Convert.ToString(await read.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))
            ?? throw new InvalidDataException(
                "An aggregate-track group reference is missing.");
    }

    private static async Task InsertAggregateTrackVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string groupRef,
        long revision,
        string representativeRef,
        SearchViewProjectedAggregateTrackGroup group,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_view_aggregate_track_versions (
                view_id, item_ref, revision, share_count,
                selectable_option_count, representative_file_ref)
            VALUES ($view, $ref, $revision, $shares, $options, $representative);
            """;
        Add(command, "$view", viewId);
        Add(command, "$ref", groupRef);
        Add(command, "$revision", revision);
        Add(command, "$shares", group.ShareCount);
        Add(command, "$options", group.SelectableOptionCount);
        Add(command, "$representative", representativeRef);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAggregateTrackOptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string groupRef,
        string fileRef,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO search_view_aggregate_track_files (
                view_id, group_ref, file_ref, admitted_revision)
            VALUES ($view, $group, $file, $revision);
            """;
        Add(command, "$view", viewId);
        Add(command, "$group", groupRef);
        Add(command, "$file", fileRef);
        Add(command, "$revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> GetOrCreateAggregateAlbumRefAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        SearchViewProjectedAggregateAlbumGroup group,
        CancellationToken cancellationToken)
    {
        string itemRef = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO search_view_aggregate_albums (
                    view_id, item_ref, stable_username, stable_folder_path,
                    query_json)
                VALUES ($view, $ref, $username, $path, $query);
                """;
            Add(insert, "$view", viewId);
            Add(insert, "$ref", itemRef);
            Add(insert, "$username", group.StableIdentity.Username);
            Add(insert, "$path", group.StableIdentity.FolderPath);
            Add(insert, "$query", JsonSerializer.Serialize(group.Query));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                return itemRef;
        }
        return await ReadAggregateAlbumRefAsync(
            connection,
            transaction,
            viewId,
            group.StableIdentity,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadAggregateAlbumRefAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        PeerDirectoryIdentity stableIdentity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_ref FROM search_view_aggregate_albums
            WHERE view_id = $view AND stable_username = $username
              AND stable_folder_path = $path;
            """;
        Add(command, "$view", viewId);
        Add(command, "$username", stableIdentity.Username);
        Add(command, "$path", stableIdentity.FolderPath);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))
            ?? throw new InvalidDataException(
                "An aggregate-album group reference is missing.");
    }

    private static async Task InsertAggregateAlbumVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string groupRef,
        long revision,
        string representativeDirectoryRef,
        SearchViewProjectedAggregateAlbumGroup group,
        bool isRemoved,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_view_aggregate_album_versions (
                view_id, item_ref, revision, group_index, share_count,
                selectable_option_count, representative_directory_ref, is_removed)
            VALUES ($view, $ref, $revision, $index, $shares,
                $options, $representative, $removed);
            """;
        Add(command, "$view", viewId);
        Add(command, "$ref", groupRef);
        Add(command, "$revision", revision);
        Add(command, "$index", group.Index);
        Add(command, "$shares", group.ShareCount);
        Add(command, "$options", group.SelectableOptionCount);
        Add(command, "$representative", representativeDirectoryRef);
        Add(command, "$removed", isRemoved);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAggregateAlbumRemovalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string groupRef,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_view_aggregate_album_versions (
                view_id, item_ref, revision, group_index, share_count,
                selectable_option_count, representative_directory_ref, is_removed)
            SELECT view_id, item_ref, $revision, group_index, share_count,
                selectable_option_count, representative_directory_ref, 1
            FROM search_view_aggregate_album_versions
            WHERE view_id = $view AND item_ref = $ref
            ORDER BY revision DESC LIMIT 1;
            """;
        Add(command, "$view", viewId);
        Add(command, "$ref", groupRef);
        Add(command, "$revision", revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException(
                "A removed aggregate-album group has no prior version.");
    }

    private static async Task ReplaceAggregateAlbumOptionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid viewId,
        string groupRef,
        long revision,
        IReadOnlySet<string> currentOptions,
        CancellationToken cancellationToken)
    {
        var previous = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                WITH latest AS (
                    SELECT directory_ref, MAX(revision) AS revision
                    FROM search_view_aggregate_album_directory_versions
                    WHERE view_id = $view AND group_ref = $group
                      AND revision < $revision
                    GROUP BY directory_ref)
                SELECT version.directory_ref
                FROM latest
                JOIN search_view_aggregate_album_directory_versions version
                  ON version.view_id = $view AND version.group_ref = $group
                 AND version.directory_ref = latest.directory_ref
                 AND version.revision = latest.revision
                WHERE version.is_present = 1;
                """;
            Add(read, "$view", viewId);
            Add(read, "$group", groupRef);
            Add(read, "$revision", revision);
            await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                previous.Add(reader.GetString(0));
        }
        foreach (string directoryRef in previous.Concat(currentOptions)
            .Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO search_view_aggregate_album_directory_versions (
                    view_id, group_ref, directory_ref, revision, is_present)
                VALUES ($view, $group, $directory, $revision, $present);
                """;
            Add(command, "$view", viewId);
            Add(command, "$group", groupRef);
            Add(command, "$directory", directoryRef);
            Add(command, "$revision", revision);
            Add(command, "$present", currentOptions.Contains(directoryRef));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string RelativePath(string folderPath, string filename)
    {
        if (!filename.StartsWith(folderPath, StringComparison.Ordinal)
            || filename.Length <= folderPath.Length
            || filename[folderPath.Length] is not ('\\' or '/'))
            throw new InvalidDataException("A projected directory child is outside its exact directory.");
        return filename[(folderPath.Length + 1)..];
    }

    private static StoredSearchView ReadView(SqliteDataReader reader)
        => new(
            Guid.Parse(Value<string>(reader, "id")),
            Guid.Parse(Value<string>(reader, "source_job_id")),
            Value<string>(reader, "projection_kind"),
            Value<string>(reader, "definition_json"),
            Time(reader, "created_at_utc"),
            Time(reader, "expires_at_utc"),
            Value<long>(reader, "revision"),
            Value<int>(reader, "source_revision"),
            Value<long>(reader, "consumed_sequence"),
            Value<long>(reader, "is_complete") != 0,
            Value<string>(reader, "retention_state"),
            ReadCounters(reader));

    private static StoredSearchViewRevision ReadRevision(SqliteDataReader reader)
        => new(
            Guid.Parse(Value<string>(reader, "view_id")),
            Value<long>(reader, "revision"),
            Value<int>(reader, "source_revision"),
            Value<long>(reader, "consumed_sequence"),
            Value<long>(reader, "is_complete") != 0,
            Value<string>(reader, "retention_state"),
            ReadCounters(reader));

    private static SearchViewCounters ReadCounters(SqliteDataReader reader)
        => new(
            Value<long>(reader, "public_file_count"),
            Value<long>(reader, "locked_file_count"),
            Value<long>(reader, "public_bytes"),
            Value<long>(reader, "locked_bytes"),
            Value<int>(reader, "observed_peer_count"),
            Value<long>(reader, "projected_file_count"),
            Value<long>(reader, "projected_public_file_count"),
            Value<long>(reader, "projected_locked_file_count"),
            Value<long>(reader, "preferred_file_count"),
            Value<long>(reader, "other_file_count"),
            Value<long>(reader, "top_level_item_count"),
            Value<long>(reader, "selectable_option_count"));

    private static StoredSearchViewFile ReadFile(SqliteDataReader reader)
    {
        var input = new SearchProjectionInput(
            Value<long>(reader, "sequence"),
            Value<int>(reader, "source_row_revision"),
            Value<string>(reader, "username"),
            Value<int>(reader, "response_file_count"),
            Value<string>(reader, "filename"),
            Value<long>(reader, "size_bytes"),
            Nullable<int>(reader, "bit_rate"),
            Nullable<int>(reader, "bit_depth"),
            Nullable<int>(reader, "sample_rate"),
            Nullable<int>(reader, "duration_seconds"),
            Value<string>(reader, "extension"),
            Nullable<int>(reader, "upload_speed"),
            NullableBool(reader, "has_free_upload_slot"),
            JsonSerializer.Deserialize<FileAttributeSnapshot[]>(
                NullableString(reader, "attributes_json") ?? "null"),
            Time(reader, "observed_at_utc"),
            Nullable<int>(reader, "queue_length"),
            Enum.Parse<SearchResultVisibility>(Value<string>(reader, "visibility")));
        var key = new SearchProjectionSortKey(
            checked((uint)Value<long>(reader, "sort_high")),
            Value<int>(reader, "sort_upload_fast"),
            checked((uint)Value<long>(reader, "sort_mid")),
            Value<int>(reader, "sort_inferred"),
            Value<int>(reader, "sort_upload_medium"),
            Value<int>(reader, "sort_bitrate"),
            Value<int>(reader, "sort_tie"));
        return new(
            Value<string>(reader, "item_ref"),
            Value<long>(reader, "admitted_revision"),
            input,
            Enum.Parse<SearchPreferenceTier>(Value<string>(reader, "preference_tier")),
            Value<long>(reader, "necessary_conditions_satisfied") != 0,
            JsonSerializer.Deserialize<SearchPreferenceCondition[]>(
                Value<string>(reader, "condition_matches_json")) ?? [],
            JsonSerializer.Deserialize<SearchPreferenceCondition[]>(
                Value<string>(reader, "configured_conditions_json")) ?? [],
            key);
    }

    private static SearchViewFilePosition Position(StoredSearchViewFile row)
        => new(
            row.SortKey.HighFlags,
            row.SortKey.UploadSpeedFast,
            row.SortKey.MidFlags,
            row.SortKey.InferredTrackCount,
            row.SortKey.UploadSpeedMedium,
            row.SortKey.BitRate,
            row.SortKey.StableTieBreaker,
            row.Input.Sequence,
            row.Ref);

    private static StoredSearchViewDirectory ReadDirectory(SqliteDataReader reader)
        => new(
            Value<string>(reader, "directory_ref"),
            Value<string>(reader, "directory_username"),
            Value<string>(reader, "directory_folder_path"),
            Value<long>(reader, "public_matching_count"),
            Value<long>(reader, "locked_matching_count"),
            Value<long>(reader, "public_matching_bytes"),
            Value<long>(reader, "locked_matching_bytes"),
            Value<long>(reader, "is_fully_retrieved") != 0,
            Nullable<long>(reader, "retrieved_file_count"),
            Nullable<long>(reader, "retrieved_bytes"),
            ReadFile(reader));

    private static SearchViewDirectoryPosition DirectoryPosition(
        StoredSearchViewDirectory row)
        => new(
            row.BestChild.SortKey.HighFlags,
            row.BestChild.SortKey.UploadSpeedFast,
            row.BestChild.SortKey.MidFlags,
            row.BestChild.SortKey.InferredTrackCount,
            row.BestChild.SortKey.UploadSpeedMedium,
            row.BestChild.SortKey.BitRate,
            row.BestChild.SortKey.StableTieBreaker,
            row.BestChild.Input.Sequence,
            row.Username,
            row.FolderPath,
            row.Ref);

    private static StoredSearchViewAggregateTrackGroup ReadAggregateTrack(
        SqliteDataReader reader)
        => new(
            Value<string>(reader, "aggregate_ref"),
            Value<int>(reader, "aggregate_index"),
            Value<string>(reader, "aggregate_query_json"),
            Value<int>(reader, "aggregate_share_count"),
            Value<long>(reader, "aggregate_option_count"),
            ReadFile(reader));

    private static StoredSearchViewAggregateAlbumGroup ReadAggregateAlbum(
        SqliteDataReader reader)
        => new(
            Value<string>(reader, "aggregate_ref"),
            Value<int>(reader, "aggregate_index"),
            Value<string>(reader, "aggregate_query_json"),
            Value<int>(reader, "aggregate_share_count"),
            Value<long>(reader, "aggregate_option_count"),
            ReadDirectory(reader));

    private static void BindPosition(SqliteCommand command, SearchViewFilePosition position)
    {
        Add(command, "$high", (long)position.HighFlags);
        Add(command, "$fast", position.UploadSpeedFast);
        Add(command, "$mid", (long)position.MidFlags);
        Add(command, "$inferred", position.InferredTrackCount);
        Add(command, "$medium", position.UploadSpeedMedium);
        Add(command, "$bitrate", position.BitRate);
        Add(command, "$tie", position.StableTieBreaker);
        Add(command, "$sequence", position.Sequence);
        Add(command, "$ref", position.Ref);
    }

    private static void BindDirectoryPosition(
        SqliteCommand command,
        SearchViewDirectoryPosition position)
    {
        Add(command, "$high", (long)position.HighFlags);
        Add(command, "$fast", position.UploadSpeedFast);
        Add(command, "$mid", (long)position.MidFlags);
        Add(command, "$inferred", position.InferredTrackCount);
        Add(command, "$medium", position.UploadSpeedMedium);
        Add(command, "$bitrate", position.BitRate);
        Add(command, "$tie", position.StableTieBreaker);
        Add(command, "$sequence", position.Sequence);
        Add(command, "$username", position.Username);
        Add(command, "$path", position.FolderPath);
        Add(command, "$ref", position.Ref);
    }

    private static string FilePageSql(bool hasCursor)
        => """
            SELECT * FROM search_view_files
            WHERE view_id = $view AND admitted_revision <= $revision
            """ + "\n" + (hasCursor ? """
              AND (
                sort_high < $high OR
                sort_high = $high AND sort_upload_fast < $fast OR
                sort_high = $high AND sort_upload_fast = $fast AND sort_mid < $mid OR
                sort_high = $high AND sort_upload_fast = $fast AND sort_mid = $mid AND sort_inferred < $inferred OR
                sort_high = $high AND sort_upload_fast = $fast AND sort_mid = $mid AND sort_inferred = $inferred AND sort_upload_medium < $medium OR
                sort_high = $high AND sort_upload_fast = $fast AND sort_mid = $mid AND sort_inferred = $inferred AND sort_upload_medium = $medium AND sort_bitrate < $bitrate OR
                sort_high = $high AND sort_upload_fast = $fast AND sort_mid = $mid AND sort_inferred = $inferred AND sort_upload_medium = $medium AND sort_bitrate = $bitrate AND sort_tie < $tie OR
                sort_high = $high AND sort_upload_fast = $fast AND sort_mid = $mid AND sort_inferred = $inferred AND sort_upload_medium = $medium AND sort_bitrate = $bitrate AND sort_tie = $tie AND sequence > $sequence OR
                sort_high = $high AND sort_upload_fast = $fast AND sort_mid = $mid AND sort_inferred = $inferred AND sort_upload_medium = $medium AND sort_bitrate = $bitrate AND sort_tie = $tie AND sequence = $sequence AND item_ref > $ref)
            """ : "") + "\n" + """
            ORDER BY sort_high DESC, sort_upload_fast DESC, sort_mid DESC,
                     sort_inferred DESC, sort_upload_medium DESC,
                     sort_bitrate DESC, sort_tie DESC, sequence, item_ref
            LIMIT $limit;
            """;

    private static string DirectoryPageSql(bool hasCursor)
        => """
            WITH latest AS (
                SELECT item_ref, MAX(revision) AS revision
                FROM search_view_directory_versions
                WHERE view_id = $view AND revision <= $revision
                GROUP BY item_ref)
            SELECT directory.item_ref AS directory_ref,
                   directory.username AS directory_username,
                   directory.folder_path AS directory_folder_path,
                   version.public_matching_count,
                   version.locked_matching_count,
                   version.public_matching_bytes,
                   version.locked_matching_bytes,
                   version.is_fully_retrieved,
                   version.retrieved_file_count,
                   version.retrieved_bytes,
                   file.*
            FROM latest
            JOIN search_view_directory_versions version
              ON version.view_id = $view
             AND version.item_ref = latest.item_ref
             AND version.revision = latest.revision
            JOIN search_view_directories directory
              ON directory.view_id = version.view_id
             AND directory.item_ref = version.item_ref
            JOIN search_view_files file
              ON file.view_id = version.view_id
             AND file.item_ref = version.best_file_ref
            WHERE version.is_removed = 0
            """ + "\n" + (hasCursor ? """
            AND (
                file.sort_high < $high OR
                file.sort_high = $high AND file.sort_upload_fast < $fast OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid < $mid OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred < $inferred OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium < $medium OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate < $bitrate OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie < $tie OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence > $sequence OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence = $sequence AND directory.username > $username COLLATE BINARY OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence = $sequence AND directory.username = $username AND directory.folder_path > $path COLLATE BINARY OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence = $sequence AND directory.username = $username AND directory.folder_path = $path AND directory.item_ref > $ref)
            """ : "") + "\n" + """
            ORDER BY file.sort_high DESC, file.sort_upload_fast DESC,
                     file.sort_mid DESC, file.sort_inferred DESC,
                     file.sort_upload_medium DESC, file.sort_bitrate DESC,
                     file.sort_tie DESC, file.sequence,
                     directory.username COLLATE BINARY,
                     directory.folder_path COLLATE BINARY,
                     directory.item_ref
            LIMIT $limit;
            """;

    private static string AggregateTrackOptionsSql(bool hasCursor)
        => """
            SELECT file.*
            FROM search_view_aggregate_track_files mapping
            JOIN search_view_files file
              ON file.view_id = mapping.view_id
             AND file.item_ref = mapping.file_ref
            WHERE mapping.view_id = $view AND mapping.group_ref = $group
              AND mapping.admitted_revision <= $revision
            """ + "\n" + (hasCursor ? """
              AND (
                file.sort_high < $high OR
                file.sort_high = $high AND file.sort_upload_fast < $fast OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid < $mid OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred < $inferred OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium < $medium OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate < $bitrate OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie < $tie OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence > $sequence OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence = $sequence AND file.item_ref > $ref)
            """ : "") + "\n" + """
            ORDER BY file.sort_high DESC, file.sort_upload_fast DESC,
                     file.sort_mid DESC, file.sort_inferred DESC,
                     file.sort_upload_medium DESC, file.sort_bitrate DESC,
                     file.sort_tie DESC, file.sequence, file.item_ref
            LIMIT $limit;
            """;

    private const string AggregateAlbumPageSql = """
        WITH latest_group AS (
            SELECT item_ref, MAX(revision) AS revision
            FROM search_view_aggregate_album_versions
            WHERE view_id = $view AND revision <= $revision
            GROUP BY item_ref),
        latest_directory AS (
            SELECT item_ref, MAX(revision) AS revision
            FROM search_view_directory_versions
            WHERE view_id = $view AND revision <= $revision
            GROUP BY item_ref)
        SELECT aggregate.item_ref AS aggregate_ref,
               group_version.group_index AS aggregate_index,
               aggregate.query_json AS aggregate_query_json,
               group_version.share_count AS aggregate_share_count,
               group_version.selectable_option_count AS aggregate_option_count,
               directory.item_ref AS directory_ref,
               directory.username AS directory_username,
               directory.folder_path AS directory_folder_path,
               directory_version.public_matching_count,
               directory_version.locked_matching_count,
               directory_version.public_matching_bytes,
               directory_version.locked_matching_bytes,
               directory_version.is_fully_retrieved,
               directory_version.retrieved_file_count,
               directory_version.retrieved_bytes,
               file.*
        FROM latest_group
        JOIN search_view_aggregate_album_versions group_version
          ON group_version.view_id = $view
         AND group_version.item_ref = latest_group.item_ref
         AND group_version.revision = latest_group.revision
        JOIN search_view_aggregate_albums aggregate
          ON aggregate.view_id = group_version.view_id
         AND aggregate.item_ref = group_version.item_ref
        JOIN latest_directory
          ON latest_directory.item_ref = group_version.representative_directory_ref
        JOIN search_view_directory_versions directory_version
          ON directory_version.view_id = $view
         AND directory_version.item_ref = latest_directory.item_ref
         AND directory_version.revision = latest_directory.revision
        JOIN search_view_directories directory
          ON directory.view_id = directory_version.view_id
         AND directory.item_ref = directory_version.item_ref
        JOIN search_view_files file
          ON file.view_id = directory_version.view_id
         AND file.item_ref = directory_version.best_file_ref
        WHERE group_version.is_removed = 0 AND directory_version.is_removed = 0
          AND ($cursor_shares IS NULL
               OR group_version.share_count < $cursor_shares
               OR group_version.share_count = $cursor_shares
                  AND group_version.group_index > $cursor_index
               OR group_version.share_count = $cursor_shares
                  AND group_version.group_index = $cursor_index
                  AND aggregate.item_ref > $cursor_ref)
        ORDER BY group_version.share_count DESC,
                 group_version.group_index, aggregate.item_ref
        LIMIT $limit;
        """;

    private static string AggregateAlbumOptionsSql(bool hasCursor)
        => """
            WITH latest_membership AS (
                SELECT directory_ref, MAX(revision) AS revision
                FROM search_view_aggregate_album_directory_versions
                WHERE view_id = $view AND group_ref = $group
                  AND revision <= $revision
                GROUP BY directory_ref),
            latest_directory AS (
                SELECT item_ref, MAX(revision) AS revision
                FROM search_view_directory_versions
                WHERE view_id = $view AND revision <= $revision
                GROUP BY item_ref)
            SELECT directory.item_ref AS directory_ref,
                   directory.username AS directory_username,
                   directory.folder_path AS directory_folder_path,
                   directory_version.public_matching_count,
                   directory_version.locked_matching_count,
                   directory_version.public_matching_bytes,
                   directory_version.locked_matching_bytes,
                   directory_version.is_fully_retrieved,
                   directory_version.retrieved_file_count,
                   directory_version.retrieved_bytes,
                   file.*
            FROM latest_membership
            JOIN search_view_aggregate_album_directory_versions membership
              ON membership.view_id = $view AND membership.group_ref = $group
             AND membership.directory_ref = latest_membership.directory_ref
             AND membership.revision = latest_membership.revision
            JOIN latest_directory
              ON latest_directory.item_ref = membership.directory_ref
            JOIN search_view_directory_versions directory_version
              ON directory_version.view_id = $view
             AND directory_version.item_ref = latest_directory.item_ref
             AND directory_version.revision = latest_directory.revision
            JOIN search_view_directories directory
              ON directory.view_id = directory_version.view_id
             AND directory.item_ref = directory_version.item_ref
            JOIN search_view_files file
              ON file.view_id = directory_version.view_id
             AND file.item_ref = directory_version.best_file_ref
            WHERE membership.is_present = 1 AND directory_version.is_removed = 0
            """ + "\n" + (hasCursor ? """
              AND (
                file.sort_high < $high OR
                file.sort_high = $high AND file.sort_upload_fast < $fast OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid < $mid OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred < $inferred OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium < $medium OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate < $bitrate OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie < $tie OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence > $sequence OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence = $sequence AND directory.username > $username COLLATE BINARY OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence = $sequence AND directory.username = $username AND directory.folder_path > $path COLLATE BINARY OR
                file.sort_high = $high AND file.sort_upload_fast = $fast AND file.sort_mid = $mid AND file.sort_inferred = $inferred AND file.sort_upload_medium = $medium AND file.sort_bitrate = $bitrate AND file.sort_tie = $tie AND file.sequence = $sequence AND directory.username = $username AND directory.folder_path = $path AND directory.item_ref > $ref)
            """ : "") + "\n" + """
            ORDER BY file.sort_high DESC, file.sort_upload_fast DESC,
                     file.sort_mid DESC, file.sort_inferred DESC,
                     file.sort_upload_medium DESC, file.sort_bitrate DESC,
                     file.sort_tie DESC, file.sequence,
                     directory.username COLLATE BINARY,
                     directory.folder_path COLLATE BINARY,
                     directory.item_ref
            LIMIT $limit;
            """;

    private static readonly SearchViewCounters EmptyCounters = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private const string FileInsertSql = """
        INSERT OR IGNORE INTO search_view_files (
            view_id, item_ref, admitted_revision, sequence, source_row_revision,
            username, response_file_count, filename, size_bytes, bit_rate,
            bit_depth, sample_rate, duration_seconds, extension, upload_speed,
            has_free_upload_slot, queue_length, attributes_json, observed_at_utc,
            visibility, preference_tier, necessary_conditions_satisfied,
            condition_matches_json,
            configured_conditions_json,
            sort_high, sort_upload_fast, sort_mid, sort_inferred,
            sort_upload_medium, sort_bitrate, sort_tie)
        VALUES (
            $view, $ref, $admitted, $sequence, $source_row_revision,
            $username, $response_count, $filename, $size, $bitrate,
            $bitdepth, $sample_rate, $length, $extension, $upload_speed,
            $free_slot, $queue_length, $attributes, $observed,
            $visibility, $tier, $necessary_satisfied, $conditions,
            $configured_conditions,
            $high, $fast, $mid,
            $inferred, $medium, $sort_bitrate, $tie);
        """;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken)
            .ConfigureAwait(false);
        return connection;
    }

    private async Task<T> WithWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            T result = await action(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new InvalidOperationException("The search-view store is unavailable.");
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value switch
        {
            null => DBNull.Value,
            Guid id => id.ToString("D"),
            DateTimeOffset time => time.ToUniversalTime().ToUnixTimeMilliseconds(),
            bool flag => flag ? 1 : 0,
            _ => value,
        });

    private static T Value<T>(SqliteDataReader reader, string name)
        => reader.GetFieldValue<T>(reader.GetOrdinal(name));

    private static T? Nullable<T>(SqliteDataReader reader, string name) where T : struct
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    private static bool? NullableBool(SqliteDataReader reader, string name)
    {
        long? value = Nullable<long>(reader, name);
        return value == null ? null : value != 0;
    }

    private static string? NullableString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset Time(SqliteDataReader reader, string name)
        => DateTimeOffset.FromUnixTimeMilliseconds(Value<long>(reader, name));

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref initialized, 0);
        writeGate.Dispose();
        SqliteConnectionPool.Clear(ConnectionString);
        return ValueTask.CompletedTask;
    }
}
