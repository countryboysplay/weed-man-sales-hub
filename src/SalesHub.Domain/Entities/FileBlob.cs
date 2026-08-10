namespace SalesHub.Domain.Entities;

/// <summary>
/// Immutable uploaded-file blob metadata (docs/01, docs/07). Blobs are never
/// overwritten — "replace" creates a new blob and repoints the reference.
/// Storage keys are server-generated; browser MIME is never trusted.
/// </summary>
public class FileBlob
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string ScanStatus { get; set; } = "Pending";     // integration point, docs/07
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public DateTimeOffset? PurgeEligibleAtUtc { get; set; }
}
