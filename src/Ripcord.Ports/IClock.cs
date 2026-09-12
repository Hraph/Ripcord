namespace Ripcord.Ports;

/// Lag is the difference between two instants, so "now" has to be injectable. Nothing in
/// Ripcord calls DateTime.Now.
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
