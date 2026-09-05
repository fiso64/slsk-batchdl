using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SubmissionCommitCoordinatorTests
{
    [TestMethod]
    public async Task AcceptedSubmissionReceiptSurvivesCoordinatorAndPersistenceRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-commit-receipt-tests",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
        });
        Guid idempotencyKey = Guid.NewGuid();
        const string fingerprint = "request-fingerprint";
        var expected = new CommitJobPreviewResponseDto(
            Guid.NewGuid(),
            idempotencyKey,
            Guid.NewGuid(),
            1,
            1,
            1,
            0,
            0,
            []);
        try
        {
            var firstPersistence = new PersistenceCoordinator(options);
            await firstPersistence.StartAsync(CancellationToken.None);
            try
            {
                var first = new SubmissionCommitCoordinator(firstPersistence);
                CommitJobPreviewResponseDto actual = await first.ExecuteAsync(
                    idempotencyKey,
                    fingerprint,
                    async cancellationToken =>
                    {
                        await firstPersistence.Submissions!.CreateAsync(
                            new SubmissionRegistration(
                                idempotencyKey,
                                DateTimeOffset.UtcNow,
                                1,
                                "{}",
                                null,
                                null,
                                null,
                                fingerprint),
                            cancellationToken);
                        return new SubmissionCommitExecution<CommitJobPreviewResponseDto>(
                            expected,
                            idempotencyKey);
                    });
                AssertReceipt(expected, actual);
            }
            finally
            {
                await firstPersistence.StopAsync(CancellationToken.None);
            }

            var secondPersistence = new PersistenceCoordinator(options);
            await secondPersistence.StartAsync(CancellationToken.None);
            try
            {
                var second = new SubmissionCommitCoordinator(secondPersistence);
                CommitJobPreviewResponseDto restored = await second.ExecuteAsync<CommitJobPreviewResponseDto>(
                    idempotencyKey,
                    fingerprint,
                    _ => throw new AssertFailedException(
                        "A retained receipt must not run the mutation again."));
                AssertReceipt(expected, restored);

                await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                    second.ExecuteAsync<CommitJobPreviewResponseDto>(
                        idempotencyKey,
                        "different-fingerprint",
                        _ => throw new AssertFailedException(
                            "A conflicting request must not run the mutation.")));
            }
            finally
            {
                await secondPersistence.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertReceipt(
        CommitJobPreviewResponseDto expected,
        CommitJobPreviewResponseDto actual)
    {
        Assert.AreEqual(expected.PreviewId, actual.PreviewId);
        Assert.AreEqual(expected.SubmissionId, actual.SubmissionId);
        Assert.AreEqual(expected.WorkflowId, actual.WorkflowId);
        Assert.AreEqual(expected.SubmittedCount, actual.SubmittedCount);
        Assert.AreEqual(0, actual.RejectionReasons.Count);
    }
}
