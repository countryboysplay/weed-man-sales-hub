using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// DB-backed yearly counter (docs/12). A single upsert increments and returns
/// the next value atomically; PostgreSQL row locking serializes concurrent
/// allocations for the same (prefix, year). Runs in the caller's transaction
/// when one is open.
/// </summary>
public sealed class PostgresPublicIdGenerator(
    SalesHubDbContext db,
    BusinessTime businessTime) : IPublicIdGenerator
{
    public async Task<string> NextAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var normalized = prefix.ToUpperInvariant();
        var year = businessTime.CurrentBusinessYear;

        var value = await db.Database
            .SqlQuery<int>($"""
                INSERT INTO public_id_sequences (prefix, year, last_value, updated_at_utc)
                VALUES ({normalized}, {year}, 1, now())
                ON CONFLICT (prefix, year)
                DO UPDATE SET last_value = public_id_sequences.last_value + 1,
                              updated_at_utc = now()
                RETURNING last_value AS "Value"
                """)
            .SingleAsync(cancellationToken);

        return PublicRecordId.Format(normalized, year, value);
    }
}
