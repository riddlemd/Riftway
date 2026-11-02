using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>The desktop environment as Chromium's <c>base::nix::GetDesktopEnvironment</c> reads it from the environment.</summary>
public enum LinuxDesktopEnvironment
{
    Other,
    Cinnamon,
    Deepin,
    Gnome,
    Kde3,
    Kde4,
    Kde5,
    Kde6,
    Pantheon,
    Unity,
    Xfce,
    Ukui,
    Lxqt,
    Cosmic,
}

/// <summary>Electron's Linux shell operations, as Electron's <c>platform_util_linux.cc</c> does them: <c>xdg-open</c>,
/// <c>xdg-email</c> and the trash tool started the way Chromium's <c>base::LaunchProcess</c> starts a child.</summary>
[SupportedOSPlatform("linux")]
public static unsafe partial class LinuxShellOps
{
    private const string LibC = "libc.so.6";
    private const int EIntr = 4, EAgain = 11, ENoMem = 12, ENoSys = 38;
    private const short SpawnSetSigDef = 0x04, SpawnSetSigMask = 0x08;
    // ExecFailed: the child ran but never reached exec, which Chromium's child reports as exit code 127.
    private const int LaunchFailed = -1, ExecFailed = 0;

    /// <summary>Electron's <c>OpenExternal</c>: <c>xdg-email</c> for a <c>mailto:</c> spec, else <c>xdg-open</c>, not
    /// waited for; null once started, else <c>Failed to open path</c>.</summary>
    public static string? OpenExternal(string spec)
    {
        var argv = spec.StartsWith("mailto:", StringComparison.Ordinal) ? new[] { "xdg-email", spec } : ["xdg-open", spec];
        return XdgUtil(argv, "", waitForExit: false).Success ? null : "Failed to open path";
    }

    /// <summary>Electron's <c>OpenPath</c>: <c>xdg-open</c> on the path from its parent folder, not waited for; "" once
    /// started, else <c>Failed to launch xdg-open</c>.</summary>
    public static string OpenPath(string path) => XdgUtil(["xdg-open", path], LinuxShellRules.DirName(path), waitForExit: false).Message;

