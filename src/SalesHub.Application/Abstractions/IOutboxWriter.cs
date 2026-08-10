namespace SalesHub.Application.Abstractions;

/// <summary>
/// Adds a durable event to the transactional outbox. Must be called inside
/// the same transaction as the state change it announces; the row is
/// delivered to SignalR/push by the outbox worker after commit (docs/03).
/// </summary>
public interface IOutboxWriter
{
    Task EnqueueAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default);
}
