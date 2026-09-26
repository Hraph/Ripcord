using Ripcord.Domain.Deployment;

namespace Ripcord.Ports.Deployment;

/// Looks at the host, and carries out one step at a time. It reports and acts; it never
/// decides what needs doing — that is DeploymentPlan, which runs anywhere.
public interface IDeploymentExecutor
{
    /// Around the listener as the caller already read it, so the plan and the report agree on
    /// its state. The publisher and every grant are read here.
    ObservedDeployment Observe(DesiredDeployment desired, ObservedService service);

    /// One service alone, which needs no configuration: `ripcord service` still shows it when
    /// `ripcord.yaml` does not load, the likeliest reason a service stopped.
    ObservedService ObserveService(RipcordService which);

    void Apply(DeploymentStep change, DesiredDeployment desired);
}
