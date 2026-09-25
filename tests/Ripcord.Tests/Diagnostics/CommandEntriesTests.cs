using Ripcord.Domain;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// The lines a command ends on are written and read back by the same code.
public class CommandEntriesTests
{
    public static TheoryData<ExitCode> Codes => new(Enum.GetValues<ExitCode>());

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_exit_reads_back_as_itself(ExitCode code) =>
        Assert.Equal(code, CommandEntries.ExitIn(CommandEntries.Exited("serve", code).Message));

    [Theory]
    [InlineData("exit 2: Success")]
    [InlineData("exit 9: Nine")]
    [InlineData("exit -1: Success")]
    [InlineData("exited 0: Success")]
    public void Anything_else_is_not_an_exit(string message) =>
        Assert.Null(CommandEntries.ExitIn(message));

    [Fact]
    public void The_written_line_is_unchanged() =>
        Assert.Equal(
            "exit 2: InvalidConfiguration",
            CommandEntries.Exited("serve", ExitCode.InvalidConfiguration).Message);
}
