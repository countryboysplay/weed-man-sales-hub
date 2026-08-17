namespace SalesHub.Application.Abstractions;

/// <summary>
/// Port over ASP.NET Core Identity so the application layer never touches
/// UserManager directly. Password hashing, lockout, and security stamps all
/// stay inside Identity (docs/04: no custom password cryptography).
/// </summary>
public interface IIdentityService
{
    Task<CredentialCheckResult> CheckCredentialsAsync(
        string username, string password, CancellationToken cancellationToken = default);

    Task<AppUserInfo?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<AppUserInfo?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<AppUserInfo> CreateUserAsync(
        NewUser user, CancellationToken cancellationToken = default);

    // ── Wave 1 lifecycle (docs/01 users; user-admin mockup) ──────────────────

    Task<UserDetails?> GetUserDetailsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserDetails>> ListUsersAsync(
        UserQuery query, CancellationToken cancellationToken = default);

    /// <summary>Manager-maintained fields. Role changes involving Owner are refused
    /// upstream; this method also refuses them as defense in depth.</summary>
    Task UpdateUserAsync(Guid userId, UserUpdate update, CancellationToken cancellationToken = default);

    /// <summary>User-editable profile fields (phone, birthday, photo).</summary>
    Task UpdateProfileAsync(Guid userId, ProfileUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Management-assigned replacement password. Bumps the security stamp.</summary>
    Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>Self-service change; verifies the current password first.</summary>
    Task<bool> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword,
        CancellationToken cancellationToken = default);

    Task DeactivateAsync(
        Guid userId, Guid actorUserId, string? reason, DateTimeOffset? scheduledReactivationAtUtc,
        CancellationToken cancellationToken = default);

    Task ReactivateAsync(Guid userId, CancellationToken cancellationToken = default);

    Task ScheduleReactivationAsync(
        Guid userId, DateTimeOffset? reactivateAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Role change that MAY involve Owner. Reserved for the Wave 6
    /// protected Owner flow — every other caller uses UpdateUserAsync, which
    /// refuses Owner transitions. Bumps the security stamp.</summary>
    Task SetRoleProtectedAsync(Guid userId, string role, CancellationToken cancellationToken = default);

    // ── Wave 4 presence (docs/01 users; presence mockups) ────────────────────

    Task<UserPresenceInfo?> GetPresenceAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Manual presence for every active user (the directory's base data).</summary>
    Task<IReadOnlyList<UserPresenceInfo>> ListPresenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the user's manual status. Length validation happens
    /// upstream in PresenceService; this is storage only.</summary>
    Task SetPresenceStatusAsync(
        Guid userId, Domain.Entities.PresenceStatus status, string? customMessage,
        CancellationToken cancellationToken = default);
}

public sealed record UserPresenceInfo(
    Guid UserId,
    string DisplayName,
    string Role,
    Domain.Entities.PresenceStatus Status,
    string? CustomStatusMessage,
    DateTimeOffset? ChangedAtUtc);

public sealed record UserQuery(
    string? Role = null,
    Guid? BranchId = null,
    bool IncludeInactive = false,
    IReadOnlyList<string>? Roles = null);

public sealed record UserUpdate(
    string? DisplayName,
    string? Role,
    string? Email,
    Guid? BranchId,
    DateOnly? HireDate);

public sealed record ProfileUpdate(
    string? Phone,
    DateOnly? Birthday,
    Guid? ProfilePhotoBlobId);

public sealed record UserDetails(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsActive,
    string? Email,
    string? Phone,
    Guid? BranchId,
    DateOnly? HireDate,
    DateOnly? Birthday,
    Guid? ProfilePhotoBlobId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc,
    string? DeactivationReason,
    DateTimeOffset? ScheduledReactivationAtUtc);

public sealed record NewUser(
    string Username,
    string Password,
    string DisplayName,
    string Role,
    string? Email);

public sealed record AppUserInfo(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsActive);

public sealed record CredentialCheckResult(
    CredentialCheckOutcome Outcome,
    AppUserInfo? User)
{
    public static CredentialCheckResult Fail(CredentialCheckOutcome outcome) => new(outcome, null);
}

public enum CredentialCheckOutcome
{
    Success = 0,
    InvalidCredentials = 1,
    LockedOut = 2,
    Deactivated = 3,
}
