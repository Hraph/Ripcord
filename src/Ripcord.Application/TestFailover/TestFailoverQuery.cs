using Ripcord.Application.Checks;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

namespace Ripcord.Application.TestFailover;

public sealed record TestFailoverRequest(
    string ConfigurationPath,
    string MachineName,
    IReadOnlyList<string> VmNames,
    bool All,
    bool DryRun);

public sealed record TestFailoverOutcome(
    ExitCode Code,
    TestFailoverReport? Report,
    RipcordConfiguration? Configuration,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage);

/// Reads the pair, judges it, then drives the host. The same preamble as `status` and
/// `check` — `ConfigurationGate` then `PairReader` — because a third copy of it would be a
/// third chance to validate against the wrong machine.
///
/// `check` is step 1 of the sequence, so this runs `CheckQuery` rather than repeating its
/// preamble — the report is built in exactly one place.
///
/// Three refusals live here rather than in the sequence, because each of them means the
/// command was asked of the wrong host or at the wrong time, and none of them is a finding
/// the rule engine has any business producing.
public sealed class TestFailoverQuery(
    IConfigStore configStore,
    PairReader pairReader,
    IHypervProvider provider,
    IClock clock,
    TestFailoverTiming timing)
{
    public async Task<TestFailoverOutcome> ExecuteAsync(
        TestFailoverRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        CheckOutcome checkOutcome;

        try
        {
            // `check` is step 1 of the sequence, so it is *run*, not reimplemented. Two places
            // building a CheckReport would be two places that have to agree on how.
            checkOutcome = await new CheckQuery(configStore, pairReader, clock)
                .ExecuteAsync(
                    new CheckRequestOptions(request.ConfigurationPath, request.MachineName),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Still read-only at this point, so the honest code is "nothing changed". The
            // CLI's generic handler would report a local access failure, which tells a
            // scheduler to investigate a run that did nothing at all.
            return new TestFailoverOutcome(
                ExitCode.Refused, null, null, [], "interrupted before anything was read.");
        }

        if (checkOutcome.Report is not { } check
            || checkOutcome.Configuration is not { } configuration)
        {
            return new TestFailoverOutcome(
                checkOutcome.Code,
                null,
                checkOutcome.Configuration,
                checkOutcome.Errors,
                checkOutcome.FailureMessage);
        }

        // A test failover happens on the host that holds the replicas. Asked of the primary
        // there is nothing to test, and the operator is on the wrong machine — which on the
        // day is exactly the mistake worth catching loudly.
        if (configuration.Replication.ExpectedRole != ExpectedRole.Replica)
        {
            return Refused(
                configuration,
                "this host normally holds the primary copies, so it has no replica to test. "
                    + $"Run this on {configuration.Peer.Hostname}.");
        }

        if (Unknown(request, configuration) is { } unknown)
        {
            return new TestFailoverOutcome(
                ExitCode.InvalidConfiguration,
                null,
                configuration,
                [new ConfigurationError("--vm", $"'{unknown}' is not a VM in this configuration")],
                null);
        }

        // Already running on the recovery side. A test copy taken now would be a copy of a
        // live production VM, and the pair has a real incident to finish first.
        if (check.Mode == OperatingMode.FailedOver)
        {
            return Refused(
                configuration,
                "the pair is failed over. Finish the failback before testing.");
        }

        TestFailoverReport report = await new TestFailoverSequence(provider, clock, timing)
            .RunAsync(
                new TestFailoverPlan(
                    check,
                    Targets(request, configuration),
                    configuration.Replication.TestFailoverSwitch,
                    configuration.Replication.TestFailoverOrphanAfter,
                    request.DryRun),
                cancellationToken)
            .ConfigureAwait(false);

        return new TestFailoverOutcome(report.Code, report, configuration, [], null);
    }

    private static IReadOnlyList<string> Targets(
        TestFailoverRequest request, RipcordConfiguration configuration) =>
        request.All
            ? [.. configuration.Vms.Select(vm => vm.Name)]
            : request.VmNames;

    /// A misspelt VM name must not silently test nothing and report success.
    private static string? Unknown(
        TestFailoverRequest request, RipcordConfiguration configuration) =>
        request.VmNames.FirstOrDefault(name =>
            !configuration.Vms.Any(vm =>
                string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase)));

    private static TestFailoverOutcome Refused(
        RipcordConfiguration configuration, string reason) =>
        new(ExitCode.Refused, null, configuration, [], reason);
}
