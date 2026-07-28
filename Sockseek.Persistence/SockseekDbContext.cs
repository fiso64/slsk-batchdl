using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;

namespace Sockseek.Persistence;

public sealed class SockseekDbContext(DbContextOptions<SockseekDbContext> options) : DbContext(options)
{
    internal DbSet<RuntimeSessionEntity> RuntimeSessions => Set<RuntimeSessionEntity>();
    internal DbSet<JobEntity> Jobs => Set<JobEntity>();
    internal DbSet<SearchJobEntity> SearchJobs => Set<SearchJobEntity>();
    internal DbSet<SearchResultEntity> SearchResults => Set<SearchResultEntity>();
    internal DbSet<TransferEntity> Transfers => Set<TransferEntity>();
    internal DbSet<TransferAttemptEntity> TransferAttempts => Set<TransferAttemptEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(SockseekDbContext).Assembly);
}
