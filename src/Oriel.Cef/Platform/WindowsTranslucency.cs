using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The Win32 half of transparent windows and visible backdrops: the frame styles a see-through window must not
/// have, and the activation a frameless window's backdrop needs.</summary>
/// <remarks>UI thread only: a subclass belongs to the thread that owns the window.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsTranslucency
{
    private const int GwlStyle = -16;
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WmStyleChanging = 0x007C;
    private const uint WmNcActivate = 0x0086;
    private const uint WmNcDestroy = 0x0082;
    private const uint SwpFrameChanged = 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010; // FRAMECHANGED, NOSIZE, NOMOVE, NOZORDER, NOACTIVATE
    private static readonly ConcurrentDictionary<nint, byte> s_seeThrough = new();

    /// <summary><paramref name="style"/> without a caption or sizing border. DWM draws a window with either as framed
    /// and covers what is behind it.</summary>
    public static uint StripFrame(uint style) => style & ~(WsCaption | WsThickFrame);

    /// <summary>Takes the caption and sizing border off for good, so the desktop shows through where the window's
    /// content is transparent. False if the window could not be subclassed.</summary>
    public static bool MakeSeeThrough(nint hwnd)
    {
        if (!s_seeThrough.ContainsKey(hwnd))
        {
            if (SetWindowSubclass(hwnd, &Proc, 2, 0) == 0) return false;
            s_seeThrough[hwnd] = 0;
        }
        SetWindowLongPtrW(hwnd, GwlStyle, (nint)StripFrame((uint)GetWindowLongPtrW(hwnd, GwlStyle)));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpFrameChanged);
        return true;
    }

    /// <summary>Activates the non-client area after a backdrop change, as Electron's <c>SetBackgroundMaterial</c> does:
    /// until then DWM draws a frameless window's backdrop as a flat inactive colour.</summary>
    public static void ActivateBackdrop(nint hwnd, bool hasFrame) => DefWindowProcW(hwnd, WmNcActivate, 1, hasFrame ? 0 : -1);

    // Chromium puts the caption back whenever size constraints change (HWNDMessageHandler::SizeConstraintsChanged
    // keeps the caption the window was created with), so every later style change is stripped too.
    [UnmanagedCallersOnly]
    private static nint Proc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmStyleChanging && wParam == GwlStyle)
        {
            var styles = (StyleStruct*)lParam;
            styles->New = StripFrame(styles->New);
        }
        else if (message == WmNcDestroy)
        {
            s_seeThrough.TryRemove(hwnd, out _);
            RemoveWindowSubclass(hwnd, &Proc, 2);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleStruct
    {
        public uint Old;
        public uint New;
    }

    [DllImport("comctl32.dll")] private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
}
