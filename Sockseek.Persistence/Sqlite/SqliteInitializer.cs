using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Sqlite;

public sealed record SqliteInitializationResult(string JournalMode, string SynchronousMode, string SchemaVersion);

public sealed class SqliteInitializer(
    IDbContextFactory<SockseekDbContext> contextFactory,
    SockseekSqliteOptions options,
    SqliteDatabaseOwner owner)
{
    internal static IReadOnlySet<string> SafeAutomaticMigrations { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "20260711200436_InitialPersistence",
        "20260712090000_AddHistoryQueryIndexes",
        "20260712170220_AddTransferAttemptSourceIdentity",
        "20260729120000_AddUploadTransferHistory",
        "20260806204353_AddChatsAndNotifications",
        "20260806213317_AddChatSequenceAllocator",
        "20260828100858_AddJobNavigationIndexes",
    };

    public async Task<SqliteInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = options.GetFullDatabasePath();
        if (!string.Equals(databasePath, owner.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The database ownership lease does not match the configured database path.");
        string databaseDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(databaseDirectory);
        PersistenceFilePrivacy.RestrictDirectory(databaseDirectory);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var unapproved = pendingMigrations.Where(migration => !SafeAutomaticMigrations.Contains(migration)).ToArray();
        if (unapproved.Length > 0)
        {
            throw new PersistenceSchemaCompatibilityException(
                "Automatic migration is blocked because these migrations have no reviewed additive-safety classification: "
                + string.Join(", ", unapproved)
                + ". Back up the database and use a release that explicitly supports this schema transition.");
        }
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        PersistenceFilePrivacy.RestrictFile(databasePath);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var connection = context.Database.GetDbConnection();
        string journalMode = ExecuteScalar(connection, "PRAGMA journal_mode=WAL;");
        string synchronousMode = ExecuteScalar(connection, "PRAGMA synchronous;");
        string schemaVersion = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).LastOrDefault()
            ?? "none";

        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite refused WAL mode and returned '{journalMode}'. Use a supported local filesystem.");
        if (synchronousMode != "2")
            throw new InvalidOperationException($"SQLite synchronous mode must be FULL (2), but was '{synchronousMode}'.");

        return new SqliteInitializationResult(journalMode, synchronousMode, schemaVersion);
    }

    private static string ExecuteScalar(System.Data.Common.DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}
