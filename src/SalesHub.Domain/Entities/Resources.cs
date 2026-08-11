namespace SalesHub.Domain.Entities;

/// <summary>Nested resource folder (adjacency list, manual ordering).</summary>
public class ResourceFolder
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

/// <summary>
/// A resource (CLAUDE.md §11): file (PDF/XLSX/DOCX/PPTX/image), website
/// link, or video link. Employees read only; agents can never download;
/// PDF viewing is watermarked; replace creates a new blob.
/// </summary>
public class Resource
{
    public Guid Id { get; set; }
    public Guid? FolderId { get; set; }
    public ResourceType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? BlobId { get; set; }
    public string? ExternalUrl { get; set; }
    public bool SensitiveStagingPlaceholder { get; set; }
    public int SortOrder { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

public enum ResourceType
{
    File = 0,
    Link = 1,
    Video = 2,
}

public class ResourceFavorite
{
    public Guid UserId { get; set; }
    public Guid ResourceId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Manager download audit, Owner-visible for 365 days (CLAUDE.md §11).</summary>
public class ResourceDownloadAudit
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
    public string ResourceTitle { get; set; } = string.Empty;
    public bool Watermarked { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
