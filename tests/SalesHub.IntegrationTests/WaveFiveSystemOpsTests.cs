using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Application.Reporting;
using SalesHub.Contracts.Support;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Wave 5 part 3: search, sync health, remote commands, reports and
/// the archive (docs/02) on real PostgreSQL.</summary>
public class WaveFiveSystemOpsTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private HttpClient _agent = null!;
    private Guid _agentId;
    private const string Password = "wave5-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var agent = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t5o-agent", Password, Roles.SalesAgent);
        _agentId = agent.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        _agent = await AuthFlows.WorkingClientAsync(_factory, "t5o-agent", Password);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record Hit(string Kind, string Reference, string Title, string Snippet, string WhyMatched);

    [Fact]
    public async Task Search_runs_behind_permission_predicates_and_explains_matches()
    {
        // An agent files a ticket; a note goes on their record.
        var ticket = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/support/",
            new CreateSupportTicketRequest("Computer", "keyboard is broken again", null, null, null));
        ticket.EnsureSuccessStatusCode();
        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/employees/{_agentId}/notes",
            new SalesHub.Contracts.Records.CreateNoteRequest(
                "Coaching", "Normal", "keyboard-slamming incident on the floor")))
            .EnsureSuccessStatusCode();

        // The agent finds their own ticket but never the management note.
        var agentHits = await _agent.GetFromJsonAsync<List<Hit>>(
            "/api/v1/search?q=keyboard", AuthFlows.Json);
        Assert.Contains(agentHits!, h => h.Kind == "Support");
        Assert.DoesNotContain(agentHits!, h => h.Kind == "ManagementNote");
        Assert.All(agentHits!, h => Assert.False(string.IsNullOrEmpty(h.WhyMatched)));

        // Management sees the note too, and that search is audited server-side.
        var ownerHits = await _owner.GetFromJsonAsync<List<Hit>>(
            "/api/v1/search?q=keyboard", AuthFlows.Json);
        Assert.Contains(ownerHits!, h => h.Kind == "ManagementNote");
        Assert.True(await _factory.WithDbAsync(db => db.AuditEvents
            .AnyAsync(a => a.Action == "search.managementRecordsSearched")));
    }

    [Fact]
    public async Task Remote_commands_reach_only_their_target_and_are_audited()
    {
        var issued = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/devices/commands",
            new { commandType = "Resync", targetUserId = _agentId });
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var commandId = JsonDocument.Parse(await issued.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Agents cannot issue commands.
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthFlows.PostWithCsrfAsync(_agent,
            "/api/v1/devices/commands",
            new { commandType = "Refresh", targetUserId = _agentId })).StatusCode);

        // The target sees it pending and acknowledges it.
        var pending = await _agent.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/sync/commands/pending", AuthFlows.Json);
        Assert.Single(pending!);
        (await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/sync/commands/{commandId}/ack", new { })).EnsureSuccessStatusCode();

        Assert.Equal(RemoteDeviceCommandStatus.Acknowledged,
            await _factory.WithDbAsync(db => db.RemoteDeviceCommands
                .Where(c => c.Id == commandId).Select(c => c.Status).SingleAsync()));

        // 90-day audit trail exists (CLAUDE.md §16).
        Assert.True(await _factory.WithDbAsync(db => db.AuditEvents
            .AnyAsync(a => a.Action == "sync.remoteCommandIssued")));
    }

    [Fact]
    public async Task Sync_health_aggregates_failures_per_user_and_device()
    {
        (await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/sync/actions", new[]
        {
            new { operation = "sales.create", status = "Accepted", error = (string?)null, idempotencyKey = "k1" },
            new { operation = "sales.create", status = "Rejected", error = (string?)"conflict", idempotencyKey = "k2" },
        })).EnsureSuccessStatusCode();

        var health = await _owner.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/sync/health", AuthFlows.Json);
        var row = Assert.Single(health!);
        Assert.Equal(1, row.GetProperty("accepted").GetInt32());
        Assert.Equal(1, row.GetProperty("rejected").GetInt32());
        Assert.Equal("Warning", row.GetProperty("severity").GetString());

        // Employees cannot see the surface.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _agent.GetAsync("/api/v1/sync/health")).StatusCode);
    }

    [Fact]
    public async Task Reports_generate_csv_artifacts_into_the_archive_with_audited_download()
    {
        // Give the period some data: one sale today via the API.
        var sale = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/sales",
            new { cid = "12345678", saleType = "Program", campaign = "AS01", amount = 199.99m });
        sale.EnsureSuccessStatusCode();

        var today = new BusinessTime(TimeProvider.System).Today;
        var run = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/reports/run",
            new { reportType = "SalesSummary", periodStart = today, periodEnd = today });
        run.EnsureSuccessStatusCode();
        Assert.True(JsonDocument.Parse(await run.Content.ReadAsStringAsync())
            .RootElement.GetProperty("success").GetBoolean());

        var archive = await _owner.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/archive/", AuthFlows.Json);
        var entry = Assert.Single(archive!);
        var entryId = entry.GetProperty("id").GetGuid();

        var download = await _owner.GetAsync($"/api/v1/archive/{entryId}/download");
        download.EnsureSuccessStatusCode();
        var csv = await download.Content.ReadAsStringAsync();
        Assert.Contains("Agent,Sales,TotalAmount", csv);
        Assert.Contains("199.99", csv);

        Assert.True(await _factory.WithDbAsync(db => db.AuditEvents
            .AnyAsync(a => a.Action == "reports.archiveAccessed")));
    }

    [Fact]
    public async Task Scheduled_reports_compute_business_time_due_dates_and_run_when_due()
    {
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/reports/schedules",
            new { reportType = "SupportTrends", cadence = "Daily", hourLocal = 6 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var scheduleId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Force it due and drive the runner directly.
        await _factory.WithDbAsync(async db =>
            await db.ReportSchedules.Where(s => s.Id == scheduleId)
                .ExecuteUpdateAsync(set => set.SetProperty(
                    s => s.NextDueAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await _factory.WithScopeAsync(async services =>
            _ = await services.GetRequiredService<ReportService>().RunDueSchedulesAsync());

        var schedule = await _factory.WithDbAsync(db => db.ReportSchedules
            .SingleAsync(s => s.Id == scheduleId));
        Assert.NotNull(schedule.LastRunAtUtc);
        Assert.True(schedule.NextDueAtUtc > DateTimeOffset.UtcNow);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.ReportRuns
            .CountAsync(r => r.ScheduleId == scheduleId && r.Success)));
    }

    [Fact]
    public async Task System_health_summary_is_management_only()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _agent.GetAsync("/api/v1/system/health")).StatusCode);

        var health = await _owner.GetFromJsonAsync<JsonElement>(
            "/api/v1/system/health", AuthFlows.Json);
        Assert.True(health.TryGetProperty("outbox", out _));
        Assert.True(health.GetProperty("jobs").GetArrayLength() > 0);
    }
}
