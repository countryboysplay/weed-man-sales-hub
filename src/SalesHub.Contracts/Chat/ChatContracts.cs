namespace SalesHub.Contracts.Chat;

public sealed record StartDirectRequest(Guid UserId);

public sealed record CreateGroupRequest(
    string Name,
    IReadOnlyList<Guid> MemberUserIds,
    bool Mandatory = false);

public sealed record UpdateGroupRequest(
    string? Name,
    bool? Mandatory,
    IReadOnlyList<Guid>? AddMemberUserIds,
    IReadOnlyList<Guid>? RemoveMemberUserIds);

public sealed record SendMessageRequest(
    string Body,
    Guid? ReplyToMessageId = null,
    IReadOnlyList<Guid>? MentionedUserIds = null,
    bool MentionEveryone = false,
    IReadOnlyList<Guid>? AttachmentBlobIds = null);

public sealed record EditMessageRequest(string Body);

public sealed record ReactionRequest(string Reaction);

public sealed record MarkReadRequest(Guid MessageId);

public sealed record ConversationDto(
    Guid Id,
    string Type,
    string Name,               // display name: group name or other member's name
    bool Mandatory,
    bool Muted,
    int UnreadCount,
    MessageDto? LastMessage,
    IReadOnlyList<MemberDto> Members);

public sealed record MemberDto(
    Guid UserId,
    string DisplayName,
    Guid? LastReadMessageId);

public sealed record AttachmentDto(
    Guid BlobId,
    string OriginalName,
    string ContentType,
    long ByteLength);

public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderUserId,
    string SenderDisplayName,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool Deleted,
    Guid? ReplyToMessageId,
    IReadOnlyList<AttachmentDto> Attachments,
    IReadOnlyDictionary<string, int> Reactions);

public sealed record MessagePageResponse(
    IReadOnlyList<MessageDto> Messages,
    Guid? NextBefore);
