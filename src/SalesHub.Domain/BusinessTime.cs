namespace SalesHub.Domain;

/// <summary>
/// Owns every America/Chicago conversion in the system (CLAUDE.md §5).
/// Storage is always UTC; business dates, business midnight, and the
/// January-1 sequence reset are all evaluated here — never with a fixed
/// UTC-6 offset, because Central Time observes daylight saving time.
/// Built on <see cref="TimeProvider"/> so tests can freeze time across
/// DST transitions.
/// </summary>
public sealed class BusinessTime
{
    public const string BusinessTimeZoneId = "America/Chicago";

    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    public BusinessTime(TimeProvider clock)
    {
        _clock = clock;
        _zone = TimeZoneInfo.FindSystemTimeZoneById(BusinessTimeZoneId);
    }

    public DateTimeOffset UtcNow => _clock.GetUtcNow();

    /// <summary>The current wall-clock time in America/Chicago.</summary>
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone);

    /// <summary>The business date an instant belongs to.</summary>
    public DateOnly BusinessDateOf(DateTimeOffset utcInstant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcInstant, _zone).DateTime);

    public DateOnly Today => BusinessDateOf(_clock.GetUtcNow());

    /// <summary>The sequence/business year an instant belongs to (resets Jan 1 CT).</summary>
    public int BusinessYearOf(DateTimeOffset utcInstant) => BusinessDateOf(utcInstant).Year;

    public int CurrentBusinessYear => BusinessYearOf(_clock.GetUtcNow());

    /// <summary>
    /// The UTC instant at which a business date starts (local midnight).
    /// Handles DST edges: a skipped local midnight moves forward with the
    /// transition; an ambiguous one resolves to the first occurrence.
    /// </summary>
    public DateTimeOffset StartOfBusinessDateUtc(DateOnly businessDate) =>
        ToUtc(businessDate.ToDateTime(TimeOnly.MinValue));

    /// <summary>The UTC instant of the next business midnight after the given instant.</summary>
    public DateTimeOffset NextBusinessMidnightUtc(DateTimeOffset utcInstant) =>
        StartOfBusinessDateUtc(BusinessDateOf(utcInstant).AddDays(1));

    /// <summary>
    /// Converts a local America/Chicago wall time to UTC, handling invalid
    /// (spring-forward gap) and ambiguous (fall-back repeat) times: invalid
    /// times shift forward by the DST delta; ambiguous times take the earlier
    /// (daylight) offset so a 12:30 AM job never runs twice.
    /// </summary>
    public DateTimeOffset ToUtc(DateTime localWallTime)
    {
        var unspecified = DateTime.SpecifyKind(localWallTime, DateTimeKind.Unspecified);
        if (_zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        if (_zone.IsAmbiguousTime(unspecified))
        {
            var offsets = _zone.GetAmbiguousTimeOffsets(unspecified);
            var earlier = offsets.Max(); // larger offset = daylight time = earlier UTC instant
            return new DateTimeOffset(unspecified, earlier).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, _zone), TimeSpan.Zero);
    }

    /// <summary>Local America/Chicago wall time for a UTC instant.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset utcInstant) =>
        TimeZoneInfo.ConvertTime(utcInstant, _zone);
}
