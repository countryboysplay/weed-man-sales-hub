using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Application.Presence;
using SalesHub.Contracts.Auth;
using SalesHub.Contracts.Presence;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>
/// Wave 4 part 1: manual presence status, DND behavior, the derived
/// directory, shifts and schedule exceptions, and the presence evaluator
/// (CLAUDE.md §12) on real PostgreSQL.
/// </summary>
public class WaveFourPresenceTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _agentAId;
    private Guid _agentBId;
    private const string Password = "wave4-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var a = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t4-agent-a", Password, Roles.SalesAgent);
        var b = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t4-agent-b", Password, Roles.SalesAgent);
        _agentAId = a.Id;
        _agentBId = b.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ── manual status ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Manual_status_persists_caps_the_message_and_shows_in_the_directory()
    {
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t4-agent-a", Password);

        // 36 characters: rejected.
        var tooLong = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/presence/status",
            new SetPresenceStatusRequest("Busy", new string('x', 36)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var ok = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/presence/status",
            new SetPresenceStatusRequest("Busy", "Door knocking until 3"));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var directory = await _owner.GetFromJsonAsync<List<PresenceDirectoryEntryDto>>(
            "/api/v1/presence/directory", AuthFlows.Json);
        var entry = Assert.Single(directory!, e => e.UserId == _agentAId);
        Assert.Equal("Busy", entry.State);
        Assert.Equal("Door knocking until 3", entry.CustomMessage);

        // Agent B never signed in: the server derives Offline, whatever the
        // manual status says.
        var offline = Assert.Single(directory!, e => e.UserId == _agentBId);
        Assert.Equal("Offline", offline.State);
    }

    [Fact]
    public async Task Idle_detector_transitions_derive_Away_never_the_client_word()
    {
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t4-agent-a", Password);

        var heartbeat = await AuthFlows.PostWithCsrfAsync(agent,
            "/api/v1/auth/idle-capability/heartbeat",
            new IdleHeartbeatRequest("idle", "unlocked", "hidden", null, "test"));
        heartbeat.EnsureSuccessStatusCode();

        var directory = await _owner.GetFromJsonAsync<List<PresenceDirectoryEntryDto>>(
            "/api/v1/presence/directory", AuthFlows.Json);
        Assert.Equal("Away", Assert.Single(directory!, e => e.UserId == _agentAId).State);

        // Active again → Available.
        _ = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/auth/idle-capability/heartbeat",
            new IdleHeartbeatRequest("active", "unlocked", "visible", null, "test"));
        directory = await _owner.GetFromJsonAsync<List<PresenceDirectoryEntryDto>>(
            "/api/v1/presence/directory", AuthFlows.Json);
        Assert.Equal("Available", Assert.Single(directory!, e => e.UserId == _agentAId).State);
    }

    [Fact]
    public async Task Leaving_DND_delivers_one_catchup_summary_with_counts_not_content()
    {
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t4-agent-a", Password);
        var entered = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/presence/status",
            new SetPresenceStatusRequest("Dnd", null));
        Assert.Equal(HttpStatusCode.NoContent, entered.StatusCode);

        // Two notifications land while on DND.
        await _factory.WithScopeAsync(async services =>
        {
            var notifications = services.GetRequiredService<NotificationService>();
            var db = services.GetRequiredService<IAppDb>();
            _ = await notifications.CreateAsync(_agentAId, new NotificationService.NewNotification(
                "chat", "New message", "Alice sent a message"));
            _ = await notifications.CreateAsync(_agentAId, new NotificationService.NewNotification(
                "tasks", "Task assigned", "Call-back sweep"));
            await db.SaveChangesAsync();
        });

        var exited = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/presence/status",
            new SetPresenceStatusRequest("Available", null));
        Assert.Equal(HttpStatusCode.NoContent, exited.StatusCode);

        var catchUp = await _factory.WithDbAsync(db => db.Notifications
            .SingleAsync(n => n.UserId == _agentAId && n.Category == "presence"));
        Assert.Contains("2 notifications", catchUp.SafePreview);
        Assert.Contains("1 chat", catchUp.SafePreview);
        Assert.Contains("1 tasks", catchUp.SafePreview);
        // Counts only — never the content of what arrived.
        Assert.DoesNotContain("Alice", catchUp.SafePreview);
    }

    // ── shifts + evaluator ────────────────────────────────────────────────────

    private async Task<Guid> AssignTodayShiftAsync(Guid userId)
    {
        var businessTime = new BusinessTime(TimeProvider.System);
        var dayOfWeek = businessTime.ToLocal(businessTime.UtcNow).DayOfWeek;
        var today = businessTime.Today;

        var template = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/shifts/templates",
            new CreateShiftTemplateRequest(
                $"Day shift {Guid.NewGuid():N}", Roles.SalesAgent, dayOfWeek.ToString(),
                new TimeOnly(0, 0), new TimeOnly(23, 59)));
        template.EnsureSuccessStatusCode();
        var templateId = JsonDocument.Parse(await template.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var assigned = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/shifts/assignments",
            new AssignShiftRequest(userId, templateId, today, null));
        assigned.EnsureSuccessStatusCode();
        return templateId;
    }

    private async Task<int> RunEvaluatorAsync()
    {
        var raised = 0;
        await _factory.WithScopeAsync(async services =>
        {
            // Zero grace so the test does not wait ten real minutes.
            var db = services.GetRequiredService<IAppDb>();
            await db.PresenceRuleSets
                .Where(r => r.Role == Roles.SalesAgent)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.LateStartGraceMinutes, 0));
            raised = await services.GetRequiredService<PresenceEvaluator>().EvaluateAsync();
        });
        return raised;
    }

    [Fact]
    public async Task Evaluator_raises_LateStart_once_with_a_PRS_id_and_writes_segments()
    {
        await AssignTodayShiftAsync(_agentBId); // agent B never signs in

        var first = await RunEvaluatorAsync();
        Assert.True(first >= 1);

        var flag = await _factory.WithDbAsync(db => db.PresenceFlags
            .SingleAsync(f => f.UserId == _agentBId && f.Category == "LateStart"));
        Assert.StartsWith("PRS-", flag.PublicId);
        Assert.True(PublicRecordId.IsWellFormed(flag.PublicId));
        Assert.Equal(PresenceFlagSeverity.Warning, flag.Severity);
        Assert.Equal(PresenceFlagStatus.Open, flag.Status);

        // Idempotent: a second pass never duplicates the day's flag.
        _ = await RunEvaluatorAsync();
        Assert.Equal(1, await _factory.WithDbAsync(db => db.PresenceFlags
            .CountAsync(f => f.UserId == _agentBId && f.Category == "LateStart")));

        // The timeline records one open Offline segment, not one per pass.
        var segments = await _factory.WithDbAsync(db => db.PresenceSegments
            .Where(s => s.UserId == _agentBId).ToListAsync());
        var open = Assert.Single(segments, s => s.EndAtUtc == null);
        Assert.Equal(PresenceSegmentState.Offline, open.State);
    }

    [Fact]
    public async Task Suspending_schedule_exception_suppresses_flags_and_ack_flow_works()
    {
        await AssignTodayShiftAsync(_agentAId);

        var today = new BusinessTime(TimeProvider.System).Today;
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/schedule-exceptions/",
            new CreateScheduleExceptionRequest(
                _agentAId, today, null, null, "Field training", "Off-site with manager",
                SuspendsPresence: true, AcknowledgmentRequired: true, AcknowledgeByUtc: null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var exception = (await created.Content.ReadFromJsonAsync<ScheduleExceptionDto>(AuthFlows.Json))!;
        Assert.StartsWith("SCH-", exception.PublicId);

        // Covered: the evaluator raises nothing for agent A today.
        _ = await RunEvaluatorAsync();
        Assert.Equal(0, await _factory.WithDbAsync(db => db.PresenceFlags
            .CountAsync(f => f.UserId == _agentAId)));

        // The employee got a required notification and can acknowledge; a
        // different employee gets 404, not somebody else's record.
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.UserId == _agentAId && n.Category == "schedule" && n.Required)));

        var stranger = await AuthFlows.WorkingClientAsync(_factory, "t4-agent-b", Password);
        Assert.Equal(HttpStatusCode.NotFound, (await AuthFlows.PostWithCsrfAsync(stranger,
            $"/api/v1/schedule-exceptions/{exception.Id}/acknowledge", new { })).StatusCode);

        var agent = await AuthFlows.WorkingClientAsync(_factory, "t4-agent-a", Password);
        Assert.Equal(HttpStatusCode.NoContent, (await AuthFlows.PostWithCsrfAsync(agent,
            $"/api/v1/schedule-exceptions/{exception.Id}/acknowledge", new { })).StatusCode);
        Assert.NotNull(await _factory.WithDbAsync(db => db.ScheduleExceptions
            .Where(x => x.Id == exception.Id).Select(x => x.AcknowledgedAtUtc).SingleAsync()));
    }

    [Fact]
    public async Task Alerts_are_rank_shaped_summary_for_supervisors_detail_for_owners()
    {
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t4-supervisor", Password, Roles.SalesSupervisor);
        await AssignTodayShiftAsync(_agentBId);
        _ = await RunEvaluatorAsync();

        var supervisor = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(supervisor, "t4-supervisor", Password);
        var summary = await supervisor.GetFromJsonAsync<PresenceAlertSummaryDto>(
            "/api/v1/presence/alerts", AuthFlows.Json);
        Assert.Equal(1, summary!.OpenWarnings);

        var detail = await _owner.GetFromJsonAsync<List<PresenceFlagDto>>(
            "/api/v1/presence/alerts", AuthFlows.Json);
        var flag = Assert.Single(detail!);
        Assert.Equal(_agentBId, flag.UserId);
        Assert.Equal("LateStart", flag.Category);

        // Agents cannot see the alert surface at all.
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t4-agent-a", Password);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync("/api/v1/presence/alerts")).StatusCode);

        // Manager+ resolves the flag; it leaves the open counts.
        var resolved = await AuthFlows.PatchWithCsrfAsync(_owner,
            $"/api/v1/presence/flags/{flag.Id}", new ResolvePresenceFlagRequest("resolve"));
        Assert.Equal(HttpStatusCode.NoContent, resolved.StatusCode);
        summary = await supervisor.GetFromJsonAsync<PresenceAlertSummaryDto>(
            "/api/v1/presence/alerts", AuthFlows.Json);
        Assert.Equal(0, summary!.OpenWarnings);
        Assert.Equal(1, summary.ResolvedToday);
    }

    [Fact]
    public async Task My_presence_shows_manual_status_derived_state_and_todays_timeline()
    {
        await AssignTodayShiftAsync(_agentAId);
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t4-agent-a", Password);
        _ = await RunEvaluatorAsync();

        var me = await agent.GetFromJsonAsync<MyPresenceDto>("/api/v1/presence/me", AuthFlows.Json);
        Assert.Equal("Available", me!.Status);
        Assert.Equal("Available", me.DerivedState);
        Assert.Contains(me.Today, s => s.State == "Available" && s.EndAtUtc == null);
    }
}
