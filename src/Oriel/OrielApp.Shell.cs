using System.ComponentModel;
using Oriel.Cef;
using Oriel.Cef.Platform;

// Each platform call runs behind an OperatingSystem check the analyzer cannot follow into lambdas.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>A file the OS asks the app to open (macOS); Electron's <c>open-file</c> arguments. Set
/// <see cref="CancelEventArgs.Cancel"/> to say the app handles it (Electron's <c>preventDefault()</c>); otherwise AppKit
/// reports that the file could not be opened.</summary>
/// <param name="path">The file.</param>
public sealed class OpenFileEventArgs(string path) : CancelEventArgs
{
    /// <summary>The file.</summary>
    public string Path { get; } = path;
}

/// <summary>A URL the OS asks the app to open (macOS); Electron's <c>open-url</c> arguments.</summary>
/// <param name="Url">The URL.</param>
public sealed record OpenUrlEventArgs(string Url);

/// <summary>The app was reactivated (macOS); Electron's <c>activate</c> arguments.</summary>
/// <param name="HasVisibleWindows">Whether the app had visible windows.</param>
public sealed record ActivateEventArgs(bool HasVisibleWindows);

/// <summary>How the app takes part in the Dock and menu bar (macOS); Electron's <c>setActivationPolicy</c> values.</summary>
public enum ActivationPolicy
{
    /// <summary>An ordinary app with a Dock icon and menu bar. Electron's <c>regular</c>.</summary>
    Regular,

    /// <summary>No Dock icon or menu bar, but windows may be activated. Electron's <c>accessory</c>.</summary>
    Accessory,

    /// <summary>No Dock icon, and no windows or activation. Electron's <c>prohibited</c>.</summary>
    Prohibited,
}

/// <summary>What <see cref="OrielApp.ShowAboutPanel"/> shows; Electron's <c>app.setAboutPanelOptions()</c> options.</summary>
public sealed record AboutPanelOptions
{
    /// <summary>The app's name. Electron's <c>applicationName</c>.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>The app's version. Electron's <c>applicationVersion</c>.</summary>
    public string? ApplicationVersion { get; init; }

    /// <summary>Copyright information. Electron's <c>copyright</c>.</summary>
    public string? Copyright { get; init; }

    /// <summary>macOS: the build version. Electron's <c>version</c>.</summary>
    public string? Version { get; init; }

    /// <summary>macOS, Windows: credit information. Electron's <c>credits</c>.</summary>
    public string? Credits { get; init; }

    /// <summary>Linux: the app's authors. Electron's <c>authors</c>.</summary>
    public IReadOnlyList<string>? Authors { get; init; }

    /// <summary>Linux: the app's website. Electron's <c>website</c>.</summary>
    public string? Website { get; init; }

    /// <summary>Linux, Windows: a PNG or JPEG icon. Electron's <c>iconPath</c>.</summary>
    public string? IconPath { get; init; }
}

public sealed partial class OrielApp
{
    private AboutPanelOptions _aboutPanel = new();

    /// <summary>macOS: the OS asks the app to open a file, as when one is dropped on its Dock icon or opened with it;
    /// handle it before <see cref="Ready"/> to catch the files the app is launched with. Electron's <c>open-file</c>.</summary>
    public event EventHandler<OpenFileEventArgs>? OpenFile;

    /// <summary>macOS: the OS asks the app to open a URL of a scheme it registered. Electron's <c>open-url</c>.</summary>
    public event EventHandler<OpenUrlEventArgs>? OpenUrl;

    /// <summary>macOS: the app was reopened, as by a click on its Dock icon. Electron's <c>activate</c>.</summary>
    public event EventHandler<ActivateEventArgs>? Activate;

    /// <summary>macOS: the app became the active app. Electron's <c>did-become-active</c>.</summary>
    public event EventHandler? DidBecomeActive;

    /// <summary>macOS: the app is no longer the active app. Electron's <c>did-resign-active</c>.</summary>
    public event EventHandler? DidResignActive;

