using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Records;
using SalesHub.Contracts.Records;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Employee management record surface (CLAUDE.md §13). All of it is
/// management-only; employees never see their management record.
/// </summary>
public static class ManagementRecordEndpoints
{
    public static IEndpointRouteBuilder MapManagementRecordEndpoints(this IEndpointRouteBuilder api)
    {
        var employees = api.MapGroup("/employees").RequireAuthorization(Policies.Management);
        employees.MapGet("/{id:guid}/management-record", GetRecordAsync);
        employees.MapPost("/{id:guid}/notes", AddNoteAsync);

        var notes = api.MapGroup("/notes").RequireAuthorization(Policies.Management);
        notes.MapPost("/{id:guid}/followups", AddFollowupAsync);
        notes.MapPost("/{id:guid}/resolve", ResolveAsync);
        notes.MapPost("/{id:guid}/reopen", ReopenAsync);
        notes.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync);
        notes.MapPost("/{id:guid}/links", LinkAsync);
        notes.MapPost("/links/{linkId:guid}/remove", UnlinkAsync);

        var tags = api.MapGroup("/management-tags").RequireAuthorization(Policies.Management);
        tags.MapGet("/", ListTagsAsync);
        tags.MapPost("/", CreateTagAsync);
        tags.MapPost("/{id:guid}/apply", ApplyTagAsync);
        tags.MapPost("/{id:guid}/remove", RemoveTagAsync);

