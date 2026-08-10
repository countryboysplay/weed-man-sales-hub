namespace SalesHub.Domain.Entities;

/// <summary>
/// Transactional outbox row (docs/03, db/schema-notes.sql). Inserted in the
/// same transaction as the state change it announces; a worker delivers it to
/// SignalR/push afterwards. Failed rows are retried with backoff and are never
/// deleted before the retention window — poison rows surface in health.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;   // module.entityAction.v1
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? LastError { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public bool Failed { get; set; }
}
