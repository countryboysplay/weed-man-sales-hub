namespace SalesHub.Domain.Entities;

/// <summary>
/// Replay protection for offline-queued mutations (CLAUDE.md §16). A replayed
/// request with the same key returns the stored response instead of running
/// the operation twice. A same-key request with a different payload hash is
/// rejected — that is a client bug, not a retry.
/// </summary>
public class IdempotencyKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int ResponseStatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
