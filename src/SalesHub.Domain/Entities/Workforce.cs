namespace SalesHub.Domain.Entities;

// ── shifts ────────────────────────────────────────────────────────────────────

/// <summary>A weekly shift pattern (docs/01): local wall times, per weekday.</summary>
public class ShiftTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartLocalTime { get; set; }
    public TimeOnly EndLocalTime { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>Versioned assignment range: which template applies to a user.</summary>
public class UserShiftAssignment
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ShiftTemplateId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public Guid AssignedByUserId { get; set; }
    public DateTimeOffset AssignedAtUtc { get; set; }
}

/// <summary>Schedule exception (SCH-YYYY-#####): a dated replacement window
/// with presence behavior and optional required acknowledgment.</summary>
public class ScheduleException
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly? ReplacementStartLocal { get; set; }
    public TimeOnly? ReplacementEndLocal { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    /// <summary>True = presence monitoring is suspended for the window.</summary>
    public bool SuspendsPresence { get; set; }

    public bool AcknowledgmentRequired { get; set; }
    public DateTimeOffset? AcknowledgeByUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

// ── presence ──────────────────────────────────────────────────────────────────

public enum PresenceStatus
{
    Available = 0,
    Busy = 1,
    Dnd = 2,
}

/// <summary>A normalized presence interval (docs/01) written by the evaluator.</summary>
public class PresenceSegment
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public PresenceSegmentState State { get; set; }
    public DateTimeOffset StartAtUtc { get; set; }
    public DateTimeOffset? EndAtUtc { get; set; }
}

public enum PresenceSegmentState
{
    Available = 0,
    Busy = 1,
    Dnd = 2,
    Away = 3,
    OnBreak = 4,
    Offline = 5,
    TechnicalGrace = 6,
    ApprovedException = 7,
}

/// <summary>Presence exception flag (PRS-YYYY-#####), raised by the evaluator
/// when rules are violated; suppressed by approved exceptions/grace.</summary>
public class PresenceFlag
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Category { get; set; } = string.Empty;   // LateStart | Disappeared | BreakOverrun
    public PresenceFlagSeverity Severity { get; set; }
    public DateTimeOffset StartAtUtc { get; set; }
    public DateTimeOffset? EndAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public PresenceFlagStatus Status { get; set; } = PresenceFlagStatus.Open;
    public string? LinkedPublicIds { get; set; }           // e.g. "TECH-2026-00112"
    public DateOnly BusinessDate { get; set; }
}

public enum PresenceFlagSeverity
{
    Logged = 0,
    Warning = 1,
    Serious = 2,
}

public enum PresenceFlagStatus
{
    Open = 0,
    Resolved = 1,
    Suppressed = 2,
}

/// <summary>Role-scoped presence thresholds (docs/01 presence_rule_sets).</summary>
public class PresenceRuleSet
{
    public Guid Id { get; set; }
    public string Role { get; set; } = string.Empty;
    public int LateStartGraceMinutes { get; set; } = 10;
    public int OfflineGraceMinutes { get; set; } = 10;
    public int SeriousOfflineMinutes { get; set; } = 20;
    public int BreakOverrunGraceMinutes { get; set; } = 5;
}

// ── time off ──────────────────────────────────────────────────────────────────

public class TimeOffType
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool Paid { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>Time-off request (TO-YYYY-#####), full or partial day(s).</summary>
public class TimeOffRequest
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public Guid TypeId { get; set; }
    public bool FullDay { get; set; } = true;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public TimeOnly? StartLocalTime { get; set; }
    public TimeOnly? EndLocalTime { get; set; }
    public string Reason { get; set; } = string.Empty;     // optional
    public TimeOffStatus Status { get; set; } = TimeOffStatus.Pending;
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? ReviewNote { get; set; }                // optional on approval
    public string? DenialReason { get; set; }              // required on denial
    public string? CoverageSnapshotJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum TimeOffStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Canceled = 3,
}

/// <summary>Canceling APPROVED time off needs management approval (CLAUDE.md §12).</summary>
public class TimeOffCancellationRequest
{
    public Guid Id { get; set; }
    public Guid TimeOffRequestId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public TimeOffStatus? ResultStatus { get; set; }       // null = pending
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
}

/// <summary>Coverage threshold per role (approvals mockup): approving time
/// off below the minimum can warn, require confirmation, or block.</summary>
public class CoverageRule
{
    public Guid Id { get; set; }
    public string Role { get; set; } = string.Empty;
    public int MinimumAgents { get; set; }
    public CoverageBehavior Behavior { get; set; } = CoverageBehavior.WarnAndConfirm;
}

public enum CoverageBehavior
{
    Warn = 0,
    WarnAndConfirm = 1,
    Block = 2,
}

// ── breaks ────────────────────────────────────────────────────────────────────

public class BreakType
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int LimitMinutes { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>A break session: one active at a time per user (CLAUDE.md §12).</summary>
public class BreakSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BreakTypeId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateOnly BusinessDate { get; set; }
    public bool OverrunFlagged { get; set; }
}

/// <summary>Break correction (BRK-YYYY-#####): the original window is
/// preserved here even after approval recalculates flags.</summary>
public class BreakCorrectionRequest
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid BreakSessionId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset OriginalStartAtUtc { get; set; }
    public DateTimeOffset? OriginalEndAtUtc { get; set; }
    public DateTimeOffset CorrectedStartAtUtc { get; set; }
    public DateTimeOffset CorrectedEndAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public BreakCorrectionStatus Status { get; set; } = BreakCorrectionStatus.Pending;
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum BreakCorrectionStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
}

// ── technical ─────────────────────────────────────────────────────────────────

/// <summary>Technical report (TECH-YYYY-#####). Reporting alone never pauses
/// presence — only an explicit management grace grant does (CLAUDE.md §12).</summary>
public class TechnicalReport
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public string IssueType { get; set; } = string.Empty;  // Internet | Computer | BrowserPwa | Other
    public string Description { get; set; } = string.Empty;
    public string Page { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string BrowserFamily { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class TechnicalGrant
{
    public Guid Id { get; set; }
    public Guid TechnicalReportId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset StartAtUtc { get; set; }
    public DateTimeOffset EndAtUtc { get; set; }
    public Guid GrantedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
