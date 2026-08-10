using SalesHub.Application.Abstractions;
using SalesHub.Application.Auth;
using SalesHub.Contracts.Events;
using SalesHub.Domain;

namespace SalesHub.Application.Users;

/// <summary>
/// Management user lifecycle (user-admin mockup, permission matrix rows
/// 32–36): update, deactivate/reactivate (+ scheduled reactivation),
/// management password reset, force logout. Every action authorizes against
/// the target's role — Owner accounts are untouchable by non-Owners, and
/// Owner role changes are refused entirely until the Wave 6 protected flow.
/// </summary>
public sealed class UserLifecycleService(
    IAppDb db,
    IIdentityService identity,
    AuthenticationService authentication,
    IAuditWriter audit,
    IOutboxWriter outbox,
    BusinessTime businessTime)
{
    public sealed record Actor(Guid UserId, string Role, Guid? SessionId);

    public enum Outcome { Done, NotFound, Forbidden, Invalid }

    public sealed record Result(Outcome Outcome, string? Error = null)
    {
        public static readonly Result Done = new(Outcome.Done);
        public static readonly Result NotFound = new(Outcome.NotFound);
        public static Result Forbid(string why) => new(Outcome.Forbidden, why);
        public static Result Bad(string why) => new(Outcome.Invalid, why);
    }

    public async Task<Result> UpdateUserAsync(
        Guid targetId, Actor actor, UserUpdate update, CancellationToken ct = default)
    {
        var target = await identity.GetUserDetailsAsync(targetId, ct);
        if (target is null)
        {
            return Result.NotFound;
        }

        if (GuardOwnerTarget(target.Role, actor) is { } refusal)
        {
            return refusal;
        }

        if (update.Role is { } role && (role == Roles.Owner || target.Role == Roles.Owner))
        {
            return Result.Forbid("Owner role changes require the protected Owner workflow.");
        }

        try
        {
            await db.ExecuteInTransactionAsync(async token =>
            {
                await identity.UpdateUserAsync(targetId, update, token);
                await audit.WriteAsync(new AuditEntry("users", "users.updated", AuditRetentionClass.AccountLifetime)
                {
                    ActorUserId = actor.UserId,
                    SessionId = actor.SessionId,
                    TargetType = "User",
                    TargetId = targetId.ToString(),
                    Before = new { target.DisplayName, target.Role, target.Email, target.BranchId, target.HireDate },
                    After = update,
                }, token);
                await db.SaveChangesAsync(token);
            }, ct);
        }
        catch (IdentityOperationException ex)
        {
            return Result.Bad(ex.Message);
        }

        return Result.Done;
    }

    public async Task<Result> DeactivateAsync(
        Guid targetId, Actor actor, string? reason, DateTimeOffset? scheduledReactivationAtUtc,
        CancellationToken ct = default)
    {
        var target = await identity.GetUserDetailsAsync(targetId, ct);
        if (target is null)
        {
            return Result.NotFound;
        }

        if (GuardOwnerTarget(target.Role, actor) is { } refusal)
        {
            return refusal;
        }

        if (targetId == actor.UserId)
        {
            return Result.Bad("You cannot deactivate your own account.");
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            await identity.DeactivateAsync(targetId, actor.UserId, reason, scheduledReactivationAtUtc, token);
            await audit.WriteAsync(new AuditEntry("users", "users.deactivated", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actor.UserId,
                SessionId = actor.SessionId,
                TargetType = "User",
                TargetId = targetId.ToString(),
                Reason = reason,
                After = new { scheduledReactivationAtUtc },
            }, token);
            await outbox.EnqueueAsync(EventTypes.UserDeactivated, new
            {
                userId = targetId,
                scheduledReactivationAtUtc,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);

        // Deactivation blocks sign-in AND ends existing sessions.
        _ = await authentication.RevokeAllSessionsAsync(
            targetId, actor.UserId, SessionRevocationReason.AccountDeactivated, ct);
        return Result.Done;
    }

    public async Task<Result> ReactivateAsync(Guid targetId, Actor actor, CancellationToken ct = default)
    {
        var target = await identity.GetUserDetailsAsync(targetId, ct);
        if (target is null)
        {
            return Result.NotFound;
        }

        if (GuardOwnerTarget(target.Role, actor) is { } refusal)
        {
            return refusal;
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            await identity.ReactivateAsync(targetId, token);
            await audit.WriteAsync(new AuditEntry("users", "users.reactivated", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actor.UserId,
                SessionId = actor.SessionId,
                TargetType = "User",
                TargetId = targetId.ToString(),
            }, token);
            await outbox.EnqueueAsync(EventTypes.UserReactivated, new { userId = targetId }, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return Result.Done;
    }

    public async Task<Result> ScheduleReactivationAsync(
        Guid targetId, Actor actor, DateTimeOffset? reactivateAtUtc, string? reason,
        CancellationToken ct = default)
    {
        var target = await identity.GetUserDetailsAsync(targetId, ct);
        if (target is null)
        {
            return Result.NotFound;
        }

        if (GuardOwnerTarget(target.Role, actor) is { } refusal)
        {
            return refusal;
        }

        if (target.IsActive)
        {
            return Result.Bad("Only a deactivated account can have a scheduled reactivation.");
        }

        if (reactivateAtUtc is { } at && at <= businessTime.UtcNow)
        {
            return Result.Bad("The reactivation time must be in the future.");
        }

        // Editing/canceling a schedule stays in the account timeline (mockup).
        await db.ExecuteInTransactionAsync(async token =>
        {
            await identity.ScheduleReactivationAsync(targetId, reactivateAtUtc, token);
            await audit.WriteAsync(new AuditEntry(
                "users",
                reactivateAtUtc is null ? "users.reactivationScheduleCanceled" : "users.reactivationScheduled",
                AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actor.UserId,
                SessionId = actor.SessionId,
                TargetType = "User",
                TargetId = targetId.ToString(),
                Reason = reason,
                Before = new { target.ScheduledReactivationAtUtc },
                After = new { reactivateAtUtc },
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return Result.Done;
    }

    public async Task<Result> ResetPasswordAsync(
        Guid targetId, Actor actor, string newPassword, CancellationToken ct = default)
    {
        var target = await identity.GetUserDetailsAsync(targetId, ct);
        if (target is null)
        {
            return Result.NotFound;
        }

        if (GuardOwnerTarget(target.Role, actor) is { } refusal)
        {
            return refusal;
        }

        try
        {
            await identity.SetPasswordAsync(targetId, newPassword, ct);
        }
        catch (IdentityOperationException ex)
        {
            return Result.Bad(ex.Message);
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            await audit.WriteAsync(new AuditEntry("users", "users.passwordReset", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actor.UserId,
                SessionId = actor.SessionId,
                TargetType = "User",
                TargetId = targetId.ToString(),
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);

        // A management-assigned password ends every existing session.
        _ = await authentication.RevokeAllSessionsAsync(
            targetId, actor.UserId, SessionRevocationReason.PasswordReset, ct);
        return Result.Done;
    }

    public async Task<Result> ForceLogoutAsync(Guid targetId, Actor actor, CancellationToken ct = default)
    {
        var target = await identity.GetUserDetailsAsync(targetId, ct);
        if (target is null)
        {
            return Result.NotFound;
        }

        if (GuardOwnerTarget(target.Role, actor) is { } refusal)
        {
            return refusal;
        }

        var revoked = await authentication.RevokeAllSessionsAsync(
            targetId, actor.UserId, SessionRevocationReason.AdministrativeLogout, ct);

        await db.ExecuteInTransactionAsync(async token =>
        {
            await audit.WriteAsync(new AuditEntry("users", "users.forceLogout", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actor.UserId,
                SessionId = actor.SessionId,
                TargetType = "User",
                TargetId = targetId.ToString(),
                After = new { revokedSessions = revoked },
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return Result.Done;
    }

    /// <summary>The Owner-protection rule shared by every lifecycle action:
    /// only Owners may act on Owner accounts (matrix rows 34–36).</summary>
    private static Result? GuardOwnerTarget(string targetRole, Actor actor) =>
        targetRole == Roles.Owner && actor.Role != Roles.Owner
            ? Result.Forbid("Only an Owner may manage an Owner account.")
            : null;
}
