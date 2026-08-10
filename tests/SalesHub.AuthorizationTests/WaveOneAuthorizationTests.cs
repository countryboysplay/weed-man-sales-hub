using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Contracts.Users;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.AuthorizationTests;

/// <summary>
/// Wave 1 authorization matrix: lifecycle endpoints are management-only,
/// Owner accounts are protected from non-Owners, directory/profile stay
/// open to employees, and notifications are own-resource scoped.
/// </summary>
public class WaveOneAuthorizationTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _agent = null!;
    private HttpClient _supervisor = null!;
    private HttpClient _owner = null!;
    private Guid _agentId;
    private Guid _ownerId;
    private const string Password = "w1-authz-password";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var agent = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "w1a-agent", Password, Roles.SalesAgent);
        _agentId = agent.Id;
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "w1a-supervisor", Password, Roles.SalesSupervisor);

        _agent = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(_agent, "w1a-agent", Password);
        _supervisor = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(_supervisor, "w1a-supervisor", Password);
        _owner = _factory.CreateCookieClient();
        var ownerLogin = await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        _ownerId = ownerLogin.UserId;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Agents_cannot_touch_the_user_admin_surface()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _agent.GetAsync("/api/v1/users/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _agent.GetAsync($"/api/v1/users/{_agentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PatchWithCsrfAsync(_agent, $"/api/v1/users/{_agentId}",
                new UpdateUserRequest("New Name", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(_agent, $"/api/v1/users/{_agentId}/force-logout",
                new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _agent.GetAsync("/api/v1/password-reset-requests/")).StatusCode);
    }

    [Fact]
    public async Task Agents_do_get_directory_branches_profile_and_notifications()
    {
        Assert.Equal(HttpStatusCode.OK, (await _agent.GetAsync("/api/v1/directory")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _agent.GetAsync("/api/v1/branches/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _agent.GetAsync("/api/v1/profile/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _agent.GetAsync("/api/v1/notifications/")).StatusCode);
    }

    [Fact]
    public async Task Supervisors_cannot_manage_an_owner_account()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(_supervisor, $"/api/v1/users/{_ownerId}/deactivate",
                new DeactivateUserRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(_supervisor, $"/api/v1/users/{_ownerId}/reset-password",
                new ResetPasswordRequest("hijacked-password-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(_supervisor, $"/api/v1/users/{_ownerId}/force-logout",
                new { })).StatusCode);
    }

    [Fact]
    public async Task Role_changes_involving_owner_are_refused_for_everyone()
    {
        var promote = await AuthFlows.PatchWithCsrfAsync(_owner, $"/api/v1/users/{_agentId}",
            new UpdateUserRequest(null, Roles.Owner, null, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);

        var demote = await AuthFlows.PatchWithCsrfAsync(_owner, $"/api/v1/users/{_ownerId}",
            new UpdateUserRequest(null, Roles.SalesManager, null, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, demote.StatusCode);
    }

    [Fact]
    public async Task Supervisors_can_manage_ordinary_roles()
    {
        var rename = await AuthFlows.PatchWithCsrfAsync(_supervisor, $"/api/v1/users/{_agentId}",
            new UpdateUserRequest("Renamed Agent", null, null, null, null));
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        var promote = await AuthFlows.PatchWithCsrfAsync(_supervisor, $"/api/v1/users/{_agentId}",
            new UpdateUserRequest(null, Roles.SalesManager, null, null, null));
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);

        // Restore for other tests.
        _ = await AuthFlows.PatchWithCsrfAsync(_supervisor, $"/api/v1/users/{_agentId}",
            new UpdateUserRequest(null, Roles.SalesAgent, null, null, null));
    }

    [Fact]
    public async Task Notifications_are_scoped_to_their_owner()
    {
        // A notification for the owner is invisible to the agent's routes.
        Guid notificationId = Guid.Empty;
        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<SalesHub.Application.Abstractions.IAppDb>();
            var notifications = sp.GetRequiredService<
                SalesHub.Application.Notifications.NotificationService>();
            await db.ExecuteInTransactionAsync(async token =>
            {
                var created = await notifications.CreateAsync(_ownerId, new(
                    "system", "Owner-only note", "Private."), token);
                notificationId = created.Id;
                await db.SaveChangesAsync(token);
            });
        });

        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthFlows.PostWithCsrfAsync(_agent,
                $"/api/v1/notifications/{notificationId}/read", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthFlows.DeleteWithCsrfAsync(_agent,
                $"/api/v1/notifications/{notificationId}")).StatusCode);
    }
}
