using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Domain;
using SalesHub.Infrastructure.Identity;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers.Jobs;

/// <summary>
/// Scheduled reactivation (user-admin mockup): management gets a one-hour
/// advance notice, and at the scheduled Central time the account reactivates
/// and both the user and management are notified. Idempotent — re-running
/// after a crash cannot double-notify (the advance-notice marker and the
/// IsActive flip are both checked).
/// </summary>
public sealed class ScheduledReactivationJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledReactivationJob> logger) : IScheduledJobHandler
{
    public const string Type = "scheduled-reactivation";
    public string JobType => Type;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesHubDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var businessTime = scope.ServiceProvider.GetRequiredService<BusinessTime>();
        var now = businessTime.UtcNow;

        // Advance notice: due within the next hour, not yet announced.
        var upcoming = await db.Set<ApplicationUser>()
            .Where(u => !u.IsActive
                && u.ScheduledReactivationAtUtc != null
                && u.ScheduledReactivationAtUtc > now
                && u.ScheduledReactivationAtUtc <= now.AddHours(1)
                && u.ReactivationNoticeSentAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var user in upcoming)
        {
            await db.ExecuteInTransactionAsync(async token =>
            {
                user.ReactivationNoticeSentAtUtc = now;
                var local = businessTime.ToLocal(user.ScheduledReactivationAtUtc!.Value);
                await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                    Category: "users",
                    Title: "Scheduled reactivation in one hour",
                    SafePreview: $"{user.DisplayName} reactivates at {local:h:mm tt} Central.",
                    ReferenceType: "User",
                    ReferenceId: user.Id.ToString()), null, token);
                await db.SaveChangesAsync(token);
            }, cancellationToken);
        }

        // Due now: reactivate, audit, notify user + management.
        var due = await db.Set<ApplicationUser>()
            .Where(u => !u.IsActive
                && u.ScheduledReactivationAtUtc != null
                && u.ScheduledReactivationAtUtc <= now)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var userId in due)
        {
            await db.ExecuteInTransactionAsync(async token =>
            {
                await identity.ReactivateAsync(userId, token);
                var details = await identity.GetUserDetailsAsync(userId, token);
                await audit.WriteAsync(new AuditEntry(
                    "users", "users.scheduledReactivationExecuted", AuditRetentionClass.AccountLifetime)
                {
                    TargetType = "User",
                    TargetId = userId.ToString(),
                }, token);
                _ = await notifications.CreateAsync(userId, new NotificationService.NewNotification(
                    Category: "account",
                    Title: "Your account is active again",
                    SafePreview: "Scheduled reactivation completed. You can sign in now."), token);
                await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                    Category: "users",
                    Title: "Account reactivated on schedule",
                    SafePreview: $"{details?.DisplayName ?? "An account"} is active again.",
                    ReferenceType: "User",
                    ReferenceId: userId.ToString()), null, token);
                await db.SaveChangesAsync(token);
            }, cancellationToken);
            logger.LogInformation("Scheduled reactivation executed for user {UserId}", userId);
        }
    }
}
