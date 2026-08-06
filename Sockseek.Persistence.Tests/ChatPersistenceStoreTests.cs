using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Chat;
using Sockseek.Persistence;
using Sockseek.Persistence.Chat;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class ChatPersistenceStoreTests
{
    [TestMethod]
    public async Task PrivateReplayIsIdempotentAndCreatesOneNotification()
    {
        await using var database = await ChatDatabase.CreateAsync();
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");

        var first = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "hello", 42, sentAt);
        var replay = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "hello", 42, sentAt);

        Assert.IsTrue(first.Inserted);
        Assert.IsFalse(replay.Inserted);
        Assert.AreEqual(first.Message.MessageId, replay.Message.MessageId);
        Assert.IsNotNull(first.Notification);
        Assert.IsNull(replay.Notification);
        ChatStoreSummary summary = await database.Store.GetSummaryAsync("local");
        Assert.AreEqual(1, summary.UnreadPrivateMessages);
        Assert.AreEqual(1, summary.UnreadNotifications);
    }

    [TestMethod]
    public async Task OutgoingMessageIdIsIdempotentAndConflictingReuseIsReported()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid id = Guid.NewGuid();

        var created = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "hello");
        var existing = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "hello");
        var conflict = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "different");

        Assert.AreEqual(OutgoingChatPreparationStatus.Created, created.Status);
        Assert.AreEqual(OutgoingChatPreparationStatus.Existing, existing.Status);
        Assert.AreEqual(OutgoingChatPreparationStatus.Conflict, conflict.Status);
        Assert.AreEqual(ChatMessageState.Pending, created.Message.State);

        ChatMessageRecord sent = await database.Store.SetMessageStateAsync(
            "local", id, ChatMessageState.Sent, null);
        Assert.AreEqual(ChatMessageState.Sent, sent.State);
    }

    [TestMethod]
    public async Task MarkReadAdvancesWatermarkAndReadsRelatedNotification()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var accepted = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "hello", 7, DateTimeOffset.UtcNow);

        await database.Store.MarkConversationReadAsync(
            "local", accepted.Message.TargetId, accepted.Message.MessageId);

        ConversationRecord conversation = await database.Store.GetConversationAsync(
            "local", accepted.Message.TargetId) ?? throw new AssertFailedException();
        Assert.AreEqual(0, conversation.UnreadCount);
        ChatStoreSummary summary = await database.Store.GetSummaryAsync("local");
        Assert.AreEqual(0, summary.UnreadNotifications);
    }

    [TestMethod]
    public async Task SequencesRemainMonotonicAfterNewestHistoryIsDeleted()
    {
        await using var database = await ChatDatabase.CreateAsync();
        var first = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "first", 1, DateTimeOffset.Parse("2026-08-06T20:00:00Z"));

        await database.Store.DeleteHistoryAsync(
            "local", first.Message.TargetId, ChatTargetKind.Direct);
        var second = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "second", 2, DateTimeOffset.Parse("2026-08-06T20:00:01Z"));

        Assert.IsTrue(second.Message.Sequence > first.Message.Sequence);
        Assert.IsNotNull(first.Notification);
        Assert.IsNotNull(second.Notification);
        Assert.IsTrue(second.Notification!.Sequence > first.Notification!.Sequence);
    }

    [TestMethod]
    public async Task LocalAccountsHaveIndependentReplayAndUnreadPartitions()
    {
        await using var database = await ChatDatabase.CreateAsync();
        DateTimeOffset timestamp = DateTimeOffset.Parse("2026-08-06T20:00:00Z");

        var first = await database.Store.AcceptPrivateMessageAsync(
            "local-a", "Alice", "one", 42, timestamp);
        var second = await database.Store.AcceptPrivateMessageAsync(
            "local-b", "Alice", "two", 42, timestamp);

        Assert.AreNotEqual(first.Message.MessageId, second.Message.MessageId);
        Assert.AreEqual(1, (await database.Store.GetSummaryAsync("local-a")).UnreadPrivateMessages);
        Assert.AreEqual(1, (await database.Store.GetSummaryAsync("local-b")).UnreadPrivateMessages);
    }

    [TestMethod]
    public async Task MessageIdReuseAcrossAccountsIsAnExplicitConflict()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid id = Guid.NewGuid();
        await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local-a", "Alice", id, "hello");

        ChatStateConflictException error = await Assert.ThrowsExceptionAsync<ChatStateConflictException>(
            () => database.Store.PrepareOutgoingPrivateMessageAsync(
                "local-b", "Alice", id, "hello"));

        StringAssert.Contains(error.Message, "MessageId");
    }

    [TestMethod]
    public async Task MessagePagesUseValidatedReverseKeysetCursors()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid? targetId = null;
        for (int id = 1; id <= 3; id++)
        {
            var accepted = await database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"message-{id}", id,
                DateTimeOffset.Parse("2026-08-06T20:00:00Z").AddSeconds(id));
            targetId = accepted.Message.TargetId;
        }

        ChatPage<ChatMessageRecord> newest = await database.Store.GetMessagesAsync(
            "local", targetId!.Value, null, 2);
        ChatPage<ChatMessageRecord> older = await database.Store.GetMessagesAsync(
            "local", targetId.Value, newest.NextCursor, 2);

        CollectionAssert.AreEqual(
            new[] { "message-2", "message-3" }, newest.Items.Select(item => item.Body).ToArray());
        CollectionAssert.AreEqual(
            new[] { "message-1" }, older.Items.Select(item => item.Body).ToArray());
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => database.Store.GetMessagesAsync(
            "local", targetId.Value, new string('a', 257), 2));
    }

    [TestMethod]
    public async Task StartupReconciliationMarksPendingMessagesUnknownWithoutResend()
    {
        await using var database = await ChatDatabase.CreateAsync();
        Guid id = Guid.NewGuid();
        var pending = await database.Store.PrepareOutgoingPrivateMessageAsync(
            "local", "Alice", id, "hello");

        Assert.AreEqual(1, await database.Store.ReconcilePendingMessagesAsync());
        ChatPage<ChatMessageRecord> messages = await database.Store.GetMessagesAsync(
            "local", pending.Message.TargetId, null, 10);
        Assert.AreEqual(ChatMessageState.Unknown, messages.Items.Single().State);
    }

    [TestMethod]
    public async Task RetentionRepairsTargetStateAndPreservesMonotonicSequences()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-06T20:00:00Z"));
        await using var database = await ChatDatabase.CreateAsync(clock);
        var first = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "old", 1, clock.GetUtcNow());

        clock.Advance(TimeSpan.FromDays(2));
        ChatRetentionResult retained = await database.Store.ApplyRetentionAsync(
            TimeSpan.FromDays(1), TimeSpan.FromDays(1), 100);

        Assert.AreEqual(1, retained.PrunedMessages);
        Assert.AreEqual(first.Message.TargetId, retained.AffectedTargets.Single().TargetId);
        Assert.AreEqual(0, (await database.Store.GetSummaryAsync("local")).UnreadNotifications);
        Assert.AreEqual(0, (await database.Store.GetMessagesAsync(
            "local", first.Message.TargetId, null, 10)).Items.Count);

        var second = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "new", 2, clock.GetUtcNow());
        Assert.IsTrue(second.Message.Sequence > first.Message.Sequence);
        Assert.IsNotNull(first.Notification);
        Assert.IsNotNull(second.Notification);
        Assert.IsTrue(second.Notification!.Sequence > first.Notification!.Sequence);
    }

    [TestMethod]
    public async Task PrivateAndRoomRetentionPoliciesAreIndependent()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-06T20:00:00Z"));
        await using var database = await ChatDatabase.CreateAsync(clock);
        IncomingChatCommitResult direct = await database.Store.AcceptPrivateMessageAsync(
            "local", "Alice", "durable direct message", 1, clock.GetUtcNow());
        IncomingChatCommitResult room = await database.Store.AcceptRoomMessageAsync(
            "local", "busy-room", "Alice", "transient room message", true);

        clock.Advance(TimeSpan.FromDays(31));
        ChatRetentionResult roomRetention = await database.Store.ApplyRetentionAsync(
            privateMessageAge: null,
            roomMessageAge: TimeSpan.FromDays(30),
            batchSize: 100);

        Assert.AreEqual(1, roomRetention.PrunedMessages);
        Assert.AreEqual(ChatTargetKind.Room, roomRetention.AffectedTargets.Single().Kind);
        Assert.AreEqual(1, (await database.Store.GetMessagesAsync(
            "local", direct.Message.TargetId, null, 10)).Items.Count);
        Assert.AreEqual(0, (await database.Store.GetMessagesAsync(
            "local", room.Message.TargetId, null, 10)).Items.Count);
        ChatStoreSummary afterRoomRetention = await database.Store.GetSummaryAsync("local");
        Assert.AreEqual(1, afterRoomRetention.UnreadPrivateMessages);
        Assert.AreEqual(0, afterRoomRetention.UnreadRoomMessages);
        Assert.AreEqual(1, afterRoomRetention.UnreadNotifications);

        ChatRetentionResult privateRetention = await database.Store.ApplyRetentionAsync(
            privateMessageAge: TimeSpan.FromDays(30),
            roomMessageAge: null,
            batchSize: 100);

        Assert.AreEqual(1, privateRetention.PrunedMessages);
        Assert.AreEqual(ChatTargetKind.Direct, privateRetention.AffectedTargets.Single().Kind);
        Assert.AreEqual(0, (await database.Store.GetSummaryAsync("local")).UnreadNotifications);
    }

    [TestMethod]
    [TestCategory("Load")]
    [Timeout(60_000)]
    public async Task RetentionWithConcurrentReadsAndNewMessagesRemainsConsistent()
    {
        const int oldMessageCount = 1_000;
        const int newMessageCount = 64;
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-01T20:00:00Z"));
        await using var database = await ChatDatabase.CreateAsync(clock);

        Task<IncomingChatCommitResult>[] oldWrites = Enumerable.Range(1, oldMessageCount)
            .Select(id => database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"old-{id}", id, clock.GetUtcNow().AddSeconds(id)))
            .ToArray();
        await Task.WhenAll(oldWrites).WaitAsync(TimeSpan.FromSeconds(30));
        Guid targetId = oldWrites[0].Result.Message.TargetId;
        long largestOldSequence = oldWrites.Max(task => task.Result.Message.Sequence);

        clock.Advance(TimeSpan.FromDays(2));
        Task<ChatRetentionResult> retention = database.Store.ApplyRetentionAsync(
            TimeSpan.FromDays(1), TimeSpan.FromDays(1), oldMessageCount);
        Task<IncomingChatCommitResult>[] newWrites = Enumerable.Range(
                oldMessageCount + 1, newMessageCount)
            .Select(id => database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"new-{id}", id, clock.GetUtcNow().AddSeconds(id)))
            .ToArray();
        Task[] readers = Enumerable.Range(0, 4).Select(async _ =>
        {
            for (int pass = 0; pass < 20; pass++)
            {
                ChatPage<ChatMessageRecord> page = await database.Store.GetMessagesAsync(
                    "local", targetId, null, ChatLimits.MaximumPageSize);
                Assert.IsTrue(page.Items.Zip(page.Items.Skip(1))
                    .All(pair => pair.First.Sequence < pair.Second.Sequence));
                await Task.Yield();
            }
        }).ToArray();

        await Task.WhenAll([retention, Task.WhenAll(newWrites), Task.WhenAll(readers)])
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(oldMessageCount, retention.Result.PrunedMessages);
        ChatPage<ChatMessageRecord> remaining = await database.Store.GetMessagesAsync(
            "local", targetId, null, ChatLimits.MaximumPageSize);
        Assert.AreEqual(newMessageCount, remaining.Items.Count);
        Assert.IsTrue(remaining.Items.All(message => message.Sequence > largestOldSequence));
        Assert.AreEqual(
            newMessageCount,
            (await database.Store.GetSummaryAsync("local")).UnreadPrivateMessages);
    }

    [TestMethod]
    public async Task BurstOfCriticalCommandsDrainsWithoutPeriodicWriterDelay()
    {
        await using var database = await ChatDatabase.CreateAsync();
        DateTimeOffset sentAt = DateTimeOffset.Parse("2026-08-06T20:00:00Z");

        Task<IncomingChatCommitResult>[] writes = Enumerable.Range(1, 128)
            .Select(id => database.Store.AcceptPrivateMessageAsync(
                "local", "Alice", $"message-{id}", id, sentAt.AddSeconds(id)))
            .ToArray();
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(128, writes.Select(task => task.Result.Message.Sequence).Distinct().Count());
        Assert.AreEqual(128, (await database.Store.GetSummaryAsync("local")).UnreadPrivateMessages);
    }

    private sealed class ChatDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly SqliteDatabaseOwner owner;
        private readonly PersistenceInbox inbox;
        private readonly CancellationTokenSource stop = new();
        private readonly Task writerTask;

        private ChatDatabase(
            string directory,
            SqliteDatabaseOwner owner,
            SockseekDbContextFactory factory,
            TimeProvider? timeProvider)
        {
            this.directory = directory;
            this.owner = owner;
            var options = new PersistenceWriterOptions();
            var health = new PersistenceHealth();
            inbox = new PersistenceInbox(options, health);
            writerTask = new PersistenceWriter(factory, inbox, health, options).RunAsync(stop.Token);
            Store = new ChatPersistenceStore(factory, inbox, timeProvider);
        }

        public ChatPersistenceStore Store { get; }

        public static async Task<ChatDatabase> CreateAsync(TimeProvider? timeProvider = null)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "sockseek-chat-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sqlite = new SockseekSqliteOptions(Path.Combine(directory, "sockseek.db"));
            SqliteDatabaseOwner owner = SqliteDatabaseOwner.Acquire(sqlite);
            var factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(sqlite));
            await new SqliteInitializer(factory, sqlite, owner).InitializeAsync();
            return new ChatDatabase(directory, owner, factory, timeProvider);
        }

        public async ValueTask DisposeAsync()
        {
            inbox.Complete();
            await writerTask;
            stop.Dispose();
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
