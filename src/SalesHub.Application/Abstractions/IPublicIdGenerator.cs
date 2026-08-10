namespace SalesHub.Application.Abstractions;

/// <summary>
/// Allocates the next NOTE-2026-00001-style public record id for the current
/// business year (America/Chicago), using the DB-backed sequence table with
/// row locking. Call inside the transaction that creates the record so an
/// aborted transaction cannot burn a visible gap silently.
/// </summary>
public interface IPublicIdGenerator
{
    Task<string> NextAsync(string prefix, CancellationToken cancellationToken = default);
}
