using Ripcord.Cli.Rendering;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Tests.Checks;

namespace Ripcord.Tests.Cli;

/// The output is the deliverable: it is what gets read on a 1024×768 KVM during an incident.
/// Fixed width, ASCII only, no colour, and nothing that depends on the terminal.
public class CheckRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_line_is_wider_than_the_fixed_width()
    {
        foreach (string rendered in new[] { Render(Broken()), Render(Pairs.Healthy(Now)) })
        {
            Assert.All(
                rendered.Split(Environment.NewLine),
                line => Assert.True(
                    line.Length <= CheckRenderer.Width,
                    $"{line.Length} characters: {line}"));
        }
    }

    /// A 1024×768 KVM in a server room is not a UTF-8 terminal, and colour is never the sole
    /// carrier of information because it may not be there at all.
    [Fact]
    public void The_output_is_ascii_with_no_escape_sequences()
    {
        string rendered = Render(Broken());

        Assert.All(rendered, character => Assert.True(
            character < 128, $"non-ASCII character U+{(int)character:X4}"));

        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
    }

    /// The first thing to read: every other line means something different depending on
    /// whether the pair is already running on the recovery side.
    [Fact]
    public void The_mode_and_both_host_roles_are_in_the_header()
    {
        string rendered = Render(Pairs.Healthy(Now));

        Assert.Contains("Mode:", rendered);
        Assert.Contains("NORMAL", rendered);
        Assert.Contains("Source:", rendered);
        Assert.Contains($"{Pairs.Source} (holds the primary copies)", rendered);
        Assert.Contains($"{Pairs.Target} (a failover would land here)", rendered);
    }

    [Fact]
    public void Failed_over_mode_is_stated_in_the_header()
    {
        CheckReport report = Report(Pairs.Healthy(Now)) with
        {
            Mode = OperatingMode.FailedOver,
        };

        Assert.Contains("FAILED OVER", CheckRenderer.Render(report));
    }

    /// The headline has to carry the count of rules that could *not* be checked: the exit
    /// code cannot say it, and nothing else would make an operator scroll.
    [Fact]
    public void The_verdict_counts_every_severity_and_the_unchecked_rules()
    {
        string rendered = Render(
            Pairs.Healthy(Now).WithTargetVm("VM-DC-01", vm => vm with { Facts = null }));

        Assert.Contains("not checked", rendered);
    }

    [Fact]
    public void A_critical_violation_says_not_ready()
    {
        Assert.Contains("NOT READY", Render(Broken()));
    }

    [Fact]
    public void A_clean_pair_does_not_say_not_ready()
    {
        Assert.DoesNotContain("NOT READY", Render(Pairs.Healthy(Now)));
    }

    /// Criticals first, then the rules that could not be checked — above the warnings,
    /// because an unverified critical is nearly as serious as a violated one.
    [Fact]
    public void The_sections_are_ordered_criticals_unchecked_warnings_information()
    {
        string rendered = Render(
            Broken()
                .WithVolume(volume => volume with { FreeBytes = 1 })
                .WithTargetVm("VM-LEGACY-01", vm => vm with { Facts = null }));

        int critical = rendered.IndexOf("CRITICAL", StringComparison.Ordinal);
        int unchecked_ = rendered.IndexOf("NOT CHECKED", StringComparison.Ordinal);
        int warning = rendered.IndexOf("WARNING", StringComparison.Ordinal);
        int information = rendered.IndexOf("INFORMATION", StringComparison.Ordinal);

        Assert.True(critical < unchecked_);
        Assert.True(unchecked_ < warning);
        Assert.True(warning < information);
    }

    /// An empty section is absent rather than printed empty: the screen is 24 lines and the
    /// operator is looking for what is wrong.
    [Fact]
    public void An_empty_section_is_not_printed()
    {
        string rendered = Render(Pairs.Healthy(Now));

        Assert.DoesNotContain("CRITICAL", rendered);
        Assert.DoesNotContain("NOT CHECKED", rendered);
        Assert.Contains("INFORMATION", rendered);
    }

    /// The rule id is printed because it is what the operator types into an acknowledgement.
    [Fact]
    public void Every_finding_prints_its_rule_id_the_observation_and_the_implication()
    {
        string rendered = Render(Broken());

        Assert.Contains($"[{CheckRules.ReplicaSwitchMismatch}] VM-DC-01", rendered);
        Assert.Contains("Observed:", rendered);
        Assert.Contains("On the day:", rendered);
        Assert.Contains("Fix:", rendered);
    }

    /// Stated, never executed.
    [Fact]
    public void The_remedy_is_printed_as_a_command_and_nothing_runs_it()
    {
        Assert.Contains("Connect-VMNetworkAdapter", Render(Broken()));
    }

    /// A rule that could not be checked shows the observation, and does not repeat the same
    /// sentence about being unverified under every one of a dozen lines.
    [Fact]
    public void An_unchecked_rule_shows_what_was_missing_without_an_implication()
    {
        string rendered = Render(
            Pairs.Healthy(Now).WithTargetVm("VM-DC-01", vm => vm with { Facts = null }));

        Assert.Contains("was not readable", rendered);
        Assert.Equal(0, Occurrences(rendered, "treat it as unverified"));
    }

    [Fact]
    public void An_active_acknowledgement_is_shown_with_its_expiry_and_reason()
    {
        string rendered = CheckRenderer.Render(Acknowledged(active: true));

        Assert.Contains("Accepted:", rendered);
        Assert.Contains("until 2027-01-01", rendered);
        Assert.Contains("accepted by design", rendered);
        Assert.Contains("1 acknowledged", rendered);
    }

    /// The reason expired: the finding coming back with no explanation would look like a new
    /// fault rather than a decision that lapsed.
    [Fact]
    public void A_lapsed_acknowledgement_says_so()
    {
        string rendered = CheckRenderer.Render(Acknowledged(active: false));

        Assert.Contains("ACKNOWLEDGEMENT LAPSED", rendered);
        Assert.Contains("NOT READY", rendered);
    }

    [Fact]
    public void The_feasibility_table_shows_the_window_and_the_verdict_per_vm()
    {
        string rendered = Render(Pairs.Healthy(Now));

        Assert.Contains("FEASIBILITY", rendered);
        Assert.Contains("Usable memory: 8192 MB", rendered);
        Assert.Contains("STARTUP", rendered);
        Assert.Contains("2048 MB", rendered);
        Assert.Contains("Headroom after the VMs that would boot: 2048 MB", rendered);
    }

    /// "NO" in capitals, because colour is not available and this is the line that decides
    /// whether a service comes back.
    [Fact]
    public void A_vm_that_would_be_refused_is_marked_in_capitals()
    {
        string rendered = Render(
            Pairs.Healthy(Now).WithTarget(
                "VM-DC-01", facts => facts with { StartupRamMb = 9216 }));

        Assert.Matches(@"VM-DC-01\s+P1\s+9216 MB.*\sNO", rendered);
    }

    /// A blank column would read as zero, and zero memory reads as a VM that needs none.
    [Fact]
    public void An_unknown_figure_prints_a_question_mark_rather_than_a_blank()
    {
        string rendered = Render(
            Pairs.Healthy(Now).WithTargetHost(facts => facts with { PhysicalRamMb = null }));

        Assert.Contains("Usable memory: unknown", rendered);
        Assert.Contains("Headroom: unknown", rendered);
    }

    /// A degradation the Application noticed is printed rather than swallowed: the operator
    /// has to know which facts are missing and why.
    [Fact]
    public void A_reading_note_is_printed_above_the_findings()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now), Now, notes: ["the host system could not be read: cimv2 down"]);

        string rendered = CheckRenderer.Render(report);

        Assert.Contains("READING", rendered);
        Assert.Contains("cimv2 down", rendered);
    }

    /// Version skew between the two hosts has to be visible: the rules are encoded in the
    /// binary and they span the pair.
    [Fact]
    public void The_version_is_printed()
    {
        Assert.Contains("ripcord ", Render(Pairs.Healthy(Now)));
    }

    private static CheckReport Acknowledged(bool active) =>
        Pairs.Evaluate(
            Pairs.Healthy(Now).WithSource("VM-BACKUP-01", facts => facts with
            {
                Disks = [.. facts.Disks, new VmDisk(@"\\.\PHYSICALDRIVE2", true)],
            }),
            Now,
            document => document.Checks = new ChecksDocument
            {
                Acknowledgements =
                [
                    new AcknowledgementDocument
                    {
                        Rule = CheckRules.PassthroughDiskOnReplicatedVm,
                        Vm = "VM-BACKUP-01",
                        Reason = "accepted by design",
                        Expires = active ? new DateTime(2027, 1, 1) : new DateTime(2026, 1, 1),
                    },
                ],
            });

    private static PairView Broken() =>
        Pairs.Healthy(Now).WithTargetAdapter(
            "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" });

    private static CheckReport Report(PairView view) =>
        Pairs.Evaluate(view, Now);

    private static string Render(PairView view) =>
        CheckRenderer.Render(Report(view));

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        int index = text.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return count;
    }
}
