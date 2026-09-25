using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The half of `ripcord init` that makes re-running it safe. Everything the interview does not
/// ask about has to come out the other side unchanged — including the parts this binary has
/// never heard of, which is the case a round trip through the object model gets wrong.
public class YamlSectionsTests
{
    private const string File = """
        # The file's own header.
        schema_version: 1

        node:
          hostname: HV-REPLICA-01

        # Everything below is about alerting, and was written by hand.
        alerting:
          enabled: true
          # A relay that only answers on the management network.
          smtp:
            host: relay.example.com

        something_a_later_version_added:
          with: a value
        """;

    private static IReadOnlyList<string> Keys(string text) =>
        [.. YamlSections.Split(text).Select(section => section.Key)];

    [Fact]
    public void Every_top_level_key_becomes_a_section() =>
        Assert.Equal(
            ["schema_version", "node", "alerting", "something_a_later_version_added"],
            Keys(File));

    /// A paragraph above `alerting:` was written about alerting. Leaving it with the section
    /// above would move somebody's explanation away from the thing it explains.
    [Fact]
    public void A_comment_block_travels_with_the_section_below_it()
    {
        YamlSection alerting = YamlSections.Split(File).Single(s => s.Key == "alerting");

        Assert.Equal("# Everything below is about alerting, and was written by hand.",
            alerting.Lines[0]);

        Assert.Contains("  # A relay that only answers on the management network.",
            alerting.Lines);
    }

    /// The whole point: a key no version of this binary models still comes back.
    [Fact]
    public void A_section_this_binary_does_not_understand_is_still_a_section()
    {
        YamlSection unknown = YamlSections
            .Split(File)
            .Single(s => s.Key == "something_a_later_version_added");

        Assert.Equal(["something_a_later_version_added:", "  with: a value"], unknown.Lines);
    }

    /// Put back together, the sections are the file again. Without this the splitter could be
    /// losing a line anywhere and every other assertion would still pass.
    [Fact]
    public void The_sections_joined_back_together_are_the_original_file()
    {
        IReadOnlyList<YamlSection> sections = YamlSections.Split(File);

        string rejoined = string.Join(
            "\n",
            sections.SelectMany(section => section.Lines));

        Assert.Equal(File.ReplaceLineEndings("\n"), rejoined);
    }

    [Fact]
    public void What_the_interview_owns_can_be_taken_out() =>
        Assert.Equal(
            ["alerting", "something_a_later_version_added"],
            YamlSections
                .Except(YamlSections.Split(File), ["schema_version", "node"])
                .Select(section => section.Key));

    /// A list item, an indented key and a comment are not top-level keys. Reading one as a
    /// section boundary would cut a block in half and carry over something unparseable.
    [Fact]
    public void Nothing_indented_is_mistaken_for_a_key() =>
        Assert.Equal(
            ["vms"],
            Keys("""
                vms:
                  - name: VM-DC-01
                    priority: P1
                  # not a key
                """));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# nothing but a comment\n")]
    public void A_file_with_no_key_has_no_sections(string? text) =>
        Assert.Empty(YamlSections.Split(text));

    private const string Indented = """
        replication:
          expected_role: replica
          # test_failover_switch: vSwitch-ISOLATED

        # The read-only pair channel.
        listener:
          enabled: true
          # The snapshot 'ripcord status' writes.
          # snapshot_path: C:\Ripcord\state.json

        storage:
          data_volume: "D:"

        # Notification, off by default.
        # alerting:
        #   enabled: true
        """;

    /// Indented under a section's keys, a comment is about that section, whatever follows.
    [Fact]
    public void An_indented_comment_at_the_end_of_a_section_stays_with_it()
    {
        IReadOnlyList<YamlSection> sections = YamlSections.Split(Indented);

        Assert.Equal(
            ["  # test_failover_switch: vSwitch-ISOLATED"],
            sections.Single(s => s.Key == "replication").Trailer);
        Assert.Equal(
            ["  # The snapshot 'ripcord status' writes.", "  # snapshot_path: C:\\Ripcord\\state.json"],
            sections.Single(s => s.Key == "listener").Trailer);
        Assert.Equal("# The read-only pair channel.", sections.Single(s => s.Key == "listener").Lines[0]);
    }

    [Fact]
    public void The_comment_block_after_the_last_section_is_the_epilogue_not_part_of_it()
    {
        Assert.Equal(
            ["storage:", "  data_volume: \"D:\""],
            YamlSections.Split(Indented).Single(s => s.Key == "storage").Lines);
        Assert.Equal(
            ["# Notification, off by default.", "# alerting:", "#   enabled: true"],
            YamlSections.Epilogue(Indented));
    }

    [Fact]
    public void A_file_ending_on_a_section_has_no_epilogue() =>
        Assert.Empty(YamlSections.Epilogue(File));
}
