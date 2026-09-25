using System.Text.RegularExpressions;
using Ripcord.Adapters.Fake;
using Ripcord.Cli.Rendering;
using Ripcord.Domain;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Replication;
using Ripcord.Tests.Checks;

namespace Ripcord.Tests.Rendering;

/// Colour is an addition to the layout, never a change to it.
///
/// Two things have to stay true, and neither is obvious from reading the renderers: a block
/// with the colour on must be the same text once the escapes are removed — same words, same
/// columns, same widths — and a block with the colour off must contain no escape and no marker
/// at all, because that is what goes into the `logs` folder and into
/// `ripcord status > state.txt`.
///
/// The trap is padding a string that already carries a marker: the marker counts toward the
/// width and the column moves. These tests do **not** catch that on their own — both sides
/// share the defect — so the guard is the golden renderings in `StatusRendererTests` and
/// `CheckRendererTests`, which compare against a block written out in full. This file guards
/// the other half: that colour and no-colour say the same thing.
public class ColourTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private static readonly Regex Escapes = new("\u001b\\[[0-9;]*m", RegexOptions.Compiled);

    public static TheoryData<string> Blocks => new(["healthy", "degraded", "unreachable"]);

    [Theory]
    [MemberData(nameof(Blocks))]
    public void Colour_changes_nothing_but_the_colour(string scenario)
    {
        PairView view = Pair(scenario);

        string coloured = StatusRenderer.Render(
            view, TimeSpan.FromMinutes(2), Now, null, Palette.Ansi);

        Assert.Equal(
            StatusRenderer.Render(view, TimeSpan.FromMinutes(2), Now),
            Escapes.Replace(coloured, ""));
    }

    /// Every line, not just the block as a whole: a column that moved by one would survive the
    /// comparison above only if it moved in both, which it cannot.
    [Theory]
    [MemberData(nameof(Blocks))]
    public void Every_line_keeps_its_width(string scenario)
    {
        PairView view = Pair(scenario);

        string[] plain = StatusRenderer
            .Render(view, TimeSpan.FromMinutes(2), Now)
            .Split('\n');

        string[] coloured = Escapes
            .Replace(StatusRenderer.Render(view, TimeSpan.FromMinutes(2), Now, null, Palette.Ansi), "")
            .Split('\n');

        Assert.Equal(plain.Select(line => line.Length), coloured.Select(line => line.Length));
    }

    /// What the log and a redirected file get. A marker reaching either of them is a control
    /// character in a file somebody opens in Notepad.
    [Theory]
    [MemberData(nameof(Blocks))]
    public void With_the_colour_off_nothing_is_left_behind(string scenario)
    {
        string plain = StatusRenderer.Render(Pair(scenario), TimeSpan.FromMinutes(2), Now);

        Assert.All(plain, character => Assert.True(
            character >= ' ' || character == '\n',
            $"a control character U+{(int)character:X4} survived into a plain rendering"));
    }

    /// The property that makes the whole scheme safe. The markers are C0 controls, and
    /// `Printable.Of` replaces those in anything that came from somewhere else — so a VM name
    /// off the pair channel cannot carry styling into the block it is printed in.
    [Fact]
    public void A_name_cannot_smuggle_styling_into_a_rendered_block()
    {
        const string Hostile = "VM-\u0011EVERYTHING-RED";

        PairView view = new(
            FakeScenarios.Healthy(Now) with
            {
                Vms = [new VmReplicationState(
                    Printable.Of(Hostile),
                    ReplicationRole.Replica,
                    ReplicationState.Replicating,
                    ReplicationHealth.Normal,
                    Now.AddSeconds(-10),
                    0)],
            },
            HostState.Unreachable("HV-PRIMARY-01", HostReachability.NotConfigured()),
            null,
            null);

        string coloured = StatusRenderer.Render(
            view, TimeSpan.FromMinutes(2), Now, null, Palette.Ansi);

        Assert.Contains("VM-?EVERYTHING-RED", coloured, StringComparison.Ordinal);
    }

    /// The same two guarantees for the other block that carries colour. `check` is where a CIM
    /// message and a rule's own text end up, so it is the one most worth asserting rather than
    /// trusting to inspection.
    [Fact]
    public void The_check_block_says_the_same_thing_either_way()
    {
        CheckReport report = Pairs.Evaluate(Pairs.Healthy(Now), Now);

        string coloured = CheckRenderer.Render(report, null, Palette.Ansi);

        Assert.Equal(CheckRenderer.Render(report), Escapes.Replace(coloured, ""));

        Assert.Equal(
            CheckRenderer.Render(report).Split('\n').Select(line => line.Length),
            Escapes.Replace(coloured, "").Split('\n').Select(line => line.Length));
    }

    /// A note comes from an exception message, which is the least trusted text on the page.
    [Fact]
    public void A_note_cannot_smuggle_styling_into_the_check_block()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now),
            Now,
            notes: [Printable.Of("the host system could not be read: \u0011everything red")]);

        Assert.Contains(
            "read: ?everything red",
            CheckRenderer.Render(report, null, Palette.Ansi),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_palette_removes_a_marker_rather_than_printing_it() =>
        Assert.Equal("Critical", Palette.None.Apply("\u0011Critical\u0014"));

    [Fact]
    public void The_colour_palette_turns_a_marker_into_an_escape() =>
        Assert.Equal(
            "\u001b[31mCritical\u001b[0m", Palette.Ansi.Apply("\u0011Critical\u0014"));

    private static PairView Pair(string scenario) =>
        scenario switch
        {
            "degraded" => new PairView(
                FakeScenarios.Degraded(Now),
                FakeScenarios.Healthy(Now) with { HostName = "HV-PRIMARY-01" },
                Now.AddMinutes(-30),
                null),
            "unreachable" => new PairView(
                FakeScenarios.Healthy(Now),
                HostState.Unreachable(
                    "HV-PRIMARY-01", HostReachability.Failed("connection refused", Now)),
                null,
                null),
            _ => new PairView(
                FakeScenarios.Healthy(Now),
                FakeScenarios.Healthy(Now) with { HostName = "HV-PRIMARY-01" },
                Now.AddSeconds(-20),
                null),
        };
}
