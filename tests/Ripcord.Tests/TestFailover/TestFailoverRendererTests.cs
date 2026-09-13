using Ripcord.Application.TestFailover;
using Ripcord.Cli.Rendering;
using Ripcord.Domain.TestFailover;
using Ripcord.Tests.Checks;

namespace Ripcord.Tests.TestFailover;

/// The monthly operations note. Read on a 1024×768 KVM, pasted into a document afterwards,
/// and skimmed — which is why "started, UNCONFIRMED" must not be able to pass for "booted".
public class TestFailoverRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_line_is_wider_than_the_screen()
    {
        string rendered = TestFailoverRenderer.Render(Report(
            Result("VM-DC-01", TestFailoverStatus.Booted, TimeSpan.FromSeconds(42)),
            Result("VM-LEGACY-01", TestFailoverStatus.NoHeartbeat, null),
            NotIsolated("VM-BACKUP-01")),
            "vSwitch-ISOLATED");

        Assert.All(
            rendered.Split(Environment.NewLine),
            line => Assert.True(line.Length <= 75, $"too wide ({line.Length}): {line}"));
    }

    /// The distinction the whole heartbeat treatment exists for. A guest with no integration
    /// services was powered on and nothing more, and the word must not read as a pass.
    [Fact]
    public void An_unconfirmed_boot_does_not_read_as_a_boot()
    {
        string rendered = TestFailoverRenderer.Render(
            Report(Result("VM-LEGACY-01", TestFailoverStatus.BootedWithoutHeartbeat, null)),
            null);

        Assert.Contains("UNCONFIRMED", rendered);
        Assert.Contains("0 of 1 booted", rendered);
    }

    /// A test VM that survived its own cleanup is the one line nobody may miss.
    [Fact]
    public void A_failed_cleanup_says_what_it_costs()
    {
        string rendered = TestFailoverRenderer.Render(
            Report(Result("VM-DC-01", TestFailoverStatus.Booted, TimeSpan.FromSeconds(9))
                with { Cleanup = Compensation.Failed("the test VM is locked") }),
            "vSwitch-ISOLATED");

        Assert.Contains("NOT destroyed", rendered);
        Assert.Contains("break the next test", rendered);
    }

    /// With no switch configured the isolation is "everything unplugged", and the header has
    /// to say so — otherwise the reader assumes a switch they never set.
    [Fact]
    public void The_header_states_how_isolation_is_obtained()
    {
        Assert.Contains(
            "every adapter disconnected",
            TestFailoverRenderer.Render(Report(), null));
    }

    [Fact]
    public void Orphans_say_that_hyper_v_never_mentions_them()
    {
        string rendered = TestFailoverRenderer.Render(
            new TestFailoverReport(
                Now,
                null,
                [new Orphan("VM-DC-01 (test copy)", TimeSpan.FromHours(30), OrphanVerdict.Lingering)],
                [],
                false,
                false),
            null);

        Assert.Contains("LEFT BEHIND", rendered);
        Assert.Contains("30h old", rendered);
    }

    /// Refused means nothing happened, and the report says so in those words rather than
    /// leaving the reader to infer it from an empty table.
    [Fact]
    public void A_refusal_says_that_nothing_changed()
    {
        string rendered = TestFailoverRenderer.Render(
            new TestFailoverReport(
                Now,
                TestFailoverPrecondition.Evaluate(Pairs.Evaluate(
                    Pairs.Healthy(Now).WithTargetAdapter(
                        "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" }),
                    Now)),
                [],
                [],
                false,
                false),
            "vSwitch-ISOLATED");

        Assert.Contains("REFUSED - NOTHING WAS CHANGED", rendered);
        Assert.Contains("Nothing was changed.", rendered);
    }

    private static TestFailoverReport Report(params VmTestFailoverResult[] results) =>
        new(Now, null, [], results, false, false);

    private static VmTestFailoverResult Result(
        string name, TestFailoverStatus status, TimeSpan? boot) =>
        new(name, status, boot, [], null, Compensation.Done);

    private static VmTestFailoverResult NotIsolated(string name) =>
        new(
            name,
            TestFailoverStatus.RefusedNotIsolated,
            null,
            [new IsolationBreach(
                "Network Adapter", "connected to 'vSwitch-PROD'", IsolationDoubt.Connected)],
            "the test VM is not isolated from the production network",
            Compensation.Done);
}
