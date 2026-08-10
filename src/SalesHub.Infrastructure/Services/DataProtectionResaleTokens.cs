using Microsoft.AspNetCore.DataProtection;
using SalesHub.Application.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>Resale confirmation tokens over ASP.NET Core Data Protection —
/// stateless, tamper-proof, ten-minute lifetime.</summary>
public sealed class DataProtectionResaleTokens(IDataProtectionProvider provider)
    : IResaleConfirmationTokens
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ITimeLimitedDataProtector _protector = provider
        .CreateProtector("SalesHub.ResaleConfirmation.v1")
        .ToTimeLimitedDataProtector();

    public string Issue(Guid sellerUserId, string cid, Guid priorSaleId) =>
        _protector.Protect($"{sellerUserId:N}|{cid}|{priorSaleId:N}", Lifetime);

    public Guid? ValidatePriorSaleId(string token, Guid sellerUserId, string cid)
    {
        string plaintext;
        try
        {
            plaintext = _protector.Unprotect(token);
        }
        catch (Exception)
        {
            return null; // tampered, expired, or foreign token — same answer
        }

        var parts = plaintext.Split('|');
        if (parts.Length != 3
            || !string.Equals(parts[0], sellerUserId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(parts[1], cid, StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[2], "N", out var priorSaleId))
        {
            return null;
        }

        return priorSaleId;
    }
}
