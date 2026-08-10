using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Notifications;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Notification Center (CLAUDE.md §15): rows here are the source of truth,
/// every route is own-resource scoped, required items always count.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/notifications").RequireAuthorization(Policies.Employee);
        group.MapGet("/", ListAsync);
        group.MapPost("/{id:guid}/read", ReadAsync);
        group.MapPost("/mark-all-read", MarkAllReadAsync);
        group.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync);
        group.MapPost("/{id:guid}/snooze", SnoozeAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        var push = api.MapGroup("/push-subscriptions").RequireAuthorization(Policies.Employee);
        push.MapPost("/", SubscribeAsync);
        push.MapDelete("/", UnsubscribeAsync);

        return api;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, IAppDb db, string? filter, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var query = db.Notifications.Where(n => n.UserId == userId);
        query = filter?.ToLowerInvariant() switch
        {
            "unread" => query.Where(n => n.ReadAtUtc == null),
            "required" => query.Where(n => n.Required && n.AcknowledgedAtUtc == null),
            _ => query,
        };

        var items = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(100)
            .Select(n => new NotificationDto(
                n.Id, n.Category, n.Required, n.Title, n.SafePreview,
                n.ReferenceType, n.ReferenceId, n.CreatedAtUtc,
                n.ReadAtUtc, n.AcknowledgedAtUtc, n.SnoozedUntilUtc))
            .ToListAsync(ct);

        // Badge semantics: required items count until handled, always.
        var unread = await db.Notifications
            .CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, ct);
        var requiredOutstanding = await db.Notifications
            .CountAsync(n => n.UserId == userId && n.Required && n.AcknowledgedAtUtc == null, ct);

        return Results.Ok(new NotificationListResponse(items, unread, requiredOutstanding));
    }

    private static async Task<IResult> ReadAsync(
        Guid id, HttpContext http, NotificationService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await service.MarkReadAsync(userId, id, ct)
            ? Results.NoContent()
            : Problems.NotFound(http, "Notification not found.");
    }

    private static async Task<IResult> MarkAllReadAsync(
        HttpContext http, NotificationService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var count = await service.MarkAllReadAsync(userId, ct);
        return Results.Ok(new { marked = count });
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id, HttpContext http, NotificationService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await service.AcknowledgeAsync(userId, id, ct)
            ? Results.NoContent()
            : Problems.NotFound(http, "Notification not found.");
    }

    private static async Task<IResult> SnoozeAsync(
        Guid id, SnoozeRequest request, HttpContext http,
        NotificationService service, BusinessTime businessTime, CancellationToken ct)
    {
        if (request.Until <= businessTime.UtcNow)
        {
            return Problems.Validation(http, "The snooze time must be in the future.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        return await service.SnoozeAsync(userId, id, request.Until, ct)
            ? Results.NoContent()
            : Problems.NotFound(http, "Notification not found.");
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, HttpContext http, NotificationService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await service.DeleteAsync(userId, id, ct) switch
        {
            NotificationService.DeleteResult.Deleted => Results.NoContent(),
            NotificationService.DeleteResult.Protected => Problems.Forbidden(
                http, "Required notifications stay until acknowledged.", "protectedNotification"),
            _ => Problems.NotFound(http, "Notification not found."),
        };
    }

    private static async Task<IResult> SubscribeAsync(
        PushSubscriptionRequest request, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Endpoint)
            || string.IsNullOrWhiteSpace(request.P256dh)
            || string.IsNullOrWhiteSpace(request.Auth))
        {
            return Problems.Validation(http, "endpoint, p256dh and auth are required.");
        }

        var (userId, session) = AuthEndpoints.Current(http);
        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
        if (existing is not null)
        {
            // Browser re-subscribed (or the device changed hands): rebind.
            existing.UserId = userId;
            existing.SessionId = session.Id;
            existing.P256dh = request.P256dh;
            existing.Auth = request.Auth;
            existing.Active = true;
            existing.DisabledAtUtc = null;
        }
        else
        {
            db.PushSubscriptions.Add(new PushSubscription
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                SessionId = session.Id,
                Endpoint = request.Endpoint,
                P256dh = request.P256dh,
                Auth = request.Auth,
                CreatedAtUtc = businessTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnsubscribeAsync(
        string endpoint, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var subscription = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == endpoint && s.UserId == userId, ct);
        if (subscription is null)
        {
            return Problems.NotFound(http, "No such subscription.");
        }

        subscription.Active = false;
        subscription.DisabledAtUtc = businessTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
