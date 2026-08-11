using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Contracts.Chat;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Chat rules (CLAUDE.md §7) on real PostgreSQL.</summary>
public class WaveThreeChatTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private const string Password = "wave3-password-1";

    public Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(HttpClient Client, Guid UserId)> UserAsync(string name, string role)
    {
        var user = await AuthFlows.CreateUserAsUnownedAdminAsync(_factory, name, Password, role);
        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(client, name, Password);
        return (client, user.Id);
    }

    [Fact]
    public async Task Dms_are_get_or_create_and_reach_both_sides()
    {
        var (alice, aliceId) = await UserAsync("c3-alice", Roles.SalesAgent);
        var (bob, bobId) = await UserAsync("c3-bob", Roles.SalesAgent);

        var first = await AuthFlows.PostWithCsrfAsync(alice, "/api/v1/conversations/direct",
            new StartDirectRequest(bobId));
        var conversationId = JsonDocument.Parse(await first.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();

        // Bob starting the same DM lands in the same conversation.
        var second = await AuthFlows.PostWithCsrfAsync(bob, "/api/v1/conversations/direct",
            new StartDirectRequest(aliceId));
        var sameId = JsonDocument.Parse(await second.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();
        Assert.Equal(conversationId, sameId);

        _ = await AuthFlows.PostWithCsrfAsync(alice,
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest("Hey Bob!"));

        var bobView = await bob.GetFromJsonAsync<List<ConversationDto>>(
            "/api/v1/conversations/", AuthFlows.Json);
        var dm = Assert.Single(bobView!);
        Assert.Equal("Hey Bob!", dm.LastMessage!.Body);
        Assert.Equal(1, dm.UnreadCount);

        // The DM recipient gets a durable notification.
        var notified = await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.UserId == bobId && n.Category == "chat"));
        Assert.True(notified);
    }

    [Fact]
    public async Task Groups_are_management_made_unleaveable_and_mandatory_unmutable()
    {
        var (agent, agentId) = await UserAsync("c3-agent", Roles.SalesAgent);
        var owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        // Agents cannot create groups.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/conversations/groups",
                new CreateGroupRequest("Rogue Group", [agentId]))).StatusCode);

        // Management creates a mandatory group.
        var created = await AuthFlows.PostWithCsrfAsync(owner, "/api/v1/conversations/groups",
            new CreateGroupRequest("All Hands", [agentId], Mandatory: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var groupId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();

        // Mandatory group cannot be muted.
        var mute = await AuthFlows.PostWithCsrfAsync(agent,
            $"/api/v1/conversations/{groupId}/mute", new { });
        Assert.Equal(HttpStatusCode.Forbidden, mute.StatusCode);
        Assert.Contains("mandatoryGroup", await mute.Content.ReadAsStringAsync());

        // There is no self-leave route at all; only management edits members.
        var demote = await AuthFlows.PatchWithCsrfAsync(owner,
            $"/api/v1/conversations/groups/{groupId}",
            new UpdateGroupRequest(null, false, null, null));
        Assert.Equal(HttpStatusCode.NoContent, demote.StatusCode);

        // Non-mandatory now: muting works.
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(agent,
                $"/api/v1/conversations/{groupId}/mute", new { })).StatusCode);
    }

    [Fact]
    public async Task Everyone_mentions_are_management_only()
    {
        var (agent, agentId) = await UserAsync("c3-shout", Roles.SalesAgent);
        var owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        var created = await AuthFlows.PostWithCsrfAsync(owner, "/api/v1/conversations/groups",
            new CreateGroupRequest("Team", [agentId]));
        var groupId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();

        var refused = await AuthFlows.PostWithCsrfAsync(agent,
            $"/api/v1/conversations/{groupId}/messages",
            new SendMessageRequest("look here", MentionEveryone: true));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        Assert.Equal(HttpStatusCode.Created,
            (await AuthFlows.PostWithCsrfAsync(owner,
                $"/api/v1/conversations/{groupId}/messages",
                new SendMessageRequest("All hands at 3", MentionEveryone: true))).StatusCode);

        // The @everyone mention lands as a durable notification for the agent.
        var notified = await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.UserId == agentId && n.Category == "chat"));
        Assert.True(notified);
    }

    [Fact]
    public async Task Deleting_a_message_erases_the_body_everywhere_forever()
    {
        var (alice, _) = await UserAsync("c3-del-a", Roles.SalesAgent);
        var (bob, bobId) = await UserAsync("c3-del-b", Roles.SalesAgent);
        var direct = await AuthFlows.PostWithCsrfAsync(alice, "/api/v1/conversations/direct",
            new StartDirectRequest(bobId));
        var conversationId = JsonDocument.Parse(await direct.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();

        var sent = await AuthFlows.PostWithCsrfAsync(alice,
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest("this will vanish"));
        var messageId = JsonDocument.Parse(await sent.Content.ReadAsStringAsync())
            .RootElement.GetProperty("messageId").GetGuid();

        // Bob cannot delete Alice's message; Alice can.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.DeleteWithCsrfAsync(bob, $"/api/v1/messages/{messageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.DeleteWithCsrfAsync(alice, $"/api/v1/messages/{messageId}")).StatusCode);

        // Canonical state: shell remains, content is GONE — not in the message
        // row, and nowhere in the audit stream either (CLAUDE.md §7).
        var row = await _factory.WithDbAsync(db => db.Messages.SingleAsync(m => m.Id == messageId));
        Assert.Equal(string.Empty, row.Body);
        Assert.NotNull(row.DeletedAtUtc);
        var auditBodies = await _factory.WithDbAsync(db => db.AuditEvents
            .Select(a => new { a.BeforeJson, a.AfterJson })
            .ToListAsync());
        Assert.DoesNotContain(auditBodies, a =>
            (a.BeforeJson ?? "").Contains("this will vanish")
            || (a.AfterJson ?? "").Contains("this will vanish"));

        // Readers see a deleted shell with no content.
        var page = await bob.GetFromJsonAsync<MessagePageResponse>(
            $"/api/v1/conversations/{conversationId}/messages", AuthFlows.Json);
        var dto = Assert.Single(page!.Messages);
        Assert.True(dto.Deleted);
        Assert.Equal(string.Empty, dto.Body);
    }

    [Fact]
    public async Task Edits_are_own_only_unlimited_and_flagged()
    {
        var (alice, _) = await UserAsync("c3-edit-a", Roles.SalesAgent);
        var (bob, bobId) = await UserAsync("c3-edit-b", Roles.SalesAgent);
        var direct = await AuthFlows.PostWithCsrfAsync(alice, "/api/v1/conversations/direct",
            new StartDirectRequest(bobId));
        var conversationId = JsonDocument.Parse(await direct.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();
        var sent = await AuthFlows.PostWithCsrfAsync(alice,
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest("typo"));
        var messageId = JsonDocument.Parse(await sent.Content.ReadAsStringAsync())
            .RootElement.GetProperty("messageId").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PatchWithCsrfAsync(bob, $"/api/v1/messages/{messageId}",
                new EditMessageRequest("hijack"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PatchWithCsrfAsync(alice, $"/api/v1/messages/{messageId}",
                new EditMessageRequest("fixed"))).StatusCode);

        var page = await bob.GetFromJsonAsync<MessagePageResponse>(
            $"/api/v1/conversations/{conversationId}/messages", AuthFlows.Json);
        Assert.Equal("fixed", page!.Messages[0].Body);
        Assert.NotNull(page.Messages[0].EditedAt); // renders "Edited"
    }

    [Fact]
    public async Task Read_receipts_and_reactions_flow_and_nonmembers_are_blocked()
    {
        var (alice, _) = await UserAsync("c3-rr-a", Roles.SalesAgent);
        var (bob, bobId) = await UserAsync("c3-rr-b", Roles.SalesAgent);
        var (mallory, _) = await UserAsync("c3-rr-m", Roles.SalesAgent);
        var direct = await AuthFlows.PostWithCsrfAsync(alice, "/api/v1/conversations/direct",
            new StartDirectRequest(bobId));
        var conversationId = JsonDocument.Parse(await direct.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();
        var sent = await AuthFlows.PostWithCsrfAsync(alice,
            $"/api/v1/conversations/{conversationId}/messages", new SendMessageRequest("hi"));
        var messageId = JsonDocument.Parse(await sent.Content.ReadAsStringAsync())
            .RootElement.GetProperty("messageId").GetGuid();

        // Nonmember: no reading, no reacting, no sending.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await mallory.GetAsync($"/api/v1/conversations/{conversationId}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(mallory,
                $"/api/v1/messages/{messageId}/reactions", new ReactionRequest("🔥"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(mallory,
                $"/api/v1/conversations/{conversationId}/messages",
                new SendMessageRequest("let me in"))).StatusCode);

        // Bob reads and reacts.
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(bob,
                $"/api/v1/conversations/{conversationId}/read",
                new MarkReadRequest(messageId))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(bob,
                $"/api/v1/messages/{messageId}/reactions", new ReactionRequest("🔥"))).StatusCode);

        var view = await alice.GetFromJsonAsync<List<ConversationDto>>(
            "/api/v1/conversations/", AuthFlows.Json);
        var dm = Assert.Single(view!);
        Assert.Equal(messageId, dm.Members.Single(m => m.UserId == bobId).LastReadMessageId);
        Assert.Equal(1, dm.LastMessage!.Reactions["🔥"]);

        // Bob's unread count went to zero.
        var bobView = await bob.GetFromJsonAsync<List<ConversationDto>>(
            "/api/v1/conversations/", AuthFlows.Json);
        Assert.Equal(0, bobView!.Single().UnreadCount);
    }
}
