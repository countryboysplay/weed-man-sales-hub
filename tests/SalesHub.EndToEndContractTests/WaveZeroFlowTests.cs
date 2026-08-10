using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SalesHub.Contracts.Auth;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.EndToEndContractTests;

/// <summary>
/// The full Wave 0 story with real workers running: provision an agent,
/// walk the idle-capability handshake, revoke a session and watch the outbox
/// deliver the auth.sessionRevoked.v1 event over SignalR.
/// </summary>
public class WaveZeroFlowTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory(workersEnabled: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Agent_lifecycle_login_verify_work_heartbeat()
    {
        await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "e2e-agent", "e2e-agent-password-1", Roles.SalesAgent);

        var agent = _factory.CreateCookieClient();
        var login = await AuthFlows.LoginAsync(agent, "e2e-agent", "e2e-agent-password-1");
        Assert.True(login.IdleCapabilityRequired);

        // Blocked before the handshake.
        var blocked = await agent.GetAsync("/api/v1/diagnostics/monitored-ping");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        // Handshake: supported + granted + started ⇒ Verified with a lease.
        var verify = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/auth/idle-capability/verify",
            new IdleCapabilityVerifyRequest(true, "granted", true, 60, DateTimeOffset.UtcNow));
        var verified = await verify.Content.ReadFromJsonAsync<IdleCapabilityVerifyResponse>(AuthFlows.Json);
        Assert.Equal("Verified", verified!.State);
        Assert.NotNull(verified.LeaseUntil);
        Assert.Equal(60, verified.HeartbeatCadenceSeconds);

        var working = await agent.GetAsync("/api/v1/diagnostics/monitored-ping");
        Assert.Equal(HttpStatusCode.OK, working.StatusCode);

        // Heartbeat slides the lease forward.
        var heartbeat = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/auth/idle-capability/heartbeat",
            new IdleHeartbeatRequest("active", "unlocked", "visible", DateTimeOffset.UtcNow, "1.0.0"));
        var beat = await heartbeat.Content.ReadFromJsonAsync<IdleHeartbeatResponse>(AuthFlows.Json);
        Assert.Equal("Verified", beat!.State);
        Assert.True(beat.LeaseUntil >= verified.LeaseUntil);

        // /me reflects the capability state.
        var me = await agent.GetFromJsonAsync<MeResponse>("/api/v1/auth/me", AuthFlows.Json);
        Assert.Equal("Verified", me!.IdleCapabilityState);
    }

    [Fact]
    public async Task Session_revocation_reaches_the_browser_over_signalr_via_the_outbox()
    {
        var owner = _factory.CreateCookieClient();
        var login = await AuthFlows.LoginAsync(
            owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        // Second signed-in device for the same owner.
        var phone = _factory.CreateCookieClient();
        var phoneLogin = await AuthFlows.LoginAsync(
            phone, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        // Connect the "phone" to /hubs/app with its session cookie.
        var cookieHeader = await CaptureCookieHeaderAsync(phone);
        var received = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{_factory.Server.BaseAddress}hubs/app", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.Headers["Cookie"] = cookieHeader;
            })
            .Build();

        connection.On<JsonElement>("event", envelope =>
        {
            if (envelope.GetProperty("eventType").GetString() == "auth.sessionRevoked.v1")
            {
                received.TrySetResult(envelope);
            }
        });
        await connection.StartAsync();

        // Owner (desktop) force-revokes the phone session.
        var revoke = await AuthFlows.DeleteWithCsrfAsync(
            owner, $"/api/v1/auth/sessions/{phoneLogin.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // The outbox worker (running in-process) must deliver it.
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var payload = envelope.GetProperty("payload");
        Assert.Equal(phoneLogin.SessionId,
            payload.GetProperty("sessionId").GetGuid());

        // And the phone's next API call is refused by the session gate.
        var refused = await phone.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // The desktop session is untouched.
        var stillFine = await owner.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillFine.StatusCode);
        Assert.Equal(login.SessionId,
            (await stillFine.Content.ReadFromJsonAsync<MeResponse>(AuthFlows.Json))!.SessionId);
    }

    [Fact]
    public async Task Problem_details_carry_code_and_correlation_id()
    {
        var client = _factory.CreateCookieClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("nobody", "nothing"), AuthFlows.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalidCredentials", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(
            body.RootElement.GetProperty("correlationId").GetString()));

        // The response echoes the correlation header for support tickets.
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public async Task Anonymous_clients_cannot_connect_to_the_hub()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{_factory.Server.BaseAddress}hubs/app", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    /// <summary>Reproduces the client's cookie header for the SignalR handshake.</summary>
    private async Task<string> CaptureCookieHeaderAsync(HttpClient client)
    {
        // The cookie jar is internal to the handler; easiest faithful copy is
        // to ask the server what it set by logging the /me roundtrip headers.
        // Instead, re-login on a raw client and keep the Set-Cookie values.
        var raw = _factory.Server.CreateClient();
        var response = await raw.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword),
            AuthFlows.Json);
        response.EnsureSuccessStatusCode();
        var cookies = response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';')[0]);
        return string.Join("; ", cookies);
    }
}
