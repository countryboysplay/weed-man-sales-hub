using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Contracts.Presence;
using SalesHub.Contracts.Workforce;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>
/// Wave 4 part 2: time off with the coverage gate, breaks with one-active and
/// same-day corrections, technical reports/grace, and the unified approvals
/// queue (CLAUDE.md §12) on real PostgreSQL.
/// </summary>
public class WaveFourWorkforceTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private HttpClient _agent = null!;
    private Guid _agentId;
    private const string Password = "wave4-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var a = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t4w-agent", Password, Roles.SalesAgent);
        _agentId = a.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        _agent = await AuthFlows.WorkingClientAsync(_factory, "t4w-agent", Password);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<Guid> VacationTypeIdAsync()
    {
        var types = await _agent.GetFromJsonAsync<List<TimeOffTypeDto>>(
            "/api/v1/time-off/types", AuthFlows.Json);
        return types!.Single(t => t.Label == "Vacation").Id;
    }

    private async Task<(Guid Id, string PublicId)> RequestVacationAsync(int daysOut = 30)
    {
        var start = new BusinessTime(TimeProvider.System).Today.AddDays(daysOut);
        var created = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/time-off/",
            new CreateTimeOffRequest(await VacationTypeIdAsync(), true,
                start, start.AddDays(2), null, null, "Family trip"));
        created.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        return (root.GetProperty("id").GetGuid(), root.GetProperty("publicId").GetString()!);
    }

    // ── time off ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Time_off_flows_request_TO_id_denial_requires_reason_approval_notifies()
    {
        var (id, publicId) = await RequestVacationAsync();
        Assert.StartsWith("TO-", publicId);

        // Overlapping second request refused.
        var start = new BusinessTime(TimeProvider.System).Today.AddDays(31);
        var overlap = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/time-off/",
            new CreateTimeOffRequest(await VacationTypeIdAsync(), true,
                start, start, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);

        // Denial without a reason is refused.
        var badDeny = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/time-off/{id}/deny", new DenyTimeOffRequest("  "));
        Assert.Equal(HttpStatusCode.BadRequest, badDeny.StatusCode);

        // Approve; the agent is notified and the status lands.
        var approve = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/time-off/{id}/approve", new ApproveTimeOffRequest(null));
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        var mine = await _agent.GetFromJsonAsync<List<TimeOffRequestDto>>(
            "/api/v1/time-off/mine", AuthFlows.Json);
        Assert.Equal("Approved", Assert.Single(mine!).Status);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.UserId == _agentId && n.Category == "timeoff")));

        // Approval froze a coverage snapshot.
        var snapshot = await _factory.WithDbAsync(db => db.TimeOffRequests
            .Where(t => t.Id == id).Select(t => t.CoverageSnapshotJson).SingleAsync());
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task Coverage_gate_blocks_or_asks_for_confirmation_per_rule()
    {
        // Floor of 1 agent, Block behavior; the only agent asks for the day off.
        await _factory.WithDbAsync(async db =>
            await db.CoverageRules.Where(r => r.Role == Roles.SalesAgent)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.MinimumAgents, 1)
                    .SetProperty(r => r.Behavior, CoverageBehavior.Block)));

        // The agent must be scheduled that day for coverage to matter.
        var businessTime = new BusinessTime(TimeProvider.System);
        var target = businessTime.Today.AddDays(35);
        var template = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/shifts/templates",
            new CreateShiftTemplateRequest("Coverage shift", Roles.SalesAgent,
                target.DayOfWeek.ToString(), new TimeOnly(9, 0), new TimeOnly(17, 0)));
        template.EnsureSuccessStatusCode();
        var templateId = JsonDocument.Parse(await template.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/shifts/assignments",
            new AssignShiftRequest(_agentId, templateId, target, null))).EnsureSuccessStatusCode();

        var created = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/time-off/",
            new CreateTimeOffRequest(await VacationTypeIdAsync(), true,
                target, target, null, null, null));
        created.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Block: approval refused outright.
        var blocked = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/time-off/{id}/approve", new ApproveTimeOffRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("coverageBlocked", await blocked.Content.ReadAsStringAsync());

        // WarnAndConfirm: first call 409s asking to confirm, the retry passes.
        await _factory.WithDbAsync(async db =>
            await db.CoverageRules.Where(r => r.Role == Roles.SalesAgent)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Behavior, CoverageBehavior.WarnAndConfirm)));

        var ask = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/time-off/{id}/approve", new ApproveTimeOffRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, ask.StatusCode);
        Assert.Contains("coverageConfirmationRequired", await ask.Content.ReadAsStringAsync());

        var confirmed = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/time-off/{id}/approve", new ApproveTimeOffRequest(null, ConfirmCoverage: true));
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task Canceling_approved_time_off_needs_management_approval()
    {
        var (id, _) = await RequestVacationAsync();
        (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/time-off/{id}/approve", new ApproveTimeOffRequest(null)))
            .EnsureSuccessStatusCode();

        // The agent cannot cancel it directly — a cancellation request opens.
        (await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/time-off/{id}/cancellation-request", new { })).EnsureSuccessStatusCode();
        var stillApproved = await _factory.WithDbAsync(db => db.TimeOffRequests
            .Where(t => t.Id == id).Select(t => t.Status).SingleAsync());
        Assert.Equal(TimeOffStatus.Approved, stillApproved);

        // It shows in the unified approvals queue; management approves it.
        var queue = await _owner.GetFromJsonAsync<ApprovalsQueueDto>(
            "/api/v1/approvals", AuthFlows.Json);
        var pending = Assert.Single(queue!.TimeOffCancellations);
        (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/time-off/cancellation-requests/{pending.Id}/decide",
            new DecideCancellationRequest(true))).EnsureSuccessStatusCode();

        Assert.Equal(TimeOffStatus.Canceled, await _factory.WithDbAsync(db => db.TimeOffRequests
            .Where(t => t.Id == id).Select(t => t.Status).SingleAsync()));
    }

    // ── breaks ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Breaks_enforce_one_active_and_same_day_corrections_preserve_originals()
    {
        var types = await _agent.GetFromJsonAsync<List<BreakTypeDto>>(
            "/api/v1/breaks/types", AuthFlows.Json);
        var lunch = types!.Single(t => t.Label == "Lunch");

        var started = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/breaks/start",
            new StartBreakRequest(lunch.Id));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var breakId = JsonDocument.Parse(await started.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Second concurrent break refused.
        Assert.Equal(HttpStatusCode.Conflict, (await AuthFlows.PostWithCsrfAsync(
            _agent, "/api/v1/breaks/start", new StartBreakRequest(lunch.Id))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/breaks/end", new { })).StatusCode);

        // Same-day correction: BRK id, originals preserved, approval applies it.
        var original = await _factory.WithDbAsync(db => db.BreakSessions
            .SingleAsync(b => b.Id == breakId));
        var correctedStart = original.StartedAtUtc.AddMinutes(-10);
        var correctedEnd = original.EndedAtUtc!.Value.AddMinutes(5);
        var correction = await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/breaks/{breakId}/corrections",
            new RequestBreakCorrectionRequest(correctedStart, correctedEnd,
                "Forgot to clock the break when I stepped out"));
        Assert.Equal(HttpStatusCode.Created, correction.StatusCode);
        var publicId = JsonDocument.Parse(await correction.Content.ReadAsStringAsync())
            .RootElement.GetProperty("publicId").GetString();
        Assert.StartsWith("BRK-", publicId);

        var queue = await _owner.GetFromJsonAsync<ApprovalsQueueDto>(
            "/api/v1/approvals", AuthFlows.Json);
        var pendingCorrection = Assert.Single(queue!.BreakCorrections);
        (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/breaks/corrections/{pendingCorrection.Id}/decide",
            new DecideBreakCorrectionRequest(true))).EnsureSuccessStatusCode();

        var applied = await _factory.WithDbAsync(db => db.BreakSessions
            .SingleAsync(b => b.Id == breakId));
        Assert.Equal(correctedStart, applied.StartedAtUtc);
        Assert.Equal(correctedEnd, applied.EndedAtUtc);

        // The original window survives on the correction record.
        var record = await _factory.WithDbAsync(db => db.BreakCorrectionRequests
            .SingleAsync(c => c.BreakSessionId == breakId));
        Assert.Equal(original.StartedAtUtc, record.OriginalStartAtUtc);
        Assert.Equal(original.EndedAtUtc, record.OriginalEndAtUtc);
    }

    [Fact]
    public async Task Past_day_breaks_are_management_edits_with_a_reason_never_self_service()
    {
        // Simulate yesterday's break directly.
        var businessTime = new BusinessTime(TimeProvider.System);
        var yesterdayUtc = businessTime.UtcNow.AddDays(-1);
        var breakId = Guid.CreateVersion7();
        await _factory.WithDbAsync(async db =>
        {
            var type = await db.BreakTypes.FirstAsync();
            db.BreakSessions.Add(new BreakSession
            {
                Id = breakId,
                UserId = _agentId,
                BreakTypeId = type.Id,
                StartedAtUtc = yesterdayUtc,
                EndedAtUtc = yesterdayUtc.AddMinutes(20),
                BusinessDate = businessTime.BusinessDateOf(yesterdayUtc),
            });
            return await db.SaveChangesAsync();
        });

        // Self-service correction refused after the day closed.
        var refused = await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/breaks/{breakId}/corrections",
            new RequestBreakCorrectionRequest(yesterdayUtc, yesterdayUtc.AddMinutes(25), "late fix"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Management edit without a reason refused; with a reason it lands + audits.
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PatchWithCsrfAsync(_owner,
            $"/api/v1/breaks/{breakId}",
            new EditBreakRequest(yesterdayUtc, yesterdayUtc.AddMinutes(25), " "))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AuthFlows.PatchWithCsrfAsync(_owner,
            $"/api/v1/breaks/{breakId}",
            new EditBreakRequest(yesterdayUtc, yesterdayUtc.AddMinutes(25),
                "Agent reported the PWA froze at clock-out"))).StatusCode);

        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditEvents
            .CountAsync(a => a.Action == "breaks.managementEdited"
                && a.TargetId == breakId.ToString())));
    }

    // ── technical grace ───────────────────────────────────────────────────────

    [Fact]
    public async Task Technical_report_alone_never_pauses_monitoring_a_grant_does()
    {
        var filed = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/technical-reports/",
            new CreateTechnicalReportRequest("Internet", "Router keeps dropping", "/sales", "1.0"));
        Assert.Equal(HttpStatusCode.Created, filed.StatusCode);
        var root = JsonDocument.Parse(await filed.Content.ReadAsStringAsync()).RootElement;
        var reportId = root.GetProperty("id").GetGuid();
        Assert.StartsWith("TECH-", root.GetProperty("publicId").GetString());

        // No grant yet → no TechnicalGrace suppression rows exist.
        Assert.Equal(0, await _factory.WithDbAsync(db => db.TechnicalGrants.CountAsync()));

        var businessTime = new BusinessTime(TimeProvider.System);
        var granted = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/technical-reports/{reportId}/grants",
            new GrantTechnicalGraceRequest(
                businessTime.UtcNow.AddMinutes(-5), businessTime.UtcNow.AddHours(1),
                "Confirmed ISP outage in the agent's area"));
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);

        var grant = await _factory.WithDbAsync(db => db.TechnicalGrants.SingleAsync());
        Assert.Equal(_agentId, grant.UserId); // bound to the reporter

        // Management list shows the active grant.
        var reports = await _owner.GetFromJsonAsync<List<TechnicalReportDto>>(
            "/api/v1/technical-reports/", AuthFlows.Json);
        Assert.True(Assert.Single(reports!).HasActiveGrant);

        // Agents cannot grant themselves grace.
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/technical-reports/{reportId}/grants",
            new GrantTechnicalGraceRequest(
                businessTime.UtcNow, businessTime.UtcNow.AddHours(8), "self serve"))).StatusCode);
    }

    [Fact]
    public async Task Approvals_queue_is_management_only_and_counts_everything()
    {
        _ = await RequestVacationAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _agent.GetAsync("/api/v1/approvals")).StatusCode);

        var queue = await _owner.GetFromJsonAsync<ApprovalsQueueDto>(
            "/api/v1/approvals", AuthFlows.Json);
        Assert.Equal(1, queue!.PendingTotal);
        Assert.Single(queue.TimeOff);
        Assert.Empty(queue.TimeOffCancellations);
        Assert.Empty(queue.BreakCorrections);
    }
}
