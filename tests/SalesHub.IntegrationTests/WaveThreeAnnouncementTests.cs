using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Contracts.Announcements;
using SalesHub.Domain;
using SalesHub.TestSupport;
using SalesHub.Workers;
using SalesHub.Workers.Jobs;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Announcement rules (CLAUDE.md §8) on real PostgreSQL.</summary>
public class WaveThreeAnnouncementTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private const string Password = "wave3-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        _owner = _factory.CreateCookieClient();
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "a3-agent", Password, Roles.SalesAgent);
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<Guid> PublishAsync(CreateAnnouncementRequest request)
    {
        var response = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/announcements/", request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Publishing_expands_targets_and_notifies_with_required_flag()
    {
        var id = await PublishAsync(new CreateAnnouncementRequest(
            "Storm protocol", "Read this.", "High", RequireAcknowledgment: true));

        // Targets expanded to every active user; the agent counts toward
        // completion, management does not.
        var targets = await _factory.WithDbAsync(db =>
            db.AnnouncementTargets.Where(t => t.AnnouncementId == id).ToListAsync());
        Assert.Equal(2, targets.Count);
        Assert.Equal(1, targets.Count(t => t.CountsTowardCompletion));

        var required = await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.Category == "announcements" && n.Required));
        Assert.Equal(2, required);
    }

    [Fact]
    public async Task Seen_and_acknowledged_are_tracked_separately_and_complete_at_100_percent()
    {
        var id = await PublishAsync(new CreateAnnouncementRequest(
            "Q3 kickoff", "Details.", "High", RequireAcknowledgment: true));

        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "a3-agent", Password);

        _ = await AuthFlows.PostWithCsrfAsync(agent, $"/api/v1/announcements/{id}/seen", new { });
        var afterSeen = await _owner.GetFromJsonAsync<AnnouncementProgressResponse>(
            $"/api/v1/announcements/{id}/progress", AuthFlows.Json);
        Assert.Equal(1, afterSeen!.Seen);
        Assert.Equal(0, afterSeen.Acknowledged);
        Assert.Equal(0, afterSeen.Percent);   // ack required: seen is not done

        _ = await AuthFlows.PostWithCsrfAsync(agent, $"/api/v1/announcements/{id}/acknowledge", new { });
        var afterAck = await _owner.GetFromJsonAsync<AnnouncementProgressResponse>(
            $"/api/v1/announcements/{id}/progress", AuthFlows.Json);
        Assert.Equal(100, afterAck!.Percent);
        Assert.Empty(afterAck.OutstandingUserIds);

        // 100% notifies management with the exact Central time.
        var completion = await _factory.WithDbAsync(db => db.Notifications
            .SingleAsync(n => n.Title == "Announcement fully acknowledged"));
        Assert.Contains("Central", completion.SafePreview);
    }

    [Fact]
    public async Task Pinning_caps_at_three_and_the_job_releases_expired_pins()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await PublishAsync(new CreateAnnouncementRequest($"Pin {i}", "x")));
        }

        foreach (var id in ids.Take(3))
        {
            Assert.Equal(HttpStatusCode.NoContent,
                (await AuthFlows.PostWithCsrfAsync(_owner,
                    $"/api/v1/announcements/{id}/pin", new { })).StatusCode);
        }

        // The fourth pin is refused.
        var fourth = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/announcements/{ids[3]}/pin", new { });
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Contains("pinLimit", await fourth.Content.ReadAsStringAsync());

        // Age one pin past seven days; the job unpins but keeps it active.
        await _factory.WithDbAsync(db => db.Announcements
            .Where(a => a.Id == ids[0])
            .ExecuteUpdateAsync(s => s.SetProperty(
                a => a.AutoUnpinAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var job = _factory.Services.GetServices<IScheduledJobHandler>()
            .OfType<AnnouncementMaintenanceJob>().Single();
        await job.ExecuteAsync(CancellationToken.None);

        var released = await _factory.WithDbAsync(db =>
            db.Announcements.SingleAsync(a => a.Id == ids[0]));
        Assert.Null(released.PinRank);
        Assert.Null(released.ArchivedAtUtc);  // still active

        // Room freed: the fourth can pin now.
        Assert.Equal(HttpStatusCode.NoContent,
            (await AuthFlows.PostWithCsrfAsync(_owner,
                $"/api/v1/announcements/{ids[3]}/pin", new { })).StatusCode);
    }

    [Fact]
    public async Task Scheduled_publish_fires_via_the_job_and_reminders_reach_only_outstanding_users()
    {
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "a3-agent2", Password, Roles.SalesAgent);

        var id = await PublishAsync(new CreateAnnouncementRequest(
            "Later today", "Scheduled.", "Normal", RequireAcknowledgment: true,
            PublishNow: false, ScheduledPublishAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ReminderEveryHours: 1));

        // Not yet published — no targets.
        Assert.Equal(0, await _factory.WithDbAsync(db =>
            db.AnnouncementTargets.CountAsync(t => t.AnnouncementId == id)));

        // Make it due and run the job.
        await _factory.WithDbAsync(db => db.Announcements
            .Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(
                a => a.ScheduledPublishAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var job = _factory.Services.GetServices<IScheduledJobHandler>()
            .OfType<AnnouncementMaintenanceJob>().Single();
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(3, await _factory.WithDbAsync(db =>
            db.AnnouncementTargets.CountAsync(t => t.AnnouncementId == id)));

        // One agent acknowledges; a reminder then reaches only the other.
        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "a3-agent", Password);
        _ = await AuthFlows.PostWithCsrfAsync(agent, $"/api/v1/announcements/{id}/acknowledge", new { });

        var before = await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.Title.StartsWith("Reminder:")));
        var remind = await AuthFlows.PostWithCsrfAsync(_owner,
            $"/api/v1/announcements/{id}/remind-outstanding", new { });
        var reminded = JsonDocument.Parse(await remind.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reminded").GetInt32();
        Assert.Equal(1, reminded);   // only a3-agent2 is outstanding
        var after = await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.Title.StartsWith("Reminder:")));
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Agents_cannot_author_or_inspect_progress()
    {
        var id = await PublishAsync(new CreateAnnouncementRequest("Ops", "x"));
        var agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agent, "a3-agent", Password);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/announcements/",
                new CreateAnnouncementRequest("Fake", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync($"/api/v1/announcements/{id}/progress")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(agent,
                $"/api/v1/announcements/{id}/remind-outstanding", new { })).StatusCode);
    }
}
