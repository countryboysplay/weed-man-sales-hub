using System.Text.Json;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

public sealed class EfOutboxWriter(
    SalesHubDbContext db,
    ICorrelationAccessor correlation,
    BusinessTime businessTime) : IOutboxWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task EnqueueAsync(
        string eventType, object payload, CancellationToken cancellationToken = default)
    {
        var now = businessTime.UtcNow;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload, Json),
            OccurredAtUtc = now,
            AvailableAtUtc = now,
            CorrelationId = correlation.CorrelationId,
        });
        return Task.CompletedTask; // rides the caller's transaction — that is the point
    }
}
