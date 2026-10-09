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

    public const string HashWindows = "30e79e5a6e1db4db305b80188052d76b46bdfd37";
    public const string HashMac = "651a9644980c8e4cec5c8b2123084229bef13d54";
    public const string HashLinux = "1d9b34f5f12ecd861e292da95c07d5a829fe490d";

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

    /// <summary>What patch 0005 adds to <c>cef_download_item_t</c>. CEF's item; the callback's thread.</summary>
    /// <returns>Null, without a call, when the loaded libcef lacks the patches.</returns>
    public static DownloadItemExtras? ReadDownloadExtras(cef_download_item_t* item)
        => ReadDownloadExtras(item, CefRuntime.IsPatched, &CefString.ReadUserFree);

    internal static DownloadItemExtras? ReadDownloadExtras(cef_download_item_t* item, bool patched,
        delegate*<cef_string_utf16_t*, string> readUserFree)
    {
        // A stock item's struct ends before these slots.
        if (!patched || item == null) return null;
        var next = (cef_download_item_next_t*)item;
        var frame = readUserFree(next->get_frame_identifier(item));
        return new DownloadItemExtras(
            next->has_user_gesture(item) != 0,
            readUserFree(next->get_last_modified_time(item)),
            readUserFree(next->get_etag(item)),
            readUserFree(next->get_request_initiator(item)),
            frame.Length == 0 ? null : frame);
    }

    /// <summary><c>CefRequestContext::StartNetLog</c> (patch 0006): starts writing the context's network events to
    /// <paramref name="path"/>. <paramref name="captureMode"/> is 0 (metadata), 1 (with cookies and credentials) or 2
    /// (with all bytes); a negative <paramref name="maxFileSize"/> is no limit. Takes the callback's reference.</summary>
    /// <returns>False, without a call, when the loaded libcef lacks the patches; the callback is then released.</returns>
    public static bool StartNetLog(cef_request_context_t* context, string path, int captureMode, long maxFileSize,
        cef_net_log_callback_t* callback)
        => StartNetLog(context, path, captureMode, maxFileSize, callback, CefRuntime.IsPatched);

    internal static bool StartNetLog(cef_request_context_t* context, string path, int captureMode, long maxFileSize,
        cef_net_log_callback_t* callback, bool patched)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!patched || context == null)
        {
            NativeObject.Release(callback);
            return false;
        }
        fixed (char* chars = path)
        {
            // libcef turns the path into a FilePath before the call returns.
            var value = new cef_string_utf16_t { str = chars, length = (nuint)path.Length };
            ((cef_request_context_next_t*)context)->start_net_log(context, &value, captureMode, maxFileSize, callback);
        }
        return true;
    }

    /// <summary><c>CefRequestContext::StopNetLog</c> (patch 0006). Takes the callback's reference.</summary>
    /// <returns>False, without a call, when the loaded libcef lacks the patches; the callback is then released.</returns>
    public static bool StopNetLog(cef_request_context_t* context, cef_net_log_callback_t* callback)
        => StopNetLog(context, callback, CefRuntime.IsPatched);

    internal static bool StopNetLog(cef_request_context_t* context, cef_net_log_callback_t* callback, bool patched)
    {
        if (!patched || context == null)
        {
            NativeObject.Release(callback);
            return false;
        }
        ((cef_request_context_next_t*)context)->stop_net_log(context, callback);
        return true;
    }

    /// <summary><c>CefRequestContext::IsNetLogging</c> (patch 0006). Browser UI thread.</summary>
    /// <returns>False, without a call, when the loaded libcef lacks the patches.</returns>
    public static bool IsNetLogging(cef_request_context_t* context) => IsNetLogging(context, CefRuntime.IsPatched);

    internal static bool IsNetLogging(cef_request_context_t* context, bool patched)
        => patched && context != null && ((cef_request_context_next_t*)context)->is_net_logging(context) != 0;
}

/// <summary>A download's fields from patch 0005: Electron's <c>hasUserGesture</c>, <c>getLastModifiedTime</c>,
/// <c>getETag</c> and <c>getInitiatorOrigin</c>, and the CEF identifier of the frame that started it.</summary>
public sealed record DownloadItemExtras(bool HasUserGesture, string LastModifiedTime, string ETag, string InitiatorOrigin,
    string? FrameIdentifier);

/// <summary><c>cef_download_item_t</c> at the next API version of a patched libcef.</summary>
public unsafe struct cef_download_item_next_t
{
    public cef_download_item_t @base;

    /// <summary>Patch 0005, in declaration order.</summary>
    public delegate* unmanaged[Cdecl]<cef_download_item_t*, int> has_user_gesture;
    public delegate* unmanaged[Cdecl]<cef_download_item_t*, cef_string_utf16_t*> get_last_modified_time;
    public delegate* unmanaged[Cdecl]<cef_download_item_t*, cef_string_utf16_t*> get_etag;
    public delegate* unmanaged[Cdecl]<cef_download_item_t*, cef_string_utf16_t*> get_request_initiator;
    public delegate* unmanaged[Cdecl]<cef_download_item_t*, cef_string_utf16_t*> get_frame_identifier;
}

/// <summary><c>cef_request_context_t</c> at the next API version of a patched libcef.</summary>
public unsafe struct cef_request_context_next_t
{
    public cef_request_context_t @base;

    /// <summary>Patch 0006, in declaration order.</summary>
    public delegate* unmanaged[Cdecl]<cef_request_context_t*, cef_string_utf16_t*, int, long, cef_net_log_callback_t*, void> start_net_log;
    public delegate* unmanaged[Cdecl]<cef_request_context_t*, cef_net_log_callback_t*, void> stop_net_log;
    public delegate* unmanaged[Cdecl]<cef_request_context_t*, int> is_net_logging;
}

/// <summary><c>cef_net_log_callback_t</c> (patch 0006), implemented by the client.</summary>
public unsafe struct cef_net_log_callback_t
{
    public cef_base_ref_counted_t @base;

    /// <summary>UI thread: <c>error</c> is empty on success, else Electron's rejection text.</summary>
    public delegate* unmanaged[Cdecl]<cef_net_log_callback_t*, cef_string_utf16_t*, void> on_net_log_complete;
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
