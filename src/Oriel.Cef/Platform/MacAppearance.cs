using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The application's light or dark appearance, and the display accessibility settings. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacAppearance
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    /// <summary>Forces the app's appearance: true dark, false light, null the system's.</summary>
    public static void SetDark(bool? dark)
    {
        var appearance = dark is { } d
            ? Send(Class("NSAppearance"), "appearanceNamed:", NSString(d ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"))
            : 0;
        Send(App(), "setAppearance:", appearance);
    }

    /// <summary>Whether the app currently draws dark: its forced appearance, else the system's.</summary>
    public static bool IsDark()
    {
        var name = Send(Send(App(), "effectiveAppearance"), "name");
        return Marshal.PtrToStringUTF8(Send(name, "UTF8String"))?.Contains("Dark", StringComparison.Ordinal) == true;
    }

    /// <summary>The system's "Increase contrast" setting.</summary>
    public static bool IncreaseContrast => Flag("accessibilityDisplayShouldIncreaseContrast");

    /// <summary>The system's "Invert colours" setting.</summary>
    public static bool InvertColors => Flag("accessibilityDisplayShouldInvertColors");

    /// <summary>The system's "Reduce transparency" setting.</summary>
    public static bool ReduceTransparency => Flag("accessibilityDisplayShouldReduceTransparency");

    private static Action? s_changed;
    private static nint s_observer;

    /// <summary>Calls <paramref name="changed"/> on the main thread when the system's appearance or display accessibility
    /// settings change; once per process.</summary>
    public static void ObserveChanges(Action changed)
    {
        if (s_observer != 0) return;
        s_changed = changed;
        var name = System.Text.Encoding.UTF8.GetBytes("OrielAppearanceObserver\0");
        var types = System.Text.Encoding.UTF8.GetBytes("v@:@\0");
        nint cls;
        fixed (byte* n = name) cls = objc_allocateClassPair(Class("NSObject"), n, 0);
        fixed (byte* t = types) class_addMethod(cls, Selector("changed:"), (delegate* unmanaged<nint, nint, nint, void>)&OnChanged, t);
        objc_registerClassPair(cls);
        s_observer = Send(Send(cls, "alloc"), "init");
        Observe(Send(Class("NSDistributedNotificationCenter"), "defaultCenter"), "AppleInterfaceThemeChangedNotification");
        Observe(Send(Send(Class("NSWorkspace"), "sharedWorkspace"), "notificationCenter"), "NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification");
    }

    /// <summary>Hands the observer the system's appearance-changed notification; for tests. Posting it on the distributed
    /// center would make every app in the session read its appearance again.</summary>
    public static void DeliverThemeChanged()
    {
        if (s_observer != 0) Send(s_observer, "changed:", 0);
    }

    private static void Observe(nint center, string notification)
        => Send4(center, "addObserver:selector:name:object:", s_observer, Selector("changed:"), NSString(notification), 0);

    [UnmanagedCallersOnly]
    private static void OnChanged(nint self, nint cmd, nint notification)
    {
        // An exception must not unwind into AppKit's notification dispatch.
        try { s_changed?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception after an appearance change: {e}"); }
    }

    private static bool Flag(string selector) => (byte)Send(Send(Class("NSWorkspace"), "sharedWorkspace"), selector) != 0;

    private static nint App() => Send(Class("NSApplication"), "sharedApplication");

    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return Send(Class("NSString"), "stringWithUTF8String:", (nint)p);
    }

    private static nint Class(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes) return objc_getClass(p);
    }

    private static nint Selector(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes) return sel_registerName(p);
    }

    private static nint Send(nint receiver, string selector)
        => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, Selector(selector));

    private static void Send4(nint receiver, string selector, nint a, nint b, nint c, nint d)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)s_msgSend)(receiver, Selector(selector), a, b, c, d);

    private static nint Send(nint receiver, string selector, nint argument)
        => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, Selector(selector), argument);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
}
