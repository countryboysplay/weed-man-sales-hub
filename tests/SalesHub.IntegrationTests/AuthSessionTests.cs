using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Contracts.Auth;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>
/// Login, logout, revocation and fresh auth against real PostgreSQL —
/// the Wave 0 gate items, over HTTP like the PWA.
/// </summary>
public class AuthSessionTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Migrations_apply_and_the_app_starts()
    {
        var live = await _factory.CreateCookieClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var applied = await _factory.WithDbAsync(async db =>
            (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(applied >= 1);
    }

    [Fact]
    public async Task Login_creates_a_server_side_session_and_audit_row()
    {
        var client = _factory.CreateCookieClient();
        var login = await AuthFlows.LoginAsync(
            client, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        var session = await _factory.WithDbAsync(db =>
            db.UserSessions.SingleAsync(s => s.Id == login.SessionId));
        Assert.Null(session.RevokedAtUtc);
        Assert.Equal(64, session.TokenHash.Length);      // SHA-256 hex, never the verifier

        var audited = await _factory.WithDbAsync(db =>
            db.AuditEvents.AnyAsync(a => a.Action == "auth.login" && a.SessionId == login.SessionId));
        Assert.True(audited);
    }

    [Fact]
    public async Task Wrong_password_fails_and_lockout_engages_after_five_attempts()
    {
        var client = _factory.CreateCookieClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest(SalesHubApiFactory.OwnerUsername, "wrong-password"), AuthFlows.Json);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Even the correct password is refused while locked out.
        var locked = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword),
            AuthFlows.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        var body = await locked.Content.ReadAsStringAsync();
        Assert.Contains("accountLocked", body);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_the_cookie_stops_working()
    {
        var client = _factory.CreateCookieClient();
        var login = await AuthFlows.LoginAsync(
            client, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        var logout = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var session = await _factory.WithDbAsync(db =>
            db.UserSessions.SingleAsync(s => s.Id == login.SessionId));
        Assert.NotNull(session.RevokedAtUtc);
        Assert.Equal(SessionRevocationReason.UserLogout, session.RevokeReason);
    }

    [Fact]
    public async Task A_revoked_session_is_refused_even_with_a_valid_cookie()
    {
        var client = _factory.CreateCookieClient();
        var login = await AuthFlows.LoginAsync(
            client, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        // Revoke server-side, bypassing the client (management force logout).
        await _factory.WithDbAsync(async db =>
        {
            var session = await db.UserSessions.SingleAsync(s => s.Id == login.SessionId);
            session.RevokedAtUtc = DateTimeOffset.UtcNow;
            session.RevokeReason = SessionRevocationReason.AdministrativeLogout;
            return await db.SaveChangesAsync();
        });

        var refused = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("sessionRevoked", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Session_revocation_writes_an_outbox_event_for_the_client()
    {
        var client = _factory.CreateCookieClient();
        var login = await AuthFlows.LoginAsync(
            client, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        var revoke = await AuthFlows.DeleteWithCsrfAsync(
            client, $"/api/v1/auth/sessions/{login.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var outboxRow = await _factory.WithDbAsync(db =>
            db.OutboxMessages.SingleAsync(m => m.EventType == "auth.sessionRevoked.v1"));
        Assert.Contains(login.SessionId.ToString(), outboxRow.PayloadJson);
        Assert.Null(outboxRow.ProcessedAtUtc);
    }

    [Fact]
    public async Task Fresh_auth_stamps_the_window_and_wrong_password_does_not()
    {
        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            client, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        var wrong = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/auth/fresh-auth",
            new FreshAuthRequest("wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var right = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/auth/fresh-auth",
            new FreshAuthRequest(SalesHubApiFactory.OwnerPassword));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        var body = await right.Content.ReadFromJsonAsync<FreshAuthResponse>(AuthFlows.Json);
        // Default window is 15 minutes (CLAUDE.md §3).
        Assert.InRange(
            body!.FreshAuthUntil - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(15.5));
    }

    [Fact]
    public async Task State_changing_requests_without_a_csrf_token_are_rejected()
    {
        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            client, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        var naked = await client.PostAsJsonAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.BadRequest, naked.StatusCode);
        Assert.Contains("antiforgery", await naked.Content.ReadAsStringAsync());
    }
}
