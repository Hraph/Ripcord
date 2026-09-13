using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Failover;
using Ripcord.Ports;
using Ripcord.Ports.Audit;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;

namespace Ripcord.Application.Failover;

/// One invocation of `failover`, whether it names a VM or sweeps several.
public sealed record SweepCommand(
    string ConfigurationPath,
    string MachineName,
    FailoverOperation Scenario,
    SweepScope Scope,
    bool DryRun,
    string User,
    BuildIdentity LocalBuild);

/// What one VM's turn came to, in the order the sweep took them.
public sealed record SweptVm(string VmName, FailoverOutcome Outcome);

public sealed record SweepReport(
    ExitCode Code,
    IReadOnlyList<SweptVm> Ran,
    IReadOnlyList<SweepExclusion> Excluded,
    IReadOnlyList<string> NotAttempted,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage);

/// Runs the failover for each VM the scope selects, one after another, in priority order.
///
/// A single `--vm` goes through here too rather than round it. One path means the exclusion
/// rules, the ordering and the reporting cannot drift between "one VM" and "three", and the
/// `never` policy cannot be bypassed by naming a machine.
///
/// **It stops at the first VM that does not succeed.** The alternative — carry on down the
/// list — was rejected: the failures that arrive during a sweep are mostly shared, not
/// per-VM (the target is out of memory, the volume is full), so continuing compounds one
/// problem into three. Stopping is recoverable: what was not attempted is named, and the
/// operator re-runs naming what is left.
public sealed class FailoverSweepQuery(
    IConfigStore configStore,
    PairReader pairReader,
    IHypervProvider provider,
    IAuditLog audit,
    IClock clock,
    FailoverTiming timing)
{
    public async Task<SweepReport> ExecuteAsync(
        SweepCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore, command.ConfigurationPath, command.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return new SweepReport(
                ExitCode.InvalidConfiguration, [], [], [], validation.Errors, null);
        }

        SweepSelection selection = FailoverSweep.Select(configuration.Vms, command.Scope);

        // A typo, or a VM the configuration says must not be moved. Either way the operator
        // asked for something other than what would happen, so nothing happens.
        if (selection.Refuses)
        {
            return new SweepReport(
                ExitCode.InvalidConfiguration,
                [],
                selection.Excluded,
                selection.VmNames,
                [.. selection.Refusals.Select(reason => new ConfigurationError("--vm", reason))],
                null);
        }

        List<SweptVm> ran = [];

        for (int index = 0; index < selection.VmNames.Count; index++)
        {
            string vmName = selection.VmNames[index];

            FailoverOutcome outcome = await new FailoverQuery(
                    configStore, pairReader, provider, audit, clock, timing)
                .ExecuteAsync(
                    new FailoverCommand(
                        command.ConfigurationPath,
                        command.MachineName,
                        vmName,
                        command.Scenario,
                        command.DryRun,
                        command.User,
                        command.LocalBuild),
                    cancellationToken)
                .ConfigureAwait(false);

            ran.Add(new SweptVm(vmName, outcome));

            if (outcome.Code != ExitCode.Success)
            {
                return new SweepReport(
                    outcome.Code,
                    ran,
                    selection.Excluded,
                    [.. selection.VmNames.Skip(index + 1)],
                    [],
                    outcome.FailureMessage);
            }
        }

        return new SweepReport(ExitCode.Success, ran, selection.Excluded, [], [], null);
    }
}