    /// <summary>Electron's <c>OpenFolder</c>: <c>xdg-open .</c> from the folder's parent, when the folder exists.</summary>
    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        _ = XdgUtil(["xdg-open", "."], LinuxShellRules.DirName(folder), waitForExit: false);
    }

    /// <summary>Electron's <c>MoveItemToTrash</c>: the tool <see cref="LinuxShellRules.TrashArgv"/> picks, waited for;
    /// true when it exits with 0.</summary>
    public static bool MoveItemToTrash(string path) => XdgUtil(LinuxShellRules.TrashArgv(path, GetEnv), "", waitForExit: true).Success;

    /// <summary>Electron's <c>Beep</c>: <c>gdk_display_beep</c> on the default display, from GTK 4's library when Chromium
    /// loaded GTK 4, else GDK 3's; nothing without a display. Call on CEF's UI thread.</summary>
    public static void Beep()
    {
        var gtk4 = File.ReadAllText("/proc/self/maps").Contains("libgtk-4.so", StringComparison.Ordinal);
        if (!NativeLibrary.TryLoad(gtk4 ? "libgtk-4.so.1" : "libgdk-3.so.0", out var gdk)) return;
        var getDefault = (delegate* unmanaged<nint>)NativeLibrary.GetExport(gdk, "gdk_display_get_default");
        var beep = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(gdk, "gdk_display_beep");
        var display = getDefault();
        if (display != 0) beep(display);
    }

    /// <summary>A variable of the native environment, which .NET's <see cref="Environment"/> does not see change.</summary>
    public static string? GetEnv(string name)
    {
        fixed (byte* n = Encoding.UTF8.GetBytes(name + "\0"))
        {
            var value = getenv(n);
            return value == null ? null : Marshal.PtrToStringUTF8((nint)value);
        }
    }

    // Electron's XDGUtil. focus_launched_process only adds XDG_ACTIVATION_TOKEN, which needs a Wayland token creator.
    private static (bool Success, string Message) XdgUtil(string[] argv, string workingDirectory, bool waitForExit)
    {
        var pid = LaunchProcess(argv, workingDirectory);
        if (pid == LaunchFailed) return (false, "Failed to launch " + argv[0]);
        if (waitForExit)
        {
            var exitCode = pid == ExecFailed ? 127 : WaitForExit(pid);
            return (exitCode == 0, "");
        }
        if (pid != ExecFailed) EnsureProcessGetsReaped(pid);
        return (true, "");
    }

    // base::LaunchProcess with Electron's options: MM_NOTTTY=1 added to the environment, stdin from /dev/null, every
    // other descriptor closed, signal dispositions and mask reset, then chdir and execvp. glibc reports a child that
    // fails before exec here instead of as exit code 127; that is ExecFailed. Only a failed clone is LaunchFailed.
    private static int LaunchProcess(string[] argv, string workingDirectory)
    {
        var actions = stackalloc byte[256];
        var attr = stackalloc byte[512];
        var signals = stackalloc byte[128];
        var strings = new List<nint>();
        try
        {
            _ = posix_spawn_file_actions_init(actions);
            _ = posix_spawnattr_init(attr);
            fixed (byte* devNull = "/dev/null\0"u8) _ = posix_spawn_file_actions_addopen(actions, 0, devNull, 0 /* O_RDONLY */, 0);
            CloseFrom(actions, 3);
            if (workingDirectory.Length > 0) _ = posix_spawn_file_actions_addchdir_np(actions, Native(workingDirectory, strings));
            _ = sigfillset(signals);
            _ = posix_spawnattr_setsigdefault(attr, signals);
            _ = sigemptyset(signals);
            _ = posix_spawnattr_setsigmask(attr, signals);
            _ = posix_spawnattr_setflags(attr, SpawnSetSigDef | SpawnSetSigMask);

            var args = stackalloc byte*[argv.Length + 1];
            for (var i = 0; i < argv.Length; i++) args[i] = Native(argv[i], strings);
            args[argv.Length] = null;
            var environment = LinuxShellRules.AlterEnvironment(NativeEnvironment(), [("MM_NOTTTY", "1")]);
            var envp = stackalloc byte*[environment.Count + 1];
            for (var i = 0; i < environment.Count; i++) envp[i] = Native(environment[i], strings);
            envp[environment.Count] = null;

            int pid;
            var error = posix_spawnp(&pid, args[0], actions, attr, args, envp);
            return error == 0 ? pid : error is EAgain or ENoMem or ENoSys ? LaunchFailed : ExecFailed;
        }
        finally
        {
            _ = posix_spawnattr_destroy(attr);
            _ = posix_spawn_file_actions_destroy(actions);
            foreach (var s in strings) NativeMemory.Free((void*)s);
        }
    }

    // posix_spawn_file_actions_addclosefrom_np needs glibc 2.34; before that each open descriptor is closed by number.
    private static void CloseFrom(void* actions, int lowest)
    {
        try
        {
            if (posix_spawn_file_actions_addclosefrom_np(actions, lowest) == 0) return;
        }
        catch (EntryPointNotFoundException) { }
        foreach (var entry in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            if (int.TryParse(Path.GetFileName(entry), out var fd) && fd >= lowest) _ = posix_spawn_file_actions_addclose(actions, fd);
    }

    // base::Process::WaitForExit: the exit status, or -1 when a signal ended the child.
    private static int WaitForExit(int pid)
    {
        int status, result;
        do result = waitpid(pid, &status, 0);
        while (result < 0 && Marshal.GetLastPInvokeError() == EIntr);
        if (result < 0) return -1;
        return (status & 0x7f) == 0 ? (status >> 8) & 0xff : -1;
    }

    // base::EnsureProcessGetsReaped: a thread of its own waits the child out.
    private static void EnsureProcessGetsReaped(int pid)
        => new Thread(() => WaitForExit(pid)) { IsBackground = true, Name = "Riftway reaper" }.Start();

    private static List<byte[]> NativeEnvironment()
    {
        var environ = *(byte***)NativeLibrary.GetExport(NativeLibrary.Load(LibC), "environ");
        var lines = new List<byte[]>();
        for (var p = environ; p != null && *p != null; p++)
            lines.Add(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(*p).ToArray());
        return lines;
    }

    private static byte* Native(string text, List<nint> owned) => Native(Encoding.UTF8.GetBytes(text), owned);

    private static byte* Native(byte[] bytes, List<nint> owned)
    {
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        p[bytes.Length] = 0;
        owned.Add((nint)p);
        return p;
    }

    [LibraryImport(LibC)] private static partial byte* getenv(byte* name);
    [LibraryImport(LibC)] private static partial int posix_spawnp(int* pid, byte* file, void* actions, void* attr, byte** argv, byte** envp);
    [LibraryImport(LibC)] private static partial int posix_spawn_file_actions_init(void* actions);
    [LibraryImport(LibC)] private static partial int posix_spawn_file_actions_destroy(void* actions);
    [LibraryImport(LibC)] private static partial int posix_spawn_file_actions_addopen(void* actions, int fd, byte* path, int flags, uint mode);
    [LibraryImport(LibC)] private static partial int posix_spawn_file_actions_addclose(void* actions, int fd);
    [LibraryImport(LibC)] private static partial int posix_spawn_file_actions_addclosefrom_np(void* actions, int from);
    [LibraryImport(LibC)] private static partial int posix_spawn_file_actions_addchdir_np(void* actions, byte* path);
    [LibraryImport(LibC)] private static partial int posix_spawnattr_init(void* attr);
    [LibraryImport(LibC)] private static partial int posix_spawnattr_destroy(void* attr);
    [LibraryImport(LibC)] private static partial int posix_spawnattr_setflags(void* attr, short flags);
    [LibraryImport(LibC)] private static partial int posix_spawnattr_setsigdefault(void* attr, void* signals);
    [LibraryImport(LibC)] private static partial int posix_spawnattr_setsigmask(void* attr, void* signals);
    [LibraryImport(LibC)] private static partial int sigfillset(void* set);
    [LibraryImport(LibC)] private static partial int sigemptyset(void* set);
    [LibraryImport(LibC, SetLastError = true)] private static partial int waitpid(int pid, int* status, int options);
}

