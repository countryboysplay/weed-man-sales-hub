using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Users;
using SalesHub.Contracts.Users;

namespace SalesHub.Api.Endpoints;

public static class UserLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapUserLifecycleEndpoints(this IEndpointRouteBuilder api)
    {
        var users = api.MapGroup("/users").RequireAuthorization(Policies.Management);

        users.MapGet("/", ListUsersAsync);
        users.MapGet("/{id:guid}", GetUserAsync);
        users.MapPatch("/{id:guid}", UpdateUserAsync);
        users.MapPost("/{id:guid}/deactivate", DeactivateAsync);
        users.MapPost("/{id:guid}/reactivate", ReactivateAsync);
        users.MapPost("/{id:guid}/schedule-reactivation", ScheduleReactivationAsync);
        users.MapDelete("/{id:guid}/schedule-reactivation", CancelScheduledReactivationAsync);
        users.MapPost("/{id:guid}/reset-password", ResetPasswordAsync);
        users.MapPost("/{id:guid}/force-logout", ForceLogoutAsync);

        // Directory: every active user can see people (permission matrix).
        api.MapGet("/directory", DirectoryAsync).RequireAuthorization(Policies.Employee);

        // Profile photos are served to any authenticated user (avatars).
        api.MapGet("/users/{id:guid}/photo", PhotoAsync).RequireAuthorization(Policies.Employee);

        var branches = api.MapGroup("/branches");
        branches.MapGet("/", ListBranchesAsync).RequireAuthorization(Policies.Employee);
        branches.MapPost("/", CreateBranchAsync).RequireAuthorization(Policies.Management);

        return api;
    }

    internal static UserLifecycleService.Actor ActorOf(HttpContext http)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        return new UserLifecycleService.Actor(
            userId,
            http.User.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            session.Id);
    }

    private static IResult From(UserLifecycleService.Result result, HttpContext http, IResult? done = null) =>
        result.Outcome switch
        {
            UserLifecycleService.Outcome.Done => done ?? Results.NoContent(),
            UserLifecycleService.Outcome.NotFound => Problems.NotFound(http, "User not found."),
            UserLifecycleService.Outcome.Forbidden => Problems.Forbidden(http, result.Error!),
            _ => Problems.Validation(http, result.Error ?? "Invalid request."),
        };

    private static async Task<IResult> ListUsersAsync(
        HttpContext http, IIdentityService identity, IAppDb db,
        string? role, Guid? branchId, bool includeInactive, CancellationToken ct)
    {
        var users = await identity.ListUsersAsync(
            new UserQuery(role, branchId, includeInactive), ct);
        var branches = await db.Branches.ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var sessions = await db.UserSessions
            .Where(s => s.RevokedAtUtc == null)
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.UserId, g => g.Count, ct);

        return Results.Ok(users.Select(u => ToResponse(u, branches, sessions)).ToList());
    }

    private static async Task<IResult> GetUserAsync(
        Guid id, HttpContext http, IIdentityService identity, IAppDb db, CancellationToken ct)
    {
        var user = await identity.GetUserDetailsAsync(id, ct);
        if (user is null)
        {
            return Problems.NotFound(http, "User not found.");
        }

        var branches = await db.Branches.ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var activeSessions = await db.UserSessions
            .CountAsync(s => s.UserId == id && s.RevokedAtUtc == null, ct);
        return Results.Ok(ToResponse(user, branches,
            new Dictionary<Guid, int> { [id] = activeSessions }));
    }

    private static async Task<IResult> UpdateUserAsync(
        Guid id, UpdateUserRequest request, HttpContext http,
        UserLifecycleService lifecycle, CancellationToken ct)
    {
        var result = await lifecycle.UpdateUserAsync(id, ActorOf(http), new UserUpdate(
            request.DisplayName, request.Role, request.Email, request.BranchId, request.HireDate), ct);
        return From(result, http);
    }

    private static async Task<IResult> DeactivateAsync(
        Guid id, DeactivateUserRequest request, HttpContext http,
        UserLifecycleService lifecycle, CancellationToken ct)
    {
        var result = await lifecycle.DeactivateAsync(
            id, ActorOf(http), request.Reason, request.ScheduledReactivationAt, ct);
        return From(result, http);
    }

    private static async Task<IResult> ReactivateAsync(
        Guid id, HttpContext http, UserLifecycleService lifecycle, CancellationToken ct) =>
        From(await lifecycle.ReactivateAsync(id, ActorOf(http)), http);

    private static async Task<IResult> ScheduleReactivationAsync(
        Guid id, ScheduleReactivationRequest request, HttpContext http,
        UserLifecycleService lifecycle, CancellationToken ct) =>
        From(await lifecycle.ScheduleReactivationAsync(
            id, ActorOf(http), request.ReactivateAt, reason: null, ct), http);

    private static async Task<IResult> CancelScheduledReactivationAsync(
        Guid id, HttpContext http, UserLifecycleService lifecycle,
        string? reason, CancellationToken ct) =>
        From(await lifecycle.ScheduleReactivationAsync(id, ActorOf(http), null, reason, ct), http);

    private static async Task<IResult> ResetPasswordAsync(
        Guid id, ResetPasswordRequest request, HttpContext http,
        UserLifecycleService lifecycle, CancellationToken ct) =>
        From(await lifecycle.ResetPasswordAsync(id, ActorOf(http), request.NewPassword, ct), http);

    private static async Task<IResult> ForceLogoutAsync(
        Guid id, HttpContext http, UserLifecycleService lifecycle, CancellationToken ct) =>
        From(await lifecycle.ForceLogoutAsync(id, ActorOf(http)), http);

    private static async Task<IResult> DirectoryAsync(
        IIdentityService identity, IAppDb db, CancellationToken ct)
    {
        var users = await identity.ListUsersAsync(new UserQuery(), ct);
        var branches = await db.Branches.ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        // Deliberately no sales figures here (directory mockup:
        // "Sales metrics — Not shown here").
        return Results.Ok(users.Select(u => new DirectoryEntry(
            u.Id, u.DisplayName, u.Role,
            u.BranchId is { } b && branches.TryGetValue(b, out var name) ? name : null,
            u.Email, u.Phone, u.ProfilePhotoBlobId is not null)).ToList());
    }

    private static async Task<IResult> PhotoAsync(
        Guid id, HttpContext http, IIdentityService identity, IFileBlobStore blobs,
        IAppDb db, CancellationToken ct)
    {
        var user = await identity.GetUserDetailsAsync(id, ct);
        if (user?.ProfilePhotoBlobId is not { } blobId)
        {
            return Problems.NotFound(http, "No profile photo.");
        }

        var blob = await db.FileBlobs.FirstOrDefaultAsync(b => b.Id == blobId, ct);
        if (blob is null)
        {
            return Problems.NotFound(http, "No profile photo.");
        }

        var stream = await blobs.OpenReadAsync(blobId, ct);
        return Results.Stream(stream, blob.ContentType);
    }

    private static async Task<IResult> ListBranchesAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.Branches
            .OrderBy(b => b.Name)
            .Select(b => new BranchDto(b.Id, b.Name, b.Active))
            .ToListAsync(ct));

    private static async Task<IResult> CreateBranchAsync(
        CreateBranchRequest request, HttpContext http, IAppDb db,
        IAuditWriter audit, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (name.Length is 0 or > 128)
        {
            return Problems.Validation(http, "A branch needs a name up to 128 characters.");
        }

        if (await db.Branches.AnyAsync(b => b.Name == name, ct))
        {
            return Problems.Conflict(http, $"Branch '{name}' already exists.");
        }

        var branch = new Domain.Entities.Branch { Id = Guid.CreateVersion7(), Name = name };
        var (userId, session) = AuthEndpoints.Current(http);
        await db.ExecuteInTransactionAsync(async token =>
        {
            db.Branches.Add(branch);
            await audit.WriteAsync(new AuditEntry(
                "users", "branches.created", Domain.AuditRetentionClass.Operational365Days)
            {
                ActorUserId = userId,
                SessionId = session.Id,
                TargetType = "Branch",
                TargetId = branch.Id.ToString(),
                After = new { name },
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return Results.Created($"/api/v1/branches/{branch.Id}",
            new BranchDto(branch.Id, branch.Name, branch.Active));
    }

    private static UserDetailsResponse ToResponse(
        UserDetails u,
        IReadOnlyDictionary<Guid, string> branches,
        IReadOnlyDictionary<Guid, int> sessions) => new(
        u.Id, u.Username, u.DisplayName, u.Role, u.IsActive, u.Email, u.Phone,
        u.BranchId,
        u.BranchId is { } b && branches.TryGetValue(b, out var name) ? name : null,
        u.HireDate, u.Birthday, u.ProfilePhotoBlobId is not null,
        u.CreatedAtUtc, u.DeactivatedAtUtc, u.DeactivationReason,
        u.ScheduledReactivationAtUtc,
        sessions.TryGetValue(u.Id, out var count) ? count : 0);
}
