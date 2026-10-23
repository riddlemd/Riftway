using System.Runtime.Versioning;
using System.Xml.Linq;
using Oriel.Cef.Platform;

// Each AppKit call runs inside a lambda behind an OperatingSystem.IsMacOS() check the analyzer cannot follow.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>Why <see cref="OrielApp.MoveToApplicationsFolder"/> asks the conflict handler. Electron's conflict types.</summary>
public enum BundleMoverConflictType
{
    /// <summary>An app of the same name is already in the Applications folder. Electron's <c>exists</c>.</summary>
    Exists,

    /// <summary>That app is also running. Electron's <c>existsAndRunning</c>.</summary>
    ExistsAndRunning,
}

/// <summary>Options for <see cref="OrielApp.MoveToApplicationsFolder"/>.</summary>
public sealed record MoveToApplicationsFolderOptions
{
    /// <summary>Asked before the app in the way is trashed, or before a running one is brought to the front and this app
    /// quits: false stops the move, true or null lets it go on. Electron's <c>conflictHandler</c>.</summary>
    public Func<BundleMoverConflictType, bool?>? ConflictHandler { get; init; }
}

/// <summary>The OS operations <see cref="OrielApp.MoveBundle"/> makes; a seam for tests.</summary>
internal interface IBundleMoverOps
{
    string BundlePath();
    bool IsInApplicationsFolder(string bundlePath);
    string? ContainingDiskImageDevice(string bundlePath);
    string LocalApplicationsDirectory();
    bool IsWritable(string path);
    bool Exists(string path);
    void ActivateApp();
    AuthorizedInstallResult AuthorizedInstall(string source, string destination);
    bool IsRunning(string bundlePath);
    void OpenAndWait(string path);
    bool Trash(string path);
    bool Copy(string source, string destination);
    bool DeleteOrTrash(string path);
    void RunShell(string script);
    int ProcessId { get; }
}

[SupportedOSPlatform("macos")]
internal sealed class MacBundleMoverOps : IBundleMoverOps
{
    internal static readonly MacBundleMoverOps Instance = new();

    public string BundlePath() => MacBundleMover.BundlePath();

    public bool IsInApplicationsFolder(string bundlePath)
        => OrielApp.InApplicationsFolder(OrielApp.RealPath(bundlePath) ?? bundlePath, MacApp.ApplicationDirectories());

    public string? ContainingDiskImageDevice(string bundlePath)
        => MacBundleMover.MountDevice(bundlePath) is { } device ? OrielApp.DiskImageDevice(MacBundleMover.HdiutilInfo(), device) : null;

    public string LocalApplicationsDirectory() => MacBundleMover.LocalApplicationsDirectory();

    public bool IsWritable(string path) => MacBundleMover.IsWritable(path);

    public bool Exists(string path) => MacBundleMover.Exists(path);

    public void ActivateApp() => MacBundleMover.ActivateApp();

    public AuthorizedInstallResult AuthorizedInstall(string source, string destination) => MacBundleMover.AuthorizedInstall(source, destination);

    public bool IsRunning(string bundlePath) => MacBundleMover.IsRunning(bundlePath);

    public void OpenAndWait(string path) => MacBundleMover.OpenAndWait(path);

    public bool Trash(string path) => MacBundleMover.Trash(path);

    public bool Copy(string source, string destination) => MacBundleMover.Copy(source, destination);

    public bool DeleteOrTrash(string path) => MacBundleMover.DeleteOrTrash(path);

    public void RunShell(string script) => MacBundleMover.RunShell(script);

    public int ProcessId => Environment.ProcessId;
}

public sealed partial class OrielApp
{
    /// <summary>Moves the app bundle into <c>/Applications</c> (asking for an administrator's password when it is not
    /// writable), trashes the original unless it ran from a disk image (which is detached), and relaunches from the new
    /// place as this app quits. Already in an Applications folder, does nothing and returns true. Electron's
    /// <c>app.moveToApplicationsFolder()</c>.</summary>
    /// <returns>True when moved (or already in place), or when a running copy was brought to the front instead; false
    /// when the conflict handler stopped it.</returns>
    /// <exception cref="InvalidOperationException">The move failed, with Electron's message.</exception>
    /// <exception cref="PlatformNotSupportedException">Not on macOS, where Electron has no such method.</exception>
    public bool MoveToApplicationsFolder(MoveToApplicationsFolderOptions? options = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("moveToApplicationsFolder is macOS only.");
        return OnMacMain(() => MoveBundle(MacBundleMoverOps.Instance, options?.ConflictHandler, Quit));
    }

