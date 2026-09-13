namespace Ripcord.Ports;

/// Lag is the difference between two instants, so "now" has to be injectable. Nothing in
/// Ripcord calls DateTime.Now.
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// The same instant, carrying this host's offset. Quiet hours are written in local time
    /// because that is the time the operator is asleep in, and an offset read here rather
    /// than deep in the decision keeps the policy free of the machine's time zone.
    DateTimeOffset LocalNow { get; }
}
