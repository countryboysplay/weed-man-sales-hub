using System.Security.Cryptography;

namespace SalesHub.Application.OwnerSecurity;

/// <summary>
/// RFC 6238 TOTP (SHA-1, 6 digits, 30-second step) with ±1 step of clock
/// skew (docs/04). No external dependency — the algorithm is a page of code
/// and this way it is auditable in-repo.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static bool Validate(byte[] secret, string code, DateTimeOffset nowUtc)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var step = nowUtc.ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            if (FixedTimeEquals(Compute(secret, step + offset), code))
            {
                return true;
            }
        }

        return false;
    }

    public static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(secret, counter);
        var dynamicOffset = hash[^1] & 0x0F;
        var binary =
            ((hash[dynamicOffset] & 0x7F) << 24)
            | (hash[dynamicOffset + 1] << 16)
            | (hash[dynamicOffset + 2] << 8)
            | hash[dynamicOffset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var mismatch = a.Length ^ b.Length;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            mismatch |= a[i] ^ b[i];
        }

        return mismatch == 0;
    }

    /// <summary>RFC 4648 Base32, for the one-time otpauth setup URI.</summary>
    public static string ToBase32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new System.Text.StringBuilder((data.Length + 4) / 5 * 8);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            result.Append(alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return result.ToString();
    }
}
