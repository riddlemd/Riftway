using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Oriel;
using Oriel.Cef.Platform;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// End-to-end shell check on Linux, driven from a worker thread once the app is ready. Fake xdg-open, xdg-email and gio
// scripts first on PATH log what they were started with; a fake portal and a fake FileManager1 on the session bus record
// what they were asked. The exit code is the number of failures (-1 when the run never finishes).
if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("RESULT: Linux only");
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), $"oriel-shell-{Environment.ProcessId}");
var fakes = Path.Combine(root, "bin");
var logs = Path.Combine(root, "log");
var exits = Path.Combine(root, "exit");
var items = Path.Combine(root, "items");
foreach (var dir in (string[])[fakes, logs, exits, items]) Directory.CreateDirectory(dir);
const string Script = """
    #!/bin/sh
    log="$ORIEL_SHELL_LOG/$(basename "$0").$$"
    {
      for a in "$@"; do printf 'arg=%s\n' "$a"; done
      printf 'cwd=%s\n' "$(pwd -P)"
      printf 'MM_NOTTTY=%s\n' "${MM_NOTTTY-unset}"
      printf 'stdin=%s\n' "$(readlink /proc/self/fd/0)"
      if [ -e "/proc/$$/fd/$ORIEL_LEAK_FD" ]; then echo leak=yes; else echo leak=no; fi
    } > "$log.tmp"
    mv "$log.tmp" "$log"
    code=0
    if [ -f "$ORIEL_SHELL_EXITS/$(basename "$0")" ]; then code=$(cat "$ORIEL_SHELL_EXITS/$(basename "$0")"); fi
    exit "$code"

    """;
foreach (var tool in (string[])["xdg-open", "xdg-email", "gio"])
{
    var path = Path.Combine(fakes, tool);
    File.WriteAllText(path, Script);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
var originalPath = Native.GetEnv("PATH") ?? "/usr/bin:/bin";
Native.SetEnv("PATH", fakes + ":" + originalPath);
Native.SetEnv("ORIEL_SHELL_LOG", logs);
Native.SetEnv("ORIEL_SHELL_EXITS", exits);
// A descriptor without close-on-exec: Chromium's launch closes it in the child anyway.
Native.SetEnv("ORIEL_LEAK_FD", Native.OpenDevNull().ToString());
// The app's own stdin is a pipe, so a child that inherited it would not see /dev/null.
Native.PipeToStdin();

// Before the app, as WindowTest starts it, so the portal is there when the shell first looks.
using var portal = Oriel.Samples.FakePortal.Start();
var app = OrielApp.Create("Oriel.ShellTest");
var failures = -1;
var results = new List<string>();
var seen = new HashSet<string>();

void Check(string name, bool pass, object? detail = null)
{
    var line = $"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]";
    results.Add(line);
    Console.WriteLine(line);
}

static bool Eventually(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return true;
        Thread.Sleep(50);
    }
    return condition();
}

// The next log a fake tool wrote that has not been read yet, or null.
ToolLog? Next(string tool, TimeSpan wait)
{
    ToolLog? found = null;
    Eventually(() =>
    {
        var file = Directory.GetFiles(logs, tool + ".*").Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal) && !seen.Contains(f)).Order().FirstOrDefault();
        if (file is null) return false;
        seen.Add(file);
        found = ToolLog.Parse(File.ReadAllLines(file));
        return true;
    }, wait);
    return found;
}

ToolLog? NextOpen() => Next("xdg-open", TimeSpan.FromSeconds(10));
bool NoOpenWithin(double seconds) => Next("xdg-open", TimeSpan.FromSeconds(seconds)) is null;

static async Task<Exception?> Throws(Func<Task> act)
{
    try { await act(); return null; }
    catch (Exception e) { return e; }
}

void SetExit(string tool, int? code)
{
    var file = Path.Combine(exits, tool);
    if (code is { } c) File.WriteAllText(file, c.ToString());
    else File.Delete(file);
}

