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

    // Wave 1 profile fields (docs/01). Phone lives on IdentityUser.PhoneNumber.
    // Core fields are manager-maintained; phone/birthday/photo are
    // user-editable (profile mockup: "MIXED CONTROL").
    public Guid? BranchId { get; set; }
    public DateOnly? HireDate { get; set; }
    public DateOnly? Birthday { get; set; }
    public Guid? ProfilePhotoBlobId { get; set; }

    /// <summary>Marker so the one-hour advance reactivation notice fires once.</summary>
    public DateTimeOffset? ReactivationNoticeSentAtUtc { get; set; }

    // Wave 4 presence: manual status is user-set and persistent (CLAUDE.md
    // §12); DND stays until the user changes it. Custom status ≤ 35 chars.
    public Domain.Entities.PresenceStatus PresenceStatus { get; set; }
        = Domain.Entities.PresenceStatus.Available;
    public string? CustomStatusMessage { get; set; }
    public DateTimeOffset? PresenceStatusChangedAtUtc { get; set; }
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
