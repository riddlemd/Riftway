using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The client half of Chromium's Windows ProcessSingleton (<c>chrome_process_finder.cc</c>
/// <c>AttemptToNotifyRunningChrome</c>): hands this launch to the instance that holds the user data directory.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsProcessSingleton
{
    private const uint WmCopyData = 0x004A, SmtoAbortIfHung = 0x0002;
    private static readonly nint s_messageParent = -3; // HWND_MESSAGE

    /// <summary>Sends "START\0<paramref name="currentDirectory"/>\0<paramref name="commandLine"/>\0" to the running
    /// instance's message window; true when it took the launch.</summary>
    public static bool Notify(string userDataDirectory, string currentDirectory, string commandLine)
    {
        // base::win::MessageWindow names the singleton's window after the user data directory.
        nint window;
        fixed (char* className = "Chrome_MessageWindow")
        fixed (char* windowName = userDataDirectory)
            window = FindWindowExW(s_messageParent, 0, className, windowName);
        if (window == 0) return false;
        uint processId;
        if (GetWindowThreadProcessId(window, &processId) == 0 || processId == 0) return false;
        var message = "START\0" + currentDirectory + "\0" + commandLine + "\0";
        // Lets the running instance bring its window forward rather than only flash it.
        _ = AllowSetForegroundWindow(processId);
        fixed (char* text = message)
        {
            var data = new CopyDataStruct { Data = 0, Size = (uint)((message.Length + 1) * sizeof(char)), Pointer = text };
            nint result = 0;
            return SendMessageTimeoutW(window, WmCopyData, 0, (nint)(&data), SmtoAbortIfHung, 20_000, &result) != 0 && result != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nint Data;
        public uint Size;
        public void* Pointer;
    }

    [DllImport("user32.dll")] private static extern nint FindWindowExW(nint parent, nint after, char* className, char* windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, uint* processId);
    [DllImport("user32.dll")] private static extern int AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, nint* result);
}
