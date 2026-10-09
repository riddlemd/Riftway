using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The AppKit and Foundation calls behind the macOS shell, as Electron's
/// <c>shell/common/platform_util_mac.mm</c> makes them. <see cref="OpenExternal"/>, <see cref="OpenPath"/> and
/// <see cref="ShowItemInFolder"/> belong on the main thread, as Electron <c>DCHECK</c>s.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacShellOps
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string LibC = "/usr/lib/libSystem.B.dylib";
    private const int BlockHasSignature = 1 << 30;
    private const nint PosixPathStyle = 0;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_stackBlock = NativeLibrary.GetExport(NativeLibrary.Load(LibC), "_NSConcreteStackBlock");
    private static readonly MacFileDialog.BlockDescriptor* s_openedDescriptor = NewDescriptor("v@?@@"u8);

    /// <summary><c>platform_util::ShowItemInFolder</c>: NSWorkspace's <c>selectFile:inFileViewerRootedAtPath:</c> with an
    /// empty root; false when it refuses, which Electron only logs.</summary>
    public static bool ShowItemInFolder(string fullPath)
    {
        var workspace = Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8));
        var path = NSString(fullPath);
        return path != 0 && ((delegate* unmanaged<nint, nint, nint, nint, byte>)s_msgSend)(workspace, Sel("selectFile:inFileViewerRootedAtPath:"u8), path, NSString("")) != 0;
    }

    /// <summary><c>platform_util::OpenPath</c>: "" when NSWorkspace's <c>openURL:</c> takes the file URL, else
    /// Electron's <c>Failed to open path</c>, or <c>Invalid path</c> when no URL can be made.</summary>
    public static string OpenPath(string path)
    {
        var url = FileUrl(path);
        if (url == 0) return "Invalid path";
        try
        {
            var workspace = Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8));
            return ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(workspace, Sel("openURL:"u8), url) != 0 ? "" : "Failed to open path";
        }
        finally { CFRelease(url); }
    }

    /// <summary><c>platform_util::OpenExternal</c> from <c>NSURLWithGURL</c> on: <paramref name="urlString"/> is the
    /// escaped spec <c>NSURLWithGURL</c> hands <c>URLWithString:</c>. <paramref name="done"/> gets "" or Electron's error
    /// text, possibly on another thread, and only once.</summary>
    public static void OpenExternal(string urlString, bool activate, Action<string> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        var url = Send(Class("NSURL"u8), Sel("URLWithString:"u8), NSString(urlString));
        if (url == 0)
        {
            done("Invalid URL");
            return;
        }

        var workspace = Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8));
        // Without a handler macOS Tahoe would put up its own dialog.
        if (Send(workspace, Sel("URLForApplicationToOpenURL:"u8), url) == 0)
        {
            done("No application found to open URL");
            return;
        }

        var configuration = Send(Class("NSWorkspaceOpenConfiguration"u8), Sel("configuration"u8));
        ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(configuration, Sel("setActivates:"u8), activate ? (byte)1 : (byte)0);
        var block = new MacFileDialog.Block
        {
            Isa = s_stackBlock,
            Flags = BlockHasSignature,
            Invoke = (nint)(delegate* unmanaged<MacFileDialog.Block*, nint, nint, void>)&OnOpened,
            Descriptor = s_openedDescriptor,
            Context = GCHandle.ToIntPtr(GCHandle.Alloc(done)),
        };
        ((delegate* unmanaged<nint, nint, nint, nint, MacFileDialog.Block*, void>)s_msgSend)(workspace, Sel("openURL:configuration:completionHandler:"u8), url, configuration, &block);
    }

    // AppKit copies the stack block and calls the copy once, on a queue of its own.
    [UnmanagedCallersOnly]
    private static void OnOpened(MacFileDialog.Block* block, nint app, nint error)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        var done = (Action<string>)handle.Target!;
        handle.Free();
        try { done(error != 0 ? "Failed to open URL" : ""); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in openExternal's completion: {e}"); }
    }

    /// <summary><c>MoveItemToTrashWithError</c> without the delete fallback (Electron's <c>PlatformTrashItem</c>): null
    /// when NSFileManager's <c>trashItemAtURL:resultingItemURL:error:</c> succeeds, else the error's
    /// <c>localizedDescription</c>, or <c>Invalid file path: </c> and the path when no URL can be made. Any thread.</summary>
    public static string? Trash(string path)
    {
        var url = FileUrl(path);
        if (url == 0) return "Invalid file path: " + path;
        try
        {
            nint error = 0;
            var manager = Send(Class("NSFileManager"u8), Sel("defaultManager"u8));
            var trashed = ((delegate* unmanaged<nint, nint, nint, nint, nint*, byte>)s_msgSend)(manager, Sel("trashItemAtURL:resultingItemURL:error:"u8), url, 0, &error) != 0;
            if (trashed) return null;
            return (error == 0 ? null : Read(Send(error, Sel("localizedDescription"u8)))) ?? "";
        }
        finally { CFRelease(url); }
    }

    /// <summary><c>NSBeep()</c>.</summary>
    public static void Beep() => NSBeep();

    /// <summary><c>realpath(3)</c>, which <c>base::MakeAbsoluteFilePath</c> is on macOS; null when it fails.</summary>
    public static string? RealPath(string path)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(path);
        var resolved = realpath(utf8, 0);
        Marshal.FreeCoTaskMem(utf8);
        if (resolved == 0) return null;
        try { return Marshal.PtrToStringUTF8(resolved); }
        finally { free(resolved); }
    }

    // base::apple::FilePathToNSURL: nothing for an empty path, else a POSIX-style CFURL that is not a directory.
    private static nint FileUrl(string path)
    {
        if (path.Length == 0) return 0;
        var utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var text = CFStringCreateWithFileSystemRepresentation(0, utf8);
            if (text == 0) return 0;
            try { return CFURLCreateWithFileSystemPath(0, text, PosixPathStyle, 0); }
            finally { CFRelease(text); }
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static MacFileDialog.BlockDescriptor* NewDescriptor(ReadOnlySpan<byte> signature)
    {
        var descriptor = (MacFileDialog.BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(MacFileDialog.BlockDescriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(MacFileDialog.Block);
        var copy = (byte*)NativeMemory.Alloc((nuint)signature.Length + 1);
        signature.CopyTo(new Span<byte>(copy, signature.Length));
        copy[signature.Length] = 0;
        descriptor->Signature = copy;
        return descriptor;
    }

    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Send(Class("NSString"u8), Sel("stringWithUTF8String:"u8), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static string? Read(nint nsString) => nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"u8)));

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

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(AppKit)] private static extern void NSBeep();
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithFileSystemRepresentation(nint allocator, nint buffer);
    [DllImport(CoreFoundation)] private static extern nint CFURLCreateWithFileSystemPath(nint allocator, nint filePath, nint pathStyle, byte isDirectory);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
    [DllImport(LibC)] private static extern nint realpath(nint path, nint resolved);
    [DllImport(LibC)] private static extern void free(nint p);
}
