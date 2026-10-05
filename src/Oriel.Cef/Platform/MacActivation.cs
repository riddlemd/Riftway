using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The application's hidden and active state, and its Dock tile's badge, as Electron drives them. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacActivation
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // NSCriticalRequest and NSInformationalRequest.
    private const nint CriticalRequest = 0;
    private const nint InformationalRequest = 10;
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
}
