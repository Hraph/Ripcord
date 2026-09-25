using Ripcord.Domain.Deployment;
using Ripcord.Ports.Hosts;

namespace Ripcord.Application.Deployment;

/// This host's certificates, for `ripcord service` and `ripcord pair` alike.
public static class HostCertificateReading
{
    /// Never throws: a store that cannot be read is said, with its reason.
    public static HostCertificates Read(ICertificateProvider store, string machineName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return HostCertificates.For(store.WithPrivateKey(), machineName, now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HostCertificates.CouldNotRead(machineName, exception.Message);
        }
    }
}
