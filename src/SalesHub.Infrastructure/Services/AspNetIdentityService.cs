using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Users;
using SalesHub.Domain;
using SalesHub.Infrastructure.Identity;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// IIdentityService over ASP.NET Core Identity. Passwords are checked with
/// lockout counting; nothing here ever logs or returns password material.
/// </summary>
public sealed class AspNetIdentityService(
    UserManager<ApplicationUser> userManager,
    BusinessTime businessTime) : IIdentityService
{
    public async Task<CredentialCheckResult> CheckCredentialsAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByNameAsync(username);
        if (user is null)
        {
            return CredentialCheckResult.Fail(CredentialCheckOutcome.InvalidCredentials);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return CredentialCheckResult.Fail(CredentialCheckOutcome.LockedOut);
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            return CredentialCheckResult.Fail(
                await userManager.IsLockedOutAsync(user)
                    ? CredentialCheckOutcome.LockedOut
                    : CredentialCheckOutcome.InvalidCredentials);
        }

        await userManager.ResetAccessFailedCountAsync(user);

        if (!user.IsActive)
        {
            // Deactivation blocks sign-in but is only revealed after the
            // password proves out, so it is not an enumeration oracle.
            return CredentialCheckResult.Fail(CredentialCheckOutcome.Deactivated);
        }

        return new CredentialCheckResult(CredentialCheckOutcome.Success, ToInfo(user));
    }

    public async Task<AppUserInfo?> FindByIdAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : ToInfo(user);
    }

    public async Task<AppUserInfo> CreateUserAsync(
        NewUser newUser, CancellationToken cancellationToken = default)
    {
        if (!Roles.IsValid(newUser.Role))
        {
            throw new IdentityOperationException($"Unknown role '{newUser.Role}'.");
        }

        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = newUser.Username,
            Email = newUser.Email,
            DisplayName = newUser.DisplayName,
            Role = newUser.Role,
            IsActive = true,
            CreatedAtUtc = businessTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user, newUser.Password);
        if (!created.Succeeded)
        {
            throw new IdentityOperationException(
                string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        var roleAssigned = await userManager.AddToRoleAsync(user, newUser.Role);
        if (!roleAssigned.Succeeded)
        {
            await userManager.DeleteAsync(user); // do not leave a role-less account behind
            throw new IdentityOperationException(
                string.Join(" ", roleAssigned.Errors.Select(e => e.Description)));
        }

        return ToInfo(user);
    }

    private static AppUserInfo ToInfo(ApplicationUser user) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.DisplayName,
        user.Role,
        user.IsActive);
}
