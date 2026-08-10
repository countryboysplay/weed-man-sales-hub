namespace SalesHub.Domain.Entities;

/// <summary>
/// DB-backed yearly counter for human-readable record IDs (docs/12).
/// Composite key (prefix, year); allocated with row locking inside the
/// caller's transaction. Never derived from COUNT(*)+1.
/// </summary>
public class PublicIdSequence
{
    public string Prefix { get; set; } = string.Empty;
    public int Year { get; set; }
    public int LastValue { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
