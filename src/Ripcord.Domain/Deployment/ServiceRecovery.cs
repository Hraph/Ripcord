namespace Ripcord.Domain.Deployment;

/// Windows restarts a Ripcord service whose process died, a minute later, three times a day.
///
/// Only on a crash: `failureflag` stays off, so a service that stops itself with an exit code —
/// a configuration it refuses — is not started again every minute to refuse it again.
public static class ServiceRecovery
{
    public const int DelayMs = 60_000;

    private const int ResetSeconds = 86_400;

    /// SC_ACTION_RESTART.
    private const int Restart = 1;

    public static string Arguments(RipcordService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return $"failure {service.Name} reset= {ResetSeconds} "
            + $"actions= restart/{DelayMs}/restart/{DelayMs}/restart/{DelayMs}";
    }

    /// The service key's `FailureActions` value, a SERVICE_FAILURE_ACTIONS laid out as five
    /// DWORDs (reset, two unused pointers, action count, unused pointer) then the actions as
    /// (type, delay) pairs. Configured when the first action restarts; null or short is not.
    public static bool Restarts(byte[]? failureActions)
    {
        if (failureActions is not { Length: >= 28 } value)
        {
            return false;
        }

        int count = BitConverter.ToInt32(value, 12);

        return count >= 1 && BitConverter.ToInt32(value, 20) == Restart;
    }
}
