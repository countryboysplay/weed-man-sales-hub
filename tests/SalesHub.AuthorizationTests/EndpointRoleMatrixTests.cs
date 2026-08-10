using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Application.Auth;
using SalesHub.Contracts.Users;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.AuthorizationTests;

/// <summary>
/// The endpoint × role matrix (docs/13): anonymous, each of the four roles,
/// resource ownership, fresh-auth, and the mandatory idle-capability gate.
/// One shared factory — the matrix is read-mostly and user setup is costly.
/// </summary>
public class EndpointRoleMatrixTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private readonly Dictionary<string, HttpClient> _clients = [];
    private const string Password = "matrix-password-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();

        foreach (var role in new[] { Roles.SalesAgent, Roles.SalesSupervisor, Roles.SalesManager })
        {
            await AuthFlows.CreateUserAsUnownedAdminAsync(
                _factory, $"matrix-{role.ToLowerInvariant()}", Password, role);
        }

        foreach (var (name, username, password) in new[]
        {
            ("agent", "matrix-salesagent", Password),
            ("supervisor", "matrix-salessupervisor", Password),
            ("manager", "matrix-salesmanager", Password),
            ("owner", SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword),
        })
        {
            var client = _factory.CreateCookieClient();
            await AuthFlows.LoginAsync(client, username, password);
            _clients[name] = client;
        }

        _clients["anonymous"] = _factory.CreateCookieClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("agent", HttpStatusCode.Forbidden)]
    [InlineData("supervisor", HttpStatusCode.Created)]
    [InlineData("manager", HttpStatusCode.Created)]
    [InlineData("owner", HttpStatusCode.Created)]
    public async Task Creating_users_is_management_only(string actor, HttpStatusCode expected)
    {
        var response = await AuthFlows.PostWithCsrfAsync(_clients[actor], "/api/v1/users",
            new CreateUserRequest(
                $"created-by-{actor}-{Guid.NewGuid():N}"[..24],
                "some-password-123", "Created User", Roles.SalesAgent, null));
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("supervisor")]
    [InlineData("manager")]
    [InlineData("owner")]
    public async Task No_role_can_create_an_owner_through_ordinary_user_creation(string actor)
    {
        if (actor == "agent")
        {
            return; // agents cannot create anyone; covered above
        }

        var response = await AuthFlows.PostWithCsrfAsync(_clients[actor], "/api/v1/users",
            new CreateUserRequest("sneaky-owner", "some-password-123", "Sneaky", Roles.Owner, null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("protectedOwnerWorkflowRequired", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("agent", HttpStatusCode.Forbidden)]
    [InlineData("supervisor", HttpStatusCode.OK)]
    [InlineData("manager", HttpStatusCode.OK)]
    [InlineData("owner", HttpStatusCode.OK)]
    public async Task Readiness_health_is_management_only(string actor, HttpStatusCode expected)
    {
        var response = await _clients[actor].GetAsync("/health/ready");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_is_anonymous()
    {
        var response = await _clients["anonymous"].GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_agent_cannot_revoke_another_users_session()
    {
        // The owner's active session, from the owner's own session list.
        var ownerSessions = await _clients["owner"].GetFromJsonAsync<List<SessionDtoLite>>(
            "/api/v1/auth/sessions", AuthFlows.Json);
        var target = ownerSessions![0].SessionId;

        var response = await AuthFlows.DeleteWithCsrfAsync(
            _clients["agent"], $"/api/v1/auth/sessions/{target}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Management_cannot_revoke_an_owners_session_only_an_owner_can()
    {
        var ownerSessions = await _clients["owner"].GetFromJsonAsync<List<SessionDtoLite>>(
            "/api/v1/auth/sessions", AuthFlows.Json);
        var target = ownerSessions![0].SessionId;

        var supervisor = await AuthFlows.DeleteWithCsrfAsync(
            _clients["supervisor"], $"/api/v1/auth/sessions/{target}");
        Assert.Equal(HttpStatusCode.Forbidden, supervisor.StatusCode);

        var manager = await AuthFlows.DeleteWithCsrfAsync(
            _clients["manager"], $"/api/v1/auth/sessions/{target}");
        Assert.Equal(HttpStatusCode.Forbidden, manager.StatusCode);
    }

    [Fact]
    public async Task Fresh_auth_gate_blocks_until_password_reentry()
    {
        var client = _clients["manager"];

        var before = await AuthFlows.PostWithCsrfAsync(
            client, "/api/v1/diagnostics/fresh-auth-ping", new { });
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Contains("requiredFreshAuth", await before.Content.ReadAsStringAsync());

        var freshAuth = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/auth/fresh-auth",
            new SalesHub.Contracts.Auth.FreshAuthRequest(Password));
        Assert.Equal(HttpStatusCode.OK, freshAuth.StatusCode);

        var after = await AuthFlows.PostWithCsrfAsync(
            client, "/api/v1/diagnostics/fresh-auth-ping", new { });
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Agents_are_blocked_from_monitored_work_without_verified_idle_capability()
    {
        var response = await _clients["agent"].GetAsync("/api/v1/diagnostics/monitored-ping");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("idleCapabilityRequired", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Non_monitored_roles_pass_the_monitored_work_gate()
    {
        // Only SalesAgent is presence-monitored in the default configuration.
        var response = await _clients["supervisor"].GetAsync("/api/v1/diagnostics/monitored-ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_verified_idle_capability_opens_the_gate_and_going_stale_closes_it()
    {
        var client = _factory.CreateCookieClient();
        var login = await AuthFlows.LoginAsync(client, "matrix-salesagent", Password);
        Assert.True(login.IdleCapabilityRequired);

        var verify = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/auth/idle-capability/verify",
            new SalesHub.Contracts.Auth.IdleCapabilityVerifyRequest(
                true, "granted", true, 60, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        var open = await client.GetAsync("/api/v1/diagnostics/monitored-ping");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        // Force the lease into the past and sweep — the stale scan must close the gate.
        await _factory.WithDbAsync(db => db.UserSessions
            .Where(s => s.Id == login.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                x => x.IdleCapabilityLeaseUntilUtc,
                DateTimeOffset.UtcNow.AddMinutes(-1))));

        await _factory.WithScopeAsync(async sp =>
        {
            var idle = sp.GetRequiredService<IdleCapabilityService>();
            var marked = await idle.MarkStaleSessionsAsync();
            Assert.True(marked >= 1);
        });

        var closed = await client.GetAsync("/api/v1/diagnostics/monitored-ping");
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        Assert.Contains("idleCapabilityRequired", await closed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("unsupported", false, "granted", true)]
    [InlineData("denied", true, "denied", true)]
    [InlineData("not-started", true, "granted", false)]
    public async Task Failed_capability_attestations_do_not_open_the_gate(
        string label, bool supported, string permission, bool started)
    {
        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(client, "matrix-salesagent", Password);

        var verify = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/auth/idle-capability/verify",
            new SalesHub.Contracts.Auth.IdleCapabilityVerifyRequest(
                supported, permission, started, 60, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        var gate = await client.GetAsync("/api/v1/diagnostics/monitored-ping");
        Assert.True(HttpStatusCode.Forbidden == gate.StatusCode,
            $"attestation '{label}' must not open the monitored-work gate");
    }

    private sealed record SessionDtoLite(Guid SessionId);
}
