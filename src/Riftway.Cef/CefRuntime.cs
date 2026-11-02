using System.Runtime.InteropServices;
using Riftway.Cef.Interop;

namespace Riftway.Cef;

/// <summary>Loads libcef and agrees the API version with it; must run before any other CEF call.</summary>
public static unsafe class CefRuntime
{
    private const string LibraryName = "libcef";

    private static nint s_library;

    /// <summary>Whether <see cref="Load"/> has succeeded in this process.</summary>
    public static bool IsLoaded => s_library != 0;

    /// <summary>Whether the loaded libcef carries Riftway's CEF patches (<c>eng/cef</c>) and this process uses their API.</summary>
    public static bool IsPatched { get; private set; }

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

        // CEF fixes the API version for the process on the first cef_api_hash call that names a version it serves, so
        // the probe of the next version is the call that fixes it. A stock libcef's next version hashes as 15400: the
        // same API, so stock CEF runs as before.
        var stock = CefPatchedApi.StockHash;
        var next = Marshal.PtrToStringUTF8((nint)CefNative.cef_api_hash(CefPatchedApi.Version, 0));
        switch (CefPatchedApi.Classify(next, stock, CefPatchedApi.PatchedHash))
        {
            case CefPatchedApi.Kind.Patched:
                IsPatched = true;
                return;
            case CefPatchedApi.Kind.Stock:
                return;
            case CefPatchedApi.Kind.Unknown:
                throw Mismatch(libraryPath, next, stock);
        }

        var actual = Marshal.PtrToStringUTF8((nint)CefNative.cef_api_hash(CefApiVersion.Version, 0));
        if (!string.Equals(actual, stock, StringComparison.Ordinal)) throw Mismatch(libraryPath, actual, stock);
    }

    private static InvalidOperationException Mismatch(string libraryPath, string? actual, string expected) => new(
        $"libcef at '{libraryPath}' has API hash '{actual}', but Riftway was built for CEF API " +
        $"{CefApiVersion.Version} ('{expected}'), optionally with Riftway's CEF patches ('{CefPatchedApi.PatchedHash}'). " +
        "The runtime and Riftway must come from the same CEF release.");
}
