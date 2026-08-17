using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Support;
using SalesHub.Contracts.Support;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class SupportEndpoints
{
    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/support").RequireAuthorization(Policies.Employee);
        group.MapPost("/", CreateAsync);
        group.MapGet("/my", MyTicketsAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/replies", ReplyAsync);
        group.MapPost("/{id:guid}/confirm-closure", ConfirmClosureAsync);

        group.MapGet("/queue", QueueAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/assign", AssignAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/collaborators", AddCollaboratorAsync)
            .RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/priority", SetPriorityAsync)
            .RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/resolve", ResolveAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/force-close", ForceCloseAsync)
            .RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/reopen", ReopenAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/links", LinkAsync).RequireAuthorization(Policies.Management);

        return api;
    }

    private static bool IsManagement(HttpContext http) =>
        Roles.Management.Contains(
            http.Items[AuthConstants.UserRoleItemKey] as string ?? "", StringComparer.Ordinal);

    private static bool IsManagerOrOwner(HttpContext http) =>
        Roles.ManagerOrOwner.Contains(
            http.Items[AuthConstants.UserRoleItemKey] as string ?? "", StringComparer.Ordinal);

    private static async Task<IResult> CreateAsync(
        CreateSupportTicketRequest request, HttpContext http, IAppDb db,
        IIdentityService identity, SupportService service, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var result = await service.CreateAsync(
            userId, session, request.IssueType, request.Description,
            request.Page, request.AppVersion, request.AttachmentBlobId, ct);
        if (result.Ticket is null)
        {
            return Problems.Validation(http, result.Error!);
        }

        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        return Results.Created($"/api/v1/support/{result.Ticket.Id}", new CreateTicketResponse(
            result.Ticket.Id,
            result.Ticket.PublicId,
            result.Ticket.Priority.ToString(),
            (result.Similar ?? []).Select(t => Summary(t, names)).ToList()));
    }

    private static async Task<IResult> MyTicketsAsync(
        HttpContext http, IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var tickets = await db.SupportTickets
            .Where(t => t.ReporterUserId == userId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(100)
            .ToListAsync(ct);
        return Results.Ok(tickets.Select(t => Summary(t, names)).ToList());
    }

    private static async Task<IResult> QueueAsync(
        string? status, string? priority, IAppDb db, IIdentityService identity,
        HttpContext http, CancellationToken ct)
    {
        var query = db.SupportTickets.AsQueryable();
        if (!string.IsNullOrEmpty(status)
            && Enum.TryParse<SupportTicketStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(t => t.Status == parsedStatus);
        }
        else if (string.IsNullOrEmpty(status))
        {
            query = query.Where(t => t.Status != SupportTicketStatus.Closed);
        }

        if (!string.IsNullOrEmpty(priority)
            && Enum.TryParse<SupportPriority>(priority, true, out var parsedPriority))
        {
            query = query.Where(t => t.Priority == parsedPriority);
        }

        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var tickets = await query
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAtUtc)
            .Take(200)
            .ToListAsync(ct);
        return Results.Ok(tickets.Select(t => Summary(t, names)).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext http, IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        var (userId, _) = AuthEndpoints.Current(http);
        var management = IsManagement(http);
        if (ticket is null || (!management && ticket.ReporterUserId != userId))
        {
            return Problems.NotFound(http, "Ticket not found.");
        }

        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var messages = db.SupportMessages.Where(m => m.TicketId == id);
        if (!management)
        {
            // Internal notes never reach the reporter (CLAUDE.md §14).
            messages = messages.Where(m => m.Visibility == SupportMessageVisibility.EmployeeReply);
        }

        var messageList = await messages.OrderBy(m => m.CreatedAtUtc).ToListAsync(ct);
        var collaborators = await db.SupportCollaborators
            .Where(c => c.TicketId == id).Select(c => c.UserId).ToListAsync(ct);
        var links = await db.SupportLinks
            .Where(l => l.TicketId == id).Select(l => l.TargetPublicId).ToListAsync(ct);

        // Advanced diagnostics are Manager/Owner only (permission matrix).
        var diagnostics = IsManagerOrOwner(http)
            ? new SupportDiagnosticsDto(
                ticket.AppVersion, ticket.BrowserFamily, ticket.DeviceId, ticket.CorrelationId)
            : null;

        return Results.Ok(new SupportTicketDto(
            ticket.Id, ticket.PublicId, ticket.ReporterUserId,
            names.GetValueOrDefault(ticket.ReporterUserId, "Unknown"),
            ticket.IssueType, ticket.Description, ticket.Page,
            ticket.Priority.ToString(),
            ticket.SuggestedPriority?.ToString(), ticket.SuggestedPriorityReason,
            ticket.Status.ToString(),
            ticket.PrimaryAssigneeUserId,
            ticket.PrimaryAssigneeUserId is { } assignee
                ? names.GetValueOrDefault(assignee, "Unknown")
                : null,
            ticket.CreatedAtUtc, ticket.ResolvedAtUtc, ticket.ClosedAtUtc,
            ticket.ForceClosed, ticket.ReporterConfirmedClosure,
            messageList.Select(m => new SupportMessageDto(
                m.Id, m.AuthorUserId, names.GetValueOrDefault(m.AuthorUserId, "Unknown"),
                m.Visibility.ToString(), m.Body, m.CreatedAtUtc)).ToList(),
            collaborators, links, diagnostics));
    }

    private static async Task<IResult> ReplyAsync(
        Guid id, SupportReplyRequest request, HttpContext http,
        SupportService service, CancellationToken ct)
    {
        var visibility = SupportMessageVisibility.EmployeeReply;
        if (!string.IsNullOrEmpty(request.Visibility)
            && !Enum.TryParse(request.Visibility, true, out visibility))
        {
            return Problems.Validation(http, "Visibility must be EmployeeReply or InternalNote.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var error = await service.ReplyAsync(
            id, userId, IsManagement(http), request.Body, visibility, ct);
        return error is null
            ? Results.NoContent()
            : error.Contains("not found") ? Problems.NotFound(http, error)
            : error.Contains("management-only") ? Problems.Forbidden(http, error)
            : Problems.Validation(http, error);
    }

    private static async Task<IResult> ConfirmClosureAsync(
        Guid id, HttpContext http, SupportService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var error = await service.ConfirmClosureAsync(id, userId, ct);
        return error is null
            ? Results.NoContent()
            : error.Contains("not found")
                ? Problems.NotFound(http, error)
                : Problems.Conflict(http, error, "notResolved");
    }

    private static async Task<IResult> AssignAsync(
        Guid id, AssignTicketRequest request, HttpContext http,
        SupportService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.AssignAsync(id, actorId, request.PrimaryAssigneeUserId, ct);
        return error is null ? Results.NoContent() : Problems.NotFound(http, error);
    }

    private static async Task<IResult> AddCollaboratorAsync(
        Guid id, AddCollaboratorRequest request, HttpContext http,
        SupportService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.AddCollaboratorAsync(id, actorId, request.UserId, ct);
        return error is null ? Results.NoContent() : Problems.NotFound(http, error);
    }

    private static async Task<IResult> SetPriorityAsync(
        Guid id, SetTicketPriorityRequest request, HttpContext http,
        SupportService service, CancellationToken ct)
    {
        if (!Enum.TryParse<SupportPriority>(request.Priority, true, out var priority))
        {
            return Problems.Validation(http, "Priority must be Low, Normal, High, or Critical.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.SetPriorityAsync(id, actorId, priority, ct);
        return error is null ? Results.NoContent() : Problems.NotFound(http, error);
    }

    private static async Task<IResult> ResolveAsync(
        Guid id, HttpContext http, SupportService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.ResolveAsync(id, actorId, ct);
        return error is null
            ? Results.NoContent()
            : error.Contains("not found")
                ? Problems.NotFound(http, error)
                : Problems.Conflict(http, error, "alreadyResolved");
    }

    private static async Task<IResult> ForceCloseAsync(
        Guid id, HttpContext http, SupportService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.ForceCloseAsync(id, actorId, ct);
        return error is null
            ? Results.NoContent()
            : error.Contains("not found")
                ? Problems.NotFound(http, error)
                : Problems.Conflict(http, error, "alreadyClosed");
    }

    private static async Task<IResult> ReopenAsync(
        Guid id, HttpContext http, SupportService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.ReopenAsync(id, actorId, ct);
        return error is null
            ? Results.NoContent()
            : error.Contains("not found")
                ? Problems.NotFound(http, error)
                : Problems.Conflict(http, error, "notReopenable");
    }

    private static async Task<IResult> LinkAsync(
        Guid id, LinkTicketRequest request, HttpContext http,
        SupportService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.LinkAsync(id, actorId, request.TargetPublicId, ct);
        return error is null
            ? Results.NoContent()
            : error.Contains("not found")
                ? Problems.NotFound(http, error)
                : Problems.Validation(http, error);
    }

    private static SupportTicketSummaryDto Summary(
        SupportTicket t, Dictionary<Guid, string> names) => new(
        t.Id, t.PublicId, t.ReporterUserId,
        names.GetValueOrDefault(t.ReporterUserId, "Unknown"),
        t.IssueType, t.Priority.ToString(), t.Status.ToString(), t.CreatedAtUtc);
}
