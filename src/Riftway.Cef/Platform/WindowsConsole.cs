using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>Electron's console routing on Windows (<c>electron_main_win.cc</c>): a GUI-subsystem app started from a
/// console writes to that console, as <c>base::RouteStdioToConsole(false)</c> does, unless
/// <c>ELECTRON_NO_ATTACH_CONSOLE</c> is set.</summary>
public static unsafe class WindowsConsole
{
    private const int StdOutputHandle = -11, StdErrorHandle = -12;
    private const uint AttachParentProcess = unchecked((uint)-1);
    private const uint FileTypeUnknown = 0;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareWrite = 0x2, OpenExisting = 3;

    /// <summary>Whether to attach: Electron routes unless the variable is set (to anything, empty included), and
    /// Chromium's <c>RouteStdioToConsole</c> leaves a process alone whose stdout or stderr is already a valid stream.
    /// <c>RIFTWAY_NO_ATTACH_CONSOLE</c> stands in for Electron's name.</summary>
    internal static bool ShouldAttach(bool stdoutValid, bool stderrValid, Func<string, string?> environment)
        => environment("ELECTRON_NO_ATTACH_CONSOLE") is null && environment("RIFTWAY_NO_ATTACH_CONSOLE") is null
            && !stdoutValid && !stderrValid;

    /// <summary>Attaches to the parent process's console and points stdout and stderr (the process's handles and .NET's
    /// <see cref="Console"/>) at it, when <see cref="ShouldAttach"/> says so. Returns whether it attached.</summary>
    [SupportedOSPlatform("windows")]
    public static bool RouteStdioToConsole()
    {
        if (!ShouldAttach(IsValid(GetStdHandle(StdOutputHandle)), IsValid(GetStdHandle(StdErrorHandle)), Environment.GetEnvironmentVariable)) return false;
        // Chromium tells already attached (ERROR_ACCESS_DENIED) and a parent gone (ERROR_GEN_FAILURE) from no console
        // at all, but with create_console_if_not_found false, as Electron passes, every failure ends here alike.
        if (AttachConsole(AttachParentProcess) == 0) return false;
        // Chromium reopens stdout and stderr on CONOUT$, line-buffered.
        nint output;
        fixed (char* name = "CONOUT$") output = CreateFileW(name, GenericRead | GenericWrite, FileShareWrite, null, OpenExisting, 0, 0);
        if (output == -1 || output == 0) return false;
        _ = SetStdHandle(StdOutputHandle, output);
        _ = SetStdHandle(StdErrorHandle, output);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        return true;
    }

    // Chromium checks the CRT's descriptors, which cannot dangle; a raw handle can, so it must also name a real file.
    private static bool IsValid(nint handle) => handle != 0 && handle != -1 && GetFileType(handle) != FileTypeUnknown;

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll")]
    private static extern int SetStdHandle(int stdHandle, nint handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll")]
    private static extern int AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint CreateFileW(char* fileName, uint access, uint share, void* security, uint creation, uint flags, nint template);
}