    /// <summary>Electron's <c>ElectronBundleMover::Move</c>.</summary>
    internal static bool MoveBundle(IBundleMoverOps ops, Func<BundleMoverConflictType, bool?>? conflictHandler, Action quit)
    {
        var bundlePath = ops.BundlePath();
        if (ops.IsInApplicationsFolder(bundlePath)) return true;

        var diskImageDevice = ops.ContainingDiskImageDevice(bundlePath);
        var applicationsDirectory = ops.LocalApplicationsDirectory();
        var bundleName = Path.GetFileName(bundlePath);
        // macOS paths: Path.Combine would join with a backslash when the flow runs (as in tests) on Windows.
        var destinationPath = applicationsDirectory.TrimEnd('/') + "/" + bundleName;
        var needAuthorization = !ops.IsWritable(applicationsDirectory) || (ops.Exists(destinationPath) && !ops.IsWritable(destinationPath));

        // Works around focus trouble with the "downloaded from the internet" dialog.
        ops.ActivateApp();

        if (needAuthorization)
        {
            switch (ops.AuthorizedInstall(bundlePath, destinationPath))
            {
                case AuthorizedInstallResult.Canceled: throw new InvalidOperationException("User rejected the authorization request");
                case AuthorizedInstallResult.Failed: throw new InvalidOperationException("Failed to copy to applications directory even with authorization");
            }
        }
        else
        {
            if (ops.Exists(destinationPath))
            {
                if (ops.IsRunning(destinationPath))
                {
                    if (conflictHandler?.Invoke(BundleMoverConflictType.ExistsAndRunning) == false) return false;
                    ops.OpenAndWait(destinationPath);
                    quit();
                    return true;
                }
                if (conflictHandler?.Invoke(BundleMoverConflictType.Exists) == false) return false;
                if (!ops.Trash(destinationPath)) throw new InvalidOperationException("Failed to delete existing application");
            }
            if (!ops.Copy(bundlePath, destinationPath)) throw new InvalidOperationException("Failed to copy current bundle to the applications folder");
        }

        // The original stays where it cannot be removed, as on a network volume; that is not an error.
        if (diskImageDevice is null) ops.DeleteOrTrash(bundlePath);
        ops.RunShell(RelaunchScript(ops.ProcessId, destinationPath));
        if (diskImageDevice is not null) ops.RunShell($"(/bin/sleep 5 && /usr/bin/hdiutil detach {ShellQuoted(diskImageDevice)}) &");
        quit();
        return true;
    }

    /// <summary>Electron's <c>ShellQuotedString</c>.</summary>
    internal static string ShellQuoted(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Electron's <c>Relaunch</c> script: waits for this process to end, clears the quarantine attribute so the
    /// warning is not shown twice, then opens the moved app.</summary>
    internal static string RelaunchScript(int pid, string destinationPath)
    {
        var quoted = ShellQuoted(destinationPath);
        return $"(while /bin/kill -0 {pid} >&/dev/null; do /bin/sleep 0.1; done; /usr/bin/xattr -d -r com.apple.quarantine {quoted}; /usr/bin/open {quoted}) &";
    }

    /// <summary>Electron's <c>ContainingDiskImageDevice</c> after <c>statfs</c>: <paramref name="device"/> if an attached
    /// image in <c>hdiutil info -plist</c> lists it as a <c>dev-entry</c>; null when not, and as soon as an entry of the
    /// wrong shape is met.</summary>
    internal static string? DiskImageDevice(string hdiutilPlist, string device)
    {
        if (ParsePlist(hdiutilPlist) is not Dictionary<string, object?> info || !info.TryGetValue("images", out var imagesValue)
            || imagesValue is not List<object?> images)
            return null;
        foreach (var image in images)
        {
            if (image is not Dictionary<string, object?> imageDict || !imageDict.TryGetValue("system-entities", out var entitiesValue)
                || entitiesValue is not List<object?> entities)
                return null;
            foreach (var entity in entities)
            {
                if (entity is not Dictionary<string, object?> entityDict || !entityDict.TryGetValue("dev-entry", out var dev) || dev is not string devEntry)
                    return null;
                if (devEntry == device) return device;
            }
        }
        return null;
    }

    // An XML property list as dictionaries, lists, strings and booleans (other values as their elements); null if it is not one.
    private static object? ParsePlist(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            return root?.Name == "plist" && root.Elements().FirstOrDefault() is { } top ? PlistValue(top) : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static object? PlistValue(XElement e) => e.Name.LocalName switch
    {
        "dict" => e.Elements().Chunk(2).Where(p => p.Length == 2 && p[0].Name == "key")
            .GroupBy(p => p[0].Value).ToDictionary(g => g.Key, g => PlistValue(g.Last()[1])),
        "array" => e.Elements().Select(PlistValue).ToList(),
        "string" => e.Value,
        "true" => true,
        "false" => false,
        // integer, real, date, data: whatever they hold, never an NSString.
        _ => e,
    };
}
