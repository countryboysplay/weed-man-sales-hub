namespace SalesHub.Contracts.Sales;

public sealed record CreateSaleRequest(
    string Cid,
    string SaleType,
    string Campaign,
    decimal Amount,
    string? DuplicateResaleConfirmation = null,
    bool LargeAmountConfirmed = false);

public sealed record EditSaleRequest(
    string? SaleType,
    string? Campaign,
    decimal? Amount,
    bool LargeAmountConfirmed = false);

public sealed record HistoricalCorrectionRequest(
    string? SaleType,
    string? Campaign,
    decimal? Amount,
    string Reason);

public sealed record HistoricalDeleteRequest(string Reason);

public sealed record SaleDto(
    Guid SaleId,
    string Cid,
    string SaleType,
    string Campaign,
    decimal Amount,
    string BusinessDate,
    DateTimeOffset CreatedAt,
    string State,
    bool EditableToday);

public sealed record MyTodayResponse(
    IReadOnlyList<SaleDto> Sales,
    int Count,
    decimal Net);

public sealed record TeamTodayRow(
    Guid UserId,
    string DisplayName,
    string Role,
    int Count,
    decimal Net);

public sealed record TeamTodayResponse(
    IReadOnlyList<TeamTodayRow> Rows,
    decimal TeamNet,
    int TeamCount,
    string BusinessDate);

public sealed record CategoryBreakdownRow(
    string SaleType,
    string Campaign,
    int Count,
    decimal Net);

public sealed record TeamMemberBreakdownResponse(
    Guid UserId,
    string DisplayName,
    IReadOnlyList<CategoryBreakdownRow> Categories,
    int Count,
    decimal Net);

public sealed record MonthSummary(
    string Month,            // YYYY-MM
    int Count,
    decimal Net,
    IReadOnlyList<CategoryBreakdownRow> Categories);

public sealed record SalesSummaryResponse(
    Guid UserId,
    string DisplayName,
    int Year,
    MonthSummary CurrentMonth,
    IReadOnlyList<MonthSummary> Months,
    int YtdCount,
    decimal YtdNet,
    IReadOnlyList<CategoryBreakdownRow> YtdCategories);
