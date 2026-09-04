using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Planning;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class JobPreviewStoreTests
{
    [TestMethod]
    public async Task PreviewPagesAndRevisionBoundCommitSurviveStoreReopenWithinSession()
    {
        await using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "job-previews.db");
        Guid previewId;
        long previewRevision;

        await using (var store = new JobPreviewStore(path))
        {
            await store.InitializeAsync();
            StoredJobPreview preview = await store.CreateAsync("{\"kind\":\"test\"}");
            previewId = preview.Id;
            await store.AppendNodesAsync(preview.Id,
            [
                Node(preview.Id, "0", null, selectable: false),
                Node(preview.Id, "0/0", "0", selectable: true),
                Node(preview.Id, "0/1", "0", selectable: true),
                Node(preview.Id, "0/2", "0", selectable: false, state: "Failed"),
            ]);
            preview = await store.CompleteAsync(preview.Id);
            previewRevision = preview.Revision;
            Assert.AreEqual("PartiallyReady", preview.State);
            Assert.AreEqual(4, preview.NodeCount);
            Assert.AreEqual(2, preview.SelectableNodeCount);

            IReadOnlyList<StoredJobPreviewNode> firstPage = await store.GetNodesAsync(
                preview.Id, "0", afterOrdinal: 0, limit: 1);
            Assert.AreEqual(1, firstPage.Count);
            IReadOnlyList<StoredJobPreviewNode> secondPage = await store.GetNodesAsync(
                preview.Id, "0", firstPage[0].Ordinal, limit: 2);
            Assert.AreEqual(2, secondPage.Count);
            Assert.AreEqual(
                firstPage[0].EffectiveSettingsRef,
                secondPage[0].EffectiveSettingsRef,
                "Nodes with identical settings should reference one immutable settings record.");

        }

        await using (var reopened = new JobPreviewStore(path))
        {
            await reopened.InitializeAsync();
            StoredPreviewCommit commit = (await reopened.ResolveCommitAsync(
                previewId,
                previewRevision,
                "AllExcept",
                new HashSet<string>(["0/1"], StringComparer.Ordinal)))!;
            Assert.AreEqual(1, commit.SelectedNodes.Count);
            Assert.AreEqual("0/0", commit.SelectedNodes[0].Ref);

            Guid submissionId = Guid.NewGuid();
            Assert.IsTrue(await reopened.TryBeginCommitAsync(previewId, submissionId));
            Assert.IsFalse(await reopened.TryBeginCommitAsync(previewId, Guid.NewGuid()));
            Assert.IsTrue(await reopened.MarkCommittedAsync(previewId, submissionId));
            StoredJobPreview committed = (await reopened.GetPreviewAsync(previewId))!;
            Assert.AreEqual("Committed", committed.State);
            Assert.AreEqual(submissionId, committed.CommittedSubmissionId);
        }
    }

    [TestMethod]
    public async Task FailedCommitClaimCanBeReleasedForRetry()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");
        await store.AppendNodesAsync(preview.Id, [Node(preview.Id, "0", null, selectable: true)]);
        await store.CompleteAsync(preview.Id);

        Guid firstSubmission = Guid.NewGuid();
        Assert.IsTrue(await store.TryBeginCommitAsync(preview.Id, firstSubmission));
        Assert.IsTrue(await store.ReleaseCommitAsync(preview.Id, firstSubmission));
        Assert.IsTrue(await store.TryBeginCommitAsync(preview.Id, Guid.NewGuid()));
    }

    [TestMethod]
    public async Task ExpiryPublishesTombstoneBeforePruningSummary()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        await using var store = new JobPreviewStore(
            Path.Combine(directory.Path, "job-previews.db"),
            clock);
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}", TimeSpan.FromMinutes(1));
        await store.AppendNodesAsync(preview.Id, [Node(preview.Id, "0", null, selectable: true)]);
        await store.CompleteAsync(preview.Id);

        clock.Advance(TimeSpan.FromMinutes(2));
        IReadOnlyList<Guid> expired = await store.ExpireDueAsync();
        CollectionAssert.AreEqual(new[] { preview.Id }, expired.ToArray());
        StoredJobPreview tombstone = (await store.GetPreviewAsync(preview.Id))!;
        Assert.AreEqual("Expired", tombstone.State);
        Assert.AreEqual(0, (await store.GetNodesAsync(preview.Id, null, -1, 10)).Count);

        clock.Advance(TimeSpan.FromDays(2));
        IReadOnlyList<StoredJobPreviewCleanup> pruned = await store.PruneTombstonesAsync(
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(30));
        Assert.AreEqual(1, pruned.Count);
        Assert.IsNull(await store.GetPreviewAsync(preview.Id));
    }

    [TestMethod]
    public async Task StreamedPlanPagesAndDirectOnlySelectionStayBounded()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");

        const int batchSize = 73;
        const int nodeCount = 501;
        for (int offset = 0; offset < nodeCount; offset += batchSize)
        {
            await store.AppendNodesAsync(
                preview.Id,
                Enumerable.Range(offset, Math.Min(batchSize, nodeCount - offset))
                    .Select(index => Node(preview.Id, $"0/{index}", "0", selectable: true))
                    .ToArray());
        }
        preview = await store.CompleteAsync(preview.Id);
        Assert.AreEqual(nodeCount, preview.NodeCount);

        long after = -1;
        int traversed = 0;
        while (true)
        {
            IReadOnlyList<StoredJobPreviewNode> page = await store.GetNodesAsync(
                preview.Id,
                "0",
                after,
                limit: JobPreviewStore.MaximumPageSize);
            Assert.IsTrue(page.Count <= JobPreviewStore.MaximumPageSize);
            if (page.Count == 0)
                break;
            traversed += page.Count;
            after = page[^1].Ordinal;
        }
        Assert.AreEqual(nodeCount, traversed);

        var selectedRefs = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < nodeCount; index += 2)
            selectedRefs.Add($"0/{index}");
        StoredPreviewCommit resolved = (await store.ResolveCommitAsync(
            preview.Id,
            preview.Revision,
            "Only",
            selectedRefs))!;
        Assert.AreEqual((nodeCount + 1) / 2, resolved.SelectedNodes.Count);
        Assert.AreEqual(0, resolved.MissingRequestedRefCount);
    }

    private static StoredJobPreviewNode Node(
        Guid previewId,
        string nodeRef,
        string? parentRef,
        bool selectable,
        string state = "Ready")
        => new(
            previewId,
            0,
            nodeRef,
            parentRef,
            "ExecutionChild",
            state,
            selectable,
            "Song",
            nodeRef,
            null,
            0,
            "[]",
            selectable ? "{}" : null,
            selectable ? "same-settings" : null,
            selectable ? "{\"downloadMode\":\"Normal\"}" : null,
            selectable ? "[]" : null,
            state == "Failed" ? "test" : null,
            state == "Failed" ? "failed row" : null);

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sockseek-preview-tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
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
