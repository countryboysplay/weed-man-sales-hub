namespace SalesHub.Domain.Entities;

// ── Owner security (CLAUDE.md §19, docs/04) ───────────────────────────────────

/// <summary>Per-Owner protected-flow credentials. The master recovery
/// credential exists only as a one-way verifier; the TOTP secret only
/// encrypted with Data Protection. Neither is ever displayed after setup.</summary>
public class OwnerSecurityConfig
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string MasterCredentialHash { get; set; } = string.Empty;
    public string? TotpSecretEncrypted { get; set; }
    public bool TotpEnabled { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    // Brute-force throttle on protected verification (docs/04).
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
}

/// <summary>Permanent security event stream for the Owner recovery surface
/// (failed verifications, lockouts, credential rotations).</summary>
public class OwnerRecoverySecurityEvent
{
    public Guid Id { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>Permanent record of Owner private-communication inspection
/// (CLAUDE.md §7/§19). Holds scope and reason — never message content;
/// deleted messages are not reconstructed.</summary>
public class PrivateCommunicationAccess
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Scope { get; set; } = string.Empty;
    /// <summary>JSON array of conversation ids in scope.</summary>
    public string TargetConversationIdsJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid AccessSessionId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
}

/// <summary>Owner emergency access session: max 60 minutes, other Owners
/// notified on start/end, terminable by another Owner with a reason.
/// Permanent audit (CLAUDE.md §19).</summary>
public class EmergencyAccessSession
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public Guid? EndedByUserId { get; set; }
    public string? EndReason { get; set; }
}

// ── sensitive exports (CLAUDE.md §13, docs/04) ────────────────────────────────

public class SensitiveExport
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;    // EXP
    public Guid RequestedByUserId { get; set; }
    public string Kind { get; set; } = string.Empty;        // EmployeeHistory
    public Guid? TargetUserId { get; set; }
    public string Format { get; set; } = string.Empty;      // Pdf | Csv
    public string Reason { get; set; } = string.Empty;
    public Guid BlobId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Child audit: every re-download of an export artifact.</summary>
public class SensitiveExportAccess
{
    public Guid Id { get; set; }
    public Guid ExportId { get; set; }
    public Guid AccessedByUserId { get; set; }
    public DateTimeOffset AccessedAtUtc { get; set; }
}

// ── settings (docs/01) ────────────────────────────────────────────────────────

public class SettingEntry
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = string.Empty;
    public SettingScope Scope { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public enum SettingScope
{
    /// <summary>Owner/system security settings — Owner only.</summary>
    System = 0,
    /// <summary>Ordinary settings the Owner may delegate to management.</summary>
    Management = 1,
}

// ── production governance records (docs/01 deployment) ───────────────────────

public class DeploymentRecord
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;    // PROD
    public string Version { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Notes { get; set; } = string.Empty;
    public Guid RecordedByUserId { get; set; }
    public DateTimeOffset DeployedAtUtc { get; set; }
}

public class StagingRecord
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;    // STAGE
    public string Reason { get; set; } = string.Empty;
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset RefreshedAtUtc { get; set; }
}

public class RollbackRecord
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;    // ROLL
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid RecordedByUserId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>Report-only recovery record (CLAUDE.md §20): a recovered artifact
/// keeps its source and never replaces the original.</summary>
public class RecoveryRecord
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = string.Empty;    // REC
    public Guid OwnerUserId { get; set; }
    public Guid ArchiveEntryId { get; set; }
    public string SourceDescription { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class KnownGoodVersion
{
    public Guid Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public Guid RecordedByUserId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public class BlockedRollbackVersion
{
    public Guid Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid RecordedByUserId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public class MaintenanceWindow
{
    public Guid Id { get; set; }
    public DateTimeOffset StartAtUtc { get; set; }
    public DateTimeOffset EndAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CanceledAtUtc { get; set; }
}
