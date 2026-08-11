using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Contracts.Work;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.TestSupport;
using SalesHub.Workers;
using SalesHub.Workers.Jobs;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Tasks (CLAUDE.md §9) and Recognitions (§13) on real PostgreSQL.</summary>
public class WaveThreeTasksRecognitionsTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _agentAId;
    private Guid _agentBId;
    private const string Password = "wave3-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var a = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t3-agent-a", Password, Roles.SalesAgent);
        var b = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t3-agent-b", Password, Roles.SalesAgent);
        _agentAId = a.Id;
        _agentBId = b.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Each_assignee_gets_an_independent_copy_and_completion_is_per_person()
    {
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/tasks/",
            new CreateTaskRequest("Call-back sweep", "Work the list.", "High",
                DueAt: DateTimeOffset.UtcNow.AddHours(4),
                AssigneeUserIds: [_agentAId, _agentBId]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var definitionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var alice = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(alice, "t3-agent-a", Password);
        var myTasks = await alice.GetFromJsonAsync<List<TaskInstanceDto>>(
            "/api/v1/tasks/my", AuthFlows.Json);
        var mine = Assert.Single(myTasks!);

        // Alice cannot complete Bob's instance.
        var bobInstance = await _factory.WithDbAsync(db => db.TaskInstances
            .SingleAsync(t => t.DefinitionId == definitionId && t.AssigneeUserId == _agentBId));
        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthFlows.PostWithCsrfAsync(alice,
                $"/api/v1/tasks/{bobInstance.Id}/complete", new { })).StatusCode);

        // Completing hers clears her active list, not Bob's.
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(alice,
                $"/api/v1/tasks/{mine.InstanceId}/complete", new { })).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<List<TaskInstanceDto>>(
            "/api/v1/tasks/my", AuthFlows.Json))!);

        // Management still sees both in history: 1 of 2 complete.
        var progress = await _owner.GetFromJsonAsync<TaskProgressResponse>(
            $"/api/v1/tasks/definitions/{definitionId}/progress", AuthFlows.Json);
        Assert.Equal(2, progress!.Assigned);
        Assert.Equal(1, progress.Completed);
        Assert.Equal(50, progress.Percent);

        // Both assignees were notified on assignment.
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.Category == "tasks" && n.Title.Contains("Call-back sweep"))));
    }

    [Fact]
    public async Task Agents_cannot_create_tasks_and_comments_notify_mentions()
    {
        var alice = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(alice, "t3-agent-a", Password);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(alice, "/api/v1/tasks/",
                new CreateTaskRequest("Self-assigned", AssigneeUserIds: [_agentAId]))).StatusCode);

        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/tasks/",
            new CreateTaskRequest("Inventory check", AssigneeUserIds: [_agentAId]));
        var definitionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        var instance = await _factory.WithDbAsync(db => db.TaskInstances
            .SingleAsync(t => t.DefinitionId == definitionId));

        // Agent comments and mentions agent B (a member of nothing — mentions
        // are explicit); B gets the notification.
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(alice, $"/api/v1/tasks/{instance.Id}/comments",
                new TaskCommentRequest("Done with aisle 3 @bob", [_agentBId]))).StatusCode);
        Assert.True(await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.UserId == _agentBId && n.Title.StartsWith("Comment on:"))));

        // Another agent cannot comment on Alice's instance.
        var bob = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(bob, "t3-agent-b", Password);
        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthFlows.PostWithCsrfAsync(bob, $"/api/v1/tasks/{instance.Id}/comments",
                new TaskCommentRequest("intruding"))).StatusCode);
    }

    [Fact]
    public async Task Recurrence_generates_next_period_instances_exactly_once()
    {
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/tasks/",
            new CreateTaskRequest("Daily standup notes", Recurrence: "Daily",
                AssigneeUserIds: [_agentAId]));
        var definitionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Pretend today's instance belongs to a long-past period. (Not
        // "UTC yesterday" — that can still be today's business date in
        // America/Chicago and would collide with the current period.)
        await _factory.WithDbAsync(db => db.TaskInstances
            .Where(t => t.DefinitionId == definitionId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.PeriodKey, "2000-01-01")));

        var job = _factory.Services.GetServices<IScheduledJobHandler>()
            .OfType<WorkMaintenanceJob>().Single();
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None); // idempotent re-run

        Assert.Equal(2, await _factory.WithDbAsync(db => db.TaskInstances
            .CountAsync(t => t.DefinitionId == definitionId)));
    }

    [Fact]
    public async Task Overdue_reminders_fire_once_per_day_for_active_instances_only()
    {
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/tasks/",
            new CreateTaskRequest("Late report", DueAt: DateTimeOffset.UtcNow.AddHours(-2),
                AssigneeUserIds: [_agentAId]));
        _ = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        var job = _factory.Services.GetServices<IScheduledJobHandler>()
            .OfType<WorkMaintenanceJob>().Single();
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None); // same day: no second nag

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.Title.StartsWith("Overdue:"))));
    }

    [Fact]
    public async Task Recognitions_are_management_issued_everyone_reacts_and_the_feed_archives_at_30_days()
    {
        var alice = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(alice, "t3-agent-a", Password);

        // Built-in badge library is seeded.
        var badges = await alice.GetFromJsonAsync<List<BadgeDto>>(
            "/api/v1/recognition-badges/", AuthFlows.Json);
        Assert.Contains(badges!, b => b.Name == "Top Performer" && b.BuiltIn);

        // Agents cannot issue recognitions.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(alice, "/api/v1/recognitions/",
                new IssueRecognitionRequest(_agentBId, badges![0].Id))).StatusCode);

        // Management issues one; the recipient is notified; everyone can react/comment.
        var issued = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/recognitions/",
            new IssueRecognitionRequest(_agentAId, badges[0].Id, "Outstanding Performance",
                "Crushed the aeration push!"));
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var recognitionId = JsonDocument.Parse(await issued.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        Assert.True(await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.UserId == _agentAId && n.Category == "recognitions")));

        var bob = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(bob, "t3-agent-b", Password);
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(bob,
                $"/api/v1/recognitions/{recognitionId}/reactions", new { reaction = "🔥" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(bob,
                $"/api/v1/recognitions/{recognitionId}/comments",
                new RecognitionCommentRequest("Way to go!"))).StatusCode);

        var feed = await bob.GetFromJsonAsync<List<RecognitionDto>>(
            "/api/v1/recognitions/?archived=false", AuthFlows.Json);
        var item = Assert.Single(feed!);
        Assert.Equal(1, item.Reactions["🔥"]);
        Assert.Equal(1, item.CommentCount);

        // Age it past 30 days; the job archives it out of the active feed.
        await _factory.WithDbAsync(db => db.Recognitions
            .Where(r => r.Id == recognitionId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                r => r.ActiveUntilUtc, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var job = _factory.Services.GetServices<IScheduledJobHandler>()
            .OfType<WorkMaintenanceJob>().Single();
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Empty((await bob.GetFromJsonAsync<List<RecognitionDto>>(
            "/api/v1/recognitions/?archived=false", AuthFlows.Json))!);
        Assert.Single((await bob.GetFromJsonAsync<List<RecognitionDto>>(
            "/api/v1/recognitions/?archived=true", AuthFlows.Json))!);
    }
}
