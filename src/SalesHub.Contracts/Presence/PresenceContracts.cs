namespace SalesHub.Contracts.Presence;

// ── manual status ─────────────────────────────────────────────────────────────

/// <summary>Status is Available | Busy | Dnd; the custom message caps at 35
/// characters (presence mockup).</summary>
public sealed record SetPresenceStatusRequest(string Status, string? CustomMessage);

public sealed record MyPresenceDto(
    string Status,
    string? CustomMessage,
    DateTimeOffset? StatusChangedAtUtc,
    string DerivedState,
    IReadOnlyList<PresenceSegmentDto> Today,
    IReadOnlyList<PresenceFlagDto> TodayFlags);

// ── directory ─────────────────────────────────────────────────────────────────

public sealed record PresenceDirectoryEntryDto(
    Guid UserId,
    string DisplayName,
    string Role,
    string State,
    string? CustomMessage,
    DateTimeOffset? StatusChangedAtUtc);

// ── timeline / flags ──────────────────────────────────────────────────────────

public sealed record PresenceSegmentDto(
    string State,
    DateTimeOffset StartAtUtc,
    DateTimeOffset? EndAtUtc);

public sealed record PresenceFlagDto(
    Guid Id,
    string PublicId,
    Guid UserId,
    string DisplayName,
    string Category,
    string Severity,
    string Status,
    DateOnly BusinessDate,
    DateTimeOffset StartAtUtc,
    DateTimeOffset? EndAtUtc,
    string? LinkedPublicIds);

/// <summary>Supervisor view: aggregate counts only, no per-agent detail
/// (permission matrix: serious-summary for supervisors).</summary>
public sealed record PresenceAlertSummaryDto(
    DateOnly BusinessDate,
    int OpenSerious,
    int OpenWarnings,
    int OpenLogged,
    int ResolvedToday);

public sealed record ResolvePresenceFlagRequest(string Action); // resolve | suppress

// ── shifts ────────────────────────────────────────────────────────────────────

public sealed record ShiftTemplateDto(
    Guid Id,
    string Name,
    string Role,
    string DayOfWeek,
    TimeOnly StartLocalTime,
    TimeOnly EndLocalTime,
    bool Active);

public sealed record CreateShiftTemplateRequest(
    string Name,
    string Role,
    string DayOfWeek,
    TimeOnly StartLocalTime,
    TimeOnly EndLocalTime);

public sealed record AssignShiftRequest(
    Guid UserId,
    Guid ShiftTemplateId,
    DateOnly StartDate,
    DateOnly? EndDate);

public sealed record ShiftAssignmentDto(
    Guid Id,
    Guid UserId,
    Guid ShiftTemplateId,
    string TemplateName,
    string DayOfWeek,
    TimeOnly StartLocalTime,
    TimeOnly EndLocalTime,
    DateOnly StartDate,
    DateOnly? EndDate);

// ── schedule exceptions ───────────────────────────────────────────────────────

public sealed record CreateScheduleExceptionRequest(
    Guid UserId,
    DateOnly Date,
    TimeOnly? ReplacementStartLocal,
    TimeOnly? ReplacementEndLocal,
    string Label,
    string? Reason,
    bool SuspendsPresence,
    bool AcknowledgmentRequired,
    DateTimeOffset? AcknowledgeByUtc);

public sealed record ScheduleExceptionDto(
    Guid Id,
    string PublicId,
    Guid UserId,
    DateOnly Date,
    TimeOnly? ReplacementStartLocal,
    TimeOnly? ReplacementEndLocal,
    string Label,
    string Reason,
    bool SuspendsPresence,
    bool AcknowledgmentRequired,
    DateTimeOffset? AcknowledgeByUtc,
    DateTimeOffset? AcknowledgedAtUtc);
