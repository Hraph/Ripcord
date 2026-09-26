using Ripcord.Adapters.Diagnostics;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Adapters;

public class RepeatLimitedDiagnosticLogTests
{
    [Fact]
    public void Repeats_are_held_back_and_destinations_pass_through()
    {
        RecordingDiagnosticLog inner = new();
        RepeatLimitedDiagnosticLog log = new(
            inner, new FixedClock(new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero)), TimeSpan.FromHours(1));
        DiagnosticEntry failure = DiagnosticEntry.Of("hyper-v", "the local Hyper-V state could not be read");

        log.Write(failure);
        log.Write(failure);
        log.Write(failure);

        Assert.Equal([failure], inner.Written);

        DiagnosticDestination destination = DiagnosticDestination.Default(@"C:\logs");
        log.SendTo(destination);
        Assert.Equal(destination, inner.Destination);
    }
}
