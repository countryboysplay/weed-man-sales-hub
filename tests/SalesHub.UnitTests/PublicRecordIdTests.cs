using SalesHub.Domain;
using Xunit;

namespace SalesHub.UnitTests;

public class PublicRecordIdTests
{
    [Theory]
    [InlineData("NOTE", 2026, 1, "NOTE-2026-00001")]
    [InlineData("TECH", 2026, 112, "TECH-2026-00112")]
    [InlineData("EXP", 2026, 311, "EXP-2026-00311")]
    [InlineData("to", 2027, 73, "TO-2027-00073")]
    public void Formats_with_zero_padded_five_digits(
        string prefix, int year, int value, string expected) =>
        Assert.Equal(expected, PublicRecordId.Format(prefix, year, value));

    [Fact]
    public void A_sequence_past_five_digits_keeps_growing_instead_of_wrapping() =>
        Assert.Equal("SUP-2026-123456", PublicRecordId.Format("SUP", 2026, 123456));

    [Theory]
    [InlineData("NOTE-2026-00001", true)]
    [InlineData("STAGE-2026-00141", true)]
    [InlineData("NOTE-26-00001", false)]
    [InlineData("note-2026-00001", false)]
    [InlineData("NOTE-2026-1", false)]
    [InlineData("", false)]
    public void Recognizes_well_formed_ids(string candidate, bool expected) =>
        Assert.Equal(expected, PublicRecordId.IsWellFormed(candidate));

    [Fact]
    public void Refuses_nonsense_input()
    {
        Assert.ThrowsAny<ArgumentException>(() => PublicRecordId.Format("", 2026, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PublicRecordId.Format("NOTE", 1999, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PublicRecordId.Format("NOTE", 2026, 0));
    }
}
