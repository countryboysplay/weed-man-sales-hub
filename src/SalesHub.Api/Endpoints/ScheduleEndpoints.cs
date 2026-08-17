using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Presence;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Shift templates, per-user assignments, and dated schedule exceptions
/// (SCH-YYYY-#####) with optional required acknowledgment (docs/01, Wave 4).
/// Times are America/Chicago wall times on purpose — the evaluator converts
/// per date, so DST transitions never shift a 9 AM shift.
/// </summary>
public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder api)
    {
        var shifts = api.MapGroup("/shifts").RequireAuthorization(Policies.Management);
        shifts.MapGet("/templates", TemplatesAsync);
        shifts.MapPost("/templates", CreateTemplateAsync);
        shifts.MapPatch("/templates/{id:guid}", UpdateTemplateAsync);
        shifts.MapGet("/assignments", AssignmentsAsync);
        shifts.MapPost("/assignments", AssignAsync);

        var mine = api.MapGroup("/shifts/mine").RequireAuthorization(Policies.Employee);
        mine.MapGet("/", MyScheduleAsync);

        var exceptions = api.MapGroup("/schedule-exceptions");
        exceptions.MapPost("/", CreateExceptionAsync).RequireAuthorization(Policies.Management);
        exceptions.MapGet("/", ListExceptionsAsync).RequireAuthorization(Policies.Management);
        exceptions.MapGet("/mine", MyExceptionsAsync).RequireAuthorization(Policies.Employee);
        exceptions.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync)
            .RequireAuthorization(Policies.Employee);

        return api;
    }

    // ── templates ─────────────────────────────────────────────────────────────

    private static async Task<IResult> TemplatesAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.ShiftTemplates
            .OrderBy(t => t.Name).ThenBy(t => t.DayOfWeek)
            .Select(t => new ShiftTemplateDto(
                t.Id, t.Name, t.Role, t.DayOfWeek.ToString(),
                t.StartLocalTime, t.EndLocalTime, t.Active))
            .ToListAsync(ct));

    private static async Task<IResult> CreateTemplateAsync(
        CreateShiftTemplateRequest request, HttpContext http, IAppDb db, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 128)
        {
            return Problems.Validation(http, "A shift template needs a name up to 128 characters.");
        }

        if (!Roles.IsValid(request.Role))
        {
            return Problems.Validation(http, $"Unknown role '{request.Role}'.");
        }

        if (!Enum.TryParse<DayOfWeek>(request.DayOfWeek, ignoreCase: true, out var dayOfWeek))
        {
            return Problems.Validation(http, "DayOfWeek must be a weekday name (e.g. Monday).");
        }

        if (request.EndLocalTime <= request.StartLocalTime)
        {
            // Overnight shifts are out of scope for a door-to-door sales team.
            return Problems.Validation(http, "The shift end must be after its start (same day).");
        }

        var template = new ShiftTemplate
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Role = request.Role,
            DayOfWeek = dayOfWeek,
            StartLocalTime = request.StartLocalTime,
            EndLocalTime = request.EndLocalTime,
            Active = true,
        };
        db.ShiftTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/shifts/templates/{template.Id}", new ShiftTemplateDto(
            template.Id, template.Name, template.Role, template.DayOfWeek.ToString(),
            template.StartLocalTime, template.EndLocalTime, template.Active));
    }

    private static async Task<IResult> UpdateTemplateAsync(
        Guid id, UpdateTemplateBody body, HttpContext http, IAppDb db, CancellationToken ct)
    {
        var template = await db.ShiftTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null)
        {
            return Problems.NotFound(http, "Shift template not found.");
        }

        if (body.Active is { } active)
        {
            template.Active = active;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── assignments ───────────────────────────────────────────────────────────

    private static async Task<IResult> AssignmentsAsync(
        Guid? userId, IAppDb db, CancellationToken ct)
    {
        var query = db.UserShiftAssignments.AsQueryable();
        if (userId is { } uid)
        {
            query = query.Where(a => a.UserId == uid);
        }

        return Results.Ok(await query
            .Join(db.ShiftTemplates, a => a.ShiftTemplateId, t => t.Id, (a, t) => new { a, t })
            .OrderBy(x => x.a.UserId).ThenBy(x => x.t.DayOfWeek)
            .Select(x => new ShiftAssignmentDto(
                x.a.Id, x.a.UserId, x.t.Id, x.t.Name, x.t.DayOfWeek.ToString(),
                x.t.StartLocalTime, x.t.EndLocalTime, x.a.StartDate, x.a.EndDate))
            .ToListAsync(ct));
    }

    private static async Task<IResult> AssignAsync(
        AssignShiftRequest request, HttpContext http, IAppDb db,
        IIdentityService identity, BusinessTime businessTime, CancellationToken ct)
    {
        if (request.EndDate is { } end && end < request.StartDate)
        {
            return Problems.Validation(http, "The assignment end date is before its start date.");
        }

        if (await identity.FindByIdAsync(request.UserId, ct) is null)
        {
            return Problems.NotFound(http, "User not found.");
        }

        var template = await db.ShiftTemplates
            .FirstOrDefaultAsync(t => t.Id == request.ShiftTemplateId, ct);
        if (template is null)
        {
            return Problems.NotFound(http, "Shift template not found.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        var assignment = new UserShiftAssignment
        {
            Id = Guid.CreateVersion7(),
            UserId = request.UserId,
            ShiftTemplateId = template.Id,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            AssignedByUserId = actorId,
            AssignedAtUtc = businessTime.UtcNow,
        };
        db.UserShiftAssignments.Add(assignment);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/shifts/assignments/{assignment.Id}", new { assignment.Id });
    }

    private static async Task<IResult> MyScheduleAsync(
        HttpContext http, IAppDb db, BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var today = businessTime.Today;
        return Results.Ok(await db.UserShiftAssignments
            .Where(a => a.UserId == userId
                && a.StartDate <= today.AddDays(7)
                && (a.EndDate == null || a.EndDate >= today))
            .Join(db.ShiftTemplates.Where(t => t.Active),
                a => a.ShiftTemplateId, t => t.Id, (a, t) => new { a, t })
            .OrderBy(x => x.t.DayOfWeek)
            .Select(x => new ShiftAssignmentDto(
                x.a.Id, x.a.UserId, x.t.Id, x.t.Name, x.t.DayOfWeek.ToString(),
                x.t.StartLocalTime, x.t.EndLocalTime, x.a.StartDate, x.a.EndDate))
            .ToListAsync(ct));
    }

    // ── schedule exceptions ───────────────────────────────────────────────────

    private static async Task<IResult> CreateExceptionAsync(
        CreateScheduleExceptionRequest request, HttpContext http, IAppDb db,
        IIdentityService identity, IPublicIdGenerator publicIds, IAuditWriter audit,
        NotificationService notifications, BusinessTime businessTime, CancellationToken ct)
    {
        var label = request.Label?.Trim() ?? "";
        if (label.Length is 0 or > 128)
        {
            return Problems.Validation(http, "A schedule exception needs a label up to 128 characters.");
        }

        if (request.ReplacementStartLocal.HasValue != request.ReplacementEndLocal.HasValue)
        {
            return Problems.Validation(http,
                "Provide both replacement start and end, or neither for a whole-day exception.");
        }

        if (request is { ReplacementStartLocal: { } s, ReplacementEndLocal: { } e } && e <= s)
        {
            return Problems.Validation(http, "The replacement window end must be after its start.");
        }

        var target = await identity.FindByIdAsync(request.UserId, ct);
        if (target is null)
        {
            return Problems.NotFound(http, "User not found.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        ScheduleException? created = null;
        await db.ExecuteInTransactionAsync(async token =>
        {
            created = new ScheduleException
            {
                Id = Guid.CreateVersion7(),
                PublicId = await publicIds.NextAsync("SCH", token),
                UserId = request.UserId,
                Date = request.Date,
                ReplacementStartLocal = request.ReplacementStartLocal,
                ReplacementEndLocal = request.ReplacementEndLocal,
                Label = label,
                Reason = request.Reason?.Trim() ?? "",
                SuspendsPresence = request.SuspendsPresence,
                AcknowledgmentRequired = request.AcknowledgmentRequired,
                AcknowledgeByUtc = request.AcknowledgeByUtc,
                CreatedByUserId = actorId,
                CreatedAtUtc = businessTime.UtcNow,
            };
            db.ScheduleExceptions.Add(created);

            await audit.WriteAsync(new AuditEntry(
                "schedule", "schedule.exceptionCreated", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actorId,
                TargetType = "ScheduleException",
                TargetId = created.Id.ToString(),
                PublicRecordId = created.PublicId,
                After = new
                {
                    request.UserId,
                    date = request.Date.ToString("O"),
                    request.SuspendsPresence,
                    request.AcknowledgmentRequired,
                },
            }, token);

            // The affected employee always hears about it; a required ack
            // makes the notification required (badge-persistent) too.
            _ = await notifications.CreateAsync(request.UserId,
                new NotificationService.NewNotification(
                    "schedule",
                    $"Schedule exception for {request.Date:MMM d}",
                    $"{label} ({created.PublicId}).",
                    Required: request.AcknowledgmentRequired,
                    ReferenceType: "ScheduleException",
                    ReferenceId: created.PublicId), token);

            await db.SaveChangesAsync(token);
        }, ct);

        return Results.Created($"/api/v1/schedule-exceptions/{created!.Id}", ToDto(created));
    }

    private static async Task<IResult> ListExceptionsAsync(
        Guid? userId, DateOnly? from, DateOnly? to, IAppDb db, CancellationToken ct)
    {
        var query = db.ScheduleExceptions.AsQueryable();
        if (userId is { } uid)
        {
            query = query.Where(x => x.UserId == uid);
        }

        if (from is { } f)
        {
            query = query.Where(x => x.Date >= f);
        }

        if (to is { } t)
        {
            query = query.Where(x => x.Date <= t);
        }

        var rows = await query.OrderByDescending(x => x.Date).Take(200).ToListAsync(ct);
        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> MyExceptionsAsync(
        HttpContext http, IAppDb db, BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var horizon = businessTime.Today.AddDays(-7);
        var rows = await db.ScheduleExceptions
            .Where(x => x.UserId == userId && x.Date >= horizon)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id, HttpContext http, IAppDb db, IAuditWriter audit,
        BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var exception = await db.ScheduleExceptions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (exception is null || exception.UserId != userId)
        {
            // Same 404 for "not yours": no existence oracle across users.
            return Problems.NotFound(http, "Schedule exception not found.");
        }

        if (exception.AcknowledgedAtUtc is null)
        {
            exception.AcknowledgedAtUtc = businessTime.UtcNow;
            await audit.WriteAsync(new AuditEntry(
                "schedule", "schedule.exceptionAcknowledged", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = userId,
                TargetType = "ScheduleException",
                TargetId = exception.Id.ToString(),
                PublicRecordId = exception.PublicId,
            }, ct);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static ScheduleExceptionDto ToDto(ScheduleException x) => new(
        x.Id, x.PublicId, x.UserId, x.Date,
        x.ReplacementStartLocal, x.ReplacementEndLocal,
        x.Label, x.Reason, x.SuspendsPresence,
        x.AcknowledgmentRequired, x.AcknowledgeByUtc, x.AcknowledgedAtUtc);

    private sealed record UpdateTemplateBody(bool? Active);
}
