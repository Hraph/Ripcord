using Ripcord.Domain.Deployment;

namespace Ripcord.Ports.Deployment;

/// Looks at the host, and carries out one step at a time. It reports and acts; it never
/// decides what needs doing — that is DeploymentPlan, which runs anywhere.
public interface IDeploymentExecutor
{
    ObservedDeployment Observe(DesiredDeployment desired);

    void Apply(DeploymentStep change, DesiredDeployment desired);
}
