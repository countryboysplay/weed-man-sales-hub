using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Auth;

/// <summary>
/// Login, logout, revocation, and fresh authentication (CLAUDE.md §3).
/// Every path writes audit in the same transaction as the state change, and
/// revocations enqueue an outbox event so the affected browser renders the
/// "Session ended" screen in realtime.
/// </summary>
public sealed class AuthenticationService(
    IAppDb db,
    IIdentityService identity,
    IAuditWriter audit,
    IOutboxWriter outbox,
    BusinessTime businessTime,
    IOptions<SecurityOptions> securityOptions,
    ILogger<AuthenticationService> logger)
{
    private readonly SecurityOptions _security = securityOptions.Value;

    public sealed record LoginInput(
        string Username,
        string Password,
        string DeviceId,
        string BrowserFamily,
        string OsFamily,
        bool PwaInstalled,
        string AppVersion,
        string IpHash);

    public sealed record LoginOutcome(
        CredentialCheckOutcome Result,
        AppUserInfo? User,
        Guid SessionId,
        string Verifier,
        bool IdleCapabilityRequired);

    public async Task<LoginOutcome> LoginAsync(LoginInput input, CancellationToken ct = default)
    {
        var check = await identity.CheckCredentialsAsync(input.Username, input.Password, ct);
        if (check.Outcome != CredentialCheckOutcome.Success || check.User is null)
        {
            // Failed attempts are logged (safe fields only), not audited per
            // user, to avoid an enumeration oracle in the audit stream.
            logger.LogInformation(
                "Login refused: {Outcome} for username hash {UsernameHash}",
                check.Outcome, SessionTokens.Hash(input.Username.ToUpperInvariant())[..12]);
            return new LoginOutcome(check.Outcome, null, Guid.Empty, string.Empty, false);
        }

        var user = check.User;
        var verifier = SessionTokens.NewVerifier();
        var now = businessTime.UtcNow;
        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = SessionTokens.Hash(verifier),
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            DeviceId = input.DeviceId,
            BrowserFamily = input.BrowserFamily,
            OsFamily = input.OsFamily,
            PwaInstalled = input.PwaInstalled,
            AppVersion = input.AppVersion,
            IpHash = input.IpHash,
            IdleCapabilityState = IdleCapabilityState.Unknown,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.UserSessions.Add(session);
            await audit.WriteAsync(new AuditEntry("auth", "auth.login", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = user.Id,
                TargetType = "UserSession",
                TargetId = session.Id.ToString(),
                SessionId = session.Id,
                DeviceId = input.DeviceId,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new LoginOutcome(
            CredentialCheckOutcome.Success,
            user,
            session.Id,
            verifier,
            IsMonitoredRole(user.Role));
    }

    /// <summary>Logout of the calling session.</summary>
    public Task LogoutAsync(Guid sessionId, Guid actorUserId, CancellationToken ct = default) =>
        RevokeAsync(sessionId, actorUserId, SessionRevocationReason.UserLogout, ct);

    /// <summary>
    /// Revoke one session. Authorization: the session owner may revoke their
    /// own sessions; management may revoke employee sessions; only an Owner
    /// may revoke an Owner's session (permission matrix rows 34–35).
    /// </summary>
    public async Task<RevocationResult> RevokeSessionAsync(
        Guid targetSessionId,
        Guid actorUserId,
        string actorRole,
        SessionRevocationReason reason,
        CancellationToken ct = default)
    {
        var target = await db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == targetSessionId && s.RevokedAtUtc == null, ct);
        if (target is null)
        {
            return RevocationResult.NotFound;
        }

        if (target.UserId != actorUserId)
        {
            if (!Roles.IsManagement(actorRole))
            {
                return RevocationResult.Forbidden;
            }

            var targetUser = await identity.FindByIdAsync(target.UserId, ct);
            if (targetUser is null)
            {
                return RevocationResult.NotFound;
            }

            if (targetUser.Role == Roles.Owner && actorRole != Roles.Owner)
            {
                return RevocationResult.Forbidden;
            }
        }

        await RevokeAsync(target.Id, actorUserId, reason, ct);
        return RevocationResult.Revoked;
    }

    public enum RevocationResult { Revoked, NotFound, Forbidden }

    private async Task RevokeAsync(
        Guid sessionId, Guid actorUserId, SessionRevocationReason reason, CancellationToken ct)
    {
        var session = await db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.RevokedAtUtc == null, ct);
        if (session is null)
        {
            return; // already revoked/absent — revocation is idempotent
        }

        var now = businessTime.UtcNow;
        await db.ExecuteInTransactionAsync(async token =>
        {
            session.RevokedAtUtc = now;
            session.RevokedByUserId = actorUserId;
            session.RevokeReason = reason;
            session.FreshAuthUntilUtc = null;

            await audit.WriteAsync(new AuditEntry("auth", "auth.sessionRevoked", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actorUserId,
                TargetType = "UserSession",
                TargetId = session.Id.ToString(),
                Reason = reason.ToString(),
                SessionId = session.Id,
                DeviceId = session.DeviceId,
            }, token);

            // Realtime: the affected browser must learn it was signed out and
            // show the "Session ended" screen with this reason.
            await outbox.EnqueueAsync(EventTypes.SessionRevoked, new
            {
                userId = session.UserId,
                sessionId = session.Id,
                reason = reason.ToString(),
            }, token);

            await db.SaveChangesAsync(token);
        }, ct);
    }

    /// <summary>Revoke every active session of a user (force logout all devices).</summary>
    public async Task<int> RevokeAllSessionsAsync(
        Guid targetUserId,
        Guid actorUserId,
        SessionRevocationReason reason,
        CancellationToken ct = default)
    {
        var sessions = await db.UserSessions
            .Where(s => s.UserId == targetUserId && s.RevokedAtUtc == null)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var id in sessions)
        {
            await RevokeAsync(id, actorUserId, reason, ct);
        }

        return sessions.Count;
    }

    /// <summary>
    /// Re-verify the password and stamp a fresh-auth assertion on the calling
    /// session (server-held; default window 15 minutes). The password itself
    /// is verified and immediately discarded.
    /// </summary>
    public async Task<DateTimeOffset?> FreshAuthAsync(
        Guid sessionId, string username, string password, CancellationToken ct = default)
    {
        var check = await identity.CheckCredentialsAsync(username, password, ct);
        if (check.Outcome != CredentialCheckOutcome.Success || check.User is null)
        {
            return null;
        }

        var session = await db.UserSessions
            .FirstOrDefaultAsync(
                s => s.Id == sessionId && s.UserId == check.User.Id && s.RevokedAtUtc == null, ct);
        if (session is null)
        {
            return null;
        }

        var until = businessTime.UtcNow + _security.FreshAuthWindow;
        await db.ExecuteInTransactionAsync(async token =>
        {
            session.FreshAuthUntilUtc = until;
            await audit.WriteAsync(new AuditEntry("auth", "auth.freshAuth", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = check.User.Id,
                TargetType = "UserSession",
                TargetId = session.Id.ToString(),
                SessionId = session.Id,
                DeviceId = session.DeviceId,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);

        return until;
    }

    public bool IsMonitoredRole(string role) =>
        _security.MonitoredRoles.Contains(role, StringComparer.Ordinal);
}
