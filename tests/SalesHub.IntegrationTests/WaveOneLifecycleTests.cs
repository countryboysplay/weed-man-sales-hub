using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Contracts.Auth;
using SalesHub.Contracts.Notifications;
using SalesHub.Contracts.Users;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Identity;
using SalesHub.TestSupport;
using SalesHub.Workers;
using SalesHub.Workers.Jobs;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>
/// Wave 1 lifecycle on real PostgreSQL: deactivation, management password
/// reset, the mediated forgot-password queue, profile self-service, the
/// notification model, and the scheduled-reactivation job.
/// </summary>
public class WaveOneLifecycleTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private const string Password = "wave1-password-1";

    public Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(HttpClient Admin, UserResponse Agent)> AdminAndAgentAsync(string username)
    {
        var agent = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, username, Password, Roles.SalesAgent);
        var admin = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            admin, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        return (admin, agent);
    }

    [Fact]
    public async Task Deactivation_blocks_login_ends_sessions_and_reactivation_restores()
    {
        var (admin, agent) = await AdminAndAgentAsync("w1-deact");

        // The agent is signed in on a device.
        var agentClient = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agentClient, "w1-deact", Password);

        var deactivate = await AuthFlows.PostWithCsrfAsync(
            admin, $"/api/v1/users/{agent.Id}/deactivate",
            new DeactivateUserRequest("Seasonal layoff", null));
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        // Existing session ends with the AccountDeactivated reason...
        var refused = await agentClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // ...and a fresh login shows the deactivated access state.
        var login = await _factory.CreateCookieClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("w1-deact", Password), AuthFlows.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("accountDeactivated", await login.Content.ReadAsStringAsync());

        var reactivate = await AuthFlows.PostWithCsrfAsync(
            admin, $"/api/v1/users/{agent.Id}/reactivate", new { });
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);

        _ = await AuthFlows.LoginAsync(_factory.CreateCookieClient(), "w1-deact", Password);
    }

    [Fact]
    public async Task Management_password_reset_revokes_sessions_and_swaps_the_password()
    {
        var (admin, agent) = await AdminAndAgentAsync("w1-reset");
        var agentClient = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(agentClient, "w1-reset", Password);

        var reset = await AuthFlows.PostWithCsrfAsync(
            admin, $"/api/v1/users/{agent.Id}/reset-password",
            new ResetPasswordRequest("brand-new-password-1"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // Old session dead (PasswordReset reason), old password dead, new works.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await agentClient.GetAsync("/api/v1/auth/me")).StatusCode);
        var oldPassword = await _factory.CreateCookieClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("w1-reset", Password), AuthFlows.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        _ = await AuthFlows.LoginAsync(
            _factory.CreateCookieClient(), "w1-reset", "brand-new-password-1");

        var reason = await _factory.WithDbAsync(db => db.UserSessions
            .Where(s => s.UserId == agent.Id)
            .Select(s => s.RevokeReason)
            .FirstAsync());
        Assert.Equal(SessionRevocationReason.PasswordReset, reason);
    }

    [Fact]
    public async Task Forgot_password_fills_the_management_queue_and_completion_assigns_a_password()
    {
        var (admin, agent) = await AdminAndAgentAsync("w1-forgot");

        var anonymous = _factory.CreateCookieClient();
        var submitted = await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password-request",
            new ForgotPasswordRequest("w1-forgot"), AuthFlows.Json);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);

        // Unknown usernames answer identically and still land in the queue.
        var unknown = await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password-request",
            new ForgotPasswordRequest("nobody-here"), AuthFlows.Json);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);

        var queue = await admin.GetFromJsonAsync<List<PasswordResetRequestDto>>(
            "/api/v1/password-reset-requests", AuthFlows.Json);
        Assert.Equal(2, queue!.Count);
        var matched = queue.Single(r => r.UsernameSubmitted == "w1-forgot");
        Assert.Equal(agent.Id, matched.MatchedUserId);
        Assert.Null(queue.Single(r => r.UsernameSubmitted == "nobody-here").MatchedUserId);

        // Owner got a management notification for each request.
        var ownerNotifications = await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.Category == "security"));
        Assert.True(ownerNotifications >= 2);

        var complete = await AuthFlows.PostWithCsrfAsync(
            admin, $"/api/v1/password-reset-requests/{matched.Id}/complete",
            new CompletePasswordResetRequest("assigned-by-mgmt-1"));
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        _ = await AuthFlows.LoginAsync(
            _factory.CreateCookieClient(), "w1-forgot", "assigned-by-mgmt-1");

        // A request without a matched account can only be dismissed.
        var unmatchedId = queue.Single(r => r.UsernameSubmitted == "nobody-here").Id;
        var refuse = await AuthFlows.PostWithCsrfAsync(
            admin, $"/api/v1/password-reset-requests/{unmatchedId}/complete",
            new CompletePasswordResetRequest("whatever-password-1"));
        Assert.Equal(HttpStatusCode.BadRequest, refuse.StatusCode);
        var dismiss = await AuthFlows.PostWithCsrfAsync(
            admin, $"/api/v1/password-reset-requests/{unmatchedId}/dismiss", new { });
        Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);
    }

    [Fact]
    public async Task Profile_self_service_updates_and_password_change_verifies_the_current_one()
    {
        _ = await AdminAndAgentAsync("w1-profile");
        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(client, "w1-profile", Password);

        var update = await AuthFlows.PatchWithCsrfAsync(client, "/api/v1/profile",
            new UpdateProfileRequest("270-555-0142", new DateOnly(1994, 6, 12)));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var profile = await client.GetFromJsonAsync<ProfileResponse>(
            "/api/v1/profile", AuthFlows.Json);
        Assert.Equal("270-555-0142", profile!.Phone);
        Assert.Equal(new DateOnly(1994, 6, 12), profile.Birthday);

        var wrong = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/profile/change-password",
            new ChangePasswordRequest("not-my-password", "self-chosen-password-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var right = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/profile/change-password",
            new ChangePasswordRequest(Password, "self-chosen-password-1"));
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        _ = await AuthFlows.LoginAsync(
            _factory.CreateCookieClient(), "w1-profile", "self-chosen-password-1");
    }

    [Fact]
    public async Task Notification_center_semantics_required_items_resist_deletion()
    {
        var (_, agent) = await AdminAndAgentAsync("w1-notify");

        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<SalesHub.Application.Abstractions.IAppDb>();
            var notifications = sp.GetRequiredService<
                SalesHub.Application.Notifications.NotificationService>();
            await db.ExecuteInTransactionAsync(async token =>
            {
                _ = await notifications.CreateAsync(agent.Id, new(
                    "system", "Ordinary note", "Nothing urgent."), token);
                _ = await notifications.CreateAsync(agent.Id, new(
                    "security", "Required acknowledgment", "Please acknowledge.",
                    Required: true), token);
                await db.SaveChangesAsync(token);
            });
        });

        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(client, "w1-notify", Password);

        var list = await client.GetFromJsonAsync<NotificationListResponse>(
            "/api/v1/notifications", AuthFlows.Json);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal(2, list.UnreadCount);
        Assert.Equal(1, list.RequiredOutstandingCount);

        var required = list.Items.Single(n => n.Required);
        var ordinary = list.Items.Single(n => !n.Required);

        // Required cannot be deleted before acknowledgment.
        var refuse = await AuthFlows.DeleteWithCsrfAsync(
            client, $"/api/v1/notifications/{required.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, refuse.StatusCode);

        var ack = await AuthFlows.PostWithCsrfAsync(
            client, $"/api/v1/notifications/{required.Id}/acknowledge", new { });
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        var deleteAfterAck = await AuthFlows.DeleteWithCsrfAsync(
            client, $"/api/v1/notifications/{required.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteAfterAck.StatusCode);

        _ = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/notifications/mark-all-read", new { });
        var after = await client.GetFromJsonAsync<NotificationListResponse>(
            "/api/v1/notifications", AuthFlows.Json);
        Assert.Equal(0, after!.UnreadCount);
        Assert.Equal(0, after.RequiredOutstandingCount);

        _ = ordinary; // ordinary stays; deletion is user's choice
    }

    [Fact]
    public async Task Scheduled_reactivation_job_notifies_ahead_then_reactivates()
    {
        var (admin, agent) = await AdminAndAgentAsync("w1-sched");
        var deactivate = await AuthFlows.PostWithCsrfAsync(
            admin, $"/api/v1/users/{agent.Id}/deactivate",
            new DeactivateUserRequest("Leave", DateTimeOffset.UtcNow.AddHours(2)));
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        var job = _factory.Services.GetServices<IScheduledJobHandler>()
            .OfType<ScheduledReactivationJob>().Single();

        // Within the hour: advance notice fires exactly once.
        await _factory.WithDbAsync(db => db.Set<ApplicationUser>()
            .Where(u => u.Id == agent.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(
                u => u.ScheduledReactivationAtUtc, DateTimeOffset.UtcNow.AddMinutes(30))));
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);
        var notices = await _factory.WithDbAsync(db => db.Notifications
            .CountAsync(n => n.Title == "Scheduled reactivation in one hour"));
        Assert.Equal(1, notices); // one management user (the owner), once

        // Due: the account reactivates and both sides are notified.
        await _factory.WithDbAsync(db => db.Set<ApplicationUser>()
            .Where(u => u.Id == agent.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(
                u => u.ScheduledReactivationAtUtc, DateTimeOffset.UtcNow.AddSeconds(-5))));
        await job.ExecuteAsync(CancellationToken.None);

        _ = await AuthFlows.LoginAsync(_factory.CreateCookieClient(), "w1-sched", Password);
        var userNote = await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.UserId == agent.Id && n.Title == "Your account is active again"));
        Assert.True(userNote);
    }

    [Fact]
    public async Task Push_subscriptions_register_rebind_and_unsubscribe()
    {
        _ = await AdminAndAgentAsync("w1-push");
        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(client, "w1-push", Password);

        var subscribe = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/push-subscriptions",
            new PushSubscriptionRequest("https://push.example/abc", "p256dh-key", "auth-secret"));
        Assert.Equal(HttpStatusCode.NoContent, subscribe.StatusCode);

        // Same endpoint again: rebinds instead of duplicating.
        var again = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/push-subscriptions",
            new PushSubscriptionRequest("https://push.example/abc", "p256dh-key-2", "auth-secret-2"));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var count = await _factory.WithDbAsync(db => db.PushSubscriptions.CountAsync());
        Assert.Equal(1, count);

        var unsubscribe = await AuthFlows.DeleteWithCsrfAsync(client,
            "/api/v1/push-subscriptions?endpoint=" + Uri.EscapeDataString("https://push.example/abc"));
        Assert.Equal(HttpStatusCode.NoContent, unsubscribe.StatusCode);
        var active = await _factory.WithDbAsync(db =>
            db.PushSubscriptions.CountAsync(s => s.Active));
        Assert.Equal(0, active);
    }
}
