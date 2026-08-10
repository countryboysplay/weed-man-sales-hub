namespace SalesHub.Domain.Entities;

/// <summary>
/// Persistent scheduled-job definition (docs/06). Business schedules are
/// expressed as cron in the job's timezone (normally America/Chicago) and the
/// next run is stored as UTC. Claiming uses a lease so a crashed host's jobs
/// recover when the lease lapses.
/// </summary>
public class ScheduledJob
{
    public Guid Id { get; set; }
    public string JobKey { get; set; } = string.Empty;      // unique, e.g. "idle-capability-stale-scan"
    public string JobType { get; set; } = string.Empty;     // handler discriminator
    public string CronExpression { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "America/Chicago";
    public bool Enabled { get; set; } = true;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public DateTimeOffset? NextRunAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public DateTimeOffset? LastFailureAtUtc { get; set; }
    public string? LastError { get; set; }
}
