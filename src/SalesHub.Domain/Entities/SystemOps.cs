namespace SalesHub.Domain.Entities;

// ── offline sync visibility (CLAUDE.md §16) ───────────────────────────────────

/// <summary>Server-side record of an accepted/rejected queued sync operation.
/// The client is the primary queue holder; this powers the management Sync
/// Health view (per user/device pending/failures).</summary>
public class SyncAction
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public SyncActionStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum SyncActionStatus
{
    Accepted = 0,
    Rejected = 1,
}

/// <summary>Management-initiated remote command (Resync / Refresh /
/// ClearSafeCache), delivered over SignalR to the target user's devices and
/// audited for 90 days (CLAUDE.md §16).</summary>
public class RemoteDeviceCommand
{
    public Guid Id { get; set; }
    public RemoteDeviceCommandType CommandType { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid? TargetSessionId { get; set; }
    public string? TargetDeviceId { get; set; }
    public RemoteDeviceCommandStatus Status { get; set; } = RemoteDeviceCommandStatus.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}

public enum RemoteDeviceCommandType
{
    Resync = 0,
    Refresh = 1,
    ClearSafeCache = 2,
}

public enum RemoteDeviceCommandStatus
{
    Pending = 0,
    Acknowledged = 1,
    Expired = 2,
}

// ── reports and archive (docs/01) ─────────────────────────────────────────────

/// <summary>A recurring report definition. Evaluated in business time by the
/// report-runner job; artifacts land in the archive.</summary>
public class ReportSchedule
{
    public Guid Id { get; set; }
    public ReportType ReportType { get; set; }
    public ReportCadence Cadence { get; set; }
    /// <summary>Local America/Chicago hour (0-23) the run is due.</summary>
    public int HourLocal { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? NextDueAtUtc { get; set; }
    public DateTimeOffset? LastRunAtUtc { get; set; }
}

public enum ReportType
{
    SalesSummary = 0,
    PresenceAttendanceSummary = 1,
    SupportTrends = 2,
}

public enum ReportCadence
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
}

public class ReportRun
{
    public Guid Id { get; set; }
    public Guid? ScheduleId { get; set; }
    public ReportType ReportType { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public Guid? ArtifactBlobId { get; set; }
    /// <summary>Null for scheduled runs; set for manual on-demand runs.</summary>
    public Guid? TriggeredByUserId { get; set; }
}

/// <summary>Archive Center entry: a durable, browsable generated artifact.
/// Recovery marking arrives with the Wave 6 report-only recovery flow.</summary>
public class ArchiveEntry
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public ReportType ReportType { get; set; }
    public Guid BlobId { get; set; }
    public Guid? ReportRunId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool Recovered { get; set; }
    public string? RecoveredFromNote { get; set; }
}
