using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Contracts.Sales;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.TestSupport;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>
/// The Sales rules, exhaustively, on real PostgreSQL (docs/10: "Do not move
/// forward until sales rules are exhaustively tested"). Maps directly to the
/// acceptance scenarios in docs/09.
/// </summary>
public class WaveTwoSalesTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;
    private const string Password = "wave2-password-1";

    public Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpClient> AgentAsync(string username)
    {
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, username, Password, Roles.SalesAgent);
        return await AuthFlows.WorkingClientAsync(_factory, username, Password);
    }

    private static Task<HttpResponseMessage> PostSaleAsync(
        HttpClient client, CreateSaleRequest request, string? idempotencyKey = null) =>
        PostSaleCoreAsync(client, request, idempotencyKey);

    private static async Task<HttpResponseMessage> PostSaleCoreAsync(
        HttpClient client, CreateSaleRequest request, string? idempotencyKey)
    {
        var token = await AuthFlows.GetCsrfTokenAsync(client);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales/")
        {
            Content = JsonContent.Create(request, options: AuthFlows.Json),
        };
        message.Headers.Add("X-CSRF-TOKEN", token);
        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(message);
    }

    // ── scenario 1: create updates the canonical row and the aggregates ──────

    [Fact]
    public async Task A_valid_sale_saves_once_and_shows_in_today_views()
    {
        var client = await AgentAsync("w2-create");
        var response = await PostSaleAsync(client,
            new CreateSaleRequest("482193", "Program", "AS01", 421.00m));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json);
        Assert.Equal("482193", sale!.Cid);
        Assert.Equal("Active", sale.State);

        var today = await client.GetFromJsonAsync<MyTodayResponse>(
            "/api/v1/sales/me/today", AuthFlows.Json);
        Assert.Equal(1, today!.Count);
        Assert.Equal(421.00m, today.Net);

        var team = await client.GetFromJsonAsync<TeamTodayResponse>(
            "/api/v1/sales/team/today", AuthFlows.Json);
        Assert.Equal(421.00m, team!.TeamNet);

        // The outbox row exists in the same transaction and never leaks the CID.
        var outboxPayload = await _factory.WithDbAsync(db => db.OutboxMessages
            .Where(m => m.EventType == "sales.saleCreated.v1")
            .Select(m => m.PayloadJson)
            .SingleAsync());
        Assert.DoesNotContain("482193", outboxPayload);
        Assert.Contains("421", outboxPayload);
    }

    // ── scenario 2: idempotent replay ────────────────────────────────────────

    [Fact]
    public async Task The_same_idempotency_key_returns_the_original_result_without_a_second_row()
    {
        var client = await AgentAsync("w2-idem");
        var request = new CreateSaleRequest("111222", "Program", "AS01", 300m);

        var first = await PostSaleAsync(client, request, "key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await PostSaleAsync(client, request, "key-1");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Same outcome, same sale — jsonb storage may reformat, so compare
        // the parsed contract rather than raw bytes.
        var firstDto = await first.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json);
        var secondDto = await second.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json);
        Assert.Equal(firstDto, secondDto);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Sales.CountAsync()));

        // Same key, different payload: a client bug, refused loudly.
        var mismatched = await PostSaleAsync(client,
            new CreateSaleRequest("999888", "Program", "AS01", 5m), "key-1");
        Assert.Equal(HttpStatusCode.Conflict, mismatched.StatusCode);
        Assert.Contains("idempotencyKeyReuse", await mismatched.Content.ReadAsStringAsync());
    }

    // ── scenario 3: duplicate Program CID with explicit resale confirmation ──

    [Fact]
    public async Task Duplicate_program_cid_needs_the_structured_resale_confirmation()
    {
        var client = await AgentAsync("w2-dup");
        _ = await PostSaleAsync(client, new CreateSaleRequest("777001", "Program", "AS01", 399m));

        var duplicate = await PostSaleAsync(client,
            new CreateSaleRequest("777001", "Program", "AS01", 421m));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal("duplicateSale", problem.RootElement.GetProperty("code").GetString());
        var prior = problem.RootElement.GetProperty("priorSale");
        Assert.Equal(399m, prior.GetProperty("amount").GetDecimal());
        var token = problem.RootElement.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        // Garbage token: refused again.
        var bad = await PostSaleAsync(client,
            new CreateSaleRequest("777001", "Program", "AS01", 421m, "not-a-real-token"));
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);

        // The real token: resale accepted and the override is recorded.
        var confirmed = await PostSaleAsync(client,
            new CreateSaleRequest("777001", "Program", "AS01", 421m, token));
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.SaleDuplicateOverrides.CountAsync()));
    }

    [Fact]
    public async Task A_resale_token_is_bound_to_its_seller()
    {
        var alice = await AgentAsync("w2-alice");
        var bob = await AgentAsync("w2-bob");
        _ = await PostSaleAsync(alice, new CreateSaleRequest("555001", "Program", "AS01", 100m));

        var refusal = await PostSaleAsync(alice,
            new CreateSaleRequest("555001", "Program", "AS01", 150m));
        using var problem = JsonDocument.Parse(await refusal.Content.ReadAsStringAsync());
        var aliceToken = problem.RootElement.GetProperty("confirmationToken").GetString()!;

        // Bob cannot spend Alice's confirmation.
        var bobAttempt = await PostSaleAsync(bob,
            new CreateSaleRequest("555001", "Program", "AS01", 150m, aliceToken));
        Assert.Equal(HttpStatusCode.Conflict, bobAttempt.StatusCode);
    }

    [Fact]
    public async Task Upsell_duplicates_block_on_cid_plus_campaign_with_no_override()
    {
        var client = await AgentAsync("w2-upsell");
        _ = await PostSaleAsync(client, new CreateSaleRequest("333001", "Upsell", "GC01", 89m));

        // Same CID + same campaign: hard block.
        var same = await PostSaleAsync(client, new CreateSaleRequest("333001", "Upsell", "GC01", 99m));
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.DoesNotContain("confirmationToken\":\"",
            await same.Content.ReadAsStringAsync());

        // Same CID, different campaign: fine.
        Assert.Equal(HttpStatusCode.Created,
            (await PostSaleAsync(client, new CreateSaleRequest("333001", "Upsell", "AE01", 129m))).StatusCode);

        // A program for the same CID is a different uniqueness scope: fine.
        Assert.Equal(HttpStatusCode.Created,
            (await PostSaleAsync(client, new CreateSaleRequest("333001", "Program", "AS01", 500m))).StatusCode);
    }

    [Fact]
    public async Task Last_years_sale_does_not_block_this_years_cid()
    {
        var client = await AgentAsync("w2-year");
        var created = await PostSaleAsync(client,
            new CreateSaleRequest("444001", "Program", "AS01", 250m));
        var saleId = (await created.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json))!.SaleId;

        // Move it into last year — the duplicate rule resets January 1.
        await _factory.WithDbAsync(db => db.Sales
            .Where(s => s.Id == saleId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                x => x.BusinessDate, new DateOnly(DateTime.UtcNow.Year - 1, 6, 15))));

        Assert.Equal(HttpStatusCode.Created,
            (await PostSaleAsync(client, new CreateSaleRequest("444001", "Program", "AS01", 275m))).StatusCode);
    }

    // ── scenarios 4–5: same-day delete, tombstone, replacement ──────────────

    [Fact]
    public async Task Same_day_delete_is_a_tombstone_out_of_totals_and_frees_the_duplicate()
    {
        var client = await AgentAsync("w2-delete");
        var created = await PostSaleAsync(client,
            new CreateSaleRequest("666001", "Program", "AS01", 421m));
        var saleId = (await created.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json))!.SaleId;

        var delete = await AuthFlows.DeleteWithCsrfAsync(client, $"/api/v1/sales/{saleId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        // Visible today as Deleted, excluded from totals immediately.
        var today = await client.GetFromJsonAsync<MyTodayResponse>(
            "/api/v1/sales/me/today", AuthFlows.Json);
        Assert.Single(today!.Sales);
        Assert.Equal("Deleted", today.Sales[0].State);
        Assert.Equal(0, today.Count);
        Assert.Equal(0m, today.Net);

        // Not restorable, not editable, delete is idempotent-ish (404 after).
        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthFlows.DeleteWithCsrfAsync(client, $"/api/v1/sales/{saleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthFlows.PatchWithCsrfAsync(client, $"/api/v1/sales/{saleId}",
                new EditSaleRequest(null, null, 1m))).StatusCode);

        // The replacement duplicate is no longer blocked.
        Assert.Equal(HttpStatusCode.Created,
            (await PostSaleAsync(client, new CreateSaleRequest("666001", "Program", "AS01", 430m))).StatusCode);

        // Excluded from the summary/export (deleted rows are not history).
        var csv = await client.GetStringAsync("/api/v1/sales/me/export/current-year.csv");
        Assert.Contains("430.00", csv);
        Assert.DoesNotContain("421.00", csv);
    }

    [Fact]
    public async Task Same_day_edit_revalidates_rules_and_ownership()
    {
        var alice = await AgentAsync("w2-edit-a");
        var bob = await AgentAsync("w2-edit-b");
        var created = await PostSaleAsync(alice,
            new CreateSaleRequest("888001", "Upsell", "GC01", 89m));
        var saleId = (await created.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json))!.SaleId;

        // Bob cannot edit Alice's sale.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.PatchWithCsrfAsync(bob, $"/api/v1/sales/{saleId}",
                new EditSaleRequest(null, null, 99m))).StatusCode);

        // Invalid campaign for the type is refused.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await AuthFlows.PatchWithCsrfAsync(alice, $"/api/v1/sales/{saleId}",
                new EditSaleRequest(null, "AS01", null))).StatusCode);

        // A valid edit lands.
        var edited = await AuthFlows.PatchWithCsrfAsync(alice, $"/api/v1/sales/{saleId}",
            new EditSaleRequest(null, "AE01", 129m));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var dto = await edited.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json);
        Assert.Equal("AE01", dto!.Campaign);
        Assert.Equal(129m, dto.Amount);
    }

    // ── scenarios 5–6: the same-day window and historical corrections ───────

    [Fact]
    public async Task Yesterdays_sale_is_read_only_to_the_seller_but_correctable_by_management()
    {
        var client = await AgentAsync("w2-hist");
        var created = await PostSaleAsync(client,
            new CreateSaleRequest("999001", "Program", "AS01", 391m));
        var saleId = (await created.Content.ReadFromJsonAsync<SaleDto>(AuthFlows.Json))!.SaleId;

        // Age the sale one business day.
        await _factory.WithDbAsync(db => db.Sales
            .Where(s => s.Id == saleId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                x => x.BusinessDate,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)))));

        // Seller: edit and delete both refused with the window-closed code.
        var edit = await AuthFlows.PatchWithCsrfAsync(client, $"/api/v1/sales/{saleId}",
            new EditSaleRequest(null, null, 421m));
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Contains("sameDayWindowClosed", await edit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await AuthFlows.DeleteWithCsrfAsync(client, $"/api/v1/sales/{saleId}")).StatusCode);

        // Management corrects with a reason; before/after are preserved.
        var admin = _factory.CreateCookieClient();
        await AuthFlows.LoginAsync(
            admin, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);

        var missingReason = await AuthFlows.PostWithCsrfAsync(admin,
            $"/api/v1/sales/{saleId}/historical-correction",
            new HistoricalCorrectionRequest(null, null, 421m, ""));
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);

        var corrected = await AuthFlows.PostWithCsrfAsync(admin,
            $"/api/v1/sales/{saleId}/historical-correction",
            new HistoricalCorrectionRequest(null, null, 421m, "Customer signed updated agreement"));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        var correction = await _factory.WithDbAsync(db => db.SaleCorrections.SingleAsync());
        Assert.Equal(SaleCorrectionType.Amend, correction.CorrectionType);
        Assert.Contains("391", correction.BeforeJson);
        Assert.Contains("421", correction.AfterJson);
        Assert.Equal("Customer signed updated agreement", correction.Reason);

        var audit = await _factory.WithDbAsync(db => db.AuditEvents
            .SingleAsync(a => a.Action == "sales.historicalCorrection"));
        Assert.Equal(AuditRetentionClass.SevenYears, audit.RetentionClass);

        // Historical delete needs its own reason and leaves a Delete correction.
        var deleteResponse = await SendDeleteWithBodyAsync(admin,
            $"/api/v1/sales/{saleId}/historical-delete",
            new HistoricalDeleteRequest("Duplicate entry found in CRM"));
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.Equal(2, await _factory.WithDbAsync(db => db.SaleCorrections.CountAsync()));
    }

    private static async Task<HttpResponseMessage> SendDeleteWithBodyAsync<T>(
        HttpClient client, string url, T payload)
    {
        var token = await AuthFlows.GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url)
        {
            Content = JsonContent.Create(payload, options: AuthFlows.Json),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    // ── the $5,000 second confirmation ───────────────────────────────────────

    [Fact]
    public async Task Large_amounts_need_the_second_confirmation()
    {
        var client = await AgentAsync("w2-large");

        var refused = await PostSaleAsync(client,
            new CreateSaleRequest("121212", "Program", "AS01", 5000.01m));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("largeAmountConfirmationRequired", body);
        Assert.Contains("5000.01", body); // the confirmation shows the details

        // Exactly $5,000 does not trigger it.
        Assert.Equal(HttpStatusCode.Created,
            (await PostSaleAsync(client, new CreateSaleRequest("131313", "Program", "AS01", 5000m))).StatusCode);

        // Confirmed: created.
        Assert.Equal(HttpStatusCode.Created,
            (await PostSaleAsync(client, new CreateSaleRequest(
                "121212", "Program", "AS01", 5000.01m, null, true))).StatusCode);
    }

    // ── validation surface ───────────────────────────────────────────────────

    [Theory]
    [InlineData("48219a", "Program", "AS01", "100")]
    [InlineData("", "Program", "AS01", "100")]
    [InlineData("482193", "Program", "GC01", "100")]
    [InlineData("482193", "Upsell", "AS01", "100")]
    [InlineData("482193", "Upsell", "ZZ01", "100")]
    [InlineData("482193", "Program", "AS01", "0")]
    [InlineData("482193", "Program", "AS01", "-5")]
    [InlineData("482193", "Program", "AS01", "10.005")]
    public async Task Invalid_sales_are_refused(
        string cid, string type, string campaign, string amount)
    {
        var client = await AgentAsync($"w2-val-{Guid.NewGuid():N}"[..16]);
        var response = await PostSaleAsync(client,
            new CreateSaleRequest(cid, type, campaign, decimal.Parse(amount)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Sales.CountAsync()));
    }

    // ── aggregates ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Team_today_shows_agents_at_zero_and_management_only_with_sales()
    {
        var alice = await AgentAsync("w2-team-alice");
        _ = await AgentAsync("w2-team-zero");     // agent with no sales
        _ = await AuthFlows.CreateUserAsUnownedAdminAsync(
            _factory, "w2-team-super", Password, Roles.SalesSupervisor);

        _ = await PostSaleAsync(alice, new CreateSaleRequest("101010", "Program", "AS01", 300m));
        _ = await PostSaleAsync(alice, new CreateSaleRequest("101011", "Upsell", "GC01", 89m));

        // The owner sells one too (management with a sale appears).
        var owner = await AuthFlows.WorkingClientAsync(_factory,
            SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword, monitored: false);
        _ = await PostSaleAsync(owner, new CreateSaleRequest("202020", "Program", "AS01", 500m));

        var team = await owner.GetFromJsonAsync<TeamTodayResponse>(
            "/api/v1/sales/team/today", AuthFlows.Json);

        var names = team!.Rows.Select(r => r.DisplayName).ToList();
        Assert.Contains("Test w2-team-zero", names);          // agent at zero appears
        Assert.Contains("Test Owner", names);                 // management with a sale appears
        Assert.DoesNotContain("Test w2-team-super", names);   // management at zero does not

        // Sorted net descending: owner(500) then alice(389) then the zero agent.
        Assert.Equal("Test Owner", team.Rows[0].DisplayName);
        Assert.Equal("Test w2-team-alice", team.Rows[1].DisplayName);
        Assert.Equal(389m, team.Rows[1].Net);
        Assert.Equal(0m, team.Rows[^1].Net);

        // Drilldown: categories only — no CID, no timestamps.
        var breakdown = await owner.GetFromJsonAsync<TeamMemberBreakdownResponse>(
            $"/api/v1/sales/team/today/{team.Rows[1].UserId}/breakdown", AuthFlows.Json);
        Assert.Equal(2, breakdown!.Categories.Count);
        Assert.Equal(389m, breakdown.Net);
        var raw = await owner.GetStringAsync(
            $"/api/v1/sales/team/today/{team.Rows[1].UserId}/breakdown");
        Assert.DoesNotContain("101010", raw);
        Assert.DoesNotContain("cid", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Summary_reports_month_and_ytd_by_category()
    {
        var client = await AgentAsync("w2-summary");
        _ = await PostSaleAsync(client, new CreateSaleRequest("303030", "Program", "AS01", 400m));
        _ = await PostSaleAsync(client, new CreateSaleRequest("303031", "Upsell", "OS01", 150m));

        var summary = await client.GetFromJsonAsync<SalesSummaryResponse>(
            "/api/v1/sales/me/summary", AuthFlows.Json);
        Assert.Equal(2, summary!.YtdCount);
        Assert.Equal(550m, summary.YtdNet);
        Assert.Equal(2, summary.YtdCategories.Count);
        Assert.Equal(550m, summary.CurrentMonth.Net);
    }
}
