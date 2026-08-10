using System.Text.Json;

namespace SalesHub.Contracts.Events;

/// <summary>
/// Standard realtime event envelope (docs/03). Event contracts are versioned
/// inside <see cref="EventType"/> (module.entityAction.v1), not in hub paths.
/// </summary>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    JsonElement Payload);