    private void InstallMacDelegate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacApp.InstallDelegate(new MacAppDelegateHandlers
        {
            WillFinishLaunching = RaiseWillFinishLaunching,
            DidFinishLaunching = atLogin => s_launchedAtLogin = atLogin,
            DidBecomeActive = () => DidBecomeActive?.Invoke(this, EventArgs.Empty),
            DidResignActive = () => DidResignActive?.Invoke(this, EventArgs.Empty),
            OpenFile = path =>
            {
                var args = new OpenFileEventArgs(path);
                OpenFile?.Invoke(this, args);
                return args.Cancel;
            },
            OpenUrl = url => OpenUrl?.Invoke(this, new OpenUrlEventArgs(url)),
            Reopen = visible => Activate?.Invoke(this, new ActivateEventArgs(visible)),
        });
    }

    /// <summary>Whether the app is the active app (macOS); false elsewhere. Electron's <c>app.isActive()</c>.</summary>
    public bool IsActive() => OperatingSystem.IsMacOS() && OnMacMain(MacApp.IsActive);

    /// <summary>Sets how the app appears in the Dock and menu bar (macOS); does nothing elsewhere. Electron's
    /// <c>app.setActivationPolicy()</c>.</summary>
    public void SetActivationPolicy(ActivationPolicy policy)
    {
        var value = policy switch
        {
            ActivationPolicy.Regular => 0,
            ActivationPolicy.Accessory => 1,
            ActivationPolicy.Prohibited => 2,
            _ => throw new ArgumentException("Invalid activation policy: must be one of 'regular', 'accessory', or 'prohibited'", nameof(policy)),
        };
        if (OperatingSystem.IsMacOS()) OnMacMain(() => MacApp.SetActivationPolicy(value));
    }

    /// <summary>Whether the app bundle runs from an Applications folder (macOS); false elsewhere. Electron's
    /// <c>app.isInApplicationsFolder()</c>.</summary>
    public bool IsInApplicationsFolder()
        => OperatingSystem.IsMacOS() && InApplicationsFolder(RealPath(MacApp.BundlePath()) ?? MacApp.BundlePath(), MacApp.ApplicationDirectories());

    // Electron's IsInApplicationsFolder: under one of the system's Applications directories, or any directory of that name.
    internal static bool InApplicationsFolder(string resolvedBundlePath, IReadOnlyList<string> applicationDirectories)
        => applicationDirectories.Any(d => resolvedBundlePath.StartsWith(d, StringComparison.Ordinal))
            || resolvedBundlePath.Split('/').Contains("Applications");

    /// <summary>Whether <see cref="SetSecureKeyboardEntryEnabled"/> turned secure keyboard entry on (macOS). Electron's
    /// <c>app.isSecureKeyboardEntryEnabled()</c>.</summary>
    public bool IsSecureKeyboardEntryEnabled() => OperatingSystem.IsMacOS() && MacApp.SecureKeyboardEntry();

    /// <summary>Turns secure keyboard entry on or off (macOS), so other processes cannot read keystrokes, and only
    /// ASCII-capable input sources are offered meanwhile. Electron's <c>app.setSecureKeyboardEntryEnabled()</c>.</summary>
    public void SetSecureKeyboardEntryEnabled(bool enabled)
    {
        if (OperatingSystem.IsMacOS()) OnMacMain(() => MacApp.SetSecureKeyboardEntry(enabled));
    }

    /// <summary>Whether the OS has an emoji picker: macOS, and Windows 10 1803 and later. Electron's
    /// <c>app.isEmojiPanelSupported()</c>.</summary>
    public bool IsEmojiPanelSupported() => OperatingSystem.IsMacOS() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134);

    /// <summary>Shows the OS's emoji picker (macOS: the character palette). Electron's <c>app.showEmojiPanel()</c>;
    /// Windows is not wired up yet (drift A25).</summary>
    public void ShowEmojiPanel()
    {
        if (OperatingSystem.IsMacOS()) OnMacMain(MacApp.ShowCharacterPalette);
    }

    /// <summary>Sets what <see cref="ShowAboutPanel"/> shows, replacing earlier options. Electron's
    /// <c>app.setAboutPanelOptions()</c>.</summary>
    public void SetAboutPanelOptions(AboutPanelOptions options) => _aboutPanel = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Shows the about panel: AppKit's standard panel on macOS (the bundle's Info.plist fills what is not set),
    /// a GTK about dialog of the set options on Linux, an information box on Windows with the executable's description
    /// and product version unless set. Electron's <c>app.showAboutPanel()</c>.</summary>
    public void ShowAboutPanel()
    {
        var options = _aboutPanel;
        if (OperatingSystem.IsMacOS())
        {
            var panel = MacAboutPanelOptions(options);
            OnMacMain(() => MacApp.ShowAboutPanel(panel));
        }
        else if (OperatingSystem.IsLinux())
        {
            CefThread.RunOnUi(() => LinuxAboutDialog.Show(options.ApplicationName, options.ApplicationVersion, options.Copyright,
                options.Website, options.IconPath, options.Authors));
        }
        else if (OperatingSystem.IsWindows())
        {
            var exe = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "");
            var box = new MessageBoxOptions
            {
                Type = MessageBoxType.Info,
                Message = WindowsAboutMessage(options, exe.FileDescription, exe.ProductVersion),
                Icon = options.IconPath is { } icon ? NativeImage.CreateFromPath(icon) : null,
            };
            _ = Dialog.ShowMessageBoxAsync(box);
        }
    }

    // Electron's macOS SetAboutPanelOptions: each string option, its key capitalised, as NSAboutPanelOption* keys.
    internal static Dictionary<string, string> MacAboutPanelOptions(AboutPanelOptions options)
    {
        var panel = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, string? value)
        {
            if (value is not null) panel[key] = value;
        }
        Add("ApplicationName", options.ApplicationName);
        Add("ApplicationVersion", options.ApplicationVersion);
        Add("Copyright", options.Copyright);
        Add("Version", options.Version);
        Add("Credits", options.Credits);
        Add("Website", options.Website);
        Add("IconPath", options.IconPath);
        return panel;
    }

    // Electron's Windows ShowAboutPanel: the executable's description and product version, overridden by the options,
    // then name, version, copyright and credits, each followed by CRLF.
    internal static string WindowsAboutMessage(AboutPanelOptions options, string? fileDescription, string? productVersion)
    {
        var lines = new[] { options.ApplicationName ?? fileDescription ?? "", options.ApplicationVersion ?? productVersion ?? "", options.Copyright, options.Credits };
        return string.Concat(lines.Where(l => l is not null).Select(l => l + "\r\n"));
    }

    // AppKit calls belong on the main thread, which is CEF's UI thread once running and the caller's before Run.
    private T OnMacMain<T>(Func<T> func) => IsReady ? CefThread.InvokeOnUi(func) : func();

    private void OnMacMain(Action action)
    {
        if (IsReady) CefThread.RunOnUi(action);
        else action();
    }
}
