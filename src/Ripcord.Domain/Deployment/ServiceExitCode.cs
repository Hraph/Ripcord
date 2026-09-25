namespace Ripcord.Domain.Deployment;

/// How Ripcord's exit code reaches Windows when it runs as a service.
///
/// A .NET service reports its stop through `ServiceBase.ExitCode`, the Win32 exit code; the
/// service-specific code is out of reach. Ripcord's own codes 2, 3 and 5 are also Win32 codes
/// the service manager reports itself — a missing binary is 2 — so they are carried with the
/// customer bit set, which Windows reserves for applications and never uses.
public static class ServiceExitCode
{
    public const int CustomerBit = 0x20000000;

    /// ERROR_PROCESS_ABORTED: the process ended without reporting a stop.
    public const int Aborted = 1067;

    public static int ToWindows(ExitCode code) =>
        code == ExitCode.Success ? 0 : CustomerBit | (int)code;

    /// ERROR_SERVICE_SPECIFIC_ERROR: the real code is in the service-specific field.
    public const int ServiceSpecificError = 1066;

    /// `Win32_Service` carries both fields. Ripcord sets only the first, but a service stopped
    /// through the service-specific route is still read for what it says.
    public static ServiceExit FromWindows(int win32ExitCode, int? serviceSpecificExitCode)
    {
        if (win32ExitCode == ServiceSpecificError
            && serviceSpecificExitCode is { } specific and not 0
            && Enum.IsDefined((ExitCode)specific))
        {
            return new ServiceExit(ServiceExitKind.Ripcord, (ExitCode)specific, win32ExitCode);
        }

        return FromWindows(win32ExitCode);
    }

    public static ServiceExit FromWindows(int win32ExitCode)
    {
        if (win32ExitCode == 0)
        {
            return new ServiceExit(ServiceExitKind.Clean, null, 0);
        }

        if (win32ExitCode == Aborted)
        {
            return new ServiceExit(ServiceExitKind.Crashed, null, win32ExitCode);
        }

        int ripcord = win32ExitCode & ~CustomerBit;

        return (win32ExitCode & CustomerBit) != 0
            && ripcord != 0
            && Enum.IsDefined((ExitCode)ripcord)
            ? new ServiceExit(ServiceExitKind.Ripcord, (ExitCode)ripcord, win32ExitCode)
            : new ServiceExit(ServiceExitKind.Other, null, win32ExitCode);
    }
}

public enum ServiceExitKind
{
    Clean,

    /// Ripcord stopped the service itself and said why in its exit code.
    Ripcord,

    Crashed,

    /// A code Windows set, not Ripcord: the binary is missing, the account cannot log on.
    Other,
}

public sealed record ServiceExit(ServiceExitKind Kind, ExitCode? Code, int Win32ExitCode);
