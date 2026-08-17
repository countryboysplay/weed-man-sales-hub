using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.OwnerSecurity;
using SalesHub.Contracts.Auth;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>Sensitive exports, settings audit, and production governance
/// records (CLAUDE.md §13, §20, §21) on real PostgreSQL.</summary>
public class WaveSixGovernanceTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _agentId;
    private const string Password = "wave6-password-1";
    private const string Master = "master-recovery-credential-1";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        var agent = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t6g-agent", Password, Roles.SalesAgent);
        _agentId = agent.Id;
        _owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            _owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task FreshAuthAsync() =>
        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/auth/fresh-auth",
            new FreshAuthRequest(SalesHubApiFactory.OwnerPassword))).EnsureSuccessStatusCode();

    private async Task ArmMasterAsync()
    {
        await FreshAuthAsync();
        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/owner-security/master-credential",
            new { masterCredential = Master })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Employee_history_export_needs_fresh_auth_and_reason_and_audits_downloads()
    {
        // No fresh auth yet → the policy blocks with the standard code.
        var blocked = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/exports/employee-history",
            new { targetUserId = _agentId, format = "Csv", reason = "annual review" });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("requiredFreshAuth", await blocked.Content.ReadAsStringAsync());

        await FreshAuthAsync();

        // Reason is mandatory.
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlows.PostWithCsrfAsync(_owner,
            "/api/v1/exports/employee-history",
            new { targetUserId = _agentId, format = "Csv", reason = " " })).StatusCode);

        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/exports/employee-history",
            new { targetUserId = _agentId, format = "Csv", reason = "annual review" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var root = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var exportId = root.GetProperty("id").GetGuid();
        Assert.StartsWith("EXP-", root.GetProperty("publicId").GetString());

        // Download twice → two child access audits.
        (await _owner.GetAsync($"/api/v1/exports/{exportId}/download")).EnsureSuccessStatusCode();
        (await _owner.GetAsync($"/api/v1/exports/{exportId}/download")).EnsureSuccessStatusCode();
        Assert.Equal(2, await _factory.WithDbAsync(db => db.SensitiveExportAccesses
            .CountAsync(a => a.ExportId == exportId)));

        // The 7-year export audit exists; agents cannot touch the surface.
        Assert.True(await _factory.WithDbAsync(db => db.AuditEvents
            .AnyAsync(a => a.Action == "exports.employeeHistory"
                && a.RetentionClass == AuditRetentionClass.SevenYears)));
        var agent = await AuthFlows.WorkingClientAsync(_factory, "t6g-agent", Password);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync("/api/v1/exports/")).StatusCode);
    }

    [Fact]
    public async Task Watermarked_pdf_export_generates()
    {
        await FreshAuthAsync();
        var created = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/exports/employee-history",
            new { targetUserId = _agentId, format = "Pdf", reason = "termination file" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var exportId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var download = await _owner.GetAsync($"/api/v1/exports/{exportId}/download");
        download.EnsureSuccessStatusCode();
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 500);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes[..4]));
    }

    [Fact]
    public async Task Settings_are_scoped_and_every_change_is_audited()
    {
        var supervisor = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "t6g-supervisor", Password, Roles.SalesSupervisor);
        _ = supervisor;
        var supervisorClient = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(supervisorClient, "t6g-supervisor", Password);

        // Management writes a management-scope setting.
        (await AuthFlows.PostWithCsrfAsync(supervisorClient, "/api/v1/auth/fresh-auth",
            new FreshAuthRequest(Password))).EnsureSuccessStatusCode();
        var putManagement = await PutAsync(supervisorClient,
            "/api/v1/settings/management/dashboard.celebrationSeconds", new { value = 30 });
        Assert.Equal(HttpStatusCode.NoContent, putManagement.StatusCode);

        // Management cannot write system scope at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(supervisorClient,
            "/api/v1/settings/system/security.freshAuthMinutes", new { value = 10 })).StatusCode);

        // Owner writes system scope with fresh auth.
        await FreshAuthAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(_owner,
            "/api/v1/settings/system/security.freshAuthMinutes", new { value = 10 })).StatusCode);

        // Writing a system key through the management route is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(_owner,
            "/api/v1/settings/management/security.freshAuthMinutes", new { value = 5 })).StatusCode);

        // Supervisors list only management scope; the owner sees both.
        var forSupervisor = await supervisorClient.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/settings/", AuthFlows.Json);
        Assert.Single(forSupervisor!);
        var forOwner = await _owner.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/settings/", AuthFlows.Json);
        Assert.Equal(2, forOwner!.Count);

        Assert.Equal(2, await _factory.WithDbAsync(db => db.AuditEvents
            .CountAsync(a => a.Action == "settings.changed")));
    }

    private static async Task<HttpResponseMessage> PutAsync<T>(
        HttpClient client, string url, T payload)
    {
        var token = await AuthFlows.GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload, options: AuthFlows.Json),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Rollbacks_respect_the_blocked_list_and_require_the_protected_flow()
    {
        await ArmMasterAsync();

        // Record a deployment (PROD id) and block a bad version.
        var deployment = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/governance/deployments",
            new { version = "1.4.0", success = true, notes = "August release" });
        Assert.Equal(HttpStatusCode.Created, deployment.StatusCode);
        Assert.StartsWith("PROD-", JsonDocument
            .Parse(await deployment.Content.ReadAsStringAsync())
            .RootElement.GetProperty("publicId").GetString());
        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/governance/blocked-rollback-versions",
            new { version = "1.2.0", reason = "data-corrupting bug" })).EnsureSuccessStatusCode();

        // Rollback to the blocked version refused even with valid verification.
        var blocked = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/governance/rollbacks",
            new
            {
                fromVersion = "1.4.0",
                toVersion = "1.2.0",
                reason = "regression",
                masterCredential = Master,
            });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        // Wrong master refused; correct one lands with a ROLL id.
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthFlows.PostWithCsrfAsync(_owner,
            "/api/v1/governance/rollbacks",
            new
            {
                fromVersion = "1.4.0",
                toVersion = "1.3.0",
                reason = "regression",
                masterCredential = "wrong",
            })).StatusCode);
        var rollback = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/governance/rollbacks",
            new
            {
                fromVersion = "1.4.0",
                toVersion = "1.3.0",
                reason = "regression in sales totals",
                masterCredential = Master,
            });
        Assert.Equal(HttpStatusCode.Created, rollback.StatusCode);
        Assert.StartsWith("ROLL-", JsonDocument
            .Parse(await rollback.Content.ReadAsStringAsync())
            .RootElement.GetProperty("publicId").GetString());
    }

    [Fact]
    public async Task Report_recovery_marks_the_archive_entry_and_keeps_the_original()
    {
        await ArmMasterAsync();

        // Produce an archive entry to recover.
        var today = new BusinessTime(TimeProvider.System).Today;
        (await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/reports/run",
            new { reportType = "SupportTrends", periodStart = today, periodEnd = today }))
            .EnsureSuccessStatusCode();
        var archive = await _owner.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/archive/", AuthFlows.Json);
        var entryId = Assert.Single(archive!).GetProperty("id").GetGuid();

        var recovery = await AuthFlows.PostWithCsrfAsync(_owner, "/api/v1/governance/recoveries",
            new
            {
                archiveEntryId = entryId,
                sourceDescription = "backup 2026-08-10 (Dropbox)",
                reason = "original artifact corrupted",
                masterCredential = Master,
            });
        Assert.Equal(HttpStatusCode.Created, recovery.StatusCode);
        Assert.StartsWith("REC-", JsonDocument
            .Parse(await recovery.Content.ReadAsStringAsync())
            .RootElement.GetProperty("publicId").GetString());

        var entry = await _factory.WithDbAsync(db => db.ArchiveEntries
            .SingleAsync(a => a.Id == entryId));
        Assert.True(entry.Recovered);
        Assert.Contains("REC-", entry.RecoveredFromNote);
    }
}
