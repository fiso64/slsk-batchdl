using System.Globalization;
using System.Text;
using Sockseek.Core.Chat;

namespace Sockseek.Api;

public sealed record ChatMessageDto(
    Guid MessageId,
    long Sequence,
    ChatTargetKind TargetKind,
    Guid TargetId,
    string Sender,
    ChatMessageDirection Direction,
    string Text,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    ChatMessageState State,
    string? FailureReason);

public sealed record ConversationSummaryDto(
    Guid ConversationId,
    string Username,
    bool PrivateMessagesBlocked,
    bool Archived,
    int UnreadCount,
    long LastReadSequence,
    long Revision,
    ChatMessageDto? LastMessage);

public sealed record ConversationPageDto(
    IReadOnlyList<ConversationSummaryDto> Items,
    string? NextCursor);

public sealed record ChatMessagePageDto(
    IReadOnlyList<ChatMessageDto> Items,
    string? NextCursor);

public sealed record AvailableRoomDto(
    string Name,
    int UserCount,
    ChatRoomKind Kind,
    bool Owned,
    bool Moderated);

public sealed record AvailableRoomPageDto(
    IReadOnlyList<AvailableRoomDto> Items,
    string? NextCursor,
    DateTimeOffset ObservedAtUtc,
    bool Truncated);

public sealed record ChatRoomSummaryDto(
    Guid RoomId,
    string Name,
    bool Configured,
    bool Remembered,
    bool Desired,
    ChatRoomKind Kind,
    bool Owned,
    bool Moderated,
    ChatRoomJoinPhase Phase,
    string? FailureReason,
    int MemberCount,
    long MemberRevision,
    bool RosterComplete,
    int UnreadCount,
    long LastReadSequence,
    long Revision,
    ChatMessageDto? LastMessage);

public sealed record RoomMemberDto(
    string Username,
    string Presence,
    string? CountryCode,
    bool IsOwner,
    bool IsOperator);

public sealed record RoomMemberPageDto(
    IReadOnlyList<RoomMemberDto> Items,
    string? NextCursor,
    long Revision,
    bool Complete);

public sealed record ChatRoomDetailDto(
    ChatRoomSummaryDto Summary,
    string? Owner,
    IReadOnlyList<string> Operators);

public sealed record ChatRoomPageDto(
    IReadOnlyList<ChatRoomSummaryDto> Items,
    string? NextCursor);

public sealed record UserNotificationDto(
    Guid NotificationId,
    long Sequence,
    UserNotificationKind Kind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc,
    string Actor,
    ChatTargetKind TargetKind,
    Guid TargetId,
    string TargetName,
    Guid SourceMessageId,
    string Preview,
    string ResourcePath);

public sealed record NotificationPageDto(
    IReadOnlyList<UserNotificationDto> Items,
    string? NextCursor);

public sealed record SendPrivateMessageRequestDto(
    Guid MessageId,
    string Username,
    string Text);

public sealed record SendChatMessageRequestDto(
    Guid MessageId,
    string Text);

public sealed record MarkChatReadRequestDto(Guid ThroughMessageId);

public sealed record ArchiveConversationRequestDto(bool Archived = true);

public sealed record JoinRoomRequestDto(string RoomName, bool Remember = true);

public sealed record AddRoomMemberRequestDto(string Username);

public sealed record MarkNotificationsReadRequestDto(
    long? ThroughSequence,
    IReadOnlyList<Guid>? Ids);

public static class ChatDtoMapper
{
    public static ChatMessageDto ToDto(ChatMessageRecord value)
        => new(
            value.MessageId,
            value.Sequence,
            value.TargetKind,
            value.TargetId,
            value.DisplaySender,
            value.Direction,
            value.Body,
            value.OccurredAtUtc,
            value.RecordedAtUtc,
            value.State,
            value.FailureReason);

    public static ConversationSummaryDto ToDto(
        ConversationRecord value,
        bool privateMessagesBlocked)
        => new(
            value.ConversationId,
            value.DisplayUsername,
            privateMessagesBlocked,
            value.ArchivedAtUtc is not null,
            value.UnreadCount,
            value.LastReadSequence,
            value.Revision,
            value.LastMessage is null ? null : ToDto(value.LastMessage));

    public static UserNotificationDto ToDto(UserNotificationRecord value)
        => new(
            value.NotificationId,
            value.Sequence,
            value.Kind,
            value.CreatedAtUtc,
            value.ReadAtUtc,
            value.SourceMessage.DisplaySender,
            value.SourceMessage.TargetKind,
            value.SourceMessage.TargetId,
            value.SourceMessage.DisplayTarget,
            value.SourceMessageId,
            Preview(value.SourceMessage.Body),
            value.SourceMessage.TargetKind == ChatTargetKind.Direct
                ? $"/api/chat/conversations/{value.SourceMessage.TargetId:D}"
                : $"/api/chat/rooms/{value.SourceMessage.TargetId:D}");

    private static string Preview(string body)
    {
        var result = new StringBuilder(ChatLimits.NotificationPreviewCharacters);
        bool pendingSpace = false;
        int count = 0;
        foreach (Rune rune in body.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune)
                || category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                if (count == ChatLimits.NotificationPreviewCharacters)
                    break;
                result.Append(' ');
                pendingSpace = false;
                count++;
            }
            if (count == ChatLimits.NotificationPreviewCharacters)
                break;
            result.Append(rune.ToString());
            count++;
        }
        return result.ToString();
    }
}
