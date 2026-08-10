using Microsoft.AspNetCore.SignalR;
using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;

namespace SalesHub.Api.Hubs;

/// <summary>
/// IRealtimePublisher over the AppHub. Every event goes out as the standard
/// envelope on the single "event" method; clients dispatch on eventType.
/// </summary>
public sealed class SignalRRealtimePublisher(IHubContext<AppHub> hub) : IRealtimePublisher
{
    private const string Method = "event";

    public Task PublishToUserAsync(
        Guid userId, EventEnvelope envelope, CancellationToken cancellationToken = default) =>
        hub.Clients.Group($"user:{userId}").SendAsync(Method, envelope, cancellationToken);

    public Task PublishToGroupAsync(
        string group, EventEnvelope envelope, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(group).SendAsync(Method, envelope, cancellationToken);
}
