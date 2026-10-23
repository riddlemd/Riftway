using System.Runtime.InteropServices;

namespace Oriel.Cef.Platform;

/// <summary>Keeps the process's SIGCHLD handler across Chromium's start-up.</summary>
/// <remarks>.NET learns that a child it started has exited from its SIGCHLD handler, installed the first time the app
/// starts a process. Chromium replaces the handler when it initialises, so a process started before then would leave
/// every later <c>Process.WaitForExit()</c> waiting forever. Electron's main process keeps its own child-process
/// handling working the same way.</remarks>
public static unsafe class ChildSignal
{
    // Opaque room for the platform's struct sigaction: 16 bytes on macOS, 152 on glibc x64.
    private const int SigactionSize = 256;

    private static int Number => OperatingSystem.IsMacOS() ? 20 : 17;

    /// <summary>Records the current handler; dispose to put it back if it has been replaced since.</summary>
    /// <returns>Null where there are no POSIX signals (Windows).</returns>
    public static IDisposable? Preserve()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return null;
        var saved = new byte[SigactionSize];
        fixed (byte* p = saved)
        {
            if (sigaction(Number, null, p) != 0) return null;
        }
        return new Restorer(saved);
    }

    // The handler is the struct's first field on both platforms.
    private static nint HandlerOf(byte[] action) => MemoryMarshal.Read<nint>(action);

    [DllImport("libc")]
    private static extern int sigaction(int signal, byte* action, byte* oldAction);

    private sealed class Restorer(byte[] saved) : IDisposable
    {
        public void Dispose()
        {
            var now = new byte[SigactionSize];
            fixed (byte* current = now)
            fixed (byte* original = saved)
            {
                if (sigaction(Number, null, current) != 0) return;
                if (HandlerOf(now) == HandlerOf(saved)) return;
                sigaction(Number, original, null);
            }
        }
    }
}
