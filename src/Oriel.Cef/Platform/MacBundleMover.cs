using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Oriel.Cef.Platform;

/// <summary>How <see cref="MacBundleMover.AuthorizedInstall"/> ended.</summary>
public enum AuthorizedInstallResult
{
    /// <summary>The destination was replaced by the copy.</summary>
    Installed,

    /// <summary>The user refused the authorization request.</summary>
    Canceled,

    /// <summary>Anything else went wrong.</summary>
    Failed,
}

/// <summary>The OS calls behind Electron's <c>ElectronBundleMover</c> (electron_bundle_mover.mm). Main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacBundleMover
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const nint ApplicationDirectory = 1;
    private const nint LocalDomainMask = 2;
    private const uint MntRootFs = 0x00004000;
    // struct statfs (64-bit inodes): f_flags and f_mntfromname offsets, and its size.
    private const int StatfsFlagsOffset = 64;
    private const int StatfsMountFromOffset = 1112;
    private const int StatfsSize = 2168;
    private const int ErrAuthorizationCanceled = -60006;
    // kAuthorizationFlagInteractionAllowed | ExtendRights | PreAuthorize.
    private const uint RightsFlags = 1 | 2 | 16;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_libSystem = NativeLibrary.Load(LibSystem);
    private static readonly nint s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthorizationItem
    {
        public byte* Name;
        public nuint ValueLength;
        public void* Value;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthorizationRights
    {
        public uint Count;
        public AuthorizationItem* Items;
    }

    /// <summary>The main bundle's path.</summary>
    public static string BundlePath() => WithPool(() => ReadString(Send(Send(Class("NSBundle"u8), Sel("mainBundle"u8)), Sel("bundlePath"u8))));

    /// <summary>The local domain's Applications directory with its symlinks resolved (<c>/Applications</c>).</summary>
    public static string LocalApplicationsDirectory() => WithPool(() =>
    {
        var dirs = NSSearchPathForDirectoriesInDomains(ApplicationDirectory, LocalDomainMask, 1);
        return ReadString(Send(Send(dirs, Sel("lastObject"u8)), Sel("stringByResolvingSymlinksInPath"u8)));
    });

    /// <summary>The device the volume holding <paramref name="bundlePath"/>'s directory is mounted from, or null for the
    /// root volume or a failed <c>statfs</c>.</summary>
    public static string? MountDevice(string bundlePath)
    {
        var directory = WithPool(() => ReadString(Send(NSString(bundlePath), Sel("stringByDeletingLastPathComponent"u8))));
        var buffer = stackalloc byte[StatfsSize];
        var path = Encoding.UTF8.GetBytes(directory + "\0");
        // On x86-64 the 64-bit-inode statfs is a separate symbol.
        var statfs = (delegate* unmanaged<byte*, byte*, int>)NativeLibrary.GetExport(s_libSystem,
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "statfs$INODE64" : "statfs");
        fixed (byte* p = path)
            if (statfs(p, buffer) != 0) return null;
        if ((*(uint*)(buffer + StatfsFlagsOffset) & MntRootFs) != 0) return null;
        return Marshal.PtrToStringUTF8((nint)(buffer + StatfsMountFromOffset));
    }

    /// <summary><c>hdiutil info -plist</c>'s output, or empty when it cannot run.</summary>
    public static string HdiutilInfo()
    {
        try
        {
            using var hdiutil = Process.Start(new ProcessStartInfo("/usr/bin/hdiutil", ["info", "-plist"]) { RedirectStandardOutput = true, UseShellExecute = false });
            if (hdiutil is null) return "";
            var output = hdiutil.StandardOutput.ReadToEnd();
            hdiutil.WaitForExit();
            return output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }

    /// <summary><c>-[NSFileManager isWritableFileAtPath:]</c>.</summary>
    public static bool IsWritable(string path) => WithPool(() => SendBool(FileManager(), Sel("isWritableFileAtPath:"u8), NSString(path)));

    /// <summary><c>-[NSFileManager fileExistsAtPath:]</c>.</summary>
    public static bool Exists(string path) => WithPool(() => SendBool(FileManager(), Sel("fileExistsAtPath:"u8), NSString(path)));

    /// <summary>Activates the app if it is not the active one, as Electron does before the move.</summary>
    public static void ActivateApp() => WithPool(() =>
    {
        var app = Send(Class("NSApplication"u8), Sel("sharedApplication"u8));
        if (!SendBool(app, Sel("isActive"u8))) ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(app, Sel("activateIgnoringOtherApps:"u8), 1);
        return 0;
    });

    /// <summary>Electron's <c>AuthorizedInstall</c>: with an administrator's authorization, <c>rm -rf</c> the destination
    /// app bundle and <c>cp -pR</c> the source there, through the deprecated <c>AuthorizationExecuteWithPrivileges</c>.</summary>
    public static AuthorizedInstallResult AuthorizedInstall(string source, string destination)
    {
        // The destination is removed with root's rm -rf: only ever an app bundle.
        if (Path.GetExtension(destination) != ".app" || destination.Trim().Length == 0 || source.Trim().Length == 0)
            return AuthorizedInstallResult.Failed;
        var security = NativeLibrary.Load(Security);
        var create = (delegate* unmanaged<void*, void*, uint, nint*, int>)NativeLibrary.GetExport(security, "AuthorizationCreate");
        var copyRights = (delegate* unmanaged<nint, AuthorizationRights*, void*, uint, void*, int>)NativeLibrary.GetExport(security, "AuthorizationCopyRights");
        var free = (delegate* unmanaged<nint, uint, int>)NativeLibrary.GetExport(security, "AuthorizationFree");
        nint authorization;
        if (create(null, null, 0, &authorization) != 0) return AuthorizedInstallResult.Failed;
        try
        {
            fixed (byte* right = "system.privilege.admin\0"u8)
            {
                var item = new AuthorizationItem { Name = right };
                var rights = new AuthorizationRights { Count = 1, Items = &item };
                var status = copyRights(authorization, &rights, null, RightsFlags, null);
                if (status == ErrAuthorizationCanceled) return AuthorizedInstallResult.Canceled;
                if (status != 0) return AuthorizedInstallResult.Failed;
            }
            if (!NativeLibrary.TryGetExport(security, "AuthorizationExecuteWithPrivileges", out var executeExport)) return AuthorizedInstallResult.Failed;
            var execute = (delegate* unmanaged<nint, byte*, uint, byte**, void*, int>)executeExport;
            // rm's exit status is ignored, since the destination most likely does not exist.
            if (!RunPrivileged(execute, authorization, "/bin/rm", ["-rf", destination], checkStatus: false)) return AuthorizedInstallResult.Failed;
            if (!RunPrivileged(execute, authorization, "/bin/cp", ["-pR", source, destination], checkStatus: true)) return AuthorizedInstallResult.Failed;
            return AuthorizedInstallResult.Installed;
        }
        finally { free(authorization, 0); }
    }

    private static bool RunPrivileged(delegate* unmanaged<nint, byte*, uint, byte**, void*, int> execute, nint authorization, string tool, string[] arguments, bool checkStatus)
    {
        var handles = new List<GCHandle>();
        try
        {
            var argv = stackalloc byte*[arguments.Length + 1];
            for (var i = 0; i < arguments.Length; i++)
            {
                var handle = GCHandle.Alloc(Encoding.UTF8.GetBytes(arguments[i] + "\0"), GCHandleType.Pinned);
                handles.Add(handle);
                argv[i] = (byte*)handle.AddrOfPinnedObject();
            }
            argv[arguments.Length] = null;
            fixed (byte* path = Encoding.UTF8.GetBytes(tool + "\0"))
                if (execute(authorization, path, 0, argv, null) != 0) return false;
            int status;
            // Any child's exit can be the one collected, as in Electron's mover; CEF's helpers rarely exit here.
            if (wait(&status) == -1 || (status & 0x7f) != 0) return false;
            return !checkStatus || ((status >> 8) & 0xff) == 0;
        }
        finally
        {
            foreach (var handle in handles) handle.Free();
        }
    }

    /// <summary>Whether a running application's bundle is <paramref name="bundlePath"/>, both standardized.</summary>
    public static bool IsRunning(string bundlePath) => WithPool(() =>
    {
        var wanted = ReadString(Send(NSString(bundlePath), Sel("stringByStandardizingPath"u8)));
        var apps = Send(Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8)), Sel("runningApplications"u8));
        for (nint i = 0, n = Send(apps, Sel("count"u8)); i < n; i++)
        {
            var url = Send(SendIndex(apps, Sel("objectAtIndex:"u8), i), Sel("bundleURL"u8));
            if (url != 0 && ReadString(Send(Send(url, Sel("path"u8)), Sel("stringByStandardizingPath"u8))) == wanted) return true;
        }
        return false;
    });

    /// <summary><c>/usr/bin/open</c> on the path, waited for.</summary>
    public static void OpenAndWait(string path)
    {
        using var open = Process.Start(new ProcessStartInfo("/usr/bin/open", [path]) { UseShellExecute = false });
        open?.WaitForExit();
    }

    /// <summary><c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>.</summary>
    public static bool Trash(string path) => WithPool(() =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, byte>)s_msgSend)(FileManager(), Sel("trashItemAtURL:resultingItemURL:error:"u8),
            Send(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(path)), 0, 0) != 0);

    /// <summary><c>-[NSFileManager copyItemAtPath:toPath:error:]</c>.</summary>
    public static bool Copy(string source, string destination) => WithPool(() =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, byte>)s_msgSend)(FileManager(), Sel("copyItemAtPath:toPath:error:"u8),
            NSString(source), NSString(destination), 0) != 0);

    /// <summary><c>removeItemAtPath:error:</c>, else <see cref="Trash"/>.</summary>
    public static bool DeleteOrTrash(string path)
        => WithPool(() => ((delegate* unmanaged<nint, nint, nint, nint, byte>)s_msgSend)(FileManager(), Sel("removeItemAtPath:error:"u8), NSString(path), 0) != 0)
            || Trash(path);

    /// <summary>Runs <paramref name="script"/> with <c>/bin/sh -c</c> and does not wait, as <c>NSTask launchedTaskWithLaunchPath:</c>.</summary>
    public static void RunShell(string script)
    {
        using var _ = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", script]) { UseShellExecute = false });
    }

    private static T WithPool<T>(Func<T> func)
    {
        var pool = objc_autoreleasePoolPush();
        try { return func(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static nint FileManager() => Send(Class("NSFileManager"u8), Sel("defaultManager"u8));

    private static string ReadString(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"u8))) ?? "";

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

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

    private static nint SendIndex(nint receiver, nint selector, nint index) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, index);

    private static bool SendBool(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(receiver, selector) != 0;

    private static bool SendBool(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(receiver, selector, arg) != 0;

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(LibSystem)] private static extern int wait(int* status);
    [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
    private static extern nint NSSearchPathForDirectoriesInDomains(nint directory, nint domainMask, byte expandTilde);
}
