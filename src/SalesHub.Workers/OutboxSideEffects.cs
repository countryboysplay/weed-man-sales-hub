using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Extra delivery work the outbox dispatcher runs after the SignalR publish
/// for specific event types. Side-effect failures are recorded, never allowed
/// to fail the outbox row — the canonical state is already committed and the
/// realtime event already delivered.
/// </summary>
public interface IOutboxSideEffect
{
    bool Handles(string eventType);

    Task ExecuteAsync(
        EventEnvelope envelope, IServiceProvider scopedServices,
        CancellationToken cancellationToken);
}

/// <summary>
/// Web Push fan-out for notifications.notificationCreated.v1 (docs/03):
/// minimal, lock-screen-safe payload to each active subscription of the
/// target user; delivery rows record the outcome; dead subscriptions are
/// deactivated. A recipient on Do Not Disturb gets no push — the durable
/// notification row still lands and the DND-exit catch-up summarizes it
/// (CLAUDE.md §12).
/// </summary>
public sealed class NotificationWebPushSideEffect(ILogger<NotificationWebPushSideEffect> logger)
    : IOutboxSideEffect
{
    public bool Handles(string eventType) => eventType == EventTypes.NotificationCreated;

    public async Task ExecuteAsync(
        EventEnvelope envelope, IServiceProvider scopedServices, CancellationToken ct)
    {
        var sender = scopedServices.GetService(typeof(IWebPushSender)) as IWebPushSender;
        var db = (SalesHubDbContext)scopedServices.GetService(typeof(SalesHubDbContext))!;
        if (sender is null || !sender.Enabled)
        {
            return;
        }

        var payload = envelope.Payload;
        var userId = payload.GetProperty("userId").GetGuid();
        var notificationId = payload.GetProperty("notificationId").GetGuid();

        var recipientOnDnd = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.PresenceStatus == PresenceStatus.Dnd)
            .FirstOrDefaultAsync(ct);
        if (recipientOnDnd)
        {
            logger.LogDebug(
                "Web Push suppressed for notification {NotificationId}: recipient is on DND",
                notificationId);
            return;
        }

        var subscriptions = await db.PushSubscriptions
            .Where(s => s.UserId == userId && s.Active)
            .ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            return;
        }

        // Minimal safe payload only (docs/03): no sensitive content on a lock screen.
        var pushJson = JsonSerializer.Serialize(new
        {
            notificationId,
            title = payload.GetProperty("title").GetString(),
            preview = payload.GetProperty("safePreview").GetString(),
            category = payload.GetProperty("category").GetString(),
            referenceType = payload.TryGetProperty("referenceType", out var rt) ? rt.GetString() : "",
            referenceId = payload.TryGetProperty("referenceId", out var ri) ? ri.GetString() : "",
        });

        foreach (var subscription in subscriptions)
        {
            var attempt = await db.NotificationDeliveries
                .CountAsync(d => d.NotificationId == notificationId && d.Channel == "WebPush", ct) + 1;
            var result = await sender.SendAsync(subscription, pushJson, ct);

            if (result == PushSendResult.Gone)
            {
                subscription.Active = false;
                subscription.DisabledAtUtc = DateTimeOffset.UtcNow;
            }

            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                Id = Guid.CreateVersion7(),
                NotificationId = notificationId,
                Channel = "WebPush",
                Attempt = attempt,
                State = result == PushSendResult.Delivered ? "Delivered" : "Failed",
                LastError = result == PushSendResult.Delivered ? null : result.ToString(),
                DeliveredAtUtc = result == PushSendResult.Delivered ? DateTimeOffset.UtcNow : null,
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogDebug("Web Push fan-out for notification {NotificationId}: {Count} subscription(s)",
            notificationId, subscriptions.Count);
    }
}
