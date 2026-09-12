using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Deployment;

public static class DesiredDeploymentFactory
{
    /// Null when the node has no listener: there is nothing to deploy, and a deploy command
    /// that installs a service the configuration disabled would be worse than useless.
    public static DesiredDeployment? From(RipcordConfiguration configuration, string binaryPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.Listener.Enabled
            ? new DesiredDeployment(
                binaryPath,
                configuration.Listener.SnapshotPath,
                configuration.Listener.Port,
                configuration.Peer.Address)
            : null;
    }
}
