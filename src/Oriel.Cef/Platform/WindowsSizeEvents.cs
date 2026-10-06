using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>Routes a window's <c>WM_SIZE</c> to a callback with its <c>SIZE_</c> type, before Chromium handles it, as
/// Electron's <c>PreHandleMSG</c> sees it.</summary>
/// <remarks>UI thread only: a subclass belongs to the thread that owns the window.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsSizeEvents
{
    private const uint WmSize = 0x0005, WmNcDestroy = 0x0082;
    // Distinct from WindowsSubclass's id: both subclasses may sit on one window.
    private const nuint SubclassId = 2;
    private static readonly ConcurrentDictionary<nint, Action<int>> s_routes = new();

    /// <summary>Calls <paramref name="onSize"/> with <c>wParam</c> (<c>SIZE_RESTORED</c> 0, <c>SIZE_MINIMIZED</c> 1,
    /// <c>SIZE_MAXIMIZED</c> 2, ...) for every <c>WM_SIZE</c> of <paramref name="hwnd"/>. False if the window could not be
    /// subclassed.</summary>
    public static bool Route(nint hwnd, Action<int> onSize)
    {
        if (s_routes.ContainsKey(hwnd))
        {
            s_routes[hwnd] = onSize;
            return true;
        }
        if (SetWindowSubclass(hwnd, &Proc, SubclassId, 0) == 0) return false;
        s_routes[hwnd] = onSize;
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint Proc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmSize && s_routes.TryGetValue(hwnd, out var onSize))
        {
            try { onSize((int)wParam); }
            // An exception must not cross into the window procedure.
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] A window state handler threw: {e}"); }
        }
        if (message == WmNcDestroy)
        {
            s_routes.TryRemove(hwnd, out _);
            RemoveWindowSubclass(hwnd, &Proc, SubclassId);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [DllImport("comctl32.dll")] private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
