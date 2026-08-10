namespace SalesHub.Contracts.Auth;

public sealed record LoginRequest(
    string Username,
    string Password,
    bool KeepSignedIn = false,
    DeviceInfo? Device = null);

public sealed record DeviceInfo(
    string? DeviceId,
    string? BrowserFamily,
    string? OsFamily,
    bool PwaInstalled = false,
    string? AppVersion = null);

public sealed record LoginResponse(
    Guid UserId,
    string Username,
    string DisplayName,
    string Role,
    Guid SessionId,
    bool IdleCapabilityRequired);

public sealed record MeResponse(
    Guid UserId,
    string Username,
    string DisplayName,
    string Role,
    Guid SessionId,
    string IdleCapabilityState,
    bool IdleCapabilityRequired,
    DateTimeOffset? FreshAuthUntil);

public sealed record SessionDto(
    Guid SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string BrowserFamily,
    string OsFamily,
    bool PwaInstalled,
    string AppVersion,
    bool Current);

public sealed record FreshAuthRequest(string Password);

public sealed record FreshAuthResponse(DateTimeOffset FreshAuthUntil);

public sealed record ForgotPasswordRequest(string Username);

/// <summary>Capability attestation from the browser (docs/05). Server trusts
/// only the authenticated session, never client-supplied identity.</summary>
public sealed record IdleCapabilityVerifyRequest(
    bool Supported,
    string Permission,
    bool DetectorStarted,
    int ThresholdSeconds,
    DateTimeOffset? ClientObservedAt);

public sealed record IdleCapabilityVerifyResponse(
    string State,
    DateTimeOffset? LeaseUntil,
    int HeartbeatCadenceSeconds);

public sealed record IdleHeartbeatRequest(
    string UserState,      // "active" | "idle"
    string ScreenState,    // "unlocked" | "locked"
    string? Visibility,
    DateTimeOffset? LastClientTransitionAt,
    string? AppVersion);

public sealed record IdleHeartbeatResponse(
    string State,
    DateTimeOffset? LeaseUntil,
    int HeartbeatCadenceSeconds);
