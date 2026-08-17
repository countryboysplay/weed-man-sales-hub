using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Presence;

/// <summary>
/// Manual presence status, the live directory, and the personal day timeline
/// (CLAUDE.md §12). Manual status is user-set and persistent — DND stays until
/// the user changes it. Away/Offline are always server-derived from session
/// activity and IdleDetector transitions; the client never asserts them.
/// </summary>
public sealed class PresenceService(
    IAppDb db,
    IIdentityService identity,
    IAuditWriter audit,
    IOutboxWriter outbox,
    NotificationService notifications,
    BusinessTime businessTime)
{
    public const int CustomMessageMaxLength = 35;

    /// <summary>How long after the last authenticated request a user still
    /// counts as connected. Requests slide LastSeenAtUtc on every call.</summary>
    public static readonly TimeSpan ConnectedWindow = TimeSpan.FromMinutes(5);

    public async Task<(bool Ok, string? Error)> SetManualStatusAsync(
        Guid userId, PresenceStatus status, string? customMessage, CancellationToken ct = default)
    {
        var message = string.IsNullOrWhiteSpace(customMessage) ? null : customMessage.Trim();
        if (message is { Length: > CustomMessageMaxLength })
        {
            return (false, $"The custom status message caps at {CustomMessageMaxLength} characters.");
        }

        var prior = await identity.GetPresenceAsync(userId, ct);
        if (prior is null)
        {
            return (false, "User not found.");
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            await identity.SetPresenceStatusAsync(userId, status, message, token);

            await audit.WriteAsync(new AuditEntry(
                "presence", "presence.statusChanged", AuditRetentionClass.Operational90Days)
            {
                ActorUserId = userId,
                TargetType = "User",
                TargetId = userId.ToString(),
                Before = new { status = prior.Status.ToString() },
                After = new { status = status.ToString(), hasCustomMessage = message is not null },
            }, token);

            await outbox.EnqueueAsync(EventTypes.PresenceStatusChanged, new
            {
                // Named subjectUserId on purpose: the dispatcher broadcasts this
                // event type to everyone; a userId field would narrow it.
                subjectUserId = userId,
                status = status.ToString(),
                customMessage = message,
            }, token);

            // Leaving DND delivers one catch-up summary: counts per category,
            // never content previews (CLAUDE.md §12).
            if (prior.Status == PresenceStatus.Dnd && status != PresenceStatus.Dnd)
            {
                await CreateDndCatchUpAsync(userId, prior.ChangedAtUtc, token);
            }

            await db.SaveChangesAsync(token);
        }, ct);

        return (true, null);
    }

    private async Task CreateDndCatchUpAsync(
        Guid userId, DateTimeOffset? dndSinceUtc, CancellationToken ct)
    {
        var since = dndSinceUtc ?? businessTime.UtcNow;
        var counts = await db.Notifications
            .Where(n => n.UserId == userId && n.CreatedAtUtc >= since)
            .GroupBy(n => n.Category)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct);
        var total = counts.Sum(c => c.Count);
        if (total == 0)
        {
            return;
        }

        var breakdown = string.Join(", ", counts.Select(c => $"{c.Count} {c.Category}"));
        _ = await notifications.CreateAsync(userId, new NotificationService.NewNotification(
            "presence",
            "While you were on Do Not Disturb",
            $"{total} notification{(total == 1 ? "" : "s")} arrived: {breakdown}."), ct);
    }

    // ── directory ─────────────────────────────────────────────────────────────

    public sealed record DirectoryEntry(
        Guid UserId,
        string DisplayName,
        string Role,
        string State,
        string? CustomMessage,
        DateTimeOffset? StatusChangedAtUtc);

    public async Task<IReadOnlyList<DirectoryEntry>> DirectoryAsync(CancellationToken ct = default)
    {
        var users = await identity.ListPresenceAsync(ct);
        var activity = await SessionActivityAsync(ct);
        var onBreak = await db.BreakSessions
            .Where(b => b.EndedAtUtc == null)
            .Select(b => b.UserId)
            .ToListAsync(ct);
        var breakSet = onBreak.ToHashSet();
        var now = businessTime.UtcNow;

        return users
            .Select(u => new DirectoryEntry(
                u.UserId, u.DisplayName, u.Role,
                DeriveState(u.Status, activity.GetValueOrDefault(u.UserId),
                    breakSet.Contains(u.UserId), now).ToString(),
                u.CustomStatusMessage,
                u.ChangedAtUtc))
            .ToList();
    }

    public async Task<PresenceSegmentState> DerivedStateAsync(
        Guid userId, CancellationToken ct = default)
    {
        var info = await identity.GetPresenceAsync(userId, ct)
            ?? throw new InvalidOperationException("User not found.");
        var activity = await SessionActivityAsync(ct);
        var onBreak = await db.BreakSessions
            .AnyAsync(b => b.UserId == userId && b.EndedAtUtc == null, ct);
        return DeriveState(info.Status, activity.GetValueOrDefault(userId), onBreak, businessTime.UtcNow);
    }

    public sealed record SessionActivity(
        DateTimeOffset LastSeenAtUtc, string? IdleUserState, string? IdleScreenState);

    /// <summary>Freshest active session per user; idle states ride along from
    /// that same session (the one the user is actually driving).</summary>
    public async Task<Dictionary<Guid, SessionActivity>> SessionActivityAsync(
        CancellationToken ct = default)
    {
        var horizon = businessTime.UtcNow - TimeSpan.FromHours(24);
        var sessions = await db.UserSessions
            .Where(s => s.RevokedAtUtc == null && s.LastSeenAtUtc > horizon)
            .Select(s => new { s.UserId, s.LastSeenAtUtc, s.LastIdleUserState, s.LastIdleScreenState })
            .ToListAsync(ct);
        return sessions
            .GroupBy(s => s.UserId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var freshest = g.OrderByDescending(s => s.LastSeenAtUtc).First();
                    return new SessionActivity(
                        freshest.LastSeenAtUtc, freshest.LastIdleUserState, freshest.LastIdleScreenState);
                });
    }

    /// <summary>
    /// Single source of truth for deriving the displayed state. Precedence:
    /// Offline (no connected session) → OnBreak → manual DND/Busy → Away
    /// (IdleDetector says idle or screen locked) → Available.
    /// </summary>
    public static PresenceSegmentState DeriveState(
        PresenceStatus manualStatus, SessionActivity? activity, bool onBreak, DateTimeOffset nowUtc)
    {
        if (activity is null || nowUtc - activity.LastSeenAtUtc > ConnectedWindow)
        {
            return PresenceSegmentState.Offline;
        }

        if (onBreak)
        {
            return PresenceSegmentState.OnBreak;
        }

        if (manualStatus == PresenceStatus.Dnd)
        {
            return PresenceSegmentState.Dnd;
        }

        if (manualStatus == PresenceStatus.Busy)
        {
            return PresenceSegmentState.Busy;
        }

        if (activity.IdleUserState == "idle" || activity.IdleScreenState == "locked")
        {
            return PresenceSegmentState.Away;
        }

        return PresenceSegmentState.Available;
    }

    // ── personal timeline ─────────────────────────────────────────────────────

    public sealed record MyDay(
        UserPresenceInfo Manual,
        PresenceSegmentState DerivedState,
        IReadOnlyList<PresenceSegment> Segments,
        IReadOnlyList<PresenceFlag> Flags);

    public async Task<MyDay?> MyDayAsync(Guid userId, CancellationToken ct = default)
    {
        var manual = await identity.GetPresenceAsync(userId, ct);
        if (manual is null)
        {
            return null;
        }

        var today = businessTime.Today;
        var dayStartUtc = businessTime.StartOfBusinessDateUtc(today);
        var segments = await db.PresenceSegments
            .Where(s => s.UserId == userId
                && (s.EndAtUtc == null || s.EndAtUtc > dayStartUtc))
            .OrderBy(s => s.StartAtUtc)
            .ToListAsync(ct);
        var flags = await db.PresenceFlags
            .Where(f => f.UserId == userId && f.BusinessDate == today)
            .OrderBy(f => f.StartAtUtc)
            .ToListAsync(ct);
        var derived = await DerivedStateAsync(userId, ct);

        return new MyDay(manual, derived, segments, flags);
    }
}