        return api;
    }

    private static async Task<IResult> GetRecordAsync(
        Guid id, HttpContext http, IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var employee = await identity.GetUserDetailsAsync(id, ct);
        if (employee is null)
        {
            return Problems.NotFound(http, "Employee not found.");
        }

        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var notes = await db.ManagementNotes
            .Where(n => n.EmployeeUserId == id)
            .OrderByDescending(n => n.PinnedRank != null)
            .ThenByDescending(n => n.PinnedRank)
            .ThenByDescending(n => n.CreatedAtUtc)
            .Take(200)
            .ToListAsync(ct);
        var noteIds = notes.Select(n => n.Id).ToList();
        var notePublicIds = notes.Select(n => n.PublicId).ToList();

        var followups = (await db.ManagementNoteFollowups
                .Where(f => noteIds.Contains(f.NoteId))
                .OrderBy(f => f.CreatedAtUtc)
                .ToListAsync(ct))
            .ToLookup(f => f.NoteId);
        var ackTargets = (await db.ManagementNoteAckTargets
                .Where(a => noteIds.Contains(a.NoteId))
                .ToListAsync(ct))
            .ToLookup(a => a.NoteId);
        var links = (await db.RecordLinks
                .Where(l => notePublicIds.Contains(l.SourcePublicId))
                .OrderBy(l => l.CreatedAtUtc)
                .ToListAsync(ct))
            .ToLookup(l => l.SourcePublicId);
        var tags = (await db.TaggedEntities
                .Where(t => notePublicIds.Contains(t.EntityPublicId))
                .Join(db.ManagementTags, t => t.TagId, m => m.Id,
                    (t, m) => new { t.EntityPublicId, m.Label })
                .ToListAsync(ct))
            .ToLookup(t => t.EntityPublicId, t => t.Label);

        var noteDtos = notes.Select(n => new ManagementNoteDto(
            n.Id, n.PublicId, n.EmployeeUserId, n.Category,
            n.Priority.ToString(), n.Status.ToString(), n.Body,
            n.CreatedByUserId, names.GetValueOrDefault(n.CreatedByUserId, "Unknown"),
            n.CreatedAtUtc, n.PinnedRank, n.ResolutionNote, n.ResolvedAtUtc,
            followups[n.Id].Select(f => new NoteFollowupDto(
                f.Id, f.AuthorUserId, names.GetValueOrDefault(f.AuthorUserId, "Unknown"),
                f.Kind.ToString(), f.Body, f.CreatedAtUtc)).ToList(),
            ackTargets[n.Id].Select(a => new NoteAckTargetDto(
                a.TargetUserId, names.GetValueOrDefault(a.TargetUserId, "Unknown"),
                a.AcknowledgedAtUtc)).ToList(),
            links[n.PublicId].Select(l => new RecordLinkDto(
                l.Id, l.TargetPublicId, l.CreatedAtUtc, l.RemovedAtUtc, l.RemoveReason)).ToList(),
            tags[n.PublicId].ToList())).ToList();

        var related = await RelatedRecordsAsync(db, id, ct);

        return Results.Ok(new EmployeeManagementRecordDto(
            employee.Id, employee.DisplayName, employee.Role, employee.IsActive,
            noteDtos, related));
    }

    private static async Task<List<RelatedRecordDto>> RelatedRecordsAsync(
        IAppDb db, Guid employeeId, CancellationToken ct)
    {
        var related = new List<RelatedRecordDto>();
        related.AddRange(await db.PresenceFlags
            .Where(f => f.UserId == employeeId)
            .OrderByDescending(f => f.StartAtUtc).Take(20)
            .Select(f => new RelatedRecordDto(
                f.PublicId, "PresenceFlag", f.Status.ToString(), f.StartAtUtc))
            .ToListAsync(ct));
        related.AddRange(await db.TimeOffRequests
            .Where(t => t.UserId == employeeId)
            .OrderByDescending(t => t.CreatedAtUtc).Take(20)
            .Select(t => new RelatedRecordDto(
                t.PublicId, "TimeOff", t.Status.ToString(), t.CreatedAtUtc))
            .ToListAsync(ct));
        related.AddRange(await db.BreakCorrectionRequests
            .Where(b => b.RequestedByUserId == employeeId)
            .OrderByDescending(b => b.CreatedAtUtc).Take(20)
            .Select(b => new RelatedRecordDto(
                b.PublicId, "BreakCorrection", b.Status.ToString(), b.CreatedAtUtc))
            .ToListAsync(ct));
        related.AddRange(await db.TechnicalReports
            .Where(t => t.ReporterUserId == employeeId)
            .OrderByDescending(t => t.CreatedAtUtc).Take(20)
            .Select(t => new RelatedRecordDto(
                t.PublicId, "TechnicalReport", "Filed", t.CreatedAtUtc))
            .ToListAsync(ct));
        related.AddRange(await db.SupportTickets
            .Where(s => s.ReporterUserId == employeeId)
            .OrderByDescending(s => s.CreatedAtUtc).Take(20)
            .Select(s => new RelatedRecordDto(
                s.PublicId, "Support", s.Status.ToString(), s.CreatedAtUtc))
            .ToListAsync(ct));
        return related.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    private static async Task<IResult> AddNoteAsync(
        Guid id, CreateNoteRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        if (!Enum.TryParse<ManagementNotePriority>(
            request.Priority, ignoreCase: true, out var priority))
        {
            return Problems.Validation(http, "Priority must be Normal or High.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        var result = await service.AddNoteAsync(
            actorId, id, request.Category, priority, request.Body,
            request.RequireAcknowledgment, request.AckTargetUserIds, ct);
        return result.Note is not null
            ? Results.Created($"/api/v1/notes/{result.Note.Id}",
                new { result.Note.Id, result.Note.PublicId })
            : Problems.Validation(http, result.Error!);
    }

    private static async Task<IResult> AddFollowupAsync(
        Guid id, FollowupRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.AddFollowupAsync(id, actorId, request.Body, ct);
        return error is null ? Results.NoContent() : Problems.Validation(http, error);
    }

    private static async Task<IResult> ResolveAsync(
        Guid id, ResolveNoteRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.ResolveAsync(id, actorId, request.ResolutionNote, ct);
        return error is null ? Results.NoContent() : Problems.Validation(http, error);
    }

    private static async Task<IResult> ReopenAsync(
        Guid id, ReopenNoteRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.ReopenAsync(id, actorId, request.Reason, ct);
        return error is null ? Results.NoContent() : Problems.Validation(http, error);
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id, HttpContext http, ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.AcknowledgeAsync(id, actorId, ct);
        return error is null ? Results.NoContent() : Problems.Forbidden(http, error);
    }

    private static async Task<IResult> LinkAsync(
        Guid id, LinkRecordRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var (link, error) = await service.LinkAsync(id, actorId, request.TargetPublicId, ct);
        return link is not null
            ? Results.Created($"/api/v1/notes/{id}/links/{link.Id}", new { link.Id })
            : Problems.Validation(http, error!);
    }

    private static async Task<IResult> UnlinkAsync(
        Guid linkId, UnlinkRecordRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.UnlinkAsync(linkId, actorId, request.Reason, ct);
        return error is null ? Results.NoContent() : Problems.Validation(http, error);
    }

    private static async Task<IResult> ListTagsAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.ManagementTags
            .Where(t => t.Active)
            .OrderBy(t => t.Label)
            .Select(t => new ManagementTagDto(t.Id, t.Label, t.Active))
            .ToListAsync(ct));

    private static async Task<IResult> CreateTagAsync(
        CreateTagRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var (tag, error) = await service.CreateTagAsync(actorId, request.Label, ct);
        return tag is not null
            ? Results.Created($"/api/v1/management-tags/{tag.Id}",
                new ManagementTagDto(tag.Id, tag.Label, tag.Active))
            : Problems.Validation(http, error!);
    }

    private static async Task<IResult> ApplyTagAsync(
        Guid id, TagEntityRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var error = await service.TagAsync(id, actorId, request.EntityPublicId, ct);
        return error is null ? Results.NoContent() : Problems.Validation(http, error);
    }

    private static async Task<IResult> RemoveTagAsync(
        Guid id, TagEntityRequest request, HttpContext http,
        ManagementRecordService service, CancellationToken ct)
    {
        var error = await service.UntagAsync(id, request.EntityPublicId, ct);
        return error is null ? Results.NoContent() : Problems.Validation(http, error);
    }
}
