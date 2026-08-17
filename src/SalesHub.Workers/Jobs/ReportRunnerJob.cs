using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Reporting;

namespace SalesHub.Workers.Jobs;

/// <summary>Runs due report schedules (docs/01 reports). Failures are recorded
/// on the run rows and notified by <see cref="ReportService"/> itself.</summary>
public sealed class ReportRunnerJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ReportRunnerJob> logger) : IScheduledJobHandler
{
    public const string Type = "report-runner";
    public string JobType => Type;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<ReportService>();
        var ran = await reports.RunDueSchedulesAsync(cancellationToken);
        if (ran > 0)
        {
            logger.LogInformation("Report runner executed {Count} due schedule(s)", ran);
        }
    }
}
