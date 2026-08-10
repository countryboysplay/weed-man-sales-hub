namespace SalesHub.Contracts.Notifications;

public sealed record NotificationDto(
    Guid Id,
    string Category,
    bool Required,
    string Title,
    string SafePreview,
    string ReferenceType,
    string ReferenceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? SnoozedUntil);

public sealed record NotificationListResponse(
    IReadOnlyList<NotificationDto> Items,
    int UnreadCount,
    int RequiredOutstandingCount);

public sealed record SnoozeRequest(DateTimeOffset Until);

public sealed record PushSubscriptionRequest(
    string Endpoint,
    string P256dh,
    string Auth);
