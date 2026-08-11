using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Announcements;

namespace SalesHub.Workers.Jobs;

/// <summary>
/// Announcement housekeeping (CLAUDE.md §8), every minute: publish due
/// scheduled announcements, release seven-day pins (the announcement stays
/// active), and send configured reminders to outstanding users only.
/// </summary>
public sealed class AnnouncementMaintenanceJob(
    IServiceScopeFactory scopeFactory,
    ILogger<AnnouncementMaintenanceJob> logger) : IScheduledJobHandler
{
    public const string Type = "announcement-maintenance";
    public string JobType => Type;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var announcements = scope.ServiceProvider.GetRequiredService<AnnouncementService>();

        var published = await announcements.PublishDueScheduledAsync(cancellationToken);
        var unpinned = await announcements.AutoUnpinExpiredAsync(cancellationToken);
        var reminded = await announcements.SendDueRemindersAsync(cancellationToken);

        if (published + unpinned + reminded > 0)
        {
            logger.LogInformation(
                "Announcement maintenance: {Published} published, {Unpinned} unpinned, {Reminded} reminded",
                published, unpinned, reminded);
        }
    }
}
