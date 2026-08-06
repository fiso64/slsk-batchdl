using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sockseek.Core.Chat;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Chat;

public sealed record ChatStoreSummary(
    int UnreadPrivateMessages,
    int UnreadRoomMessages,
    int UnreadNotifications,
    long Revision);

public sealed record ChatRetentionTarget(ChatTargetKind Kind, Guid TargetId);
public sealed record ChatRetentionResult(
    int PrunedMessages,
    IReadOnlyList<ChatRetentionTarget> AffectedTargets);

public sealed class ChatPersistenceStore(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceInbox inbox,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<int> ReconcilePendingMessagesAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(async (context, ct) =>
        {
            var pending = await context.ChatMessages
                .Where(message => message.SendState == nameof(ChatMessageState.Pending))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var message in pending)
                message.SendState = nameof(ChatMessageState.Unknown);
            return pending.Count;
        }, cancellationToken).ConfigureAwait(false);

    public async Task<IncomingChatCommitResult> AcceptPrivateMessageAsync(
        string localAccount,
        string username,
        string body,
        int protocolMessageId,
        DateTimeOffset protocolTimestamp,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        string peerKey = ChatIdentity.NormalizeUsername(username);
        body = ChatIdentity.ValidateMessage(body);
        var commandResult = await ExecuteAsync(async (context, ct) =>
        {
            var existing = await context.ChatMessages.FirstOrDefaultAsync(message =>
                    message.LocalAccountKey == accountKey
                    && message.TargetKind == nameof(ChatTargetKind.Direct)
                    && message.TargetKey == peerKey
                    && message.ProtocolMessageId == protocolMessageId
                    && message.ProtocolTimestamp == ToUnixMilliseconds(protocolTimestamp), ct)
                .ConfigureAwait(false);
            if (existing is not null)
                return new IncomingCommandResult(false, existing.Id, null);

            long now = ToUnixMilliseconds(clock.GetUtcNow());
            var conversation = await context.ChatConversations.FirstOrDefaultAsync(
                    item => item.LocalAccountKey == accountKey && item.PeerKey == peerKey, ct)
                .ConfigureAwait(false);
            if (conversation is null)
            {
                conversation = new ChatConversationEntity
                {
                    Id = Guid.NewGuid(),
                    LocalAccountKey = accountKey,
                    PeerKey = peerKey,
                    DisplayUsername = username.Trim(),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                context.ChatConversations.Add(conversation);
            }

            long sequence = await NextMessageSequenceAsync(context, ct).ConfigureAwait(false);
            var message = new ChatMessageEntity
            {
                Id = Guid.NewGuid(),
                Sequence = sequence,
                LocalAccountKey = accountKey,
                TargetKind = nameof(ChatTargetKind.Direct),
                TargetId = conversation.Id,
                TargetKey = peerKey,
                DisplayTarget = username.Trim(),
                SenderKey = peerKey,
                DisplaySender = username.Trim(),
                Direction = nameof(ChatMessageDirection.Incoming),
                Body = body,
                OccurredAtUtc = ToUnixMilliseconds(protocolTimestamp),
                RecordedAtUtc = now,
                SendState = nameof(ChatMessageState.Received),
                ProtocolMessageId = protocolMessageId,
                ProtocolTimestamp = ToUnixMilliseconds(protocolTimestamp),
            };
            context.ChatMessages.Add(message);
            conversation.DisplayUsername = username.Trim();
            conversation.ArchivedAtUtc = null;
            conversation.LastMessageSequence = sequence;
            conversation.UpdatedAtUtc = now;
            conversation.Revision++;

            var notification = new NotificationEntity
            {
                Id = Guid.NewGuid(),
                Sequence = await NextNotificationSequenceAsync(context, ct).ConfigureAwait(false),
                LocalAccountKey = accountKey,
                Kind = nameof(UserNotificationKind.PrivateMessage),
                SourceMessageId = message.Id,
                CreatedAtUtc = now,
            };
            context.Notifications.Add(notification);
            return new IncomingCommandResult(true, message.Id, notification.Id);
        }, cancellationToken).ConfigureAwait(false);

        return await HydrateIncomingResultAsync(
            commandResult,
            ChatTargetKind.Direct,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IncomingChatCommitResult> AcceptRoomMessageAsync(
        string localAccount,
        string roomName,
        string username,
        string body,
        bool createNotification,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        string senderKey = ChatIdentity.NormalizeUsername(username);
        body = ChatIdentity.ValidateMessage(body);
        var commandResult = await ExecuteAsync(async (context, ct) =>
        {
            long now = ToUnixMilliseconds(clock.GetUtcNow());
            var room = await GetOrCreateRoomEntityAsync(
                context, accountKey, roomKey, roomName.Trim(), now, ct).ConfigureAwait(false);
            long sequence = await NextMessageSequenceAsync(context, ct).ConfigureAwait(false);
            var message = new ChatMessageEntity
            {
                Id = Guid.NewGuid(),
                Sequence = sequence,
                LocalAccountKey = accountKey,
                TargetKind = nameof(ChatTargetKind.Room),
                TargetId = room.Id,
                TargetKey = roomKey,
                DisplayTarget = roomName.Trim(),
                SenderKey = senderKey,
                DisplaySender = username.Trim(),
                Direction = nameof(ChatMessageDirection.Incoming),
                Body = body,
                OccurredAtUtc = now,
                RecordedAtUtc = now,
                SendState = nameof(ChatMessageState.Received),
            };
            context.ChatMessages.Add(message);
            room.LastMessageSequence = sequence;
            room.UpdatedAtUtc = now;
            room.Revision++;

            Guid? notificationId = null;
            if (createNotification)
            {
                var notification = new NotificationEntity
                {
                    Id = Guid.NewGuid(),
                    Sequence = await NextNotificationSequenceAsync(context, ct).ConfigureAwait(false),
                    LocalAccountKey = accountKey,
                    Kind = nameof(UserNotificationKind.RoomMention),
                    SourceMessageId = message.Id,
                    CreatedAtUtc = now,
                };
                context.Notifications.Add(notification);
                notificationId = notification.Id;
            }
            return new IncomingCommandResult(true, message.Id, notificationId);
        }, cancellationToken).ConfigureAwait(false);

        return await HydrateIncomingResultAsync(
            commandResult,
            ChatTargetKind.Room,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutgoingChatPreparationResult> PrepareOutgoingPrivateMessageAsync(
        string localAccount,
        string username,
        Guid messageId,
        string body,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        string peerKey = ChatIdentity.NormalizeUsername(username);
        body = ChatIdentity.ValidateMessage(body);
        return await PrepareOutgoingAsync(
            accountKey, ChatTargetKind.Direct, peerKey, username.Trim(), messageId, body,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutgoingChatPreparationResult> PrepareOutgoingRoomMessageAsync(
        string localAccount,
        Guid roomId,
        Guid messageId,
        string body,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        body = ChatIdentity.ValidateMessage(body);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var room = await context.ChatRoomSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == roomId && item.LocalAccountKey == accountKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The room was not found.");
        return await PrepareOutgoingAsync(
            accountKey, ChatTargetKind.Room, room.RoomKey, room.DisplayName, messageId, body,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OutgoingChatPreparationResult> PrepareOutgoingAsync(
        string accountKey,
        ChatTargetKind targetKind,
        string targetKey,
        string displayTarget,
        Guid messageId,
        string body,
        CancellationToken cancellationToken)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("MessageId cannot be empty.", nameof(messageId));

        var prepared = await ExecuteAsync(async (context, ct) =>
        {
            var existing = await context.ChatMessages.FirstOrDefaultAsync(
                item => item.Id == messageId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                bool same = existing.LocalAccountKey == accountKey
                            && existing.TargetKind == targetKind.ToString()
                            && existing.TargetKey == targetKey
                            && existing.Direction == nameof(ChatMessageDirection.Outgoing)
                            && existing.Body == body;
                return new OutgoingCommandResult(
                    same ? OutgoingChatPreparationStatus.Existing : OutgoingChatPreparationStatus.Conflict,
                    existing.Id);
            }

            long now = ToUnixMilliseconds(clock.GetUtcNow());
            Guid targetId;
            if (targetKind == ChatTargetKind.Direct)
            {
                var conversation = await context.ChatConversations.FirstOrDefaultAsync(
                    item => item.LocalAccountKey == accountKey && item.PeerKey == targetKey, ct).ConfigureAwait(false);
                if (conversation is null)
                {
                    conversation = new ChatConversationEntity
                    {
                        Id = Guid.NewGuid(),
                        LocalAccountKey = accountKey,
                        PeerKey = targetKey,
                        DisplayUsername = displayTarget,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    context.ChatConversations.Add(conversation);
                }
                targetId = conversation.Id;
                conversation.ArchivedAtUtc = null;
                conversation.DisplayUsername = displayTarget;
                conversation.UpdatedAtUtc = now;
                conversation.Revision++;
            }
            else
            {
                var room = await GetOrCreateRoomEntityAsync(
                    context, accountKey, targetKey, displayTarget, now, ct).ConfigureAwait(false);
                targetId = room.Id;
                room.UpdatedAtUtc = now;
                room.Revision++;
            }

            long sequence = await NextMessageSequenceAsync(context, ct).ConfigureAwait(false);
            var message = new ChatMessageEntity
            {
                Id = messageId,
                Sequence = sequence,
                LocalAccountKey = accountKey,
                TargetKind = targetKind.ToString(),
                TargetId = targetId,
                TargetKey = targetKey,
                DisplayTarget = displayTarget,
                SenderKey = accountKey,
                DisplaySender = accountKey,
                Direction = nameof(ChatMessageDirection.Outgoing),
                Body = body,
                OccurredAtUtc = now,
                RecordedAtUtc = now,
                SendState = nameof(ChatMessageState.Pending),
            };
            context.ChatMessages.Add(message);
            if (targetKind == ChatTargetKind.Direct)
            {
                var conversation = context.ChatConversations.Local.Single(item => item.Id == targetId);
                conversation.LastMessageSequence = sequence;
            }
            else
            {
                var room = await context.ChatRoomSubscriptions.SingleAsync(item => item.Id == targetId, ct).ConfigureAwait(false);
                room.LastMessageSequence = sequence;
            }
            return new OutgoingCommandResult(OutgoingChatPreparationStatus.Created, message.Id);
        }, cancellationToken).ConfigureAwait(false);

        var messageRecord = await GetMessageAsync(accountKey, prepared.MessageId, cancellationToken).ConfigureAwait(false);
        if (messageRecord is null)
            throw new ChatStateConflictException(
                "MessageId was already used by another local account.");
        ConversationRecord? conversationRecord = messageRecord.TargetKind == ChatTargetKind.Direct
            ? await GetConversationAsync(accountKey, messageRecord.TargetId, cancellationToken).ConfigureAwait(false)
            : null;
        RoomSubscriptionRecord? roomRecord = messageRecord.TargetKind == ChatTargetKind.Room
            ? await GetRoomAsync(accountKey, messageRecord.TargetId, cancellationToken).ConfigureAwait(false)
            : null;
        return new OutgoingChatPreparationResult(
            prepared.Status, messageRecord, conversationRecord, roomRecord);
    }

    public async Task<ChatMessageRecord> SetMessageStateAsync(
        string localAccount,
        Guid messageId,
        ChatMessageState state,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        failureReason = failureReason is null
            ? null
            : failureReason[..Math.Min(failureReason.Length, ChatLimits.MaximumFailureReasonLength)];
        await ExecuteAsync(async (context, ct) =>
        {
            var message = await context.ChatMessages.SingleOrDefaultAsync(
                item => item.Id == messageId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The message was not found.");
            if (message.Direction != nameof(ChatMessageDirection.Outgoing))
                throw new InvalidOperationException("Only outgoing message state can be changed.");
            message.SendState = state.ToString();
            message.FailureReason = failureReason;
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return await GetMessageAsync(accountKey, messageId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The updated message was not found.");
    }

    public async Task<ConversationRecord?> GetConversationAsync(
        string localAccount,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.ChatConversations.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == conversationId && item.LocalAccountKey == accountKey, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : await MapConversationAsync(context, entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationRecord?> GetConversationByPeerAsync(
        string localAccount,
        string username,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        string peerKey = ChatIdentity.NormalizeUsername(username);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.ChatConversations.AsNoTracking().SingleOrDefaultAsync(
            item => item.LocalAccountKey == accountKey && item.PeerKey == peerKey, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : await MapConversationAsync(context, entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatPage<ConversationRecord>> GetConversationsAsync(
        string localAccount,
        bool? unread,
        bool? archived,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        var position = DecodeSummaryCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<ChatConversationEntity> query = context.ChatConversations.AsNoTracking()
            .Where(item => item.LocalAccountKey == accountKey);
        if (archived is not null)
            query = archived.Value ? query.Where(item => item.ArchivedAtUtc != null) : query.Where(item => item.ArchivedAtUtc == null);
        if (unread is not null)
        {
            query = unread.Value
                ? query.Where(item => context.ChatMessages.Any(message =>
                    message.LocalAccountKey == accountKey
                    && message.TargetId == item.Id
                    && message.Direction == nameof(ChatMessageDirection.Incoming)
                    && message.Sequence > item.LastReadSequence))
                : query.Where(item => !context.ChatMessages.Any(message =>
                    message.LocalAccountKey == accountKey
                    && message.TargetId == item.Id
                    && message.Direction == nameof(ChatMessageDirection.Incoming)
                    && message.Sequence > item.LastReadSequence));
        }
        if (position is not null)
            query = query.Where(item => item.LastMessageSequence < position.Value.Sequence
                || item.LastMessageSequence == position.Value.Sequence && item.Id.CompareTo(position.Value.Id) < 0);
        var entities = await query.OrderByDescending(item => item.LastMessageSequence)
            .ThenByDescending(item => item.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<ConversationRecord>(Math.Min(limit, entities.Count));
        foreach (var entity in entities.Take(limit))
            records.Add(await MapConversationAsync(context, entity, cancellationToken).ConfigureAwait(false));
        string? next = entities.Count > limit && records.Count > 0
            ? EncodeSummaryCursor(records[^1].LastMessageSequence, records[^1].ConversationId)
            : null;
        return new ChatPage<ConversationRecord>(records, next);
    }

    public async Task<ChatPage<ChatMessageRecord>> GetMessagesAsync(
        string localAccount,
        Guid targetId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        long? before = DecodeSequenceCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.ChatMessages.AsNoTracking().Where(
            item => item.LocalAccountKey == accountKey && item.TargetId == targetId);
        if (before is not null)
            query = query.Where(item => item.Sequence < before.Value);
        var entities = await query.OrderByDescending(item => item.Sequence)
            .Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = entities.Take(limit).Select(MapMessage).Reverse().ToArray();
        string? next = entities.Count > limit && items.Length > 0
            ? EncodeSequenceCursor(items[0].Sequence)
            : null;
        return new ChatPage<ChatMessageRecord>(items, next);
    }

    public async Task<RoomSubscriptionRecord> UpsertRoomAsync(
        string localAccount,
        string roomName,
        bool runtimeDesired,
        ChatRoomKind kind,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        Guid roomId = await ExecuteAsync(async (context, ct) =>
        {
            long now = ToUnixMilliseconds(clock.GetUtcNow());
            var room = await GetOrCreateRoomEntityAsync(
                context, accountKey, roomKey, roomName.Trim(), now, ct).ConfigureAwait(false);
            room.DisplayName = roomName.Trim();
            room.RuntimeDesired = runtimeDesired;
            if (kind != ChatRoomKind.Unknown)
                room.Kind = kind.ToString();
            room.UpdatedAtUtc = now;
            room.Revision++;
            return room.Id;
        }, cancellationToken).ConfigureAwait(false);
        return await GetRoomAsync(accountKey, roomId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The room subscription was not persisted.");
    }

    public async Task<RoomSubscriptionRecord?> GetRoomAsync(
        string localAccount,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.ChatRoomSubscriptions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == roomId && item.LocalAccountKey == accountKey, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : await MapRoomAsync(context, entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomSubscriptionRecord?> GetRoomByNameAsync(
        string localAccount,
        string roomName,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        string roomKey = ChatIdentity.NormalizeRoom(roomName);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.ChatRoomSubscriptions.AsNoTracking().SingleOrDefaultAsync(
            item => item.LocalAccountKey == accountKey && item.RoomKey == roomKey, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : await MapRoomAsync(context, entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatPage<RoomSubscriptionRecord>> GetRoomsAsync(
        string localAccount,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        var position = DecodeSummaryCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.ChatRoomSubscriptions.AsNoTracking().Where(item => item.LocalAccountKey == accountKey);
        if (position is not null)
            query = query.Where(item => item.LastMessageSequence < position.Value.Sequence
                || item.LastMessageSequence == position.Value.Sequence && item.Id.CompareTo(position.Value.Id) < 0);
        var entities = await query.OrderByDescending(item => item.LastMessageSequence)
            .ThenByDescending(item => item.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<RoomSubscriptionRecord>();
        foreach (var entity in entities.Take(limit))
            records.Add(await MapRoomAsync(context, entity, cancellationToken).ConfigureAwait(false));
        string? next = entities.Count > limit && records.Count > 0
            ? EncodeSummaryCursor(records[^1].LastMessageSequence, records[^1].RoomId)
            : null;
        return new ChatPage<RoomSubscriptionRecord>(records, next);
    }

    public async Task<ChatPage<RoomSubscriptionRecord>> GetFilteredRoomsAsync(
        string localAccount,
        Func<RoomSubscriptionRecord, bool> predicate,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        limit = ChatIdentity.ValidatePageSize(limit);
        var matches = new List<RoomSubscriptionRecord>(limit + 1);
        string? next = cursor;
        do
        {
            ChatPage<RoomSubscriptionRecord> page = await GetRoomsAsync(
                localAccount,
                next,
                ChatLimits.MaximumPageSize,
                cancellationToken).ConfigureAwait(false);
            foreach (RoomSubscriptionRecord room in page.Items)
            {
                if (predicate(room))
                    matches.Add(room);
                if (matches.Count > limit)
                {
                    RoomSubscriptionRecord last = matches[limit - 1];
                    return new ChatPage<RoomSubscriptionRecord>(
                        matches.Take(limit).ToArray(),
                        EncodeSummaryCursor(last.LastMessageSequence, last.RoomId));
                }
            }
            next = page.NextCursor;
        } while (next is not null);

        return new ChatPage<RoomSubscriptionRecord>(matches, null);
    }

    public async Task<IReadOnlyList<RoomSubscriptionRecord>> GetDesiredRoomsAsync(
        string localAccount,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await context.ChatRoomSubscriptions.AsNoTracking()
            .Where(item => item.LocalAccountKey == accountKey && item.RuntimeDesired)
            .OrderBy(item => item.DisplayName).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<RoomSubscriptionRecord>();
        foreach (var entity in entities)
            result.Add(await MapRoomAsync(context, entity, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public Task MarkConversationReadAsync(
        string localAccount, Guid conversationId, Guid throughMessageId, CancellationToken cancellationToken = default)
        => MarkTargetReadAsync(localAccount, conversationId, throughMessageId, ChatTargetKind.Direct, cancellationToken);

    public Task MarkRoomReadAsync(
        string localAccount, Guid roomId, Guid throughMessageId, CancellationToken cancellationToken = default)
        => MarkTargetReadAsync(localAccount, roomId, throughMessageId, ChatTargetKind.Room, cancellationToken);

    private async Task MarkTargetReadAsync(
        string localAccount,
        Guid targetId,
        Guid throughMessageId,
        ChatTargetKind kind,
        CancellationToken cancellationToken)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await ExecuteAsync(async (context, ct) =>
        {
            var through = await context.ChatMessages.SingleOrDefaultAsync(
                item => item.Id == throughMessageId && item.LocalAccountKey == accountKey && item.TargetId == targetId, ct)
                .ConfigureAwait(false) ?? throw new KeyNotFoundException("The message was not found in this chat target.");
            if (kind == ChatTargetKind.Direct)
            {
                var target = await context.ChatConversations.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The conversation was not found.");
                if (through.Sequence > target.LastReadSequence)
                {
                    target.LastReadSequence = through.Sequence;
                    target.Revision++;
                    target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
                }
            }
            else
            {
                var target = await context.ChatRoomSubscriptions.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The room was not found.");
                if (through.Sequence > target.LastReadSequence)
                {
                    target.LastReadSequence = through.Sequence;
                    target.Revision++;
                    target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
                }
            }
            long now = ToUnixMilliseconds(clock.GetUtcNow());
            var notifications = await (
                from notification in context.Notifications
                join message in context.ChatMessages on notification.SourceMessageId equals message.Id
                where notification.LocalAccountKey == accountKey
                      && notification.ReadAtUtc == null
                      && message.TargetId == targetId
                      && message.Sequence <= through.Sequence
                select notification).ToListAsync(ct).ConfigureAwait(false);
            foreach (var notification in notifications)
                notification.ReadAtUtc = now;
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ArchiveConversationAsync(
        string localAccount, Guid conversationId, bool archived, CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await ExecuteAsync(async (context, ct) =>
        {
            var conversation = await context.ChatConversations.SingleOrDefaultAsync(
                item => item.Id == conversationId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The conversation was not found.");
            conversation.ArchivedAtUtc = archived ? ToUnixMilliseconds(clock.GetUtcNow()) : null;
            conversation.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
            conversation.Revision++;
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteHistoryAsync(
        string localAccount,
        Guid targetId,
        ChatTargetKind kind,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await ExecuteAsync(async (context, ct) =>
        {
            var messages = await context.ChatMessages.Where(
                item => item.LocalAccountKey == accountKey && item.TargetId == targetId).ToListAsync(ct).ConfigureAwait(false);
            context.ChatMessages.RemoveRange(messages);
            if (kind == ChatTargetKind.Direct)
            {
                var target = await context.ChatConversations.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The conversation was not found.");
                target.LastMessageSequence = 0;
                target.LastReadSequence = 0;
                target.Revision++;
                target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
            }
            else
            {
                var target = await context.ChatRoomSubscriptions.SingleOrDefaultAsync(
                    item => item.Id == targetId && item.LocalAccountKey == accountKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The room was not found.");
                target.LastMessageSequence = 0;
                target.LastReadSequence = 0;
                target.Revision++;
                target.UpdatedAtUtc = ToUnixMilliseconds(clock.GetUtcNow());
            }
            return messages.Count;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatPage<UserNotificationRecord>> GetNotificationsAsync(
        string localAccount,
        bool? unread,
        UserNotificationKind? kind,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        limit = ChatIdentity.ValidatePageSize(limit);
        long? before = DecodeSequenceCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Notifications.AsNoTracking().Where(item => item.LocalAccountKey == accountKey);
        if (unread is not null)
            query = unread.Value ? query.Where(item => item.ReadAtUtc == null) : query.Where(item => item.ReadAtUtc != null);
        if (kind is not null)
        {
            string kindValue = kind.Value.ToString();
            query = query.Where(item => item.Kind == kindValue);
        }
        if (before is not null)
            query = query.Where(item => item.Sequence < before.Value);
        var entities = await query.OrderByDescending(item => item.Sequence).Take(limit + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<UserNotificationRecord>();
        foreach (var entity in entities.Take(limit))
            records.Add(await MapNotificationAsync(context, entity, cancellationToken).ConfigureAwait(false));
        string? next = entities.Count > limit && records.Count > 0
            ? EncodeSequenceCursor(records[^1].Sequence)
            : null;
        return new ChatPage<UserNotificationRecord>(records, next);
    }

    public async Task<UserNotificationRecord?> GetNotificationAsync(
        string localAccount, Guid notificationId, CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.Notifications.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == notificationId && item.LocalAccountKey == accountKey, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : await MapNotificationAsync(context, entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkNotificationsReadAsync(
        string localAccount,
        long? throughSequence,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        if ((throughSequence is null) == (ids is null))
            throw new ArgumentException("Specify either throughSequence or ids.");
        if (throughSequence < 0)
            throw new ArgumentOutOfRangeException(
                nameof(throughSequence), "Notification sequence cannot be negative.");
        if (ids is { Count: > ChatLimits.MaximumPageSize })
            throw new ArgumentException($"At most {ChatLimits.MaximumPageSize} notification ids may be supplied.");
        await ExecuteAsync(async (context, ct) =>
        {
            IQueryable<NotificationEntity> query = context.Notifications.Where(
                item => item.LocalAccountKey == accountKey && item.ReadAtUtc == null);
            query = throughSequence is not null
                ? query.Where(item => item.Sequence <= throughSequence.Value)
                : query.Where(item => ids!.Contains(item.Id));
            var notifications = await query.ToListAsync(ct).ConfigureAwait(false);
            long now = ToUnixMilliseconds(clock.GetUtcNow());
            foreach (var notification in notifications)
                notification.ReadAtUtc = now;
            return notifications.Count;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatStoreSummary> GetSummaryAsync(
        string localAccount,
        CancellationToken cancellationToken = default)
    {
        string accountKey = ChatIdentity.NormalizeAccount(localAccount);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        int direct = await CountUnreadAsync(context, accountKey, ChatTargetKind.Direct, cancellationToken).ConfigureAwait(false);
        int room = await CountUnreadAsync(context, accountKey, ChatTargetKind.Room, cancellationToken).ConfigureAwait(false);
        int notifications = await context.Notifications.CountAsync(
            item => item.LocalAccountKey == accountKey && item.ReadAtUtc == null, cancellationToken).ConfigureAwait(false);
        long revision = Math.Max(
            await context.ChatConversations.Where(item => item.LocalAccountKey == accountKey)
                .Select(item => (long?)item.Revision).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
            await context.ChatRoomSubscriptions.Where(item => item.LocalAccountKey == accountKey)
                .Select(item => (long?)item.Revision).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0);
        return new ChatStoreSummary(direct, room, notifications, revision);
    }

    public async Task<ChatRetentionResult> ApplyRetentionAsync(
        TimeSpan? privateMessageAge,
        TimeSpan? roomMessageAge,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (privateMessageAge is null && roomMessageAge is null)
            return new ChatRetentionResult(0, []);
        if (privateMessageAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(privateMessageAge));
        if (roomMessageAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(roomMessageAge));
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        long now = ToUnixMilliseconds(clock.GetUtcNow());
        long? privateMessageCutoff = privateMessageAge is null
            ? null
            : now - (long)privateMessageAge.Value.TotalMilliseconds;
        long? roomMessageCutoff = roomMessageAge is null
            ? null
            : now - (long)roomMessageAge.Value.TotalMilliseconds;
        return await ExecuteAsync(async (context, ct) =>
        {
            var messages = await context.ChatMessages
                .Where(item =>
                    privateMessageCutoff.HasValue
                    && item.TargetKind == nameof(ChatTargetKind.Direct)
                    && item.RecordedAtUtc < privateMessageCutoff.Value
                    || roomMessageCutoff.HasValue
                    && item.TargetKind == nameof(ChatTargetKind.Room)
                    && item.RecordedAtUtc < roomMessageCutoff.Value)
                .OrderBy(item => item.Sequence)
                .Take(batchSize)
                .ToListAsync(ct).ConfigureAwait(false);
            if (messages.Count == 0)
                return new ChatRetentionResult(0, []);
            Guid[] targetIds = messages.Select(item => item.TargetId).Distinct().ToArray();
            context.ChatMessages.RemoveRange(messages);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);

            var affected = new List<ChatRetentionTarget>(targetIds.Length);
            foreach (Guid targetId in targetIds)
            {
                long last = await context.ChatMessages
                    .Where(item => item.TargetId == targetId)
                    .Select(item => (long?)item.Sequence)
                    .MaxAsync(ct).ConfigureAwait(false) ?? 0;
                var conversation = await context.ChatConversations
                    .SingleOrDefaultAsync(item => item.Id == targetId, ct).ConfigureAwait(false);
                if (conversation is not null)
                {
                    conversation.LastMessageSequence = last;
                    conversation.LastReadSequence = Math.Min(conversation.LastReadSequence, last);
                    conversation.UpdatedAtUtc = now;
                    conversation.Revision++;
                    affected.Add(new ChatRetentionTarget(ChatTargetKind.Direct, targetId));
                    continue;
                }
                var room = await context.ChatRoomSubscriptions
                    .SingleOrDefaultAsync(item => item.Id == targetId, ct).ConfigureAwait(false);
                if (room is not null)
                {
                    room.LastMessageSequence = last;
                    room.LastReadSequence = Math.Min(room.LastReadSequence, last);
                    room.UpdatedAtUtc = now;
                    room.Revision++;
                    affected.Add(new ChatRetentionTarget(ChatTargetKind.Room, targetId));
                }
            }
            return new ChatRetentionResult(messages.Count, affected);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IncomingChatCommitResult> HydrateIncomingResultAsync(
        IncomingCommandResult result,
        ChatTargetKind kind,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var messageEntity = await context.ChatMessages.AsNoTracking().SingleAsync(
            item => item.Id == result.MessageId, cancellationToken).ConfigureAwait(false);
        ChatMessageRecord message = MapMessage(messageEntity);
        ConversationRecord? conversation = kind == ChatTargetKind.Direct
            ? await MapConversationAsync(context,
                await context.ChatConversations.AsNoTracking().SingleAsync(item => item.Id == message.TargetId, cancellationToken),
                cancellationToken).ConfigureAwait(false)
            : null;
        RoomSubscriptionRecord? room = kind == ChatTargetKind.Room
            ? await MapRoomAsync(context,
                await context.ChatRoomSubscriptions.AsNoTracking().SingleAsync(item => item.Id == message.TargetId, cancellationToken),
                cancellationToken).ConfigureAwait(false)
            : null;
        UserNotificationRecord? notification = result.NotificationId is { } notificationId
            ? await MapNotificationAsync(context,
                await context.Notifications.AsNoTracking().SingleAsync(item => item.Id == notificationId, cancellationToken),
                cancellationToken).ConfigureAwait(false)
            : null;
        return new IncomingChatCommitResult(result.Inserted, message, conversation, room, notification);
    }

    private async Task<ChatMessageRecord?> GetMessageAsync(
        string accountKey,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.ChatMessages.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == messageId && item.LocalAccountKey == accountKey, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : MapMessage(entity);
    }

    private static async Task<ChatRoomSubscriptionEntity> GetOrCreateRoomEntityAsync(
        SockseekDbContext context,
        string accountKey,
        string roomKey,
        string displayName,
        long now,
        CancellationToken cancellationToken)
    {
        var room = await context.ChatRoomSubscriptions.FirstOrDefaultAsync(
            item => item.LocalAccountKey == accountKey && item.RoomKey == roomKey, cancellationToken).ConfigureAwait(false);
        if (room is not null)
            return room;
        room = new ChatRoomSubscriptionEntity
        {
            Id = Guid.NewGuid(),
            LocalAccountKey = accountKey,
            RoomKey = roomKey,
            DisplayName = displayName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.ChatRoomSubscriptions.Add(room);
        return room;
    }

    private static async Task<long> NextMessageSequenceAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
    {
        ChatSequenceEntity sequence = await GetSequenceAsync(context, cancellationToken).ConfigureAwait(false);
        sequence.LastMessageSequence = checked(sequence.LastMessageSequence + 1);
        return sequence.LastMessageSequence;
    }

    private static async Task<long> NextNotificationSequenceAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
    {
        ChatSequenceEntity sequence = await GetSequenceAsync(context, cancellationToken).ConfigureAwait(false);
        sequence.LastNotificationSequence = checked(sequence.LastNotificationSequence + 1);
        return sequence.LastNotificationSequence;
    }

    private static async Task<ChatSequenceEntity> GetSequenceAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
    {
        ChatSequenceEntity? sequence = context.ChatSequences.Local.FirstOrDefault(item => item.Id == 1)
            ?? await context.ChatSequences.SingleOrDefaultAsync(
            item => item.Id == 1, cancellationToken).ConfigureAwait(false);
        if (sequence is not null)
            return sequence;

        sequence = new ChatSequenceEntity
        {
            Id = 1,
            LastMessageSequence = await context.ChatMessages
                .Select(item => (long?)item.Sequence)
                .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
            LastNotificationSequence = await context.Notifications
                .Select(item => (long?)item.Sequence)
                .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0,
        };
        context.ChatSequences.Add(sequence);
        return sequence;
    }

    private static ChatMessageRecord MapMessage(ChatMessageEntity entity)
        => new(
            entity.Id,
            entity.Sequence,
            entity.LocalAccountKey,
            Enum.Parse<ChatTargetKind>(entity.TargetKind),
            entity.TargetId,
            entity.TargetKey,
            entity.DisplayTarget,
            entity.SenderKey,
            entity.DisplaySender,
            Enum.Parse<ChatMessageDirection>(entity.Direction),
            entity.Body,
            FromUnixMilliseconds(entity.OccurredAtUtc),
            FromUnixMilliseconds(entity.RecordedAtUtc),
            Enum.Parse<ChatMessageState>(entity.SendState),
            entity.FailureReason,
            entity.ProtocolMessageId,
            entity.ProtocolTimestamp is { } timestamp ? FromUnixMilliseconds(timestamp) : null);

    private static async Task<ConversationRecord> MapConversationAsync(
        SockseekDbContext context,
        ChatConversationEntity entity,
        CancellationToken cancellationToken)
    {
        var last = entity.LastMessageSequence == 0
            ? null
            : await context.ChatMessages.AsNoTracking().SingleOrDefaultAsync(
                item => item.Sequence == entity.LastMessageSequence, cancellationToken).ConfigureAwait(false);
        int unread = await context.ChatMessages.CountAsync(item =>
            item.LocalAccountKey == entity.LocalAccountKey
            && item.TargetId == entity.Id
            && item.Direction == nameof(ChatMessageDirection.Incoming)
            && item.Sequence > entity.LastReadSequence, cancellationToken).ConfigureAwait(false);
        return new ConversationRecord(
            entity.Id, entity.LocalAccountKey, entity.PeerKey, entity.DisplayUsername,
            entity.ArchivedAtUtc is { } archived ? FromUnixMilliseconds(archived) : null,
            entity.LastReadSequence, entity.LastMessageSequence, entity.Revision,
            FromUnixMilliseconds(entity.CreatedAtUtc), FromUnixMilliseconds(entity.UpdatedAtUtc),
            unread, last is null ? null : MapMessage(last));
    }

    private static async Task<RoomSubscriptionRecord> MapRoomAsync(
        SockseekDbContext context,
        ChatRoomSubscriptionEntity entity,
        CancellationToken cancellationToken)
    {
        var last = entity.LastMessageSequence == 0
            ? null
            : await context.ChatMessages.AsNoTracking().SingleOrDefaultAsync(
                item => item.Sequence == entity.LastMessageSequence, cancellationToken).ConfigureAwait(false);
        int unread = await context.ChatMessages.CountAsync(item =>
            item.LocalAccountKey == entity.LocalAccountKey
            && item.TargetId == entity.Id
            && item.Direction == nameof(ChatMessageDirection.Incoming)
            && item.SenderKey != entity.LocalAccountKey
            && item.Sequence > entity.LastReadSequence, cancellationToken).ConfigureAwait(false);
        return new RoomSubscriptionRecord(
            entity.Id, entity.LocalAccountKey, entity.RoomKey, entity.DisplayName,
            entity.RuntimeDesired, Enum.Parse<ChatRoomKind>(entity.Kind),
            entity.LastReadSequence, entity.LastMessageSequence, entity.Revision,
            FromUnixMilliseconds(entity.CreatedAtUtc), FromUnixMilliseconds(entity.UpdatedAtUtc),
            unread, last is null ? null : MapMessage(last));
    }

    private static async Task<UserNotificationRecord> MapNotificationAsync(
        SockseekDbContext context,
        NotificationEntity entity,
        CancellationToken cancellationToken)
    {
        var message = await context.ChatMessages.AsNoTracking().SingleAsync(
            item => item.Id == entity.SourceMessageId, cancellationToken).ConfigureAwait(false);
        return new UserNotificationRecord(
            entity.Id, entity.Sequence, entity.LocalAccountKey,
            Enum.Parse<UserNotificationKind>(entity.Kind), entity.SourceMessageId,
            FromUnixMilliseconds(entity.CreatedAtUtc),
            entity.ReadAtUtc is { } read ? FromUnixMilliseconds(read) : null,
            MapMessage(message));
    }

    private static async Task<int> CountUnreadAsync(
        SockseekDbContext context,
        string accountKey,
        ChatTargetKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == ChatTargetKind.Direct)
        {
            return await (
                from message in context.ChatMessages
                join target in context.ChatConversations on message.TargetId equals target.Id
                where message.LocalAccountKey == accountKey
                      && message.Direction == nameof(ChatMessageDirection.Incoming)
                      && message.Sequence > target.LastReadSequence
                select message).CountAsync(cancellationToken).ConfigureAwait(false);
        }
        return await (
            from message in context.ChatMessages
            join target in context.ChatRoomSubscriptions on message.TargetId equals target.Id
            where message.LocalAccountKey == accountKey
                  && message.Direction == nameof(ChatMessageDirection.Incoming)
                  && message.SenderKey != accountKey
                  && message.Sequence > target.LastReadSequence
            select message).CountAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<SockseekDbContext, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        var command = new AwaitablePersistenceCommand<TResult>(action);
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        // Once admitted, the command is durable intent. Do not let request
        // cancellation abandon it while the single writer still commits it.
        return await command.Task.ConfigureAwait(false);
    }

    private static string EncodeSequenceCursor(long sequence)
        => Base64UrlEncode(sequence.ToString(CultureInfo.InvariantCulture));

    private static long? DecodeSequenceCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length > 128 || !long.TryParse(Base64UrlDecode(cursor), NumberStyles.None,
                CultureInfo.InvariantCulture, out long sequence) || sequence < 1)
            throw new ArgumentException("The chat cursor is invalid.", nameof(cursor));
        return sequence;
    }

    private static string EncodeSummaryCursor(long sequence, Guid id)
        => Base64UrlEncode($"{sequence.ToString(CultureInfo.InvariantCulture)}:{id:D}");

    private static (long Sequence, Guid Id)? DecodeSummaryCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length > 256)
            throw new ArgumentException("The chat cursor is invalid.", nameof(cursor));
        string[] parts = Base64UrlDecode(cursor).Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
            || sequence < 0
            || !Guid.TryParseExact(parts[1], "D", out Guid id))
            throw new ArgumentException("The chat cursor is invalid.", nameof(cursor));
        return (sequence, id);
    }

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        if (value.Length is 0 or > 256)
            throw new ArgumentException("The chat cursor is invalid.", nameof(value));
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(padded)); }
        catch (FormatException ex) { throw new ArgumentException("The chat cursor is invalid.", nameof(value), ex); }
    }

    private static long ToUnixMilliseconds(DateTimeOffset value)
        => value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMilliseconds(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private sealed record IncomingCommandResult(bool Inserted, Guid MessageId, Guid? NotificationId);
    private sealed record OutgoingCommandResult(OutgoingChatPreparationStatus Status, Guid MessageId);
}