async Task RunAsync()
{
    var cwd = Directory.GetCurrentDirectory();
    var folder = Path.Combine(items, "dir one");
    Directory.CreateDirectory(folder);
    var file = Path.Combine(folder, "café.txt");
    File.WriteAllText(file, "x");

    // openExternal: xdg-open with the canonical spec, started from the app's directory, not waited for.
    var error = await Throws(() => Shell.OpenExternalAsync("https://example.com/a b?q=1"));
    var log = NextOpen();
    Check("OpenExternalAsync resolves once xdg-open starts", error is null, error?.Message);
    Check("OpenExternalAsync hands xdg-open the canonical spec", log?.Args is ["https://example.com/a%20b?q=1"], log?.Text);
    Check("xdg-open starts in the app's directory", log?.Cwd == cwd, $"{log?.Cwd} vs {cwd}");
    Check("xdg-open gets MM_NOTTTY=1 (Electron's mailcap hint)", log?.MmNotTty == "1", log?.MmNotTty);
    Check("xdg-open's stdin is /dev/null (base::LaunchProcess)", log?.Stdin == "/dev/null", log?.Stdin);
    Check("xdg-open inherits no other descriptor (base::LaunchProcess)", log?.Leak == "no", log?.Leak);

    error = await Throws(() => Shell.OpenExternalAsync("mailto:someone@example.com"));
    var mail = Next("xdg-email", TimeSpan.FromSeconds(10));
    Check("OpenExternalAsync sends mailto: to xdg-email", error is null && mail?.Args is ["mailto:someone@example.com"], error?.Message ?? mail?.Text);
    Check("mailto: does not also start xdg-open", NoOpenWithin(0.5));

    // openPath: xdg-open with the path, from its folder.
    var opened = await Shell.OpenPathAsync(file);
    log = NextOpen();
    Check("OpenPathAsync resolves to \"\"", opened == "", opened);
    Check("OpenPathAsync starts xdg-open on the path from its folder", log?.Args.SequenceEqual([file]) == true && log.Cwd == folder, log?.Text);

    // With no xdg-open at all the child fails at exec, which Chromium never sees: both still succeed.
    if (originalPath.Split(':').Any(d => File.Exists(Path.Combine(d, "xdg-open"))))
        Console.WriteLine("SKIP  a missing xdg-open: this system has a real one");
    else
    {
        File.Move(Path.Combine(fakes, "xdg-open"), Path.Combine(root, "xdg-open.off"));
        try
        {
            error = await Throws(() => Shell.OpenExternalAsync("https://example.com/"));
            Check("OpenExternalAsync resolves without an xdg-open to exec", error is null, error?.Message);
            opened = await Shell.OpenPathAsync(file);
            Check("OpenPathAsync gives \"\" without an xdg-open to exec", opened == "", opened);
        }
        finally { File.Move(Path.Combine(root, "xdg-open.off"), Path.Combine(fakes, "xdg-open")); }
    }

    // trashItem: gio trash, waited for; any failure is one message.
    var victim = Path.Combine(items, "victim.txt");
    File.WriteAllText(victim, "x");
    error = await Throws(() => Shell.TrashItemAsync(victim));
    var gio = Next("gio", TimeSpan.Zero);
    Check("TrashItemAsync resolves when gio exits with 0", error is null, error?.Message);
    Check("TrashItemAsync waits for `gio trash <path>`", gio?.Args.SequenceEqual(["trash", victim]) == true, gio?.Text ?? "no log when it resolved");
    Check("the trash tool starts like xdg-open (cwd, MM_NOTTTY, stdin)", gio is { MmNotTty: "1", Stdin: "/dev/null", Leak: "no" } && gio.Cwd == cwd, gio?.Text);
    SetExit("gio", 2);
    error = await Throws(() => Shell.TrashItemAsync(victim));
    _ = Next("gio", TimeSpan.Zero);
    Check("TrashItemAsync rejects when gio fails: Failed to move item to trash", error is IOException { Message: "Failed to move item to trash" }, error?.Message);
    SetExit("gio", null);
    File.Move(Path.Combine(fakes, "gio"), Path.Combine(root, "gio.off"));
    try
    {
        error = await Throws(() => Shell.TrashItemAsync(victim));
        Check("TrashItemAsync rejects without a gio to exec", error is IOException { Message: "Failed to move item to trash" }, error?.Message);
    }
    finally { File.Move(Path.Combine(root, "gio.off"), Path.Combine(fakes, "gio")); }

    if (originalPath.Split(':').FirstOrDefault(d => File.Exists(Path.Combine(d, "gio"))) is null)
        Console.WriteLine("SKIP  a real `gio trash`: gio is not installed (libglib2.0-bin)");
    else
    {
        Native.SetEnv("PATH", originalPath);
        try
        {
            var real = Path.Combine(items, $"real-{Environment.ProcessId}.txt");
            File.WriteAllText(real, "x");
            error = await Throws(() => Shell.TrashItemAsync(real));
            var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/Trash");
            var name = Path.GetFileName(real);
            Check("TrashItemAsync moves a file to the XDG trash with a .trashinfo", error is null && !File.Exists(real)
                && File.Exists(Path.Combine(trash, "files", name)) && File.Exists(Path.Combine(trash, "info", name + ".trashinfo")), error?.Message);
        }
        finally { Native.SetEnv("PATH", fakes + ":" + originalPath); }
    }

    // showItemInFolder: the fake portal is running, so OpenURI.OpenDirectory with the item's descriptor.
    if (portal is null) Check("a session bus for the fake portal", false, Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
    else
    {
        Shell.ShowItemInFolder(file);
        Check("ShowItemInFolder asks the portal's OpenDirectory", Eventually(() => portal.OpenDirectories.Count == 1, TimeSpan.FromSeconds(10)), portal.OpenDirectories.Count);
        var call = portal.OpenDirectories.LastOrDefault();
        Check("OpenDirectory gets the item's descriptor and no parent window", call is { ParentWindow: "" } && call.Target == file, call?.Target);
        var token = call?.Options.GetValueOrDefault("handle_token") ?? "";
        Check("OpenDirectory options: an empty activation_token and a 128-bit handle_token", call?.Options.GetValueOrDefault("activation_token") == ""
            && Regex.IsMatch(token, "^[0-9A-F]{32}$") && call.Options.Count == 2, string.Join(",", call?.Options ?? []));
        Check("a successful Response opens nothing else", NoOpenWithin(1.5));

        bool FallsBack(string what, Action arrange, string path)
        {
            arrange();
            var before = portal.OpenDirectories.Count;
            Shell.ShowItemInFolder(path);
            var fallback = NextOpen();
            Check(what, portal.OpenDirectories.Count == before + 1 && fallback?.Args is ["."] && fallback.Cwd == items, fallback?.Text);
            return fallback is not null;
        }
        // Electron's OpenFolder(dir) runs `xdg-open .` from the folder's parent: the item's grandparent.
        FallsBack("a Response of 2 opens the parent folder's parent with `xdg-open .`", () => portal.OpenDirectoryResponse = 2, file);
        portal.OpenDirectoryResponse = 1;
        var asked = portal.OpenDirectories.Count;
        Shell.ShowItemInFolder(file);
        Check("a Response of 1 (cancelled by the user) opens nothing else", Eventually(() => portal.OpenDirectories.Count == asked + 1, TimeSpan.FromSeconds(10)) && NoOpenWithin(1.5));
        FallsBack("an OpenDirectory error falls back to `xdg-open .`", () => { portal.OpenDirectoryResponse = 0; portal.OpenDirectoryFails = true; }, file);
        portal.OpenDirectoryFails = false;
        FallsBack("a portal answering on its own request path is listened to there", () => { portal.OpenDirectoryIgnoresToken = true; portal.OpenDirectoryResponse = 2; }, file);
        portal.OpenDirectoryIgnoresToken = false;
        portal.OpenDirectoryResponse = 0;

        var count = portal.OpenDirectories.Count;
        Shell.ShowItemInFolder(Path.Combine(folder, "missing.txt"));
        log = NextOpen();
        Check("an item that cannot be opened skips the portal and falls back", log?.Args is ["."] && log.Cwd == items && portal.OpenDirectories.Count == count, log?.Text);
        Shell.ShowItemInFolder(Path.Combine(items, "no such folder", "missing.txt"));
        Check("with no parent folder either, nothing opens", NoOpenWithin(1.5) && portal.OpenDirectories.Count == count);
    }

    // Without a portal: FileManager1.ShowItems with the item's file URI. A helper that looks for a portal under a name
    // nobody owns, as Chromium's CreateWithPortalServiceName.
    using (var manager = FakeFileManager.Start(LinuxShowItemHelper.FileManagerService))
    {
        if (manager is null) Check("a session bus for the fake file manager", false);
        else
        {
            var helper = new LinuxShowItemHelper(portalService: "org.oriel.test.NoPortal");
            helper.ShowItemInFolder(file);
            Check("without a portal, ShowItems gets the escaped file URI and an empty startup id", Eventually(() => !manager.ShowItems.IsEmpty, TimeSpan.FromSeconds(10))
                && manager.ShowItems.First() is { Uris: [var uri], StartupId: "" } && uri == "file://" + folder.Replace(" ", "%20") + "/caf%C3%A9.txt",
                string.Join(",", manager.ShowItems.FirstOrDefault().Uris ?? []));
            Check("a successful ShowItems opens nothing else", NoOpenWithin(1.5));
            manager.Fails = true;
            helper.ShowItemInFolder(file);
            log = NextOpen();
            Check("a ShowItems error falls back to `xdg-open .`", manager.ShowItems.Count == 2 && log?.Args is ["."] && log.Cwd == items, log?.Text);
            manager.Fails = false;
            if (portal is not null)
            {
                var asked = portal.OpenDirectories.Count;
                new LinuxShowItemHelper().ShowItemInFolder(file);
                Check("with both running, the portal is preferred", Eventually(() => portal.OpenDirectories.Count == asked + 1, TimeSpan.FromSeconds(10))
                    && manager.ShowItems.Count == 2 && NoOpenWithin(1.5), portal.OpenDirectories.Count - asked);
            }
        }
    }

    var neither = new LinuxShowItemHelper("org.oriel.test.NoPortal", "org.oriel.test.NoFileManager");
    neither.ShowItemInFolder(file);
    log = NextOpen();
    Check("with neither service, ShowItemInFolder opens `xdg-open .` from the grandparent", log?.Args is ["."] && log.Cwd == items, log?.Text);

    // beep: gdk_display_beep rings the X bell.
    using (var bell = XBellMonitor.Start())
    {
        if (bell is null) Check("an X display with XKB to hear the bell", false);
        else
        {
            _ = bell.Drain();
            Shell.Beep();
            var rings = 0;
            Check("Beep rings the X bell (XkbBellNotify)", Eventually(() => (rings += bell.Drain()) > 0, TimeSpan.FromSeconds(5)), rings);
        }
    }

    // Electron defines the shortcut methods on Windows only.
    Check("WriteShortcutLink is Windows only", Throws(() => Task.FromResult(Shell.WriteShortcutLink(Path.Combine(items, "a.lnk"), new ShortcutDetails { Target = "/bin/true" }))).Result is PlatformNotSupportedException);
    Check("ReadShortcutLink is Windows only", Throws(() => Task.FromResult(Shell.ReadShortcutLink(Path.Combine(items, "a.lnk")))).Result is PlatformNotSupportedException);
}

app.Ready += (_, _) => _ = Task.Run(async () =>
{
    try { await RunAsync(); }
    catch (Exception e) { Check("run completed", false, e); }
    failures = results.Count(r => r.StartsWith("FAIL", StringComparison.Ordinal));
    app.Quit();
});

_ = Task.Delay(TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("ORIEL_TEST_TIMEOUT") ?? "120"))).ContinueWith(_ =>
{
    Console.WriteLine("FAIL  run did not finish in time");
    Environment.Exit(-1);
}, TaskScheduler.Default);

