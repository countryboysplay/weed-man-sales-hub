namespace SalesHub.Domain.Entities;

// ── employee management record (CLAUDE.md §13) ───────────────────────────────

/// <summary>Management note (NOTE-YYYY-#####): the primary entry is append-only
/// — corrections and outcomes arrive as follow-ups, never edits. Text only.</summary>
public class ManagementNote
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid EmployeeUserId { get; set; }
    public string Category { get; set; } = string.Empty;   // Attendance | Coaching | Technical | Other
    public ManagementNotePriority Priority { get; set; }
    public ManagementNoteStatus Status { get; set; } = ManagementNoteStatus.Open;
    public string Body { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? ResolutionNote { get; set; }

    /// <summary>High-priority notes auto-pin; null = not pinned.</summary>
    public int? PinnedRank { get; set; }

    public bool AcknowledgmentRequired { get; set; }
}

public enum ManagementNotePriority
{
    Normal = 0,
    High = 1,
}

public enum ManagementNoteStatus
{
    Open = 0,
    Resolved = 1,
}

/// <summary>Append-only chronology under a note. Reopens and resolutions land
/// here too, so a reopen preserves the prior resolution (CLAUDE.md §13).</summary>
public class ManagementNoteFollowup
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Guid AuthorUserId { get; set; }
    public ManagementNoteFollowupKind Kind { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum ManagementNoteFollowupKind
{
    Followup = 0,
    Resolution = 1,
    Reopen = 2,
}

/// <summary>A manager who must acknowledge the note.</summary>
public class ManagementNoteAckTarget
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Guid TargetUserId { get; set; }
    public DateTimeOffset RequiredAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}

/// <summary>Validated link between public-id records (NOTE ↔ PRS/BRK/TECH/TO/
/// SCH/SUP...). Unlinking keeps the row with a mandatory reason — the link
/// history is part of the record.</summary>
public class RecordLink
{
    public Guid Id { get; set; }
    public string SourcePublicId { get; set; } = string.Empty;
    public string TargetPublicId { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? RemovedByUserId { get; set; }
    public DateTimeOffset? RemovedAtUtc { get; set; }
    public string? RemoveReason { get; set; }

    public bool IsActive => RemovedAtUtc is null;
}

/// <summary>One shared, configurable tag library (CLAUDE.md §13).</summary>
public class ManagementTag
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class TaggedEntity
{
    public Guid Id { get; set; }
    public Guid TagId { get; set; }
    public string EntityPublicId { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
