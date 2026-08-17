using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Presence;

namespace SalesHub.Workers.Jobs;

/// <summary>
/// Minute-cadence presence evaluation for monitored roles (docs/05): segment
/// upkeep, PRS flag raising/escalation, and suppression handling all live in
/// <see cref="PresenceEvaluator"/> — this is only the scheduling shell.
/// </summary>
public sealed class PresenceEvaluationJob(
    IServiceScopeFactory scopeFactory,
    ILogger<PresenceEvaluationJob> logger) : IScheduledJobHandler
{
    public const string Type = "presence-evaluation";
    public string JobType => Type;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<PresenceEvaluator>();
        var changed = await evaluator.EvaluateAsync(cancellationToken);
        if (changed > 0)
        {
            logger.LogInformation("Presence evaluation raised/updated {Count} flag(s)", changed);
        }
    }
}