app.Run();
Console.WriteLine(failures < 0 ? "RESULT: no result" : $"RESULT: {failures} failure(s)");
return failures;

/// <summary>What a fake tool logged about how it was started.</summary>
sealed record ToolLog(string[] Args, string? Cwd, string? MmNotTty, string? Stdin, string? Leak, string Text)
{
    public static ToolLog Parse(string[] lines)
    {
        string? Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        return new ToolLog([.. lines.Where(l => l.StartsWith("arg=", StringComparison.Ordinal)).Select(l => l[4..])],
            Field("cwd"), Field("MM_NOTTTY"), Field("stdin"), Field("leak"), string.Join(" | ", lines));
    }
}

static unsafe partial class Native
{
    public static string? GetEnv(string name) => LinuxShellOps.GetEnv(name);

    // The native environment, where the shell's launches read it; .NET's own copy would not reach them.
    public static void SetEnv(string name, string value)
    {
        fixed (byte* n = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        fixed (byte* v = System.Text.Encoding.UTF8.GetBytes(value + "\0"))
            _ = setenv(n, v, 1);
    }

    public static int OpenDevNull()
    {
        fixed (byte* p = "/dev/null\0"u8) return open(p, 0);
    }

    public static void PipeToStdin()
    {
        var fds = stackalloc int[2];
        if (pipe(fds) == 0) _ = dup2(fds[0], 0);
    }

    [LibraryImport("libc.so.6")] private static partial int pipe(int* fds);
    [LibraryImport("libc.so.6")] private static partial int dup2(int from, int to);
    [LibraryImport("libc.so.6")] private static partial int setenv(byte* name, byte* value, int overwrite);
    [LibraryImport("libc.so.6")] private static partial int open(byte* path, int flags);
}
