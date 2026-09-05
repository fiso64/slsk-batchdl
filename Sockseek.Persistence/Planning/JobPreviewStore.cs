using Microsoft.Data.Sqlite;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Planning;

public sealed record StoredJobPreview(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string State,
    long Revision,
    int NodeCount,
    int ReadyNodeCount,
    int FailedNodeCount,
    int SelectableNodeCount,
    Guid? CommittedSubmissionId);

public sealed record StoredJobPreviewNode(
    Guid PreviewId,
    long Ordinal,
    string Ref,
    string? ParentRef,
    string Role,
    string State,
    bool IsSelectable,
    string Kind,
    string? ItemName,
    string? QueryText,
    int DirectChildCount,
    string AppliedAutoProfilesJson,
    string? SpecificationJson,
    string? EffectiveSettingsRef,
    string? EffectiveSettingsJson,
    string? CredentialBindingsJson,
    string? FailureCode,
    string? FailureMessage);

public sealed record StoredPreviewCommit(
    StoredJobPreview Preview,
    string Mode,
    IReadOnlyList<StoredJobPreviewNode> SelectedNodes,
    int FailedNodeCount,
    int MissingRequestedRefCount);

public sealed record StoredJobPreviewWork(Guid PreviewId, string RequestJson);
public sealed record StoredJobPreviewCleanup(Guid PreviewId, string RequestJson);

