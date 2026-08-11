namespace SalesHub.Contracts.Announcements;

public sealed record CreateAnnouncementRequest(
    string Title,
    string Body,
    string Priority = "Normal",              // Normal | High
    bool RequireAcknowledgment = false,
    IReadOnlyList<Guid>? TargetUserIds = null,   // null/empty = everyone
    bool PublishNow = true,
    DateTimeOffset? ScheduledPublishAt = null,
    DateTimeOffset? ViewBy = null,
    DateTimeOffset? AcknowledgeBy = null,
    int? ReminderEveryHours = null);

public sealed record AnnouncementDto(
    Guid Id,
    string Title,
    string Body,
    string Priority,
    bool RequireAcknowledgment,
    DateTimeOffset? PublishedAt,
    int? PinRank,
    DateTimeOffset? ViewBy,
    DateTimeOffset? AcknowledgeBy,
    DateTimeOffset? SeenAt,
    DateTimeOffset? AcknowledgedAt);

public sealed record AnnouncementProgressResponse(
    Guid AnnouncementId,
    int TargetCount,
    int CountedTargets,
    int Seen,
    int Acknowledged,
    int Percent,
    IReadOnlyList<Guid> OutstandingUserIds);
