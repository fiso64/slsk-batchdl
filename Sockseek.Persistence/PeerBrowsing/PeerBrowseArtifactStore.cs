using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sockseek.Core;
using Sockseek.Core.IO;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Snapshots;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Persistence;

namespace Sockseek.Persistence.PeerBrowsing;

/// <summary>
/// Owns short-lived peer-browse resources and one immutable SQLite artifact per
/// successful generation. It is deliberately separate from Sockseek's domain
/// history database.
/// </summary>
public sealed class PeerBrowseArtifactStore
{
    public static readonly TimeSpan DefaultResourceRetention = TimeSpan.FromHours(24);
    public const long DefaultArtifactByteBudget = 2L * 1024 * 1024 * 1024;
    public const int DefaultResourceCountTarget = 4_096;

    private readonly string rootDirectory;
    private readonly string stagingDirectory;
    private readonly string artifactDirectory;
    private readonly string registryPath;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan resourceRetention;
    private readonly long artifactByteBudget;
    private readonly int resourceCountTarget;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly Lock leaseGate = new();
    private readonly Dictionary<Guid, int> leases = [];
    private readonly HashSet<Guid> evicting = [];
    private long lastCleanupLogTick = long.MinValue;
    private long lastBudgetLogTick = long.MinValue;
    private bool initialized;
    private readonly ILogger<PeerBrowseArtifactStore> logger;

    /// <summary>Raised after a browse resource has been removed from the registry.</summary>
    public event Action<Guid>? ResourceRemoved;

    public PeerBrowseArtifactStore(
        string dataDirectory,
        TimeProvider? timeProvider = null,
        TimeSpan? resourceRetention = null,
        long artifactByteBudget = DefaultArtifactByteBudget,
        int resourceCountTarget = DefaultResourceCountTarget,
        ILogger<PeerBrowseArtifactStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (resourceRetention is { } retention && retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(resourceRetention));
        if (artifactByteBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(artifactByteBudget));
        if (resourceCountTarget <= 0)
            throw new ArgumentOutOfRangeException(nameof(resourceCountTarget));

        rootDirectory = Path.GetFullPath(Path.Combine(dataDirectory, "peer-browses"));
        stagingDirectory = Path.Combine(rootDirectory, "staging");
        artifactDirectory = Path.Combine(rootDirectory, "artifacts");
        registryPath = Path.Combine(rootDirectory, "resources.sqlite");
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<PeerBrowseArtifactStore>.Instance;
        this.resourceRetention = resourceRetention ?? DefaultResourceRetention;
        this.artifactByteBudget = artifactByteBudget;
        this.resourceCountTarget = resourceCountTarget;
    }

