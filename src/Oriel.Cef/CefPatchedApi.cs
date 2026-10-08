using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>The API that Oriel's CEF patches (<c>eng/cef/patches</c>) add on top of CEF's API version 15400. CEF puts
/// unreleased API at its "next" version (999998), which every libcef also serves; a patched libcef's hashes for it are
/// these.</summary>
/// <remarks>Recompute the hashes whenever a patch changes a header in <c>include/</c>: see <c>eng/cef/README.md</c>.</remarks>
public static class CefPatchedApi
{
    /// <summary>CEF's <c>CEF_API_VERSION_NEXT</c>.</summary>
    public const int Version = 999998;

    public const string HashWindows = "2181c18fcd03e8f743b044ef9c4daee1eb6b2f30";
    public const string HashMac = "3493b49edc044991e6e1a1888ee84361aede1c9e";
    public const string HashLinux = "a00027a57b67e129a7aefc08e6028c21fd94021a";

    /// <summary>What a libcef's hash for the next version says about it.</summary>
    internal enum Kind
    {
        /// <summary>No next version: configure 15400 as before.</summary>
        Unavailable,

        /// <summary>Next is the same API as 15400: a stock libcef.</summary>
        Stock,

        /// <summary>Next carries Oriel's patches.</summary>
        Patched,

        /// <summary>Next is some other API: another CEF release or other patches.</summary>
        Unknown,
    }

    internal static Kind Classify(string? nextHash, string stockHash, string patchedHash)
        => nextHash is null ? Kind.Unavailable
            : string.Equals(nextHash, stockHash, StringComparison.Ordinal) ? Kind.Stock
            : string.Equals(nextHash, patchedHash, StringComparison.Ordinal) ? Kind.Patched
            : Kind.Unknown;

    internal static string StockHash => OperatingSystem.IsWindows() ? CefApiVersion.HashWindows
        : OperatingSystem.IsMacOS() ? CefApiVersion.HashMac
        : CefApiVersion.HashLinux;

    internal static string PatchedHash => OperatingSystem.IsWindows() ? HashWindows
        : OperatingSystem.IsMacOS() ? HashMac
        : HashLinux;
}

/// <summary><c>cef_window_delegate_t</c> at the next API version of a patched libcef: CEF appends API added after a
/// release to the end of the struct.</summary>
public unsafe struct cef_window_delegate_next_t
{
    public cef_window_delegate_t @base;

    /// <summary><c>CefWindowDelegate::IsTranslucent</c> (patch 0001): asked once, before the native window exists.</summary>
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> is_translucent;
}
