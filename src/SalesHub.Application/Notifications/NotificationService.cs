using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Notifications;

/// <summary>
/// Canonical notification model (CLAUDE.md §15). Rows here are the source of
/// truth; SignalR/Web Push deliveries fan out from the outbox event written
/// in the same transaction. Required notifications cannot be deleted and
/// always count toward the badge.
/// </summary>
public sealed class NotificationService(
    IAppDb db,
    IOutboxWriter outbox,
    IIdentityService identity,
    BusinessTime businessTime)
{
    public sealed record NewNotification(
        string Category,
        string Title,
        string SafePreview,
        bool Required = false,
        string ReferenceType = "",
        string ReferenceId = "",
        bool ProtectedFromClear = false);

    /// <summary>Creates the durable row + outbox event. Call inside the caller's
    /// transaction; nothing is delivered until that transaction commits.</summary>
    public async Task<Notification> CreateAsync(
        Guid userId, NewNotification input, CancellationToken ct = default)
    {
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Category = input.Category,
            Required = input.Required,
            Title = input.Title,
            SafePreview = input.SafePreview,
            ReferenceType = input.ReferenceType,
            ReferenceId = input.ReferenceId,
            CreatedAtUtc = businessTime.UtcNow,
            ProtectedFromClear = input.ProtectedFromClear || input.Required,
        };
        db.Notifications.Add(notification);

        await outbox.EnqueueAsync(EventTypes.NotificationCreated, new
        {
            userId,
            notificationId = notification.Id,
            category = notification.Category,
            required = notification.Required,
            title = notification.Title,
            safePreview = notification.SafePreview,
            referenceType = notification.ReferenceType,
            referenceId = notification.ReferenceId,
        }, ct);

        return notification;
    }

    /// <summary>One notification per management user (expanded rows, like
    /// announcement targets — stable even if roles change later).</summary>
    public async Task CreateForManagementAsync(
        NewNotification input, Guid? excludeUserId = null, CancellationToken ct = default)
    {
        var management = await identity.ListUsersAsync(
            new UserQuery(Roles: Roles.Management), ct);
        foreach (var user in management)
        {
            if (user.Id == excludeUserId)
            {
                continue;
            }

            _ = await CreateAsync(user.Id, input, ct);
        }
    }

    public async Task<bool> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notification is null)
        {
            return false;
        }

        notification.ReadAtUtc ??= businessTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        return await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAtUtc, now), ct);
    }

    public async Task<bool> AcknowledgeAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notification is null)
        {
            return false;
        }

        var now = businessTime.UtcNow;
        notification.AcknowledgedAtUtc ??= now;
        notification.ReadAtUtc ??= now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SnoozeAsync(
        Guid userId, Guid notificationId, DateTimeOffset until, CancellationToken ct = default)
    {
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notification is null)
        {
            return false;
        }

        notification.SnoozedUntilUtc = until;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deletes one notification. Required/protected rows refuse —
    /// they leave the center only via acknowledgment/retention rules.</summary>
    public async Task<DeleteResult> DeleteAsync(
        Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notification is null)
        {
            return DeleteResult.NotFound;
        }

        if (notification.ProtectedFromClear && notification.AcknowledgedAtUtc is null)
        {
            return DeleteResult.Protected;
        }

        db.Notifications.Remove(notification);
        await db.SaveChangesAsync(ct);
        return DeleteResult.Deleted;
    }

    public enum DeleteResult { Deleted, NotFound, Protected }
}
