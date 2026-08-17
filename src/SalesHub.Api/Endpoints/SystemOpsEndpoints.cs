using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Reporting;
using SalesHub.Application.Search;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Wave 5 part 3 surface: global search, sync health + remote device
/// commands, system health summary, and the reports/archive center.
/// </summary>
public static class SystemOpsEndpoints
{
    public static IEndpointRouteBuilder MapSystemOpsEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/search", SearchAsync).RequireAuthorization(Policies.Employee);

        var sync = api.MapGroup("/sync");
        sync.MapPost("/actions", RecordSyncActionsAsync).RequireAuthorization(Policies.Employee);
        sync.MapGet("/commands/pending", PendingCommandsAsync)
            .RequireAuthorization(Policies.Employee);
        sync.MapPost("/commands/{id:guid}/ack", AckCommandAsync)
            .RequireAuthorization(Policies.Employee);
        sync.MapGet("/health", SyncHealthAsync).RequireAuthorization(Policies.Management);

        api.MapPost("/devices/commands", IssueCommandAsync)
            .RequireAuthorization(Policies.Management);
        api.MapGet("/system/health", SystemHealthAsync)
            .RequireAuthorization(Policies.Management);

        var reports = api.MapGroup("/reports").RequireAuthorization(Policies.Management);
        reports.MapGet("/schedules", SchedulesAsync);
        reports.MapPost("/schedules", CreateScheduleAsync);
        reports.MapPatch("/schedules/{id:guid}", ToggleScheduleAsync);
        reports.MapGet("/runs", RunsAsync);
        reports.MapPost("/run", RunNowAsync);

        var archive = api.MapGroup("/archive").RequireAuthorization(Policies.Management);
        archive.MapGet("/", ArchiveAsync);
        archive.MapGet("/{id:guid}/download", DownloadArchiveAsync);

