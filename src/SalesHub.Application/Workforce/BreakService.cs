using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Workforce;

/// <summary>
/// Breaks (CLAUDE.md §12): one active break per user (also enforced by a
/// partial unique index), employee corrections only on the same business day
/// via BRK requests that preserve the original window, and management-only
/// edits after the day closes — always with a reason, always audited.
/// </summary>
public sealed class BreakService(
    IAppDb db,
    IPublicIdGenerator publicIds,
    IAuditWriter audit,
    IOutboxWriter outbox,
    NotificationService notifications,
    BusinessTime businessTime)
{
    public sealed record Result(BreakSession? Session, string? Error, string? Code = null)
    {
        public static Result Fail(string error, string? code = null) => new(null, error, code);
    }

    public async Task<Result> StartAsync(
        Guid userId, Guid breakTypeId, CancellationToken ct = default)
    {
        var type = await db.BreakTypes
            .FirstOrDefaultAsync(t => t.Id == breakTypeId && t.Active, ct);
        if (type is null)
        {
            return Result.Fail("Unknown break type.");
        }

        var active = await db.BreakSessions
            .AnyAsync(b => b.UserId == userId && b.EndedAtUtc == null, ct);
        if (active)
        {
            return Result.Fail("A break is already running — end it first.", "breakActive");
        }

        var now = businessTime.UtcNow;
        var session = new BreakSession
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            BreakTypeId = type.Id,
            StartedAtUtc = now,
            BusinessDate = businessTime.BusinessDateOf(now),
        };
        db.BreakSessions.Add(session);

        await audit.WriteAsync(new AuditEntry(
            "breaks", "breaks.started", AuditRetentionClass.Operational90Days)
        {
            ActorUserId = userId,
            TargetType = "BreakSession",
            TargetId = session.Id.ToString(),
            After = new { type = type.Label },
        }, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Raced our own pre-check; the partial unique index held the line.
            return Result.Fail("A break is already running — end it first.", "breakActive");
        }

        return new Result(session, null);
    }

    public async Task<Result> EndAsync(Guid userId, CancellationToken ct = default)
    {
        var session = await db.BreakSessions
            .FirstOrDefaultAsync(b => b.UserId == userId && b.EndedAtUtc == null, ct);
        if (session is null)
        {
            return Result.Fail("No break is running.", "noActiveBreak");
        }

        session.EndedAtUtc = businessTime.UtcNow;
        await audit.WriteAsync(new AuditEntry(
            "breaks", "breaks.ended", AuditRetentionClass.Operational90Days)
        {
            ActorUserId = userId,
            TargetType = "BreakSession",
            TargetId = session.Id.ToString(),
        }, ct);
        await db.SaveChangesAsync(ct);
        return new Result(session, null);
    }

    public sealed record CorrectionResult(BreakCorrectionRequest? Correction, string? Error)
    {
        public static CorrectionResult Fail(string error) => new(null, error);
    }

    /// <summary>Self-service correction: same business day only, and the
    /// original window rides along untouched for the reviewer.</summary>
    public async Task<CorrectionResult> RequestCorrectionAsync(
        Guid userId, Guid breakSessionId, DateTimeOffset correctedStart,
        DateTimeOffset correctedEnd, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return CorrectionResult.Fail("A correction needs a reason.");
        }

        if (correctedEnd <= correctedStart)
        {
            return CorrectionResult.Fail("The corrected end must be after the corrected start.");
        }

        var session = await db.BreakSessions
            .FirstOrDefaultAsync(b => b.Id == breakSessionId && b.UserId == userId, ct);
        if (session is null)
        {
            return CorrectionResult.Fail("Break not found.");
        }

        if (session.BusinessDate != businessTime.Today)
        {
            // After business midnight this becomes a management edit.
            return CorrectionResult.Fail(
                "Same-day corrections only — ask a manager to adjust a past day.");
        }

        var open = await db.BreakCorrectionRequests.AnyAsync(c =>
            c.BreakSessionId == session.Id
            && c.Status == BreakCorrectionStatus.Pending, ct);
        if (open)
        {
            return CorrectionResult.Fail("A correction is already pending for this break.");
        }

        BreakCorrectionRequest correction = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            correction = new BreakCorrectionRequest
            {
                Id = Guid.CreateVersion7(),
                PublicId = await publicIds.NextAsync("BRK", token),
                BreakSessionId = session.Id,
                RequestedByUserId = userId,
                OriginalStartAtUtc = session.StartedAtUtc,
                OriginalEndAtUtc = session.EndedAtUtc,
                CorrectedStartAtUtc = correctedStart,
                CorrectedEndAtUtc = correctedEnd,
                Reason = reason.Trim(),
                Status = BreakCorrectionStatus.Pending,
                CreatedAtUtc = businessTime.UtcNow,
            };
            db.BreakCorrectionRequests.Add(correction);

            await audit.WriteAsync(new AuditEntry(
                "breaks", "breaks.correctionRequested", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = userId,
                TargetType = "BreakCorrectionRequest",
                TargetId = correction.Id.ToString(),
                PublicRecordId = correction.PublicId,
            }, token);

            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "approvals",
                "Break correction request",
                $"{correction.PublicId}: same-day break time correction.",
                ReferenceType: "BreakCorrectionRequest",
                ReferenceId: correction.PublicId), excludeUserId: userId, ct: token);

            await outbox.EnqueueAsync(EventTypes.ApprovalsChanged,
                new { kind = "breakCorrection", publicId = correction.PublicId }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new CorrectionResult(correction, null);
    }

    public async Task<CorrectionResult> DecideCorrectionAsync(
        Guid correctionId, Guid actorUserId, bool approve, CancellationToken ct = default)
    {
        var correction = await db.BreakCorrectionRequests
            .FirstOrDefaultAsync(c => c.Id == correctionId
                && c.Status == BreakCorrectionStatus.Pending, ct);
        if (correction is null)
        {
            return CorrectionResult.Fail("Pending correction not found.");
        }

        var session = await db.BreakSessions
            .FirstAsync(b => b.Id == correction.BreakSessionId, ct);

        await db.ExecuteInTransactionAsync(async token =>
        {
            correction.Status = approve
                ? BreakCorrectionStatus.Approved
                : BreakCorrectionStatus.Denied;
            correction.ReviewedByUserId = actorUserId;
            correction.ReviewedAtUtc = businessTime.UtcNow;

            if (approve)
            {
                // The original window stays on the correction row (docs/01);
                // only the live session moves.
                session.StartedAtUtc = correction.CorrectedStartAtUtc;
                session.EndedAtUtc = correction.CorrectedEndAtUtc;
                session.OverrunFlagged = false; // the evaluator re-derives from the new window
            }

            await audit.WriteAsync(new AuditEntry(
                "breaks",
                approve ? "breaks.correctionApproved" : "breaks.correctionDenied",
                AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actorUserId,
                TargetType = "BreakCorrectionRequest",
                TargetId = correction.Id.ToString(),
                PublicRecordId = correction.PublicId,
                Before = new
                {
                    startAtUtc = correction.OriginalStartAtUtc,
                    endAtUtc = correction.OriginalEndAtUtc,
                },
                After = approve
                    ? new
                    {
                        startAtUtc = (DateTimeOffset?)correction.CorrectedStartAtUtc,
                        endAtUtc = (DateTimeOffset?)correction.CorrectedEndAtUtc,
                    }
                    : null,
            }, token);

            _ = await notifications.CreateAsync(correction.RequestedByUserId,
                new NotificationService.NewNotification(
                    "breaks",
                    approve ? "Break correction approved" : "Break correction denied",
                    $"{correction.PublicId}: your correction was "
                        + (approve ? "applied." : "declined."),
                    ReferenceType: "BreakCorrectionRequest",
                    ReferenceId: correction.PublicId), token);

            await outbox.EnqueueAsync(EventTypes.ApprovalsChanged,
                new { kind = "breakCorrection", publicId = correction.PublicId }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new CorrectionResult(correction, null);
    }

    /// <summary>Management edit for past days. The pre-edit window is frozen
    /// into the audit event; the reason is mandatory.</summary>
    public async Task<Result> EditAsync(
        Guid breakSessionId, Guid actorUserId, DateTimeOffset start, DateTimeOffset end,
        string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Fail("An edit needs a reason.");
        }

        if (end <= start)
        {
            return Result.Fail("The end must be after the start.");
        }

        var session = await db.BreakSessions
            .FirstOrDefaultAsync(b => b.Id == breakSessionId, ct);
        if (session is null)
        {
            return Result.Fail("Break not found.", "notFound");
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            var before = new { startAtUtc = session.StartedAtUtc, endAtUtc = session.EndedAtUtc };
            session.StartedAtUtc = start;
            session.EndedAtUtc = end;
            session.BusinessDate = businessTime.BusinessDateOf(start);

            await audit.WriteAsync(new AuditEntry(
                "breaks", "breaks.managementEdited", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actorUserId,
                TargetType = "BreakSession",
                TargetId = session.Id.ToString(),
                Reason = reason.Trim(),
                Before = before,
                After = new { startAtUtc = start, endAtUtc = (DateTimeOffset?)end },
            }, token);

            _ = await notifications.CreateAsync(session.UserId,
                new NotificationService.NewNotification(
                    "breaks",
                    "Break adjusted by management",
                    $"A break on {session.BusinessDate:MMM d} was adjusted.",
                    ReferenceType: "BreakSession",
                    ReferenceId: session.Id.ToString()), token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new Result(session, null);
    }
}
