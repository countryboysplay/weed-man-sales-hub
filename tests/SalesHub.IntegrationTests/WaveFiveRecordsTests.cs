using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Contracts.Records;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Employee management records (CLAUDE.md §13) on real PostgreSQL.</summary>
public class WaveFiveRecordsTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private HttpClient _supervisor = null!;
    private Guid _agentId;
    private Guid _supervisorId;
    private const string Password = "wave5-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var agent = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t5-agent", Password, Roles.SalesAgent);
        _agentId = agent.Id;
        var supervisor = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t5-supervisor", Password, Roles.SalesSupervisor);
        _supervisorId = supervisor.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        _supervisor = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(_supervisor, "t5-supervisor", Password);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(Guid Id, string PublicId)> AddNoteAsync(
        HttpClient client, string priority = "Normal", bool requireAck = false,
        IReadOnlyList<Guid>? ackTargets = null)
    {
        var response = await AuthFlows.PostWithCsrfAsync(client,
            $"/api/v1/employees/{_agentId}/notes",
            new CreateNoteRequest("Coaching", priority, "Missed the morning huddle twice.",
                requireAck, ackTargets));
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (root.GetProperty("id").GetGuid(), root.GetProperty("publicId").GetString()!);
    }

    [Fact]
    public async Task Notes_are_management_only_and_high_priority_auto_pins_and_notifies()
    {
        // The employee can never touch their own management record.
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t5-agent", Password);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync($"/api/v1/employees/{_agentId}/management-record")).StatusCode);

        var (_, publicId) = await AddNoteAsync(_supervisor, "High");
        Assert.StartsWith("NOTE-", publicId);

        var record = await _owner.GetFromJsonAsync<EmployeeManagementRecordDto>(
            $"/api/v1/employees/{_agentId}/management-record", AuthFlows.Json);
        var note = Assert.Single(record!.Notes);
        Assert.NotNull(note.PinnedRank);            // High auto-pins
        Assert.Equal("High", note.Priority);

        // High notified the rest of management (owner), not the author.
        Assert.True(await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.Category == "records" && n.UserId != _supervisorId)));
    }

    [Fact]
    public async Task Chronology_is_append_only_reopen_needs_a_reason_and_preserves_resolution()
    {
        var (noteId, _) = await AddNoteAsync(_owner);

        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/notes/{noteId}/followups",
            new FollowupRequest("Spoke with the agent; improvement plan agreed.")))
            .EnsureSuccessStatusCode();
        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/notes/{noteId}/resolve",
            new ResolveNoteRequest("Attendance back to normal for two weeks.")))
            .EnsureSuccessStatusCode();

        // Reopen without a reason refused; with a reason it opens again.
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/notes/{noteId}/reopen", new ReopenNoteRequest(" "))).StatusCode);
        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/notes/{noteId}/reopen",
            new ReopenNoteRequest("Missed again this Monday."))).EnsureSuccessStatusCode();

        var record = await _owner.GetFromJsonAsync<EmployeeManagementRecordDto>(
            $"/api/v1/employees/{_agentId}/management-record", AuthFlows.Json);
        var note = Assert.Single(record!.Notes);
        Assert.Equal("Open", note.Status);
        // The prior resolution survives in the chronology.
        Assert.Contains(note.Followups, f => f.Kind == "Resolution"
            && f.Body.Contains("back to normal"));
        Assert.Contains(note.Followups, f => f.Kind == "Reopen");
        Assert.Equal(3, note.Followups.Count); // followup + resolution + reopen
    }

    [Fact]
    public async Task Acknowledgment_targets_get_required_notifications_and_only_they_can_ack()
    {
        var (noteId, _) = await AddNoteAsync(
            _owner, "Normal", requireAck: true, ackTargets: [_supervisorId]);

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.UserId == _supervisorId && n.Required
                && n.Category == "records")));

        // The owner is not a target — ack refused; the supervisor's lands.
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/notes/{noteId}/acknowledge", new { })).StatusCode);
        (await AuthFlows.PostWithCsrfAsync(_supervisor,
            $"/api/v1/notes/{noteId}/acknowledge", new { })).EnsureSuccessStatusCode();

        var record = await _owner.GetFromJsonAsync<EmployeeManagementRecordDto>(
            $"/api/v1/employees/{_agentId}/management-record", AuthFlows.Json);
        var target = Assert.Single(Assert.Single(record!.Notes).AckTargets);
        Assert.NotNull(target.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Links_validate_targets_and_unlinking_keeps_the_row_with_a_reason()
    {
        var (noteId, _) = await AddNoteAsync(_owner);

        // A well-formed but nonexistent target is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/notes/{noteId}/links",
            new LinkRecordRequest("TECH-2026-99999"))).StatusCode);

        // File a real TECH report to link to.
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t5-agent", Password);
        var filed = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/technical-reports/",
            new SalesHub.Contracts.Workforce.CreateTechnicalReportRequest(
                "Computer", "Laptop rebooted mid-shift", null, null));
        filed.EnsureSuccessStatusCode();
        var techPublicId = JsonDocument.Parse(await filed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("publicId").GetString()!;

        var linked = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/notes/{noteId}/links", new LinkRecordRequest(techPublicId));
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        var linkId = JsonDocument.Parse(await linked.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Unlink demands a reason and preserves the row.
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/notes/links/{linkId}/remove", new UnlinkRecordRequest(""))).StatusCode);
        (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/notes/links/{linkId}/remove",
            new UnlinkRecordRequest("Linked the wrong report"))).EnsureSuccessStatusCode();

        var link = await _factory.WithDbAsync(db => db.RecordLinks.SingleAsync(l => l.Id == linkId));
        Assert.NotNull(link.RemovedAtUtc);
        Assert.Equal("Linked the wrong report", link.RemoveReason);
    }

    [Fact]
    public async Task Tags_are_one_shared_library_with_unique_labels()
    {
        var created = await AuthFlows.PostWithCsrfAsync(_owner,
            "/api/v1/management-tags/", new CreateTagRequest("Attendance Watch"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tagId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PostWithCsrfAsync(_owner,
            "/api/v1/management-tags/", new CreateTagRequest("Attendance Watch"))).StatusCode);

        var (_, notePublicId) = await AddNoteAsync(_supervisor);
        (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/management-tags/{tagId}/apply",
            new TagEntityRequest(notePublicId))).EnsureSuccessStatusCode();

        var record = await _owner.GetFromJsonAsync<EmployeeManagementRecordDto>(
            $"/api/v1/employees/{_agentId}/management-record", AuthFlows.Json);
        Assert.Contains("Attendance Watch", Assert.Single(record!.Notes).Tags);
    }
}
