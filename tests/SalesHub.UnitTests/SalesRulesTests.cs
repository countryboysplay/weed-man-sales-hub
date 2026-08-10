using SalesHub.Domain;
using SalesHub.Domain.Entities;
using Xunit;

namespace SalesHub.UnitTests;

public class SalesRulesTests
{
    [Theory]
    [InlineData("482193", true)]
    [InlineData("1", true)]
    [InlineData("12345678901234567890", true)]   // 20 digits — the cap
    [InlineData("123456789012345678901", false)] // 21 digits
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("48219a", false)]
    [InlineData("48 219", false)]
    [InlineData("-48219", false)]
    [InlineData("48219.0", false)]
    public void Cid_must_be_a_numeric_string(string? cid, bool valid) =>
        Assert.Equal(valid, SalesRules.IsValidCid(cid));

    [Theory]
    [InlineData(SaleType.Program, "AS01", true)]
    [InlineData(SaleType.Program, "as01", true)]  // normalized later
    [InlineData(SaleType.Program, "GC01", false)]
    [InlineData(SaleType.Upsell, "GC01", true)]
    [InlineData(SaleType.Upsell, "AE01", true)]
    [InlineData(SaleType.Upsell, "OS01", true)]
    [InlineData(SaleType.Upsell, "AS01", false)]
    [InlineData(SaleType.Upsell, "XX99", false)]
    [InlineData(SaleType.Program, "", false)]
    [InlineData(SaleType.Program, null, false)]
    public void Campaign_must_match_the_sale_type(SaleType type, string? campaign, bool valid) =>
        Assert.Equal(valid, SalesRules.IsValidCampaign(type, campaign));

    [Theory]
    [InlineData("421.00", true)]
    [InlineData("0.01", true)]
    [InlineData("9999999999.99", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("421.005", false)]           // sub-cent precision
    [InlineData("10000000000.00", false)]    // beyond numeric(12,2)
    public void Amount_is_positive_currency_with_two_decimals(string raw, bool valid) =>
        Assert.Equal(valid, SalesRules.IsValidAmount(decimal.Parse(raw)));

    [Theory]
    [InlineData("5000.00", false)]  // exactly the threshold: no confirmation
    [InlineData("5000.01", true)]
    [InlineData("421.00", false)]
    public void Only_amounts_over_5000_need_the_second_confirmation(string raw, bool needs) =>
        Assert.Equal(needs, SalesRules.NeedsLargeAmountConfirmation(decimal.Parse(raw)));
}
