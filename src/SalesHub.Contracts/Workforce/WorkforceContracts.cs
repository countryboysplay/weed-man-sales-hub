namespace SalesHub.Contracts.Workforce;

// ── time off ──────────────────────────────────────────────────────────────────

public sealed record TimeOffTypeDto(Guid Id, string Label, bool Paid);

public sealed record CreateTimeOffRequest(
    Guid TypeId,
    bool FullDay,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartLocalTime,
    TimeOnly? EndLocalTime,
    string? Reason);

public sealed record TimeOffRequestDto(
    Guid Id,
    string PublicId,
    Guid UserId,
    string DisplayName,
    string TypeLabel,
    bool FullDay,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartLocalTime,
    TimeOnly? EndLocalTime,
    string Reason,
    string Status,
    string? ReviewNote,
    string? DenialReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    bool CancellationPending);

/// <summary>Approval body. When coverage would dip below the rule's minimum
/// and the behavior is WarnAndConfirm, the first call returns 409
/// coverageConfirmationRequired and the client retries with ConfirmCoverage.</summary>
public sealed record ApproveTimeOffRequest(string? Note, bool ConfirmCoverage = false);

public sealed record DenyTimeOffRequest(string Reason);

public sealed record CoverageCheckDto(
    string Role,
    int MinimumAgents,
    string Behavior,
    IReadOnlyList<CoverageDayDto> Days);

public sealed record CoverageDayDto(DateOnly Date, int Scheduled, int OnApprovedLeave, int Remaining);

public sealed record DecideCancellationRequest(bool Approve);

// ── breaks ────────────────────────────────────────────────────────────────────

public sealed record BreakTypeDto(Guid Id, string Label, int LimitMinutes);

public sealed record StartBreakRequest(Guid BreakTypeId);

public sealed record BreakSessionDto(
    Guid Id,
    Guid UserId,
    string TypeLabel,
    int LimitMinutes,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    DateOnly BusinessDate,
    bool OverrunFlagged);

public sealed record RequestBreakCorrectionRequest(
    DateTimeOffset CorrectedStartAtUtc,
    DateTimeOffset CorrectedEndAtUtc,
    string Reason);

public sealed record BreakCorrectionDto(
    Guid Id,
    string PublicId,
    Guid BreakSessionId,
    Guid RequestedByUserId,
    string DisplayName,
    DateTimeOffset OriginalStartAtUtc,
    DateTimeOffset? OriginalEndAtUtc,
    DateTimeOffset CorrectedStartAtUtc,
    DateTimeOffset CorrectedEndAtUtc,
    string Reason,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record DecideBreakCorrectionRequest(bool Approve);

/// <summary>Management edit of a past day's break (after-midnight fixes go
/// through management, not self-service corrections). Reason is required
/// and audited.</summary>
public sealed record EditBreakRequest(
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string Reason);

// ── technical reports / grace ─────────────────────────────────────────────────

public sealed record CreateTechnicalReportRequest(
    string IssueType,   // Internet | Computer | BrowserPwa | Other
    string Description,
    string? Page,
    string? AppVersion);

public sealed record TechnicalReportDto(
    Guid Id,
    string PublicId,
    Guid ReporterUserId,
    string DisplayName,
    string IssueType,
    string Description,
    string Page,
    DateTimeOffset CreatedAtUtc,
    bool HasActiveGrant);

public sealed record GrantTechnicalGraceRequest(
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string Reason);

// ── unified approvals queue ───────────────────────────────────────────────────

public sealed record ApprovalsQueueDto(
    int PendingTotal,
    IReadOnlyList<TimeOffRequestDto> TimeOff,
    IReadOnlyList<TimeOffCancellationDto> TimeOffCancellations,
    IReadOnlyList<BreakCorrectionDto> BreakCorrections,
    int OpenPasswordResets);

public sealed record TimeOffCancellationDto(
    Guid Id,
    Guid TimeOffRequestId,
    string TimeOffPublicId,
    Guid RequestedByUserId,
    string DisplayName,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset CreatedAtUtc);
