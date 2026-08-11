namespace SalesHub.Domain.Entities;

/// <summary>Badge library: built-ins plus management-managed custom badges.</summary>
public class RecognitionBadge
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
    public bool BuiltIn { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>
/// A recognition (CLAUDE.md §13): management issues, everyone reacts and
/// comments, active in the feed for 30 days then auto-archived.
/// </summary>
public class Recognition
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid AuthorUserId { get; set; }
    public Guid BadgeId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ActiveUntilUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
}

public class RecognitionReaction
{
    public Guid RecognitionId { get; set; }
    public Guid UserId { get; set; }
    public string Reaction { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class RecognitionComment
{
    public Guid Id { get; set; }
    public Guid RecognitionId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
