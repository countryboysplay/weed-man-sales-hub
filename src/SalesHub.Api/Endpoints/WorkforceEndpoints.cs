using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Workforce;
using SalesHub.Contracts.Workforce;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Wave 4 part 2 surface: time off, breaks, technical reports/grace, and the
/// unified approvals queue. Authorization is enforced here server-side; the
/// client's idea of its own rank is never trusted.
/// </summary>
public static class WorkforceEndpoints
{
    public static IEndpointRouteBuilder MapWorkforceEndpoints(this IEndpointRouteBuilder api)
    {
        var timeOff = api.MapGroup("/time-off").RequireAuthorization(Policies.Employee);
        timeOff.MapGet("/types", TimeOffTypesAsync);
        timeOff.MapPost("/", RequestTimeOffAsync);
        timeOff.MapGet("/mine", MyTimeOffAsync);
        timeOff.MapPost("/{id:guid}/cancellation-request", RequestCancellationAsync);
        timeOff.MapGet("/pending", PendingTimeOffAsync).RequireAuthorization(Policies.Management);
        timeOff.MapGet("/{id:guid}/coverage", CoverageAsync).RequireAuthorization(Policies.Management);
        timeOff.MapPost("/{id:guid}/approve", ApproveTimeOffAsync)
            .RequireAuthorization(Policies.Management);
        timeOff.MapPost("/{id:guid}/deny", DenyTimeOffAsync)
            .RequireAuthorization(Policies.Management);
        timeOff.MapPost("/cancellation-requests/{id:guid}/decide", DecideCancellationAsync)
            .RequireAuthorization(Policies.Management);

        var breaks = api.MapGroup("/breaks").RequireAuthorization(Policies.Employee);
        breaks.MapGet("/types", BreakTypesAsync);
        breaks.MapPost("/start", StartBreakAsync);
        breaks.MapPost("/end", EndBreakAsync);
        breaks.MapGet("/mine", MyBreaksAsync);
        breaks.MapPost("/{id:guid}/corrections", RequestBreakCorrectionAsync);
        breaks.MapPost("/corrections/{id:guid}/decide", DecideBreakCorrectionAsync)
            .RequireAuthorization(Policies.Management);
        breaks.MapPatch("/{id:guid}", EditBreakAsync).RequireAuthorization(Policies.Management);

        var technical = api.MapGroup("/technical-reports").RequireAuthorization(Policies.Employee);
        technical.MapPost("/", FileTechnicalReportAsync);
        technical.MapGet("/", ListTechnicalReportsAsync).RequireAuthorization(Policies.Management);
        technical.MapPost("/{id:guid}/grants", GrantGraceAsync)
            .RequireAuthorization(Policies.Management);

        api.MapGet("/approvals", ApprovalsQueueAsync).RequireAuthorization(Policies.Management);

        return api;
    }

    // ── time off ──────────────────────────────────────────────────────────────

