using System.Buffers;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Planning;

public sealed record StoredInputArtifact(
    string Id,
    string Sha256,
    long Length,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? OriginalName);

public sealed record InputArtifactLease(
    StoredInputArtifact Artifact,
    string Path);

/// <summary>
/// Immutable disk-spooled browser input. Blob files remain owner-managed, while
/// metadata and pins use the main persistence database and lifecycle.
/// </summary>
public sealed class InputArtifactStore(
    string directory,
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceHealth health,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);
    private const int MaintenanceBatchSize = 256;
    private readonly string root = Path.GetFullPath(directory);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim uploadSlots = new(2, 2);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
            return;
        try
        {
            Directory.CreateDirectory(root);
            PersistenceFilePrivacy.RestrictDirectory(root);
            foreach (string temporary in Directory.EnumerateFiles(root, "*.uploading"))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await DeleteOrphanBlobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref initialized, 0);
            throw;
        }
    }

    public async Task<StoredInputArtifact> CreateAsync(
        Stream source,
        string? originalName,
        TimeSpan? retention = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(source);
        TimeSpan effectiveRetention = retention ?? DefaultRetention;
        if (effectiveRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));

        await uploadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        string id = Guid.NewGuid().ToString("N");
        string temporaryPath = Path.Combine(root, id + ".uploading");
        string finalPath = BlobPath(id);
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                await using var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    length = checked(length + read);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            File.Move(temporaryPath, finalPath);
            PersistenceFilePrivacy.RestrictFile(finalPath);
            DateTimeOffset created = clock.GetUtcNow();
            var artifact = new StoredInputArtifact(
                id,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                length,
                created,
                created + effectiveRetention,
                SafeName(originalName));
            try
            {
                await WithWriteAsync(async (context, ct) =>
                {
                    context.InputArtifacts.Add(ToEntity(artifact));
                    await context.SaveChangesAsync(ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { File.Delete(finalPath); } catch { }
                throw;
            }
            return artifact;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }
            uploadSlots.Release();
        }
    }

    public async Task<InputArtifactLease?> ResolveAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(artifactId);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        InputArtifactEntity? row = await ObserveAsync(async () =>
        {
            await using SockseekDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await context.InputArtifacts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    artifact => artifact.Id == artifactId
                        && (artifact.ExpiresAtUtc > now
                            || context.InputArtifactPins.Any(pin =>
                                pin.ArtifactId == artifact.Id)),
                    cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (row is null)
            return null;
        StoredInputArtifact artifact = FromEntity(row);
        string path = BlobPath(artifact.Id);
        if (!File.Exists(path))
        {
            throw new InvalidDataException(
                "The input artifact metadata exists but its immutable content is missing.");
        }
        return new(artifact, path);
    }

    public async Task<bool> PinAsync(
        string artifactId,
        string ownerKind,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(artifactId);
        ValidateOwnerKind(ownerKind);
        return await WithWriteAsync(async (context, ct) =>
        {
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            bool available = await context.InputArtifacts.AnyAsync(
                artifact => artifact.Id == artifactId
                    && (artifact.ExpiresAtUtc > now
                        || context.InputArtifactPins.Any(pin =>
                            pin.ArtifactId == artifact.Id)),
                ct).ConfigureAwait(false);
            if (!available || await context.InputArtifactPins.AnyAsync(
                    pin => pin.ArtifactId == artifactId
                        && pin.OwnerKind == ownerKind
                        && pin.OwnerId == ownerId,
                    ct).ConfigureAwait(false))
            {
                return false;
            }
            context.InputArtifactPins.Add(new InputArtifactPinEntity
            {
                ArtifactId = artifactId,
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                CreatedAtUtc = now,
            });
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnpinAsync(
        string artifactId,
        string ownerKind,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(artifactId);
        ValidateOwnerKind(ownerKind);
        await WithWriteAsync(async (context, ct) =>
        {
            await context.InputArtifactPins
                .Where(pin => pin.ArtifactId == artifactId
                    && pin.OwnerKind == ownerKind
                    && pin.OwnerId == ownerId)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReleasePinsAsync(
        string ownerKind,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateOwnerKind(ownerKind);
        return await WithWriteAsync(
            (context, ct) => context.InputArtifactPins
                .Where(pin => pin.OwnerKind == ownerKind)
                .ExecuteDeleteAsync(ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        int total = 0;
        while (true)
        {
            string[] expired = await WithWriteAsync(async (context, ct) =>
            {
                long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                string[] ids = await context.InputArtifacts
                    .Where(artifact => artifact.ExpiresAtUtc <= now
                        && !context.InputArtifactPins.Any(pin =>
                            pin.ArtifactId == artifact.Id))
                    .OrderBy(artifact => artifact.ExpiresAtUtc)
                    .ThenBy(artifact => artifact.Id)
                    .Select(artifact => artifact.Id)
                    .Take(MaintenanceBatchSize)
                    .ToArrayAsync(ct).ConfigureAwait(false);
                if (ids.Length > 0)
                {
                    await context.InputArtifacts
                        .Where(artifact => ids.Contains(artifact.Id))
                        .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }
                return ids;
            }, cancellationToken).ConfigureAwait(false);
            if (expired.Length == 0)
                return total;
            total += expired.Length;
            foreach (string id in expired)
            {
                try { File.Delete(BlobPath(id)); }
                catch (FileNotFoundException) { }
            }
        }
    }

    private async Task DeleteOrphanBlobsAsync(CancellationToken cancellationToken)
    {
        foreach (string[] paths in Directory.EnumerateFiles(root, "*.blob")
                     .Chunk(MaintenanceBatchSize))
        {
            string[] ids = paths
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .ToArray();
            string[] retained = await ObserveAsync(async () =>
            {
                await using SockseekDbContext context = await contextFactory
                    .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                return await context.InputArtifacts
                    .Where(artifact => ids.Contains(artifact.Id))
                    .Select(artifact => artifact.Id)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var retainedSet = retained.ToHashSet(StringComparer.Ordinal);
            for (int index = 0; index < paths.Length; index++)
            {
                if (retainedSet.Contains(ids[index]))
                    continue;
                try { File.Delete(paths[index]); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task WithWriteAsync(
        Func<SockseekDbContext, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => await WithWriteAsync(async (context, ct) =>
        {
            await action(context, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private async Task<T> WithWriteAsync<T>(
        Func<SockseekDbContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ObserveAsync(async () =>
            {
                await using SockseekDbContext context = await contextFactory
                    .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await context.Database
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                T result = await action(context, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task<T> ObserveAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is DbException or DbUpdateException)
                health.RecordOperationalFailure(clock.GetUtcNow(), exception);
            throw;
        }
    }

    private string BlobPath(string id) => Path.Combine(root, id + ".blob");

    private static InputArtifactEntity ToEntity(StoredInputArtifact artifact)
        => new()
        {
            Id = artifact.Id,
            Sha256 = artifact.Sha256,
            Length = artifact.Length,
            CreatedAtUtc = artifact.CreatedAtUtc.ToUnixTimeMilliseconds(),
            ExpiresAtUtc = artifact.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            OriginalName = artifact.OriginalName,
        };

    private static StoredInputArtifact FromEntity(InputArtifactEntity artifact)
        => new(
            artifact.Id,
            artifact.Sha256,
            artifact.Length,
            DateTimeOffset.FromUnixTimeMilliseconds(artifact.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(artifact.ExpiresAtUtc),
            artifact.OriginalName);

    private static string? SafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string name = Path.GetFileName(value.Trim());
        name = new string(name.Where(character => !char.IsControl(character)).ToArray());
        if (name.Length > 255)
            name = name[..255];
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static void ValidateId(string id)
    {
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("The input artifact ID is invalid.", nameof(id));
    }

    private static void ValidateOwnerKind(string ownerKind)
    {
        if (string.IsNullOrWhiteSpace(ownerKind) || ownerKind.Length > 32)
            throw new ArgumentException("Artifact pin owner kind is invalid.", nameof(ownerKind));
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new InvalidOperationException("The input-artifact store is unavailable.");
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref initialized, 0);
        uploadSlots.Dispose();
        writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