    public string RootDirectory => rootDirectory;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref initialized))
            return;

        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;

            OwnerOnlyFilePermissions.EnsureDirectory(rootDirectory);
            OwnerOnlyFilePermissions.EnsureDirectory(stagingDirectory);
            OwnerOnlyFilePermissions.EnsureDirectory(artifactDirectory);
            await using (SqliteConnection connection = await OpenRegistryAsync(
                             SqliteOpenMode.ReadWriteCreate,
                             cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    PRAGMA temp_store=FILE;
                    PRAGMA busy_timeout=5000;

                    CREATE TABLE IF NOT EXISTS browse_resources (
                        browse_id TEXT PRIMARY KEY,
                        local_account TEXT NOT NULL,
                        username TEXT NOT NULL,
                        state INTEGER NOT NULL,
                        phase INTEGER NOT NULL,
                        compressed_bytes_received INTEGER NOT NULL,
                        compressed_bytes_expected INTEGER,
                        decompressed_bytes_read INTEGER NOT NULL DEFAULT 0,
                        directory_count INTEGER NOT NULL,
                        file_count INTEGER NOT NULL,
                        total_file_bytes INTEGER NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        completed_at_utc TEXT,
                        expires_at_utc TEXT NOT NULL,
                        failure_code TEXT,
                        failure_message TEXT,
                        artifact_file TEXT,
                        artifact_bytes INTEGER,
                        revision INTEGER NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_browse_resources_key
                        ON browse_resources(local_account, username, completed_at_utc DESC);
                    CREATE INDEX IF NOT EXISTS idx_browse_resources_expiry
                        ON browse_resources(expires_at_utc);
                    """,
                    cancellationToken).ConfigureAwait(false);

                // Early user-browse builds persisted this now-unused progress
                // value as NOT NULL without a default. Retain it internally so
                // both that layout and the short-lived layout that omitted it can
                // accept new rows. It is deliberately not part of the public DTO.
                if (!await HasBrowseResourceColumnAsync(
                        connection,
                        "decompressed_bytes_read",
                        cancellationToken).ConfigureAwait(false))
                {
                    await ExecuteAsync(
                        connection,
                        "ALTER TABLE browse_resources ADD COLUMN decompressed_bytes_read INTEGER NOT NULL DEFAULT 0;",
                        cancellationToken).ConfigureAwait(false);
                }

                DateTimeOffset now = timeProvider.GetUtcNow();
                await using var interrupted = connection.CreateCommand();
                interrupted.CommandText =
                    """
                    UPDATE browse_resources
                    SET state = $failed,
                        failure_code = 'daemon-restarted',
                        failure_message = 'The daemon restarted before the peer browse completed.',
                        updated_at_utc = $now,
                        expires_at_utc = $expires,
                        revision = revision + 1
                    WHERE state IN ($queued, $running);
                    """;
                Add(interrupted, "$failed", (int)PeerBrowseState.Failed);
                Add(interrupted, "$queued", (int)PeerBrowseState.Queued);
                Add(interrupted, "$running", (int)PeerBrowseState.Running);
                Add(interrupted, "$now", Format(now));
                Add(interrupted, "$expires", Format(now + resourceRetention));
                await interrupted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            OwnerOnlyFilePermissions.EnsureFile(registryPath);
            DeleteFiles(stagingDirectory, "*.staging");
            await RemoveOrphanArtifactsAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }

        await EvictAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowseResource> CreateQueuedAsync(
        string localAccount,
        string username,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        localAccount = PeerIdentityValidator.ValidateUsername(localAccount);
        username = PeerIdentityValidator.ValidateUsername(username);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var resource = new PeerBrowseResource(
            Guid.NewGuid(),
            localAccount,
            username,
            PeerBrowseState.Queued,
            PeerBrowsePhase.WaitingForPeer,
            0,
            null,
            0,
            0,
            0,
            now,
            now,
            null,
            now + resourceRetention,
            null,
            1);

        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO browse_resources(
                browse_id, local_account, username, state, phase,
                compressed_bytes_received, compressed_bytes_expected,
                decompressed_bytes_read, directory_count, file_count,
                total_file_bytes, created_at_utc, updated_at_utc,
                completed_at_utc, expires_at_utc, failure_code,
                failure_message, artifact_file, artifact_bytes, revision)
            VALUES(
                $id, $account, $username, $state, $phase,
                0, NULL, 0, 0, 0, 0, $created, $updated,
                NULL, $expires, NULL, NULL, NULL, NULL, 1);
            """;
        Add(command, "$id", resource.BrowseId.ToString("D"));
        Add(command, "$account", resource.LocalAccount);
        Add(command, "$username", resource.Username);
        Add(command, "$state", (int)resource.State);
        Add(command, "$phase", (int)resource.Phase);
        Add(command, "$created", Format(resource.CreatedAt));
        Add(command, "$updated", Format(resource.UpdatedAt));
        Add(command, "$expires", Format(resource.ExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return resource;
    }

    public async Task<PeerBrowseResource?> GetAsync(
        Guid browseId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        PeerBrowseResource? resource;
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        {
            resource = await ReadResourceAsync(connection, browseId, cancellationToken).ConfigureAwait(false);
        }
        if (resource is null
            || resource.State is PeerBrowseState.Queued or PeerBrowseState.Running
            || resource.ExpiresAt > timeProvider.GetUtcNow())
            return resource;

        await EvictAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<PeerBrowseResource?> FindFreshAsync(
        string localAccount,
        string username,
        TimeSpan freshness,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (freshness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(freshness));
        localAccount = PeerIdentityValidator.ValidateUsername(localAccount);
        username = PeerIdentityValidator.ValidateUsername(username);

        PeerBrowseResource? resource;
        string? artifactFile;
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                ResourceSelect +
                """
                 WHERE local_account = $account
                   AND username = $username
                   AND state = $complete
                   AND completed_at_utc > $cutoff
                   AND expires_at_utc > $now
                 ORDER BY completed_at_utc DESC
                 LIMIT 1;
                """;
            Add(command, "$account", localAccount);
            Add(command, "$username", username);
            Add(command, "$complete", (int)PeerBrowseState.Complete);
            DateTimeOffset now = timeProvider.GetUtcNow();
            Add(command, "$cutoff", Format(now - freshness));
            Add(command, "$now", Format(now));
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            resource = ReadResource(reader);
            artifactFile = reader.IsDBNull(14) ? null : reader.GetString(14);
        }

        return artifactFile is not null
               && File.Exists(ResolveArtifactPath(artifactFile))
            ? resource
            : null;
    }

    public Task MarkRunningAsync(Guid browseId, CancellationToken cancellationToken = default)
        => UpdateLifecycleAsync(
            browseId,
            PeerBrowseState.Running,
            PeerBrowsePhase.WaitingForPeer,
            null,
            cancellationToken);

    public Task MarkIndexingAsync(Guid browseId, CancellationToken cancellationToken = default)
        => UpdateLifecycleAsync(
            browseId,
            PeerBrowseState.Running,
            PeerBrowsePhase.Indexing,
            null,
            cancellationToken,
            onlyWhileActive: true);

    public async Task UpdateProgressAsync(
        Guid browseId,
        PeerBrowsePhase phase,
        long compressedBytesReceived,
        long? compressedBytesExpected,
        PeerBrowseIndexProgress progress,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE browse_resources
            SET phase = CASE WHEN phase < $phase THEN $phase ELSE phase END,
                compressed_bytes_received = MAX(compressed_bytes_received, $compressed),
                compressed_bytes_expected = COALESCE($expected, compressed_bytes_expected),
                directory_count = MAX(directory_count, $directories),
                file_count = MAX(file_count, $files),
                total_file_bytes = MAX(total_file_bytes, $total_bytes),
                updated_at_utc = $updated,
                revision = revision + 1
            WHERE browse_id = $id AND state = $running;
            """;
        Add(command, "$id", browseId.ToString("D"));
        Add(command, "$phase", (int)phase);
        Add(command, "$compressed", compressedBytesReceived);
        Add(command, "$expected", compressedBytesExpected);
        Add(command, "$directories", progress.DirectoryCount);
        Add(command, "$files", progress.FileCount);
        Add(command, "$total_bytes", progress.TotalFileBytes);
        Add(command, "$updated", Format(timeProvider.GetUtcNow()));
        Add(command, "$running", (int)PeerBrowseState.Running);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        Guid browseId,
        string code,
        string message,
        CancellationToken cancellationToken = default)
    {
        await UpdateLifecycleAsync(
                browseId,
                PeerBrowseState.Failed,
                PeerBrowsePhase.Indexing,
                new PeerBrowseFailure(code, message),
                cancellationToken,
                onlyWhileActive: true)
            .ConfigureAwait(false);
        await EvictCoreAsync(browseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkCancelledAsync(Guid browseId, CancellationToken cancellationToken = default)
    {
        await UpdateLifecycleAsync(
                browseId,
                PeerBrowseState.Cancelled,
                PeerBrowsePhase.Indexing,
                new PeerBrowseFailure("browse-cancelled", "The peer browse was cancelled."),
                cancellationToken,
                onlyWhileActive: true)
            .ConfigureAwait(false);
        await EvictCoreAsync(browseId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PeerBrowseArtifactWriter> BeginWriteAsync(
        PeerBrowseResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await PeerBrowseArtifactWriter.CreateAsync(this, resource, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PeerDirectorySnapshot> ReadDirectoryAsync(
        Guid browseId,
        PeerDirectoryIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        PeerBrowseResource resource = await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(resource.Username, identity.Username))
            throw new ArgumentException("The directory peer does not match the browse resource.", nameof(identity));

        await using ArtifactLease lease = await AcquireLeaseAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenArtifactAsync(
            lease.ArtifactPath,
            cancellationToken).ConfigureAwait(false);
        string identityPath = NormalizeIdentityPath(identity.FolderPath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.file_id, f.wire_filename, f.size_bytes, f.extension,
                   f.bit_rate, f.bit_depth, f.sample_rate, f.length_seconds,
                   a.attribute_type, a.attribute_value
            FROM files f
            JOIN directories d ON d.directory_id = f.directory_id
            LEFT JOIN file_attributes a ON a.file_id = f.file_id
            WHERE ordinal_same_or_descendant(d.identity_path, $path)
              AND f.visibility = $public
            ORDER BY f.file_id, a.attribute_ordinal;
            """;
        Add(command, "$path", identityPath);
        Add(command, "$public", (int)PeerBrowseEntryVisibility.Public);

        var targets = new List<PeerFileTarget>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await ReadPeerFileTargetsAsync(
            reader,
            identity.Username,
            attributeTypeOrdinal: 8,
            attributeValueOrdinal: 9,
            static _ => false,
            (target, _) => targets.Add(target),
            cancellationToken).ConfigureAwait(false);
        return new PeerDirectorySnapshot(identity, targets, isComplete: true);
    }

    public async Task<PeerBrowseDirectoryEntry?> ReadDirectoryEntryAsync(
        Guid browseId,
        long directoryId,
        CancellationToken cancellationToken = default)
    {
        if (directoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(directoryId));
        await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using ArtifactLease lease = await AcquireLeaseAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenArtifactAsync(lease.ArtifactPath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = DirectorySelect + " WHERE directory_id = $id;";
        Add(command, "$id", directoryId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDirectoryEntry(reader)
            : null;
    }

    public async Task<PeerBrowsePage<PeerBrowseDirectoryEntry>> ReadDirectoriesAsync(
        Guid browseId,
        long? parentId,
        string? query,
        bool recursive,
        string? afterSortKey,
        long? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(afterSortKey, afterId, limit);
        await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using ArtifactLease lease = await AcquireLeaseAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenArtifactAsync(lease.ArtifactPath, cancellationToken).ConfigureAwait(false);

        string? parentIdentity = null;
        if (recursive && parentId is not null)
        {
            await using var parent = connection.CreateCommand();
            parent.CommandText = "SELECT identity_path FROM directories WHERE directory_id = $id;";
            Add(parent, "$id", parentId.Value);
            parentIdentity = await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new KeyNotFoundException($"Peer browse directory '{parentId}' does not exist.");
        }

        var predicates = new List<string>();
        if (recursive)
        {
            if (parentIdentity is not null)
                predicates.Add("ordinal_descendant(identity_path, $parent_identity)");
        }
        else if (parentId is null)
        {
            predicates.Add("parent_id IS NULL");
        }
        else
        {
            predicates.Add("parent_id = $parent_id");
        }
        if (!string.IsNullOrEmpty(query))
            predicates.Add("ordinal_contains(display_path, $query)");
        if (afterSortKey is not null)
            predicates.Add("(display_path > $after_key COLLATE BINARY OR (display_path = $after_key COLLATE BINARY AND directory_id > $after_id))");

        await using var command = connection.CreateCommand();
        command.CommandText = DirectorySelect
                              + (predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates))
                              + " ORDER BY display_path COLLATE BINARY, directory_id LIMIT $limit;";
        if (parentId is not null)
            Add(command, "$parent_id", parentId.Value);
        Add(command, "$parent_identity", parentIdentity);
        Add(command, "$query", query);
        Add(command, "$after_key", afterSortKey);
        Add(command, "$after_id", afterId);
        Add(command, "$limit", checked(limit + 1));

        var items = new List<PeerBrowseDirectoryEntry>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(ReadDirectoryEntry(reader));
        return ToPage(items, limit, static item => item.DisplayPath, static item => item.DirectoryId);
    }

    public async Task<PeerBrowsePage<PeerBrowseFileEntry>> ReadFilesAsync(
        Guid browseId,
        long directoryId,
        string? query,
        string? afterSortKey,
        long? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (directoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(directoryId));
        ValidatePage(afterSortKey, afterId, limit);
        await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using ArtifactLease lease = await AcquireLeaseAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenArtifactAsync(lease.ArtifactPath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH page AS (
                SELECT file_id, directory_id, visibility, name, size_bytes, extension,
                       bit_rate, bit_depth, sample_rate, length_seconds
                FROM files
                WHERE directory_id = $directory_id
                  AND ($query IS NULL OR ordinal_contains(name, $query))
                  AND ($after_key IS NULL
                       OR name > $after_key COLLATE BINARY
                       OR (name = $after_key COLLATE BINARY AND file_id > $after_id))
                ORDER BY name COLLATE BINARY, file_id
                LIMIT $limit
            )
            SELECT page.file_id, page.directory_id, page.visibility, page.name, page.size_bytes,
                   page.extension, page.bit_rate, page.bit_depth, page.sample_rate,
                   page.length_seconds, a.attribute_type, a.attribute_value
            FROM page
            LEFT JOIN file_attributes a ON a.file_id = page.file_id
            ORDER BY page.name COLLATE BINARY, page.file_id, a.attribute_ordinal;
            """;
        Add(command, "$directory_id", directoryId);
        Add(command, "$query", string.IsNullOrEmpty(query) ? null : query);
        Add(command, "$after_key", afterSortKey);
        Add(command, "$after_id", afterId);
        Add(command, "$limit", checked(limit + 1));

        var items = new List<PeerBrowseFileEntry>(limit + 1);
        long? currentId = null;
        long currentDirectory = 0;
        PeerBrowseEntryVisibility visibility = PeerBrowseEntryVisibility.Public;
        string? name = null;
        long size = 0;
        string? extension = null;
        int? bitRate = null;
        int? bitDepth = null;
        int? sampleRate = null;
        int? length = null;
        List<PeerBrowseFileAttribute>? attributes = null;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long fileId = reader.GetInt64(0);
            if (fileId != currentId)
            {
                AddCurrent();
                currentId = fileId;
                currentDirectory = reader.GetInt64(1);
                visibility = (PeerBrowseEntryVisibility)reader.GetInt32(2);
                name = reader.GetString(3);
                size = reader.GetInt64(4);
                extension = reader.IsDBNull(5) ? null : reader.GetString(5);
                bitRate = NullableInt(reader, 6);
                bitDepth = NullableInt(reader, 7);
                sampleRate = NullableInt(reader, 8);
                length = NullableInt(reader, 9);
                attributes = null;
            }
            if (!reader.IsDBNull(10))
            {
                attributes ??= [];
                attributes.Add(new PeerBrowseFileAttribute(reader.GetInt32(10), reader.GetInt32(11)));
            }
        }
        AddCurrent();
        return ToPage(items, limit, static item => item.Name, static item => item.FileId);

        void AddCurrent()
        {
            if (currentId is null)
                return;
            items.Add(new PeerBrowseFileEntry(
                currentId.Value,
                currentDirectory,
                visibility,
                name!,
                size,
                extension,
                bitRate,
                bitDepth,
                sampleRate,
                length,
                attributes));
        }
    }

    public async Task<PeerBrowseFileEntry?> ReadFileEntryAsync(
        Guid browseId,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        if (fileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(fileId));
        await RequireCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using ArtifactLease lease = await AcquireLeaseAsync(browseId, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenArtifactAsync(lease.ArtifactPath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.file_id, f.directory_id, f.visibility, f.name, f.size_bytes, f.extension,
                   f.bit_rate, f.bit_depth, f.sample_rate, f.length_seconds,
                   a.attribute_type, a.attribute_value
            FROM files f
            LEFT JOIN file_attributes a ON a.file_id = f.file_id
            WHERE f.file_id = $file_id
            ORDER BY a.attribute_ordinal;
            """;
        Add(command, "$file_id", fileId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var attributes = new List<PeerBrowseFileAttribute>();
        long directoryId = reader.GetInt64(1);
        PeerBrowseEntryVisibility visibility = (PeerBrowseEntryVisibility)reader.GetInt32(2);
        string name = reader.GetString(3);
        long size = reader.GetInt64(4);
        string? extension = reader.IsDBNull(5) ? null : reader.GetString(5);
        int? bitRate = NullableInt(reader, 6);
        int? bitDepth = NullableInt(reader, 7);
        int? sampleRate = NullableInt(reader, 8);
        int? length = NullableInt(reader, 9);
        do
        {
            if (!reader.IsDBNull(10))
                attributes.Add(new PeerBrowseFileAttribute(reader.GetInt32(10), reader.GetInt32(11)));
        }
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));

        return new PeerBrowseFileEntry(
            fileId,
            directoryId,
            visibility,
            name,
            size,
            extension,
            bitRate,
            bitDepth,
            sampleRate,
            length,
            attributes.Count == 0 ? null : attributes);
    }

    public async Task<PeerBrowseDownloadResolution> ResolveDownloadSelectionAsync(
        Guid browseId,
        IReadOnlyList<long> directoryIds,
        IReadOnlyList<long> fileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directoryIds);
        ArgumentNullException.ThrowIfNull(fileIds);
        if (directoryIds.Count == 0 && fileIds.Count == 0)
            throw new PeerBrowseSelectionException("At least one directory or file must be selected.");
        if (directoryIds.Any(id => id <= 0) || fileIds.Any(id => id <= 0))
            throw new PeerBrowseSelectionException("Selection IDs must be positive.");

        PeerBrowseResource resource = await RequireCompleteAsync(
            browseId,
            cancellationToken).ConfigureAwait(false);
        await using ArtifactLease lease = await AcquireLeaseAsync(
            browseId,
            cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenArtifactAsync(
            lease.ArtifactPath,
            cancellationToken).ConfigureAwait(false);

        long[] distinctDirectoryIds = directoryIds.Distinct().ToArray();
        long[] distinctFileIds = fileIds.Distinct().ToArray();
        int redundant = checked(
            directoryIds.Count - distinctDirectoryIds.Length
            + fileIds.Count - distinctFileIds.Length);
        await CreateIdTableAsync(
            connection,
            "selected_directories",
            distinctDirectoryIds,
            cancellationToken).ConfigureAwait(false);
        await CreateIdTableAsync(
            connection,
            "selected_files",
            distinctFileIds,
            cancellationToken).ConfigureAwait(false);

        var selectedDirectories = new List<SelectionDirectory>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT d.directory_id, d.identity_path, d.name, d.display_path,
                       d.visibility, d.locked_descendant_count
                FROM directories d
                JOIN selected_directories s ON s.id = d.directory_id
                ORDER BY length(d.identity_path), d.identity_path COLLATE BINARY, d.directory_id;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                selectedDirectories.Add(new SelectionDirectory(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    (PeerBrowseEntryVisibility)reader.GetInt32(4),
                    reader.GetInt64(5)));
            }
        }
        if (selectedDirectories.Count != distinctDirectoryIds.Length)
            throw new PeerBrowseSelectionException("One or more selected directories do not exist in this browse.");
        if (selectedDirectories.Any(directory => directory.Visibility == PeerBrowseEntryVisibility.Locked))
            throw new PeerBrowseSelectionException("Locked directories cannot be selected for download.");

        var canonicalDirectories = new List<SelectionDirectory>();
        var canonicalDirectoryPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (SelectionDirectory directory in selectedDirectories)
        {
            if (HasSameOrAncestor(canonicalDirectoryPaths, directory.IdentityPath))
            {
                redundant = checked(redundant + 1);
            }
            else
            {
                canonicalDirectories.Add(directory);
                canonicalDirectoryPaths.Add(directory.IdentityPath);
            }
        }

        List<SelectedFile> selectedFiles = await ReadSelectedFilesAsync(
            connection,
            resource.Username,
            cancellationToken).ConfigureAwait(false);
        if (selectedFiles.Count != distinctFileIds.Length)
            throw new PeerBrowseSelectionException("One or more selected files do not exist in this browse.");
        if (selectedFiles.Any(file => file.Visibility != PeerBrowseEntryVisibility.Public))
            throw new PeerBrowseSelectionException("Locked files cannot be selected for download.");

        var standaloneFiles = new List<SelectedFile>();
        foreach (SelectedFile file in selectedFiles)
        {
            if (HasSameOrAncestor(canonicalDirectoryPaths, file.Directory.IdentityPath))
            {
                redundant = checked(redundant + 1);
            }
            else
            {
                standaloneFiles.Add(file);
            }
        }

        var orderedPlans = new List<(string SortKey, DirectoryTransferPlan Plan)>();
        long totalFiles = 0;
        long totalBytes = 0;
        long lockedSkipped = 0;
        IReadOnlyDictionary<long, DirectoryTransferPlan> directoryPlans = await ReadPlansAsync(
            connection,
            resource.Username,
            canonicalDirectories,
            cancellationToken).ConfigureAwait(false);
        foreach (SelectionDirectory directory in canonicalDirectories)
        {
            DirectoryTransferPlan plan = directoryPlans[directory.DirectoryId];
            orderedPlans.Add(("0\0" + directory.DisplayPath, plan));
            totalFiles = checked(totalFiles + plan.Entries.Count);
            totalBytes = SaturatingAdd(totalBytes, plan.TotalKnownBytes);
            lockedSkipped = checked(lockedSkipped + directory.LockedDescendantCount);
        }

        foreach (IGrouping<long, SelectedFile> group in standaloneFiles
                     .GroupBy(file => file.Directory.DirectoryId)
                     .OrderBy(group => group.First().Directory.DisplayPath, StringComparer.Ordinal))
        {
            SelectedFile first = group.First();
            var entries = group
                .OrderBy(file => file.Target.Filename, StringComparer.Ordinal)
                .Select(file => new DirectoryTransferEntry(file.Target, []))
                .ToArray();
            var plan = new DirectoryTransferPlan(
                PeerBrowsePath.Leaf(first.Directory.IdentityPath),
                entries);
            orderedPlans.Add(("1\0" + first.Directory.DisplayPath, plan));
            totalFiles = checked(totalFiles + plan.Entries.Count);
            totalBytes = SaturatingAdd(totalBytes, plan.TotalKnownBytes);
        }

        return new PeerBrowseDownloadResolution(
            orderedPlans
                .OrderBy(item => item.SortKey, StringComparer.Ordinal)
                .Select(item => item.Plan)
                .ToArray(),
            canonicalDirectories.Count,
            standaloneFiles.Count,
            totalFiles,
            totalBytes,
            redundant,
            lockedSkipped);
    }

    private static async Task CreateIdTableAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TEMP TABLE {tableName}(id INTEGER PRIMARY KEY); "
            + $"INSERT INTO {tableName}(id) SELECT CAST(value AS INTEGER) FROM json_each($ids);";
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool HasSameOrAncestor(HashSet<string> candidates, string identityPath)
    {
        if (candidates.Contains(identityPath))
            return true;
        for (int separator = identityPath.LastIndexOf('\\'); separator > 0;
             separator = identityPath.LastIndexOf('\\', separator - 1))
        {
            if (candidates.Contains(identityPath[..separator]))
                return true;
        }
        return false;
    }

    private static async Task<List<SelectedFile>> ReadSelectedFilesAsync(
        SqliteConnection connection,
        string username,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.file_id, f.wire_filename, f.size_bytes, f.extension,
                   f.bit_rate, f.bit_depth, f.sample_rate, f.length_seconds,
                   d.directory_id, d.identity_path, d.name, d.display_path,
                   f.visibility, d.visibility, d.locked_descendant_count,
                   a.attribute_type, a.attribute_value
            FROM files f
            JOIN selected_files s ON s.id = f.file_id
            JOIN directories d ON d.directory_id = f.directory_id
            LEFT JOIN file_attributes a ON a.file_id = f.file_id
            ORDER BY f.file_id, a.attribute_ordinal;
            """;
        var files = new List<SelectedFile>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await ReadPeerFileTargetsAsync(
            reader,
            username,
            attributeTypeOrdinal: 15,
            attributeValueOrdinal: 16,
            row => new SelectedFileContext(
                new SelectionDirectory(
                    row.GetInt64(8),
                    row.GetString(9),
                    row.GetString(10),
                    row.GetString(11),
                    (PeerBrowseEntryVisibility)row.GetInt32(13),
                    row.GetInt64(14)),
                (PeerBrowseEntryVisibility)row.GetInt32(12)),
            (target, context) => files.Add(new SelectedFile(
                target,
                context.Directory,
                context.Visibility)),
            cancellationToken).ConfigureAwait(false);
        return files;
    }

    private static async Task<IReadOnlyDictionary<long, DirectoryTransferPlan>> ReadPlansAsync(
        SqliteConnection connection,
        string username,
        IReadOnlyList<SelectionDirectory> roots,
        CancellationToken cancellationToken)
    {
        if (roots.Count == 0)
            return new Dictionary<long, DirectoryTransferPlan>();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE selected_subtrees(root_id, directory_id) AS (
                SELECT CAST(value AS INTEGER), CAST(value AS INTEGER)
                FROM json_each($root_ids)
                UNION ALL
                SELECT selected_subtrees.root_id, child.directory_id
                FROM selected_subtrees
                JOIN directories child ON child.parent_id = selected_subtrees.directory_id
            )
            SELECT f.file_id, f.wire_filename, f.size_bytes, f.extension,
                   f.bit_rate, f.bit_depth, f.sample_rate, f.length_seconds,
                   root.directory_id, root.identity_path, d.identity_path,
                   a.attribute_type, a.attribute_value
            FROM selected_subtrees selected
            JOIN directories root ON root.directory_id = selected.root_id
            JOIN directories d ON d.directory_id = selected.directory_id
            JOIN files f ON f.directory_id = d.directory_id
            LEFT JOIN file_attributes a ON a.file_id = f.file_id
            WHERE f.visibility = $public
            ORDER BY root.directory_id, f.file_id, a.attribute_ordinal;
            """;
        Add(command, "$root_ids", JsonSerializer.Serialize(roots.Select(root => root.DirectoryId)));
        Add(command, "$public", (int)PeerBrowseEntryVisibility.Public);

        var entriesByRoot = roots.ToDictionary(
            root => root.DirectoryId,
            static _ => new List<DirectoryTransferEntry>());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await ReadPeerFileTargetsAsync(
            reader,
            username,
            attributeTypeOrdinal: 11,
            attributeValueOrdinal: 12,
            static row => new PlanFileContext(row.GetInt64(8), row.GetString(9), row.GetString(10)),
            (target, context) =>
            {
                string[] relative = StringComparer.Ordinal.Equals(context.DirectoryPath, context.RootPath)
                    ? []
                    : context.DirectoryPath[(context.RootPath.Length + 1)..].Split('\\');
                entriesByRoot[context.RootId].Add(new DirectoryTransferEntry(target, relative));
            },
            cancellationToken).ConfigureAwait(false);

        var plans = new Dictionary<long, DirectoryTransferPlan>(roots.Count);
        foreach (SelectionDirectory root in roots)
        {
            List<DirectoryTransferEntry> entries = entriesByRoot[root.DirectoryId];
            if (entries.Count == 0)
                throw new PeerBrowseSelectionException(
                    $"Selected directory '{root.Name}' contains no downloadable public files.");
            plans.Add(root.DirectoryId, new DirectoryTransferPlan(PeerBrowsePath.Leaf(root.IdentityPath), entries));
        }
        return plans;
    }

    private static async Task ReadPeerFileTargetsAsync<TContext>(
        SqliteDataReader reader,
        string username,
        int attributeTypeOrdinal,
        int attributeValueOrdinal,
        Func<SqliteDataReader, TContext> readContext,
        Action<PeerFileTarget, TContext> addTarget,
        CancellationToken cancellationToken)
    {
        long? currentId = null;
        string? filename = null;
        long size = 0;
        string? extension = null;
        int? bitRate = null;
        int? bitDepth = null;
        int? sampleRate = null;
        int? length = null;
        TContext context = default!;
        List<FileAttributeSnapshot>? attributes = null;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long fileId = reader.GetInt64(0);
            if (currentId != fileId)
            {
                AddCurrent();
                currentId = fileId;
                filename = reader.GetString(1);
                size = reader.GetInt64(2);
                extension = reader.IsDBNull(3) ? null : reader.GetString(3);
                bitRate = NullableInt(reader, 4);
                bitDepth = NullableInt(reader, 5);
                sampleRate = NullableInt(reader, 6);
                length = NullableInt(reader, 7);
                context = readContext(reader);
                attributes = null;
            }

            if (!reader.IsDBNull(attributeTypeOrdinal))
            {
                int type = reader.GetInt32(attributeTypeOrdinal);
                attributes ??= [];
                attributes.Add(new FileAttributeSnapshot(
                    type.ToString(CultureInfo.InvariantCulture),
                    reader.GetInt32(attributeValueOrdinal),
                    type));
            }
        }
        AddCurrent();

        void AddCurrent()
        {
            if (currentId is null)
                return;
            addTarget(
                new PeerFileTarget(
                    new PeerFileIdentity(username, filename!),
                    size,
                    extension,
                    bitRate,
                    bitDepth,
                    sampleRate,
                    length,
                    attributes),
                context);
        }
    }

    public async Task<PeerBrowseResourcePage> ListAsync(
        string localAccount,
        string? username,
        PeerBrowseState? state,
        DateTimeOffset? afterCreatedAt,
        Guid? afterBrowseId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateResourcePage(afterCreatedAt, afterBrowseId, limit);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenRegistryAsync(SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ResourceSelect
            + " WHERE local_account = $local_account"
            + " AND (state IN ($queued_state, $running_state) OR expires_at_utc > $now)"
            + " AND ($username IS NULL OR username = $username)"
            + " AND ($state IS NULL OR state = $state)"
            + " AND ($after_created IS NULL OR created_at_utc < $after_created"
            + "      OR (created_at_utc = $after_created AND browse_id > $after_browse_id))"
            + " ORDER BY created_at_utc DESC, browse_id LIMIT $limit;";
        Add(command, "$local_account", localAccount);
        Add(command, "$queued_state", (int)PeerBrowseState.Queued);
        Add(command, "$running_state", (int)PeerBrowseState.Running);
        Add(command, "$now", Format(timeProvider.GetUtcNow()));
        Add(command, "$username", username);
        Add(command, "$state", state is null ? null : (int)state.Value);
        Add(command, "$after_created", afterCreatedAt is null ? null : Format(afterCreatedAt.Value));
        Add(command, "$after_browse_id", afterBrowseId?.ToString("D"));
        Add(command, "$limit", checked(limit + 1));
        var items = new List<PeerBrowseResource>(limit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(ReadResource(reader));
        bool hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        PeerBrowseResource? last = hasMore ? items[^1] : null;
        return new PeerBrowseResourcePage(
            items,
            last?.CreatedAt,
            last?.BrowseId);
    }

    public Task EvictAsync(CancellationToken cancellationToken = default)
        => EvictCoreAsync(preserveBrowseId: null, cancellationToken);

    private async Task EvictCoreAsync(
        Guid? preserveBrowseId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<(
            Guid Id,
            PeerBrowseState State,
            string? ArtifactFile,
            long Bytes,
            DateTimeOffset ExpiresAt)>();
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT browse_id, state, artifact_file, COALESCE(artifact_bytes, 0), expires_at_utc
                FROM browse_resources
                ORDER BY COALESCE(completed_at_utc, created_at_utc), browse_id;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((
                    Guid.Parse(reader.GetString(0)),
                    (PeerBrowseState)reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    Parse(reader.GetString(4))));
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        long totalBytes = 0;
        foreach (var candidate in candidates)
            totalBytes = SaturatingAdd(totalBytes, candidate.Bytes);
        int retainedArtifacts = candidates.Count(static candidate => candidate.ArtifactFile is not null);
        int retainedResources = candidates.Count;
        foreach (var candidate in candidates)
        {
            bool active = candidate.State is PeerBrowseState.Queued or PeerBrowseState.Running;
            bool expired = !active && candidate.ExpiresAt <= now;
            bool overCount = !active
                             && retainedResources > resourceCountTarget
                             && candidate.Id != preserveBrowseId;
            bool overBudget = totalBytes > artifactByteBudget
                              && candidate.ArtifactFile is not null
                              && candidate.Id != preserveBrowseId
                              && retainedArtifacts > 1;
            if (!expired && !overCount && !overBudget)
                continue;
            if (!TryBeginEviction(candidate.Id))
                continue;

            try
            {
                // Remove the registry entry first: a failed registry mutation must
                // never leave a completed resource pointing at a missing artifact.
                await DeleteResourceAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
                retainedResources--;
                if (candidate.ArtifactFile is not null)
                {
                    try
                    {
                        DeleteArtifactFile(candidate.ArtifactFile);
                    }
                    catch (IOException)
                    {
                        // The now-orphaned artifact is retried by startup cleanup.
                        LogCleanupFailure();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // The now-orphaned artifact is retried by startup cleanup.
                        LogCleanupFailure();
                    }
                    totalBytes = Math.Max(0, totalBytes - candidate.Bytes);
                    retainedArtifacts--;
                }
                if (overBudget)
                    LogBudgetEviction();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogCleanupFailure(exception);
            }
            finally
            {
                EndEviction(candidate.Id);
            }
        }
        PeerBrowseTelemetry.UpdateArtifacts(retainedArtifacts, totalBytes);
    }

    private async Task UpdateLifecycleAsync(
        Guid browseId,
        PeerBrowseState state,
        PeerBrowsePhase phase,
        PeerBrowseFailure? failure,
        CancellationToken cancellationToken,
        bool onlyWhileActive = false)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE browse_resources
            SET state = $state,
                phase = $phase,
                updated_at_utc = $updated,
                expires_at_utc = CASE WHEN $terminal = 1 THEN $expires ELSE expires_at_utc END,
                failure_code = $failure_code,
                failure_message = $failure_message,
                revision = revision + 1
            WHERE browse_id = $id
              AND ($only_active = 0 OR state IN ($queued, $running));
            """;
        Add(command, "$id", browseId.ToString("D"));
        Add(command, "$state", (int)state);
        Add(command, "$phase", (int)phase);
        Add(command, "$updated", Format(now));
        bool terminal = state is PeerBrowseState.Failed or PeerBrowseState.Cancelled;
        Add(command, "$terminal", terminal ? 1 : 0);
        Add(command, "$expires", Format(now + resourceRetention));
        Add(command, "$failure_code", failure?.Code);
        Add(command, "$failure_message", failure?.Message);
        Add(command, "$only_active", onlyWhileActive ? 1 : 0);
        Add(command, "$queued", (int)PeerBrowseState.Queued);
        Add(command, "$running", (int)PeerBrowseState.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0
            && !onlyWhileActive)
            throw new KeyNotFoundException($"Peer browse '{browseId}' does not exist.");
    }

    internal string GetStagingPath(Guid browseId)
        => Path.Combine(stagingDirectory, browseId.ToString("N") + ".sqlite.staging");

    internal async Task PromoteAsync(
        PeerBrowseResource resource,
        string stagingPath,
        PeerBrowseIndexProgress progress,
        CancellationToken cancellationToken)
    {
        string fileName = resource.BrowseId.ToString("N") + ".sqlite";
        string finalPath = Path.Combine(artifactDirectory, fileName);
        File.Move(stagingPath, finalPath, overwrite: false);
        OwnerOnlyFilePermissions.EnsureFile(finalPath);
        long artifactBytes = new FileInfo(finalPath).Length;
        DateTimeOffset now = timeProvider.GetUtcNow();

        try
        {
            await using SqliteConnection connection = await OpenRegistryAsync(
                SqliteOpenMode.ReadWrite,
                cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE browse_resources
                SET state = $complete,
                    phase = $ready,
                    directory_count = $directories,
                    file_count = $files,
                    total_file_bytes = $total_bytes,
                    updated_at_utc = $completed,
                    completed_at_utc = $completed,
                    expires_at_utc = $expires,
                    failure_code = NULL,
                    failure_message = NULL,
                    artifact_file = $artifact_file,
                    artifact_bytes = $artifact_bytes,
                    revision = revision + 1
                WHERE browse_id = $id AND state = $running;
                """;
            Add(command, "$id", resource.BrowseId.ToString("D"));
            Add(command, "$complete", (int)PeerBrowseState.Complete);
            Add(command, "$ready", (int)PeerBrowsePhase.Ready);
            Add(command, "$running", (int)PeerBrowseState.Running);
            Add(command, "$directories", progress.DirectoryCount);
            Add(command, "$files", progress.FileCount);
            Add(command, "$total_bytes", progress.TotalFileBytes);
            Add(command, "$completed", Format(now));
            Add(command, "$expires", Format(now + resourceRetention));
            Add(command, "$artifact_file", fileName);
            Add(command, "$artifact_bytes", artifactBytes);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new InvalidOperationException("The peer browse was no longer running when its artifact completed.");
        }
        catch
        {
            TryDeleteCleanupFile(finalPath);
            throw;
        }

        await EvictCoreAsync(resource.BrowseId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PeerBrowseResource> RequireCompleteAsync(Guid browseId, CancellationToken cancellationToken)
    {
        PeerBrowseResource? resource = await GetAsync(browseId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            throw new KeyNotFoundException($"Peer browse '{browseId}' has expired or does not exist.");
        if (resource.State != PeerBrowseState.Complete)
            throw new InvalidOperationException($"Peer browse '{browseId}' is not complete.");
        return resource;
    }

    internal async ValueTask<ArtifactLease> AcquireLeaseAsync(Guid browseId, CancellationToken cancellationToken = default)
    {
        lock (leaseGate)
        {
            if (evicting.Contains(browseId))
                throw new KeyNotFoundException($"Peer browse '{browseId}' has expired or does not exist.");
            leases[browseId] = leases.GetValueOrDefault(browseId) + 1;
        }

        try
        {
            string? artifactFile = await GetArtifactFileAsync(browseId, cancellationToken).ConfigureAwait(false);
            if (artifactFile is null)
                throw new KeyNotFoundException($"Peer browse '{browseId}' has expired or does not exist.");
            return new ArtifactLease(this, browseId, ResolveArtifactPath(artifactFile));
        }
        catch
        {
            ReleaseLease(browseId);
            throw;
        }
    }

    private void ReleaseLease(Guid browseId)
    {
        lock (leaseGate)
        {
            int count = leases.GetValueOrDefault(browseId);
            if (count <= 1)
                leases.Remove(browseId);
            else
                leases[browseId] = count - 1;
        }
    }

    private bool TryBeginEviction(Guid browseId)
    {
        lock (leaseGate)
        {
            if (leases.ContainsKey(browseId) || !evicting.Add(browseId))
                return false;
            return true;
        }
    }

    private void EndEviction(Guid browseId)
    {
        lock (leaseGate)
            evicting.Remove(browseId);
    }

    private async Task<string?> GetArtifactFileAsync(Guid browseId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_file FROM browse_resources WHERE browse_id = $id;";
        Add(command, "$id", browseId.ToString("D"));
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (string)value;
    }

    private string ResolveArtifactPath(string fileName)
    {
        if (!StringComparer.Ordinal.Equals(fileName, Path.GetFileName(fileName)))
            throw new InvalidDataException("Peer browse registry contains an invalid artifact filename.");
        string fullPath = Path.GetFullPath(Path.Combine(artifactDirectory, fileName));
        string expectedRoot = Path.GetFullPath(artifactDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Peer browse artifact escapes its storage root.");
        return fullPath;
    }

    private async Task DeleteResourceAsync(Guid browseId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenRegistryAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM browse_resources WHERE browse_id = $id;";
        Add(command, "$id", browseId.ToString("D"));
        int removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
            PublishResourceRemoved(browseId);
    }

    private void PublishResourceRemoved(Guid browseId)
    {
        Action<Guid>? handlers = ResourceRemoved;
        if (handlers is null)
            return;
        foreach (Action<Guid> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(browseId);
            }
            catch
            {
                // Observers do not own artifact cleanup.
            }
        }
    }

    private void DeleteArtifactFile(string fileName)
    {
        string path = ResolveArtifactPath(fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    private async Task RemoveOrphanArtifactsAsync(CancellationToken cancellationToken)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        await using (SqliteConnection connection = await OpenRegistryAsync(
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT artifact_file FROM browse_resources WHERE artifact_file IS NOT NULL;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                retained.Add(reader.GetString(0));
        }

        foreach (string path in Directory.EnumerateFiles(artifactDirectory, "*.sqlite", SearchOption.TopDirectoryOnly))
        {
            if (!retained.Contains(Path.GetFileName(path)))
                TryDeleteCleanupFile(path);
        }
    }

    private void DeleteFiles(string directory, string pattern)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                TryDeleteCleanupFile(path);
        }
        catch (IOException exception)
        {
            LogCleanupFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogCleanupFailure(exception);
        }
    }

    private void TryDeleteCleanupFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            LogCleanupFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogCleanupFailure(exception);
        }
    }

    internal void DeleteStagingBestEffort(string path)
        => TryDeleteCleanupFile(path);

    private async ValueTask<SqliteConnection> OpenRegistryAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = registryPath,
            Mode = mode,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async ValueTask<SqliteConnection> OpenArtifactAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction<string?, string?, bool>(
            "ordinal_contains",
            static (value, query) => value is not null
                && query is not null
                && value.Contains(query, StringComparison.OrdinalIgnoreCase),
            isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>(
            "ordinal_same_or_descendant",
            static (candidate, root) => candidate is not null
                && root is not null
                && PeerBrowsePath.IsSameOrDescendant(candidate, root),
            isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>(
            "ordinal_descendant",
            static (candidate, root) => candidate is not null
                && root is not null
                && PeerBrowsePath.IsDescendant(candidate, root),
            isDeterministic: true);
        return connection;
    }

    private static async Task<PeerBrowseResource?> ReadResourceAsync(
        SqliteConnection connection,
        Guid browseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ResourceSelect + " WHERE browse_id = $id;";
        Add(command, "$id", browseId.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadResource(reader)
            : null;
    }

    private static PeerBrowseResource ReadResource(SqliteDataReader reader)
    {
        string? failureCode = reader.IsDBNull(16) ? null : reader.GetString(16);
        return new PeerBrowseResource(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            (PeerBrowseState)reader.GetInt32(3),
            (PeerBrowsePhase)reader.GetInt32(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            Parse(reader.GetString(10)),
            Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : Parse(reader.GetString(12)),
            Parse(reader.GetString(13)),
            failureCode is null
                ? null
                : new PeerBrowseFailure(failureCode, reader.IsDBNull(17) ? "" : reader.GetString(17)),
            reader.GetInt64(18));
    }

    private const string ResourceSelect =
        """
        SELECT browse_id, local_account, username, state, phase,
               compressed_bytes_received, compressed_bytes_expected,
               directory_count, file_count,
               total_file_bytes, created_at_utc, updated_at_utc,
               completed_at_utc, expires_at_utc, artifact_file, artifact_bytes,
               failure_code, failure_message, revision
        FROM browse_resources
        """;

    internal static string NormalizeIdentityPath(string wirePath)
        => PeerBrowsePath.NormalizeDirectoryIdentity(wirePath);

    internal static string DisplayPath(string identityPath)
        => PeerBrowsePath.ToDisplayPath(identityPath);

    private static int? NullableInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static void ValidatePage(string? afterSortKey, long? afterId, int limit)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be between 1 and 500.");
        if ((afterSortKey is null) != (afterId is null))
            throw new ArgumentException("Both cursor sort key and ID must be supplied together.");
    }

    private static void ValidateResourcePage(
        DateTimeOffset? afterCreatedAt,
        Guid? afterBrowseId,
        int limit)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be between 1 and 500.");
        if ((afterCreatedAt is null) != (afterBrowseId is null))
            throw new ArgumentException("Both resource cursor values must be supplied together.");
    }

    private static PeerBrowsePage<T> ToPage<T>(
        List<T> items,
        int limit,
        Func<T, string> sortKey,
        Func<T, long> id)
        where T : class
    {
        bool hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        T? last = hasMore ? items[^1] : default;
        return new PeerBrowsePage<T>(
            items,
            last is null ? null : sortKey(last),
            last is null ? null : id(last));
    }

    private static PeerBrowseDirectoryEntry ReadDirectoryEntry(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            (PeerBrowseEntryVisibility)reader.GetInt32(4),
            reader.GetInt32(5) != 0,
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt32(11) != 0);

    private const string DirectorySelect =
        """
        SELECT directory_id, parent_id, name, display_path, visibility,
               is_synthetic, direct_directory_count, direct_file_count,
               recursive_file_count, recursive_file_bytes,
               locked_descendant_count, has_children
        FROM directories
        """;

    private sealed record SelectionDirectory(
        long DirectoryId,
        string IdentityPath,
        string Name,
        string DisplayPath,
        PeerBrowseEntryVisibility Visibility,
        long LockedDescendantCount);

    private sealed record SelectedFile(
        PeerFileTarget Target,
        SelectionDirectory Directory,
        PeerBrowseEntryVisibility Visibility);

    private sealed record SelectedFileContext(
        SelectionDirectory Directory,
        PeerBrowseEntryVisibility Visibility);

    private readonly record struct PlanFileContext(
        long RootId,
        string RootPath,
        string DirectoryPath);

    private static async Task<bool> HasBrowseResourceColumnAsync(
        SqliteConnection connection,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(browse_resources);";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                return true;
        }
        return false;
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

    internal static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    internal static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private void LogBudgetEviction()
    {
        if (!TryLog(ref lastBudgetLogTick))
            return;
        PersistenceLogMessages.BrowseArtifactEvicted(logger);
    }

    private void LogCleanupFailure(Exception? exception = null)
    {
        if (!TryLog(ref lastCleanupLogTick))
            return;
        if (exception is null)
            PersistenceLogMessages.BrowseCleanupFailed(logger);
        else
            PersistenceLogMessages.BrowseCleanupFailed(logger, exception);
    }

    private static bool TryLog(ref long previous)
    {
        long now = Environment.TickCount64;
        long observed = Volatile.Read(ref previous);
        if (observed != long.MinValue && now - observed < 60_000)
            return false;
        return Interlocked.CompareExchange(ref previous, now, observed) == observed;
    }

    public sealed class ArtifactLease : IAsyncDisposable
    {
        private PeerBrowseArtifactStore? owner;

        internal ArtifactLease(PeerBrowseArtifactStore owner, Guid browseId, string artifactPath)
        {
            this.owner = owner;
            BrowseId = browseId;
            ArtifactPath = artifactPath;
        }

        public Guid BrowseId { get; }
        internal string ArtifactPath { get; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref owner, null)?.ReleaseLease(BrowseId);
            return ValueTask.CompletedTask;
        }
    }
}
