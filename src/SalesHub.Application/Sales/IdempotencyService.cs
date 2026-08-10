using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Sales;

/// <summary>
/// Idempotency-Key handling for replayable mutations (CLAUDE.md §16,
/// acceptance scenario 2): the same key with the same payload returns the
/// stored response instead of running twice; the same key with a different
/// payload is a client bug and is refused.
/// </summary>
public sealed class IdempotencyService(
    IAppDb db,
    BusinessTime businessTime,
    IOptions<SecurityOptions> securityOptions)
{
    public sealed record Replay(int StatusCode, string? ResponseJson, bool HashMismatch);

    public static string HashRequest(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    public async Task<Replay?> TryReplayAsync(
        Guid userId, string key, string requestHash, CancellationToken ct = default)
    {
        var existing = await db.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.UserId == userId && k.Key == key, ct);
        if (existing is null)
        {
            return null;
        }

        return existing.RequestHash == requestHash
            ? new Replay(existing.ResponseStatusCode, existing.ResponseJson, false)
            : new Replay(0, null, HashMismatch: true);
    }

    /// <summary>Stores the outcome; a concurrent duplicate insert loses the
    /// race silently — the unique (user, key) index guarantees one winner.</summary>
    public async Task StoreAsync(
        Guid userId, string key, string operation, string requestHash,
        int statusCode, string responseJson, CancellationToken ct = default)
    {
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Key = key,
            Operation = operation,
            RequestHash = requestHash,
            ResponseStatusCode = statusCode,
            ResponseJson = responseJson,
            CreatedAtUtc = businessTime.UtcNow,
            ExpiresAtUtc = businessTime.UtcNow + securityOptions.Value.IdempotencyKeyLifetime,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race to a concurrent identical request; its stored
            // response is authoritative.
        }
    }
}
