using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using SalesHub.Application.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Master recovery credential verifier over ASP.NET Core Identity's
/// PasswordHasher (PBKDF2) — no custom password cryptography (docs/04).
/// </summary>
public sealed class IdentityMasterCredentialHasher : IMasterCredentialHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();

    public string Hash(string credential) => Hasher.HashPassword(Subject, credential);

    public bool Verify(string hash, string candidate) =>
        Hasher.VerifyHashedPassword(Subject, hash, candidate)
            is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
}

/// <summary>
/// TOTP-secret encryption at rest with ASP.NET Core Data Protection
/// (docs/04). The key ring lives outside the deployment directory in
/// production (Program.cs).
/// </summary>
public sealed class DataProtectionOwnerSecrets(IDataProtectionProvider provider) : IProtectedSecrets
{
    private readonly IDataProtector _protector =
        provider.CreateProtector("SalesHub.OwnerSecurity.Totp.v1");

    public string Protect(byte[] secret) =>
        Convert.ToBase64String(_protector.Protect(secret));

    public byte[] Unprotect(string protectedValue) =>
        _protector.Unprotect(Convert.FromBase64String(protectedValue));
}
