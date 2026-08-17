using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Presence;
using SalesHub.Contracts.Presence;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/presence").RequireAuthorization(Policies.Employee);
        group.MapPost("/status", SetStatusAsync);
        group.MapGet("/me", MeAsync);
        group.MapGet("/directory", DirectoryAsync);

        // Alerts are management-only; the shape depends on rank (permission
        // matrix: supervisors get the serious summary, managers/owners the
        // full detail).
        group.MapGet("/alerts", AlertsAsync).RequireAuthorization(Policies.Management);
        group.MapPatch("/flags/{id:guid}", UpdateFlagAsync)
            .RequireAuthorization(Policies.ManagerOrOwner);

        return api;
    }

    private static async Task<IResult> SetStatusAsync(
        SetPresenceStatusRequest request, HttpContext http,
        PresenceService presence, CancellationToken ct)
    {
        if (!Enum.TryParse<PresenceStatus>(request.Status, ignoreCase: true, out var status))
        {
            return Problems.Validation(http, "Status must be Available, Busy, or Dnd.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var (ok, error) = await presence.SetManualStatusAsync(
            userId, status, request.CustomMessage, ct);
        return ok ? Results.NoContent() : Problems.Validation(http, error!);
    }

    private static async Task<IResult> MeAsync(
        HttpContext http, PresenceService presence, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var day = await presence.MyDayAsync(userId, ct);
        if (day is null)
        {
            return Problems.NotFound(http, "User not found.");
        }

        return Results.Ok(new MyPresenceDto(
            day.Manual.Status.ToString(),
            day.Manual.CustomStatusMessage,
            day.Manual.ChangedAtUtc,
            day.DerivedState.ToString(),
            day.Segments
                .Select(s => new PresenceSegmentDto(s.State.ToString(), s.StartAtUtc, s.EndAtUtc))
                .ToList(),
            day.Flags
                .Select(f => new PresenceFlagDto(
                    f.Id, f.PublicId, f.UserId, day.Manual.DisplayName, f.Category,
                    f.Severity.ToString(), f.Status.ToString(), f.BusinessDate,
                    f.StartAtUtc, f.EndAtUtc, f.LinkedPublicIds))
                .ToList()));
    }

    private static async Task<IResult> DirectoryAsync(
        PresenceService presence, CancellationToken ct)
    {
        var entries = await presence.DirectoryAsync(ct);
        return Results.Ok(entries
            .Select(e => new PresenceDirectoryEntryDto(
                e.UserId, e.DisplayName, e.Role, e.State, e.CustomMessage, e.StatusChangedAtUtc))
            .ToList());
    }

    private static async Task<IResult> AlertsAsync(
        HttpContext http, IAppDb db, IIdentityService identity,
        BusinessTime businessTime, DateOnly? date, CancellationToken ct)
    {
        var businessDate = date ?? businessTime.Today;
        var role = http.Items[AuthConstants.UserRoleItemKey] as string ?? string.Empty;

        if (role == Roles.SalesSupervisor)
        {
            // Summary only: counts, no per-agent drill-down at this rank.
            var flags = await db.PresenceFlags
                .Where(f => f.BusinessDate == businessDate)
                .Select(f => new { f.Severity, f.Status })
                .ToListAsync(ct);
            return Results.Ok(new PresenceAlertSummaryDto(
                businessDate,
                flags.Count(f => f.Status == PresenceFlagStatus.Open
                    && f.Severity == PresenceFlagSeverity.Serious),
                flags.Count(f => f.Status == PresenceFlagStatus.Open
                    && f.Severity == PresenceFlagSeverity.Warning),
                flags.Count(f => f.Status == PresenceFlagStatus.Open
                    && f.Severity == PresenceFlagSeverity.Logged),
                flags.Count(f => f.Status == PresenceFlagStatus.Resolved)));
        }

        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var detail = await db.PresenceFlags
            .Where(f => f.BusinessDate == businessDate)
            .OrderByDescending(f => f.Severity).ThenBy(f => f.StartAtUtc)
            .ToListAsync(ct);
        return Results.Ok(detail
            .Select(f => new PresenceFlagDto(
                f.Id, f.PublicId, f.UserId,
                names.GetValueOrDefault(f.UserId, "Unknown"),
                f.Category, f.Severity.ToString(), f.Status.ToString(),
                f.BusinessDate, f.StartAtUtc, f.EndAtUtc, f.LinkedPublicIds))
            .ToList());
    }

    private static async Task<IResult> UpdateFlagAsync(
        Guid id, ResolvePresenceFlagRequest request, HttpContext http,
        IAppDb db, IAuditWriter audit, CancellationToken ct)
    {
        var status = request.Action?.ToLowerInvariant() switch
        {
            "resolve" => PresenceFlagStatus.Resolved,
            "suppress" => PresenceFlagStatus.Suppressed,
            _ => (PresenceFlagStatus?)null,
        };
        if (status is null)
        {
            return Problems.Validation(http, "Action must be 'resolve' or 'suppress'.");
        }

        var flag = await db.PresenceFlags.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (flag is null)
        {
            return Problems.NotFound(http, "Presence flag not found.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        var before = flag.Status;
        flag.Status = status.Value;
        await audit.WriteAsync(new AuditEntry(
            "presence", "presence.flagReviewed", AuditRetentionClass.Operational365Days)
        {
            ActorUserId = actorId,
            TargetType = "PresenceFlag",
            TargetId = flag.Id.ToString(),
            PublicRecordId = flag.PublicId,
            Before = new { status = before.ToString() },
            After = new { status = flag.Status.ToString() },
        }, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
