namespace SalesHub.Domain;

/// <summary>
/// Server-held Idle Detection capability state per user session (docs/05).
/// Only <see cref="Verified"/> permits entering active monitored work state.
/// </summary>
public enum IdleCapabilityState
{
    Unknown = 0,
    Unsupported = 1,
    PermissionDenied = 2,
    Starting = 3,
    Verified = 4,
    Stale = 5,
    Revoked = 6,
    Error = 7,
}

public static class IdleCapabilityStateExtensions
{
    /// <summary>
    /// The one and only gate: no state other than Verified may enter monitored
    /// work. In-page activity signals can never substitute (CLAUDE.md §4).
    /// </summary>
    public static bool PermitsMonitoredWork(this IdleCapabilityState state) =>
        state == IdleCapabilityState.Verified;
}
