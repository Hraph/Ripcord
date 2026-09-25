using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Ports.Configuration;

namespace Ripcord.Tests.Cli;

/// `ripcord init` as somebody actually meets it: a console, a list of typed answers, and a
/// file at the end. The interview's own decisions are covered in
/// `ConfigurationInterviewTests`; what is asserted here is the loop around it — what it prints,
/// what it writes, and the three ways it refuses.
public class InitCliTests
{
    private const string ConfigPath = "/opt/ripcord/ripcord.yaml";

    /// The whole of a first run, in the order the questions come.
    private static readonly string[] FirstRun =
    [
        "dr",
        "HV-PRIMARY-01",
        "192.0.2.10",
        FakeScenarios.ProductionSwitch,
        "all",
        // The fake host reports three VMs, so three priorities and three answers about
        // domain controllers.
        "P1", "y",
        "P2", "n",
        "P2", "n",
        "y",
        "y",
    ];

    [Fact]
    public async Task A_host_with_no_configuration_is_set_up_and_the_file_validates()
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run(FirstRun, store);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.NotNull(store.Written);

        Assert.Empty(Validated(store.Written!).Errors);
        Assert.Contains($"wrote {ConfigPath}", run.Output, StringComparison.Ordinal);
    }

    /// The point of the closing lines: "installed" and "working" are different states, and the
    /// pair channel is still off after this.
    [Fact]
    public async Task It_ends_on_the_next_command_to_type()
    {
        CliRun run = await Run(FirstRun, new MemoryConfigStore(
            ConfigurationRead.Absent(ConfigPath, "there is no file")));

        Assert.Contains("Next:  ripcord status", run.Output, StringComparison.Ordinal);
        Assert.Contains("pair channel is separate", run.Output, StringComparison.Ordinal);
    }

    /// Nothing is written, and the file is on the screen instead — the output somebody reads
    /// before deciding, which is what rule 4 asks of every mutating command.
    [Fact]
    public async Task Dry_run_shows_the_file_and_writes_nothing()
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run(FirstRun, store, "--dry-run");

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Null(store.Written);
        Assert.Contains("nothing was written", run.Output, StringComparison.Ordinal);
        Assert.Contains("expected_role: replica", run.Output, StringComparison.Ordinal);
    }

    /// The refusal that matters on a host nobody is sitting at. A scheduled task or a service
    /// reaching this must stop, not wait for an answer that is never coming.
    [Fact]
    public async Task With_nothing_answering_it_refuses_rather_than_waits()
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run([], store);

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Null(store.Written);
        Assert.Contains("nothing is answering", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_at_the_end_writes_nothing()
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run([.. FirstRun[..^1], "n"], store);

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Null(store.Written);
        Assert.Contains("nothing was changed", run.Error, StringComparison.Ordinal);
    }

    /// An answer the file would not take is refused on the console too, not only in the
    /// interview's own tests — and the run still completes once a good one is given.
    [Fact]
    public async Task A_refused_answer_is_explained_and_asked_again()
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run(
            ["dr", "HV-PRIMARY-01", "the-dr-box", .. FirstRun[2..]], store);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains(
            "'the-dr-box' is not an IP address written in full.", run.Error, StringComparison.Ordinal);
    }

    /// Re-running it is an edit: the previous file is kept, and the sections the interview
    /// never asks about come back out untouched.
    [Fact]
    public async Task A_re_run_keeps_the_previous_file_and_what_it_never_asked_about()
    {
        const string Previous = """
            schema_version: 1

            node:
              hostname: HV-REPLICA-01
              host_memory_reserve_gb: 4

            # Set up by hand months ago.
            alerting:
              enabled: false
            """;

        MemoryConfigStore store = new(
            Yaml.Read(Previous), Previous);

        CliRun run = await Run(FirstRun, store);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("# Set up by hand months ago.", store.Written!, StringComparison.Ordinal);
        Assert.Contains("alerting:", store.Written!, StringComparison.Ordinal);

        Assert.Equal($"{ConfigPath}.1", store.Kept);
        Assert.Contains("kept as it was: alerting", run.Output, StringComparison.Ordinal);
        Assert.Contains($"previous one is at {ConfigPath}.1", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--role", "sideways")]
    [InlineData("--nonsense", "")]
    public async Task An_option_it_does_not_understand_is_refused_before_anything_is_asked(
        string option, string value)
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run(FirstRun, store, option, value);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Null(store.Written);
    }

    /// The one answer a script can supply, so an installer does not have to ask a question it
    /// already knows the answer to.
    [Fact]
    public async Task A_role_on_the_command_line_is_not_asked_for()
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run(FirstRun[1..], store, "--role", "dr");

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("expected_role: replica", store.Written!, StringComparison.Ordinal);
    }

    /// The other half of the ask: a command run without a configuration says what to do about
    /// it, rather than repeating the path three times inside a .NET sentence.
    [Fact]
    public async Task A_command_with_no_configuration_names_the_command_that_makes_one()
    {
        CliRun run = await RunVerb(
            [],
            new MemoryConfigStore(ConfigurationRead.Absent(ConfigPath, "there is no file")),
            "status");

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Equal(
            [
                "ripcord: no configuration on this host.",
                $"  expected at {ConfigPath}",
                "  create one with:  ripcord init",
            ],
            run.Error.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));
    }

    /// Decision 3: a host with no VM is set up all the same, with nothing asked per VM.
    [Fact]
    public async Task A_host_with_no_vm_is_set_up_and_the_file_validates()
    {
        MemoryConfigStore store = new(ConfigurationRead.Absent(ConfigPath, "there is no file"));

        CliRun run = await Run(
            ["dr", "HV-PRIMARY-01", "192.0.2.10", FakeScenarios.ProductionSwitch, "", "y", "y"],
            store,
            NoVm());

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("This host has no virtual machine", run.Output, StringComparison.Ordinal);
        Assert.Contains("vms: []", store.Written!, StringComparison.Ordinal);
        Assert.Empty(Validated(store.Written!).Errors);
    }

    /// The field report: the sample `install.ps1` leaves behind named VMs the host never had.
    [Fact]
    public async Task An_unreadable_host_offers_no_name_from_the_previous_file()
    {
        string sample = File.ReadAllText(
            Path.Combine(Architecture.RepositoryLayout.Root, "config", "ripcord.dr.yaml"));
        MemoryConfigStore store = new(Yaml.Read(sample), sample);

        CliRun run = await Run(
            ["", "", "", "", "VM-SR-01", "P1", "n", "y", "n"],
            store,
            FakeHypervProvider.FailingLocally("WMI is down"));

        string beforeTyping = run.Output[..run.Output.IndexOf("THE FILE", StringComparison.Ordinal)];

        foreach (string placeholder in new[] { "VM-DC-01", "VM-LEGACY-01", "VM-BACKUP-01" })
        {
            Assert.DoesNotContain(placeholder, beforeTyping, StringComparison.Ordinal);
        }
    }

    /// `checks` is carried as the original lines, so an acknowledgement of a dropped VM stays
    /// and the file would be refused. Said before the yes, never discovered after.
    [Fact]
    public async Task A_dropped_vm_named_by_an_acknowledgement_is_warned_about_before_writing()
    {
        string sample = File.ReadAllText(
            Path.Combine(Architecture.RepositoryLayout.Root, "config", "ripcord.dr.yaml"));
        MemoryConfigStore store = new(Yaml.Read(sample), sample);
        FakeHypervProvider provider = new(FakeScenarios.Healthy(TestPorts.Now) with
        {
            Vms = [.. FakeScenarios.Healthy(TestPorts.Now).Vms.Where(vm => vm.Name != "VM-BACKUP-01")],
        });

        CliRun run = await Run(["", "", "", "", "", "", "", "", "", "", "n"], store, provider);

        Assert.Contains("  - VM-BACKUP-01", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "checks.acknowledgements[0]: VM-BACKUP-01 is no longer declared",
            run.Output,
            StringComparison.Ordinal);
        Assert.True(
            run.Output.IndexOf("no longer declared", StringComparison.Ordinal)
                < run.Output.IndexOf("Write this to", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("check")]
    public async Task A_configuration_with_no_vm_says_so(string verb)
    {
        ConfigurationDocument document = Tests.Configuration.ValidDocument.Create();
        document.Vms = [];
        document.Checks = null;
        document.Replication!.UnattendedTestFailoverVms = null;

        CliRun run = await RunVerb(
            [], new MemoryConfigStore(ConfigurationRead.Succeeded(document)), verb, provider: NoVm());

        Assert.Contains(
            ConfigurationNotes.NoVmDeclared, run.Output + run.Error, StringComparison.Ordinal);
    }

    private static FakeHypervProvider NoVm() =>
        new(FakeScenarios.Healthy(TestPorts.Now) with { Vms = [] });

    private static ConfigurationValidation Validated(string yaml) =>
        ConfigurationValidator.Validate(
            Yaml.Read(yaml).Document, FakeScenarios.LocalHostName);

    private static async Task<CliRun> Run(
        IReadOnlyList<string> typed,
        MemoryConfigStore store,
        params string[] options)
    {
        string[] arguments = [.. options.Where(option => option.Length > 0)];

        return await RunVerb(typed, store, "init", arguments);
    }

    private static Task<CliRun> Run(
        IReadOnlyList<string> typed, MemoryConfigStore store, FakeHypervProvider provider) =>
        RunVerb(typed, store, "init", provider: provider);

    private static async Task<CliRun> RunVerb(
        IReadOnlyList<string> typed,
        MemoryConfigStore store,
        string verb,
        string[]? options = null,
        FakeHypervProvider? provider = null)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            TestPorts.With(store, provider),
            new CliEnvironment(
                FakeScenarios.LocalHostName,
                ConfigPath,
                "/opt/ripcord/ripcord",
                new StringReader(string.Join("\n", typed))));

        ExitCode code = await cli.RunAsync(
            [verb, .. options ?? []], output, error, CancellationToken.None);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);
}
