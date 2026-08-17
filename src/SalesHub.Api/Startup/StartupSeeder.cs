using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Identity;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Workers;
using SalesHub.Workers.Jobs;

namespace SalesHub.Api.Startup;

/// <summary>
/// Startup provisioning: migrations (opt-in), the four roles, the Wave 0
/// scheduled jobs, and — only when configured — an initial Owner account.
/// There is no public registration; without a seeded Owner nobody can sign
/// in, which is the intended production posture until provisioning runs.
/// </summary>
public static class StartupSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesHubDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("StartupSeeder");

        if (configuration.GetValue("Database:MigrateOnStartup", false))
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied");
        }

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                _ = await roleManager.CreateAsync(new ApplicationRole(role));
            }
        }

        await EnsureJobAsync(db, IdleCapabilityStaleScanJob.Type, "* * * * *");
        await EnsureJobAsync(db, IdempotencyKeyCleanupJob.Type, "0 * * * *");
        await EnsureJobAsync(db, ScheduledReactivationJob.Type, "* * * * *");
        await EnsureJobAsync(db, AnnouncementMaintenanceJob.Type, "* * * * *");
        await EnsureJobAsync(db, WorkMaintenanceJob.Type, "*/15 * * * *");
        await EnsureJobAsync(db, PresenceEvaluationJob.Type, "* * * * *");

        // Default presence thresholds for the monitored role (docs/01); the
        // Owner tunes these later from settings.
        if (!await db.PresenceRuleSets.AnyAsync(r => r.Role == Roles.SalesAgent))
        {
            db.PresenceRuleSets.Add(new PresenceRuleSet
            {
                Id = Guid.CreateVersion7(),
                Role = Roles.SalesAgent,
            });
        }

        // Workforce catalogs (Wave 4). MinimumAgents 0 means the coverage
        // check passes until management configures a real floor.
        if (!await db.TimeOffTypes.AnyAsync())
        {
            db.TimeOffTypes.AddRange(
                new TimeOffType { Id = Guid.CreateVersion7(), Label = "Vacation", SortOrder = 1, Paid = true },
                new TimeOffType { Id = Guid.CreateVersion7(), Label = "Personal", SortOrder = 2, Paid = true },
                new TimeOffType { Id = Guid.CreateVersion7(), Label = "Unpaid", SortOrder = 3, Paid = false });
        }

        if (!await db.BreakTypes.AnyAsync())
        {
            db.BreakTypes.AddRange(
                new BreakType { Id = Guid.CreateVersion7(), Label = "Break", LimitMinutes = 15 },
                new BreakType { Id = Guid.CreateVersion7(), Label = "Lunch", LimitMinutes = 30 },
                new BreakType { Id = Guid.CreateVersion7(), Label = "Other", LimitMinutes = 60 });
        }

        if (!await db.CoverageRules.AnyAsync(r => r.Role == Roles.SalesAgent))
        {
            db.CoverageRules.Add(new CoverageRule
            {
                Id = Guid.CreateVersion7(),
                Role = Roles.SalesAgent,
                MinimumAgents = 0,
                Behavior = CoverageBehavior.WarnAndConfirm,
            });
        }

        await db.SaveChangesAsync();

        var recognitionService = scope.ServiceProvider
            .GetRequiredService<SalesHub.Application.Recognitions.RecognitionService>();
        await recognitionService.EnsureBuiltInBadgesAsync();

        var seedUsername = configuration["Seed:Owner:Username"];
        var seedPassword = configuration["Seed:Owner:Password"];
        if (!string.IsNullOrWhiteSpace(seedUsername) && !string.IsNullOrWhiteSpace(seedPassword))
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await userManager.FindByNameAsync(seedUsername) is null)
            {
                var owner = new ApplicationUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = seedUsername,
                    DisplayName = configuration["Seed:Owner:DisplayName"] ?? seedUsername,
                    Role = Roles.Owner,
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                var created = await userManager.CreateAsync(owner, seedPassword);
                if (created.Succeeded)
                {
                    _ = await userManager.AddToRoleAsync(owner, Roles.Owner);
                    logger.LogInformation("Seeded initial Owner account {Username}", seedUsername);
                }
                else
                {
                    logger.LogError("Owner seeding failed: {Errors}",
                        string.Join("; ", created.Errors.Select(e => e.Description)));
                }
            }
        }
    }

    private static async Task EnsureJobAsync(SalesHubDbContext db, string jobKey, string cron)
    {
        var existing = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.JobKey == jobKey);
        if (existing is not null)
        {
            return;
        }

        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            JobKey = jobKey,
            JobType = jobKey,
            CronExpression = cron,
            TimeZoneId = BusinessTime.BusinessTimeZoneId,
            Enabled = true,
        };
        job.NextRunAtUtc = ScheduledJobRunner.ComputeNextRun(job, DateTimeOffset.UtcNow);
        db.ScheduledJobs.Add(job);
    }
}