/// <summary>The string rules of Electron's Linux shell code and the Chromium helpers it calls: pure code.</summary>
public static class LinuxShellRules
{
    // Chromium's kPathCharmap: control and non-ASCII bytes, space and "#%:<>?[\]^`{|}.
    private static readonly uint[] s_pathCharmap = [0xffffffff, 0xd400002d, 0x78000000, 0xb8000001, 0xffffffff, 0xffffffff, 0xffffffff, 0xffffffff];

    /// <summary>Electron's <c>MoveItemToTrash</c>: <c>ELECTRON_TRASH</c> names the tool, else KDE picks
    /// <c>kioclient5</c> or <c>kioclient</c>; anything else is <c>gio trash</c>.</summary>
    public static string[] TrashArgv(string path, Func<string, string?> getEnv)
    {
        var trash = getEnv("ELECTRON_TRASH") ?? "";
        if (trash.Length == 0)
        {
            trash = DesktopEnvironment(getEnv) switch
            {
                LinuxDesktopEnvironment.Kde4 or LinuxDesktopEnvironment.Kde5 => "kioclient5",
                LinuxDesktopEnvironment.Kde3 or LinuxDesktopEnvironment.Kde6 => "kioclient",
                _ => "",
            };
        }
        return trash switch
        {
            "kioclient5" or "kioclient" => [trash, "move", path, "trash:/"],
            "trash-cli" => ["trash-put", path],
            "gvfs-trash" => ["gvfs-trash", path],
            _ => ["gio", "trash", path],
        };
    }

