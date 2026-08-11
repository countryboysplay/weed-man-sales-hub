using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Recognitions;
using SalesHub.Application.Tasks;

namespace SalesHub.Workers.Jobs;

/// <summary>
/// Task and recognition housekeeping: recurrence generation, overdue task
/// reminders (CLAUDE.md §9), and the 30-day recognition archive transition
/// (CLAUDE.md §13). Runs every 15 minutes; all operations are idempotent.
/// </summary>
public sealed class WorkMaintenanceJob(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkMaintenanceJob> logger) : IScheduledJobHandler
{
    public const string Type = "work-maintenance";
    public string JobType => Type;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<TaskService>();
        var recognitions = scope.ServiceProvider.GetRequiredService<RecognitionService>();

        var generated = await tasks.GenerateRecurringAsync(cancellationToken);
        var reminded = await tasks.SendOverdueRemindersAsync(cancellationToken);
        var archived = await recognitions.ArchiveExpiredAsync(cancellationToken);

        if (generated + reminded + archived > 0)
        {
            logger.LogInformation(
                "Work maintenance: {Generated} recurring task(s), {Reminded} overdue reminder(s), {Archived} recognition(s) archived",
                generated, reminded, archived);
        }
    }
}
