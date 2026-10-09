using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Platform;

/// <summary>The process's own argc/argv, as CEF needs them to tell process types apart.</summary>
/// <remarks>.NET's string[] drops argv[0] and re-encodes the rest, so the originals are read from the OS.</remarks>
public static unsafe class ProcessArgs
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    /// <summary>The original argv on macOS.</summary>
    [SupportedOSPlatform("macos")]
    public static cef_main_args_posix_t Mac() => new() { argc = *_NSGetArgc(), argv = *_NSGetArgv() };

    /// <summary>The original argv on Linux, rebuilt from /proc/self/cmdline.</summary>
    /// <remarks>The buffers live for the process: CEF keeps pointers into argv.</remarks>
    [SupportedOSPlatform("linux")]
    public static cef_main_args_posix_t Linux() => FromCmdline(File.ReadAllBytes("/proc/self/cmdline"));

    /// <summary>argv[0] as given, which Chromium's <c>CommandLine::GetProgram()</c> returns on Linux; null if unreadable.</summary>
    [SupportedOSPlatform("linux")]
    public static string? LinuxProgram()
    {
        try
        {
            var cmdline = File.ReadAllBytes("/proc/self/cmdline");
            var end = Array.IndexOf(cmdline, (byte)0);
            return System.Text.Encoding.UTF8.GetString(cmdline, 0, end < 0 ? cmdline.Length : end);
        }
        catch (IOException) { return null; }
    }

    /// <summary>argc/argv from /proc/&lt;pid&gt;/cmdline content: NUL-terminated arguments, the last one included.</summary>
    internal static cef_main_args_posix_t FromCmdline(ReadOnlySpan<byte> cmdline)
    {
        var data = (byte*)NativeMemory.Alloc((nuint)cmdline.Length + 1);
        cmdline.CopyTo(new Span<byte>(data, cmdline.Length));
        data[cmdline.Length] = 0;

        var count = cmdline.Count((byte)0);
        var argv = (byte**)NativeMemory.Alloc((nuint)(count + 1), (nuint)sizeof(byte*));

        var start = 0;
        var index = 0;
        for (var i = 0; i < cmdline.Length; i++)
        {
            if (cmdline[i] != 0) continue;
            argv[index++] = data + start;
            start = i + 1;
        }

        argv[count] = null;
        return new() { argc = count, argv = argv };
    }

    /// <summary>The exe's HINSTANCE on Windows; CEF reads the command line from the OS itself.</summary>
    [SupportedOSPlatform("windows")]
    public static cef_main_args_win_t Windows() => new() { instance = GetModuleHandleW(null) };

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(char* moduleName);

    [DllImport(LibSystem)]
    private static extern int* _NSGetArgc();

    [DllImport(LibSystem)]
    private static extern byte*** _NSGetArgv();
}
