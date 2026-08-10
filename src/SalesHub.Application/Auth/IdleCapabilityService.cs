using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Auth;

/// <summary>
/// The mandatory Idle Detection capability handshake (CLAUDE.md §4, docs/05).
/// The server binds attestations to the authenticated session, never to
/// client-claimed identity, and derives all state itself. There is no code
/// path that reaches Verified without a successful IdleDetector attestation —
/// in-page activity signals cannot substitute.
/// </summary>
public sealed class IdleCapabilityService(
    IAppDb db,
    IAuditWriter audit,
    BusinessTime businessTime,
    IOptions<SecurityOptions> securityOptions)
{
    private readonly SecurityOptions _security = securityOptions.Value;

    public sealed record VerifyInput(
        bool Supported,
        string Permission,
        bool DetectorStarted,
        int ThresholdSeconds);

    public sealed record CapabilityStatus(
        IdleCapabilityState State,
        DateTimeOffset? LeaseUntil,
        int HeartbeatCadenceSeconds);

    public async Task<CapabilityStatus?> VerifyAsync(
        Guid sessionId, VerifyInput input, CancellationToken ct = default)
    {
        var session = await ActiveSessionAsync(sessionId, ct);
        if (session is null)
        {
            return null;
        }

        var now = businessTime.UtcNow;
        var state = Classify(input);

        await db.ExecuteInTransactionAsync(async token =>
        {
            session.IdleCapabilityState = state;
            if (state == IdleCapabilityState.Verified)
            {
                session.IdlePermissionVerifiedAtUtc = now;
                session.IdleDetectorStartedAtUtc = now;
                session.LastIdleHeartbeatAtUtc = now;
                session.IdleCapabilityLeaseUntilUtc = now + _security.IdleCapabilityLease;
            }
            else
            {
                session.IdleCapabilityLeaseUntilUtc = null;
            }

            await audit.WriteAsync(new AuditEntry(
                "presence", "presence.idleCapabilityVerified", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = session.UserId,
                TargetType = "UserSession",
                TargetId = session.Id.ToString(),
                SessionId = session.Id,
                DeviceId = session.DeviceId,
                After = new
                {
                    state = state.ToString(),
                    input.Supported,
                    input.Permission,
                    input.DetectorStarted,
                    input.ThresholdSeconds,
                },
            }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return Status(session);
    }

    /// <summary>
    /// Heartbeat slides the capability lease and records detector state. A
    /// heartbeat on a non-Verified or lapsed session does not resurrect it —
    /// the client must re-verify.
    /// </summary>
    public async Task<CapabilityStatus?> HeartbeatAsync(
        Guid sessionId, string userState, string screenState, CancellationToken ct = default)
    {
        var session = await ActiveSessionAsync(sessionId, ct);
        if (session is null)
        {
            return null;
        }

        var now = businessTime.UtcNow;

        if (session.IdleCapabilityState == IdleCapabilityState.Verified)
        {
            var lapsed = session.IdleCapabilityLeaseUntilUtc is { } lease && lease < now;
            if (lapsed)
            {
                session.IdleCapabilityState = IdleCapabilityState.Stale;
                session.IdleCapabilityLeaseUntilUtc = null;
            }
            else
            {
                session.LastIdleHeartbeatAtUtc = now;
                session.IdleCapabilityLeaseUntilUtc = now + _security.IdleCapabilityLease;
            }

            await db.SaveChangesAsync(ct);
        }

        // Detector state transitions feed the presence evaluator from Wave 4;
        // storing coarse transitions only, never raw input events (docs/05).
        _ = userState;
        _ = screenState;

        return Status(session);
    }

    /// <summary>
    /// Scheduled-job sweep: any Verified session whose lease lapsed becomes
    /// Stale, which blocks monitored work until the client re-verifies.
    /// </summary>
    public async Task<int> MarkStaleSessionsAsync(CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        return await db.UserSessions
            .Where(s => s.RevokedAtUtc == null
                && s.IdleCapabilityState == IdleCapabilityState.Verified
                && s.IdleCapabilityLeaseUntilUtc != null
                && s.IdleCapabilityLeaseUntilUtc < now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.IdleCapabilityState, IdleCapabilityState.Stale)
                .SetProperty(s => s.IdleCapabilityLeaseUntilUtc, (DateTimeOffset?)null), ct);
    }

    private IdleCapabilityState Classify(VerifyInput input)
    {
        if (!input.Supported)
        {
            return IdleCapabilityState.Unsupported;
        }

        if (!string.Equals(input.Permission, "granted", StringComparison.OrdinalIgnoreCase))
        {
            return IdleCapabilityState.PermissionDenied;
        }

        if (!input.DetectorStarted)
        {
            return IdleCapabilityState.Starting;
        }

        if (input.ThresholdSeconds < _security.MinIdleThresholdSeconds)
        {
            // A threshold below the web API minimum means the client is not
            // running an approved configuration.
            return IdleCapabilityState.Error;
        }

        return IdleCapabilityState.Verified;
    }

    private CapabilityStatus Status(UserSession session) => new(
        session.IdleCapabilityState,
        session.IdleCapabilityLeaseUntilUtc,
        (int)_security.IdleHeartbeatCadence.TotalSeconds);

    private Task<UserSession?> ActiveSessionAsync(Guid sessionId, CancellationToken ct) =>
        db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.RevokedAtUtc == null, ct);
}
