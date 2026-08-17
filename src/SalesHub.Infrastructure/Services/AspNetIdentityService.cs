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

    public async Task<AppUserInfo?> FindByUsernameAsync(
        string username, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByNameAsync(username);
        return user is null ? null : ToInfo(user);
    }

    public async Task<UserDetails?> GetUserDetailsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : ToDetails(user);
    }

    public async Task<IReadOnlyList<UserDetails>> ListUsersAsync(
        UserQuery query, CancellationToken cancellationToken = default)
    {
        var users = userManager.Users.AsNoTracking();
        if (!query.IncludeInactive)
        {
            users = users.Where(u => u.IsActive);
        }

        if (!string.IsNullOrEmpty(query.Role))
        {
            users = users.Where(u => u.Role == query.Role);
        }

        if (query.Roles is { Count: > 0 })
        {
            users = users.Where(u => query.Roles.Contains(u.Role));
        }

        if (query.BranchId is { } branch)
        {
            users = users.Where(u => u.BranchId == branch);
        }

        var list = await users.OrderBy(u => u.DisplayName).ToListAsync(cancellationToken);
        return list.Select(ToDetails).ToList();
    }

    public async Task UpdateUserAsync(
        Guid userId, UserUpdate update, CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(userId);

        if (update.Role is { } newRole && newRole != user.Role)
        {
            if (!Roles.IsValid(newRole))
            {
                throw new IdentityOperationException($"Unknown role '{newRole}'.");
            }

            if (newRole == Roles.Owner || user.Role == Roles.Owner)
            {
                // CLAUDE.md §19: Owner promotion/demotion is a protected flow.
                throw new IdentityOperationException(
                    "Owner role changes require the protected Owner workflow.");
            }

            _ = await userManager.RemoveFromRoleAsync(user, user.Role);
            var added = await userManager.AddToRoleAsync(user, newRole);
            if (!added.Succeeded)
            {
                throw new IdentityOperationException(
                    string.Join(" ", added.Errors.Select(e => e.Description)));
            }

            user.Role = newRole;
            // Role changes must bite active sessions quickly.
            _ = await userManager.UpdateSecurityStampAsync(user);
        }

        if (update.DisplayName is { } displayName && !string.IsNullOrWhiteSpace(displayName))
        {
            user.DisplayName = displayName.Trim();
        }

        if (update.Email is not null)
        {
            user.Email = string.IsNullOrWhiteSpace(update.Email) ? null : update.Email.Trim();
        }

        if (update.BranchId is not null)
        {
            user.BranchId = update.BranchId;
        }

        if (update.HireDate is not null)
        {
            user.HireDate = update.HireDate;
        }

        await SaveAsync(user);
    }

    public async Task UpdateProfileAsync(
        Guid userId, ProfileUpdate update, CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(userId);
        if (update.Phone is not null)
        {
            user.PhoneNumber = string.IsNullOrWhiteSpace(update.Phone) ? null : update.Phone.Trim();
        }

        if (update.Birthday is not null)
        {
            user.Birthday = update.Birthday;
        }

        if (update.ProfilePhotoBlobId is not null)
        {
            user.ProfilePhotoBlobId = update.ProfilePhotoBlobId;
        }

        await SaveAsync(user);
    }

    public async Task SetPasswordAsync(
        Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        // Remove+add instead of the token-based reset: management assignment
        // is already authorized upstream and needs no reset token machinery.
        var user = await RequireAsync(userId);
        if (await userManager.HasPasswordAsync(user))
        {
            var removed = await userManager.RemovePasswordAsync(user);
            if (!removed.Succeeded)
            {
                throw new IdentityOperationException(
                    string.Join(" ", removed.Errors.Select(e => e.Description)));
            }
        }

        var added = await userManager.AddPasswordAsync(user, newPassword);
        if (!added.Succeeded)
        {
            throw new IdentityOperationException(
                string.Join(" ", added.Errors.Select(e => e.Description)));
        }

        _ = await userManager.UpdateSecurityStampAsync(user);
    }

    public async Task<bool> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(userId);
        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            return true;
        }

        if (result.Errors.Any(e => e.Code == "PasswordMismatch"))
        {
            return false;
        }

        throw new IdentityOperationException(
            string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    public async Task DeactivateAsync(
        Guid userId, Guid actorUserId, string? reason,
        DateTimeOffset? scheduledReactivationAtUtc,
        CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(userId);
        user.IsActive = false;
        user.DeactivatedAtUtc = businessTime.UtcNow;
        user.DeactivatedByUserId = actorUserId;
        user.DeactivationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        user.ScheduledReactivationAtUtc = scheduledReactivationAtUtc;
        await SaveAsync(user);
    }

    public async Task ReactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(userId);
        user.IsActive = true;
        user.DeactivatedAtUtc = null;
        user.DeactivatedByUserId = null;
        user.DeactivationReason = null;
        user.ScheduledReactivationAtUtc = null;
        user.ReactivationNoticeSentAtUtc = null;
        await SaveAsync(user);
    }

    public async Task ScheduleReactivationAsync(
        Guid userId, DateTimeOffset? reactivateAtUtc, CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(userId);
        user.ScheduledReactivationAtUtc = reactivateAtUtc;
        user.ReactivationNoticeSentAtUtc = null; // a new schedule notifies again
        await SaveAsync(user);
    }

    public async Task SetRoleProtectedAsync(
        Guid userId, string role, CancellationToken cancellationToken = default)
    {
        if (!Roles.IsValid(role))
        {
            throw new IdentityOperationException($"Unknown role '{role}'.");
        }

        var user = await RequireAsync(userId);
        if (user.Role == role)
        {
            return;
        }

        _ = await userManager.RemoveFromRoleAsync(user, user.Role);
        var added = await userManager.AddToRoleAsync(user, role);
        if (!added.Succeeded)
        {
            throw new IdentityOperationException(
                string.Join(" ", added.Errors.Select(e => e.Description)));
        }

        user.Role = role;
        await SaveAsync(user);
        _ = await userManager.UpdateSecurityStampAsync(user);
    }

    public async Task<UserPresenceInfo?> GetPresenceAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : ToPresence(user);
    }

    public async Task<IReadOnlyList<UserPresenceInfo>> ListPresenceAsync(
        CancellationToken cancellationToken = default)
    {
        var users = await userManager.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .ToListAsync(cancellationToken);
        return users.Select(ToPresence).ToList();
    }

    public async Task SetPresenceStatusAsync(
        Guid userId, Domain.Entities.PresenceStatus status, string? customMessage,
        CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(userId);
        user.PresenceStatus = status;
        user.CustomStatusMessage = customMessage;
        user.PresenceStatusChangedAtUtc = businessTime.UtcNow;
        await SaveAsync(user);
    }

    private async Task<ApplicationUser> RequireAsync(Guid userId) =>
        await userManager.FindByIdAsync(userId.ToString())
            ?? throw new IdentityOperationException("User not found.");

    private async Task SaveAsync(ApplicationUser user)
    {
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new IdentityOperationException(
                string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    private static AppUserInfo ToInfo(ApplicationUser user) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.DisplayName,
        user.Role,
        user.IsActive);

    private static UserPresenceInfo ToPresence(ApplicationUser user) => new(
        user.Id,
        user.DisplayName,
        user.Role,
        user.PresenceStatus,
        user.CustomStatusMessage,
        user.PresenceStatusChangedAtUtc);

    private static UserDetails ToDetails(ApplicationUser user) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.Email,
        user.PhoneNumber,
        user.BranchId,
        user.HireDate,
        user.Birthday,
        user.ProfilePhotoBlobId,
        user.CreatedAtUtc,
        user.DeactivatedAtUtc,
        user.DeactivationReason,
        user.ScheduledReactivationAtUtc);
}
