using Ripcord.Application.Checks;
using Ripcord.Domain;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;
using Ripcord.Ports;
using Ripcord.Ports.Audit;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;

namespace Ripcord.Application.Failover;

public sealed record FailoverCommand(
    string ConfigurationPath,
    string MachineName,
    string VmName,
    bool DryRun,
    string User,
    BuildIdentity LocalBuild);

public sealed record FailoverOutcome(
    ExitCode Code,
    FailoverRunReport? Report,
    FailoverPlan? Plan,
    RipcordConfiguration? Configuration,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage,
    FailoverRefusal? Refusal = null);

/// Reads the pair, judges it, and then drives this host's half of a planned failover.
///
/// `check` is the precondition, so it is **run** rather than reimplemented — one place builds a
/// `CheckReport` and one place decides what it means. Milestone 3 established the shape and
/// this follows it rather than growing a third copy of the preamble.
///
/// The refusals that live here are the ones that mean the command was asked of the wrong host
/// or at the wrong time. They are not findings, and the rule engine has no business producing
/// them.
public sealed class FailoverQuery(
    IConfigStore configStore,
    PairReader pairReader,
    IHypervProvider provider,
    IAuditLog audit,
    IClock clock,
    FailoverTiming timing)
{
    public async Task<FailoverOutcome> ExecuteAsync(
        FailoverCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        CheckOutcome check;

        try
        {
            check = await new CheckQuery(configStore, pairReader, clock)
                .ExecuteAsync(
                    new CheckRequestOptions(command.ConfigurationPath, command.MachineName),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Everything so far was a read, so "nothing changed" is the honest code. The CLI's
            // generic handler reports a local access failure, which would tell a scheduler to
            // investigate a run that did nothing at all.
            return Failed(ExitCode.Refused, "interrupted before anything was read.");
        }

        if (check.Report is not { } report
            || check.Configuration is not { } configuration
            || check.View is not { } view)
        {
            return new FailoverOutcome(
                check.Code, null, null, check.Configuration, check.Errors, check.FailureMessage);
        }

        if (!configuration.Vms.Any(vm =>
            string.Equals(vm.Name, command.VmName, StringComparison.OrdinalIgnoreCase)))
        {
            return new FailoverOutcome(
                ExitCode.InvalidConfiguration, null, null, configuration,
                [new ConfigurationError(
                    "--vm", $"'{command.VmName}' is not a VM in this configuration")],
                null);
        }

        // Before anything else about this VM. The sequences are encoded in the binary and span
        // two hosts, so a pair running two versions would execute half a sequence written by
        // each — and the plan this run would print is itself the wrong plan. It blocks the dry
        // run too, deliberately: a plan produced by a binary that will not be executing the
        // other half is worse than no plan, because it reads as one.
        VersionSkew skew = VersionSkew.Between(command.LocalBuild, view.PeerBuild);

        if (skew.Verdict != VersionSkewVerdict.Same)
        {
            return new FailoverOutcome(
                ExitCode.Refused, null, null, configuration, [],
                $"the two hosts are not running the same Ripcord: {skew.Explanation}");
        }

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(
                report, FailoverOperation.PlannedFailover, command.VmName);

        if (refusal.Refuses)
        {
            return new FailoverOutcome(
                ExitCode.Refused, null, PlanFor(report, command.VmName), configuration, [],
                "the preconditions for a planned failover are not met", refusal);
        }

        FailoverPlan plan = PlanFor(report, command.VmName);

        VmReplicationState? onSource = Find(SourceOf(view, report), command.VmName);
        VmReplicationState? onTarget = Find(TargetOf(view, report), command.VmName);

        FailoverRunReport run = await new PlannedFailoverSequence(
                provider, audit, clock, timing)
            .RunAsync(
                plan,
                FailoverProgress.Of(plan, onSource, onTarget),
                SplitBrain.Of(onSource, onTarget),
                new FailoverRequest(
                    command.VmName,
                    command.MachineName,
                    configuration.Replication.ExpectedSwitchName,
                    command.DryRun,
                    command.User,
                    Unverified(refusal),
                    command.LocalBuild),
                cancellationToken)
            .ConfigureAwait(false);

        // The other host reads this one only through the snapshot, so a sequence that acted
        // and did not republish leaves the peer looking at the state from before it ran.
        // Deliberately after the run and regardless of its outcome: a failed run is when an
        // accurate view of this host matters most.
        await RepublishAsync(pairReader, configuration, cancellationToken).ConfigureAwait(false);

        return new FailoverOutcome(
            run.Code, run, plan, configuration, [], null, refusal);
    }

    /// The plan is built from the report's own idea of which host is which, so the sequence and
    /// the rules cannot disagree about the direction of the pair.
    private static FailoverPlan PlanFor(CheckReport report, string vmName) =>
        FailoverPlan.Planned(vmName, report.SourceHostName, report.TargetHostName);

    /// "Source" means the host that normally holds the primary copies, which is not necessarily
    /// the local one — `CheckSubject` already settled that from `replication.expected_role`, and
    /// re-deriving it here would be a second answer to a question with one.
    private static HostState SourceOf(PairView view, CheckReport report) =>
        report.TargetIsLocal ? view.Peer : view.Local;

    private static HostState TargetOf(PairView view, CheckReport report) =>
        report.TargetIsLocal ? view.Local : view.Peer;

    private static VmReplicationState? Find(HostState host, string name) =>
        host.Vms.FirstOrDefault(vm =>
            string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase));

    /// What the run is proceeding past, in the operator's words rather than rule ids, because
    /// this is read out of the audit trail the next morning by somebody who was not here.
    private static IReadOnlyList<string> Unverified(FailoverRefusal refusal) =>
        [.. refusal.Proceeded
            .Where(finding => finding.Verdict == FindingVerdict.Unevaluable)
            .Select(finding => $"{finding.Rule.Id}: {finding.Observed}")];

    /// Publishing is never a reason to fail the command: the failover has already happened,
    /// and replacing its outcome with a snapshot-writing error would lose the part the operator
    /// needs. The consequence shows up as a stale peer in `status`, which is visible.
    private static async Task RepublishAsync(
        PairReader pairReader,
        RipcordConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await pairReader.RepublishAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
        }
    }

    private static FailoverOutcome Failed(ExitCode code, string message) =>
        new(code, null, null, null, [], message);
}
