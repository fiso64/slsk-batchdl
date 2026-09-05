using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Planning;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class InputArtifactStoreTests
{
    [TestMethod]
    public async Task UploadIsImmutableRestartableAndPinnedInMainDatabaseAcrossExpiry()
    {
        await using var database = await SharedDatabase.CreateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        byte[] content = Encoding.UTF8.GetBytes("artist,title\nA,T\n");
        string artifactId;

        await using (var store = database.CreateStore(clock))
        {
            await store.InitializeAsync();
            StoredInputArtifact artifact = await store.CreateAsync(
                new MemoryStream(content, writable: false),
                "../../unsafe.csv",
                TimeSpan.FromMinutes(1));
            artifactId = artifact.Id;
            Assert.AreEqual("unsafe.csv", artifact.OriginalName);
            Assert.AreEqual(content.LongLength, artifact.Length);
            Assert.AreEqual(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))
                    .ToLowerInvariant(),
                artifact.Sha256);
            Assert.IsTrue(await store.PinAsync(artifact.Id, "preview", Guid.NewGuid()));
        }

        clock.Advance(TimeSpan.FromHours(1));
        await using (var reopened = database.CreateStore(clock))
        {
            await reopened.InitializeAsync();
            InputArtifactLease lease = (await reopened.ResolveAsync(artifactId))!;
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(lease.Path));
            Assert.AreEqual(0, await reopened.PruneExpiredAsync());
            Assert.IsTrue(File.Exists(lease.Path));
        }

        Assert.IsFalse(File.Exists(Path.Combine(database.BlobDirectory, "artifacts.db")),
            "Artifact metadata must use the main persistence database.");
    }

    [TestMethod]
    public async Task StartupRemovesCrashOrphanedTemporaryAndBlobFiles()
    {
        await using var database = await SharedDatabase.CreateAsync();
        string orphanId = Guid.NewGuid().ToString("N");
        string orphanBlob = Path.Combine(database.BlobDirectory, orphanId + ".blob");
        string temporary = Path.Combine(
            database.BlobDirectory,
            Guid.NewGuid().ToString("N") + ".uploading");
        await File.WriteAllTextAsync(orphanBlob, "orphan");
        await File.WriteAllTextAsync(temporary, "partial");

        await using var store = database.CreateStore();
        await store.InitializeAsync();

        Assert.IsFalse(File.Exists(orphanBlob));
        Assert.IsFalse(File.Exists(temporary));
    }

    private sealed class SharedDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly SqliteDatabaseOwner owner;

        private SharedDatabase(
            string directory,
            SqliteDatabaseOwner owner,
            SockseekDbContextFactory factory)
        {
            this.directory = directory;
            this.owner = owner;
            Factory = factory;
            BlobDirectory = Path.Combine(directory, "planning", "input-artifacts");
            Directory.CreateDirectory(BlobDirectory);
        }

        public string BlobDirectory { get; }
        public SockseekDbContextFactory Factory { get; }
        public PersistenceHealth Health { get; } = new();

        public static async Task<SharedDatabase> CreateAsync()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "sockseek-input-artifact-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var options = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
            SqliteDatabaseOwner owner = SqliteDatabaseOwner.Acquire(options);
            var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(options));
            try
            {
                await new SqliteInitializer(factory, options, owner).InitializeAsync();
                return new SharedDatabase(directory, owner, factory);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        public InputArtifactStore CreateStore(TimeProvider? clock = null)
            => new(BlobDirectory, Factory, Health, clock);

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current += elapsed;
    }
}
