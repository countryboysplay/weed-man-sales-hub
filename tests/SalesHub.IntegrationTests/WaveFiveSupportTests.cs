using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Contracts.Support;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Support tickets (CLAUDE.md §14) on real PostgreSQL.</summary>
public class WaveFiveSupportTests : IAsyncLifetime
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
            _factory, "t5s-agent", Password, Roles.SalesAgent);
        _agentId = agent.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        _agent = await AuthFlows.WorkingClientAsync(_factory, "t5s-agent", Password);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<CreateTicketResponse> CreateTicketAsync(
        string description = "The sales page will not load for me")
    {
        var response = await AuthFlows.PostWithCsrfAsync(_agent, "/api/v1/support/",
            new CreateSupportTicketRequest("BrowserPwa", description, "/sales", "1.0", null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateTicketResponse>(AuthFlows.Json))!;
    }

    [Fact]
    public async Task Context_is_captured_server_side_and_diagnostics_are_manager_only()
    {
        var created = await CreateTicketAsync();
        Assert.StartsWith("SUP-", created.PublicId);

        // Server captured browser/device/correlation from the session, not the body.
        var stored = await _factory.WithDbAsync(db => db.SupportTickets
            .SingleAsync(t => t.Id == created.Id));
        Assert.NotEqual("", stored.CorrelationId);

        // The reporter's detail view has no diagnostics block; the owner's does.
        var mine = await _agent.GetFromJsonAsync<SupportTicketDto>(
            $"/api/v1/support/{created.Id}", AuthFlows.Json);
        Assert.Null(mine!.Diagnostics);
        var owners = await _owner.GetFromJsonAsync<SupportTicketDto>(
            $"/api/v1/support/{created.Id}", AuthFlows.Json);
        Assert.NotNull(owners!.Diagnostics);

        // Management was notified of the new ticket.
        Assert.True(await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.Category == "support" && n.ReferenceId == created.PublicId)));
    }

    [Fact]
    public async Task Critical_wording_suggests_priority_and_similar_tickets_surface()
    {
        var first = await CreateTicketAsync("Everyone is locked out, nobody can log in");
        Assert.Equal("Critical", first.SuggestedPriority);

        var second = await CreateTicketAsync("Another BrowserPwa problem on my machine");
        Assert.Contains(second.SimilarTickets, s => s.PublicId == first.PublicId);
    }

    [Fact]
    public async Task Internal_notes_never_reach_the_reporter()
    {
        var created = await CreateTicketAsync();

        // The reporter cannot write internal notes.
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/support/{created.Id}/replies",
            new SupportReplyRequest("sneaky", "InternalNote"))).StatusCode);

        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/support/{created.Id}/replies",
            new SupportReplyRequest("Reporter has a history of browser issues.", "InternalNote")))
            .EnsureSuccessStatusCode();
        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/support/{created.Id}/replies",
            new SupportReplyRequest("Please clear your cache and retry.", "EmployeeReply")))
            .EnsureSuccessStatusCode();

        var mine = await _agent.GetFromJsonAsync<SupportTicketDto>(
            $"/api/v1/support/{created.Id}", AuthFlows.Json);
        var visible = Assert.Single(mine!.Messages);
        Assert.Equal("EmployeeReply", visible.Visibility);
        Assert.DoesNotContain("history", visible.Body);

        // Management sees the full chronology, and the reply moved the state.
        var owners = await _owner.GetFromJsonAsync<SupportTicketDto>(
            $"/api/v1/support/{created.Id}", AuthFlows.Json);
        Assert.Equal(2, owners!.Messages.Count);
        Assert.Equal("WaitingOnUser", owners.Status);
    }

    [Fact]
    public async Task Lifecycle_resolve_then_reporter_confirms_closure_reopen_keeps_id()
    {
        var created = await CreateTicketAsync();

        // Reporter cannot confirm closure before Resolved.
        Assert.Equal(HttpStatusCode.Conflict, (await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/support/{created.Id}/confirm-closure", new { })).StatusCode);

        (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/support/{created.Id}/resolve", new { })).EnsureSuccessStatusCode();
        (await AuthFlows.PostWithCsrfAsync(_agent,
            $"/api/v1/support/{created.Id}/confirm-closure", new { })).EnsureSuccessStatusCode();

        var closed = await _factory.WithDbAsync(db => db.SupportTickets
            .SingleAsync(t => t.Id == created.Id));
        Assert.True(closed.ReporterConfirmedClosure);

        // Reopen keeps the original SUP id.
        (await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/support/{created.Id}/reopen", new { })).EnsureSuccessStatusCode();
        var reopened = await _factory.WithDbAsync(db => db.SupportTickets
            .SingleAsync(t => t.Id == created.Id));
        Assert.Equal(created.PublicId, reopened.PublicId);
        Assert.Equal("InProgress", reopened.Status.ToString());
    }

    [Fact]
    public async Task Queue_is_management_only_and_strangers_cannot_read_others_tickets()
    {
        var created = await CreateTicketAsync();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _agent.GetAsync("/api/v1/support/queue")).StatusCode);

        var queue = await _owner.GetFromJsonAsync<List<SupportTicketSummaryDto>>(
            "/api/v1/support/queue", AuthFlows.Json);
        Assert.Contains(queue!, t => t.PublicId == created.PublicId);

        // Another agent gets 404 on someone else's ticket — no existence oracle.
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t5s-other", Password, Roles.SalesAgent);
        var other = await AuthFlows.WorkingClientAsync(_factory, "t5s-other", Password);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/v1/support/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Assignment_and_priority_override_are_recorded()
    {
        var created = await CreateTicketAsync();
        var ownerId = await _factory.WithDbAsync(db => db.Users
            .Where(u => u.UserName == SalesHubApiFactory.OwnerUsername)
            .Select(u => u.Id).SingleAsync());

        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/support/{created.Id}/assign",
            new AssignTicketRequest(ownerId))).EnsureSuccessStatusCode();
        (await AuthFlows.PostWithCsrfAsync(_owner, $"/api/v1/support/{created.Id}/priority",
            new SetTicketPriorityRequest("High"))).EnsureSuccessStatusCode();

        var ticket = await _owner.GetFromJsonAsync<SupportTicketDto>(
            $"/api/v1/support/{created.Id}", AuthFlows.Json);
        Assert.Equal("High", ticket!.Priority);
        Assert.Equal("Normal", ticket.SuggestedPriority); // the suggestion is preserved
        Assert.Equal(ownerId, ticket.PrimaryAssigneeUserId);
        Assert.Equal("InProgress", ticket.Status);
    }
}
