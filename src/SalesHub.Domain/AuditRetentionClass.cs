namespace SalesHub.Domain;

/// <summary>
/// Retention classes for audit events (docs/04). Retention cleanup jobs read
/// this; nothing else may decide how long an audit row lives.
/// </summary>
public enum AuditRetentionClass
{
    Operational90Days = 0,
    Operational365Days = 1,
    AccountLifetime = 2,
    SevenYears = 3,
    Permanent = 4,
    Configurable = 5,
}
