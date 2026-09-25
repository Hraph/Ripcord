using Ripcord.Application.Alerting;
using Ripcord.Application.Checks;
using Ripcord.Application.Updates;
using Ripcord.Application.Deployment;
using Ripcord.Application.Failover;
using Ripcord.Application.Status;
using Ripcord.Application.TestFailover;
using Ripcord.Application;
using Ripcord.Cli.Rendering;
using Ripcord.Domain.Dashboard;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Dashboard;
using Ripcord.Ports.Deployment;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Updates;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Diagnostics;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Hosts;
using Ripcord.Ports.Pairing;
using Ripcord.Ports.Alerting;
using Ripcord.Ports.Audit;
using Ripcord.Ports.Diagnostics;
using Ripcord.Ports.Replication;
using Ripcord.Ports.Updates;
using Ripcord.Ports;

namespace Ripcord.Cli;

/// What the composition root knows about the machine. Passed in rather than read here so the
/// whole command surface is exercisable from the Linux test container.
public sealed record CliEnvironment(
    string MachineName,
    string DefaultConfigurationPath,
    string BinaryPath,
    TextReader? ConfirmationReader = null,
    /// Who is running the command. It goes in the audit trail (D8), which is read afterwards by
    /// somebody working out who moved production and what they knew at the time.
    string UserName = "unknown",
    /// Which binary this is. Normally the one that is running, and injectable so a test can
    /// put two known builds on the two sides of the pair rather than assert against whatever
    /// the test host happened to stamp.
    BuildIdentity? Build = null,
    /// The public half of the key every release is signed with, compiled into the host rather
    /// than read from the configuration: an attacker who can edit `ripcord.yaml` must not be
    /// able to change what this host accepts as a genuine binary. Null on a host that carries
    /// no key, which refuses every install rather than trusting one.
    string? ReleaseSigningKey = null,
    /// Whether this console understands colour. Decided by the composition root, which is the
    /// only place that can ask the operating system — and null everywhere else, which means
    /// plain text. A test, a redirected file and the listener service all get plain text
    /// without having to say so.
    Palette? Palette = null,
    /// The same answer for the error stream, which is redirected separately: `2> errors.log`
    /// on a live console leaves one a console and the other a file.
    Palette? ErrorPalette = null);

/// Every port the command surface reaches the machine through. Grouped rather than listed one
/// by one, because each milestone adds another and a composition root nobody can read is a
/// composition root nobody checks.
public sealed record RipcordPorts(
    IConfigStore ConfigStore,
    IHypervProvider Provider,
    IHostSystemProvider HostSystem,
    ICertificateProvider Certificates,
    IPeerChannel PeerChannel,
    ISnapshotStore SnapshotStore,
    IDeploymentExecutor DeploymentExecutor,
    IPeerListener PeerListener,
    IDashboardServer DashboardServer,
    IAuditLog Audit,
    INotifier Notifier,
    IAlertStateStore AlertState,
    IReleaseFeed ReleaseFeed,
    IReleaseSource ReleaseSource,
    IUpdateNoticeStore UpdateNotices,
    IBinarySwap BinarySwap,
    IClock Clock,
    IDiagnosticLog Diagnostics,
    IDiagnosticLogReader LogReader);

/// Argument parsing and console rendering. No decision lives here: the exit code comes from
/// the use case, the layout from StatusRenderer.
public sealed class RipcordCli(RipcordPorts ports, CliEnvironment environment)
{
    /// Plain text unless the composition root said otherwise.
    private Palette Ink => environment.Palette ?? Palette.None;

    /// The error stream's own answer. Never assumed to be the output stream's: everything
    /// written here is what ends up in a file when somebody redirects `2>`.
    private Palette ErrorInk => environment.ErrorPalette ?? Palette.None;

    /// A refusal, in red when the console can take it.
    ///
    /// Applied at the few lines that say the command did not do what was asked, rather than to
    /// everything written to the error stream: notes and degradations go there too, and a
    /// screen where everything is red is a screen where nothing is.
    private void Refuse(TextWriter error, string line) =>
        error.WriteLine(this.ErrorInk.Apply(Rendering.Ink.Red(line)));

    /// Typed in full, not "y": this creates a Windows service and opens an inbound port on a
    /// host that may run a domain controller. A keystroke is not a decision.
    private const string ConfirmationPrompt = "Type the node name to confirm";

