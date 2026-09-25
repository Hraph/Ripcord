using Ripcord.Domain.Deployment;
using Ripcord.Ports.Deployment;

namespace Ripcord.Application.Deployment;

public static class ServiceReading
{
    /// Never throws: a service Windows would not describe is "cannot tell", with the reason.
    public static ObservedService Read(IDeploymentExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        try
        {
            return executor.ObserveService();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ObservedService(
                true, null, ServiceRunState.Unknown, null, null, null, exception.Message);
        }
    }
}
