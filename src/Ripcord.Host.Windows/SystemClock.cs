using Ripcord.Ports;

namespace Ripcord.Host.Windows;

/// The only place in Ripcord that reads the wall clock.
internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}
