namespace SalesHub.Domain.Entities;

// ── support (CLAUDE.md §14) ───────────────────────────────────────────────────

/// <summary>Support ticket (SUP-YYYY-#####). Context (app version, browser,
/// device, page, correlation) is captured server-side at creation. The system
/// may suggest a priority; management can override it.</summary>
public class SupportTicket
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public string IssueType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Page { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string BrowserFamily { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;

    public SupportPriority Priority { get; set; } = SupportPriority.Normal;
    public SupportPriority? SuggestedPriority { get; set; }
    public string? SuggestedPriorityReason { get; set; }

    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Open;
    public Guid? PrimaryAssigneeUserId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public bool ForceClosed { get; set; }

    /// <summary>Set when the reporter confirms closure after Resolved.</summary>
    public bool ReporterConfirmedClosure { get; set; }
}

public enum SupportPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

public enum SupportTicketStatus
{
    Open = 0,
    InProgress = 1,
    WaitingOnUser = 2,
    Resolved = 3,
    Closed = 4,
}

/// <summary>Ticket chronology entry. InternalNote is never visible to the
/// reporter — visibility filtering is server-side, always.</summary>
public class SupportMessage
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid AuthorUserId { get; set; }
    public SupportMessageVisibility Visibility { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum SupportMessageVisibility
{
    EmployeeReply = 0,
    InternalNote = 1,
}

public class SupportCollaborator
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}

public class SupportAttachment
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid BlobId { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Link from a ticket to another public-id record (TECH, PRS, ...).</summary>
public class SupportLink
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public string TargetPublicId { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
