using Microsoft.AspNetCore.Identity;

namespace SalesHub.Infrastructure.Identity;

/// <summary>
/// Identity-backed user (docs/01 `users`). Wave 0 carries the fields the
/// auth foundation needs; the full profile (branch, hire date, birthday,
/// photo, deactivation workflow fields) lands with Wave 1's user lifecycle.
/// A user has exactly one application role, held in Identity role membership;
/// <see cref="Role"/> mirrors it for cheap reads and is kept in sync by
/// the identity service.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeactivatedAtUtc { get; set; }
    public Guid? DeactivatedByUserId { get; set; }
    public string? DeactivationReason { get; set; }
    public DateTimeOffset? ScheduledReactivationAtUtc { get; set; }
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string roleName) : base(roleName)
    {
    }
}
