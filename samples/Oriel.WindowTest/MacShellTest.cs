using System.Diagnostics;
using System.Runtime.Versioning;
using Oriel;

/// <summary>The shell on macOS, read back from outside Oriel: the Trash folder, NSFileManager in a JXA script, a URL
/// handler applet registered with LaunchServices that records what it is sent, and Finder's selection.</summary>
[SupportedOSPlatform("macos")]
static class MacShellTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oriel-mac-shell-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            await TrashAsync(check, dir);
            await OpenAsync(check, step, dir);
            ShowItem(check, step, dir);
        }
        finally { Directory.Delete(dir, recursive: true); }

        // No read-back for the sound.
        Shell.Beep();
        Check(check, "shortcut links are Windows only", Throws(() => Shell.ReadShortcutLink("/nonexistent.lnk")) is PlatformNotSupportedException);
    }

    private static async Task TrashAsync(MessageBoxTest.CheckFn check, string dir)
    {
        var name = $"oriel-trash-{Guid.NewGuid():N}.txt";
        var victim = Path.Combine(dir, name);
        File.WriteAllText(victim, "oriel");
        var error = await ThrowsAsync(() => Shell.TrashItemAsync(victim));
        var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        string[] inTrash;
        // Reading ~/.Trash needs Full Disk Access for the terminal; without it only the source can be checked.
        try { inTrash = [.. Directory.GetFiles(trash, Path.GetFileNameWithoutExtension(name) + "*")]; }
        catch (UnauthorizedAccessException) { inTrash = ["<~/.Trash unreadable>"]; }
        Check(check, "TrashItemAsync moves the file to ~/.Trash", error is null && !File.Exists(victim) && inTrash.Length == 1, $"{error?.Message} | {string.Join(",", inTrash)}");
        foreach (var file in inTrash.Where(File.Exists)) File.Delete(file);

        // The rejection is NSFileManager's own localizedDescription, as a JXA script gets it independently. A Ref()
        // out-parameter crashes osascript on macOS 26; $() is JXA's NSError** form.
        var missing = Path.Combine(dir, "missing.txt");
        error = await ThrowsAsync(() => Shell.TrashItemAsync(missing));
        var expected = Jxa($"const e = $(); $.NSFileManager.defaultManager.trashItemAtURLResultingItemURLError($.NSURL.fileURLWithPath('{missing}'), $(), e); e.localizedDescription.js");
        Check(check, "TrashItemAsync of a missing file rejects with the system's description", error is IOException && error.Message == expected && expected.Length > 0, $"{error?.Message} | {expected}");
        error = await ThrowsAsync(() => Shell.TrashItemAsync(""));
        Check(check, "TrashItemAsync(\"\") rejects with Electron's Invalid file path", error?.Message == "Invalid file path: ", error?.Message);
    }

    private static async Task OpenAsync(MessageBoxTest.CheckFn check, TimeSpan step, string dir)
    {
        Check(check, "OpenPathAsync of a missing file is Electron's Failed to open path", await Shell.OpenPathAsync(Path.Combine(dir, "missing.txt")) == "Failed to open path");
        Check(check, "OpenPathAsync(\"\") is Electron's Invalid path", await Shell.OpenPathAsync("") == "Invalid path");

        var unknown = $"oriel-unknown-{Guid.NewGuid():N}://x";
        var error = await ThrowsAsync(() => Shell.OpenExternalAsync(unknown));
        Check(check, "OpenExternalAsync with no handler rejects: No application found to open URL", error?.Message == "No application found to open URL", error?.Message);
        error = await ThrowsAsync(() => Shell.OpenExternalAsync("not a url"));
        Check(check, "OpenExternalAsync of a non-URL rejects: Invalid URL", error?.Message == "Invalid URL", error?.Message);

        // A handler applet for a fresh scheme writes each URL it is sent to a file.
        var scheme = $"oriel-test-{Guid.NewGuid():N}"[..24];
        var received = Path.Combine(dir, "received.txt");
        // LaunchServices marks an app in a temporary folder launch-disabled and never binds its schemes.
        var appletDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "oriel-test-" + Path.GetRandomFileName());
        var applet = Path.Combine(appletDir, "OrielUrlHandler.app");
        Directory.CreateDirectory(appletDir);
        Run("osacompile", "-o", applet, "-e", $"on open location u\n do shell script \"printf %s \" & quoted form of u & \" > \" & quoted form of \"{received}\"\nend open location");
        var plist = Path.Combine(applet, "Contents", "Info.plist");
        Run("plutil", "-replace", "CFBundleIdentifier", "-string", "dev.oriel.test." + scheme.Replace('-', '.'), plist);
        Run("plutil", "-insert", "CFBundleURLTypes", "-json", $"[{{\"CFBundleURLName\":\"Oriel test\",\"CFBundleURLSchemes\":[\"{scheme}\"]}}]", plist);
        const string LsRegister = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
        // The plist edits break osacompile's ad-hoc signature.
        Run("codesign", "-f", "-s", "-", applet);
        Run(LsRegister, "-f", applet);
        try
        {
            var front = Jxa("$.NSWorkspace.sharedWorkspace.frontmostApplication.bundleIdentifier.js");
            error = await ThrowsAsync(() => Shell.OpenExternalAsync($"{scheme}://host/a b?q=[1]#f\"g", new OpenExternalOptions { Activate = false }));
            var got = Eventually(() => File.Exists(received), step * 2) ? File.ReadAllText(received) : null;
            Check(check, "OpenExternalAsync hands the handler the URL escaped as NSURLWithGURL does", error is null && got == $"{scheme}://host/a%20b?q=%5B1%5D#f%22g", $"{error?.Message} | {got}");
            Check(check, "Activate=false leaves the frontmost app in front (weak: the applet may already have quit)",
                Jxa("$.NSWorkspace.sharedWorkspace.frontmostApplication.bundleIdentifier.js") == front, front);
        }
        finally
        {
            Run(LsRegister, "-u", applet);
            Run("pkill", "-f", applet);
            if (Directory.Exists(appletDir)) Directory.Delete(appletDir, recursive: true);
        }
    }

    private static void ShowItem(MessageBoxTest.CheckFn check, TimeSpan step, string dir)
    {
        var file = Path.Combine(dir, "shown file.txt");
        File.WriteAllText(file, "oriel");
        Shell.ShowItemInFolder(file);
        // Finder's selection needs the Automation permission for osascript; the detail shows a refusal.
        // Finder answers with the resolved path: /private/var/folders for the temp folder's /var/folders.
        var real = Run("realpath", file);
        string selected = "";
        var found = Eventually(() => (selected = Run("osascript", "-e", "tell application \"Finder\" to get POSIX path of (item 1 of (get selection) as alias)")) == real, step * 2);
        var front = found ? "" : Run("osascript", "-e", "tell application \"Finder\" to get POSIX path of (target of front Finder window as alias)");
        Check(check, "ShowItemInFolder selects the file in Finder", found, $"{selected} | front window: {front}");
        // Only the window ShowItemInFolder opened; any other Finder window is the user's.
        if (found) Run("osascript", "-e", "tell application \"Finder\" to close front window");
    }

    private static void Check(MessageBoxTest.CheckFn check, string name, bool pass, object? detail = null) => check("macOS shell: " + name, pass, detail);

    private static Exception? Throws(Action act)
    {
        try { act(); return null; }
        catch (Exception e) { return e; }
    }

    private static async Task<Exception?> ThrowsAsync(Func<Task> act)
    {
        try { await act(); return null; }
        catch (Exception e) { return e; }
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static string Jxa(string body) => Run("osascript", "-l", "JavaScript", "-e", "ObjC.import('AppKit'); " + body);

    private static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.TrimEnd('\n') : "<" + error.Trim() + ">";
    }
}
