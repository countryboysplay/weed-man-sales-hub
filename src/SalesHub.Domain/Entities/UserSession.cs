namespace SalesHub.Domain.Entities;

/// <summary>
/// One signed-in browser/device (CLAUDE.md §3). Every authenticated request
/// must map to an active row here; revocation is immediate regardless of
/// cookie lifetime. The cookie carries only the session id and a random
/// verifier — this row stores the verifier's SHA-256, never the verifier.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the 256-bit random session verifier. Never the plaintext.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public SessionRevocationReason? RevokeReason { get; set; }

    // Device/browser metadata for the profile "Devices & Sessions" surface.
    public string DeviceId { get; set; } = string.Empty;
    public string BrowserFamily { get; set; } = string.Empty;
    public string OsFamily { get; set; } = string.Empty;
    public bool PwaInstalled { get; set; }
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Security-safe IP metadata (hash), per policy. Never the raw address.</summary>
    public string IpHash { get; set; } = string.Empty;

    // Fresh authentication: server-held assertion, default window 15 minutes.
    public DateTimeOffset? FreshAuthUntilUtc { get; set; }

    // Mandatory Idle Detection capability (CLAUDE.md §4, docs/05).
    public IdleCapabilityState IdleCapabilityState { get; set; } = IdleCapabilityState.Unknown;
    public DateTimeOffset? IdlePermissionVerifiedAtUtc { get; set; }
    public DateTimeOffset? IdleDetectorStartedAtUtc { get; set; }
    public DateTimeOffset? LastIdleHeartbeatAtUtc { get; set; }
    public DateTimeOffset? IdleCapabilityLeaseUntilUtc { get; set; }

    public bool IsActive => RevokedAtUtc is null;
}
