using System.Text;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Reporting;

/// <summary>
/// Scheduled and on-demand reports (docs/01 reports/archive). Runs generate
/// CSV artifacts into immutable blob storage and land in the Archive Center;
/// failures are recorded on the run and pushed to management. Periods are
/// business dates (America/Chicago), never raw UTC days.
/// </summary>
public sealed class ReportService(
    IAppDb db,
    IIdentityService identity,
    IFileBlobStore blobs,
    IAuditWriter audit,
    NotificationService notifications,
    BusinessTime businessTime)
{
    public async Task<(ReportSchedule? Schedule, string? Error)> CreateScheduleAsync(
        Guid actorUserId, ReportType type, ReportCadence cadence, int hourLocal,
        CancellationToken ct = default)
    {
        if (hourLocal is < 0 or > 23)
        {
            return (null, "The hour must be 0-23 (America/Chicago).");
        }

        var schedule = new ReportSchedule
        {
            Id = Guid.CreateVersion7(),
            ReportType = type,
            Cadence = cadence,
            HourLocal = hourLocal,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = businessTime.UtcNow,
        };
        schedule.NextDueAtUtc = NextDue(schedule, businessTime.UtcNow);
        db.ReportSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);
        return (schedule, null);
    }

    /// <summary>Next due instant strictly after "after": today/this period's
    /// hour if still ahead, otherwise the next period's.</summary>
    public DateTimeOffset NextDue(ReportSchedule schedule, DateTimeOffset after)
    {
        var date = businessTime.BusinessDateOf(after);
        for (var i = 0; i < 62; i++)
        {
            var candidateDate = date.AddDays(i);
            var periodOk = schedule.Cadence switch
            {
                ReportCadence.Daily => true,
                ReportCadence.Weekly => candidateDate.DayOfWeek == DayOfWeek.Monday,
                ReportCadence.Monthly => candidateDate.Day == 1,
                _ => false,
            };
            if (!periodOk)
            {
                continue;
            }

            var candidate = businessTime.ToUtc(
                candidateDate.ToDateTime(new TimeOnly(schedule.HourLocal, 0)));
            if (candidate > after)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No due date found in the next two months.");
    }

    /// <summary>The reporting period the run covers: the completed previous
    /// day/week/month relative to the due instant.</summary>
    public (DateOnly Start, DateOnly End) PeriodFor(ReportCadence cadence, DateTimeOffset dueUtc)
    {
        var date = businessTime.BusinessDateOf(dueUtc);
        return cadence switch
        {
            ReportCadence.Daily => (date.AddDays(-1), date.AddDays(-1)),
            ReportCadence.Weekly => (date.AddDays(-7), date.AddDays(-1)),
            _ => (new DateOnly(date.Year, date.Month, 1).AddMonths(-1),
                new DateOnly(date.Year, date.Month, 1).AddDays(-1)),
        };
    }

    public async Task<ReportRun> RunAsync(
        ReportType type, DateOnly periodStart, DateOnly periodEnd,
        Guid? scheduleId = null, Guid? triggeredByUserId = null, CancellationToken ct = default)
    {
        var run = new ReportRun
        {
            Id = Guid.CreateVersion7(),
            ScheduleId = scheduleId,
            ReportType = type,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            StartedAtUtc = businessTime.UtcNow,
            TriggeredByUserId = triggeredByUserId,
        };
        db.ReportRuns.Add(run);

        try
        {
            var csv = type switch
            {
                ReportType.SalesSummary => await SalesSummaryCsvAsync(periodStart, periodEnd, ct),
                ReportType.PresenceAttendanceSummary =>
                    await PresenceSummaryCsvAsync(periodStart, periodEnd, ct),
                _ => await SupportTrendsCsvAsync(periodStart, periodEnd, ct),
            };

            var title = $"{type} {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            var blob = await blobs.SaveAsync(
                stream, $"{type}-{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}.csv",
                "text/csv", triggeredByUserId, ct);

            run.ArtifactBlobId = blob.Id;
            run.Success = true;
            run.CompletedAtUtc = businessTime.UtcNow;

            db.ArchiveEntries.Add(new ArchiveEntry
            {
                Id = Guid.CreateVersion7(),
                Title = title,
                ReportType = type,
                BlobId = blob.Id,
                ReportRunId = run.Id,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                CreatedAtUtc = run.CompletedAtUtc.Value,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Success = false;
            run.Error = ex.GetType().Name + ": " + ex.Message;
            run.CompletedAtUtc = businessTime.UtcNow;

            // Report Failures panel + management push (docs mockup).
            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "reports",
                "Report generation failed",
                $"{type} for {periodStart:MMM d}–{periodEnd:MMM d} failed.",
                ReferenceType: "ReportRun",
                ReferenceId: run.Id.ToString()), ct: ct);
        }

        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Runs every due schedule once and advances its next-due marker.</summary>
    public async Task<int> RunDueSchedulesAsync(CancellationToken ct = default)
    {
        var now = businessTime.UtcNow;
        var due = await db.ReportSchedules
            .Where(s => s.Enabled && s.NextDueAtUtc != null && s.NextDueAtUtc <= now)
            .ToListAsync(ct);
        foreach (var schedule in due)
        {
            var (start, end) = PeriodFor(schedule.Cadence, schedule.NextDueAtUtc!.Value);
            _ = await RunAsync(schedule.ReportType, start, end, schedule.Id, null, ct);
            schedule.LastRunAtUtc = now;
            schedule.NextDueAtUtc = NextDue(schedule, now);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return due.Count;
    }

    /// <summary>Archive download: streamed to management with an access audit.</summary>
    public async Task<(Stream? Content, ArchiveEntry? Entry)> OpenArchiveEntryAsync(
        Guid entryId, Guid actorUserId, CancellationToken ct = default)
    {
        var entry = await db.ArchiveEntries.FirstOrDefaultAsync(a => a.Id == entryId, ct);
        if (entry is null)
        {
            return (null, null);
        }

        await audit.WriteAsync(new AuditEntry(
            "reports", "reports.archiveAccessed", AuditRetentionClass.Operational365Days)
        {
            ActorUserId = actorUserId,
            TargetType = "ArchiveEntry",
            TargetId = entry.Id.ToString(),
            After = new { entry.Title },
        }, ct);
        await db.SaveChangesAsync(ct);

        return (await blobs.OpenReadAsync(entry.BlobId, ct), entry);
    }

    // ── generators ────────────────────────────────────────────────────────────

    private async Task<string> SalesSummaryCsvAsync(
        DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await db.Sales
            .Where(s => s.State == SaleState.Active
                && s.BusinessDate >= start && s.BusinessDate <= end)
            .GroupBy(s => s.SellerUserId)
            .Select(g => new
            {
                SellerUserId = g.Key,
                Count = g.Count(),
                Total = g.Sum(s => s.Amount),
            })
            .ToListAsync(ct);
        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var csv = new StringBuilder("Agent,Sales,TotalAmount\r\n");
        foreach (var row in rows.OrderByDescending(r => r.Total))
        {
            csv.Append(Escape(names.GetValueOrDefault(row.SellerUserId, "Unknown")))
                .Append(',').Append(row.Count)
                .Append(',').Append(row.Total.ToString("F2"))
                .Append("\r\n");
        }

        return csv.ToString();
    }

    private async Task<string> PresenceSummaryCsvAsync(
        DateOnly start, DateOnly end, CancellationToken ct)
    {
        var flags = await db.PresenceFlags
            .Where(f => f.BusinessDate >= start && f.BusinessDate <= end)
            .GroupBy(f => new { f.UserId, f.Category })
            .Select(g => new { g.Key.UserId, g.Key.Category, Count = g.Count() })
            .ToListAsync(ct);
        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var csv = new StringBuilder("Employee,LateStart,Disappeared,BreakOverrun\r\n");
        foreach (var group in flags.GroupBy(f => f.UserId))
        {
            csv.Append(Escape(names.GetValueOrDefault(group.Key, "Unknown")))
                .Append(',').Append(group.FirstOrDefault(x => x.Category == "LateStart")?.Count ?? 0)
                .Append(',').Append(group.FirstOrDefault(x => x.Category == "Disappeared")?.Count ?? 0)
                .Append(',').Append(group.FirstOrDefault(x => x.Category == "BreakOverrun")?.Count ?? 0)
                .Append("\r\n");
        }

        return csv.ToString();
    }

    private async Task<string> SupportTrendsCsvAsync(
        DateOnly start, DateOnly end, CancellationToken ct)
    {
        var startUtc = businessTime.StartOfBusinessDateUtc(start);
        var endUtc = businessTime.StartOfBusinessDateUtc(end.AddDays(1));
        var rows = await db.SupportTickets
            .Where(t => t.CreatedAtUtc >= startUtc && t.CreatedAtUtc < endUtc)
            .GroupBy(t => new { t.IssueType, t.Priority })
            .Select(g => new { g.Key.IssueType, g.Key.Priority, Count = g.Count() })
            .ToListAsync(ct);

        var csv = new StringBuilder("IssueType,Priority,Tickets\r\n");
        foreach (var row in rows.OrderBy(r => r.IssueType).ThenByDescending(r => r.Priority))
        {
            csv.Append(Escape(row.IssueType))
                .Append(',').Append(row.Priority)
                .Append(',').Append(row.Count)
                .Append("\r\n");
        }

        return csv.ToString();
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
