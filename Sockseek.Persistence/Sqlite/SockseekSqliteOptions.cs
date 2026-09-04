using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Sqlite;

public sealed record SockseekSqliteOptions(
    string DatabasePath,
    int DefaultTimeoutSeconds = 5,
    int BusyTimeoutMilliseconds = 5_000)
{
    public string GetFullDatabasePath() => Path.GetFullPath(DatabasePath);
}

public static class SockseekDbContextOptions
{
    public static DbContextOptions<SockseekDbContext> Create(SockseekSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DefaultTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Default timeout must be positive.");
        if (options.BusyTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Busy timeout must be positive.");

        string connectionString = CreateConnectionString(options);

        return new DbContextOptionsBuilder<SockseekDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(SockseekDbContext).Assembly.FullName))
            .AddInterceptors(new SqliteConnectionInterceptor(options.BusyTimeoutMilliseconds))
            .Options;
    }

    internal static string CreateConnectionString(SockseekSqliteOptions options)
        => new SqliteConnectionStringBuilder
        {
            DataSource = options.GetFullDatabasePath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            DefaultTimeout = options.DefaultTimeoutSeconds,
            Pooling = true,
        }.ToString();
}

internal static class SqliteConnectionPool
{
    public static void Clear(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
