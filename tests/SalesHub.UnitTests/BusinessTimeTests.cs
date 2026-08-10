using Microsoft.Extensions.Time.Testing;
using SalesHub.Domain;
using Xunit;

namespace SalesHub.UnitTests;

/// <summary>
/// America/Chicago business-time rules (CLAUDE.md §5) across DST, frozen with
/// FakeTimeProvider. 2026 transitions: spring forward March 8 (2:00→3:00),
/// fall back November 1 (2:00→1:00).
/// </summary>
public class BusinessTimeTests
{
    private static BusinessTime At(DateTimeOffset utcNow) =>
        new(new FakeTimeProvider(utcNow));

    [Fact]
    public void Business_date_is_central_not_utc()
    {
        // 03:30 UTC on July 2 is 22:30 CDT on July 1.
        var time = At(new DateTimeOffset(2026, 7, 2, 3, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 7, 1), time.Today);
    }

    [Fact]
    public void Business_year_boundary_is_central_midnight_not_utc()
    {
        // Jan 1 2027 04:00 UTC is still Dec 31 2026 22:00 CST — sequence year 2026.
        var time = At(new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero));
        Assert.Equal(2026, time.CurrentBusinessYear);

        // Jan 1 2027 06:00 UTC is Jan 1 2027 00:00 CST — the reset moment.
        Assert.Equal(2027, At(new DateTimeOffset(2027, 1, 1, 6, 0, 0, TimeSpan.Zero)).CurrentBusinessYear);
    }

    [Fact]
    public void Start_of_business_date_is_six_hours_utc_in_winter_five_in_summer()
    {
        var winter = At(default).StartOfBusinessDateUtc(new DateOnly(2026, 1, 15));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero), winter);

        var summer = At(default).StartOfBusinessDateUtc(new DateOnly(2026, 7, 15));
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 5, 0, 0, TimeSpan.Zero), summer);
    }

    [Fact]
    public void Next_business_midnight_crosses_spring_forward_correctly()
    {
        var time = At(default);

        // Mar 7 23:00 CST → next midnight is Mar 8 00:00 CST (06:00 UTC).
        var lateMarch7 = new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero),
            time.NextBusinessMidnightUtc(lateMarch7));

        // During March 8 (the 23-hour day) → next midnight is Mar 9 00:00 CDT
        // (05:00 UTC): the UTC gap between the two midnights is 23 hours.
        var noonMarch8 = new DateTimeOffset(2026, 3, 8, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 9, 5, 0, 0, TimeSpan.Zero),
            time.NextBusinessMidnightUtc(noonMarch8));
    }

    [Fact]
    public void Invalid_spring_forward_wall_time_shifts_forward()
    {
        // 2:30 AM on March 8 2026 does not exist in Chicago; it lands at 3:30 CDT.
        var utc = At(default).ToUtc(new DateTime(2026, 3, 8, 2, 30, 0));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void Ambiguous_fall_back_wall_time_takes_first_occurrence()
    {
        // 1:30 AM on November 1 2026 happens twice; the first (CDT) wins so a
        // 12:30 AM-style job never double-fires.
        var utc = At(default).ToUtc(new DateTime(2026, 11, 1, 1, 30, 0));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void Never_a_fixed_offset()
    {
        var january = At(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)).Now.Offset;
        var july = At(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)).Now.Offset;
        Assert.Equal(TimeSpan.FromHours(-6), january);
        Assert.Equal(TimeSpan.FromHours(-5), july);
    }
}
