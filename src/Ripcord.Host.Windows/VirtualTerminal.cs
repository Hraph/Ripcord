using System.Runtime.InteropServices;

namespace Ripcord.Host.Windows;

/// Asks the console whether it will interpret escape sequences, and switches that on if it
/// will.
///
/// Windows does not do this by default. `ENABLE_VIRTUAL_TERMINAL_PROCESSING` has to be set on
/// the output handle, and on a console that does not support it `SetConsoleMode` fails — which
/// is the answer wanted, rather than a guess. The alternative is printing `[31m` across a
/// KVM in the middle of an incident, which is worse than no colour at all.
///
/// This is in the composition root because it is the only part of Ripcord allowed to ask the
/// operating system anything. Everything downstream is handed a `Palette` and never learns
/// where it came from.
internal static class VirtualTerminal
{
    private const int StdOutputHandle = -11;

    private const uint EnableVirtualTerminalProcessing = 0x0004;

    public static bool TryEnable()
    {
        try
        {
            nint handle = NativeMethods.GetStdHandle(StdOutputHandle);

            if (handle == 0 || handle == -1)
            {
                return false;
            }

            if (!NativeMethods.GetConsoleMode(handle, out uint mode))
            {
                // Not a console at all: a pipe, a file, or a service with no window.
                return false;
            }

            return (mode & EnableVirtualTerminalProcessing) != 0
                || NativeMethods.SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// `DllImport` rather than `LibraryImport`: the generated marshalling for the latter needs
    /// `AllowUnsafeBlocks` across the whole project, which is a large permission to grant for
    /// three calls that pass an integer and a handle.
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GetStdHandle(int handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetConsoleMode(nint handle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetConsoleMode(nint handle, uint mode);
    }
}
