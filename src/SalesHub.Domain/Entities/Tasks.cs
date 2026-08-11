namespace SalesHub.Domain.Entities;

/// <summary>
/// A management-authored task definition (CLAUDE.md §9). Each assignee gets
/// an independent instance; recurrence generates fresh instances per period.
/// </summary>
public class TaskDefinition
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskPriority Priority { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public TaskRecurrence Recurrence { get; set; }
    public bool OverdueReminders { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool Active { get; set; } = true;
}

public enum TaskPriority
{
    Normal = 0,
    High = 1,
}

public enum TaskRecurrence
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}

/// <summary>One assignee's independent copy (CLAUDE.md §9): completing it
/// removes it from their active list; history stays management-visible.</summary>
public class TaskInstance
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid AssigneeUserId { get; set; }
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Active;
    public DateTimeOffset? DueAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? LastOverdueReminderAtUtc { get; set; }

    /// <summary>Recurrence period this instance belongs to ("" for one-shot;
    /// "2026-08-11" daily, "2026-W33" weekly, "2026-08" monthly). Unique per
    /// definition+assignee+period so a re-run job cannot double-generate.</summary>
    public string PeriodKey { get; set; } = string.Empty;
}

public enum WorkTaskStatus
{
    Active = 0,
    Completed = 1,
}

public class TaskComment
{
    public Guid Id { get; set; }
    public Guid InstanceId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class TaskAttachment
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid BlobId { get; set; }
    public string OriginalName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
}
