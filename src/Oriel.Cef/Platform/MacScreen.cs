using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>What Chromium's <c>BuildDisplayForScreen</c> (ui/display/mac/screen_mac.mm) reads from an NSScreen beyond what CEF reports.</summary>
/// <param name="Label">NSScreen's <c>localizedName</c>.</param>
/// <param name="Builtin"><c>CGDisplayIsBuiltin</c> returned YES.</param>
/// <param name="Monochrome"><c>CGDisplayUsesForceToGray</c>, which is system-wide.</param>
/// <param name="RefreshRate">1 / <c>minimumRefreshInterval</c>, else <c>maximumFramesPerSecond</c>, as Chromium's float.</param>
/// <param name="MaxPotentialEdr"><c>maximumPotentialExtendedDynamicRangeColorComponentValue</c>.</param>
/// <param name="MaxEdr"><c>maximumExtendedDynamicRangeColorComponentValue</c>.</param>
/// <param name="IccProfile">The screen's color space as ICC data, or null without one.</param>
public sealed record MacScreenInfo(string Label, bool Builtin, bool Monochrome, float RefreshRate, float MaxPotentialEdr, float MaxEdr, byte[]? IccProfile);

/// <summary>NSScreen details and screen-change notifications. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacScreen
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static Action? s_changed;
    private static nint s_observer;

    /// <summary>The NSScreen whose <c>NSScreenNumber</c> is <paramref name="displayId"/>, or null when none is.</summary>
    public static MacScreenInfo? Describe(uint displayId)
    {
        var screen = Find(displayId);
        if (screen == 0) return null;
        var interval = SendDouble(screen, Sel("minimumRefreshInterval"u8));
        // Chromium stores the rate in a float: 1/0.016666 is 60.0024, not 60.00240009600384.
        var refresh = interval > 0 ? (float)(1.0 / interval) : (float)Send(screen, Sel("maximumFramesPerSecond"u8));
        return new MacScreenInfo(
            ReadString(Send(screen, Sel("localizedName"u8))),
            CGDisplayIsBuiltin(displayId) == 1,
            CGDisplayUsesForceToGray() != 0,
            refresh,
            (float)SendDouble(screen, Sel("maximumPotentialExtendedDynamicRangeColorComponentValue"u8)),
            (float)SendDouble(screen, Sel("maximumExtendedDynamicRangeColorComponentValue"u8)),
            IccProfile(screen));
    }

    /// <summary>Calls <paramref name="changed"/> on the main thread whenever Chromium re-reads the screens: on
    /// <c>NSApplicationDidChangeScreenParametersNotification</c>, <c>NSScreenColorSpaceDidChangeNotification</c> and
    /// CoreGraphics' display reconfiguration callback. Idempotent; the last <paramref name="changed"/> wins.</summary>
    public static void ObserveChanges(Action changed)
    {
        s_changed = changed;
        if (s_observer != 0) return;
        fixed (byte* name = "OrielScreenObserver"u8)
        fixed (byte* types = "v@:@"u8)
        {
            var cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            class_addMethod(cls, Sel("screensChanged:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnNotification, types);
            objc_registerClassPair(cls);
            s_observer = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
        var center = Send(Class("NSNotificationCenter"u8), Sel("defaultCenter"u8));
        var appKit = NativeLibrary.Load(AppKit);
        foreach (var notification in new[] { "NSApplicationDidChangeScreenParametersNotification", "NSScreenColorSpaceDidChangeNotification" })
        {
            var nameString = *(nint*)NativeLibrary.GetExport(appKit, notification);
            ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)s_msgSend)(
                center, Sel("addObserver:selector:name:object:"u8), s_observer, Sel("screensChanged:"u8), nameString, 0);
        }
        CGDisplayRegisterReconfigurationCallback(&OnReconfigured, 0);
    }

    /// <summary>Posts <c>NSApplicationDidChangeScreenParametersNotification</c> as AppKit does when a display changes.</summary>
    public static void PostScreenParametersChanged()
    {
        var center = Send(Class("NSNotificationCenter"u8), Sel("defaultCenter"u8));
        var name = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(AppKit), "NSApplicationDidChangeScreenParametersNotification");
        ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(center, Sel("postNotificationName:object:"u8), name, App());
    }

    [UnmanagedCallersOnly]
    private static void OnNotification(nint self, nint cmd, nint notification) => Raise();

    [UnmanagedCallersOnly]
    private static void OnReconfigured(uint display, uint flags, nint userInfo) => Raise();

    private static void Raise()
    {
        try { s_changed?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a screen change: {e}"); }
    }

    private static nint Find(uint displayId)
    {
        var screens = Send(Class("NSScreen"u8), Sel("screens"u8));
        var key = NewString("NSScreenNumber"u8);
        for (nint i = 0, n = Send(screens, Sel("count"u8)); i < n; i++)
        {
            var screen = Send(screens, Sel("objectAtIndex:"u8), i);
            var number = Send(Send(screen, Sel("deviceDescription"u8)), Sel("objectForKey:"u8), key);
            if (number != 0 && (uint)Send(number, Sel("unsignedIntValue"u8)) == displayId) return screen;
        }
        return 0;
    }

    private static byte[]? IccProfile(nint screen)
    {
        var colorSpace = Send(screen, Sel("colorSpace"u8));
        var cgColorSpace = colorSpace == 0 ? 0 : Send(colorSpace, Sel("CGColorSpace"u8));
        if (cgColorSpace == 0) return null;
        var data = CGColorSpaceCopyICCData(cgColorSpace);
        if (data == 0) return null;
        try { return new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(data), (int)CFDataGetLength(data)).ToArray(); }
        finally { CFRelease(data); }
    }

    private static nint App() => Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

    private static nint NewString(ReadOnlySpan<byte> utf8)
    {
        fixed (byte* p = utf8) return Send(Class("NSString"u8), Sel("stringWithUTF8String:"u8), (nint)p);
    }

    private static string ReadString(nint nsString)
        => nsString == 0 ? string.Empty : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"u8))) ?? string.Empty;

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static double SendDouble(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, double>)s_msgSend)(receiver, selector);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(CoreGraphics)] private static extern int CGDisplayIsBuiltin(uint display);
    // Private, but exported; Chromium declares it the same way.
    [DllImport(CoreGraphics)] private static extern byte CGDisplayUsesForceToGray();
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceCopyICCData(nint space);
    [DllImport(CoreGraphics)] private static extern int CGDisplayRegisterReconfigurationCallback(delegate* unmanaged<uint, uint, nint, void> callback, nint userInfo);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(nint data);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
}
