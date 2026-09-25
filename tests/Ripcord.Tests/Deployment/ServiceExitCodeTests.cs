using Ripcord.Domain;
using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// Ripcord's exit code as Windows records it for the listener service, and back.
public class ServiceExitCodeTests
{
    [Fact]
    public void Success_is_zero() =>
        Assert.Equal(0, ServiceExitCode.ToWindows(ExitCode.Success));

    /// Never the bare code: 2 is also "file not found", which is what the service manager
    /// reports for a missing binary.
    [Fact]
    public void A_failure_carries_the_customer_bit() =>
        Assert.Equal(0x20000003, ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure));

    public static TheoryData<ExitCode> Failures =>
        new(Enum.GetValues<ExitCode>().Where(code => code != ExitCode.Success));

    [Theory]
    [MemberData(nameof(Failures))]
    public void Every_failure_reads_back_as_itself(ExitCode code)
    {
        ServiceExit exit = ServiceExitCode.FromWindows(ServiceExitCode.ToWindows(code));

        Assert.Equal(ServiceExitKind.Ripcord, exit.Kind);
        Assert.Equal(code, exit.Code);
    }

    [Fact]
    public void Zero_is_a_clean_stop() =>
        Assert.Equal(ServiceExitKind.Clean, ServiceExitCode.FromWindows(0).Kind);

    [Fact]
    public void Process_aborted_is_a_crash() =>
        Assert.Equal(ServiceExitKind.Crashed, ServiceExitCode.FromWindows(1067).Kind);

    [Theory]
    [InlineData(2)]
    [InlineData(1053)]
    [InlineData(0x20000000)]
    [InlineData(0x20000063)]
    public void A_code_ripcord_did_not_set_is_not_read_as_ripcords(int win32ExitCode)
    {
        ServiceExit exit = ServiceExitCode.FromWindows(win32ExitCode);

        Assert.Equal(ServiceExitKind.Other, exit.Kind);
        Assert.Null(exit.Code);
    }

    [Fact]
    public void A_service_specific_error_carries_ripcords_code()
    {
        ServiceExit exit = ServiceExitCode.FromWindows(1066, 2);

        Assert.Equal(ServiceExitKind.Ripcord, exit.Kind);
        Assert.Equal(ExitCode.InvalidConfiguration, exit.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void A_service_specific_code_ripcord_does_not_have_is_not_read_as_ripcords(int specific) =>
        Assert.Equal(ServiceExitKind.Other, ServiceExitCode.FromWindows(1066, specific).Kind);
}
