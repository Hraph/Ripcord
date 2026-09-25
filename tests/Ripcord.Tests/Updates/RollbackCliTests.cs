using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;
using Ripcord.Ports.Configuration;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Updates;

/// `ripcord rollback` as the operator meets it. The decision is `RollbackPlanTests`; what is
/// asserted here is the loop around it — that nothing moves without a yes,
/// that `--dry-run` moves nothing at all, and that a host with nothing set aside is
/// refused rather than told it succeeded.
public class RollbackCliTests
{
    private const string BinaryPath = "/opt/ripcord/ripcord";

    [Fact]
    public async Task With_nothing_set_aside_it_refuses_and_moves_nothing()
    {
        KeptBinary swap = new(hasPrevious: false);

        CliRun run = await Run(swap, typed: "y");

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Null(swap.Swapped);
        // Asserted on a phrase the 75-column wrap keeps on one line: "nothing to go back to"
        // is split across two in the rendered block.
        Assert.Contains("REFUSED", run.Output, StringComparison.Ordinal);
        Assert.Contains("there is no binary set aside", run.Output, StringComparison.Ordinal);
        Assert.Contains("Set aside  nothing", run.Output, StringComparison.Ordinal);
    }

    /// Rule 4. The output somebody reads before deciding is the one that changes nothing.
    [Fact]
    public async Task Dry_run_prints_the_sequence_and_moves_nothing()
    {
        KeptBinary swap = new();

        CliRun run = await Run(swap, dryRun: true);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Null(swap.Swapped);
        Assert.Contains("Set aside  0.2.1", run.Output, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
    }

    /// Rule 3. The binary set aside is kept, so this is undone by running it again, and it
    /// asks y/n with Enter declining.
    [Fact]
    public async Task Without_a_yes_nothing_is_exchanged()
    {
        KeptBinary swap = new();

        CliRun run = await Run(swap, typed: "");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Null(swap.Swapped);
        Assert.Contains("not confirmed", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirmed_it_exchanges_the_binary_and_says_what_now_runs()
    {
        KeptBinary swap = new();

        CliRun run = await Run(swap, typed: "y");

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal(BinaryPath, swap.Swapped);
        Assert.Contains("this host now runs 0.2.1", run.Output, StringComparison.Ordinal);
    }

    /// Failed, and the running binary went back. The host is where it was, so it is worth
    /// re-running: exit 3.
    [Fact]
    public async Task An_exchange_that_was_undone_says_the_host_is_where_it_was()
    {
        CliRun run = await Run(
            new KeptBinary(outcome: SwapOutcome.Recovered), typed: "y");

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Contains("runs what it was running", run.Error, StringComparison.Ordinal);
    }

    /// Failed, and it could **not** go back. This host may have no binary where the service
    /// starts from, so it is exit 5 and it names the file the old one is under.
    ///
    /// This is the case an earlier version of this command claimed could not happen. It can:
    /// there is a window, two renames wide, in which the path the service starts from is
    /// empty, and a claim that a window does not exist is not made true by asserting it with a
    /// double that never opens one.
    [Fact]
    public async Task An_exchange_that_could_not_be_undone_asks_for_a_human()
    {
        CliRun run = await Run(
            new KeptBinary(outcome: SwapOutcome.LeftIncomplete),
            typed: "y");

        Assert.Equal(ExitCode.IntermediateState, run.Code);
        Assert.Contains("may have no binary", run.Error, StringComparison.Ordinal);
        Assert.Contains("'.swap'", run.Error, StringComparison.Ordinal);
    }

    /// A file left by an exchange that did not finish holds the only copy of a binary this
    /// host was running. Refused before the plan is even weighed.
    [Fact]
    public async Task An_earlier_exchange_left_behind_stops_this_one()
    {
        KeptBinary swap = new(interrupted: true);

        CliRun run = await Run(swap, typed: "y");

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Null(swap.Swapped);
        Assert.Contains("did not finish", run.Output, StringComparison.Ordinal);
    }

    private static async Task<CliRun> Run(
        KeptBinary swap,
        string? typed = null,
        bool dryRun = false)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            // The configuration here declares no `updates` block at all, so both switches are
            // off — and going back still works. They gate reaching off the host; this reaches
            // for a file already on it, and a host forbidden to fetch anything is the one most
            // likely to need to go back.
            TestPorts.With(new MemoryConfigStore(new ConfigurationRead(ValidDocument.Create(), [])))
                with { BinarySwap = swap },
            new CliEnvironment(
                FakeScenarios.LocalHostName,
                "/opt/ripcord/ripcord.yaml",
                BinaryPath,
                new StringReader(typed ?? ""),
                Build: new BuildIdentity("0.3.0", "abc123")));

        ExitCode code = await cli.RunAsync(
            dryRun ? ["rollback", "--dry-run"] : ["rollback"],
            output,
            error,
            CancellationToken.None);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    /// A host with — or without — a binary kept aside by an earlier update, and an exchange
    /// that ends however the test needs it to.
    private sealed class KeptBinary(
        bool hasPrevious = true,
        SwapOutcome outcome = SwapOutcome.Exchanged,
        bool interrupted = false) : IBinarySwap
    {
        public string? Swapped { get; private set; }

        public StagedBinaries Observe(string binaryPath) =>
            new(
                false,
                hasPrevious,
                hasPrevious ? "0.2.1" : null,
                hasPrevious ? new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero) : null,
                interrupted);

        public void Apply(UpdateStep move, StagedRelease release, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a rollback installs nothing");

        public void Restore(string binaryPath) =>
            throw new InvalidOperationException("a rollback is not a failed swap");

        public SwapOutcome SwapWithPrevious(string binaryPath)
        {
            this.Swapped = binaryPath;
            return outcome;
        }
    }
}
