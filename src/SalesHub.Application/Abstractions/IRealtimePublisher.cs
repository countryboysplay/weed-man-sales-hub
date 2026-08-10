using SalesHub.Contracts.Events;

namespace SalesHub.Application.Abstractions;

/// <summary>
/// Delivery port used by the outbox dispatcher. Implemented in the Api layer
/// over SignalR hub contexts. Group targeting follows docs/03; authorization
/// is enforced by hub policies, not by group names.
/// </summary>
public interface IRealtimePublisher
{
    Task PublishToUserAsync(Guid userId, EventEnvelope envelope, CancellationToken cancellationToken = default);
    Task PublishToGroupAsync(string group, EventEnvelope envelope, CancellationToken cancellationToken = default);
}
