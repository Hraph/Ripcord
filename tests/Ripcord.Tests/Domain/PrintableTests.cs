using Ripcord.Domain;

namespace Ripcord.Tests.Domain;

/// Names arrive from the other host and from Hyper-V, and end up on a terminal during an
/// incident and in a mail subject. Neither place may be steered by whoever chose the name.
public class PrintableTests
{
    [Fact]
    public void An_ordinary_name_is_left_exactly_as_it_is() =>
        Assert.Equal("VM-DC-01", Printable.Of("VM-DC-01"));

    /// The attack this closes: an escape sequence in a VM name repainting the verdict the
    /// operator is reading.
    [Fact]
    public void An_escape_sequence_cannot_survive_into_the_output() =>
        Assert.Equal("VM-DC-01?[2K READY", Printable.Of("VM-DC-01\u001b[2K READY"));

    /// A carriage return in a mail subject is a header of the sender's choosing.
    [Theory]
    [InlineData("a\rb", "a?b")]
    [InlineData("a\nb", "a?b")]
    [InlineData("a\tb", "a?b")]
    [InlineData("a\u0000b", "a?b")]
    [InlineData("a\u007fb", "a?b")]
    [InlineData("a\u009bb", "a?b")]
    public void Every_control_character_is_replaced(string given, string expected) =>
        Assert.Equal(expected, Printable.Of(given));

    /// Replaced rather than removed: a tampered name has to read as tampered instead of
    /// quietly becoming the name it was imitating.
    [Fact]
    public void The_length_is_kept_so_a_tampered_name_looks_tampered() =>
        Assert.Equal(
            "VM-DC-01".Length + 1, Printable.Of("VM-DC-01\u001b").Length);

    [Fact]
    public void Nothing_is_an_empty_string_rather_than_null() =>
        Assert.Equal("", Printable.Of(null));

    /// An absent switch name means "bound to no switch", which is a critical rule of its own.
    /// Turning it into an empty string would answer a different question.
    [Fact]
    public void Null_stays_null_where_absence_is_a_fact() => Assert.Null(Printable.OrNull(null));

    [Fact]
    public void A_present_value_is_cleaned_the_same_way_either_side() =>
        Assert.Equal("vSwitch?PROD", Printable.OrNull("vSwitch\u001bPROD"));

    /// An accented host name is ordinary text, not a control character.
    [Fact]
    public void Text_outside_ascii_is_left_alone() =>
        Assert.Equal("HV-R\u00c9PLICA-01", Printable.Of("HV-R\u00c9PLICA-01"));
}
