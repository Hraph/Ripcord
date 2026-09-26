using Ripcord.Domain.Deployment;
using Ripcord.Ports.Deployment;

namespace Ripcord.Application.Deployment;

public static class ServiceReading
{
    /// Never throws: a service Windows would not describe is "cannot tell", with the reason.
    public static ObservedService Read(IDeploymentExecutor executor, RipcordService? service = null)
    {
        ArgumentNullException.ThrowIfNull(executor);

        try
        {
            return executor.ObserveService(service ?? RipcordService.Listener);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ObservedService(
                true, null, ServiceRunState.Unknown, null, null, null, exception.Message);
        }
    }
}
