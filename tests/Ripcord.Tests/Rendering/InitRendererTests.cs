using Ripcord.Cli.Rendering;
using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Rendering;

/// The layout of the one interactive screen in the binary.
///
/// What is asserted here is the three rules it was rebuilt to: a section is headed once and
/// not per question, a small fixed set of answers sits beside the prompt instead of becoming a
/// numbered list, and a list read off the host is numbered because there the number is the
/// answer.
public class InitRendererTests
{
    private static readonly InterviewQuestion Binary = new(
        "dc:VM-DC-01",
        "VM-DC-01  domain controller",
        "n",
        ["y", "n"],
        [],
        ConfigurationInterview.Groups.Priorities);

    private static readonly InterviewQuestion Listed = new(
        "switch",
        "Which switch?",
        null,
        ["vSwitch-PROD  (external)", "vSwitch-ISOLATED  (private)"],
        [],
        ConfigurationInterview.Groups.Replication,
        Listed: true);

    /// `1 y / 2 n` above a yes-or-no question is ceremony, and ceremony is what gets skipped.
    [Fact]
    public void A_small_set_of_answers_sits_beside_the_prompt()
    {
        Assert.Equal("  VM-DC-01  domain controller  y/n [n] > ", InitRenderer.Prompt(Binary));

        Assert.DoesNotContain(
            InitRenderer.Lines(Binary), line => line.Contains('y', StringComparison.Ordinal));
    }

    /// A switch or a VM is picked by number, so the number is what is shown — and never
    /// repeated beside the prompt, where twelve VM names would fill the line.
    [Fact]
    public void A_list_read_off_the_host_is_numbered_above_the_prompt()
    {
        Assert.Equal(
            ["", "REPLICATION", new string('-', 75), "     1  vSwitch-PROD  (external)",
             "     2  vSwitch-ISOLATED  (private)", ""],
            InitRenderer.Lines(Listed));

        Assert.Equal("  Which switch? > ", InitRenderer.Prompt(Listed));
    }

    /// Twelve VMs used to mean twelve copies of the sentence explaining what a priority is.
    [Fact]
    public void A_section_is_headed_once_and_not_again()
    {
        Assert.Contains(
            ConfigurationInterview.Groups.Priorities,
            InitRenderer.Lines(Binary, previousGroup: null));

        Assert.DoesNotContain(
            ConfigurationInterview.Groups.Priorities,
            InitRenderer.Lines(Binary, ConfigurationInterview.Groups.Priorities));
    }

    /// What Enter accepts is the last thing before the cursor, told apart from the options it
    /// is one of.
    [Fact]
    public void A_question_with_no_default_offers_nothing_in_brackets() =>
        Assert.Equal(
            "  Its name > ", InitRenderer.Prompt(InterviewQuestion.Of("peer.hostname", "Its name")));

    /// A first run and a re-run are different situations and the head of the screen says which.
    [Theory]
    [InlineData(true, "Enter keeps it")]
    [InlineData(false, "Nothing here yet")]
    public void The_banner_says_whether_there_was_a_file(bool rewriting, string expected) =>
        Assert.Contains(
            InitRenderer.Banner("D:\\Ripcord\\ripcord.yaml", rewriting),
            line => line.Contains(expected, StringComparison.Ordinal));

    /// The path is on the banner line, so the one thing about to be written is named before
    /// any question is asked.
    [Fact]
    public void The_banner_names_the_file_it_is_about_to_write() =>
        Assert.Equal(
            "RIPCORD INIT" + new string(' ', 75 - 12 - 25) + "D:\\Ripcord\\ripcord.yaml.x",
            InitRenderer.Banner("D:\\Ripcord\\ripcord.yaml.x", rewriting: false)[0]);

    /// With no palette there is no escape and no marker: this screen is also what a redirected
    /// run and the transcript in a ticket look like.
    [Fact]
    public void Nothing_is_left_behind_when_the_colour_is_off()
    {
        IEnumerable<string> everything =
        [
            .. InitRenderer.Banner("D:\\ripcord.yaml", rewriting: true),
            .. InitRenderer.Lines(Listed),
            InitRenderer.Prompt(Binary),
        ];

        Assert.All(
            everything.SelectMany(line => line),
            character => Assert.True(
                character >= ' ',
                $"a control character U+{(int)character:X4} survived into a plain screen"));
    }
}
