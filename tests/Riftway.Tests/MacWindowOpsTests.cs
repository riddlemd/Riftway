using System.Runtime.InteropServices;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed unsafe class MacWindowOpsTests
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [Fact]
    public void AWindowNoProcessOwnsHasNoPid()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(0, MacWindow.WindowOwnerPid(0));
        Assert.Equal(0, MacWindow.WindowOwnerPid(0xFFFFFFF0));
    }

    [Fact]
    public void AnOnScreenWindowHasItsOwnersPid()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // The first on-screen window and its owner, read from the window list directly.
        var cg = NativeLibrary.Load(CoreGraphics);
        var cf = NativeLibrary.Load(CoreFoundation);
        var list = ((delegate* unmanaged<uint, uint, nint>)NativeLibrary.GetExport(cg, "CGWindowListCopyWindowInfo"))(1, 0);
        var at = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(cf, "CFArrayGetValueAtIndex");
        var get = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(cf, "CFDictionaryGetValue");
        var number = (delegate* unmanaged<nint, nint, long*, byte>)NativeLibrary.GetExport(cf, "CFNumberGetValue");
        var count = ((delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(cf, "CFArrayGetCount"))(list);
        if (count == 0) return;
        var entry = at(list, 0);
        long id = 0, pid = 0;
        // kCFNumberSInt64Type.
        number(get(entry, *(nint*)NativeLibrary.GetExport(cg, "kCGWindowNumber")), 4, &id);
        number(get(entry, *(nint*)NativeLibrary.GetExport(cg, "kCGWindowOwnerPID")), 4, &pid);
        ((delegate* unmanaged<nint, void>)NativeLibrary.GetExport(cf, "CFRelease"))(list);
        Assert.True(pid > 0);
        Assert.Equal((int)pid, MacWindow.WindowOwnerPid((uint)id));
    }
}