    private static async Task<IResult> TimeOffTypesAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.TimeOffTypes
            .Where(t => t.Active)
            .OrderBy(t => t.SortOrder)
            .Select(t => new TimeOffTypeDto(t.Id, t.Label, t.Paid))
            .ToListAsync(ct));

    private static async Task<IResult> RequestTimeOffAsync(
        CreateTimeOffRequest request, HttpContext http, TimeOffService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var result = await service.RequestAsync(userId, request, ct);
        if (result.Request is null)
        {
            return result.Code == "overlap"
                ? Problems.Conflict(http, result.Error!, "overlap")
                : Problems.Validation(http, result.Error!);
        }

        return Results.Created($"/api/v1/time-off/{result.Request.Id}",
            new { result.Request.Id, result.Request.PublicId });
    }

    private static async Task<IResult> MyTimeOffAsync(
        HttpContext http, IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return Results.Ok(await TimeOffDtosAsync(
            db, identity, q => q.Where(t => t.UserId == userId), ct));
    }

    private static async Task<IResult> PendingTimeOffAsync(
        IAppDb db, IIdentityService identity, CancellationToken ct) =>
        Results.Ok(await TimeOffDtosAsync(
            db, identity, q => q.Where(t => t.Status == TimeOffStatus.Pending), ct));

    private static async Task<IResult> CoverageAsync(
        Guid id, HttpContext http, IAppDb db, TimeOffService service, CancellationToken ct)
    {
        var request = await db.TimeOffRequests.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (request is null)
        {
            return Problems.NotFound(http, "Time-off request not found.");
        }

        return Results.Ok(await service.CheckCoverageAsync(request, ct));
    }

    private static async Task<IResult> ApproveTimeOffAsync(
        Guid id, ApproveTimeOffRequest body, HttpContext http,
        TimeOffService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var result = await service.ApproveAsync(id, actorId, body.Note, body.ConfirmCoverage, ct);
        return result.Request is not null
            ? Results.NoContent()
            : result.Code switch
            {
                "notFound" => Problems.NotFound(http, result.Error!),
                "coverageBlocked" => Problems.Conflict(http, result.Error!, "coverageBlocked"),
                "coverageConfirmationRequired" =>
                    Problems.Conflict(http, result.Error!, "coverageConfirmationRequired"),
                "notPending" => Problems.Conflict(http, result.Error!, "notPending"),
                _ => Problems.Validation(http, result.Error!),
            };
    }

    private static async Task<IResult> DenyTimeOffAsync(
        Guid id, DenyTimeOffRequest body, HttpContext http,
        TimeOffService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var result = await service.DenyAsync(id, actorId, body.Reason, ct);
        return result.Request is not null
            ? Results.NoContent()
            : result.Code switch
            {
                "notFound" => Problems.NotFound(http, result.Error!),
                "notPending" => Problems.Conflict(http, result.Error!, "notPending"),
                _ => Problems.Validation(http, result.Error!),
            };
    }

    private static async Task<IResult> RequestCancellationAsync(
        Guid id, HttpContext http, TimeOffService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var result = await service.RequestCancellationAsync(id, userId, ct);
        return result.Request is not null
            ? Results.NoContent()
            : result.Code switch
            {
                "notFound" => Problems.NotFound(http, result.Error!),
                "duplicate" => Problems.Conflict(http, result.Error!, "duplicate"),
                _ => Problems.Conflict(http, result.Error!, result.Code ?? "conflict"),
            };
    }

    private static async Task<IResult> DecideCancellationAsync(
        Guid id, DecideCancellationRequest body, HttpContext http,
        TimeOffService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var result = await service.DecideCancellationAsync(id, actorId, body.Approve, ct);
        return result.Request is not null
            ? Results.NoContent()
            : Problems.NotFound(http, result.Error!);
    }

    // ── breaks ────────────────────────────────────────────────────────────────

    private static async Task<IResult> BreakTypesAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.BreakTypes
            .Where(t => t.Active)
            .OrderBy(t => t.LimitMinutes)
            .Select(t => new BreakTypeDto(t.Id, t.Label, t.LimitMinutes))
            .ToListAsync(ct));

    private static async Task<IResult> StartBreakAsync(
        StartBreakRequest request, HttpContext http, BreakService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var result = await service.StartAsync(userId, request.BreakTypeId, ct);
        return result.Session is not null
            ? Results.Created($"/api/v1/breaks/{result.Session.Id}", new { result.Session.Id })
            : result.Code == "breakActive"
                ? Problems.Conflict(http, result.Error!, "breakActive")
                : Problems.Validation(http, result.Error!);
    }

    private static async Task<IResult> EndBreakAsync(
        HttpContext http, BreakService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var result = await service.EndAsync(userId, ct);
        return result.Session is not null
            ? Results.NoContent()
            : Problems.Conflict(http, result.Error!, "noActiveBreak");
    }

    private static async Task<IResult> MyBreaksAsync(
        HttpContext http, IAppDb db, BusinessTime businessTime,
        DateOnly? date, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var businessDate = date ?? businessTime.Today;
        return Results.Ok(await db.BreakSessions
            .Where(b => b.UserId == userId && b.BusinessDate == businessDate)
            .Join(db.BreakTypes, b => b.BreakTypeId, t => t.Id, (b, t) => new { b, t })
            .OrderBy(x => x.b.StartedAtUtc)
            .Select(x => new BreakSessionDto(
                x.b.Id, x.b.UserId, x.t.Label, x.t.LimitMinutes,
                x.b.StartedAtUtc, x.b.EndedAtUtc, x.b.BusinessDate, x.b.OverrunFlagged))
            .ToListAsync(ct));
    }

    private static async Task<IResult> RequestBreakCorrectionAsync(
        Guid id, RequestBreakCorrectionRequest request, HttpContext http,
        BreakService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var result = await service.RequestCorrectionAsync(
            userId, id, request.CorrectedStartAtUtc, request.CorrectedEndAtUtc,
            request.Reason, ct);
        return result.Correction is not null
            ? Results.Created($"/api/v1/breaks/corrections/{result.Correction.Id}",
                new { result.Correction.Id, result.Correction.PublicId })
            : Problems.Validation(http, result.Error!);
    }

    private static async Task<IResult> DecideBreakCorrectionAsync(
        Guid id, DecideBreakCorrectionRequest body, HttpContext http,
        BreakService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var result = await service.DecideCorrectionAsync(id, actorId, body.Approve, ct);
        return result.Correction is not null
            ? Results.NoContent()
            : Problems.NotFound(http, result.Error!);
    }

    private static async Task<IResult> EditBreakAsync(
        Guid id, EditBreakRequest request, HttpContext http,
        BreakService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var result = await service.EditAsync(
            id, actorId, request.StartAtUtc, request.EndAtUtc, request.Reason, ct);
        return result.Session is not null
            ? Results.NoContent()
            : result.Code == "notFound"
                ? Problems.NotFound(http, result.Error!)
                : Problems.Validation(http, result.Error!);
    }

    // ── technical ─────────────────────────────────────────────────────────────

    private static async Task<IResult> FileTechnicalReportAsync(
        CreateTechnicalReportRequest request, HttpContext http,
        TechnicalReportService service, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var result = await service.FileAsync(
            userId, request.IssueType, request.Description, request.Page,
            request.AppVersion, session.BrowserFamily, ct);
        return result.Report is not null
            ? Results.Created($"/api/v1/technical-reports/{result.Report.Id}",
                new { result.Report.Id, result.Report.PublicId })
            : Problems.Validation(http, result.Error!);
    }

    private static async Task<IResult> ListTechnicalReportsAsync(
        IAppDb db, IIdentityService identity, BusinessTime businessTime, CancellationToken ct)
    {
        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var now = businessTime.UtcNow;
        var reports = await db.TechnicalReports
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(100)
            .ToListAsync(ct);
        var reportIds = reports.Select(r => r.Id).ToList();
        var activeGrants = await db.TechnicalGrants
            .Where(g => reportIds.Contains(g.TechnicalReportId)
                && g.StartAtUtc <= now && g.EndAtUtc > now)
            .Select(g => g.TechnicalReportId)
            .ToListAsync(ct);
        var activeSet = activeGrants.ToHashSet();

        return Results.Ok(reports
            .Select(r => new TechnicalReportDto(
                r.Id, r.PublicId, r.ReporterUserId,
                names.GetValueOrDefault(r.ReporterUserId, "Unknown"),
                r.IssueType, r.Description, r.Page, r.CreatedAtUtc,
                activeSet.Contains(r.Id)))
            .ToList());
    }

    private static async Task<IResult> GrantGraceAsync(
        Guid id, GrantTechnicalGraceRequest request, HttpContext http,
        TechnicalReportService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var result = await service.GrantGraceAsync(
            id, actorId, request.StartAtUtc, request.EndAtUtc, request.Reason, ct);
        return result.Grant is not null
            ? Results.Created($"/api/v1/technical-reports/{id}/grants/{result.Grant.Id}",
                new { result.Grant.Id })
            : Problems.Validation(http, result.Error!);
    }

    // ── unified approvals queue ───────────────────────────────────────────────

    private static async Task<IResult> ApprovalsQueueAsync(
        IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var timeOff = await TimeOffDtosAsync(
            db, identity, q => q.Where(t => t.Status == TimeOffStatus.Pending), ct, names);

        var cancellations = await db.TimeOffCancellationRequests
            .Where(c => c.ResultStatus == null)
            .Join(db.TimeOffRequests, c => c.TimeOffRequestId, t => t.Id, (c, t) => new { c, t })
            .OrderBy(x => x.c.CreatedAtUtc)
            .ToListAsync(ct);
        var cancellationDtos = cancellations
            .Select(x => new TimeOffCancellationDto(
                x.c.Id, x.t.Id, x.t.PublicId, x.c.RequestedByUserId,
                names.GetValueOrDefault(x.c.RequestedByUserId, "Unknown"),
                x.t.StartDate, x.t.EndDate, x.c.CreatedAtUtc))
            .ToList();

        var corrections = await db.BreakCorrectionRequests
            .Where(c => c.Status == BreakCorrectionStatus.Pending)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);
        var correctionDtos = corrections
            .Select(c => new BreakCorrectionDto(
                c.Id, c.PublicId, c.BreakSessionId, c.RequestedByUserId,
                names.GetValueOrDefault(c.RequestedByUserId, "Unknown"),
                c.OriginalStartAtUtc, c.OriginalEndAtUtc,
                c.CorrectedStartAtUtc, c.CorrectedEndAtUtc,
                c.Reason, c.Status.ToString(), c.CreatedAtUtc))
            .ToList();

        var openResets = await db.PasswordResetRequests
            .CountAsync(r => r.Status == PasswordResetRequestStatus.Open, ct);

        return Results.Ok(new ApprovalsQueueDto(
            timeOff.Count + cancellationDtos.Count + correctionDtos.Count + openResets,
            timeOff, cancellationDtos, correctionDtos, openResets));
    }

    // ── shared projection ─────────────────────────────────────────────────────

    private static async Task<List<TimeOffRequestDto>> TimeOffDtosAsync(
        IAppDb db, IIdentityService identity,
        Func<IQueryable<TimeOffRequest>, IQueryable<TimeOffRequest>> filter,
        CancellationToken ct, Dictionary<Guid, string>? names = null)
    {
        names ??= (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var rows = await filter(db.TimeOffRequests)
            .Join(db.TimeOffTypes, t => t.TypeId, y => y.Id, (t, y) => new { t, y })
            .OrderByDescending(x => x.t.CreatedAtUtc)
            .Take(200)
            .ToListAsync(ct);
        var requestIds = rows.Select(x => x.t.Id).ToList();
        var pendingCancellations = (await db.TimeOffCancellationRequests
                .Where(c => requestIds.Contains(c.TimeOffRequestId) && c.ResultStatus == null)
                .Select(c => c.TimeOffRequestId)
                .ToListAsync(ct))
            .ToHashSet();

        return rows
            .Select(x => new TimeOffRequestDto(
                x.t.Id, x.t.PublicId, x.t.UserId,
                names.GetValueOrDefault(x.t.UserId, "Unknown"),
                x.y.Label, x.t.FullDay, x.t.StartDate, x.t.EndDate,
                x.t.StartLocalTime, x.t.EndLocalTime, x.t.Reason,
                x.t.Status.ToString(), x.t.ReviewNote, x.t.DenialReason,
                x.t.CreatedAtUtc, x.t.ReviewedAtUtc,
                pendingCancellations.Contains(x.t.Id)))
            .ToList();
    }
}
