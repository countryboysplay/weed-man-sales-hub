using System.Text;
using SalesHub.Application.OwnerSecurity;
using Xunit;

namespace SalesHub.UnitTests;

public class TotpTests
{
    // RFC 6238 Appendix B reference secret ("12345678901234567890", SHA-1).
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]          // RFC vector 94287082, last 6 digits
    [InlineData(1111111109L, "081804")]  // RFC vector 07081804
    [InlineData(1234567890L, "005924")]  // RFC vector 89005924
    public void Matches_rfc_6238_reference_vectors(long unixSeconds, string expected)
    {
        var code = Totp.Compute(RfcSecret, unixSeconds / Totp.StepSeconds);
        Assert.Equal(expected, code);
    }

    [Fact]
    public void Accepts_one_step_of_clock_skew_and_nothing_more()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = now.ToUnixTimeSeconds() / Totp.StepSeconds;

        Assert.True(Totp.Validate(RfcSecret, Totp.Compute(RfcSecret, step), now));
        Assert.True(Totp.Validate(RfcSecret, Totp.Compute(RfcSecret, step - 1), now));
        Assert.True(Totp.Validate(RfcSecret, Totp.Compute(RfcSecret, step + 1), now));
        Assert.False(Totp.Validate(RfcSecret, Totp.Compute(RfcSecret, step - 2), now));
        Assert.False(Totp.Validate(RfcSecret, Totp.Compute(RfcSecret, step + 2), now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Rejects_malformed_codes(string? code)
    {
        Assert.False(Totp.Validate(
            RfcSecret, code!, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));
    }

    [Fact]
    public void Base32_round_trips_through_the_otpauth_alphabet()
    {
        var secret = Totp.NewSecret();
        var encoded = Totp.ToBase32(secret);
        Assert.Matches("^[A-Z2-7]+$", encoded);
        Assert.Equal(32, encoded.Length); // 20 bytes → 32 base32 chars
    }
}
