using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The application's hidden and active state, and its Dock tile's badge, as Electron drives them. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacActivation
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // NSCriticalRequest and NSInformationalRequest.
    private const nint CriticalRequest = 0;
    private const nint InformationalRequest = 10;
    // NSApplicationActivateAllWindows and NSApplicationActivateIgnoringOtherApps.
    private const nint ActivateAllWindows = 1;
    private const nint ActivateIgnoringOtherApps = 2;
    // kCurrentProcess, kProcessTransformToForegroundApplication and kProcessTransformToUIElementApplication.
    private const uint CurrentProcess = 2;
    private const uint ToForeground = 1;
    private const uint ToUIElement = 4;
    private static Func<nint>? s_dockMenu;
    private static bool s_dockMenuInstalled;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessSerialNumber
    {
        public uint High, Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    /// <summary>[NSApp hide:nil].</summary>
    public static void Hide() => Send(App(), Sel("hide:"u8), 0);

    /// <summary>[NSApp unhide:nil].</summary>
    public static void Unhide() => Send(App(), Sel("unhide:"u8), 0);

    /// <summary>[[NSRunningApplication currentApplication] isHidden].</summary>
    public static bool IsHidden() => (byte)Send(Send(Class("NSRunningApplication"u8), Sel("currentApplication"u8)), Sel("isHidden"u8)) != 0;

    /// <summary>[NSApp isActive].</summary>
    public static bool IsActive() => (byte)Send(App(), Sel("isActive"u8)) != 0;

    /// <summary>Activates the application; <paramref name="steal"/> takes focus even from the active app.</summary>
    public static void Activate(bool steal)
        => ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(App(), Sel("activateIgnoringOtherApps:"u8), steal ? (byte)1 : (byte)0);

    /// <summary>Bounces the Dock icon until the app is active (critical) or once; the request's id, or 0 when active.</summary>
    public static nint Bounce(bool critical) => Send(App(), Sel("requestUserAttention:"u8), critical ? CriticalRequest : InformationalRequest);

    /// <summary>Cancels a <see cref="Bounce"/>.</summary>
    public static void CancelBounce(nint id) => Send(App(), Sel("cancelUserAttentionRequest:"u8), id);

    /// <summary>[[NSApp dockTile] setBadgeLabel:]; empty removes the badge.</summary>
    public static void SetBadge(string text) => Send(Send(App(), Sel("dockTile"u8)), Sel("setBadgeLabel:"u8), NSString(text));

    /// <summary>[[NSApp dockTile] badgeLabel], or empty.</summary>
    public static string Badge()
    {
        var label = Send(Send(App(), Sel("dockTile"u8)), Sel("badgeLabel"u8));
        return label == 0 ? "" : Marshal.PtrToStringUTF8(Send(label, Sel("UTF8String"u8))) ?? "";
    }

    /// <summary>Bounces the Downloads stack for <paramref name="path"/>, as Electron's <c>dock.downloadFinished()</c>.</summary>
    public static void DownloadFinished(string path)
    {
        var center = Send(Class("NSDistributedNotificationCenter"u8), Sel("defaultCenter"u8));
        ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(center, Sel("postNotificationName:object:"u8),
            NSString("com.apple.DownloadFileFinished"), NSString(path));
    }

    /// <summary>[[NSRunningApplication currentApplication] isActive], as Electron's dock.show() reads it.</summary>
    public static bool IsRunningActive() => (byte)Send(Current(), Sel("isActive"u8)) != 0;

    /// <summary>Whether the app has a Dock icon: its activation policy is Regular.</summary>
    public static bool DockVisible() => Send(Current(), Sel("activationPolicy"u8)) == 0;

    /// <summary>TransformProcessType to a foreground app (with a Dock icon) or a UI element (without).</summary>
    public static void TransformProcess(bool foreground)
    {
        var psn = new ProcessSerialNumber { High = 0, Low = CurrentProcess };
        _ = TransformProcessType(&psn, foreground ? ToForeground : ToUIElement);
    }

    /// <summary>Activates the Dock itself, Electron's workaround before TransformProcessType on an active app.</summary>
    public static void ActivateDockApp()
    {
        var apps = Send(Class("NSRunningApplication"u8), Sel("runningApplicationsWithBundleIdentifier:"u8), NSString("com.apple.dock"));
        if (Send(apps, Sel("count"u8)) == 0) return;
        Send(Send(apps, Sel("objectAtIndex:"u8), 0), Sel("activateWithOptions:"u8), ActivateIgnoringOtherApps | ActivateAllWindows);
    }

    /// <summary>[[NSRunningApplication currentApplication] activateWithOptions:NSApplicationActivateIgnoringOtherApps].</summary>
    public static void ActivateSelf() => Send(Current(), Sel("activateWithOptions:"u8), ActivateIgnoringOtherApps);

    /// <summary>[NSApp setApplicationIconImage:] with one representation per (PNG, scale) pair, each sized in points as
    /// NativeImage's AsNSImage does, and marked a template as that NSImage would be; none restores the bundle's icon.</summary>
    public static void SetIcon(IReadOnlyList<(byte[] Png, double Scale)> representations, bool template = false)
    {
        if (representations.Count == 0)
        {
            Send(App(), Sel("setApplicationIconImage:"u8), 0);
            return;
        }
        var image = Send(Send(Class("NSImage"u8), Sel("alloc"u8)), Sel("init"u8));
        foreach (var (png, scale) in representations)
        {
            nint data;
            fixed (byte* p = png) data = ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)s_msgSend)(Class("NSData"u8), Sel("dataWithBytes:length:"u8), p, (nuint)png.Length);
            var rep = Send(Class("NSBitmapImageRep"u8), Sel("imageRepWithData:"u8), data);
            if (rep == 0) continue;
            var points = new NSSize { Width = Send(rep, Sel("pixelsWide"u8)) / scale, Height = Send(rep, Sel("pixelsHigh"u8)) / scale };
            ((delegate* unmanaged<nint, nint, NSSize, void>)s_msgSend)(rep, Sel("setSize:"u8), points);
            Send(image, Sel("addRepresentation:"u8), rep);
        }
        if (template) ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(image, Sel("setTemplate:"u8), 1);
        Send(App(), Sel("setApplicationIconImage:"u8), image);
        Send(image, Sel("release"u8));
    }

    /// <summary>Whether [NSApp applicationIconImage] is a template image; for tests.</summary>
    public static bool IconIsTemplate() => ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(Send(App(), Sel("applicationIconImage"u8)), Sel("isTemplate"u8)) != 0;

    /// <summary>The size of [NSApp applicationIconImage], in points.</summary>
    public static (double Width, double Height) IconSize()
    {
        var size = ((delegate* unmanaged<nint, nint, NSSize>)s_msgSend)(Send(App(), Sel("applicationIconImage"u8)), Sel("size"u8));
        return (size.Width, size.Height);
    }

    /// <summary>Makes the app delegate's <c>applicationDockMenu:</c> answer <paramref name="provider"/>'s menu (an
    /// autoreleased NSMenu, or 0 for none), as Electron's delegate does; idempotent.</summary>
    public static void InstallDockMenu(Func<nint> provider)
    {
        s_dockMenu = provider;
        if (s_dockMenuInstalled) return;
        var appDelegate = Send(App(), Sel("delegate"u8));
        fixed (byte* types = "@@:@"u8)
        {
            if (appDelegate != 0)
            {
                // Electron's own delegate answers only the app's menu, not whatever the existing delegate offers.
                class_replaceMethod(object_getClass(appDelegate), Sel("applicationDockMenu:"u8), (delegate* unmanaged<nint, nint, nint, nint>)&DockMenu, types);
            }
            else
            {
                // CEF leaves NSApp without a delegate. One answering only applicationDockMenu: changes nothing else, and
                // NSApp holds its delegate weakly, so this one lives for the process.
                fixed (byte* name = "RiftwayApplicationDelegate"u8)
                {
                    var cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                    class_replaceMethod(cls, Sel("applicationDockMenu:"u8), (delegate* unmanaged<nint, nint, nint, nint>)&DockMenu, types);
                    objc_registerClassPair(cls);
                    Send(App(), Sel("setDelegate:"u8), Send(Send(cls, Sel("alloc"u8)), Sel("init"u8)));
                }
            }
        }
        s_dockMenuInstalled = true;
    }

    /// <summary>What the Dock asks the app delegate for: [[NSApp delegate] applicationDockMenu:NSApp].</summary>
    public static nint AskDockMenu() => Send(Send(App(), Sel("delegate"u8)), Sel("applicationDockMenu:"u8), App());

    [UnmanagedCallersOnly]
    private static nint DockMenu(nint self, nint cmd, nint sender)
    {
        try { return s_dockMenu?.Invoke() ?? 0; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] Building the Dock menu threw: {e}");
            return 0;
        }
    }

    /// <summary>The class name of [NSApp delegate], or empty.</summary>
    public static string DelegateClass() => Send(App(), Sel("delegate"u8)) is var d and not 0 ? Marshal.PtrToStringUTF8(class_getName(object_getClass(d))) ?? "" : "";

    private static nint Current() => Send(Class("NSRunningApplication"u8), Sel("currentApplication"u8));

    private static nint App() => Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return ((delegate* unmanaged<nint, nint, byte*, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithUTF8String:"u8), p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint object_getClass(nint obj);
    [DllImport(ObjC)] private static extern nint class_getName(nint cls);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern nint class_replaceMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern int TransformProcessType(ProcessSerialNumber* psn, uint transformState);
}
