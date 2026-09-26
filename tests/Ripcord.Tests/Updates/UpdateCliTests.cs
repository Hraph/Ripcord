using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Cli.Rendering;
using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Alerting;
using Ripcord.Ports.Audit;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Dashboard;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Pairing;
using Ripcord.Ports.Replication;
using Ripcord.Ports.Updates;
using Ripcord.Ports;
using Ripcord.Tests.Alerting;
using Ripcord.Tests.Updates;

namespace Ripcord.Tests.Cli;

/// The command that replaces the binary. Every test here is about what it refuses, because
/// the refusals are the feature: the one thing worse than a host that will not update is a
/// host that installs whatever it was handed.
public sealed class UpdateCliTests
{
    private const string Machine = FakeScenarios.LocalHostName;

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_dry_run_shows_the_plan_and_is_never_asked_to_confirm()
    {
        Swap swap = new();

        CliRun run = await Run(["update", "--dry-run"], swap: swap);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Type the node name", run.Output, StringComparison.Ordinal);
        Assert.Empty(swap.Moves);
    }

    [Fact]
    public async Task A_real_run_does_nothing_until_yes_is_typed()
    {
        Swap swap = new();

        CliRun run = await Run(["update"], swap: swap, typed: "no");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("nothing was changed", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_confirmed_update_installs_the_release()
    {
        Swap swap = new();

        CliRun run = await Run(["update"], swap: swap, typed: "y");

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal(
            [UpdateAction.DiscardPrevious, UpdateAction.SetAside, UpdateAction.Install],
            swap.Moves);
        Assert.DoesNotContain("\r", run.Output, StringComparison.Ordinal);
    }

    /// On a console one line shows the step running, the download's percentage with it, and
    /// is erased before the result.
    [Fact]
    public async Task A_live_console_shows_each_step_on_one_line()
    {
        CliRun run = await Run(["update"], typed: "y", live: true);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("\r  download the release and its signature... 100%", run.Output, StringComparison.Ordinal);
        Assert.Contains("\r  put the new binary where the running one was...", run.Output, StringComparison.Ordinal);
        Assert.Matches(@"\r +\r\r?\n  0\.2\.0 is installed", run.Output);
    }

    /// A line wider than the console wraps, and `\r` then redraws only its last row.
    [Fact]
    public void Every_progress_line_fits_the_console()
    {
        Assert.All(UpdatePlan.For(Subjects.Available()).Steps, step =>
        {
            Assert.InRange(UpdateRenderer.RenderRunning(step).Length, 1, 75);
            Assert.InRange(UpdateRenderer.RenderRunning(step, "100%").Length, 1, 75);
            Assert.InRange(UpdateRenderer.RenderRunning(step, "160 MB").Length, 1, 75);
        });
    }

    [Fact]
    public async Task A_download_cut_off_part_way_is_erased_before_the_failure()
    {
        CliRun run = await Run(["update"], source: Source.CutOff(), typed: "y", live: true);

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Matches(@"\r +\r\r?\n  FAILED: ", run.Output);
    }

    /// The listener still runs the binary the last update set aside, which Windows will not
    /// delete. Refused before anything is fetched or moved, naming the restart.
    [Fact]
    public async Task An_update_while_the_set_aside_binary_runs_is_refused_before_anything_moves()
    {
        Swap swap = new() { PreviousInUse = true };

        CliRun run = await Run(["update"], swap: swap, typed: "y");

        Assert.NotEqual(ExitCode.Success, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("ripcord service restart", run.Output, StringComparison.Ordinal);
    }

    /// The binary that is running is still the old one. Saying so is the difference between a
    /// confusing `ripcord version` and a support call.
    [Fact]
    public async Task A_finished_update_says_the_new_version_starts_on_the_next_run()
    {
        CliRun run = await Run(["update"], typed: "y");

        Assert.Contains("restart", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// The whole point of the feature. A release signed by anybody else is not installed, and
    /// the host is not touched on the way to finding that out.
    [Fact]
    public async Task A_release_signed_by_a_stranger_is_refused_after_yes_was_typed()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: "y", source: Source.SignedByAStranger());

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("signature", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// A host that carries no signing key cannot tell a genuine release from any other, so it
    /// installs nothing. Refusing is the only safe reading of "I cannot check".
    [Fact]
    public async Task A_host_with_no_signing_key_installs_nothing()
    {
        Swap swap = new();

        CliRun run = await Run(["update"], swap: swap, typed: "y", signingKey: null);

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(swap.Moves);
    }

    [Fact]
    public async Task A_host_not_allowed_to_install_is_refused_by_name()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: "y", store: new Store(check: true, install: false));

        Assert.NotEqual(ExitCode.Success, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("updates.install", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_not_allowed_to_look_is_refused_before_anything_is_fetched()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: "y", store: new Store(check: false, install: false));

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("updates.check", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_already_on_the_latest_release_changes_nothing()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: "y", feed: StubReleaseFeed.Publishing("0.1.0"));

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Empty(swap.Moves);
    }

    /// Decision D54, said before the prompt rather than discovered during the next failover.
    [Fact]
    public async Task The_consequence_for_the_pair_is_printed_above_the_confirmation()
    {
        CliRun run = await Run(["update", "--dry-run"]);

        Assert.Contains("WARNING", run.Output, StringComparison.Ordinal);
        Assert.Contains("failover", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_option_is_refused_rather_than_ignored()
    {
        CliRun run = await Run(["update", "--yes"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--yes", run.Error, StringComparison.Ordinal);
    }

    /// `update` asks the feed, so it knows what is published — and it used to throw that
    /// answer away. `status` and `check` never look for themselves, by design, so the file
    /// this writes is the only way either of them can mention a release. A run that paid for
    /// the network call and left it unwritten meant somebody had to run `check-update` as
    /// well, for a fact the tool already had.
    [Fact]
    public async Task A_dry_run_writes_down_the_release_it_just_looked_up()
    {
        MemoryUpdateNoticeStore notices = new();

        await Run(["update", "--dry-run"], notices: notices);

        Assert.Equal("0.2.0", notices.Read()?.Version);
    }

    /// Including when the plan refuses. It still looked, and the answer is still worth having
    /// on a host that may check and may not install — which is the default posture.
    [Fact]
    public async Task A_refused_update_still_writes_down_what_is_published()
    {
        MemoryUpdateNoticeStore notices = new();

        CliRun run = await Run(
            ["update"], store: new Store(check: true, install: false), notices: notices);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Equal("0.2.0", notices.Read()?.Version);
    }

    private static async Task<CliRun> Run(
        string[] args,
        Swap? swap = null,
        Source? source = null,
        Store? store = null,
        IReleaseFeed? feed = null,
        IUpdateNoticeStore? notices = null,
        string? typed = null,
        string? signingKey = "carried",
        bool live = false,
        CancellationToken cancellationToken = default)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            new RipcordPorts(
                store ?? new Store(check: true, install: true),
                new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                FakeHostSystemProvider.Target(),
                FakeCertificateProvider.Valid(
                    Tests.Configuration.ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
                FakePeerChannel.Absent(),
                new InMemorySnapshotStore(),
                new NoOpDeploymentExecutor(),
                new NoOpPeerListener(),
                new NoOpDashboardServer(),
                new InMemoryAuditLog(),
                new StubNotifier(),
                new MemoryAlertStateStore(),
                feed ?? StubReleaseFeed.Publishing("0.2.0"),
                source ?? Source.Genuine(),
                notices ?? new MemoryUpdateNoticeStore(),
                swap ?? new Swap(),
                new FixedClock(Now),
                new SilentDiagnosticLog(),
                new NoLogs()),
            new CliEnvironment(
                Machine,
                "ripcord.yaml",
                @"D:\Ripcord\ripcord.exe",
                new StringReader(typed ?? ""),
                "tester",
                new BuildIdentity("0.1.0", "abc123def456"),
                signingKey is null ? null : Keys.Pinned,
                LiveConsole: live));

        ExitCode code = await cli.RunAsync(args, output, error, cancellationToken);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    /// Records the moves it was asked to make, and makes none of them.
    private sealed class Swap : IBinarySwap
    {
        public List<UpdateAction> Moves { get; } = [];

        public bool PreviousInUse { get; init; }

        public UpdateAction? FailOn { get; init; }

        public StagedBinaries Observe(string binaryPath) =>
            new(false, this.PreviousInUse, PreviousInUse: this.PreviousInUse);

        public void Apply(UpdateStep move, StagedRelease release, CancellationToken cancellationToken)
        {
            if (move.Action == this.FailOn)
            {
                throw new IOException($"the host refused to {move.Action}");
            }

            this.Moves.Add(move.Action);
        }

        public void Restore(string binaryPath)
        {
        }

        public string? Swapped { get; private set; }

        

        public SwapOutcome SwapWithPrevious(string binaryPath)
        {
            this.Swapped = binaryPath;
            return SwapOutcome.Exchanged;
        }
    }

    private sealed class Store(bool check, bool install) : ReadOnlyConfigStore
    {
        public override ConfigurationRead Read(string path)
        {
            ConfigurationDocument document = Tests.Configuration.ValidDocument.Create();
            document.Node!.Hostname = Machine;
            document.Listener!.Enabled = false;
            document.Updates = new UpdatesDocument { Check = check, Install = install };

            return ConfigurationRead.Succeeded(document);
        }
    }
}
