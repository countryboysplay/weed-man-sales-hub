using SalesHub.Domain;

namespace SalesHub.Application;

/// <summary>
/// Security knobs with production-safe defaults. The fresh-auth window is an
/// Owner-configurable setting in the approved GUI; it becomes a settings row
/// in the settings wave — until then it is deployment configuration.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Fresh authentication window (CLAUDE.md §3). Default 15 minutes.</summary>
    public TimeSpan FreshAuthWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long an idle-capability verification stays valid without a heartbeat.</summary>
    public TimeSpan IdleCapabilityLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Heartbeat cadence advertised to the client (docs/03: 30–60s).</summary>
    public TimeSpan IdleHeartbeatCadence { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum IdleDetector threshold; the web API minimum is 60 seconds.</summary>
    public int MinIdleThresholdSeconds { get; set; } = 60;

    /// <summary>
    /// Roles whose work sessions are presence-monitored and therefore subject
    /// to the mandatory Idle Detection gate (CLAUDE.md §4).
    /// </summary>
    public string[] MonitoredRoles { get; set; } = [Roles.SalesAgent];

    /// <summary>Idempotency key retention.</summary>
    public TimeSpan IdempotencyKeyLifetime { get; set; } = TimeSpan.FromHours(48);
}
