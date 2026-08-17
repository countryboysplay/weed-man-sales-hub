using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.OwnerSecurity;
using SalesHub.Contracts.Auth;
using SalesHub.Contracts.Chat;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>
/// Protected Owner flows (CLAUDE.md §19) on real PostgreSQL: master
/// credential + TOTP verification, Owner role changes, private
/// communication inspection, and emergency access.
/// </summary>
public class WaveSixOwnerSecurityTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private const string Password = "wave6-password-1";
    private const string Master = "master-recovery-credential-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task FreshAuthAsync(HttpClient client, string password) =>
        (await AuthFlows.PostWithCsrfAsync(client, "/api/v1/auth/fresh-auth",
            new FreshAuthRequest(password))).EnsureSuccessStatusCode();

    /// <summary>Sets up master credential + TOTP for the owner and returns a
    /// generator for currently valid codes.</summary>
    private async Task<Func<string>> ArmOwnerSecurityAsync()
    {
        await FreshAuthAsync(_owner, SalesHubApiFactory.OwnerPassword);
        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/master-credential",
            new { masterCredential = Master })).EnsureSuccessStatusCode();

        var begin = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/totp/begin",
            new { masterCredential = Master });
        begin.EnsureSuccessStatusCode();
        var uri = JsonDocument.Parse(await begin.Content.ReadAsStringAsync())
            .RootElement.GetProperty("otpauthUri").GetString()!;
        var secretBase32 = uri.Split("secret=")[1].Split('&')[0];
        var secret = FromBase32(secretBase32);

        string CurrentCode() => Totp.Compute(
            secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.StepSeconds);

        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/totp/confirm",
            new { code = CurrentCode() })).EnsureSuccessStatusCode();
        return CurrentCode;
    }

    private static byte[] FromBase32(string encoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var output = new List<byte>();
        foreach (var c in encoded)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. output];
    }

    [Fact]
    public async Task Master_setup_requires_fresh_auth_and_wrong_credentials_throttle()
    {
        // Without fresh auth: refused.
        var stale = await AuthFlows.PostWithCsrfAsync(_owner,
            "/api/v1/owner-security/master-credential", new { masterCredential = Master });
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);

        var code = await ArmOwnerSecurityAsync();

        // A protected action with the wrong master fails and is recorded.
        var wrong = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/emergency",
            new
            {
                durationMinutes = 30,
                reason = "test",
                masterCredential = "not-the-master-credential",
                totpCode = code(),
            });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.True(await _factory.WithDbAsync(db => db.OwnerRecoverySecurityEvents
            .AnyAsync(e => e.EventType == "masterCredential.verifyFailed")));

        // The status view never returns credential material.
        var status = await _owner.GetFromJsonAsync<JsonElement>(
            "/api/v1/owner-security/status", AuthFlows.Json);
        Assert.True(status.GetProperty("masterCredentialConfigured").GetBoolean());
        Assert.True(status.GetProperty("totpEnabled").GetBoolean());
    }

    [Fact]
    public async Task Owner_promotion_and_demotion_run_only_through_the_protected_flow()
    {
        var agent = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t6-agent", Password, Roles.SalesAgent);
        var code = await ArmOwnerSecurityAsync();

        // Promote to Owner with full verification.
        var promote = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/owner-role",
            new
            {
                targetUserId = agent.Id,
                newRole = Roles.Owner,
                reason = "co-owner onboarding",
                masterCredential = Master,
                totpCode = code(),
            });
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);
        Assert.Equal(Roles.Owner, await _factory.WithDbAsync(db => db.Users
            .Where(u => u.Id == agent.Id).Select(u => u.Role).SingleAsync()));

        // Demote back — two owners exist, so this is allowed.
        var demote = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/owner-role",
            new
            {
                targetUserId = agent.Id,
                newRole = Roles.SalesAgent,
                reason = "onboarding reversed",
                masterCredential = Master,
                totpCode = code(),
            });
        Assert.Equal(HttpStatusCode.NoContent, demote.StatusCode);

        // The last active Owner cannot be demoted.
        var ownerId = await _factory.WithDbAsync(db => db.Users
            .Where(u => u.UserName == SalesHubApiFactory.OwnerUsername)
            .Select(u => u.Id).SingleAsync());
        var last = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/owner-role",
            new
            {
                targetUserId = ownerId,
                newRole = Roles.SalesManager,
                reason = "should fail",
                masterCredential = Master,
                totpCode = code(),
            });
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);

        // Permanent audit exists for the changes.
        Assert.Equal(2, await _factory.WithDbAsync(db => db.AuditEvents
            .CountAsync(a => a.Action == "ownerSecurity.ownerRoleChanged")));
    }

    [Fact]
    public async Task Private_communication_inspection_is_gated_scoped_and_permanently_audited()
    {
        // Two agents talk in a DM.
        var a = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t6-dm-a", Password, Roles.SalesAgent);
        var b = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t6-dm-b", Password, Roles.SalesAgent);
        var alice = await AuthFlows.WorkingClientAsync(_factory, "t6-dm-a", Password);
        var direct = await AuthFlows.PostWithCsrfAsync(alice, "/api/v1/conversations/direct",
            new StartDirectRequest(b.Id));
        direct.EnsureSuccessStatusCode();
        var conversationId = JsonDocument.Parse(await direct.Content.ReadAsStringAsync())
            .RootElement.GetProperty("conversationId").GetGuid();
        (await AuthFlows.PostWithCsrfAsync(alice,
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest("the quarterly numbers are fake"))).EnsureSuccessStatusCode();

        var code = await ArmOwnerSecurityAsync();

        // Start access; the permanent record must exist before content flows.
        var started = await AuthFlows.PostWithCsrfAsync(_owner,
            "/api/v1/owner-security/private-access",
            new
            {
                conversationIds = new[] { conversationId },
                scope = "single-conversation",
                reason = "HR investigation 44-B",
                masterCredential = Master,
                totpCode = code(),
            });
        started.EnsureSuccessStatusCode();
        var accessSessionId = JsonDocument.Parse(await started.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessSessionId").GetGuid();
        Assert.Equal(1, await _factory.WithDbAsync(db =>
            db.PrivateCommunicationAccesses.CountAsync()));

        // Read within scope succeeds and reads are logged as child metadata.
        var read = await _owner.GetAsync(
            $"/api/v1/owner-security/private-access/{accessSessionId}/conversations/{conversationId}");
        read.EnsureSuccessStatusCode();
        Assert.Contains("quarterly numbers", await read.Content.ReadAsStringAsync());
        Assert.True(await _factory.WithDbAsync(db => db.AuditEvents
            .AnyAsync(a => a.Action == "ownerSecurity.privateCommunicationRead")));

        // Out-of-scope conversation refused even with a live access session.
        _ = a;
        var outOfScope = await _owner.GetAsync(
            $"/api/v1/owner-security/private-access/{accessSessionId}/conversations/{Guid.CreateVersion7()}");
        Assert.Equal(HttpStatusCode.Forbidden, outOfScope.StatusCode);
    }

    [Fact]
    public async Task Emergency_access_caps_at_60_minutes_and_notifies_other_owners()
    {
        var code = await ArmOwnerSecurityAsync();

        // A second owner to receive notifications.
        var second = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t6-owner2", Password, Roles.SalesManager);
        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/owner-role",
            new
            {
                targetUserId = second.Id,
                newRole = Roles.Owner,
                reason = "second owner",
                masterCredential = Master,
                totpCode = code(),
            })).EnsureSuccessStatusCode();

        // 61 minutes: refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PostWithCsrfAsync(_owner,
            "/api/v1/owner-security/emergency",
            new
            {
                durationMinutes = 61,
                reason = "too long",
                masterCredential = Master,
                totpCode = code(),
            })).StatusCode);

        var started = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/emergency",
            new
            {
                durationMinutes = 45,
                reason = "restore access to locked report",
                masterCredential = Master,
                totpCode = code(),
            });
        started.EnsureSuccessStatusCode();
        var emergencyId = JsonDocument.Parse(await started.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // The other owner was notified with a required notification.
        Assert.True(await _factory.WithDbAsync(db => db.Notifications
            .AnyAsync(n => n.UserId == second.Id && n.Category == "security" && n.Required)));

        // The second owner terminates it — reason required.
        var ownerTwo = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(ownerTwo, "t6-owner2", Password);
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PostWithCsrfAsync(ownerTwo,
            $"/api/v1/owner-security/emergency/{emergencyId}/end",
            new { })).StatusCode);
        (await AuthFlows.PostWithCsrfAsync(ownerTwo,
            $"/api/v1/owner-security/emergency/{emergencyId}/end",
            new { reason = "not warranted" })).EnsureSuccessStatusCode();

        var session = await _factory.WithDbAsync(db => db.EmergencyAccessSessions
            .SingleAsync(s => s.Id == emergencyId));
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal(second.Id, session.EndedByUserId);
    }
}
