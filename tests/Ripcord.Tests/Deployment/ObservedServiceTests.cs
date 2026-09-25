using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// The Windows service as reported, and the two things read out of its raw values.
public class ObservedServiceTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Ripcord\\ripcord.exe\" serve", @"C:\Program Files\Ripcord\ripcord.exe")]
    [InlineData(@"C:\Ripcord\ripcord.exe serve", @"C:\Ripcord\ripcord.exe")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Command_line_binary_is_parsed_quoted_and_unquoted(string? commandLine, string? binary) =>
        Assert.Equal(binary, ObservedService.BinaryIn(commandLine));

    [Theory]
    [InlineData("Running", ServiceRunState.Running)]
    [InlineData("Stopped", ServiceRunState.Stopped)]
    [InlineData("Start Pending", ServiceRunState.StartPending)]
    [InlineData("Stop Pending", ServiceRunState.StopPending)]
    [InlineData("Paused", ServiceRunState.Paused)]
    [InlineData("Halted", ServiceRunState.Unknown)]
    [InlineData(null, ServiceRunState.Unknown)]
    public void Unrecognised_state_is_unknown_never_stopped(string? state, ServiceRunState expected) =>
        Assert.Equal(expected, ObservedService.ParseState(state));

    [Fact]
    public void Disabled_is_read_whatever_the_case() =>
        Assert.True((ObservedService.Absent with { StartMode = "disabled" }).IsDisabled);
}
