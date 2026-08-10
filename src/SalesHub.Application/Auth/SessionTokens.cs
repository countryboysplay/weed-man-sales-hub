using System.Security.Cryptography;
using System.Text;

namespace SalesHub.Application.Auth;

/// <summary>
/// Session verifier handling. The cookie carries (sessionId, verifier); the
/// database stores only SHA-256(verifier), so a database leak cannot mint
/// valid cookies. Comparison is constant-time.
/// </summary>
public static class SessionTokens
{
    public static string NewVerifier() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string verifier) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    public static bool Matches(string verifier, string storedHash)
    {
        var computed = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        byte[] stored;
        try
        {
            stored = Convert.FromHexString(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }
}
