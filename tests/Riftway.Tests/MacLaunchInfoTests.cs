using System.Runtime.InteropServices;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Electron v44's applicationDidFinishLaunching: launch info, from a notification built here and never posted.
public sealed unsafe class MacLaunchInfoTests
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    private static nint Send(nint receiver, string selector, params nint[] args) => args.Length switch
    {
        0 => ((delegate* unmanaged<nint, nint, nint>)s_send)(receiver, sel_registerName(selector)),
        1 => ((delegate* unmanaged<nint, nint, nint, nint>)s_send)(receiver, sel_registerName(selector), args[0]),
        _ => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_send)(receiver, sel_registerName(selector), args[0], args[1]),
    };

    private static nint Str(string s) => Send(objc_getClass("NSString"), "stringWithUTF8String:", Marshal.StringToHGlobalAnsi(s));

    private static nint Dictionary(nint key, nint value) => Send(objc_getClass("NSDictionary"), "dictionaryWithObject:forKey:", value, key);

    private static nint LaunchNotification(nint userInfo)
    {
        var appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var key = *(nint*)NativeLibrary.GetExport(appKit, "NSApplicationLaunchUserNotificationKey");
        return ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)s_send)(objc_getClass("NSNotification"),
            sel_registerName("notificationWithName:object:userInfo:"), Str("NSApplicationDidFinishLaunchingNotification"), 0,
            userInfo == 0 ? 0 : Dictionary(key, userInfo));
    }

    [Fact]
    public void APlainLaunchHasNoInfo()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Empty(MacApp.LaunchInfo(LaunchNotification(0)));
        Assert.Empty(MacApp.LaunchInfo(0));
    }

    [Fact]
    public void ALaunchFromALegacyNotificationIsItsUserInfo()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var legacy = Send(Send(objc_getClass("NSUserNotification"), "alloc"), "init");
        Send(legacy, "setUserInfo:", Dictionary(Str("page"), Str("inbox")));
        Assert.Equal("""{"page":"inbox"}""", MacApp.LaunchInfo(LaunchNotification(legacy)).ToJsonString());
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
}
