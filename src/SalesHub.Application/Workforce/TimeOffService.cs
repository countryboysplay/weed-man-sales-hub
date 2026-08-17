using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Contracts.Workforce;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Workforce;

/// <summary>
/// Time off (TO-YYYY-#####): request → management decision, with the coverage
/// check on approval (Warn / WarnAndConfirm / Block per role rule). Denials
/// require a reason; canceling APPROVED time off needs its own management
/// approval (CLAUDE.md §12). The approval-time coverage numbers are frozen
/// into CoverageSnapshotJson so later schedule edits can't rewrite history.
/// </summary>
public sealed class TimeOffService(
    IAppDb db,
    IIdentityService identity,
    IPublicIdGenerator publicIds,
    IAuditWriter audit,
    IOutboxWriter outbox,
    NotificationService notifications,
    BusinessTime businessTime)
{
    public sealed record Result(TimeOffRequest? Request, string? Error, string? Code = null)
    {
        public static Result Fail(string error, string? code = null) => new(null, error, code);
    }

    public async Task<Result> RequestAsync(
        Guid userId, CreateTimeOffRequest input, CancellationToken ct = default)
    {
        if (input.EndDate < input.StartDate)
        {
            return Result.Fail("The end date is before the start date.");
        }

        if (input.StartDate < businessTime.Today)
        {
            return Result.Fail("Time off cannot start in the past.");
        }

        if (!input.FullDay)
        {
            if (input.StartDate != input.EndDate)
            {
                return Result.Fail("A partial-day request covers a single date.");
            }

            if (input.StartLocalTime is not { } s || input.EndLocalTime is not { } e || e <= s)
            {
                return Result.Fail("A partial-day request needs a valid start and end time.");
            }
        }

        var type = await db.TimeOffTypes
            .FirstOrDefaultAsync(t => t.Id == input.TypeId && t.Active, ct);
        if (type is null)
        {
            return Result.Fail("Unknown time-off type.");
        }

        var overlapping = await db.TimeOffRequests.AnyAsync(t =>
            t.UserId == userId
            && (t.Status == TimeOffStatus.Pending || t.Status == TimeOffStatus.Approved)
            && t.StartDate <= input.EndDate && t.EndDate >= input.StartDate, ct);
        if (overlapping)
        {
            return Result.Fail("An open request already covers part of that range.", "overlap");
        }

        TimeOffRequest request = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            request = new TimeOffRequest
            {
                Id = Guid.CreateVersion7(),
                PublicId = await publicIds.NextAsync("TO", token),
                UserId = userId,
                TypeId = type.Id,
                FullDay = input.FullDay,
                StartDate = input.StartDate,
                EndDate = input.EndDate,
                StartLocalTime = input.FullDay ? null : input.StartLocalTime,
                EndLocalTime = input.FullDay ? null : input.EndLocalTime,
                Reason = input.Reason?.Trim() ?? "",
                Status = TimeOffStatus.Pending,
                CreatedAtUtc = businessTime.UtcNow,
            };
            db.TimeOffRequests.Add(request);

            await audit.WriteAsync(new AuditEntry(
                "timeoff", "timeoff.requested", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = userId,
                TargetType = "TimeOffRequest",
                TargetId = request.Id.ToString(),
                PublicRecordId = request.PublicId,
                After = new
                {
                    type = type.Label,
                    input.FullDay,
                    startDate = input.StartDate.ToString("O"),
                    endDate = input.EndDate.ToString("O"),
                },
            }, token);

            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "approvals",
                "Time-off request",
                $"{request.PublicId}: {input.StartDate:MMM d}–{input.EndDate:MMM d}.",
                ReferenceType: "TimeOffRequest",
                ReferenceId: request.PublicId), excludeUserId: userId, ct: token);

            await outbox.EnqueueAsync(EventTypes.ApprovalsChanged,
                new { kind = "timeOff", publicId = request.PublicId }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new Result(request, null);
    }

    /// <summary>Coverage math for one request: for each affected date, how many
    /// same-role agents are scheduled and how many are already on approved
    /// leave — assuming this request were approved too.</summary>
    public async Task<CoverageCheckDto> CheckCoverageAsync(
        TimeOffRequest request, CancellationToken ct = default)
    {
        var requester = await identity.FindByIdAsync(request.UserId, ct)
            ?? throw new InvalidOperationException("Requester not found.");
        var rule = await db.CoverageRules.FirstOrDefaultAsync(r => r.Role == requester.Role, ct);
        var minimum = rule?.MinimumAgents ?? 0;
        var behavior = rule?.Behavior ?? CoverageBehavior.Warn;

        var roleUsers = (await identity.ListUsersAsync(new UserQuery(Role: requester.Role), ct))
            .Select(u => u.Id)
            .ToList();

        var assignments = await db.UserShiftAssignments
            .Where(a => roleUsers.Contains(a.UserId)
                && a.StartDate <= request.EndDate
                && (a.EndDate == null || a.EndDate >= request.StartDate))
            .Join(db.ShiftTemplates.Where(t => t.Active),
                a => a.ShiftTemplateId, t => t.Id,
                (a, t) => new { a.UserId, a.StartDate, a.EndDate, t.DayOfWeek })
            .ToListAsync(ct);

        var approvedLeave = await db.TimeOffRequests
            .Where(t => t.Status == TimeOffStatus.Approved
                && roleUsers.Contains(t.UserId)
                && t.StartDate <= request.EndDate && t.EndDate >= request.StartDate)
            .Select(t => new { t.UserId, t.StartDate, t.EndDate })
            .ToListAsync(ct);

        var days = new List<CoverageDayDto>();
        for (var date = request.StartDate; date <= request.EndDate; date = date.AddDays(1))
        {
            var weekday = date.DayOfWeek;
            var scheduled = assignments
                .Where(a => a.DayOfWeek == weekday
                    && a.StartDate <= date && (a.EndDate == null || a.EndDate >= date))
                .Select(a => a.UserId)
                .Distinct()
                .ToHashSet();
            var onLeave = approvedLeave
                .Where(l => l.StartDate <= date && l.EndDate >= date)
                .Select(l => l.UserId)
                .Where(scheduled.Contains)
                .Distinct()
                .Count();
            // This request's own days count as leave for the what-if.
            if (scheduled.Contains(request.UserId))
            {
                onLeave += 1;
            }

            days.Add(new CoverageDayDto(date, scheduled.Count, onLeave, scheduled.Count - onLeave));
        }

        return new CoverageCheckDto(requester.Role, minimum, behavior.ToString(), days);
    }

    public async Task<Result> ApproveAsync(
        Guid requestId, Guid actorUserId, string? note, bool confirmCoverage,
        CancellationToken ct = default)
    {
        var request = await db.TimeOffRequests
            .FirstOrDefaultAsync(t => t.Id == requestId, ct);
        if (request is null)
        {
            return Result.Fail("Time-off request not found.", "notFound");
        }

        if (request.Status != TimeOffStatus.Pending)
        {
            return Result.Fail("Only pending requests can be approved.", "notPending");
        }

        var coverage = await CheckCoverageAsync(request, ct);
        var shortfall = coverage.Days.Any(d => d.Remaining < coverage.MinimumAgents);
        if (shortfall)
        {
            switch (Enum.Parse<CoverageBehavior>(coverage.Behavior))
            {
                case CoverageBehavior.Block:
                    return Result.Fail(
                        "Approving this would drop coverage below the configured minimum.",
                        "coverageBlocked");
                case CoverageBehavior.WarnAndConfirm when !confirmCoverage:
                    return Result.Fail(
                        "Coverage drops below the minimum; confirm to approve anyway.",
                        "coverageConfirmationRequired");
                case CoverageBehavior.Warn:
                case CoverageBehavior.WarnAndConfirm:
                    break;
            }
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            request.Status = TimeOffStatus.Approved;
            request.ReviewedByUserId = actorUserId;
            request.ReviewedAtUtc = businessTime.UtcNow;
            request.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            request.CoverageSnapshotJson = JsonSerializer.Serialize(coverage);

            await WriteDecisionAsync(request, actorUserId, "timeoff.approved", token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new Result(request, null);
    }

    public async Task<Result> DenyAsync(
        Guid requestId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            // The denial reason is mandatory and shown to the employee.
            return Result.Fail("A denial requires a reason.");
        }

        var request = await db.TimeOffRequests
            .FirstOrDefaultAsync(t => t.Id == requestId, ct);
        if (request is null)
        {
            return Result.Fail("Time-off request not found.", "notFound");
        }

        if (request.Status != TimeOffStatus.Pending)
        {
            return Result.Fail("Only pending requests can be denied.", "notPending");
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            request.Status = TimeOffStatus.Denied;
            request.ReviewedByUserId = actorUserId;
            request.ReviewedAtUtc = businessTime.UtcNow;
            request.DenialReason = reason.Trim();

            await WriteDecisionAsync(request, actorUserId, "timeoff.denied", token);
            await db.SaveChangesAsync(token);
        }, ct);

        return new Result(request, null);
    }

    public async Task<Result> RequestCancellationAsync(
        Guid requestId, Guid userId, CancellationToken ct = default)
    {
        var request = await db.TimeOffRequests
            .FirstOrDefaultAsync(t => t.Id == requestId && t.UserId == userId, ct);
        if (request is null)
        {
            return Result.Fail("Time-off request not found.", "notFound");
        }

        // A pending request cancels immediately — it never needed approval.
        if (request.Status == TimeOffStatus.Pending)
        {
            request.Status = TimeOffStatus.Canceled;
            await audit.WriteAsync(new AuditEntry(
                "timeoff", "timeoff.canceled", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = userId,
                TargetType = "TimeOffRequest",
                TargetId = request.Id.ToString(),
                PublicRecordId = request.PublicId,
            }, ct);
            await db.SaveChangesAsync(ct);
            return new Result(request, null);
        }

        if (request.Status != TimeOffStatus.Approved)
        {
            return Result.Fail("Only pending or approved time off can be canceled.", "notCancelable");
        }

        var alreadyPending = await db.TimeOffCancellationRequests.AnyAsync(
            c => c.TimeOffRequestId == request.Id && c.ResultStatus == null, ct);
        if (alreadyPending)
        {
            return Result.Fail("A cancellation request is already pending.", "duplicate");
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.TimeOffCancellationRequests.Add(new TimeOffCancellationRequest
            {
                Id = Guid.CreateVersion7(),
                TimeOffRequestId = request.Id,
                RequestedByUserId = userId,
                CreatedAtUtc = businessTime.UtcNow,
            });

            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "approvals",
                "Time-off cancellation request",
                $"{request.PublicId}: cancellation of approved time off.",
                ReferenceType: "TimeOffRequest",
                ReferenceId: request.PublicId), excludeUserId: userId, ct: token);

            await outbox.EnqueueAsync(EventTypes.ApprovalsChanged,
                new { kind = "timeOffCancellation", publicId = request.PublicId }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new Result(request, null);
    }

    public async Task<Result> DecideCancellationAsync(
        Guid cancellationId, Guid actorUserId, bool approve, CancellationToken ct = default)
    {
        var cancellation = await db.TimeOffCancellationRequests
            .FirstOrDefaultAsync(c => c.Id == cancellationId, ct);
        if (cancellation is null || cancellation.ResultStatus is not null)
        {
            return Result.Fail("Pending cancellation request not found.", "notFound");
        }

        var request = await db.TimeOffRequests
            .FirstAsync(t => t.Id == cancellation.TimeOffRequestId, ct);

        await db.ExecuteInTransactionAsync(async token =>
        {
            cancellation.ReviewedByUserId = actorUserId;
            cancellation.ReviewedAtUtc = businessTime.UtcNow;
            cancellation.ResultStatus = approve ? TimeOffStatus.Canceled : TimeOffStatus.Approved;
            if (approve)
            {
                request.Status = TimeOffStatus.Canceled;
            }

            await audit.WriteAsync(new AuditEntry(
                "timeoff",
                approve ? "timeoff.cancellationApproved" : "timeoff.cancellationDenied",
                AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actorUserId,
                TargetType = "TimeOffRequest",
                TargetId = request.Id.ToString(),
                PublicRecordId = request.PublicId,
            }, token);

            _ = await notifications.CreateAsync(request.UserId,
                new NotificationService.NewNotification(
                    "timeoff",
                    approve ? "Time off canceled" : "Cancellation declined",
                    $"{request.PublicId}: your cancellation request was "
                        + (approve ? "approved." : "declined — the time off stands."),
                    ReferenceType: "TimeOffRequest",
                    ReferenceId: request.PublicId), token);

            await outbox.EnqueueAsync(EventTypes.ApprovalsChanged,
                new { kind = "timeOffCancellation", publicId = request.PublicId }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new Result(request, null);
    }

    private async Task WriteDecisionAsync(
        TimeOffRequest request, Guid actorUserId, string action, CancellationToken ct)
    {
        await audit.WriteAsync(new AuditEntry(
            "timeoff", action, AuditRetentionClass.Operational365Days)
        {
            ActorUserId = actorUserId,
            TargetType = "TimeOffRequest",
            TargetId = request.Id.ToString(),
            PublicRecordId = request.PublicId,
            After = new { status = request.Status.ToString() },
        }, ct);

        var approved = request.Status == TimeOffStatus.Approved;
        _ = await notifications.CreateAsync(request.UserId,
            new NotificationService.NewNotification(
                "timeoff",
                approved ? "Time off approved" : "Time off denied",
                approved
                    ? $"{request.PublicId}: {request.StartDate:MMM d}–{request.EndDate:MMM d} approved."
                    : $"{request.PublicId}: denied — {request.DenialReason}",
                ReferenceType: "TimeOffRequest",
                ReferenceId: request.PublicId), ct);

        await outbox.EnqueueAsync(EventTypes.TimeOffDecided, new
        {
            userId = request.UserId,
            publicId = request.PublicId,
            status = request.Status.ToString(),
        }, ct);

        await outbox.EnqueueAsync(EventTypes.ApprovalsChanged,
            new { kind = "timeOff", publicId = request.PublicId }, ct);
    }
}
