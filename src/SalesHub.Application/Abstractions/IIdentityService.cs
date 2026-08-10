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

    Task<AppUserInfo> CreateUserAsync(
        NewUser user, CancellationToken cancellationToken = default);
}

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
