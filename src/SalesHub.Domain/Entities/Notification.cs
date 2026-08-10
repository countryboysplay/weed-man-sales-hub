namespace SalesHub.Domain.Entities;

/// <summary>
/// Canonical in-app notification (CLAUDE.md §15, docs/01). The Notification
/// Center rows are the source of truth; SignalR and Web Push are delivery
/// channels only. Required notifications always count toward the badge and
/// survive until their acknowledgment/retention rules allow removal.
/// </summary>
public class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Category { get; set; } = string.Empty;   // e.g. "security", "system"
    public bool Required { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Lock-screen-safe preview: never sensitive content (docs/03).</summary>
    public string SafePreview { get; set; } = string.Empty;

    public string ReferenceType { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public DateTimeOffset? SnoozedUntilUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public bool ProtectedFromClear { get; set; }
}

/// <summary>One delivery attempt of a notification over one channel.</summary>
public class NotificationDelivery
{
    public Guid Id { get; set; }
    public Guid NotificationId { get; set; }
    public string Channel { get; set; } = string.Empty;    // WebPush | SignalR
    public int Attempt { get; set; }
    public string State { get; set; } = string.Empty;      // Delivered | Failed
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
}

/// <summary>Web Push subscription per browser/device (docs/01, docs/03).</summary>
public class PushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DisabledAtUtc { get; set; }
}
