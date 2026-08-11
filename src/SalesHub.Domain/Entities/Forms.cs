namespace SalesHub.Domain.Entities;

/// <summary>
/// A form (CLAUDE.md §10): native builder forms or Google Form links.
/// Published forms are visible to all active users. Native published edits
/// create a new version snapshot and take effect immediately.
/// </summary>
public class Form
{
    public Guid Id { get; set; }
    public FormType Type { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public FormStatus Status { get; set; } = FormStatus.Draft;
    public string? ExternalUrl { get; set; }             // GoogleLink only
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int? PinRank { get; set; }
    public Guid? CurrentVersionId { get; set; }

    /// <summary>Native Email Request workflow forms track Open → Completed.</summary>
    public bool TracksCompletion { get; set; }
}

public enum FormType
{
    Native = 0,
    GoogleLink = 1,
}

public enum FormStatus
{
    Draft = 0,
    Published = 1,
}

/// <summary>
/// Immutable published definition snapshot. The definition is the builder
/// graph as JSON: sections → fields (SingleLine | Number | Dropdown | YesNo,
/// required flags, options, conditional branching). Submissions reference
/// the version they answered.
/// </summary>
public class FormVersion
{
    public Guid Id { get; set; }
    public Guid FormId { get; set; }
    public int VersionNumber { get; set; }
    public string DefinitionJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class FormSubmission
{
    public Guid Id { get; set; }
    public Guid FormId { get; set; }
    public Guid FormVersionId { get; set; }
    public Guid UserId { get; set; }
    public string AnswersJson { get; set; } = string.Empty;
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public FormSubmissionStatus Status { get; set; } = FormSubmissionStatus.Submitted;
    public Guid? CompletedByUserId { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public enum FormSubmissionStatus
{
    Submitted = 0,
    Open = 1,        // tracked workflow forms awaiting management completion
    Completed = 2,
}

/// <summary>
/// The dedicated Email Request workflow (CLAUDE.md §10): CID, customer
/// email, quote type, lawn area/coverage. Management marks complete, the
/// submitter is notified, and the completed request disappears — it is
/// deliberately not archived.
/// </summary>
public class EmailRequest
{
    public Guid Id { get; set; }
    public Guid SubmitterUserId { get; set; }
    public string Cid { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string QuoteType { get; set; } = string.Empty;
    public string LawnArea { get; set; } = string.Empty;
    public string Coverage { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
