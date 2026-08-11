namespace SalesHub.Domain.Entities;

/// <summary>
/// An announcement (CLAUDE.md §8). Targets are expanded to rows at publish
/// time so completion stays stable even if teams change later. Up to three
/// pinned at once; pins auto-release after seven days while the
/// announcement itself stays active.
/// </summary>
public class Announcement
{
    public Guid Id { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public AnnouncementPriority Priority { get; set; }
    public bool RequireAcknowledgment { get; set; }
    public DateTimeOffset? ViewByUtc { get; set; }
    public DateTimeOffset? AcknowledgeByUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ScheduledPublishAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public int? PinRank { get; set; }                    // 1..3, null = unpinned
    public DateTimeOffset? AutoUnpinAtUtc { get; set; }
    public bool CompletionNotified { get; set; }

    /// <summary>Reminder cadence for outstanding users, hours; null = none.</summary>
    public int? ReminderEveryHours { get; set; }
    public DateTimeOffset? LastReminderAtUtc { get; set; }
}

public enum AnnouncementPriority
{
    Normal = 0,
    High = 1,
}

/// <summary>One targeted user, expanded at publication (docs/01).</summary>
public class AnnouncementTarget
{
    public Guid AnnouncementId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Management targets receive the announcement but are excluded
    /// from the completion percentage (CLAUDE.md §8).</summary>
    public bool CountsTowardCompletion { get; set; }

    public DateTimeOffset? SeenAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}
