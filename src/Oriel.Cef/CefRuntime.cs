using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>Loads libcef and agrees the API version with it; must run before any other CEF call.</summary>
public static unsafe class CefRuntime
{
    private const string LibraryName = "libcef";

    private static nint s_library;

    /// <summary>Whether <see cref="Load"/> has succeeded in this process.</summary>
    public static bool IsLoaded => s_library != 0;

    /// <summary>Loads libcef from <paramref name="libraryPath"/> and checks its API hash.</summary>
    /// <exception cref="InvalidOperationException">The library's API hash is not the one these bindings were generated for.</exception>
    public static void Load(string libraryPath)
    {
        ArgumentNullException.ThrowIfNull(libraryPath);
        if (IsLoaded) return;

        // libcef lives at a different path per OS and per process (a macOS helper sits three
        // bundles deep), so the generated "libcef" imports are bound to whatever was loaded here.
        s_library = NativeLibrary.Load(libraryPath);
        NativeLibrary.SetDllImportResolver(typeof(CefNative).Assembly, (name, _, _) => name == LibraryName ? s_library : 0);

        // CEF fixes the API version for the process on the first cef_api_hash call.
        var actual = Marshal.PtrToStringUTF8((nint)CefNative.cef_api_hash(CefApiVersion.Version, 0));
        var expected = OperatingSystem.IsWindows() ? CefApiVersion.HashWindows
            : OperatingSystem.IsMacOS() ? CefApiVersion.HashMac
            : CefApiVersion.HashLinux;

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"libcef at '{libraryPath}' has API hash '{actual}', but Oriel was built for CEF API " +
                $"{CefApiVersion.Version} ('{expected}'). The runtime and Oriel must come from the same CEF release.");
        }
    }
}
