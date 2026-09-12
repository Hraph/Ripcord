using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Deployment;

namespace Ripcord.Application.Deployment;

public sealed record DeploymentRequest(
    string ConfigurationPath, string MachineName, string BinaryPath, bool Remove);

/// The plan, and everything needed to explain or carry it out. Nothing has changed yet.
public sealed record DeploymentOutcome(
    ExitCode Code,
    DeploymentPlan? Plan,
    DesiredDeployment? Desired,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage);

/// Works out what deploying the listener on this host would change. Deciding is separate from
/// doing on purpose: `--dry-run` runs exactly this and stops.
public sealed class ListenerDeployment(IConfigStore configStore, IDeploymentExecutor executor)
{
    public DeploymentOutcome Plan(DeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ConfigurationRead read = configStore.Read(request.ConfigurationPath);

        if (read.Errors.Count > 0)
        {
            return Invalid(read.Errors);
        }

        ConfigurationValidation validation =
            ConfigurationValidator.Validate(read.Document, request.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return Invalid(validation.Errors);
        }

        if (DesiredDeploymentFactory.From(configuration, request.BinaryPath) is not { } desired)
        {
            return new DeploymentOutcome(
                ExitCode.InvalidConfiguration,
                null,
                null,
                [new ConfigurationError("listener.enabled", "the listener is disabled on this node")],
                null);
        }

        try
        {
            ObservedDeployment observed = executor.Observe(desired);

            return new DeploymentOutcome(
                ExitCode.Success,
                request.Remove ? DeploymentPlan.ToRemove(observed) : DeploymentPlan.For(desired, observed),
                desired,
                [],
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DeploymentOutcome(
                ExitCode.LocalAccessFailure, null, desired, [], exception.Message);
        }
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
}