        return api;
    }

    // ── search ────────────────────────────────────────────────────────────────

    private static async Task<IResult> SearchAsync(
        string q, HttpContext http, SearchService search, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var role = http.Items[AuthConstants.UserRoleItemKey] as string ?? "";
        var hits = await search.SearchAsync(userId, role, q, ct);
        return Results.Ok(hits);
    }

    // ── sync ──────────────────────────────────────────────────────────────────

    public sealed record SyncActionReport(
        string Operation, string Status, string? Error, string? IdempotencyKey);

    private static async Task<IResult> RecordSyncActionsAsync(
        List<SyncActionReport> reports, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (reports.Count is 0 or > 100)
        {
            return Problems.Validation(http, "Send 1-100 sync action reports.");
        }

        var (userId, session) = AuthEndpoints.Current(http);
        foreach (var report in reports)
        {
            if (!Enum.TryParse<SyncActionStatus>(report.Status, true, out var status))
            {
                return Problems.Validation(http, "Status must be Accepted or Rejected.");
            }

            db.SyncActions.Add(new SyncAction
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                SessionId = session.Id,
                DeviceId = session.DeviceId,
                Operation = report.Operation.Length > 128
                    ? report.Operation[..128]
                    : report.Operation,
                IdempotencyKey = report.IdempotencyKey,
                Status = status,
                Error = report.Error,
                CreatedAtUtc = businessTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    public sealed record SyncHealthRow(
        Guid UserId, string DisplayName, string DeviceId,
        int Accepted, int Rejected, string Severity, DateTimeOffset LastActivityUtc);

    private static async Task<IResult> SyncHealthAsync(
        IAppDb db, IIdentityService identity, BusinessTime businessTime, CancellationToken ct)
    {
        var horizon = businessTime.UtcNow - TimeSpan.FromDays(7);
        var rows = await db.SyncActions
            .Where(s => s.CreatedAtUtc >= horizon)
            .GroupBy(s => new { s.UserId, s.DeviceId })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.DeviceId,
                Accepted = g.Count(x => x.Status == SyncActionStatus.Accepted),
                Rejected = g.Count(x => x.Status == SyncActionStatus.Rejected),
                Last = g.Max(x => x.CreatedAtUtc),
            })
            .ToListAsync(ct);
        var names = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        return Results.Ok(rows
            .OrderByDescending(r => r.Rejected)
            .Select(r => new SyncHealthRow(
                r.UserId, names.GetValueOrDefault(r.UserId, "Unknown"), r.DeviceId,
                r.Accepted, r.Rejected,
                r.Rejected >= 5 ? "High" : r.Rejected > 0 ? "Warning" : "OK",
                r.Last))
            .ToList());
    }

    public sealed record IssueCommandRequest(
        string CommandType, Guid TargetUserId, Guid? TargetSessionId, string? TargetDeviceId);

    private static async Task<IResult> IssueCommandAsync(
        IssueCommandRequest request, HttpContext http, IAppDb db, IAuditWriter audit,
        IOutboxWriter outbox, IIdentityService identity, BusinessTime businessTime,
        CancellationToken ct)
    {
        if (!Enum.TryParse<RemoteDeviceCommandType>(request.CommandType, true, out var type))
        {
            return Problems.Validation(http, "Command must be Resync, Refresh, or ClearSafeCache.");
        }

        if (await identity.FindByIdAsync(request.TargetUserId, ct) is null)
        {
            return Problems.NotFound(http, "Target user not found.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        RemoteDeviceCommand command = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            command = new RemoteDeviceCommand
            {
                Id = Guid.CreateVersion7(),
                CommandType = type,
                RequestedByUserId = actorId,
                TargetUserId = request.TargetUserId,
                TargetSessionId = request.TargetSessionId,
                TargetDeviceId = request.TargetDeviceId,
                CreatedAtUtc = businessTime.UtcNow,
            };
            db.RemoteDeviceCommands.Add(command);

            // Remote commands are audited for 90 days (CLAUDE.md §16).
            await audit.WriteAsync(new AuditEntry(
                "sync", "sync.remoteCommandIssued", AuditRetentionClass.Operational90Days)
            {
                ActorUserId = actorId,
                TargetType = "RemoteDeviceCommand",
                TargetId = command.Id.ToString(),
                After = new { commandType = type.ToString(), request.TargetUserId },
            }, token);

            await outbox.EnqueueAsync(EventTypes.RemoteDeviceCommandIssued, new
            {
                userId = request.TargetUserId, // routes to the target's devices
                commandId = command.Id,
                commandType = type.ToString(),
                targetSessionId = request.TargetSessionId,
                targetDeviceId = request.TargetDeviceId,
            }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return Results.Created($"/api/v1/devices/commands/{command.Id}", new { command.Id });
    }

    private static async Task<IResult> PendingCommandsAsync(
        HttpContext http, IAppDb db, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var commands = await db.RemoteDeviceCommands
            .Where(c => c.TargetUserId == userId
                && c.Status == RemoteDeviceCommandStatus.Pending
                && (c.TargetSessionId == null || c.TargetSessionId == session.Id)
                && (c.TargetDeviceId == null || c.TargetDeviceId == session.DeviceId))
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new { c.Id, CommandType = c.CommandType.ToString(), c.CreatedAtUtc })
            .ToListAsync(ct);
        return Results.Ok(commands);
    }

    private static async Task<IResult> AckCommandAsync(
        Guid id, HttpContext http, IAppDb db, BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var command = await db.RemoteDeviceCommands
            .FirstOrDefaultAsync(c => c.Id == id && c.TargetUserId == userId, ct);
        if (command is null)
        {
            return Problems.NotFound(http, "Command not found.");
        }

        if (command.Status == RemoteDeviceCommandStatus.Pending)
        {
            command.Status = RemoteDeviceCommandStatus.Acknowledged;
            command.AcknowledgedAtUtc = businessTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    // ── system health ─────────────────────────────────────────────────────────

    private static async Task<IResult> SystemHealthAsync(
        IAppDb db, BusinessTime businessTime, CancellationToken ct)
    {
        var now = businessTime.UtcNow;
        var outboxPending = await db.OutboxMessages
            .CountAsync(m => m.ProcessedAtUtc == null && !m.Failed, ct);
        var outboxFailed = await db.OutboxMessages.CountAsync(m => m.Failed, ct);
        var jobs = await db.ScheduledJobs
            .Select(j => new
            {
                j.JobKey,
                j.Enabled,
                j.NextRunAtUtc,
                LastRun = db.ScheduledJobRuns
                    .Where(r => r.JobId == j.Id)
                    .OrderByDescending(r => r.StartedAtUtc)
                    .Select(r => (DateTimeOffset?)r.StartedAtUtc)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var staleJobs = jobs.Count(j => j.Enabled
            && j.NextRunAtUtc != null && j.NextRunAtUtc < now - TimeSpan.FromMinutes(10));

        return Results.Ok(new
        {
            status = outboxFailed > 0 || staleJobs > 0 ? "Degraded" : "Healthy",
            outbox = new { pending = outboxPending, failed = outboxFailed },
            jobs,
            staleJobs,
            serverTimeUtc = now,
        });
    }

    // ── reports / archive ─────────────────────────────────────────────────────

    public sealed record CreateScheduleRequest(string ReportType, string Cadence, int HourLocal);

    private static async Task<IResult> SchedulesAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.ReportSchedules
            .OrderBy(s => s.ReportType)
            .Select(s => new
            {
                s.Id,
                ReportType = s.ReportType.ToString(),
                Cadence = s.Cadence.ToString(),
                s.HourLocal,
                s.Enabled,
                s.NextDueAtUtc,
                s.LastRunAtUtc,
            })
            .ToListAsync(ct));

    private static async Task<IResult> CreateScheduleAsync(
        CreateScheduleRequest request, HttpContext http,
        ReportService reports, CancellationToken ct)
    {
        if (!Enum.TryParse<ReportType>(request.ReportType, true, out var type))
        {
            return Problems.Validation(http,
                "ReportType must be SalesSummary, PresenceAttendanceSummary, or SupportTrends.");
        }

        if (!Enum.TryParse<ReportCadence>(request.Cadence, true, out var cadence))
        {
            return Problems.Validation(http, "Cadence must be Daily, Weekly, or Monthly.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        var (schedule, error) = await reports.CreateScheduleAsync(
            actorId, type, cadence, request.HourLocal, ct);
        return schedule is not null
            ? Results.Created($"/api/v1/reports/schedules/{schedule.Id}",
                new { schedule.Id, schedule.NextDueAtUtc })
            : Problems.Validation(http, error!);
    }

    public sealed record ToggleScheduleRequest(bool Enabled);

    private static async Task<IResult> ToggleScheduleAsync(
        Guid id, ToggleScheduleRequest request, HttpContext http,
        IAppDb db, CancellationToken ct)
    {
        var schedule = await db.ReportSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return Problems.NotFound(http, "Schedule not found.");
        }

        schedule.Enabled = request.Enabled;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RunsAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.ReportRuns
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                ReportType = r.ReportType.ToString(),
                r.PeriodStart,
                r.PeriodEnd,
                r.StartedAtUtc,
                r.CompletedAtUtc,
                r.Success,
                r.Error,
            })
            .ToListAsync(ct));

    public sealed record RunNowRequest(string ReportType, DateOnly PeriodStart, DateOnly PeriodEnd);

    private static async Task<IResult> RunNowAsync(
        RunNowRequest request, HttpContext http, ReportService reports, CancellationToken ct)
    {
        if (!Enum.TryParse<ReportType>(request.ReportType, true, out var type))
        {
            return Problems.Validation(http, "Unknown report type.");
        }

        if (request.PeriodEnd < request.PeriodStart)
        {
            return Problems.Validation(http, "The period end is before its start.");
        }

        var (actorId, _) = AuthEndpoints.Current(http);
        var run = await reports.RunAsync(
            type, request.PeriodStart, request.PeriodEnd, null, actorId, ct);
        return Results.Ok(new { run.Id, run.Success, run.Error });
    }

    private static async Task<IResult> ArchiveAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.ArchiveEntries
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(200)
            .Select(a => new
            {
                a.Id,
                a.Title,
                ReportType = a.ReportType.ToString(),
                a.PeriodStart,
                a.PeriodEnd,
                a.CreatedAtUtc,
                a.Recovered,
            })
            .ToListAsync(ct));

    private static async Task<IResult> DownloadArchiveAsync(
        Guid id, HttpContext http, ReportService reports, CancellationToken ct)
    {
        var (actorId, _) = AuthEndpoints.Current(http);
        var (content, entry) = await reports.OpenArchiveEntryAsync(id, actorId, ct);
        return content is null || entry is null
            ? Problems.NotFound(http, "Archive entry not found.")
            : Results.Stream(content, "text/csv", $"{entry.Title}.csv");
    }
}