/// <summary>
/// Separate optional SQLite resource for expiring plans. It is
/// deliberately outside runtime history so Review can fail independently of
/// direct Start and local planning.
/// </summary>
public sealed class JobPreviewStore(
    string databasePath,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    public const int MaximumPageSize = 200;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = 5,
    }.ToString();
    private int initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
            return;
        string path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The job-preview database path has no parent directory.");
        Directory.CreateDirectory(directory);
        PersistenceFilePrivacy.RestrictDirectory(directory);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS job_previews (
                    id TEXT PRIMARY KEY,
                    created_at_utc INTEGER NOT NULL,
                    expires_at_utc INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    node_count INTEGER NOT NULL,
                    ready_node_count INTEGER NOT NULL,
                    failed_node_count INTEGER NOT NULL,
                    selectable_node_count INTEGER NOT NULL,
                    next_ordinal INTEGER NOT NULL,
                    request_json TEXT NOT NULL,
                    committed_submission_id TEXT NULL,
                    CHECK (revision >= 0),
                    CHECK (node_count >= 0 AND ready_node_count >= 0 AND failed_node_count >= 0),
                    CHECK (selectable_node_count >= 0 AND next_ordinal >= 0)
                );
                CREATE TABLE IF NOT EXISTS job_preview_nodes (
                    preview_id TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    node_ref TEXT NOT NULL,
                    parent_ref TEXT NULL,
                    role TEXT NOT NULL,
                    state TEXT NOT NULL,
                    is_selectable INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    item_name TEXT NULL,
                    query_text TEXT NULL,
                    direct_child_count INTEGER NOT NULL,
                    applied_auto_profiles_json TEXT NOT NULL,
                    specification_json TEXT NULL,
                    effective_settings_ref TEXT NULL,
                    failure_code TEXT NULL,
                    failure_message TEXT NULL,
                    PRIMARY KEY (preview_id, node_ref),
                    UNIQUE (preview_id, ordinal),
                    FOREIGN KEY (preview_id) REFERENCES job_previews(id) ON DELETE CASCADE,
                    FOREIGN KEY (preview_id, effective_settings_ref)
                        REFERENCES job_preview_effective_settings(preview_id, settings_ref)
                );
                CREATE TABLE IF NOT EXISTS job_preview_effective_settings (
                    preview_id TEXT NOT NULL,
                    settings_ref TEXT NOT NULL,
                    settings_json TEXT NOT NULL,
                    credential_bindings_json TEXT NOT NULL,
                    PRIMARY KEY (preview_id, settings_ref),
                    UNIQUE (preview_id, settings_json, credential_bindings_json),
                    FOREIGN KEY (preview_id) REFERENCES job_previews(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_job_preview_nodes_parent
                    ON job_preview_nodes(preview_id, parent_ref, ordinal);
                DROP TABLE IF EXISTS job_preview_selection_entries;
                DROP TABLE IF EXISTS job_preview_selections;
                """, cancellationToken).ConfigureAwait(false);
            if (!await HasColumnAsync(
                    connection,
                    "job_previews",
                    "request_json",
                    cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE job_previews ADD COLUMN request_json TEXT NOT NULL DEFAULT '{}';",
                    cancellationToken).ConfigureAwait(false);
            }
            PersistenceFilePrivacy.RestrictFile(path);
        }
        catch
        {
            Interlocked.Exchange(ref initialized, 0);
            throw;
        }
    }

    public async Task<StoredJobPreview> CreateAsync(
        string requestJson,
        TimeSpan? retention = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(requestJson))
            throw new ArgumentException("A preview request is required.", nameof(requestJson));
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expires = now + (retention ?? DefaultRetention);
        Guid id = Guid.NewGuid();
        await WithWriteAsync(async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO job_previews (
                    id, created_at_utc, expires_at_utc, state, revision,
                    node_count, ready_node_count, failed_node_count,
                    selectable_node_count, next_ordinal, request_json)
                VALUES ($id, $created, $expires, 'Planning', 1, 0, 0, 0, 0, 0, $request);
                """;
            Add(command, "$id", id);
            Add(command, "$created", now);
            Add(command, "$expires", expires);
            Add(command, "$request", requestJson);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return new(id, now, expires, "Planning", 1, 0, 0, 0, 0, null);
    }

    public async Task<IReadOnlyList<StoredJobPreviewWork>> GetPlanningWorkAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, request_json FROM job_previews
            WHERE state = 'Planning' AND expires_at_utc > $now
            ORDER BY created_at_utc, id;
            """;
        Add(command, "$now", clock.GetUtcNow());
        var work = new List<StoredJobPreviewWork>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            work.Add(new(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1)));
        }
        return work;
    }

    public async Task<StoredJobPreviewWork?> GetPlanningWorkAsync(
        Guid previewId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, request_json FROM job_previews
            WHERE id = $id AND state = 'Planning' AND expires_at_utc > $now;
            """;
        Add(command, "$id", previewId);
        Add(command, "$now", clock.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredJobPreviewWork(Guid.Parse(reader.GetString(0)), reader.GetString(1))
            : null;
    }

    public async Task AppendNodesAsync(
        Guid previewId,
        IReadOnlyList<StoredJobPreviewNode> nodes,
        CancellationToken cancellationToken = default)
    {
        if (nodes.Count == 0)
            return;
        EnsureInitialized();
        await WithWriteAsync(async (connection, transaction, ct) =>
        {
            long ordinal = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT next_ordinal FROM job_previews WHERE id = $id AND state = 'Planning';",
                ("$id", previewId),
                ct).ConfigureAwait(false);
            int ready = 0;
            int failed = 0;
            int selectable = 0;
            foreach (StoredJobPreviewNode node in nodes)
            {
                if (node.SpecificationJson != null)
                {
                    if (node.EffectiveSettingsRef == null
                        || node.EffectiveSettingsJson == null
                        || node.CredentialBindingsJson == null)
                    {
                        throw new InvalidDataException(
                            "A retained preview specification must reference effective settings.");
                    }
                    await InsertEffectiveSettingsAsync(
                        connection,
                        transaction,
                        previewId,
                        node,
                        ct).ConfigureAwait(false);
                }
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO job_preview_nodes (
                        preview_id, ordinal, node_ref, parent_ref, role, state,
                        is_selectable, kind, item_name, query_text, direct_child_count,
                        applied_auto_profiles_json, specification_json,
                        effective_settings_ref, failure_code,
                        failure_message)
                    VALUES (
                        $preview, $ordinal, $ref, $parent, $role, $state,
                        $selectable, $kind, $item, $query, $children,
                        $profiles, $specification, $settingsRef,
                        $failureCode, $failureMessage);
                    """;
                Add(command, "$preview", previewId);
                Add(command, "$ordinal", ordinal++);
                Add(command, "$ref", node.Ref);
                Add(command, "$parent", node.ParentRef);
                Add(command, "$role", node.Role);
                Add(command, "$state", node.State);
                Add(command, "$selectable", node.IsSelectable ? 1 : 0);
                Add(command, "$kind", node.Kind);
                Add(command, "$item", node.ItemName);
                Add(command, "$query", node.QueryText);
                Add(command, "$children", node.DirectChildCount);
                Add(command, "$profiles", node.AppliedAutoProfilesJson);
                Add(command, "$specification", node.SpecificationJson);
                Add(command, "$settingsRef", node.EffectiveSettingsRef);
                Add(command, "$failureCode", node.FailureCode);
                Add(command, "$failureMessage", Limit(node.FailureMessage));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (node.State == "Ready") ready++; else failed++;
                if (node.IsSelectable) selectable++;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE job_previews SET
                    node_count = node_count + $count,
                    ready_node_count = ready_node_count + $ready,
                    failed_node_count = failed_node_count + $failed,
                    selectable_node_count = selectable_node_count + $selectable,
                    next_ordinal = $next,
                    revision = revision + 1
                WHERE id = $id AND state = 'Planning';
                """;
            Add(update, "$count", nodes.Count);
            Add(update, "$ready", ready);
            Add(update, "$failed", failed);
            Add(update, "$selectable", selectable);
            Add(update, "$next", ordinal);
            Add(update, "$id", previewId);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The job preview is no longer planning.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredJobPreview> CompleteAsync(
        Guid previewId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await WithWriteAsync(async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE job_previews SET
                    state = CASE
                        WHEN ready_node_count = 0 THEN 'Failed'
                        WHEN failed_node_count > 0 THEN 'PartiallyReady'
                        ELSE 'Ready'
                    END,
                    revision = revision + 1
                WHERE id = $id AND state = 'Planning';
                """;
            Add(command, "$id", previewId);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The job preview is not planning.");
        }, cancellationToken).ConfigureAwait(false);
        return await GetRequiredPreviewAsync(previewId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredJobPreview?> GetPreviewAsync(
        Guid previewId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_previews WHERE id = $id;";
        Add(command, "$id", previewId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPreview(reader)
            : null;
    }

    public async Task<IReadOnlyList<StoredJobPreviewNode>> GetNodesAsync(
        Guid previewId,
        string? parentRef,
        long afterOrdinal,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Preview node page size must be between 1 and {MaximumPageSize}.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = parentRef == null
            ? """
                SELECT n.*, settings.settings_json AS effective_settings_json,
                       settings.credential_bindings_json AS credential_bindings_json
                FROM job_preview_nodes n
                LEFT JOIN job_preview_effective_settings settings
                  ON settings.preview_id = n.preview_id
                 AND settings.settings_ref = n.effective_settings_ref
                WHERE n.preview_id = $preview AND n.parent_ref IS NULL AND n.ordinal > $after
                ORDER BY ordinal LIMIT $limit;
                """
            : """
                SELECT n.*, settings.settings_json AS effective_settings_json,
                       settings.credential_bindings_json AS credential_bindings_json
                FROM job_preview_nodes n
                LEFT JOIN job_preview_effective_settings settings
                  ON settings.preview_id = n.preview_id
                 AND settings.settings_ref = n.effective_settings_ref
                WHERE n.preview_id = $preview AND n.parent_ref = $parent AND n.ordinal > $after
                ORDER BY ordinal LIMIT $limit;
                """;
        Add(command, "$preview", previewId);
        Add(command, "$parent", parentRef);
        Add(command, "$after", afterOrdinal);
        Add(command, "$limit", limit);
        var rows = new List<StoredJobPreviewNode>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadNode(reader));
        return rows;
    }

    public async Task<StoredPreviewCommit?> ResolveCommitAsync(
        Guid previewId,
        long revision,
        string mode,
        IReadOnlySet<string> refs,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (mode is not ("Only" or "AllExcept"))
            throw new ArgumentException("Selection mode must be Only or AllExcept.", nameof(mode));
        ArgumentNullException.ThrowIfNull(refs);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        StoredJobPreview? preview = await ReadPreviewAsync(connection, previewId, cancellationToken).ConfigureAwait(false);
        if (preview == null)
            return null;
        if (preview.ExpiresAtUtc <= clock.GetUtcNow())
            throw new InvalidOperationException("The job preview has expired.");
        if (preview.State is not ("Ready" or "PartiallyReady"))
            throw new InvalidOperationException("The job preview is not available for commit.");
        if (revision != preview.Revision)
            throw new InvalidOperationException("The job preview revision is stale.");

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.*, settings.settings_json AS effective_settings_json,
                   settings.credential_bindings_json AS credential_bindings_json
            FROM job_preview_nodes n
            LEFT JOIN job_preview_effective_settings settings
              ON settings.preview_id = n.preview_id
             AND settings.settings_ref = n.effective_settings_ref
            WHERE n.preview_id = $preview AND n.state = 'Ready' AND n.is_selectable = 1
            ORDER BY n.ordinal;
            """;
        Add(command, "$preview", previewId);
        var selected = new List<StoredJobPreviewNode>();
        var found = mode == "Only" ? new HashSet<string>(StringComparer.Ordinal) : null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredJobPreviewNode node = ReadNode(reader);
            bool include = mode == "Only" ? refs.Contains(node.Ref) : !refs.Contains(node.Ref);
            if (!include)
                continue;
            selected.Add(node);
            found?.Add(node.Ref);
        }
        int missing = mode == "Only" ? refs.Count - found!.Count : 0;
        return new StoredPreviewCommit(
            preview,
            mode,
            selected,
            preview.FailedNodeCount,
            missing);
    }

    public async Task<bool> MarkCommittedAsync(
        Guid previewId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WithWriteAsync(async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE job_previews SET state = 'Committed',
                    committed_submission_id = $submission, revision = revision + 1
                WHERE id = $preview AND state = 'Committing'
                    AND committed_submission_id = $submission;
                """;
            Add(command, "$submission", submissionId);
            Add(command, "$preview", previewId);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<StoredJobPreviewCleanup?> DeleteCommittedAsync(
        Guid previewId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
        => WithWriteAsync<StoredJobPreviewCleanup?>(async (connection, transaction, ct) =>
        {
            string? requestJson;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT request_json FROM job_previews
                    WHERE id = $preview AND state = 'Committed'
                        AND committed_submission_id = $submission;
                    """;
                Add(select, "$preview", previewId);
                Add(select, "$submission", submissionId);
                requestJson = Convert.ToString(
                    await select.ExecuteScalarAsync(ct).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            if (requestJson == null)
                return null;
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM job_previews WHERE id = $preview;";
            Add(delete, "$preview", previewId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new StoredJobPreviewCleanup(previewId, requestJson);
        }, cancellationToken);

    public async Task<bool> TryBeginCommitAsync(
        Guid previewId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WithWriteAsync(async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE job_previews SET state = 'Committing',
                    committed_submission_id = $submission, revision = revision + 1
                WHERE id = $preview AND state IN ('Ready', 'PartiallyReady')
                    AND expires_at_utc > $now;
                """;
            Add(command, "$submission", submissionId);
            Add(command, "$preview", previewId);
            Add(command, "$now", clock.GetUtcNow());
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseCommitAsync(
        Guid previewId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WithWriteAsync(async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE job_previews SET
                    state = CASE WHEN failed_node_count > 0 THEN 'PartiallyReady' ELSE 'Ready' END,
                    committed_submission_id = NULL,
                    revision = revision + 1
                WHERE id = $preview AND state = 'Committing'
                    AND committed_submission_id = $submission;
                """;
            Add(command, "$preview", previewId);
            Add(command, "$submission", submissionId);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> ExpireDueAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await WithWriteAsync(async (connection, transaction, ct) =>
        {
            var ids = new List<Guid>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT id FROM job_previews
                    WHERE expires_at_utc <= $now
                        AND state IN ('Planning', 'Ready', 'PartiallyReady', 'Failed')
                    ORDER BY created_at_utc, id;
                    """;
                Add(select, "$now", clock.GetUtcNow());
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    ids.Add(Guid.Parse(reader.GetString(0)));
            }
            if (ids.Count == 0)
                return ids;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE job_previews SET state = 'Expired', revision = revision + 1
                    WHERE expires_at_utc <= $now
                        AND state IN ('Planning', 'Ready', 'PartiallyReady', 'Failed');
                    DELETE FROM job_preview_nodes
                    WHERE preview_id IN (SELECT id FROM job_previews WHERE state = 'Expired');
                    DELETE FROM job_preview_effective_settings
                    WHERE preview_id IN (SELECT id FROM job_previews WHERE state = 'Expired');
                    """;
                Add(update, "$now", clock.GetUtcNow());
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return ids;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredJobPreviewCleanup>> PruneTombstonesAsync(
        TimeSpan expiredTombstoneRetention,
        TimeSpan committedRetention,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (expiredTombstoneRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiredTombstoneRetention));
        if (committedRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(committedRetention));
        return await WithWriteAsync(async (connection, transaction, ct) =>
        {
            DateTimeOffset now = clock.GetUtcNow();
            var rows = new List<StoredJobPreviewCleanup>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT id, request_json FROM job_previews
                    WHERE (state = 'Expired' AND expires_at_utc <= $expiredBefore)
                       OR (state = 'Committed' AND created_at_utc <= $committedBefore)
                    ORDER BY created_at_utc, id;
                    """;
                Add(select, "$expiredBefore", now - expiredTombstoneRetention);
                Add(select, "$committedBefore", now - committedRetention);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
            }
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM job_previews
                WHERE (state = 'Expired' AND expires_at_utc <= $expiredBefore)
                   OR (state = 'Committed' AND created_at_utc <= $committedBefore);
                """;
            Add(delete, "$expiredBefore", now - expiredTombstoneRetention);
            Add(delete, "$committedBefore", now - committedRetention);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return rows;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredJobPreview> GetRequiredPreviewAsync(Guid id, CancellationToken cancellationToken)
        => await GetPreviewAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The job preview was not found.");

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task WithWriteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => await WithWriteAsync(async (connection, transaction, ct) =>
        {
            await action(connection, transaction, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private async Task<T> WithWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
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

    private static async Task<StoredJobPreview?> ReadPreviewAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_previews WHERE id = $id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPreview(reader)
            : null;
    }

    private static StoredJobPreview ReadPreview(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at_utc"))),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("expires_at_utc"))),
            reader.GetString(reader.GetOrdinal("state")),
            reader.GetInt64(reader.GetOrdinal("revision")),
            reader.GetInt32(reader.GetOrdinal("node_count")),
            reader.GetInt32(reader.GetOrdinal("ready_node_count")),
            reader.GetInt32(reader.GetOrdinal("failed_node_count")),
            reader.GetInt32(reader.GetOrdinal("selectable_node_count")),
            reader.IsDBNull(reader.GetOrdinal("committed_submission_id"))
                ? null
                : Guid.Parse(reader.GetString(reader.GetOrdinal("committed_submission_id"))));

    private static StoredJobPreviewNode ReadNode(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(reader.GetOrdinal("preview_id"))),
            reader.GetInt64(reader.GetOrdinal("ordinal")),
            reader.GetString(reader.GetOrdinal("node_ref")),
            Text(reader, "parent_ref"),
            reader.GetString(reader.GetOrdinal("role")),
            reader.GetString(reader.GetOrdinal("state")),
            reader.GetInt64(reader.GetOrdinal("is_selectable")) != 0,
            reader.GetString(reader.GetOrdinal("kind")),
            Text(reader, "item_name"),
            Text(reader, "query_text"),
            reader.GetInt32(reader.GetOrdinal("direct_child_count")),
            reader.GetString(reader.GetOrdinal("applied_auto_profiles_json")),
            Text(reader, "specification_json"),
            Text(reader, "effective_settings_ref"),
            Text(reader, "effective_settings_json"),
            Text(reader, "credential_bindings_json"),
            Text(reader, "failure_code"),
            Text(reader, "failure_message"));

    private static async Task InsertEffectiveSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid previewId,
        StoredJobPreviewNode node,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO job_preview_effective_settings (
                preview_id, settings_ref, settings_json, credential_bindings_json)
            VALUES ($preview, $ref, $settings, $bindings);
            """;
        Add(command, "$preview", previewId);
        Add(command, "$ref", node.EffectiveSettingsRef);
        Add(command, "$settings", node.EffectiveSettingsJson);
        Add(command, "$bindings", node.CredentialBindingsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = """
            SELECT COUNT(*) FROM job_preview_effective_settings
            WHERE preview_id = $preview AND settings_ref = $ref
              AND settings_json = $settings
              AND credential_bindings_json = $bindings;
            """;
        Add(verify, "$preview", previewId);
        Add(verify, "$ref", node.EffectiveSettingsRef);
        Add(verify, "$settings", node.EffectiveSettingsJson);
        Add(verify, "$bindings", node.CredentialBindingsJson);
        if (Convert.ToInt64(await verify.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidDataException(
                "A job-preview effective-settings ref resolved to different content.");
        }
    }

    private static string? Text(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object? Value) parameter,
        CancellationToken cancellationToken)
        => await ScalarLongAsync(connection, transaction, sql, [parameter], cancellationToken).ConfigureAwait(false);

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object? Value) first,
        (string Name, object? Value) second,
        CancellationToken cancellationToken)
        => await ScalarLongAsync(connection, transaction, sql, [first, second], cancellationToken).ConfigureAwait(false);

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            Add(command, parameter.Name, parameter.Value);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value == null || value == DBNull.Value)
            throw new KeyNotFoundException("The job preview was not found or is no longer mutable.");
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
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

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value switch
        {
            null => DBNull.Value,
            Guid id => id.ToString("D"),
            DateTimeOffset time => time.ToUniversalTime().ToUnixTimeMilliseconds(),
            _ => value,
        });

    private static string? Limit(string? value)
        => value == null || value.Length <= 2048 ? value : value[..2048];

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new InvalidOperationException("The job-preview store is unavailable.");
    }

    public ValueTask DisposeAsync()
    {
        writeGate.Dispose();
        SqliteConnectionPool.Clear(connectionString);
        return ValueTask.CompletedTask;
    }
}
