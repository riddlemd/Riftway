using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>The API that Oriel's CEF patches (<c>eng/cef/patches</c>) add on top of CEF's API version 15400. CEF puts
/// unreleased API at its "next" version (999998), which every libcef also serves; a patched libcef's hashes for it are
/// these.</summary>
/// <remarks>Recompute the hashes whenever a patch changes a header in <c>include/</c>: see <c>eng/cef/README.md</c>.</remarks>
public static unsafe class CefPatchedApi
{
    /// <summary>CEF's <c>CEF_API_VERSION_NEXT</c>.</summary>
    public const int Version = 999998;

    public const string HashWindows = "2a33e58413e982d6979304c0de20e59fca1baf21";
    public const string HashMac = "fb4c2b3be1c83fafb8e5674aeb2917eb22a6a797";
    public const string HashLinux = "2d137e8cc7d079f5f2b310a17f487df99370c039";

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

    /// <summary><c>CefWindow::SetEventListeningEnabled</c> (patch 0002): on Linux, stops or restarts every mouse, key,
    /// touch and pointer crossing event to <paramref name="window"/> (the Views host's <c>DisableEventListening</c>), as
    /// Electron's <c>setEnabled</c> does. Browser UI thread.</summary>
    /// <returns>False, without a call, when the loaded libcef lacks the patches.</returns>
    public static bool SetEventListeningEnabled(cef_window_t* window, bool enabled)
        => SetEventListeningEnabled(window, enabled, CefRuntime.IsPatched);

    internal static bool SetEventListeningEnabled(cef_window_t* window, bool enabled, bool patched)
    {
        // Only a patched libcef hands out the next version's cef_window_t, with the slot appended.
        if (!patched || window == null) return false;
        ((cef_window_next_t*)window)->set_event_listening_enabled(window, enabled ? 1 : 0);
        return true;
    }

    /// <summary><c>CefWindow::SetXdgActivationToken</c> (patch 0004): Chromium puts <paramref name="token"/> on the next
    /// window it maps or activates (<c>base::nix::SetActivationToken</c>), as for a status notifier host's
    /// <c>ProvideXdgActivationToken</c>. Linux; browser UI thread.</summary>
    /// <returns>False, without a call, when the loaded libcef lacks the patches.</returns>
    public static bool SetXdgActivationToken(string token)
        => SetXdgActivationToken(token, CefRuntime.IsPatched, &cef_window_set_xdg_activation_token);

    internal static bool SetXdgActivationToken(string token, bool patched, delegate*<cef_string_utf16_t*, void> set)
    {
        ArgumentNullException.ThrowIfNull(token);
        // A stock libcef has no such export.
        if (!patched) return false;
        fixed (char* chars = token)
        {
            // CEF copies the string before the call returns, so a borrowed buffer is enough.
            var value = new cef_string_utf16_t { str = chars, length = (nuint)token.Length };
            set(&value);
        }
        return true;
    }

    [DllImport("libcef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void cef_window_set_xdg_activation_token(cef_string_utf16_t* token);
}

/// <summary><c>cef_window_delegate_t</c> at the next API version of a patched libcef: CEF appends API added after a
/// release to the end of the struct.</summary>
public unsafe struct cef_window_delegate_next_t
{
    public cef_window_delegate_t @base;

    /// <summary><c>CefWindowDelegate::IsTranslucent</c> (patch 0001): asked once, before the native window exists.</summary>
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> is_translucent;
}

/// <summary><c>cef_window_t</c> at the next API version of a patched libcef.</summary>
public unsafe struct cef_window_next_t
{
    public cef_window_t @base;

    /// <summary><c>CefWindow::SetEventListeningEnabled</c> (patch 0002).</summary>
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, void> set_event_listening_enabled;
}
