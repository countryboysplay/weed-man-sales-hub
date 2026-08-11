using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Tasks;

/// <summary>
/// Tasks (CLAUDE.md §9): management creates and assigns (one, several, or
/// everyone); each assignee owns an independent instance; completion clears
/// it from their active list while history stays management-visible;
/// recurrence mints new instances per period; overdue reminders are
/// per-definition and at most daily per instance.
/// </summary>
public sealed class TaskService(
    IAppDb db,
    IIdentityService identity,
    NotificationService notifications,
    IOutboxWriter outbox,
    BusinessTime businessTime)
{
    public sealed record CreateInput(
        Guid ActorUserId,
        string Title,
        string Description,
        TaskPriority Priority,
        DateTimeOffset? DueAtUtc,
        TaskRecurrence Recurrence,
        bool OverdueReminders,
        bool AssignToEveryone,
        IReadOnlyList<Guid> AssigneeUserIds);

    public async Task<(TaskDefinition? Definition, string? Error)> CreateAsync(
        CreateInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            return (null, "A task needs a title.");
        }

        var assignees = input.AssignToEveryone
            ? (await identity.ListUsersAsync(new UserQuery(), ct)).Select(u => u.Id).ToList()
            : input.AssigneeUserIds.Distinct().ToList();
        if (assignees.Count == 0)
        {
            return (null, "A task needs at least one assignee.");
        }

        var now = businessTime.UtcNow;
        var definition = new TaskDefinition
        {
            Id = Guid.CreateVersion7(),
            Title = input.Title.Trim(),
            Description = input.Description?.Trim() ?? "",
            Priority = input.Priority,
            DueAtUtc = input.DueAtUtc,
            Recurrence = input.Recurrence,
            OverdueReminders = input.OverdueReminders,
            CreatedByUserId = input.ActorUserId,
            CreatedAtUtc = now,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.TaskDefinitions.Add(definition);
            var period = PeriodKeyFor(definition.Recurrence, businessTime.Today);
            foreach (var assignee in assignees)
            {
                db.TaskInstances.Add(new TaskInstance
                {
                    Id = Guid.CreateVersion7(),
                    DefinitionId = definition.Id,
                    AssigneeUserId = assignee,
                    DueAtUtc = definition.DueAtUtc,
                    CreatedAtUtc = now,
                    PeriodKey = period,
                });
                _ = await notifications.CreateAsync(assignee, new NotificationService.NewNotification(
                    "tasks",
                    definition.Priority == TaskPriority.High
                        ? $"High priority task: {definition.Title}"
                        : $"New task: {definition.Title}",
                    definition.DueAtUtc is { } due
                        ? $"Due {businessTime.ToLocal(due):MMM d, h:mm tt} Central."
                        : "No due date.",
                    ReferenceType: "TaskDefinition",
                    ReferenceId: definition.Id.ToString()), token);
                await outbox.EnqueueAsync(EventTypes.TaskAssigned, new
                {
                    userId = assignee,
                    definitionId = definition.Id,
                    title = definition.Title,
                }, token);
            }

            await db.SaveChangesAsync(token);
        }, ct);

        return (definition, null);
    }

    public async Task<bool> CompleteAsync(
        Guid instanceId, Guid actorUserId, CancellationToken ct = default)
    {
        var instance = await db.TaskInstances.FirstOrDefaultAsync(
            t => t.Id == instanceId && t.AssigneeUserId == actorUserId
                && t.Status == WorkTaskStatus.Active, ct);
        if (instance is null)
        {
            return false;
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            instance.Status = WorkTaskStatus.Completed;
            instance.CompletedAtUtc = businessTime.UtcNow;
            await outbox.EnqueueAsync(EventTypes.TaskCompleted, new
            {
                userId = actorUserId,
                instanceId,
                definitionId = instance.DefinitionId,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return true;
    }

    public async Task<(bool Ok, string? Error)> CommentAsync(
        Guid instanceId, Guid authorUserId, bool authorIsManagement, string body,
        IReadOnlyList<Guid> mentionedUserIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (false, "A comment needs text.");
        }

        var instance = await db.TaskInstances.FirstOrDefaultAsync(t => t.Id == instanceId, ct);
        if (instance is null || (!authorIsManagement && instance.AssigneeUserId != authorUserId))
        {
            return (false, "Task not found.");
        }

        var definition = await db.TaskDefinitions.FirstAsync(d => d.Id == instance.DefinitionId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            db.TaskComments.Add(new TaskComment
            {
                Id = Guid.CreateVersion7(),
                InstanceId = instanceId,
                AuthorUserId = authorUserId,
                Body = body.Trim(),
                CreatedAtUtc = businessTime.UtcNow,
            });

            // @mentions notify (CLAUDE.md §9); the assignee also hears about
            // management comments on their instance.
            var targets = mentionedUserIds.Distinct().Where(id => id != authorUserId).ToHashSet();
            if (authorUserId != instance.AssigneeUserId)
            {
                targets.Add(instance.AssigneeUserId);
            }

            foreach (var target in targets)
            {
                _ = await notifications.CreateAsync(target, new NotificationService.NewNotification(
                    "tasks",
                    $"Comment on: {definition.Title}",
                    body.Length <= 80 ? body : body[..77] + "…",
                    ReferenceType: "TaskInstance",
                    ReferenceId: instanceId.ToString()), token);
            }

            await db.SaveChangesAsync(token);
        }, ct);
        return (true, null);
    }

    // ── jobs ─────────────────────────────────────────────────────────────────

    /// <summary>Mints the current period's instances for recurring
    /// definitions; the unique (definition, assignee, period) index makes
    /// re-runs harmless. Assignees derive from the previous period.</summary>
    public async Task<int> GenerateRecurringAsync(CancellationToken ct = default)
    {
        var today = businessTime.Today;
        var created = 0;
        var definitions = await db.TaskDefinitions
            .Where(d => d.Active && d.Recurrence != TaskRecurrence.None)
            .ToListAsync(ct);

        foreach (var definition in definitions)
        {
            var period = PeriodKeyFor(definition.Recurrence, today);
            var assignees = await db.TaskInstances
                .Where(t => t.DefinitionId == definition.Id)
                .Select(t => t.AssigneeUserId)
                .Distinct()
                .ToListAsync(ct);
            var already = await db.TaskInstances
                .Where(t => t.DefinitionId == definition.Id && t.PeriodKey == period)
                .Select(t => t.AssigneeUserId)
                .ToListAsync(ct);

            foreach (var assignee in assignees.Except(already))
            {
                db.TaskInstances.Add(new TaskInstance
                {
                    Id = Guid.CreateVersion7(),
                    DefinitionId = definition.Id,
                    AssigneeUserId = assignee,
                    CreatedAtUtc = businessTime.UtcNow,
                    PeriodKey = period,
                });
                created++;
            }
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return created;
    }

    /// <summary>Overdue reminders: enabled per definition, at most one per
    /// instance per day, only while the instance is still active.</summary>
    public async Task<int> SendOverdueRemindersAsync(CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        var overdue = await db.TaskInstances
            .Join(db.TaskDefinitions.Where(d => d.OverdueReminders),
                t => t.DefinitionId, d => d.Id, (t, d) => new { t, d })
            .Where(x => x.t.Status == WorkTaskStatus.Active
                && x.t.DueAtUtc != null && x.t.DueAtUtc < now
                && (x.t.LastOverdueReminderAtUtc == null
                    || x.t.LastOverdueReminderAtUtc < now.AddHours(-24)))
            .ToListAsync(ct);

        foreach (var item in overdue)
        {
            await db.ExecuteInTransactionAsync(async token =>
            {
                item.t.LastOverdueReminderAtUtc = now;
                _ = await notifications.CreateAsync(item.t.AssigneeUserId,
                    new NotificationService.NewNotification(
                        "tasks",
                        $"Overdue: {item.d.Title}",
                        $"This task was due {businessTime.ToLocal(item.t.DueAtUtc!.Value):MMM d, h:mm tt} Central.",
                        ReferenceType: "TaskInstance",
                        ReferenceId: item.t.Id.ToString()), token);
                await db.SaveChangesAsync(token);
            }, ct);
        }

        return overdue.Count;
    }

    internal static string PeriodKeyFor(TaskRecurrence recurrence, DateOnly today) =>
        recurrence switch
        {
            TaskRecurrence.Daily => today.ToString("yyyy-MM-dd"),
            TaskRecurrence.Weekly =>
                $"{today.Year}-W{System.Globalization.ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue)):D2}",
            TaskRecurrence.Monthly => today.ToString("yyyy-MM"),
            _ => string.Empty,
        };
}
