using Ripcord.Domain.Configuration;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Domain.Deployment;

public static class DesiredDeploymentFactory
{
    /// Built for a disabled listener too: the plan refuses to install it, and removing or
    /// inspecting a service installed earlier still needs the paths.
    public static DesiredDeployment From(RipcordConfiguration configuration, string binaryPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new DesiredDeployment(
            binaryPath,
            configuration.Listener.SnapshotPath,
            configuration.Listener.Port,
            configuration.Peer.Address,
            LogFolder.Beside(binaryPath),
            configuration.Listener.Enabled,
            configuration.Peer.OfflineAfter,
            configuration.Listener.LocalCertificateThumbprint);
    }
}
