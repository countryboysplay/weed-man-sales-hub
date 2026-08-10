using SalesHub.Domain.Entities;

namespace SalesHub.Domain;

/// <summary>
/// Pure Add-Sale validation rules (CLAUDE.md §6). Campaigns: Program is
/// AS01; Upsell is GC01 | AE01 | OS01. Amount is positive decimal currency;
/// anything over the large-amount threshold needs an explicit second
/// confirmation carrying the sale details.
/// </summary>
public static class SalesRules
{
    public const decimal LargeAmountThreshold = 5_000m;
    public const decimal MaxAmount = 9_999_999_999.99m; // numeric(12,2) ceiling

    public static readonly IReadOnlyList<string> ProgramCampaigns = ["AS01"];
    public static readonly IReadOnlyList<string> UpsellCampaigns = ["GC01", "AE01", "OS01"];

    public static bool IsValidCid(string? cid) =>
        !string.IsNullOrEmpty(cid) && cid.Length <= 20 && cid.All(char.IsAsciiDigit);

    public static bool IsValidCampaign(SaleType saleType, string? campaign) =>
        saleType switch
        {
            SaleType.Program => ProgramCampaigns.Contains(
                campaign ?? "", StringComparer.OrdinalIgnoreCase),
            SaleType.Upsell => UpsellCampaigns.Contains(
                campaign ?? "", StringComparer.OrdinalIgnoreCase),
            _ => false,
        };

    public static bool IsValidAmount(decimal amount) =>
        amount > 0m && amount <= MaxAmount && decimal.Round(amount, 2) == amount;

    public static bool NeedsLargeAmountConfirmation(decimal amount) =>
        amount > LargeAmountThreshold;

    public static string NormalizeCampaign(string campaign) => campaign.ToUpperInvariant();
}
