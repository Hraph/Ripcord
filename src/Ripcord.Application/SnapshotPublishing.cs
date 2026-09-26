using Ripcord.Domain.Configuration;
using Ripcord.Domain.Diagnostics;
using Ripcord.Domain.Pairing;
using Ripcord.Ports;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Application;

/// The publishing service's loop: this host's snapshot every `interval`, until the service is
/// stopped. What it writes about itself is `PublishJournal`'s to decide.
public sealed class SnapshotPublishing(
    PairReader pairReader, IClock clock, IDiagnosticLog diagnostics, TimeSpan interval)
{
    public async Task RunAsync(RipcordConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        PublishJournal journal = PublishJournal.Start;

        try
        {
            while (true)
            {
                DateTimeOffset started = clock.UtcNow;

                Publication publication = await pairReader
                    .PublishAsync(configuration, cancellationToken)
                    .ConfigureAwait(false);

                // Nothing would be served: stopping says so, where running empty would not.
                if (publication.Kind == PublicationKind.ListenerDisabled)
                {
                    diagnostics.Write(DiagnosticEntry.Of(
                        PublishJournal.Operation, "the listener is disabled: nothing to publish"));
                    return;
                }

                (journal, IReadOnlyList<DiagnosticEntry> lines) = journal.After(publication, clock.UtcNow);

                foreach (DiagnosticEntry line in lines)
                {
                    diagnostics.Write(line);
                }

                TimeSpan wait = interval - (clock.UtcNow - started);

                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (DiagnosticEntry line in journal.Stopping())
            {
                diagnostics.Write(line);
            }
        }
    }
}
