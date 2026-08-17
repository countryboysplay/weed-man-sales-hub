using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Workforce;

/// <summary>
/// Technical reports (TECH-YYYY-#####) and explicit grace grants. Filing a
/// report NEVER pauses presence monitoring by itself — only a management
/// grant does, and the grant window is what the presence evaluator honors
/// (CLAUDE.md §12).
/// </summary>
public sealed class TechnicalReportService(
    IAppDb db,
    IPublicIdGenerator publicIds,
    IAuditWriter audit,
    NotificationService notifications,
    BusinessTime businessTime)
{
    private static readonly string[] IssueTypes = ["Internet", "Computer", "BrowserPwa", "Other"];

    public sealed record ReportResult(TechnicalReport? Report, string? Error)
    {
        public static ReportResult Fail(string error) => new(null, error);
    }

    public async Task<ReportResult> FileAsync(
        Guid reporterUserId, string issueType, string description, string? page,
        string? appVersion, string? browserFamily, CancellationToken ct = default)
    {
        if (!IssueTypes.Contains(issueType, StringComparer.OrdinalIgnoreCase))
        {
            return ReportResult.Fail("Issue type must be Internet, Computer, BrowserPwa, or Other.");
        }

        var text = description?.Trim() ?? "";
        if (text.Length is 0 or > 4000)
        {
            return ReportResult.Fail("A report needs a description up to 4000 characters.");
        }

        TechnicalReport report = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            report = new TechnicalReport
            {
                Id = Guid.CreateVersion7(),
                PublicId = await publicIds.NextAsync("TECH", token),
                ReporterUserId = reporterUserId,
                IssueType = IssueTypes.First(t => t.Equals(issueType, StringComparison.OrdinalIgnoreCase)),
                Description = text,
                Page = page?.Trim() ?? "",
                AppVersion = appVersion?.Trim() ?? "",
                BrowserFamily = browserFamily?.Trim() ?? "",
                CreatedAtUtc = businessTime.UtcNow,
            };
            db.TechnicalReports.Add(report);

            await audit.WriteAsync(new AuditEntry(
                "technical", "technical.reported", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = reporterUserId,
                TargetType = "TechnicalReport",
                TargetId = report.Id.ToString(),
                PublicRecordId = report.PublicId,
                After = new { issueType = report.IssueType },
            }, token);

            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "technical",
                "Technical issue reported",
                $"{report.PublicId}: {report.IssueType}.",
                ReferenceType: "TechnicalReport",
                ReferenceId: report.PublicId), excludeUserId: reporterUserId, ct: token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new ReportResult(report, null);
    }

    public sealed record GrantResult(TechnicalGrant? Grant, string? Error)
    {
        public static GrantResult Fail(string error) => new(null, error);
    }

    /// <summary>The explicit management decision that pauses monitoring for a
    /// window. Bound to the report and to the reporter — a grant cannot cover
    /// someone who never reported the problem.</summary>
    public async Task<GrantResult> GrantGraceAsync(
        Guid technicalReportId, Guid actorUserId, DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return GrantResult.Fail("A grace grant needs a reason.");
        }

        if (endAtUtc <= startAtUtc)
        {
            return GrantResult.Fail("The grant end must be after its start.");
        }

        var report = await db.TechnicalReports
            .FirstOrDefaultAsync(r => r.Id == technicalReportId, ct);
        if (report is null)
        {
            return GrantResult.Fail("Technical report not found.");
        }

        TechnicalGrant grant = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            grant = new TechnicalGrant
            {
                Id = Guid.CreateVersion7(),
                TechnicalReportId = report.Id,
                UserId = report.ReporterUserId,
                StartAtUtc = startAtUtc,
                EndAtUtc = endAtUtc,
                GrantedByUserId = actorUserId,
                Reason = reason.Trim(),
            };
            db.TechnicalGrants.Add(grant);

            await audit.WriteAsync(new AuditEntry(
                "technical", "technical.graceGranted", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actorUserId,
                TargetType = "TechnicalGrant",
                TargetId = grant.Id.ToString(),
                PublicRecordId = report.PublicId,
                Reason = grant.Reason,
                After = new { grant.UserId, startAtUtc, endAtUtc },
            }, token);

            _ = await notifications.CreateAsync(report.ReporterUserId,
                new NotificationService.NewNotification(
                    "technical",
                    "Technical grace granted",
                    $"{report.PublicId}: monitoring paused "
                        + $"{businessTime.ToLocal(startAtUtc):h:mm tt}–{businessTime.ToLocal(endAtUtc):h:mm tt}.",
                    ReferenceType: "TechnicalReport",
                    ReferenceId: report.PublicId), token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new GrantResult(grant, null);
    }
}
