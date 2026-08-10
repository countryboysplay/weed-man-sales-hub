using System.Text.Json;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

public sealed class EfAuditWriter(
    SalesHubDbContext db,
    ICorrelationAccessor correlation,
    BusinessTime businessTime) : IAuditWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            Category = entry.Category,
            Action = entry.Action,
            ActorUserId = entry.ActorUserId,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            PublicRecordId = entry.PublicRecordId,
            Reason = entry.Reason,
            BeforeJson = entry.Before is null ? null : JsonSerializer.Serialize(entry.Before, Json),
            AfterJson = entry.After is null ? null : JsonSerializer.Serialize(entry.After, Json),
            OccurredAtUtc = businessTime.UtcNow,
            SessionId = entry.SessionId,
            DeviceId = entry.DeviceId,
            CorrelationId = correlation.CorrelationId,
            RetentionClass = entry.RetentionClass,
        });
        return Task.CompletedTask; // persisted by the ambient SaveChanges/transaction
    }
}
