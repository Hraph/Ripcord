using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports;

namespace Ripcord.Application.Checks;

public sealed record CheckRequestOptions(string ConfigurationPath, string MachineName);

/// Everything `ripcord check` produced. The exit code comes from the report, which gets it
/// from the Domain: "is this infrastructure ready" is a decision, and decisions do not live
/// in a use case any more than they live in a renderer.
public sealed record CheckOutcome(
    ExitCode Code,
    CheckReport? Report,
    RipcordConfiguration? Configuration,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage);

/// Reads the pair, then judges it. The same read as `status` — `check` republishes this
/// host's snapshot on the way through, so a host that is only ever checked on a schedule
/// still keeps the other side's pair view fresh.
public sealed class CheckQuery(IConfigStore configStore, PairReader pairReader, IClock clock)
{
    public async Task<CheckOutcome> ExecuteAsync(
        CheckRequestOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore, options.ConfigurationPath, options.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return new CheckOutcome(
                ExitCode.InvalidConfiguration, null, null, validation.Errors, null);
        }

        PairRead read = await pairReader
            .ReadAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        if (read.View is not { } view)
        {
            return new CheckOutcome(
                ExitCode.LocalAccessFailure, null, configuration, [], read.FailureMessage);
        }

        CheckReport report = CheckEngine.Evaluate(
            new Domain.Checks.CheckRequest(view, configuration, clock.UtcNow, read.Notes));

        return new CheckOutcome(report.Code, report, configuration, [], null);
    }
}
