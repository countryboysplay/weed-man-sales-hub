namespace SalesHub.Domain.Entities;

/// <summary>One execution of a scheduled job: outcome, attempt, correlation (docs/06).</summary>
public class ScheduledJobRun
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset ScheduledForUtc { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int Attempt { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorClass { get; set; }
    public string? ErrorMessage { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}
