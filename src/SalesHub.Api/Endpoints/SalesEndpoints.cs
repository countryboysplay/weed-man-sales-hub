using System.Text.Json;
using SalesHub.Api.Auth;
using SalesHub.Application.Sales;
using SalesHub.Contracts.Sales;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// The Sales module (CLAUDE.md §6, docs/02). The whole surface sits behind
/// the monitored-work gate: agents need a Verified idle capability; the
/// non-monitored management roles pass through. All rules are enforced in
/// SalesService — these handlers translate outcomes to HTTP.
/// </summary>
public static class SalesEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder api)
    {
        var sales = api.MapGroup("/sales")
            .RequireAuthorization(Policies.Employee, Policies.MonitoredWorkSession);

        sales.MapPost("/", CreateAsync);
        sales.MapGet("/me/today", MyTodayAsync);
        sales.MapGet("/me/summary", MySummaryAsync);
        sales.MapGet("/me/export/current-year.csv", ExportAsync);
        sales.MapGet("/team/today", TeamTodayAsync);
        sales.MapGet("/team/today/{userId:guid}/breakdown", BreakdownAsync);
        sales.MapPatch("/{id:guid}", EditAsync);
        sales.MapDelete("/{id:guid}", DeleteAsync);

        sales.MapPost("/{id:guid}/historical-correction", CorrectAsync)
            .RequireAuthorization(Policies.Management);
        sales.MapDelete("/{id:guid}/historical-delete", HistoricalDeleteAsync)
            .RequireAuthorization(Policies.Management);
        sales.MapGet("/users/{userId:guid}/summary", UserSummaryAsync)
            .RequireAuthorization(Policies.Management);

        return api;
    }

    private static async Task<IResult> CreateAsync(
        CreateSaleRequest request,
        HttpContext http,
        SalesService salesService,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);

        // Idempotent replay (acceptance scenario 2): same key + same payload
        // returns the original outcome; same key + different payload refuses.
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        string? requestHash = null;
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (idempotencyKey.Length > 128)
            {
                return Problems.Validation(http, "Idempotency-Key is limited to 128 characters.");
            }

            requestHash = IdempotencyService.HashRequest(JsonSerializer.Serialize(request, Json));
            var replay = await idempotency.TryReplayAsync(userId, idempotencyKey, requestHash, ct);
            if (replay is { HashMismatch: true })
            {
                return Problems.Conflict(http,
                    "This Idempotency-Key was already used with a different payload.",
                    "idempotencyKeyReuse");
            }

            if (replay is not null)
            {
                return Results.Content(
                    replay.ResponseJson ?? "{}", "application/json",
                    statusCode: replay.StatusCode);
            }
        }

        if (!TryParseSaleType(request.SaleType, out var saleType))
        {
            return Problems.Validation(http, "saleType must be Program or Upsell.");
        }

        var outcome = await salesService.CreateAsync(new SalesService.CreateInput(
            userId, request.Cid?.Trim() ?? "", saleType, request.Campaign?.Trim() ?? "",
            request.Amount, request.DuplicateResaleConfirmation,
            request.LargeAmountConfirmed, session.Id), ct);

        var result = outcome.Failure switch
        {
            SalesService.Failure.None => Results.Created(
                $"/api/v1/sales/{outcome.Sale!.Id}", ToDto(outcome.Sale, editableToday: true)),
            SalesService.Failure.LargeAmountConfirmationRequired =>
                LargeAmountProblem(http, request),
            SalesService.Failure.DuplicateProgram => DuplicateProblem(http, outcome.Duplicate!),
            SalesService.Failure.DuplicateUpsell => Problems.Conflict(http,
                outcome.Error!, "duplicate-upsell"),
            _ => Problems.Validation(http, outcome.Error!),
        };

        if (requestHash is not null && outcome.Failure == SalesService.Failure.None)
        {
            // Only successful creates are memoized: a refused attempt must be
            // retryable after the client fixes the problem.
            await idempotency.StoreAsync(userId, idempotencyKey!, "sales.create", requestHash,
                StatusCodes.Status201Created,
                JsonSerializer.Serialize(ToDto(outcome.Sale!, true), Json), ct);
        }

        return result;
    }

    private static IResult LargeAmountProblem(HttpContext http, CreateSaleRequest request) =>
        Results.Problem(
            title: "Confirm this large sale.",
            detail: $"The amount exceeds {SalesRules.LargeAmountThreshold:C0}. " +
                    "Confirm the details and resubmit with largeAmountConfirmed.",
            statusCode: StatusCodes.Status409Conflict,
            type: "https://saleshub/problems/large-amount-confirmation",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "largeAmountConfirmationRequired",
                ["correlationId"] = http.TraceIdentifier,
                ["saleDetails"] = new
                {
                    cid = request.Cid,
                    saleType = request.SaleType,
                    campaign = request.Campaign,
                    amount = request.Amount,
                },
            });

    private static IResult DuplicateProblem(HttpContext http, SalesService.DuplicateInfo info) =>
        Results.Problem(
            title: "A program sale already exists for this CID this year.",
            detail: "Confirm the prior sale was canceled and this is a resale to continue.",
            statusCode: StatusCodes.Status409Conflict,
            type: "https://saleshub/problems/duplicate-sale",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "duplicateSale",
                ["correlationId"] = http.TraceIdentifier,
                ["priorSale"] = new
                {
                    saleId = info.PriorSaleId,
                    businessDate = info.PriorBusinessDate.ToString("yyyy-MM-dd"),
                    amount = info.PriorAmount,
                },
                ["confirmationToken"] = info.ConfirmationToken,
            });

    private static async Task<IResult> MyTodayAsync(
        HttpContext http, SalesService salesService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (sales, count, net) = await salesService.MyTodayAsync(userId, ct);
        return Results.Ok(new MyTodayResponse(
            sales.Select(s => ToDto(s, s.State == SaleState.Active)).ToList(), count, net));
    }

    private static async Task<IResult> MySummaryAsync(
        HttpContext http, SalesService salesService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return Results.Ok(await BuildSummaryAsync(userId, http, salesService, ct));
    }

    private static async Task<IResult> UserSummaryAsync(
        Guid userId, HttpContext http, SalesService salesService, CancellationToken ct) =>
        Results.Ok(await BuildSummaryAsync(userId, http, salesService, ct));

    private static async Task<SalesSummaryResponse> BuildSummaryAsync(
        Guid userId, HttpContext http, SalesService salesService, CancellationToken ct)
    {
        var identity = http.RequestServices
            .GetRequiredService<SalesHub.Application.Abstractions.IIdentityService>();
        var user = await identity.FindByIdAsync(userId, ct);
        var summary = await salesService.SummaryAsync(userId, ct);

        var months = summary.Months
            .Select(m => new MonthSummary(
                $"{summary.Year:D4}-{m.Month:D2}", m.Count, m.Net,
                m.Categories.Select(ToCategory).ToList()))
            .ToList();
        var currentMonthNumber = DateTimeOffset.UtcNow.Month; // display fallback only
        var current = months.LastOrDefault()
            ?? new MonthSummary($"{summary.Year:D4}-{currentMonthNumber:D2}", 0, 0m, []);

        return new SalesSummaryResponse(
            userId, user?.DisplayName ?? "", summary.Year, current, months,
            summary.YtdCount, summary.YtdNet,
            summary.YtdCategories.Select(ToCategory).ToList());
    }

    private static async Task<IResult> ExportAsync(
        HttpContext http, SalesService salesService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var csv = await salesService.ExportCurrentYearCsvAsync(userId, ct);
        return Results.Text(csv, "text/csv", System.Text.Encoding.UTF8);
    }

    private static async Task<IResult> TeamTodayAsync(
        SalesService salesService, BusinessTime businessTime, CancellationToken ct)
    {
        var rows = await salesService.TeamTodayAsync(ct);
        return Results.Ok(new TeamTodayResponse(
            rows.Select(r => new TeamTodayRow(r.UserId, r.DisplayName, r.Role, r.Count, r.Net)).ToList(),
            rows.Sum(r => r.Net),
            rows.Sum(r => r.Count),
            businessTime.Today.ToString("yyyy-MM-dd")));
    }

    private static async Task<IResult> BreakdownAsync(
        Guid userId, HttpContext http, SalesService salesService, CancellationToken ct)
    {
        var identity = http.RequestServices
            .GetRequiredService<SalesHub.Application.Abstractions.IIdentityService>();
        var user = await identity.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.NotFound(http, "User not found.");
        }

        var categories = await salesService.TodayBreakdownAsync(userId, ct);
        return Results.Ok(new TeamMemberBreakdownResponse(
            userId, user.DisplayName,
            categories.Select(ToCategory).ToList(),
            categories.Sum(c => c.Count),
            categories.Sum(c => c.Net)));
    }

    private static async Task<IResult> EditAsync(
        Guid id, EditSaleRequest request, HttpContext http,
        SalesService salesService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        SaleType? saleType = null;
        if (request.SaleType is { } rawType)
        {
            if (!TryParseSaleType(rawType, out var parsed))
            {
                return Problems.Validation(http, "saleType must be Program or Upsell.");
            }

            saleType = parsed;
        }

        var outcome = await salesService.EditSameDayAsync(id, userId, new SalesService.EditInput(
            saleType, request.Campaign, request.Amount, request.LargeAmountConfirmed), ct);
        return FromMutation(outcome, http);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, HttpContext http, SalesService salesService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return FromMutation(await salesService.DeleteSameDayAsync(id, userId, ct), http);
    }

    private static async Task<IResult> CorrectAsync(
        Guid id, HistoricalCorrectionRequest request, HttpContext http,
        SalesService salesService, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        SaleType? saleType = null;
        if (request.SaleType is { } rawType)
        {
            if (!TryParseSaleType(rawType, out var parsed))
            {
                return Problems.Validation(http, "saleType must be Program or Upsell.");
            }

            saleType = parsed;
        }

        var outcome = await salesService.CorrectHistoricalAsync(id, new SalesService.CorrectionInput(
            saleType, request.Campaign, request.Amount, request.Reason, userId, session.Id), ct);
        return FromMutation(outcome, http);
    }

    private static async Task<IResult> HistoricalDeleteAsync(
        Guid id,
        [Microsoft.AspNetCore.Mvc.FromBody] HistoricalDeleteRequest request,
        HttpContext http,
        SalesService salesService, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        return FromMutation(
            await salesService.DeleteHistoricalAsync(id, request.Reason, userId, session.Id, ct),
            http);
    }

    private static IResult FromMutation(SalesService.MutationOutcome outcome, HttpContext http) =>
        outcome.Failure switch
        {
            SalesService.Failure.None => outcome.Sale is { } sale
                ? Results.Ok(ToDto(sale, sale.State == SaleState.Active))
                : Results.NoContent(),
            SalesService.Failure.NotFound => Problems.NotFound(http, outcome.Error!),
            SalesService.Failure.Forbidden => Problems.Forbidden(http, outcome.Error!),
            SalesService.Failure.NotEditableToday => Problems.Forbidden(
                http, outcome.Error!, "sameDayWindowClosed"),
            SalesService.Failure.LargeAmountConfirmationRequired => Problems.Conflict(
                http, outcome.Error!, "largeAmountConfirmationRequired"),
            SalesService.Failure.DuplicateProgram or SalesService.Failure.DuplicateUpsell =>
                Problems.Conflict(http, outcome.Error!, "duplicateSale"),
            _ => Problems.Validation(http, outcome.Error!),
        };

    private static bool TryParseSaleType(string raw, out SaleType saleType) =>
        Enum.TryParse(raw, ignoreCase: true, out saleType)
            && Enum.IsDefined(saleType);

    private static SaleDto ToDto(Sale sale, bool editableToday) => new(
        sale.Id, sale.Cid, sale.SaleType.ToString(), sale.Campaign, sale.Amount,
        sale.BusinessDate.ToString("yyyy-MM-dd"), sale.CreatedAtUtc,
        sale.State.ToString(), editableToday);

    private static CategoryBreakdownRow ToCategory(SalesService.CategoryRow row) => new(
        row.SaleType.ToString(), row.Campaign, row.Count, row.Net);
}
