namespace Ripcord.Host.Windows;

/// Composition root. The only place that knows about the WMI adapters, and the only
/// project that produces ripcord.exe. Wiring arrives with milestone 1.
internal static class Program
{
    private static int Main(string[] args) => 0;
}
