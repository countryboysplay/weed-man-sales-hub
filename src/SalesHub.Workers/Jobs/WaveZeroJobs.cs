using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Auth;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers.Jobs;

/// <summary>
/// Marks Verified sessions whose idle-capability lease lapsed as Stale, which
/// blocks monitored work until the browser re-verifies (docs/05).
/// </summary>
public sealed class IdleCapabilityStaleScanJob(
    IServiceScopeFactory scopeFactory,
    ILogger<IdleCapabilityStaleScanJob> logger) : IScheduledJobHandler
{
    public const string Type = "idle-capability-stale-scan";
    public string JobType => Type;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var idleCapability = scope.ServiceProvider.GetRequiredService<IdleCapabilityService>();
        var marked = await idleCapability.MarkStaleSessionsAsync(cancellationToken);
        if (marked > 0)
        {
            logger.LogInformation("Marked {Count} idle-capability session(s) stale", marked);
        }
    }
}

/// <summary>Removes expired idempotency keys past their retention.</summary>
public sealed class IdempotencyKeyCleanupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<IdempotencyKeyCleanupJob> logger) : IScheduledJobHandler
{
    public const string Type = "idempotency-key-cleanup";
    public string JobType => Type;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesHubDbContext>();
        var removed = await db.IdempotencyKeys
            .Where(k => k.ExpiresAtUtc < DateTimeOffset.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} expired idempotency key(s)", removed);
        }
    }
}
