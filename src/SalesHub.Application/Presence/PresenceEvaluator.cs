using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Presence;

/// <summary>
/// The minute-cadence presence evaluation pass (CLAUDE.md §12, docs/05) for
/// monitored roles. Writes normalized presence segments, raises PRS flags
/// (LateStart / Disappeared / BreakOverrun) against the role's rule set, and
/// suppresses monitoring during approved time off, suspending schedule
/// exceptions, and explicit technical grace grants — a technical report alone
/// never pauses monitoring. Everything is derived server-side from session
/// activity and coarse IdleDetector transitions; runs are idempotent (one
/// flag per user/category/business date, enforced by a unique index).
/// </summary>
public sealed class PresenceEvaluator(
    IAppDb db,
    IIdentityService identity,
    IPublicIdGenerator publicIds,
    IAuditWriter audit,
    IOutboxWriter outbox,
    NotificationService notifications,
    PresenceService presence,
    BusinessTime businessTime,
    IOptions<SecurityOptions> securityOptions)
{
    private readonly SecurityOptions _security = securityOptions.Value;

    public async Task<int> EvaluateAsync(CancellationToken ct = default)
    {
        var monitored = (await identity.ListPresenceAsync(ct))
            .Where(u => _security.MonitoredRoles.Contains(u.Role, StringComparer.Ordinal))
            .ToList();
        if (monitored.Count == 0)
        {
            return 0;
        }

        var now = businessTime.UtcNow;
        var today = businessTime.Today;
        var localDayOfWeek = businessTime.ToLocal(now).DayOfWeek;
        var userIds = monitored.Select(u => u.UserId).ToList();

        var activity = await presence.SessionActivityAsync(ct);

        var openBreaks = await db.BreakSessions
            .Where(b => b.EndedAtUtc == null && userIds.Contains(b.UserId))
            .Join(db.BreakTypes, b => b.BreakTypeId, t => t.Id,
                (b, t) => new { Session = b, t.LimitMinutes })
            .ToDictionaryAsync(x => x.Session.UserId, ct);

        var exceptions = (await db.ScheduleExceptions
                .Where(e => e.Date == today && userIds.Contains(e.UserId))
                .ToListAsync(ct))
            .ToLookup(e => e.UserId);

        var timeOff = (await db.TimeOffRequests
                .Where(t => t.Status == TimeOffStatus.Approved
                    && t.StartDate <= today && t.EndDate >= today
                    && userIds.Contains(t.UserId))
                .ToListAsync(ct))
            .ToLookup(t => t.UserId);

        var grants = (await db.TechnicalGrants
                .Where(g => g.StartAtUtc <= now && g.EndAtUtc > now && userIds.Contains(g.UserId))
                .ToListAsync(ct))
            .ToLookup(g => g.UserId);

        var ruleSets = await db.PresenceRuleSets.ToDictionaryAsync(r => r.Role, ct);

        var shiftWindows = (await db.UserShiftAssignments
                .Where(a => userIds.Contains(a.UserId)
                    && a.StartDate <= today
                    && (a.EndDate == null || a.EndDate >= today))
                .Join(db.ShiftTemplates.Where(t => t.Active && t.DayOfWeek == localDayOfWeek),
                    a => a.ShiftTemplateId, t => t.Id,
                    (a, t) => new UserShiftWindow(a.UserId, t.StartLocalTime, t.EndLocalTime))
                .ToListAsync(ct))
            .ToLookup(x => x.UserId);

        var openSegments = (await db.PresenceSegments
                .Where(s => s.EndAtUtc == null && userIds.Contains(s.UserId))
                .ToListAsync(ct))
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.StartAtUtc).First());

        var todayFlags = (await db.PresenceFlags
                .Where(f => f.BusinessDate == today && userIds.Contains(f.UserId))
                .ToListAsync(ct))
            .ToDictionary(f => (f.UserId, f.Category));

        var raised = 0;

        await db.ExecuteInTransactionAsync(async token =>
        {
            foreach (var user in monitored)
            {
                var act = activity.GetValueOrDefault(user.UserId);
                var userBreak = openBreaks.GetValueOrDefault(user.UserId);
                var rules = ruleSets.GetValueOrDefault(user.Role) ?? new PresenceRuleSet();

                var suppression = Suppression(
                    user.UserId, today, now, exceptions, timeOff, grants);

                var state = PresenceService.DeriveState(
                    user.Status, act, userBreak is not null, now);

                // Suppression only recolors what would otherwise look like a
                // violation; a user actively working during a grace window
                // still shows as working.
                if (suppression is { } covered
                    && state is PresenceSegmentState.Away or PresenceSegmentState.Offline)
                {
                    state = covered;
                }

                var segment = UpkeepSegment(openSegments, user.UserId, state, now);

                if (suppression is not null)
                {
                    continue; // no flags while covered
                }

                var window = ShiftWindowUtc(user.UserId, today, shiftWindows, exceptions);
                var inShift = window is { } w && now >= w.StartUtc && now < w.EndUtc;
                var hasActivityThisShift = window is { } win
                    && act is not null && act.LastSeenAtUtc >= win.StartUtc;

                // LateStart: the shift began, grace passed, and the user has
                // not been seen since it started.
                if (inShift && !hasActivityThisShift
                    && now > window!.Value.StartUtc + TimeSpan.FromMinutes(rules.LateStartGraceMinutes))
                {
                    raised += await EnsureFlagAsync(todayFlags, user, "LateStart",
                        PresenceFlagSeverity.Warning, window.Value.StartUtc, today, "shift-start", token);
                }
                else if (hasActivityThisShift
                    && todayFlags.TryGetValue((user.UserId, "LateStart"), out var late)
                    && late.EndAtUtc is null)
                {
                    late.EndAtUtc = now; // arrived — close the window, keep the record
                }

                // Disappeared: seen this shift, then went dark (offline) or
                // idle/locked (away) past the grace threshold.
                var gapStart = state switch
                {
                    PresenceSegmentState.Offline when act is not null => act.LastSeenAtUtc,
                    PresenceSegmentState.Offline => segment.StartAtUtc,
                    PresenceSegmentState.Away => segment.StartAtUtc,
                    _ => (DateTimeOffset?)null,
                };
                if (inShift && hasActivityThisShift && gapStart is { } gone)
                {
                    var gap = now - gone;
                    if (gap > TimeSpan.FromMinutes(rules.OfflineGraceMinutes))
                    {
                        var severity = gap > TimeSpan.FromMinutes(rules.SeriousOfflineMinutes)
                            ? PresenceFlagSeverity.Serious
                            : PresenceFlagSeverity.Warning;
                        raised += await EnsureFlagAsync(todayFlags, user, "Disappeared",
                            severity, gone, today, "presence-evaluator", token);
                    }
                }
                else if (gapStart is null
                    && todayFlags.TryGetValue((user.UserId, "Disappeared"), out var disappeared)
                    && disappeared.EndAtUtc is null)
                {
                    disappeared.EndAtUtc = now;
                    disappeared.Status = PresenceFlagStatus.Resolved; // they came back
                }

                // BreakOverrun: the active break ran past its limit plus grace.
                if (userBreak is not null)
                {
                    var limitEnd = userBreak.Session.StartedAtUtc
                        + TimeSpan.FromMinutes(userBreak.LimitMinutes);
                    if (now > limitEnd + TimeSpan.FromMinutes(rules.BreakOverrunGraceMinutes))
                    {
                        userBreak.Session.OverrunFlagged = true;
                        raised += await EnsureFlagAsync(todayFlags, user, "BreakOverrun",
                            PresenceFlagSeverity.Warning, limitEnd, today, "break-monitor", token);
                    }
                }
                else if (todayFlags.TryGetValue((user.UserId, "BreakOverrun"), out var overrun)
                    && overrun.EndAtUtc is null)
                {
                    overrun.EndAtUtc = now; // break ended; management still reviews
                }
            }

            await db.SaveChangesAsync(token);
        }, ct);

        return raised;
    }

    /// <summary>The covering suppression state for "now", if any: an explicit
    /// technical grace grant, approved time off, or a suspending schedule
    /// exception (whole day, or its replacement window when one is set).</summary>
    private PresenceSegmentState? Suppression(
        Guid userId, DateOnly today, DateTimeOffset now,
        ILookup<Guid, ScheduleException> exceptions,
        ILookup<Guid, TimeOffRequest> timeOff,
        ILookup<Guid, TechnicalGrant> grants)
    {
        if (grants[userId].Any())
        {
            return PresenceSegmentState.TechnicalGrace;
        }

        foreach (var request in timeOff[userId])
        {
            if (request.FullDay)
            {
                return PresenceSegmentState.ApprovedException;
            }

            if (request is { StartLocalTime: { } start, EndLocalTime: { } end }
                && CoversNow(today, start, end, now))
            {
                return PresenceSegmentState.ApprovedException;
            }
        }

        foreach (var exception in exceptions[userId].Where(e => e.SuspendsPresence))
        {
            if (exception.ReplacementStartLocal is null || exception.ReplacementEndLocal is null)
            {
                return PresenceSegmentState.ApprovedException;
            }

            if (CoversNow(today, exception.ReplacementStartLocal.Value,
                exception.ReplacementEndLocal.Value, now))
            {
                return PresenceSegmentState.ApprovedException;
            }
        }

        return null;
    }

    private bool CoversNow(DateOnly date, TimeOnly start, TimeOnly end, DateTimeOffset now) =>
        businessTime.ToUtc(date.ToDateTime(start)) <= now
            && now < businessTime.ToUtc(date.ToDateTime(end));

    /// <summary>Today's expected working window in UTC: the union of assigned
    /// templates for this weekday, overridden by a non-suspending schedule
    /// exception's replacement window. Null when nothing is scheduled.</summary>
    private sealed record UserShiftWindow(Guid UserId, TimeOnly StartLocalTime, TimeOnly EndLocalTime);

    private (DateTimeOffset StartUtc, DateTimeOffset EndUtc)? ShiftWindowUtc(
        Guid userId, DateOnly today,
        ILookup<Guid, UserShiftWindow> shiftWindows,
        ILookup<Guid, ScheduleException> exceptions)
    {
        var replacement = exceptions[userId].FirstOrDefault(e =>
            !e.SuspendsPresence
            && e.ReplacementStartLocal is not null
            && e.ReplacementEndLocal is not null);
        if (replacement is not null)
        {
            return (businessTime.ToUtc(today.ToDateTime(replacement.ReplacementStartLocal!.Value)),
                businessTime.ToUtc(today.ToDateTime(replacement.ReplacementEndLocal!.Value)));
        }

        var windows = shiftWindows[userId].ToList();
        if (windows.Count == 0)
        {
            return null;
        }

        var start = windows.Min(w => w.StartLocalTime);
        var end = windows.Max(w => w.EndLocalTime);
        return (businessTime.ToUtc(today.ToDateTime(start)),
            businessTime.ToUtc(today.ToDateTime(end)));
    }

    private PresenceSegment UpkeepSegment(
        Dictionary<Guid, PresenceSegment> openSegments,
        Guid userId, PresenceSegmentState state, DateTimeOffset now)
    {
        var open = openSegments.GetValueOrDefault(userId);
        if (open is not null && open.State == state)
        {
            return open;
        }

        if (open is not null)
        {
            open.EndAtUtc = now;
        }

        var segment = new PresenceSegment
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            State = state,
            StartAtUtc = now,
        };
        db.PresenceSegments.Add(segment);
        openSegments[userId] = segment;
        return segment;
    }

    /// <summary>Creates or escalates the day's flag for (user, category).
    /// Returns 1 when something changed. A first Serious observation — or an
    /// escalation to Serious — pushes a management notification.</summary>
    private async Task<int> EnsureFlagAsync(
        Dictionary<(Guid UserId, string Category), PresenceFlag> todayFlags,
        UserPresenceInfo user, string category, PresenceFlagSeverity severity,
        DateTimeOffset startAt, DateOnly businessDate, string source, CancellationToken ct)
    {
        if (todayFlags.TryGetValue((user.UserId, category), out var existing))
        {
            if (existing.Status != PresenceFlagStatus.Open || severity <= existing.Severity)
            {
                return 0;
            }

            existing.Severity = severity;
            existing.EndAtUtc = null; // condition is live again
            await audit.WriteAsync(new AuditEntry(
                "presence", "presence.flagEscalated", AuditRetentionClass.Operational365Days)
            {
                TargetType = "PresenceFlag",
                TargetId = existing.Id.ToString(),
                PublicRecordId = existing.PublicId,
                After = new { severity = severity.ToString() },
            }, ct);
            if (severity == PresenceFlagSeverity.Serious)
            {
                await NotifyManagementAsync(user, category, existing.PublicId, ct);
            }

            return 1;
        }

        var flag = new PresenceFlag
        {
            Id = Guid.CreateVersion7(),
            PublicId = await publicIds.NextAsync("PRS", ct),
            UserId = user.UserId,
            Category = category,
            Severity = severity,
            StartAtUtc = startAt,
            Source = source,
            Status = PresenceFlagStatus.Open,
            BusinessDate = businessDate,
        };
        db.PresenceFlags.Add(flag);
        todayFlags[(user.UserId, category)] = flag;

        await audit.WriteAsync(new AuditEntry(
            "presence", "presence.flagRaised", AuditRetentionClass.Operational365Days)
        {
            TargetType = "PresenceFlag",
            TargetId = flag.Id.ToString(),
            PublicRecordId = flag.PublicId,
            After = new { user.UserId, category, severity = severity.ToString() },
        }, ct);

        // subjectUserId (not userId) so the dispatcher routes this to the
        // management group, not to the flagged agent's own device.
        await outbox.EnqueueAsync(EventTypes.PresenceFlagRaised, new
        {
            subjectUserId = user.UserId,
            publicId = flag.PublicId,
            category,
            severity = severity.ToString(),
            businessDate = businessDate.ToString("O"),
        }, ct);

        if (severity == PresenceFlagSeverity.Serious)
        {
            await NotifyManagementAsync(user, category, flag.PublicId, ct);
        }

        return 1;
    }

    private async Task NotifyManagementAsync(
        UserPresenceInfo user, string category, string publicId, CancellationToken ct) =>
        await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
            "presence",
            "Serious presence alert",
            $"{user.DisplayName}: {category} ({publicId}).",
            ReferenceType: "PresenceFlag",
            ReferenceId: publicId), excludeUserId: null, ct);
}