    /// <summary>Chromium's <c>base::nix::GetDesktopEnvironment</c>: <c>XDG_CURRENT_DESKTOP</c>, then
    /// <c>DESKTOP_SESSION</c>, then the older session variables.</summary>
    public static LinuxDesktopEnvironment DesktopEnvironment(Func<string, string?> getEnv)
    {
        if (getEnv("XDG_CURRENT_DESKTOP") is { } current)
        {
            foreach (var value in current.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                switch (value)
                {
                    case "Unity":
                        return (getEnv("DESKTOP_SESSION") ?? "").Contains("gnome-fallback", StringComparison.Ordinal)
                            ? LinuxDesktopEnvironment.Gnome : LinuxDesktopEnvironment.Unity;
                    case "Deepin": return LinuxDesktopEnvironment.Deepin;
                    case "GNOME": return LinuxDesktopEnvironment.Gnome;
                    case "X-Cinnamon": return LinuxDesktopEnvironment.Cinnamon;
                    case "KDE":
                        return getEnv("KDE_SESSION_VERSION") switch
                        {
                            "5" => LinuxDesktopEnvironment.Kde5,
                            "6" => LinuxDesktopEnvironment.Kde6,
                            _ => LinuxDesktopEnvironment.Kde4,
                        };
                    case "Pantheon": return LinuxDesktopEnvironment.Pantheon;
                    case "XFCE": return LinuxDesktopEnvironment.Xfce;
                    case "UKUI": return LinuxDesktopEnvironment.Ukui;
                    case "LXQt": return LinuxDesktopEnvironment.Lxqt;
                    case "COSMIC": return LinuxDesktopEnvironment.Cosmic;
                }
            }
        }

        var session = getEnv("DESKTOP_SESSION") ?? "";
        switch (session)
        {
            case "deepin": return LinuxDesktopEnvironment.Deepin;
            case "gnome" or "mate": return LinuxDesktopEnvironment.Gnome;
            case "kde4" or "kde-plasma": return LinuxDesktopEnvironment.Kde4;
            case "kde": return getEnv("KDE_SESSION_VERSION") is not null ? LinuxDesktopEnvironment.Kde4 : LinuxDesktopEnvironment.Kde3;
        }
        if (session.Contains("xfce", StringComparison.Ordinal) || session == "xubuntu") return LinuxDesktopEnvironment.Xfce;
        if (session == "ukui") return LinuxDesktopEnvironment.Ukui;
        if (getEnv("GNOME_DESKTOP_SESSION_ID") is not null) return LinuxDesktopEnvironment.Gnome;
        if (getEnv("KDE_FULL_SESSION") is not null)
            return getEnv("KDE_SESSION_VERSION") is not null ? LinuxDesktopEnvironment.Kde4 : LinuxDesktopEnvironment.Kde3;
        return LinuxDesktopEnvironment.Other;
    }

    /// <summary>Chromium's <c>internal::AlterEnvironment</c>: the lines whose name is not changed, in order, then each
    /// change with a value (an empty value unsets).</summary>
    public static List<byte[]> AlterEnvironment(IReadOnlyList<byte[]> environment, IReadOnlyList<(string Name, string Value)> changes)
    {
        var names = changes.Select(c => Encoding.UTF8.GetBytes(c.Name)).ToList();
        var result = new List<byte[]>();
        foreach (var line in environment)
        {
            var end = Array.IndexOf(line, (byte)'=');
            var name = end < 0 ? line : line[..end];
            if (!names.Any(n => n.AsSpan().SequenceEqual(name))) result.Add(line);
        }
        foreach (var (name, value) in changes.OrderBy(c => c.Name, StringComparer.Ordinal))
            if (value.Length > 0) result.Add(Encoding.UTF8.GetBytes(name + "=" + value));
        return result;
    }

    /// <summary>What Electron hands <c>ShowItems</c>: <c>file://</c> and the path's UTF-8 bytes through Chromium's
    /// <c>base::EscapePath</c>, upper-case hex.</summary>
    public static string FileUri(string path)
    {
        var escaped = new StringBuilder("file://");
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            if ((s_pathCharmap[b >> 5] & (1u << (b & 31))) != 0) escaped.Append('%').Append(b.ToString("X2"));
            else escaped.Append((char)b);
        }
        return escaped.ToString();
    }

    /// <summary>Chromium's <c>XdgDesktopPortalRequestPath</c>: the request object a portal call with
    /// <paramref name="token"/> as its <c>handle_token</c> answers on.</summary>
    public static string PortalRequestPath(string sender, string token)
        => $"/org/freedesktop/portal/desktop/request/{(sender.StartsWith(':') ? sender[1..] : sender).Replace('.', '_')}/{token}";

    /// <summary><c>base::FilePath::DirName</c> on POSIX: the parent, keeping <c>/</c> and a leading <c>//</c>, "." when
    /// there is none.</summary>
    public static string DirName(string path)
    {
        var p = StripTrailingSeparators(path);
        var lastSeparator = p.LastIndexOf('/');
        return lastSeparator < 0 ? "." : StripTrailingSeparators(p[..(lastSeparator + 1)]);
    }

    // Two separators at the very start are kept, unless there were more.
    private static string StripTrailingSeparators(string path)
    {
        var lastStripped = -1;
        var length = path.Length;
        for (var pos = path.Length; pos > 1 && path[pos - 1] == '/'; pos--)
        {
            if (pos != 2 || lastStripped == 3 || path[0] != '/')
            {
                length = pos - 1;
                lastStripped = pos;
            }
        }
        return path[..length];
    }
}
