using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class PersistenceLoadTests
{
    [TestMethod]
    [TestCategory("Load")]
    public async Task HundredThousandJobsAndTransfers_StayCursorBoundedAndUseListingIndexes()
    {
        await using var database = new LoadDatabase();
        await database.Initializer.InitializeAsync();
        Guid runtimeId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        await SeedRuntimeAsync(database, runtimeId);

        await ExecuteAsync(database, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000)
            INSERT INTO jobs (
                id, workflow_id, parent_job_id, source_job_id, result_job_id,
                last_runtime_id, last_sequence, display_id, kind, lifecycle_state,
                activity_phase, terminal_outcome, skip_reason, cancellation_source,
                failure_reason, created_at_utc, started_at_utc, updated_at_utc,
                completed_at_utc, revision, payload_schema_version)
            SELECT printf('10000000-0000-0000-0000-%012x', x), $workflow, NULL, NULL, NULL,
                $runtime, x, x, 'Song', 'Terminal', 'None', 'Succeeded', 'None', 'None',
                'None', x, x, x, x, 2, 1
            FROM n;
            """, ("$workflow", SqlGuid(workflowId)), ("$runtime", SqlGuid(runtimeId)));

        await ExecuteAsync(database, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000)
            INSERT INTO transfers (
                id, job_id, workflow_id, last_runtime_id, last_sequence, direction,
                source, username, remote_path, local_path, state, terminal_outcome,
                total_bytes, transferred_bytes, attempt_count, created_at_utc,
                started_at_utc, completed_at_utc, failure_reason, revision)
            SELECT printf('20000000-0000-0000-0000-%012x', x), NULL, $workflow, $runtime, x,
                'Download', 'SoulseekPeer', 'peer', 'remote', 'local', 'Completed', 'Succeeded',
                100, 100, 1, x, x, x, 'None', 2
            FROM n;
            """, ("$workflow", SqlGuid(workflowId)), ("$runtime", SqlGuid(runtimeId)));

        var stopwatch = Stopwatch.StartNew();
        var jobs = new JobHistoryReader(database.Factory);
        var firstJobs = await jobs.GetJobsAsync(new JobHistoryQuery(Limit: 100, WorkflowId: workflowId, IncludeAll: true));
        var secondJobs = await jobs.GetJobsAsync(new JobHistoryQuery(firstJobs.NextCursor, 100, WorkflowId: workflowId, IncludeAll: true));
        var transfers = new TransferHistoryReader(database.Factory);
        var firstTransfers = await transfers.GetTransfersAsync(new TransferHistoryQuery(Limit: 100, WorkflowId: workflowId));
        var secondTransfers = await transfers.GetTransfersAsync(new TransferHistoryQuery(firstTransfers.NextCursor, 100, WorkflowId: workflowId));
        stopwatch.Stop();

        Assert.AreEqual(100, firstJobs.Items.Count);
        Assert.AreEqual(100, secondJobs.Items.Count);
        Assert.AreEqual(200, firstJobs.Items.Concat(secondJobs.Items).Select(job => job.Id).Distinct().Count());
        Assert.AreEqual(100, firstTransfers.Items.Count);
        Assert.AreEqual(100, secondTransfers.Items.Count);
        Assert.AreEqual(200, firstTransfers.Items.Concat(secondTransfers.Items).Select(item => item.Id).Distinct().Count());
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Two bounded pages per resource took {stopwatch.Elapsed}.");

        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM jobs WHERE workflow_id = $workflow ORDER BY display_id, id LIMIT 101",
            ("$workflow", SqlGuid(workflowId))), "IX_jobs_workflow_id_display_id_id");
        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM jobs WHERE parent_job_id = $parent ORDER BY display_id, id LIMIT 101",
            ("$parent", SqlGuid(Guid.NewGuid()))), "IX_jobs_parent_job_id_display_id_id");
        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM transfers WHERE workflow_id = $workflow ORDER BY created_at_utc, id LIMIT 101",
            ("$workflow", SqlGuid(workflowId))), "IX_transfers_workflow_id_created_at_utc_id");
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task HundredThousandSearchResults_StaySequenceBoundedAndUsePaginationIndex()
    {
        await using var database = new LoadDatabase();
        await database.Initializer.InitializeAsync();
        Guid runtimeId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid searchId = Guid.NewGuid();
        await SeedRuntimeAsync(database, runtimeId);
        await ExecuteAsync(database, """
            INSERT INTO jobs (
                id, workflow_id, last_runtime_id, last_sequence, display_id, kind,
                lifecycle_state, activity_phase, terminal_outcome, skip_reason,
                cancellation_source, failure_reason, created_at_utc, started_at_utc,
                updated_at_utc, completed_at_utc, revision, payload_schema_version)
            VALUES ($search, $workflow, $runtime, 100001, 1, 'Search', 'Terminal', 'None',
                'Succeeded', 'None', 'None', 'None', 1, 1, 100001, 100001, 100001, 1);
            INSERT INTO search_jobs (
                job_id, query, revision, result_count, locked_file_count, is_complete,
                completed_at_utc, result_persistence_state)
            VALUES ($search, 'large search', 100001, 100000, 0, 1, 100001, 'Complete');
            """, ("$search", SqlGuid(searchId)), ("$workflow", SqlGuid(workflowId)), ("$runtime", SqlGuid(runtimeId)));
        await ExecuteAsync(database, """
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000)
            INSERT INTO search_results (
                id, search_job_id, sequence, revision, username, remote_filename,
                size_bytes, response_file_count, extension, observed_at_utc)
            SELECT printf('30000000-0000-0000-0000-%012x', x), $search, x, x,
                'peer-' || x, 'Music\\file-' || x || '.mp3', 100, 1, '.mp3', x
            FROM n;
            """, ("$search", SqlGuid(searchId)));

        var reader = new SearchHistoryReader(database.Factory);
        var first = await reader.GetRawResultsAsync(searchId, 0, 200);
        var tail = await reader.GetRawResultsAsync(searchId, 99_950, 100);
        Assert.IsNotNull(first);
        Assert.AreEqual(200, first.Items.Count);
        Assert.AreEqual(200L, first.NextSequence);
        Assert.IsNotNull(tail);
        Assert.AreEqual(50, tail.Items.Count);
        Assert.IsNull(tail.NextSequence);
        CollectionAssert.AreEqual(
            Enumerable.Range(99_951, 50).Select(value => (long)value).ToArray(),
            tail.Items.Select(item => item.Sequence).ToArray());

        StringAssert.Contains(await QueryPlanAsync(database,
            "SELECT id FROM search_results WHERE search_job_id = $search AND sequence > 50000 ORDER BY sequence LIMIT 201",
            ("$search", SqlGuid(searchId))), "IX_search_results_search_job_id_sequence");
    }

    private static async Task SeedRuntimeAsync(LoadDatabase database, Guid runtimeId)
        => await ExecuteAsync(database,
            "INSERT INTO runtime_sessions (id, started_at_utc, version) VALUES ($runtime, 1, 'load-test')",
            ("$runtime", SqlGuid(runtimeId)));

    private static string SqlGuid(Guid value) => value.ToString().ToUpperInvariant();

    private static async Task ExecuteAsync(LoadDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> QueryPlanAsync(LoadDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var context = await database.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await using var rows = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await rows.ReadAsync())
            details.Add(rows.GetString(3));
        return string.Join(Environment.NewLine, details);
    }

    private sealed class LoadDatabase : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sockseek-persistence-load", Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabaseOwner owner;

        public LoadDatabase()
        {
            Directory.CreateDirectory(directory);
            var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
            owner = SqliteDatabaseOwner.Acquire(options);
            Factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
            Initializer = new SqliteInitializer(Factory, options, owner);
        }

        public SockseekDbContextFactory Factory { get; }
        public SqliteInitializer Initializer { get; }

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
