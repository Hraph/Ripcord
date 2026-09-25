using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Diagnostics;
using Ripcord.Ports.Hosts;
using Ripcord.Ports.Replication;

namespace Ripcord.Application;

/// The local host, read once and used by both `status` and `check`. `status` publishes it so
/// the peer can see it (decision D18) and `check` reasons over it, so it is assembled in one
/// place rather than twice.
///
/// Notes are degradations, not failures: a host that cannot read its own free space still
/// knows everything about its VMs, and the rules that needed the missing facts say so.
public sealed record LocalRead(
    HostState? State, string? FailureMessage, IReadOnlyList<string> Notes)
{
    public static LocalRead Failed(string message) => new(null, message, []);
}

public sealed class LocalStateReader(
    IHypervProvider provider,
    IHostSystemProvider hostSystem,
    ICertificateProvider certificates,
    IDiagnosticLog diagnostics)
{
    public async Task<LocalRead> ReadAsync(
        RipcordConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HostState state;

        try
        {
            state = await provider.GetLocalStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The console gets the sentence, the log gets the exception. `exception.Message`
            // on a CIM failure is one translated clause — "type mismatch for parameter X" —
            // with no class, no method and no stack, and on a host with no debugger that is
            // the whole of what anybody would have to work from.
            diagnostics.Write(DiagnosticEntry.Of(
                "hyper-v", "the local Hyper-V state could not be read", exception.ToString()));

            return LocalRead.Failed($"cannot read the local Hyper-V state: {exception.Message}");
        }

        List<string> notes = [];

        HostSystemReading reading = await this
            .ReadHostSystemAsync(notes, cancellationToken)
            .ConfigureAwait(false);

        CertificateFact? certificate = this.FindCertificate(configuration, notes);

        return new LocalRead(
            state with
            {
                Facts = new HostFacts(reading.PhysicalRamMb, reading.Volumes, certificate),
            },
            null,
            notes);
    }

    private async Task<HostSystemReading> ReadHostSystemAsync(
        List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            return await hostSystem.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Write(DiagnosticEntry.Of(
                "host-system", "the host system could not be read", exception.ToString()));

            notes.Add($"the host system could not be read: {exception.Message}");
            return HostSystemReading.Unknown();
        }
    }

    /// With the listener off there is no configured thumbprint, so the store is never opened.
    /// A thumbprint that matches nothing is not a note: it is the certificate rule's finding.
    private CertificateFact? FindCertificate(
        RipcordConfiguration configuration, List<string> notes)
    {
        if (configuration.Listener is not { Enabled: true, LocalCertificateThumbprint: { } thumbprint })
        {
            return null;
        }

        try
        {
            return certificates.Find(thumbprint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Write(DiagnosticEntry.Of(
                "certificates",
                "the certificate store could not be read",
                exception.ToString()));

            notes.Add($"the certificate store could not be read: {exception.Message}");
            return null;
        }
    }
}
