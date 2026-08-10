namespace SalesHub.Domain.Entities;

/// <summary>
/// Structured audit event (docs/04). Before/after are JSON documents; the
/// retention class decides lifetime and only retention jobs may delete rows.
/// Never stores passwords, cookies, TOTP material, or deleted chat content.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;   // e.g. "auth", "users"
    public string Action { get; set; } = string.Empty;     // e.g. "auth.sessionRevoked"
    public Guid? ActorUserId { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string? PublicRecordId { get; set; }
    public string? Reason { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public Guid? SessionId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public AuditRetentionClass RetentionClass { get; set; }
}
