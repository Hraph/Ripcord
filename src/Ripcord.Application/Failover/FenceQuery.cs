using Ripcord.Application.Checks;
using Ripcord.Domain;
using Ripcord.Domain.Audit;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;
using Ripcord.Ports;
using Ripcord.Ports.Audit;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;

namespace Ripcord.Application.Failover;

public sealed record FenceCommand(
    string ConfigurationPath,
    string MachineName,
    bool DryRun,
    string User,
    BuildIdentity LocalBuild);

/// One VM's fence, as it turned out.
public sealed record FencedVm(string VmName, AutomaticStartAction Previous, string? Failure)
{
    public bool Succeeded => this.Failure is null;
}

public sealed record FenceOutcome(
    ExitCode Code,
    FencePlan? Plan,
    IReadOnlyList<FencedVm> Fenced,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage,

    /// Said out loud when the peer could not be read and the whole configured set was fenced
    /// rather than the failed-over half of it.
    string? Scope = null);

/// `ripcord fence` — the first thing to run on the original primary after it comes back from an
/// unplanned failover, and before anything else is done to the pair.
///
/// Both hosts sit on the same external switch on the same subnet. With the common
/// `StartIfRunning` default, restoring power boots the *original* domain controller alongside
/// the failed-over copy — two domain controllers, same identity, same IP, both writing, and an
/// Active Directory divergence no failback sequence repairs. `RecoveryHistory 0` leaves no
/// earlier point to fall back to either.
///
/// The prior settings are recorded in the audit trail before anything is changed, because
/// `reprotect` has to put them back and a host that comes home with every VM set to never start
/// is a second outage waiting for the next reboot.
public sealed class FenceQuery(
    IConfigStore configStore,
    PairReader pairReader,
    IHypervProvider provider,
    IAuditLog audit,
    IClock clock)
{
    private const string Operation = "fence";

    public async Task<FenceOutcome> ExecuteAsync(
        FenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore, command.ConfigurationPath, command.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return new FenceOutcome(
                ExitCode.InvalidConfiguration, null, [], validation.Errors, null);
        }

        PairRead read = await pairReader
            .ReadAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        if (read.View is not { } view)
        {
            return new FenceOutcome(
                ExitCode.LocalAccessFailure, null, [], [], read.FailureMessage);
        }

        // Which VMs moved is read off the host that took them. When it cannot be read, the
        // whole configured set is fenced instead — the asymmetry decides it: fencing a VM that
        // never moved costs somebody a manual start, and the value to put back is recorded;
        // not fencing one that did costs an Active Directory divergence nothing repairs.
        bool peerReadable = view.Peer.IsReachable && view.Peer.Vms.Count > 0;

        IReadOnlyList<string> failedOver = peerReadable
            ? Fencing.FailedOver(view.Peer.Vms)
            : [.. configuration.Vms.Select(vm => vm.Name)];

        FencePlan plan = Fencing.Plan(view.Local.Vms, failedOver);

        string? scope = peerReadable
            ? null
            : $"{configuration.Peer.Hostname} could not be read, so every VM in this "
                + "configuration is fenced rather than only the ones that moved";

        if (plan.Halt is not null)
        {
            return new FenceOutcome(
                ExitCode.Refused, plan, [], [], plan.Halt, scope);
        }

        if (command.DryRun || !plan.HasWork)
        {
            return new FenceOutcome(ExitCode.Success, plan, [], [], null, scope);
        }

        return await this.ApplyAsync(plan, command, scope, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<FenceOutcome> ApplyAsync(
        FencePlan plan,
        FenceCommand command,
        string? scope,
        CancellationToken cancellationToken)
    {
        // Before the first change, never after: the prior settings are the whole reason this
        // is recoverable, and a host that cannot write them down must not change them. The
        // exception is deliberately not caught.
        audit.Append(this.Entry(
            command,
            AuditStage.Starting,
            "about to set the startup action to Nothing for "
                + string.Join(", ", plan.ToFence.Select(Restorable))));

        List<FencedVm> fenced = [];

        foreach (FenceAction action in plan.ToFence)
        {
            try
            {
                await provider.SetAutomaticStartActionAsync(
                        action.VmName, AutomaticStartAction.Nothing, cancellationToken)
                    .ConfigureAwait(false);

                fenced.Add(new FencedVm(action.VmName, action.Previous, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The rest are still attempted. Each VM is an independent claimant, and
                // stopping at the first failure would leave the others free to boot.
                fenced.Add(new FencedVm(action.VmName, action.Previous, exception.Message));
            }
        }

        ExitCode code = fenced.All(vm => vm.Succeeded)
            ? ExitCode.Success
            : ExitCode.IntermediateState;

        this.Record(command, fenced, code);

        return new FenceOutcome(code, plan, fenced, [], null, scope);
    }

    private void Record(FenceCommand command, IReadOnlyList<FencedVm> fenced, ExitCode code)
    {
        try
        {
            audit.Append(this.Entry(
                command,
                AuditStage.Finished,
                $"exit {(int)code}: fenced "
                    + string.Join(
                        ", ", fenced.Where(vm => vm.Succeeded).Select(vm => vm.VmName))));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The starting entry already carries the prior settings, which is the part
            // `reprotect` needs. Replacing a real outcome with a logging error would lose the
            // part the operator needs.
            _ = exception;
        }
    }

    /// The prior setting in the operator's words, so the trail is readable by somebody
    /// restoring it by hand months later.
    private static string Restorable(FenceAction action) =>
        action.PreviousIsKnown
            ? $"{action.VmName} (was {action.Previous})"
            : $"{action.VmName} (previous setting could not be read — restore it by hand)";

    private AuditEntry Entry(FenceCommand command, AuditStage stage, string detail) =>
        new(
            clock.UtcNow,
            command.User,
            command.MachineName,
            Operation,

            // Host-wide rather than about one VM: the fence is a property of this host coming
            // back, and the VMs it touched are named in the detail.
            command.MachineName,
            stage,
            detail,
            [],
            command.LocalBuild.ToString());
}
