using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Windows' sandboxed layout: CEF's <c>bootstrap.exe</c> renamed to the app's exe, which loads
/// Riftway.Host.Native as <c>&lt;exe name&gt;.dll</c> and starts .NET from it in the browser process.</summary>
/// <remarks>Only bootstrap.exe holds Chromium's sandbox broker: CEF 154 ships no <c>cef_sandbox.lib</c> that MSVC
/// or a .NET apphost can link, and CEF turns the sandbox off whenever <c>browser_subprocess_path</c> is set.</remarks>
public static unsafe class BootstrapHost
{
    /// <summary>The host DLL's export that returns the sandbox info bootstrap.exe created for this process.</summary>
    public const string SandboxInfoExport = "riftway_sandbox_info";

    /// <summary>Whether this process was started by the bootstrap host, and the sandbox info to pass to
    /// <c>cef_execute_process</c> and <c>cef_initialize</c> (null when bootstrap supplied none).</summary>
    [SupportedOSPlatform("windows")]
    public static bool TryGetSandboxInfo(out void* sandboxInfo)
    {
        sandboxInfo = null;
        if (ClientDllPath(Environment.ProcessPath) is not { } path) return false;
        nint module;
        // Already loaded by bootstrap.exe when hosted; GetModuleHandle never loads, so an apphost's managed
        // <App>.dll of the same name is not mapped as a native image.
        fixed (char* name = path) module = GetModuleHandleW(name);
        if (module == 0 || !NativeLibrary.TryGetExport(module, SandboxInfoExport, out var export)) return false;
        sandboxInfo = ((delegate* unmanaged<void*>)export)();
        return true;
    }

    /// <summary>Whether this process was started by the bootstrap host (the default Windows layout).</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsHosted => TryGetSandboxInfo(out _);

    /// <summary>bootstrap.exe loads the client DLL named after the exe, beside it.</summary>
    internal static string? ClientDllPath(string? processPath)
        => string.IsNullOrEmpty(processPath) ? null : Path.ChangeExtension(processPath, ".dll");

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(char* moduleName);
}
