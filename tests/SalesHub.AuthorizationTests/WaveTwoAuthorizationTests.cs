using System.Net;
using System.Net.Http.Json;
using SalesHub.Contracts.Sales;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.AuthorizationTests;

/// <summary>
/// Sales authorization: the monitored-work gate guards the whole surface for
/// agents, historical corrections and cross-user summaries are management
/// only, and sales are always created as the caller.
/// </summary>
public class WaveTwoAuthorizationTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private const string Password = "w2-authz-password";

    public async Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "w2z-agent", Password, Roles.SalesAgent);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task The_sales_surface_is_the_working_app_idle_gate_applies_to_agents()
    {
        var client = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(client, "w2z-agent", Password);

        // Without a Verified capability every sales route is blocked...
        var blockedCreate = await AuthFlows.PostWithCsrfAsync(client, "/api/v1/sales/",
            new CreateSaleRequest("123456", "Program", "AS01", 100m));
        Assert.Equal(HttpStatusCode.Forbidden, blockedCreate.StatusCode);
        Assert.Contains("idleCapabilityRequired", await blockedCreate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/sales/me/today")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/sales/team/today")).StatusCode);

        // ...and opens after the handshake.
        await AuthFlows.VerifyIdleCapabilityAsync(client);
        Assert.Equal(HttpStatusCode.Created,
            (await AuthFlows.PostWithCsrfAsync(client, "/api/v1/sales/",
                new CreateSaleRequest("123456", "Program", "AS01", 100m))).StatusCode);
    }

    [Fact]
    public async Task Non_monitored_management_enters_the_sales_surface_without_the_handshake()
    {
        var owner = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            owner, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.OK,
            (await owner.GetAsync("/api/v1/sales/team/today")).StatusCode);
    }

    [Fact]
    public async Task Agents_cannot_reach_management_sales_functions()
    {
        var agent = await AuthFlows.WorkingClientAsync(_factory, "w2z-agent", Password);
        var created = await AuthFlows.PostWithCsrfAsync(agent, "/api/v1/sales/",
            new CreateSaleRequest("654321", "Program", "AS01", 200m));
        var saleId = (await created.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json))!.SaleId;

        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PostWithCsrfAsync(agent,
                $"/api/v1/sales/{saleId}/historical-correction",
                new HistoricalCorrectionRequest(null, null, 250m, "self-serve"))).StatusCode);

        var ownerLogin = await AuthFlows.LoginAsync(_factory.CreateCookieClient(),
            SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync($"/api/v1/sales/users/{ownerLogin.UserId}/summary")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_clients_get_nothing_from_sales()
    {
        var anonymous = _factory.CreateCookieClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/sales/team/today")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/v1/sales/",
                new CreateSaleRequest("123456", "Program", "AS01", 100m), AuthFlows.Json)).StatusCode);
    }
}
