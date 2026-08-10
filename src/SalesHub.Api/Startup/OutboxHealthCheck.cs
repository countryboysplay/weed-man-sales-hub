using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Startup;

/// <summary>
/// Surfaces outbox lag and poison rows (docs/06): parked failed events are a
/// health problem, not a silent condition.
/// </summary>
public sealed class OutboxHealthCheck(SalesHubDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failed = await db.OutboxMessages.CountAsync(m => m.Failed, cancellationToken);
        var pending = await db.OutboxMessages.CountAsync(
            m => m.ProcessedAtUtc == null && !m.Failed, cancellationToken);

        var data = new Dictionary<string, object>
        {
            ["pending"] = pending,
            ["failed"] = failed,
        };

        if (failed > 0)
        {
            return HealthCheckResult.Degraded($"{failed} outbox event(s) parked as failed.", data: data);
        }

        return pending > 500
            ? HealthCheckResult.Degraded($"Outbox backlog is {pending} events.", data: data)
            : HealthCheckResult.Healthy("Outbox current.", data);
    }
}
