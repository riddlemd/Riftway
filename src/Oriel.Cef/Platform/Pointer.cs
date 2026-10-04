using System.Runtime.InteropServices;

namespace Oriel.Cef.Platform;

/// <summary>The mouse pointer's screen position, which CEF does not report.</summary>
public static unsafe partial class Pointer
{
    /// <summary>The pointer's position: in screen DIPs on macOS (<paramref name="isPixels"/> false), in screen
    /// pixels on Windows and Linux.</summary>
    /// <exception cref="PlatformNotSupportedException">Another OS.</exception>
    /// <exception cref="InvalidOperationException">Linux: no X display.</exception>
    public static (int X, int Y) Position(out bool isPixels)
    {
        isPixels = !OperatingSystem.IsMacOS();
        if (OperatingSystem.IsMacOS()) return MacPosition();
        if (OperatingSystem.IsWindows()) return WindowsPosition();
        if (OperatingSystem.IsLinux()) return LinuxPosition();
        throw new PlatformNotSupportedException($"Oriel does not run on {Environment.OSVersion} yet.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    // Global display coordinates in points, origin at the top-left of the main display, as CEF's DIPs.
    private static (int X, int Y) MacPosition()
    {
        var e = CGEventCreate(0);
        if (e == 0) return (0, 0);
        var location = CGEventGetLocation(e);
        CFRelease(e);
        return ((int)Math.Floor(location.X), (int)Math.Floor(location.Y));
    }

    private static (int X, int Y) WindowsPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    private static nint s_xDisplay;

    private static (int X, int Y) LinuxPosition()
    {
        if (s_xDisplay == 0) s_xDisplay = XOpenDisplay(null);
        if (s_xDisplay == 0) throw new InvalidOperationException("No X display to read the pointer from.");
        XQueryPointer(s_xDisplay, XDefaultRootWindow(s_xDisplay), out _, out _, out var x, out var y, out _, out _, out _);
        return (x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreGraphics)] private static partial nint CGEventCreate(nint source);
    [LibraryImport(CoreGraphics)] private static partial CGPoint CGEventGetLocation(nint e);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint obj);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out WinPoint point);

    [LibraryImport("libX11.so.6")] private static partial nint XOpenDisplay(byte* name);
    [LibraryImport("libX11.so.6")] private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryPointer(nint display, nint window, out nint root, out nint child, out int rootX, out int rootY,
        out int windowX, out int windowY, out uint mask);
}
