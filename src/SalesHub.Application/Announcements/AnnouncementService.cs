using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Announcements;

/// <summary>
/// Announcements (CLAUDE.md §8): management-authored, targets expanded at
/// publish, Seen tracked separately from Acknowledged, up to three pins with
/// seven-day auto-unpin, completion excludes managers, 100% notifies
/// management with the exact Central time, reminders reach only outstanding
/// users.
/// </summary>
public sealed class AnnouncementService(
    IAppDb db,
    IIdentityService identity,
    NotificationService notifications,
    IAuditWriter audit,
    IOutboxWriter outbox,
    BusinessTime businessTime)
{
    public const int MaxPinned = 3;
    public static readonly TimeSpan PinLifetime = TimeSpan.FromDays(7);

    public enum Failure { None, NotFound, Validation, PinLimit }

    // ── authoring ────────────────────────────────────────────────────────────

    public sealed record DraftInput(
        Guid AuthorUserId,
        string Title,
        string Body,
        AnnouncementPriority Priority,
        bool RequireAcknowledgment,
        IReadOnlyList<Guid>? TargetUserIds,   // null/empty = all active users
        DateTimeOffset? ScheduledPublishAtUtc,
        DateTimeOffset? ViewByUtc,
        DateTimeOffset? AcknowledgeByUtc,
        int? ReminderEveryHours);

    public async Task<(Failure, string?, Announcement?)> CreateAsync(
        DraftInput input, bool publishNow, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            return (Failure.Validation, "An announcement needs a title.", null);
        }

        var announcement = new Announcement
        {
            Id = Guid.CreateVersion7(),
            AuthorUserId = input.AuthorUserId,
            Title = input.Title.Trim(),
            Body = input.Body?.Trim() ?? string.Empty,
            Priority = input.Priority,
            RequireAcknowledgment = input.RequireAcknowledgment,
            CreatedAtUtc = businessTime.UtcNow,
            ScheduledPublishAtUtc = input.ScheduledPublishAtUtc,
            ViewByUtc = input.ViewByUtc,
            AcknowledgeByUtc = input.AcknowledgeByUtc,
            ReminderEveryHours = input.ReminderEveryHours,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.Announcements.Add(announcement);
            await db.SaveChangesAsync(token);
            if (publishNow)
            {
                await PublishCoreAsync(announcement, input.TargetUserIds, token);
                await db.SaveChangesAsync(token);
            }
            else if (input.TargetUserIds is { Count: > 0 })
            {
                // Remember chosen targets for scheduled publish by storing
                // rows now; they re-expand against active users at publish.
                db.AnnouncementTargets.AddRange(input.TargetUserIds.Distinct().Select(id =>
                    new AnnouncementTarget { AnnouncementId = announcement.Id, UserId = id }));
                await db.SaveChangesAsync(token);
            }
        }, ct);

        return (Failure.None, null, announcement);
    }

    public async Task<Failure> PublishAsync(Guid announcementId, CancellationToken ct = default)
    {
        var announcement = await db.Announcements.FirstOrDefaultAsync(
            a => a.Id == announcementId && a.ArchivedAtUtc == null, ct);
        if (announcement is null)
        {
            return Failure.NotFound;
        }

        if (announcement.PublishedAtUtc is not null)
        {
            return Failure.None; // idempotent
        }

        var preselected = await db.AnnouncementTargets
            .Where(t => t.AnnouncementId == announcementId)
            .Select(t => t.UserId)
            .ToListAsync(ct);

        await db.ExecuteInTransactionAsync(async token =>
        {
            if (preselected.Count > 0)
            {
                await db.AnnouncementTargets
                    .Where(t => t.AnnouncementId == announcementId)
                    .ExecuteDeleteAsync(token);
            }

            await PublishCoreAsync(announcement, preselected.Count > 0 ? preselected : null, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return Failure.None;
    }

    private async Task PublishCoreAsync(
        Announcement announcement, IReadOnlyList<Guid>? targetUserIds, CancellationToken ct)
    {
        var everyone = await identity.ListUsersAsync(new UserQuery(), ct);
        var chosen = targetUserIds is { Count: > 0 }
            ? everyone.Where(u => targetUserIds.Contains(u.Id)).ToList()
            : everyone.ToList();

        announcement.PublishedAtUtc = businessTime.UtcNow;
        announcement.ScheduledPublishAtUtc = null;

        foreach (var user in chosen)
        {
            db.AnnouncementTargets.Add(new AnnouncementTarget
            {
                AnnouncementId = announcement.Id,
                UserId = user.Id,
                CountsTowardCompletion = !Roles.IsManagement(user.Role),
            });

            _ = await notifications.CreateAsync(user.Id, new NotificationService.NewNotification(
                "announcements",
                announcement.Priority == AnnouncementPriority.High
                    ? $"High priority: {announcement.Title}"
                    : announcement.Title,
                announcement.RequireAcknowledgment
                    ? "Acknowledgment required."
                    : "New announcement.",
                Required: announcement.RequireAcknowledgment,
                ReferenceType: "Announcement",
                ReferenceId: announcement.Id.ToString()), ct);
        }

        await audit.WriteAsync(new AuditEntry(
            "announcements", "announcements.published", AuditRetentionClass.Operational365Days)
        {
            ActorUserId = announcement.AuthorUserId,
            TargetType = "Announcement",
            TargetId = announcement.Id.ToString(),
            After = new { announcement.Title, targets = chosen.Count },
        }, ct);

        await outbox.EnqueueAsync(EventTypes.AnnouncementPublished, new
        {
            announcementId = announcement.Id,
            title = announcement.Title,
            priority = announcement.Priority.ToString(),
            requireAcknowledgment = announcement.RequireAcknowledgment,
        }, ct);
    }

    // ── pinning ──────────────────────────────────────────────────────────────

    public async Task<Failure> PinAsync(Guid announcementId, bool pin, CancellationToken ct = default)
    {
        var announcement = await db.Announcements.FirstOrDefaultAsync(
            a => a.Id == announcementId && a.ArchivedAtUtc == null, ct);
        if (announcement is null)
        {
            return Failure.NotFound;
        }

        if (!pin)
        {
            announcement.PinRank = null;
            announcement.AutoUnpinAtUtc = null;
            await db.SaveChangesAsync(ct);
            return Failure.None;
        }

        var pinned = await db.Announcements
            .Where(a => a.PinRank != null && a.Id != announcementId)
            .CountAsync(ct);
        if (pinned >= MaxPinned)
        {
            return Failure.PinLimit; // hard cap of three (CLAUDE.md §8)
        }

        announcement.PinRank = pinned + 1;
        announcement.AutoUnpinAtUtc = businessTime.UtcNow + PinLifetime;
        await db.SaveChangesAsync(ct);
        return Failure.None;
    }

    // ── seen / acknowledged ──────────────────────────────────────────────────

    public async Task<Failure> MarkAsync(
        Guid announcementId, Guid userId, bool acknowledge, CancellationToken ct = default)
    {
        var target = await db.AnnouncementTargets.FirstOrDefaultAsync(
            t => t.AnnouncementId == announcementId && t.UserId == userId, ct);
        if (target is null)
        {
            return Failure.NotFound;
        }

        var now = businessTime.UtcNow;
        target.SeenAtUtc ??= now;
        if (acknowledge)
        {
            target.AcknowledgedAtUtc ??= now;
        }

        await db.SaveChangesAsync(ct);
        await CheckCompletionAsync(announcementId, ct);
        return Failure.None;
    }

    /// <summary>At 100% completion (nonmanagement targets meeting the seen/ack
    /// requirement) notify management once, with the exact Central time.</summary>
    private async Task CheckCompletionAsync(Guid announcementId, CancellationToken ct)
    {
        var announcement = await db.Announcements.FirstAsync(a => a.Id == announcementId, ct);
        if (announcement.CompletionNotified)
        {
            return;
        }

        var counted = await db.AnnouncementTargets
            .Where(t => t.AnnouncementId == announcementId && t.CountsTowardCompletion)
            .ToListAsync(ct);
        if (counted.Count == 0)
        {
            return;
        }

        var complete = announcement.RequireAcknowledgment
            ? counted.All(t => t.AcknowledgedAtUtc is not null)
            : counted.All(t => t.SeenAtUtc is not null);
        if (!complete)
        {
            return;
        }

        announcement.CompletionNotified = true;
        var centralTime = businessTime.ToLocal(businessTime.UtcNow);
        await db.ExecuteInTransactionAsync(async token =>
        {
            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "announcements",
                "Announcement fully acknowledged",
                $"\"{announcement.Title}\" reached 100% at {centralTime:h:mm tt} Central on {centralTime:MMMM d}.",
                ReferenceType: "Announcement",
                ReferenceId: announcement.Id.ToString()), null, token);
            await outbox.EnqueueAsync(EventTypes.AnnouncementProgressChanged, new
            {
                announcementId,
                percent = 100,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
    }

    // ── progress + reminders ─────────────────────────────────────────────────

    public sealed record Progress(
        int TargetCount, int CountedTargets, int Seen, int Acknowledged, int Percent,
        IReadOnlyList<Guid> Outstanding);

    public async Task<Progress?> ProgressAsync(Guid announcementId, CancellationToken ct = default)
    {
        var announcement = await db.Announcements
            .FirstOrDefaultAsync(a => a.Id == announcementId, ct);
        if (announcement is null)
        {
            return null;
        }

        var targets = await db.AnnouncementTargets
            .Where(t => t.AnnouncementId == announcementId)
            .ToListAsync(ct);
        var counted = targets.Where(t => t.CountsTowardCompletion).ToList();
        var done = counted.Count(t => announcement.RequireAcknowledgment
            ? t.AcknowledgedAtUtc is not null
            : t.SeenAtUtc is not null);
        var outstanding = counted
            .Where(t => announcement.RequireAcknowledgment
                ? t.AcknowledgedAtUtc is null
                : t.SeenAtUtc is null)
            .Select(t => t.UserId)
            .ToList();

        return new Progress(
            targets.Count,
            counted.Count,
            targets.Count(t => t.SeenAtUtc is not null),
            targets.Count(t => t.AcknowledgedAtUtc is not null),
            counted.Count == 0 ? 100 : (int)Math.Round(done * 100.0 / counted.Count),
            outstanding);
    }

    /// <summary>Reminder push targets only outstanding users (CLAUDE.md §8).</summary>
    public async Task<int> RemindOutstandingAsync(Guid announcementId, CancellationToken ct = default)
    {
        var announcement = await db.Announcements.FirstOrDefaultAsync(
            a => a.Id == announcementId && a.PublishedAtUtc != null && a.ArchivedAtUtc == null, ct);
        if (announcement is null)
        {
            return 0;
        }

        var progress = await ProgressAsync(announcementId, ct);
        if (progress is null || progress.Outstanding.Count == 0)
        {
            return 0;
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            foreach (var userId in progress.Outstanding)
            {
                _ = await notifications.CreateAsync(userId, new NotificationService.NewNotification(
                    "announcements",
                    $"Reminder: {announcement.Title}",
                    announcement.RequireAcknowledgment
                        ? "This announcement still needs your acknowledgment."
                        : "This announcement is still waiting for you.",
                    Required: announcement.RequireAcknowledgment,
                    ReferenceType: "Announcement",
                    ReferenceId: announcement.Id.ToString()), token);
            }

            announcement.LastReminderAtUtc = businessTime.UtcNow;
            await db.SaveChangesAsync(token);
        }, ct);

        return progress.Outstanding.Count;
    }

    // ── jobs ─────────────────────────────────────────────────────────────────

    public async Task<int> PublishDueScheduledAsync(CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        var due = await db.Announcements
            .Where(a => a.PublishedAtUtc == null
                && a.ArchivedAtUtc == null
                && a.ScheduledPublishAtUtc != null
                && a.ScheduledPublishAtUtc <= now)
            .Select(a => a.Id)
            .ToListAsync(ct);
        foreach (var id in due)
        {
            _ = await PublishAsync(id, ct);
        }

        return due.Count;
    }

    public async Task<int> AutoUnpinExpiredAsync(CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        // The announcement stays active — only the pin releases (CLAUDE.md §8).
        return await db.Announcements
            .Where(a => a.PinRank != null && a.AutoUnpinAtUtc != null && a.AutoUnpinAtUtc <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.PinRank, (int?)null)
                .SetProperty(a => a.AutoUnpinAtUtc, (DateTimeOffset?)null), ct);
    }

    public async Task<int> SendDueRemindersAsync(CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        var candidates = await db.Announcements
            .Where(a => a.PublishedAtUtc != null
                && a.ArchivedAtUtc == null
                && a.ReminderEveryHours != null
                && !a.CompletionNotified)
            .ToListAsync(ct);

        var reminded = 0;
        foreach (var announcement in candidates)
        {
            var anchor = announcement.LastReminderAtUtc ?? announcement.PublishedAtUtc!.Value;
            if (now - anchor >= TimeSpan.FromHours(announcement.ReminderEveryHours!.Value))
            {
                reminded += await RemindOutstandingAsync(announcement.Id, ct);
            }
        }

        return reminded;
    }
}
