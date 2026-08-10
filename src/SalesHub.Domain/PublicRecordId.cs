using System.Text.RegularExpressions;

namespace SalesHub.Domain;

/// <summary>
/// Human-readable record IDs like NOTE-2026-00001 (CLAUDE.md §18).
/// Yearly sequences reset January 1 America/Chicago; values come from the
/// DB-backed <c>public_id_sequences</c> table, never COUNT(*)+1 (docs/12).
/// Internal PKs stay UUID; these are display/reference identifiers.
/// </summary>
public static partial class PublicRecordId
{
    public static readonly IReadOnlyList<string> KnownPrefixes =
    [
        "REC", "NOTE", "TECH", "TO", "BRK", "SCH",
        "PRS", "SUP", "PROD", "STAGE", "ROLL", "EXP",
    ];

    [GeneratedRegex(@"^[A-Z]{2,8}-\d{4}-\d{5}$")]
    private static partial Regex FormatPattern();

    public static string Format(string prefix, int year, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        // Five digits zero-padded per docs/12; a sequence past 99999 keeps
        // going rather than wrapping — uniqueness beats fixed width.
        return $"{prefix.ToUpperInvariant()}-{year:D4}-{value:D5}";
    }

    public static bool IsWellFormed(string candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && FormatPattern().IsMatch(candidate);
}
