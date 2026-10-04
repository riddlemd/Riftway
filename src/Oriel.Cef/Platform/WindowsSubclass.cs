using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A thumbnail toolbar button for <see cref="WindowsTaskbar.SetThumbButtons"/>; <paramref name="Flags"/> are
/// Win32's <c>THBF_</c> flags.</summary>
public readonly record struct ThumbButton(uint Id, nint Icon, string? Tooltip, uint Flags);

/// <summary>Routes thumbnail toolbar clicks (<c>WM_COMMAND</c> with <c>THBN_CLICKED</c>) from a window to a callback.</summary>
/// <remarks>UI thread only: a subclass belongs to the thread that owns the window.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsSubclass
{
    private const uint WmCommand = 0x0111;
    private const uint WmNcDestroy = 0x0082;
    private const int ThbnClicked = 0x1800;
    private static readonly ConcurrentDictionary<nint, Action<int>> s_clicks = new();

    /// <summary>Calls <paramref name="onClick"/> with the button id whenever a thumbnail button of
    /// <paramref name="hwnd"/> is clicked. False if the window could not be subclassed.</summary>
    public static bool RouteThumbButtonClicks(nint hwnd, Action<int> onClick)
    {
        if (s_clicks.ContainsKey(hwnd))
        {
            s_clicks[hwnd] = onClick;
            return true;
        }
        if (SetWindowSubclass(hwnd, &Proc, 1, 0) == 0) return false;
        s_clicks[hwnd] = onClick;
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint Proc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmCommand && ((wParam >> 16) & 0xFFFF) == ThbnClicked && s_clicks.TryGetValue(hwnd, out var onClick))
        {
            try { onClick((int)(wParam & 0xFFFF)); }
            // An exception must not cross into the window procedure.
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] A thumbnail button's click handler threw: {e}"); }
            return 0;
        }
        if (message == WmNcDestroy)
        {
            s_clicks.TryRemove(hwnd, out _);
            RemoveWindowSubclass(hwnd, &Proc, 1);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [DllImport("comctl32.dll")] private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
