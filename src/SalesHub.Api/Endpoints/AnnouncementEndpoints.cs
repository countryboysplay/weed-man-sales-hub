using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Announcements;
using SalesHub.Contracts.Announcements;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class AnnouncementEndpoints
{
    public static IEndpointRouteBuilder MapAnnouncementEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/announcements");
        group.MapGet("/", FeedAsync).RequireAuthorization(Policies.Employee);
        group.MapPost("/{id:guid}/seen", (Guid id, HttpContext http, AnnouncementService svc, CancellationToken ct) =>
            MarkAsync(id, http, svc, false, ct)).RequireAuthorization(Policies.Employee);
        group.MapPost("/{id:guid}/acknowledge", (Guid id, HttpContext http, AnnouncementService svc, CancellationToken ct) =>
            MarkAsync(id, http, svc, true, ct)).RequireAuthorization(Policies.Employee);

        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/publish", PublishAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/archive", ArchiveAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/pin", (Guid id, HttpContext http, AnnouncementService svc, CancellationToken ct) =>
            PinAsync(id, http, svc, true, ct)).RequireAuthorization(Policies.Management);
        group.MapDelete("/{id:guid}/pin", (Guid id, HttpContext http, AnnouncementService svc, CancellationToken ct) =>
            PinAsync(id, http, svc, false, ct)).RequireAuthorization(Policies.Management);
        group.MapGet("/{id:guid}/progress", ProgressAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/remind-outstanding", RemindAsync).RequireAuthorization(Policies.Management);

        return api;
    }

    private static async Task<IResult> FeedAsync(
        HttpContext http, IAppDb db, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var feed = await db.AnnouncementTargets
            .Where(t => t.UserId == userId)
            .Join(db.Announcements.Where(a => a.PublishedAtUtc != null && a.ArchivedAtUtc == null),
                t => t.AnnouncementId, a => a.Id,
                (t, a) => new { t, a })
            .OrderByDescending(x => x.a.PinRank != null)
            .ThenBy(x => x.a.PinRank)
            .ThenByDescending(x => x.a.PublishedAtUtc)
            .Take(100)
            .Select(x => new AnnouncementDto(
                x.a.Id, x.a.Title, x.a.Body, x.a.Priority.ToString(),
                x.a.RequireAcknowledgment, x.a.PublishedAtUtc, x.a.PinRank,
                x.a.ViewByUtc, x.a.AcknowledgeByUtc, x.t.SeenAtUtc, x.t.AcknowledgedAtUtc))
            .ToListAsync(ct);
        return Results.Ok(feed);
    }

    private static async Task<IResult> CreateAsync(
        CreateAnnouncementRequest request, HttpContext http,
        AnnouncementService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        if (!Enum.TryParse<AnnouncementPriority>(request.Priority, true, out var priority))
        {
            return Problems.Validation(http, "priority must be Normal or High.");
        }

        var (failure, error, announcement) = await service.CreateAsync(new AnnouncementService.DraftInput(
            userId, request.Title, request.Body, priority, request.RequireAcknowledgment,
            request.TargetUserIds, request.ScheduledPublishAt, request.ViewBy,
            request.AcknowledgeBy, request.ReminderEveryHours),
            request.PublishNow && request.ScheduledPublishAt is null, ct);

        return failure == AnnouncementService.Failure.None
            ? Results.Created($"/api/v1/announcements/{announcement!.Id}", new { announcement.Id })
            : Problems.Validation(http, error!);
    }

    private static async Task<IResult> PublishAsync(
        Guid id, HttpContext http, AnnouncementService service, CancellationToken ct) =>
        await service.PublishAsync(id, ct) == AnnouncementService.Failure.None
            ? Results.NoContent()
            : Problems.NotFound(http, "Announcement not found.");

    private static async Task<IResult> ArchiveAsync(
        Guid id, HttpContext http, IAppDb db, BusinessTime businessTime, CancellationToken ct)
    {
        var announcement = await db.Announcements
            .FirstOrDefaultAsync(a => a.Id == id && a.ArchivedAtUtc == null, ct);
        if (announcement is null)
        {
            return Problems.NotFound(http, "Announcement not found.");
        }

        announcement.ArchivedAtUtc = businessTime.UtcNow;
        announcement.PinRank = null;
        announcement.AutoUnpinAtUtc = null;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PinAsync(
        Guid id, HttpContext http, AnnouncementService service, bool pin, CancellationToken ct) =>
        await service.PinAsync(id, pin, ct) switch
        {
            AnnouncementService.Failure.None => Results.NoContent(),
            AnnouncementService.Failure.PinLimit => Problems.Conflict(
                http, "Up to three announcements can be pinned.", "pinLimit"),
            _ => Problems.NotFound(http, "Announcement not found."),
        };

    private static async Task<IResult> MarkAsync(
        Guid id, HttpContext http, AnnouncementService service, bool acknowledge, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await service.MarkAsync(id, userId, acknowledge, ct) == AnnouncementService.Failure.None
            ? Results.NoContent()
            : Problems.NotFound(http, "You are not targeted by this announcement.");
    }

    private static async Task<IResult> ProgressAsync(
        Guid id, HttpContext http, AnnouncementService service, CancellationToken ct)
    {
        var progress = await service.ProgressAsync(id, ct);
        return progress is null
            ? Problems.NotFound(http, "Announcement not found.")
            : Results.Ok(new AnnouncementProgressResponse(
                id, progress.TargetCount, progress.CountedTargets, progress.Seen,
                progress.Acknowledged, progress.Percent, progress.Outstanding));
    }

    private static async Task<IResult> RemindAsync(
        Guid id, AnnouncementService service, CancellationToken ct) =>
        Results.Ok(new { reminded = await service.RemindOutstandingAsync(id, ct) });
}