    public async Task<ExitCode> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            WriteUsage(output);
            return ExitCode.Success;
        }

        this.PointTheLogAtTheConfiguration(args);

        string operation = args[0];

        // Written before the command runs, not after. A verb that takes the host down with it
        // — or that is still running when somebody gives up and closes the window — leaves no
        // second line, and the first one is then the whole of what the log can say.
        ports.Diagnostics.Write(DiagnosticEntry.Of(
            operation, $"running: {string.Join(' ', args)}"));

        try
        {
            ExitCode code = await this.DispatchAsync(args, output, error, cancellationToken)
                .ConfigureAwait(false);

            ports.Diagnostics.Write(CommandEntries.Exited(operation, code));

            return code;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C. A stack trace is not an answer, and the read had not changed anything.
            ports.Diagnostics.Write(CommandEntries.Cancelled(operation));
            error.WriteLine("ripcord: cancelled.");
            return ExitCode.LocalAccessFailure;
        }
        catch (Exception exception)
        {
            // Recorded and rethrown: the line is what tells whoever looks why, and the caller
            // decides what the failure is — a crash by hand, an exit code set for Windows by
            // the listener service.
            ports.Diagnostics.Write(CommandEntries.Crashed(operation, exception.ToString()));

            throw;
        }
    }

    /// Moves the log to where `ripcord.yaml` asks for it, before the command reads anything.
    ///
    /// The file is read here for that one section and validated for none of it: the first
    /// thing worth logging is often the reason this file cannot be used, and a log that waited
    /// for a valid configuration would be silent exactly then.
    private void PointTheLogAtTheConfiguration(string[] args)
    {
        string path = environment.DefaultConfigurationPath;

        // Deliberately not `TryConsumeConfig`: that one refuses a malformed or repeated
        // `--config` and is the parser whose refusal the operator reads. This is a guess at
        // which file to look in, made before any of that, and the worst it can do is point the
        // log at the wrong path for the one line that says the option was wrong.
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--config")
            {
                path = args[index + 1];
            }
        }

        try
        {
            ports.Diagnostics.SendTo(DiagnosticDestination.From(
                ports.ConfigStore.Read(path).Document?.Diagnostics, this.DefaultLogFolder));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Unreadable, unparseable, not there at all. The log stays where it started,
            // the logs folder beside the binary, and the command carries on to report it properly.
        }
    }

    /// The logs folder beside the binary, with the audit trail and the alert state next to
    /// it: one directory holds everything this host writes about itself.
    private string DefaultLogFolder =>
        Path.Combine(Path.GetDirectoryName(environment.BinaryPath) ?? "", LogFolder.Name);

    private async Task<ExitCode> DispatchAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        switch (args[0])
        {
            case "status":
                return await this.StatusAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "check":
                return await this.CheckAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "test-failover":
                return await this.TestFailoverAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "failover":
                return await this.FailoverAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "failback":
                return await this.FailoverAsync(
                        args[1..], output, error, cancellationToken,
                        FailoverOperation.Failback)
                    .ConfigureAwait(false);

            case "fence":
                return await this.FenceAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "serve":
                return await this.ServeAsync(args[1..], error, cancellationToken)
                    .ConfigureAwait(false);

            case "dashboard":
                return await this.DashboardAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "service":
                return this.Service(args[1..], output, error);

            case "update":
                return await this.UpdateAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "rollback":
                return await this.RollbackAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "check-update":
                return await this.CheckUpdateAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "init":
                return await this.InitAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "version":
                output.WriteLine($"ripcord {BuildInfo.VersionWithCommit}");
                return ExitCode.Success;

            default:
                error.WriteLine($"ripcord: unknown command '{args[0]}'.");
                WriteUsage(error);
                return ExitCode.InvalidConfiguration;
        }
    }

    /// Writes the first `ripcord.yaml`, or re-writes one without losing it.
    ///
    /// The whole of the decision-making is `ConfigurationInterview`; what is here is a loop
    /// that prints a question and reads a line. That split is what lets the interview be
    /// driven from a list of strings in a test, including the assertion that what it produces
    /// passes the validator.
    private async Task<ExitCode> InitAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadInitOptions(args, out InitOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        // Read before anything is asked. A file that fails validation is the commonest reason
        // to be running this, so it is read as a document and never validated: what it says is
        // the answer to every question, and what it says about sections nobody asks about is
        // carried across untouched.
        ConfigurationDocument? seed = ports.ConfigStore.Read(options.Path).Document;
        string? previous = ports.ConfigStore.ReadText(options.Path);

        foreach (string line in InitRenderer.Banner(options.Path, previous is not null, this.Ink))
        {
            output.WriteLine(line);
        }

        ConfigurationInterview interview = ConfigurationInterview.Start(
            await this.ReadFactsAsync(cancellationToken).ConfigureAwait(false),
            seed,
            options.Role);

        // The section heading is written when the section changes, so a run of twelve VMs is
        // headed once rather than twelve times.
        string? group = null;

        while (interview.Question is { } question)
        {
            foreach (string line in InitRenderer.Lines(question, group, this.Ink))
            {
                output.WriteLine(line);
            }

            group = question.Group;

            output.Write(InitRenderer.Prompt(question, this.Ink));

            if (environment.ConfirmationReader?.ReadLine() is not { } typed)
            {
                // No console, or the end of a pipe. An interview that blocks for ever on a
                // host nobody is sitting at is worse than one that will not start.
                this.Refuse(error, "ripcord: init asks questions, and nothing is answering.");
                error.WriteLine("  run it from a console; nothing was written.");
                return ExitCode.Refused;
            }

            interview = interview.Answer(typed);

            if (interview.Rejection is { } rejection)
            {
                error.WriteLine($"  {rejection}");
            }
        }

        IReadOnlyList<string> dropped =
            ConfigurationTemplate.DroppedAcknowledgements(interview.Draft!, previous);

        return this.WriteConfiguration(
            output,
            error,
            options,
            ConfigurationTemplate.Render(interview.Draft!, previous),
            previous,
            new InitClosing(
                dropped,
                // What the file still names after the entries init could take out.
                interview.StaleAcknowledgements.Count > dropped.Count
                    ? interview.StaleAcknowledgements
                    : [],
                seed?.Listener?.Enabled == true));
    }

    /// `ListenerCarried`: the carried `listener` section switches the listener on, so the
    /// running service has to be restarted to read the new file.
    private sealed record InitClosing(
        IReadOnlyList<string> DroppedAcknowledgements,
        IReadOnlyList<string> Stale,
        bool ListenerCarried);

    private ExitCode WriteConfiguration(
        TextWriter output,
        TextWriter error,
        InitOptions options,
        string yaml,
        string? previous,
        InitClosing closing)
    {
        IReadOnlyList<string> stale = closing.Stale;

        output.WriteLine();
        output.WriteLine(this.Ink.Apply(Rendering.Ink.Bold("THE FILE")));
        output.WriteLine(this.Ink.Apply(Rendering.Ink.Faint(Layout.Line(Layout.Width))));
        output.WriteLine();

        // The file itself, uncoloured: it is the thing being agreed to, and tinting somebody
        // else's YAML is where a rendering starts editorialising.
        output.WriteLine(yaml.TrimEnd());
        output.WriteLine();

        if (ConfigurationTemplate.CarriedOver(previous) is { Count: > 0 } carried)
        {
            output.WriteLine(this.Ink.Apply(
                Rendering.Ink.Faint($"  kept as it was: {string.Join(", ", carried)}")));
        }

        foreach (string vm in closing.DroppedAcknowledgements)
        {
            output.WriteLine(this.Ink.Apply(Rendering.Ink.Amber(
                $"  dropped from checks, VM no longer declared: {vm}")));
        }

        // What init could not take out itself: said before the yes, and the yes is not the
        // default, so the reflex Enter does not write a file every command refuses.
        if (stale.Count > 0)
        {
            output.WriteLine();

            foreach (string entry in stale)
            {
                output.WriteLine(this.Ink.Apply(Rendering.Ink.Amber($"  {entry}")));
            }

            output.WriteLine(this.Ink.Apply(Rendering.Ink.Amber(
                "  Every command refuses the file until these are removed from 'checks'.")));
            output.WriteLine(this.Ink.Apply(Rendering.Ink.Amber(
                "  Answer y only if you will edit checks right after.")));
            output.WriteLine();
        }

        if (options.DryRun)
        {
            output.WriteLine("  --dry-run: nothing was written.");
            return ExitCode.Success;
        }

        // The previous file is kept, so this is recoverable and asks for a word rather than
        // the typed node name every irreversible operation asks for.
        bool yesByDefault = stale.Count == 0;

        output.Write(this.Ink.Apply(
            "  " + Rendering.Ink.Bold($"Write this to {options.Path}?")
            + Rendering.Ink.Faint(yesByDefault ? "  y/n [y]" : "  y/n [n]")
            + Rendering.Ink.Cyan(" > ")));

        string? answer = environment.ConfirmationReader?.ReadLine()?.Trim().ToLowerInvariant();

        if (answer is "n" or "no" || (!yesByDefault && answer is not ("y" or "yes")))
        {
            this.Refuse(error, "ripcord: not written, nothing was changed.");
            return ExitCode.Refused;
        }

        ConfigurationWrite written = ports.ConfigStore.Write(
            options.Path, yaml, options.Path + ".1");

        if (written.FailureMessage is { } failure)
        {
            error.WriteLine($"ripcord: {failure}");

            error.WriteLine(written.Kept is { } moved
                ? $"  the previous configuration is at {moved}"
                : "  the configuration on this host was not touched.");

            return ExitCode.LocalAccessFailure;
        }

        output.WriteLine();
        output.WriteLine(this.Ink.Apply(Rendering.Ink.Green($"  wrote {options.Path}")));

        if (written.Kept is { } kept)
        {
            output.WriteLine($"  the previous one is at {kept}");
        }

        output.WriteLine();

        if (closing.ListenerCarried)
        {
            output.WriteLine(this.Ink.Apply(
                "  Next:  " + Rendering.Ink.Bold("ripcord status")
                + ", then " + Rendering.Ink.Bold("ripcord service restart")));
            output.WriteLine("  The listener reads this file only when it starts.");
        }
        else
        {
            output.WriteLine(this.Ink.Apply("  Next:  " + Rendering.Ink.Bold("ripcord status")));
            output.WriteLine("  The pair channel is separate and off until two certificates exist");
            output.WriteLine("  on the hosts - see docs/commands/serve.md.");
        }

        return ExitCode.Success;
    }

    /// What the host can say about itself before there is a configuration. Neither list is
    /// required: a host whose Hyper-V cannot be read still completes the interview by typing,
    /// and the reason it could not be read is in the diagnostic log.
    private async Task<InterviewFacts> ReadFactsAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<Domain.Inventory.HostSwitch> switches = await ports.Provider
                .GetSwitchesAsync(cancellationToken)
                .ConfigureAwait(false);

            HostState state = await ports.Provider
                .GetLocalStateAsync(cancellationToken)
                .ConfigureAwait(false);

            return InterviewFacts.From(environment.MachineName, switches, state);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ports.Diagnostics.Write(Domain.Diagnostics.DiagnosticEntry.Of(
                "init",
                "this host's switches and VMs could not be read, so init asks for them",
                exception.ToString()));

            return InterviewFacts.Unread(environment.MachineName);
        }
    }

    private sealed record InitOptions(string Path, ExpectedRole? Role, bool DryRun);

    private bool TryReadInitOptions(
        string[] args, out InitOptions options, out string? error)
    {
        string? path = null;
        ExpectedRole? role = null;
        bool dryRun = false;
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config":
                    if (!TryConsumeConfig(args, ref index, ref path, out error))
                    {
                        options = new InitOptions(environment.DefaultConfigurationPath, null, false);
                        return false;
                    }

                    break;

                case "--role" when index + 1 < args.Length:
                    role = args[++index].ToLowerInvariant() switch
                    {
                        "primary" => ExpectedRole.Primary,
                        "dr" or "replica" => ExpectedRole.Replica,
                        _ => null,
                    };

                    if (role is null)
                    {
                        error = $"--role takes primary or dr, not '{args[index]}'.";
                        options = new InitOptions(environment.DefaultConfigurationPath, null, false);
                        return false;
                    }

                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    options = new InitOptions(environment.DefaultConfigurationPath, null, false);
                    return false;
            }
        }

        options = new InitOptions(path ?? environment.DefaultConfigurationPath, role, dryRun);
        return true;
    }

    private async Task<ExitCode> StatusAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadConfigurationPath(args, out string path, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        StatusQuery query = new(ports.ConfigStore, this.Pair(), ports.DeploymentExecutor);

        StatusOutcome outcome = await query
            .ExecuteAsync(new StatusRequest(path, environment.MachineName), cancellationToken)
            .ConfigureAwait(false);

        // Degradations are never silent: a host whose free space could not be read renders
        // its VMs all the same, and the operator has to know which part is missing.
        foreach (string note in outcome.Notes)
        {
            error.WriteLine($"ripcord: {note}");
        }

        if (outcome.Rendered is { } rendered)
        {
            output.Write(StatusRenderer.Render(
                rendered.View,
                rendered.Configuration.Peer.OfflineAfter,
                ports.Clock.UtcNow,
                this.KnownUpdate(),
                this.Ink,
                rendered.Listener));
            return outcome.Code;
        }

        this.WriteFailure(error, outcome);
        return outcome.Code;
    }

    /// Read-only, and the one command whose exit code reports on the infrastructure rather
    /// than on the tool: 1 means at least one critical rule is violated (decision D7).
    ///
    /// `--notify` is the scheduled task's form of the same command. Delivery never changes
    /// the exit code: a relay that is down must not be reported as a pair that is broken.
    private async Task<ExitCode> CheckAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadCheckOptions(args, out CheckOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        CheckQuery query = new(ports.ConfigStore, this.Pair(), ports.Clock);

        CheckOutcome outcome = await query
            .ExecuteAsync(
                new CheckRequestOptions(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName),
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Report is { } report)
        {
            output.Write(CheckRenderer.Render(report, this.KnownUpdate(), this.Ink));

            if (options.Notify)
            {
                await this.NotifyAsync(
                        report, outcome.Configuration!, options.DryRun, error, cancellationToken)
                    .ConfigureAwait(false);
            }

            return outcome.Code;
        }

        this.WriteFailure(error, new StatusOutcome(
            outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

        return outcome.Code;
    }

    /// Whatever the alerting decided goes to stderr beside the report: a notification that
    /// was held, refused or never attempted is a degraded state, and degradations are never
    /// silent (rule 5).
    private async Task NotifyAsync(
        CheckReport report,
        RipcordConfiguration configuration,
        bool dryRun,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        AlertDispatch dispatch = new(ports.AlertState, ports.Notifier, ports.Clock);

        AlertOutcome outcome = await dispatch
            .ExecuteAsync(report, configuration.Alerting, dryRun, cancellationToken)
            .ConfigureAwait(false);

        foreach (string note in outcome.Notes)
        {
            error.WriteLine($"ripcord: {note}");
        }
    }

    /// Reports that a newer release exists, and nothing more. Installing it is `ripcord
    /// update`, which is a separate switch in the configuration as well as a separate verb:
    /// permission to look is not permission to replace the binary this host fails over with.
    /// Switched off unless the configuration says otherwise — a host with no outbound access
    /// is the design, not a limitation.
    private async Task<ExitCode> CheckUpdateAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadConfigurationPath(args, out string path, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        UpdateQuery query = new(ports.ConfigStore, ports.ReleaseFeed);

        UpdateOutcome outcome = await query
            .ExecuteAsync(
                new UpdateCheckOptions(
                    path, environment.MachineName, this.LocalBuild.Version),
                cancellationToken)
            .ConfigureAwait(false);

        this.RecordKnownRelease(outcome.Version);

        if (outcome.Code == ExitCode.Success && outcome.Status is { } status)
        {
            output.WriteLine(status.Verdict == UpdateVerdict.UpdateAvailable
                ? $"An update is available: {status.Explanation}."
                : $"{status.Explanation}.");

            return outcome.Code;
        }

        this.WriteFailure(error, new StatusOutcome(
            outcome.Code,
            null,
            outcome.Errors,
            outcome.FailureMessage ?? outcome.Status?.Explanation,
            []));

        return outcome.Code;
    }

    /// The service entry point. It serves the published snapshot and nothing else — it never
    /// reads Hyper-V, which is the whole point of the privilege split in decision D18.
    private async Task<ExitCode> ServeAsync(
        string[] args, TextWriter error, CancellationToken cancellationToken)
    {
        // Its own parser, not the deployment one: `serve` takes --config and nothing else, and
        // borrowing a parser that also accepts --dry-run and --remove meant accepting two
        // options it then ignored. An option that appears to be read and is not is worse on
        // this command surface than one that is refused.
        if (!this.TryReadConfigurationPath(args, out string path, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        ConfigurationValidation validation = ConfigurationGate.Open(
            ports.ConfigStore, path, environment.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            this.WriteFailure(error, new StatusOutcome(
                ExitCode.InvalidConfiguration, null, validation.Errors, null, []));
            return ExitCode.InvalidConfiguration;
        }

        // A node with the listener switched off must degrade, not fail: that is the
        // documented off switch.
        if (PeerEndpoint.From(configuration) is not { } endpoint)
        {
            error.WriteLine("ripcord: the listener is disabled on this node, nothing to serve.");
            return ExitCode.Success;
        }

        await ports.PeerListener
            .RunAsync(configuration.Listener, endpoint.Rules, cancellationToken)
            .ConfigureAwait(false);

        return ExitCode.Success;
    }

    /// Mutating, so it obeys both rules at once: `--dry-run` shows the plan and stops, and
    /// without it nothing happens until the operator types the node name.
    /// The read-only page of milestone 7. It runs the same `check` the console does and lays
    /// its answer out in a browser — it decides nothing of its own, and it is served on the
    /// loopback interface only.
    private async Task<ExitCode> DashboardAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadConfigurationPath(args, out string path, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        ConfigurationValidation validation = ConfigurationGate.Open(
            ports.ConfigStore, path, environment.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            this.WriteFailure(error, new StatusOutcome(
                ExitCode.InvalidConfiguration, null, validation.Errors, null, []));
            return ExitCode.InvalidConfiguration;
        }

        // Off is the ordinary state, and asking a node that serves nothing to serve is not an
        // error — the same degradation `serve` makes when the listener is switched off.
        if (!configuration.Dashboard.Enabled)
        {
            error.WriteLine(
                "ripcord: the dashboard is disabled on this node, nothing to serve.");
            return ExitCode.Success;
        }

        output.WriteLine(
            $"ripcord: serving the read-only page on http://127.0.0.1:{configuration.Dashboard.Port}/");

        await ports.DashboardServer
            .RunAsync(
                configuration.Dashboard,
                token => this.PageAsync(path, configuration.Dashboard.Refresh, token),
                cancellationToken)
            .ConfigureAwait(false);

        return ExitCode.Success;
    }

    /// One reading per request, and one reading only: `check` already carries the pair it
    /// judged, so the page shows the findings and the inventory they were decided from rather
    /// than two reads taken a moment apart that can disagree.
    ///
    /// Nothing here throws out of the callback. A page is the only channel this command has —
    /// there is no stderr anybody is watching and no exit code to carry a failure — so a
    /// failed reading has to become a page that says it failed.
    private async Task<string> PageAsync(
        string path, TimeSpan refresh, CancellationToken cancellationToken)
    {
        DateTimeOffset now = ports.Clock.UtcNow;

        try
        {
            CheckQuery query = new(ports.ConfigStore, this.Pair(), ports.Clock);

            CheckOutcome outcome = await query
                .ExecuteAsync(
                    new CheckRequestOptions(path, environment.MachineName), cancellationToken)
                .ConfigureAwait(false);

            if (outcome.View is not { } view)
            {
                return DashboardRenderer.Render(
                    DashboardView.Unavailable(now, Reason(outcome)), refresh);
            }

            // A `CheckOutcome` carrying a view always carries the configuration it was read
            // with: the query returns the two together or neither.
            return DashboardRenderer.Render(
                DashboardView.Of(
                    view,
                    outcome.Report,
                    outcome.Configuration!.Peer.OfflineAfter,
                    now,
                    outcome.Report?.Notes ?? []),
                refresh);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DashboardRenderer.Render(
                DashboardView.Unavailable(now, exception.Message), refresh);
        }
    }

    private static string Reason(CheckOutcome outcome) =>
        outcome.FailureMessage
            ?? (outcome.Errors.Count > 0
                ? string.Join(
                    ", ",
                    outcome.Errors.Select(error => $"{error.Path} {error.Message}".Trim()))
                : "the pair could not be read");

    /// The listener service: what it is doing, and the two things that change it.
    ///
    /// Bare, it reports and changes nothing — rule 3, and the question an operator asks first.
    /// `install` and `remove` are words rather than flags because both mutate a host that may
    /// be running a domain controller, and a word is harder to type by accident than a flag
    /// next to the one you meant.
    private ExitCode Service(string[] args, TextWriter output, TextWriter error)
    {
        string verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "";

        return verb switch
        {
            // One report under two spellings: `status` is what an operator types by habit.
            "" => this.ServiceState(args, output, error),
            "status" => this.ServiceState(args[1..], output, error),
            "install" => this.Deploy(args[1..], output, error, removing: false),
            "remove" => this.Deploy(args[1..], output, error, removing: true),
            "restart" => this.Restart(args[1..], output, error),
            _ => Unknown(verb, error),
        };
    }

    /// `status` is left off on purpose: it is the bare form spelt out, not a verb of its own.
    private static ExitCode Unknown(string verb, TextWriter error)
    {
        error.WriteLine($"ripcord: 'service {verb}' is not a service verb.");
        error.WriteLine("  to look:    ripcord service");
        error.WriteLine("  to change:  ripcord service install | remove | restart");

        return ExitCode.InvalidConfiguration;
    }

    /// Read-only, and the reason this command is a noun. Installed and running are two facts,
    /// and an operator asking "is the listener up" must not have to read a deployment plan
    /// backwards to find out — nor, when it is down, go hunting for why.
    ///
    /// The service is shown even when `ripcord.yaml` does not load: that is the likeliest
    /// reason it stopped. The exit code is still the configuration's.
    private ExitCode ServiceState(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryReadConfigurationPath(args, out string path, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        ServiceReport report = new ServiceInspection(
                ports.ConfigStore, ports.DeploymentExecutor, ports.LogReader)
            .Inspect(
                new DeploymentRequest(
                    path, environment.MachineName, environment.BinaryPath, Remove: false),
                ports.Clock.UtcNow,
                BuildInfo.VersionWithCommit);

        output.Write(DeploymentRenderer.RenderState(report, this.Ink));

        if (report.Deployment.Observed is null)
        {
            this.WriteDeploymentFailure(error, report.Deployment);
            return report.Deployment.Code;
        }

        return ExitCode.Success;
    }

    /// Putting an edited configuration into effect. Not a deployment: nothing about the host
    /// changes, the listener simply reads the file again — which it only does when it starts.
    ///
    /// No typed confirmation. It is over in a second, it changes nothing that outlives it, and
    /// the pair view on the other host goes offline for that second and comes back. Asking an
    /// operator to type a node name for that is how a confirmation becomes a reflex, and a
    /// reflex is what the failover ones must not be.
    private ExitCode Restart(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryReadOptions(args, out DeployOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        ListenerDeployment deployment = new(ports.ConfigStore, ports.DeploymentExecutor);

        DeploymentOutcome outcome = deployment.Plan(new DeploymentRequest(
            options.ConfigurationPath ?? environment.DefaultConfigurationPath,
            environment.MachineName,
            environment.BinaryPath,
            Remove: false));

        if (outcome.Observed is not { } observed || outcome.Desired is not { } desired)
        {
            this.WriteDeploymentFailure(error, outcome);
            return outcome.Code;
        }

        // Started, it would stop again at once: `serve` has nothing to do.
        if (!desired.ListenerEnabled)
        {
            error.WriteLine("ripcord: the listener is disabled in ripcord.yaml.");
            error.WriteLine("  Set listener.enabled: true, or run 'ripcord service remove'.");
            return ExitCode.Refused;
        }

        DeploymentPlan plan = DeploymentPlan.ToRestart(observed);

        if (!plan.ChangesAnything)
        {
            error.WriteLine(
                "ripcord: there is no listener service on this host to restart. "
                + "Install it with 'ripcord service install'.");

            return ExitCode.InvalidConfiguration;
        }

        output.Write(DeploymentRenderer.Render(
            plan, desired, removing: false, observed,
            heading: "RIPCORD LISTENER RESTART", palette: this.Ink));

        if (options.DryRun)
        {
            output.WriteLine("  Nothing was changed. Re-run without --dry-run to apply.");
            return ExitCode.Success;
        }

        DeploymentResult result = deployment.Apply(plan, desired);

        output.Write(DeploymentRenderer.RenderResult(
            result.Applied, result.Failed, result.FailureMessage, this.Ink));

        return result.Code;
    }

    private ExitCode Deploy(string[] args, TextWriter output, TextWriter error, bool removing)
    {
        if (!TryReadOptions(args, out DeployOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        options = options with { Remove = removing };

        ListenerDeployment deployment = new(ports.ConfigStore, ports.DeploymentExecutor);

        DeploymentOutcome outcome = deployment.Plan(new DeploymentRequest(
            options.ConfigurationPath ?? environment.DefaultConfigurationPath,
            environment.MachineName,
            environment.BinaryPath,
            options.Remove));

        if (outcome.Plan is not { } plan || outcome.Desired is not { } desired)
        {
            this.WriteDeploymentFailure(error, outcome);
            return outcome.Code;
        }

        output.Write(
            DeploymentRenderer.Render(plan, desired, options.Remove, outcome.Observed, palette: this.Ink));

        // Before the dry-run check and the prompt: a plan that cannot be applied asks nothing.
        if (plan.IsBlocked)
        {
            return ExitCode.InvalidConfiguration;
        }

        if (!plan.ChangesAnything)
        {
            return ExitCode.Success;
        }

        if (options.DryRun)
        {
            output.WriteLine("  Nothing was changed. Re-run without --dry-run to apply.");
            return ExitCode.Success;
        }

        // Refused, like every other declined confirmation: an operator who typed the wrong
        // thing and a configuration that cannot be read are different answers, and a caller
        // reading the exit code has no other way to tell them apart.
        if (!this.Confirmed(
            output,
            error,
            "This creates a Windows service and opens an inbound port on this host."))
        {
            return ExitCode.Refused;
        }

        DeploymentResult result = deployment.Apply(plan, desired);
        output.Write(DeploymentRenderer.RenderResult(
            result.Applied, result.Failed, result.FailureMessage, this.Ink));

        return result.Code;
    }

    /// Mutating, so it obeys both rules: `--dry-run` shows the whole plan and stops, and
    /// without it nothing is created until the operator types the node name. A test failover
    /// creates and destroys a real VM on this host — a keystroke is not a decision.
    /// The reason the project exists. Everything about it is shaped by being read under
    /// pressure: `--dry-run` prints the whole cross-host plan and stops, and a real run does
    /// nothing at all until the node name is typed in full.
    /// `failover` and `failback` are the same command with the scenario fixed rather than
    /// typed. `failback` is the planned sequence pointed home, so giving it its own parser
    /// would be a second place for the scope rules to drift.
    private async Task<ExitCode> FailoverAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        FailoverOperation? fixedScenario = null)
    {
        if (!TryReadFailoverOptions(
            args, fixedScenario, out FailoverOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            WriteUsage(error);
            return ExitCode.InvalidConfiguration;
        }

        // Rule 3, and the sharpest case of it in the whole tool: this shuts production down and
        // moves it to the other host. A keystroke is not a decision.
        if (!options.DryRun && !this.Confirmed(output, error, Impact(options)))
        {
            return ExitCode.Refused;
        }

        SweepReport sweep = await new FailoverSweepQuery(
                ports.ConfigStore, this.Pair(), ports.Provider, ports.Audit, ports.Clock, FailoverTiming.Default)
            .ExecuteAsync(
                new SweepCommand(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    options.Scenario,
                    options.Scope,
                    options.DryRun,
                    environment.UserName,
                    this.LocalBuild),
                cancellationToken)
            .ConfigureAwait(false);

        // Printed before the runs, not after: a VM left out of a sweep is something the
        // operator has to know about while they still have the screen in front of them.
        foreach (SweepExclusion excluded in sweep.Excluded)
        {
            error.WriteLine($"ripcord: {excluded.VmName} was left out — {excluded.Reason}");
        }

        foreach (SweptVm swept in sweep.Ran)
        {
            this.WriteVmOutcome(swept, output, error);
        }

        foreach (string skipped in sweep.NotAttempted)
        {
            error.WriteLine($"ripcord: {skipped} was not attempted; the sweep stopped first");
        }

        if (sweep.Ran.Count == 0)
        {
            this.WriteFailure(error, new StatusOutcome(
                sweep.Code, null, sweep.Errors, sweep.FailureMessage, []));
        }

        return sweep.Code;
    }

    /// The first command to run on the original primary when it comes back from an unplanned
    /// failover, and before anything else is done to the pair.
    private async Task<ExitCode> FenceAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadFenceOptions(args, out FenceOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            WriteUsage(error);
            return ExitCode.InvalidConfiguration;
        }

        // Rule 3. It changes what this host does on its next boot, which is the whole point,
        // and a host left unable to start its VMs is its own kind of outage.
        if (!options.DryRun && !this.Confirmed(
            output,
            error,
            "This stops the VMs on this host from starting themselves when it reboots."))
        {
            return ExitCode.Refused;
        }

        FenceOutcome outcome = await new FenceQuery(
                ports.ConfigStore, this.Pair(), ports.Provider, ports.Audit, ports.Clock)
            .ExecuteAsync(
                new FenceCommand(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    options.DryRun,
                    environment.UserName,
                    this.LocalBuild),
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Errors.Count > 0)
        {
            this.WriteFailure(error, new StatusOutcome(
                outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

            return outcome.Code;
        }

        output.Write(FenceRenderer.Render(outcome, ports.Clock.UtcNow, this.Ink));
        return outcome.Code;
    }

    private readonly record struct FenceOptions(string? ConfigurationPath, bool DryRun);

    private static bool TryReadFenceOptions(
        string[] args, out FenceOptions options, out string? error)
    {
        string? path = null;
        bool dryRun = false;

        error = null;
        options = new FenceOptions(null, false);

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    return false;
            }
        }

        options = new FenceOptions(path, dryRun);
        return true;
    }

    private void WriteVmOutcome(SweptVm swept, TextWriter output, TextWriter error)
    {
        FailoverOutcome outcome = swept.Outcome;

        if (outcome.Report is { } report)
        {
            output.Write(FailoverRenderer.Render(report, DateTimeOffset.UtcNow, this.Ink));
            return;
        }

        if (outcome.Code == ExitCode.Refused)
        {
            error.WriteLine(
                $"ripcord: {swept.VmName} refused, nothing was changed: {outcome.FailureMessage}");

            foreach (Finding finding in outcome.Refusal?.Unevaluated ?? [])
            {
                error.WriteLine($"  {finding.Rule.Id}: {finding.Observed}");
            }

            return;
        }

        this.WriteFailure(error, new StatusOutcome(
            outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));
    }

    /// What the operator is agreeing to, in the words of the scenario they typed. A planned
    /// failover sends the last changes across before it moves; an unplanned one cannot, so
    /// everything written since the last replication cycle is gone — and printing the planned
    /// wording here would conceal the cost of the command being confirmed.
    private static string Impact(FailoverOptions options)
    {
        string subject = options.Scope.NamedVms.Count > 0
            ? $"'{string.Join("', '", options.Scope.NamedVms)}'"
            : options.Scope.Priority is { } tier
                ? $"every {tier} VM"
                : "every VM in this configuration";

        return options.Scenario switch
        {
            FailoverOperation.UnplannedFailover =>
                $"This brings {subject} up on this host from the last replicated point. "
                    + "Everything written since the last replication is lost.",

            FailoverOperation.Failback =>
                $"This shuts down {subject} here and moves it back to the other host.",

            _ => $"This shuts down {subject} and fails it over to the other host.",
        };
    }

    private sealed record FailoverOptions(
        string? ConfigurationPath, SweepScope Scope, FailoverOperation Scenario, bool DryRun);

    /// `--scenario` is required rather than defaulted. Planned and unplanned are different
    /// operations with different consequences, and a default would let the wrong one run
    /// because nobody typed the word.
    ///
    /// So is the scope: exactly one of `--vm`, `--all` and `--priority`. Neither a VM nor a
    /// sweep is a missing argument, not a failover of everything.
    private static bool TryReadFailoverOptions(
        string[] args,
        FailoverOperation? fixedScenario,
        out FailoverOptions options,
        out string? error)
    {
        string? path = null;
        List<string> vmNames = [];
        string? scenario = null;
        string? priority = null;
        bool all = false;
        bool dryRun = false;

        error = null;
        options = new FailoverOptions(
            null, SweepScope.Named([]), FailoverOperation.PlannedFailover, false);

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--all":
                    all = true;
                    break;

                case "--vm" when index + 1 < args.Length:
                    vmNames.Add(args[++index]);
                    break;

                case "--vm":
                    error = "--vm needs a VM name.";
                    return false;

                case "--priority" when index + 1 < args.Length:
                    priority = args[++index];
                    break;

                case "--priority":
                    error = "--priority needs a tier.";
                    return false;

                case "--scenario" when index + 1 < args.Length:
                    scenario = args[++index];
                    break;

                case "--scenario":
                    error = "--scenario needs a value.";
                    return false;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    return false;
            }
        }

        FailoverOperation operation;

        if (fixedScenario is { } fixedOperation)
        {
            // `failback` is one operation. A --scenario on it would be the operator naming a
            // second one, and the two could only disagree.
            if (scenario is not null)
            {
                error = "failback takes no --scenario.";
                return false;
            }

            operation = fixedOperation;
        }
        else if (scenario is null)
        {
            error = "--scenario is required: planned or unplanned.";
            return false;
        }
        else if (!TryReadScenario(scenario, out operation))
        {
            error = $"--scenario '{scenario}' is not one this binary runs; "
                + "use planned or unplanned.";
            return false;
        }

        if (!TryReadScope(vmNames, all, priority, out SweepScope scope, out error))
        {
            return false;
        }

        options = new FailoverOptions(path, scope, operation, dryRun);
        return true;
    }

    private static bool TryReadScenario(string written, out FailoverOperation operation)
    {
        operation = FailoverOperation.PlannedFailover;

        switch (written)
        {
            case "planned":
                return true;

            case "unplanned":
                operation = FailoverOperation.UnplannedFailover;
                return true;

            default:
                return false;
        }
    }

    /// Exactly one form. `--all --vm VM-DC-01` has no reading that is obviously right, and a
    /// tool that guesses at one moves production on the guess.
    private static bool TryReadScope(
        List<string> vmNames,
        bool all,
        string? priority,
        out SweepScope scope,
        out string? error)
    {
        scope = SweepScope.Named([]);
        error = null;

        int forms = (vmNames.Count > 0 ? 1 : 0) + (all ? 1 : 0) + (priority is null ? 0 : 1);

        if (forms == 0)
        {
            error = "one of --vm, --all or --priority is required.";
            return false;
        }

        if (forms > 1)
        {
            error = "--vm, --all and --priority are alternatives; use one.";
            return false;
        }

        if (all)
        {
            scope = SweepScope.All;
            return true;
        }

        if (priority is not null)
        {
            // By name, never by ordinal, for the same reason the configuration reads it that
            // way: Enum.TryParse would read "1" as P2 and sweep the second tier.
            if (!Enum.GetNames<VmPriority>().Contains(priority, StringComparer.OrdinalIgnoreCase)
                || !Enum.TryParse(priority, ignoreCase: true, out VmPriority tier))
            {
                error = $"--priority '{priority}' is not a declared tier; "
                    + "use one of " + string.Join(", ", Enum.GetNames<VmPriority>()) + ".";
                return false;
            }

            scope = SweepScope.OfPriority(tier);
            return true;
        }

        scope = SweepScope.Named(vmNames);
        return true;
    }

    private async Task<ExitCode> TestFailoverAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadTestFailoverOptions(args, out TestFailoverOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            WriteUsage(error);
            return ExitCode.InvalidConfiguration;
        }

        // Unattended skips the confirmation and pays for it elsewhere: the run is refused
        // for any VM the configuration has not named, and every unevaluable finding blocks
        // rather than only the six that block an attended run.
        if (!options.DryRun && !options.Unattended && !this.Confirmed(
            output,
            error,
            "This creates a test VM on this host and destroys it again when the test ends."))
        {
            return ExitCode.Refused;
        }

        TestFailoverQuery query = new(
            ports.ConfigStore, this.Pair(), ports.Provider, ports.Clock, TestFailoverTiming.Default);

        TestFailoverOutcome outcome = await query
            .ExecuteAsync(
                new TestFailoverRequest(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    options.VmNames,
                    options.All,
                    options.DryRun,
                    options.Unattended),
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Report is { } report)
        {
            output.Write(TestFailoverRenderer.Render(
                report, outcome.Configuration?.Replication.TestFailoverSwitch, this.Ink));
            return outcome.Code;
        }

        if (outcome.Code == ExitCode.Refused)
        {
            error.WriteLine($"ripcord: refused, nothing was changed: {outcome.FailureMessage}");
            return outcome.Code;
        }

        this.WriteFailure(error, new StatusOutcome(
            outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

        return outcome.Code;
    }

    private readonly record struct TestFailoverOptions(
        string? ConfigurationPath,
        IReadOnlyList<string> VmNames,
        bool All,
        bool DryRun,
        bool Unattended);

    /// `--vm` may be repeated. Neither `--vm` nor `--all` is refused rather than defaulted:
    /// a mutating command with no subject must not guess which VMs were meant.
    private static bool TryReadTestFailoverOptions(
        string[] args, out TestFailoverOptions options, out string? error)
    {
        string? path = null;
        List<string> vmNames = [];
        bool all = false;
        bool dryRun = false;
        bool unattended = false;
        error = null;
        options = default;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--all":
                    all = true;
                    break;

                case "--unattended":
                    unattended = true;
                    break;

                case "--vm" when index + 1 < args.Length:
                    vmNames.Add(args[++index]);
                    break;

                case "--vm":
                    error = "--vm needs a VM name.";
                    return false;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    return false;
            }
        }

        if (all && vmNames.Count > 0)
        {
            error = "--all and --vm cannot be combined.";
            return false;
        }

        if (!all && vmNames.Count == 0)
        {
            error = "name the VMs with --vm, or test every one with --all.";
            return false;
        }

        options = new TestFailoverOptions(path, vmNames, all, dryRun, unattended);
        return true;
    }

    private PairReader Pair() =>
        new(
            new LocalStateReader(
                ports.Provider, ports.HostSystem, ports.Certificates, ports.Diagnostics),
            ports.PeerChannel,
            ports.SnapshotStore,
            ports.Clock,
            this.LocalBuild,
            ports.Diagnostics);

    /// What this binary is, for the snapshot it publishes and for the skew check that reads the
    /// peer's. Both halves, because two builds at the same version from different commits differ
    /// in exactly the way nobody thinks to check.
    internal BuildIdentity LocalBuild =>
        environment.Build ?? new BuildIdentity(BuildInfo.Version, BuildInfo.CommitHash);

    /// Written down rather than only printed. Nothing on these hosts looks on its own, and no
    /// other command looks while it runs — a fifteen-second timeout in front of an unplanned
    /// failover is what the design exists to avoid — so this file is the only way `status` and
    /// `check` can mention a release at all.
    private void RecordKnownRelease(string? version)
    {
        if (version is { Length: > 0 } published)
        {
            ports.UpdateNotices.Write(new UpdateNotice(published, ports.Clock.UtcNow));
        }
    }

    /// What the last look found, if it is still worth saying. No network: a command run
    /// during an incident on a host with no outbound access must not wait fifteen seconds to
    /// find out about a release.
    private string? KnownUpdate() =>
        Domain.Updates.UpdateNotices.For(
            ports.UpdateNotices.Read(), this.LocalBuild.Version, ports.Clock.UtcNow);

    private bool Confirmed(TextWriter output, TextWriter error, string consequence)
    {
        output.WriteLine($"  {consequence}");
        output.Write($"  {ConfirmationPrompt} ({environment.MachineName}): ");

        string? typed = environment.ConfirmationReader?.ReadLine();

        if (string.Equals(typed?.Trim(), environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        this.Refuse(error, "ripcord: not confirmed, nothing was changed.");
        return false;
    }

    private void WriteDeploymentFailure(TextWriter error, DeploymentOutcome outcome)
    {
        if (outcome.FailureMessage is { } failure)
        {
            error.WriteLine($"ripcord: cannot inspect this host: {failure}");
            return;
        }

        if (this.WroteMissingConfiguration(error, outcome.Errors))
        {
            return;
        }

        this.Refuse(error, "ripcord: the configuration cannot be used.");

        foreach (ConfigurationError configurationError in outcome.Errors)
        {
            error.WriteLine($"  {configurationError.Path}: {configurationError.Message}");
        }
    }

    /// A file that is not there is not a file that is wrong. One is repaired by editing and
    /// the other by creating, and they were saying the same sentence — with the path in it
    /// three times and no mention of the command that produces one.
    private bool WroteMissingConfiguration(
        TextWriter error, IReadOnlyList<ConfigurationError> errors)
    {
        if (!MissingConfiguration.In(errors))
        {
            return false;
        }

        IReadOnlyList<string> lines = MissingConfiguration.Lines(
            errors.First(one => one.Kind == ConfigurationErrorKind.Missing).Path);

        // The first line says what is wrong and the last says what to type; the path between
        // them is a fact, not a refusal.
        this.Refuse(error, lines[0]);

        foreach (string line in lines.Skip(1))
        {
            error.WriteLine(line);
        }

        return true;
    }

    /// Every error at once, so six typos take one run rather than six.
    private void WriteFailure(TextWriter error, StatusOutcome outcome)
    {
        if (outcome.FailureMessage is { } failure)
        {
            error.WriteLine($"ripcord: cannot read the local Hyper-V state: {failure}");
            return;
        }

        if (this.WroteMissingConfiguration(error, outcome.Errors))
        {
            return;
        }

        this.Refuse(error, "ripcord: the configuration cannot be used.");

        foreach (ConfigurationError configurationError in outcome.Errors)
        {
            error.WriteLine(configurationError.Path.Length > 0
                ? $"  {configurationError.Path}: {configurationError.Message}"
                : $"  {configurationError.Message}");
        }
    }

    /// `--config` is parsed identically by every command, and identically wrong would be
    /// three different ways of reading the wrong host's file. Returns false with an error
    /// once the option is malformed or repeated.
    private static bool TryConsumeConfig(
        string[] args, ref int index, ref string? path, out string? error)
    {
        error = null;

        if (path is not null)
        {
            // Taking the last silently would mean reading a file the operator did not mean.
            error = "--config given more than once.";
            return false;
        }

        if (index + 1 >= args.Length)
        {
            error = "--config needs a path.";
            return false;
        }

        // Resolved here, once. Everything downstream treats this as the file's location —
        // the snapshot defaults beside it, and the folder that access is granted on comes from
        // it — and a relative path would make both depend on the directory the command
        // happened to be run from.
        try
        {
            path = Path.GetFullPath(args[++index]);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"--config is not a path: {exception.Message}";
            return false;
        }
    }

    /// An option given without its value is refused rather than silently falling back to the
    /// default file — reading the wrong host's configuration is the failure this tool exists
    /// to prevent.
    private bool TryReadConfigurationPath(string[] args, out string path, out string? error)
    {
        string? given = null;
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--config")
            {
                error = $"unexpected argument '{args[index]}'.";
                path = environment.DefaultConfigurationPath;
                return false;
            }

            if (!TryConsumeConfig(args, ref index, ref given, out error))
            {
                path = environment.DefaultConfigurationPath;
                return false;
            }
        }

        path = given ?? environment.DefaultConfigurationPath;
        return true;
    }

    private readonly record struct CheckOptions(
        string? ConfigurationPath, bool Notify, bool DryRun);

    /// `--dry-run` on its own is refused rather than accepted as a no-op: `check` changes
    /// nothing, so an operator who typed it meant the notification.
    private static bool TryReadCheckOptions(
        string[] args, out CheckOptions options, out string? error)
    {
        string? path = null;
        bool notify = false;
        bool dryRun = false;
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--notify":
                    notify = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    options = default;
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    options = default;
                    return false;
            }
        }

        if (dryRun && !notify)
        {
            error = "check changes nothing; --dry-run only means anything with --notify.";
            options = default;
            return false;
        }

        options = new CheckOptions(path, notify, dryRun);
        return true;
    }

    private readonly record struct DeployOptions(
        string? ConfigurationPath, bool DryRun, bool Remove);

    /// Unknown options are refused rather than ignored: on a mutating command, a misspelt
    /// `--dry-run` that silently became a real run is the worst possible outcome.
    private static bool TryReadOptions(
        string[] args, out DeployOptions options, out string? error)
    {
        string? path = null;
        bool dryRun = false;
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    options = default;
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    options = default;
                    return false;
            }
        }

        options = new DeployOptions(path, dryRun, Remove: false);
        return true;
    }

    /// Replaces this host's binary with a newer published release, once the operator has
    /// typed the node name. It is the only command that changes the tool rather than the
    /// infrastructure, and the only one whose consequences land on the *other* host too:
    /// updating one side makes the pair disagree, and a failover spanning both is refused
    /// while it does (decision D54). That is said above the prompt, never after it.
    /// Going back to the binary the last update set aside. No network, no signature to check:
    /// these are the bytes that were running on this host, and the hosts this runs on are
    /// meant to have no outbound access at all.
    private async Task<ExitCode> RollbackAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadUpdateOptions(args, out UpdateOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        RollbackQuery query = new(
            ports.ConfigStore, ports.BinarySwap, this.Pair(), ports.Clock);

        RollbackOutcome outcome = await query
            .ExecuteAsync(
                new RollbackRequest(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    environment.BinaryPath,
                    this.LocalBuild),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (string note in outcome.Notes)
        {
            error.WriteLine($"ripcord: {note}");
        }

        if (outcome.Plan is not { } plan)
        {
            this.WriteFailure(error, new StatusOutcome(
                outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

            return outcome.Code;
        }

        output.Write(RollbackRenderer.Render(
            plan, outcome.RunningVersion, plan.SetAsideAt, this.Ink));

        if (!plan.ChangesAnything)
        {
            return outcome.Code;
        }

        if (options.DryRun)
        {
            output.WriteLine();
            output.WriteLine("  Nothing was changed. Run without --dry-run to go back.");
            return ExitCode.Success;
        }

        if (!this.Confirmed(
            output,
            error,
            "This replaces the binary this host runs its failovers with."))
        {
            return ExitCode.Refused;
        }

        RollbackApplied applied = query.Apply(environment.BinaryPath);

        if (applied.Message is { } message)
        {
            this.Refuse(error, $"ripcord: {message}");
            return applied.Code;
        }

        output.WriteLine();
        output.WriteLine(this.Ink.Apply(Rendering.Ink.Green(
            $"  this host now runs {plan.PreviousVersion ?? "the binary that was set aside"}")));

        output.WriteLine("  the new version starts on the next service start, not now.");

        return applied.Code;
    }

    private async Task<ExitCode> UpdateAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadUpdateOptions(args, out UpdateOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        UpdatePlanQuery query = new(
            ports.ConfigStore, ports.ReleaseFeed, this.Pair(), ports.Clock, ports.BinarySwap);

        UpdatePlanOutcome outcome = await query
            .ExecuteAsync(
                new UpdatePlanRequest(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    this.LocalBuild,
                    environment.BinaryPath),
                cancellationToken)
            .ConfigureAwait(false);

        // It asked the feed, so it knows. Writing it down here too was missing: `update
        // --dry-run` paid for the network call and threw the answer away, leaving `status`
        // and `check` unable to mention a release until somebody also ran `check-update`.
        this.RecordKnownRelease(outcome.Version);

        // Degradations are never silent, and here one of them is "the consequences could not
        // be checked" — which the operator has to see before they type anything.
        foreach (string note in outcome.Notes)
        {
            error.WriteLine($"ripcord: {note}");
        }

        if (outcome.Plan is not { } plan)
        {
            this.WriteFailure(error, new StatusOutcome(
                outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

            return outcome.Code;
        }

        output.Write(UpdateRenderer.Render(plan, this.LocalBuild.Version, outcome.Version, this.Ink));

        if (!plan.ChangesAnything)
        {
            return outcome.Code;
        }

        if (options.DryRun)
        {
            output.WriteLine();
            output.WriteLine("  Nothing was changed. Run without --dry-run to install.");
            return ExitCode.Success;
        }

        if (!this.Confirmed(
            output,
            error,
            "This replaces the binary this host runs its failovers with."))
        {
            return ExitCode.Refused;
        }

        UpdateInstallation installation = new(
            ports.ReleaseSource, ports.BinarySwap, environment.ReleaseSigningKey);

        UpdateResult result = await installation
            .ApplyAsync(plan, environment.BinaryPath, outcome.Version!, cancellationToken)
            .ConfigureAwait(false);

        output.Write(UpdateRenderer.RenderResult(result, outcome.Version!, this.Ink));

        return result.Code;
    }

    private sealed record UpdateOptions(string? ConfigurationPath, bool DryRun);

    private static bool TryReadUpdateOptions(
        string[] args, out UpdateOptions options, out string? error)
    {
        string? path = null;
        bool dryRun = false;
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    options = new UpdateOptions(null, false);
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    options = new UpdateOptions(null, false);
                    return false;
            }
        }

        options = new UpdateOptions(path, dryRun);
        return true;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("ripcord - disaster recovery for a Hyper-V Replica pair");
        writer.WriteLine();
        writer.WriteLine("  ripcord init [--config <path>]     write this host's ripcord.yaml");
        writer.WriteLine("               [--role primary|dr]   by interview - run it again to");
        writer.WriteLine("               [--dry-run]           change it without losing it");
        writer.WriteLine("  ripcord status [--config <path>]   read both sides of the pair");
        writer.WriteLine("  ripcord check [--config <path>]    would a failover work right now");
        writer.WriteLine("                [--notify [--dry-run]]");
        writer.WriteLine("                                     notify on a new critical finding");
        writer.WriteLine("  ripcord service [status]           is the listener running, and if");
        writer.WriteLine("                                     not, why: last exit, its log");
        writer.WriteLine("  ripcord service install [--dry-run]");
        writer.WriteLine("  ripcord service remove [--dry-run]");
        writer.WriteLine("  ripcord service restart [--dry-run]");
        writer.WriteLine("                                     after editing ripcord.yaml: the");
        writer.WriteLine("                                     listener reads it only at start");
        writer.WriteLine("  ripcord test-failover (--vm <name> | --all) [--dry-run]");
        writer.WriteLine("                        [--unattended]");
        writer.WriteLine("                                     boot a replica in isolation, then destroy it");
        writer.WriteLine(
            "  ripcord failover --scenario planned|unplanned --vm <name> [--dry-run]");
        writer.WriteLine("                                     move a VM to the other host");
        writer.WriteLine("                                     --all or --priority P1 sweeps");
        writer.WriteLine("  ripcord failback --vm <name> [--dry-run]");
        writer.WriteLine("                                     move a VM back home once the");
        writer.WriteLine("                                     pair is protected again");
        writer.WriteLine("  ripcord fence [--dry-run]          stop this host's VMs starting");
        writer.WriteLine("                                     themselves — run it first when a");
        writer.WriteLine("                                     failed-over host comes back");
        writer.WriteLine("  ripcord serve [--config <path>]    run the read-only pair listener");
        writer.WriteLine("  ripcord dashboard [--config <path>]");
        writer.WriteLine("                                     serve the read-only page on");
        writer.WriteLine("                                     127.0.0.1 (off unless the");
        writer.WriteLine("                                     configuration switches it on)");
        writer.WriteLine("  ripcord update [--config <path>] [--dry-run]");
        writer.WriteLine("                                     install a newer release on this");
        writer.WriteLine("                                     host (off unless the configuration");
        writer.WriteLine("                                     switches it on)");
        writer.WriteLine("  ripcord rollback [--config <path>] go back to the binary the last");
        writer.WriteLine("                   [--dry-run]       update set aside - no network");
        writer.WriteLine("  ripcord check-update               is a newer release published");
        writer.WriteLine("                                     (off unless the configuration");
        writer.WriteLine("                                     switches it on)");
        writer.WriteLine("  ripcord version                    version and commit hash");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success (an unreachable peer included), "
            + "1 a critical rule is violated");
        writer.WriteLine("            or a test failover did not come up, "
            + "2 bad configuration,");
        writer.WriteLine("            3 local access failure, "
            + "4 refused or interrupted - nothing changed,");
        writer.WriteLine("            5 a mutating operation left an intermediate state.");
    }
}
