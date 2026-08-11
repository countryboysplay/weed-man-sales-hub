namespace SalesHub.Domain.Entities;

/// <summary>
/// A DM or group conversation (CLAUDE.md §7). Everyone can DM anyone;
/// only management creates groups; users cannot leave groups; mandatory
/// groups cannot be muted.
/// </summary>
public class Conversation
{
    public Guid Id { get; set; }
    public ConversationType Type { get; set; }
    public string Name { get; set; } = string.Empty;      // groups only
    public bool Mandatory { get; set; }                    // groups only
    public Guid CreatedByUserId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Canonical "guidA:guidB" (sorted) for direct conversations so
    /// a pair of users always lands in the same DM. Null for groups.</summary>
    public string? DirectKey { get; set; }

    public static string MakeDirectKey(Guid a, Guid b) =>
        a.CompareTo(b) <= 0 ? $"{a:N}:{b:N}" : $"{b:N}:{a:N}";
}

public enum ConversationType
{
    Direct = 0,
    Group = 1,
}

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset JoinedAtUtc { get; set; }

    /// <summary>Null = not muted. Mandatory groups refuse muting.</summary>
    public DateTimeOffset? MutedAtUtc { get; set; }

    public Guid? LastReadMessageId { get; set; }
    public DateTimeOffset? LastReadAtUtc { get; set; }
}

/// <summary>
/// A chat message. Deletion erases the body in canonical state — there is no
/// hidden copy anywhere, and later Owner inspection sees only what remains
/// (CLAUDE.md §7, docs/01).
/// </summary>
public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? EditedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public Guid? ReplyToMessageId { get; set; }
}

public class MessageAttachment
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid BlobId { get; set; }
    public string OriginalName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long ByteLength { get; set; }
}

public class MessageReaction
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public string Reaction { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
