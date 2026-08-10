namespace SalesHub.Domain.Entities;

/// <summary>
/// Management-mediated forgot-password flow (CLAUDE.md §3): no email token,
/// no self-service link. A user submits their username; management assigns a
/// replacement password through user administration. Visible to all
/// management roles including Owners (owner decision, 2026-08-10).
/// </summary>
public class PasswordResetRequest
{
    public Guid Id { get; set; }

    /// <summary>What the requester typed — kept even when it matches no account,
    /// so management sees the attempt without the API leaking existence.</summary>
    public string UsernameSubmitted { get; set; } = string.Empty;

    /// <summary>Resolved account, when the submitted username matched one.</summary>
    public Guid? MatchedUserId { get; set; }

    public PasswordResetRequestStatus Status { get; set; } = PasswordResetRequestStatus.Open;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? HandledByUserId { get; set; }
    public DateTimeOffset? HandledAtUtc { get; set; }
}

public enum PasswordResetRequestStatus
{
    Open = 0,
    Completed = 1,   // management assigned a replacement password
    Dismissed = 2,
}
