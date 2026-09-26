using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Application.Deployment;

public sealed record DeploymentRequest(
    string ConfigurationPath, string MachineName, string BinaryPath, bool Remove);

/// The plan, and everything needed to explain or carry it out. Nothing has changed yet.
public sealed record DeploymentOutcome(
    ExitCode Code,
    DeploymentPlan? Plan,
    DesiredDeployment? Desired,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage,

    /// What the host looked like when the plan was worked out. Carried so the command can say
    /// what is there as well as what would change — an operator asking "is the listener
    /// running" should not have to read a plan backwards to find out.
    ObservedDeployment? Observed = null);

/// What tells a running service's build from the binary on disk: its process record, and
/// this command's own build. Without it, an outdated service is not looked for.
public sealed record DeploymentBuild(IDiagnosticLogReader LogReader, string ThisBuild);

/// Works out what deploying the listener on this host would change. Deciding is separate from
/// doing on purpose: `--dry-run` runs exactly this and stops.
public sealed class ListenerDeployment(
    IConfigStore configStore, IDeploymentExecutor executor, DeploymentBuild? build = null)
{
    /// `service` is the reading the caller already made, if it made one.
    public DeploymentOutcome Plan(DeploymentRequest request, ObservedService? service = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        ConfigurationRead read = configStore.Read(request.ConfigurationPath);

        if (read.Errors.Count > 0)
        {
            return Invalid(read.Errors);
        }

        ConfigurationValidation validation =
            ConfigurationValidator.Validate(
                read.Document, request.MachineName, request.ConfigurationPath);

        if (validation.Configuration is not { } configuration)
        {
            return Invalid(validation.Errors);
        }

        DesiredDeployment desired = DesiredDeploymentFactory.From(configuration, request.BinaryPath);

        try
        {
            ObservedService listener = service ?? executor.ObserveService(RipcordService.Listener);
            ObservedDeployment observed = this.Outdated(
                executor.Observe(desired, listener), listener, desired, request.BinaryPath);

            return new DeploymentOutcome(
                ExitCode.Success,
                request.Remove ? DeploymentPlan.ToRemove(observed) : DeploymentPlan.For(desired, observed),
                desired,
                [],
                null,
                observed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DeploymentOutcome(
                ExitCode.LocalAccessFailure, null, desired, [], exception.Message);
        }
    }

    private ObservedDeployment Outdated(
        ObservedDeployment observed, ObservedService listener, DesiredDeployment desired, string binaryPath)
    {
        if (build is null)
        {
            return observed;
        }

        ObservedPublisher publisher = observed.Publisher;

        return observed with
        {
            ListenerOutdated = Judge(listener, desired.LogsFolder, RipcordService.Listener),
            Publisher = publisher with
            {
                Outdated = Judge(publisher.Service, desired.PublisherLogsFolder, RipcordService.Publisher),
            },
        };

        bool Judge(ObservedService running, string logsFolder, RipcordService which) =>
            ServiceProcessReading.Judge(
                build.LogReader, running, logsFolder, build.ThisBuild, binaryPath, which)
            is { Outdated: true };
    }

    /// Applied in order, stopping at the first failure: a half-applied plan is reported as
    /// such rather than pressed on with. The exit code says the host is in an intermediate
    /// state, which is what the operator needs to know before walking away.
    public DeploymentResult Apply(DeploymentPlan plan, DesiredDeployment desired)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<DeploymentStep> applied = [];

        foreach (DeploymentStep step in plan.Steps)
        {
            try
            {
                executor.Apply(step, desired);
                applied.Add(step);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new DeploymentResult(applied, step, exception.Message);
            }
        }

        return new DeploymentResult(applied, null, null);
    }

    private static DeploymentOutcome Invalid(IReadOnlyList<ConfigurationError> errors) =>
        new(ExitCode.InvalidConfiguration, null, null, errors, null);
}

public sealed record DeploymentResult(
    IReadOnlyList<DeploymentStep> Applied, DeploymentStep? Failed, string? FailureMessage)
{
    public bool Succeeded => this.Failed is null;

    /// A failure with a step behind it is the case exit code 5 exists for: the service exists
    /// and the port does not, or the firewall rule was deleted and its replacement never
    /// landed. An operator must not walk away from that, and the renderer says so in the same
    /// breath.
    ///
    /// A failure with nothing applied is 3, not 4. Code 4 says a human or a precondition
    /// stopped the run deliberately; `sc.exe` refusing for want of privilege is the tool
    /// failing to do its work, and a scheduled caller reading 4 would file it as "somebody
    /// declined" and never look. "Nothing was changed" is said in the text, where it does not
    /// have to carry two meanings at once.
    public ExitCode Code =>
        this.Failed is null
            ? ExitCode.Success
            : this.Applied.Count == 0
                ? ExitCode.LocalAccessFailure
                : ExitCode.IntermediateState;
}
