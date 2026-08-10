using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SalesHub.Contracts.Auth;
using SalesHub.Contracts.Sales;
using SalesHub.Domain;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.EndToEndContractTests;

/// <summary>
/// Acceptance scenario 1 end to end: a sale saves once, the celebration/
/// totals event reaches every connected user via the outbox worker, and the
/// broadcast never carries the CID.
/// </summary>
public class SalesRealtimeTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory(workersEnabled: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task A_new_sale_reaches_other_users_in_realtime_without_the_cid()
    {
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "rt-seller", "rt-password-123", Roles.SalesAgent);
        var seller = await AuthFlows.WorkingClientAsync(_factory, "rt-seller", "rt-password-123");

        // A different user (the owner) listens on the hub.
        var raw = _factory.Server.CreateClient();
        var login = await raw.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword),
            AuthFlows.Json);
        login.EnsureSuccessStatusCode();
        var cookies = string.Join("; ",
            login.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));

        var received = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{_factory.Server.BaseAddress}hubs/app", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.Headers["Cookie"] = cookies;
            })
            .Build();
        connection.On<JsonElement>("event", envelope =>
        {
            if (envelope.GetProperty("eventType").GetString() == "sales.saleCreated.v1")
            {
                received.TrySetResult(envelope);
            }
        });
        await connection.StartAsync();

        var create = await AuthFlows.PostWithCsrfAsync(seller, "/api/v1/sales/",
            new CreateSaleRequest("482193", "Program", "AS01", 421.00m));
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var payload = envelope.GetProperty("payload");

        // Celebration content: seller, amount, type, campaign — never the CID.
        Assert.Equal("Test rt-seller", payload.GetProperty("sellerDisplayName").GetString());
        Assert.Equal(421.00m, payload.GetProperty("amount").GetDecimal());
        Assert.Equal("AS01", payload.GetProperty("campaign").GetString());
        Assert.False(payload.TryGetProperty("cid", out _));
        Assert.DoesNotContain("482193", payload.GetRawText());
    }
}
