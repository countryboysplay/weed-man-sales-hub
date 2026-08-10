namespace SalesHub.Domain.Entities;

/// <summary>
/// One sale (CLAUDE.md §6, docs/01). Money is decimal numeric(12,2); the
/// business date is assigned server-side in America/Chicago; deletion is a
/// tombstone — removed from totals immediately, visible in the current-day
/// UI until business midnight, never restorable.
/// </summary>
public class Sale
{
    public Guid Id { get; set; }
    public Guid SellerUserId { get; set; }

    /// <summary>Numeric string, validated digits-only. Immutable after create —
    /// a wrong CID is fixed by same-day delete + re-add.</summary>
    public string Cid { get; set; } = string.Empty;

    public SaleType SaleType { get; set; }
    public string Campaign { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public SaleState State { get; set; } = SaleState.Active;
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL xmin).</summary>
    public uint Version { get; set; }
}

public enum SaleType
{
    Program = 0,
    Upsell = 1,
}

public enum SaleState
{
    Active = 0,
    Deleted = 1,
}

/// <summary>
/// Management correction audit (docs/01): before/after values, required
/// reason, actor, exact time. The sale row shows current truth; this table
/// shows how it got there. Same-day employee deletions do NOT create rows
/// here — they are the seller's own irreversible action, not a correction.
/// </summary>
public class SaleCorrection
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public SaleCorrectionType CorrectionType { get; set; }
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public enum SaleCorrectionType
{
    Amend = 0,
    Delete = 1,
}

/// <summary>Explicit "prior sale was canceled, this is a resale" confirmation
/// that allowed a duplicate Program CID (CLAUDE.md §6).</summary>
public class SaleDuplicateOverride
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public Guid PriorSaleId { get; set; }
    public Guid ConfirmedByUserId { get; set; }
    public DateTimeOffset ConfirmedAtUtc { get; set; }
}
