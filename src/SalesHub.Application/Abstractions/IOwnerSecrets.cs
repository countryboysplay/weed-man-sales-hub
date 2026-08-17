namespace SalesHub.Application.Abstractions;

/// <summary>One-way verifier for the master recovery credential (docs/04):
/// hash on setup, verify on use, never reversible, never logged.</summary>
public interface IMasterCredentialHasher
{
    string Hash(string credential);

    bool Verify(string hash, string candidate);
}

/// <summary>Small protected secret payloads (TOTP secret) encrypted at rest
/// with ASP.NET Core Data Protection (docs/04). Not for passwords.</summary>
public interface IProtectedSecrets
{
    string Protect(byte[] secret);

    byte[] Unprotect(string protectedValue);
}
