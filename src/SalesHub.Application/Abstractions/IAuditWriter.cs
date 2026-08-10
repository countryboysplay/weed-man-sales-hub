using SalesHub.Domain;

namespace SalesHub.Application.Abstractions;

/// <summary>
/// Writes a structured audit event (docs/04) in the ambient transaction.
/// Values passed here must already be safe to persist: no passwords, no
/// cookies, no TOTP material, no deleted message content.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed record AuditEntry(
    string Category,
    string Action,
    AuditRetentionClass RetentionClass)
{
    public Guid? ActorUserId { get; init; }
    public string TargetType { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;
    public string? PublicRecordId { get; init; }
    public string? Reason { get; init; }
    public object? Before { get; init; }
    public object? After { get; init; }
    public Guid? SessionId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
}
