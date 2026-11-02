using System.Text;
using System.Text.RegularExpressions;
using Riftway.Cef;
using Riftway.Cef.Platform;

// Each AppKit call runs behind an OperatingSystem.IsMacOS() check the analyzer cannot follow into lambdas.
#pragma warning disable CA1416

namespace Riftway;

/// <summary>Options for <see cref="RiftwayApp.Focus"/>; Electron's <c>app.focus()</c> options.</summary>
public sealed record AppFocusOptions
{
    /// <summary>Takes focus even when another app is active (macOS).</summary>
    public bool Steal { get; init; }
}

/// <summary>How <see cref="AppDock.Bounce"/> bounces; Electron's <c>type</c>.</summary>
public enum DockBounceType
{
    /// <summary>Once.</summary>
    Informational,

    /// <summary>Until the app is active or the request is cancelled.</summary>
    Critical,
}

/// <summary>The app's Dock tile (macOS); Electron's <c>app.dock</c>. Its methods do nothing, and its getters return
/// defaults, on other OSes, where Electron has no <c>app.dock</c>.</summary>
public sealed class AppDock
{
    // UI thread only.
    private long? _lastShow;
    private Menu? _menu;

    internal AppDock() { }

    /// <summary>Hides the Dock icon. Ignored within a second of <see cref="ShowAsync"/>, as Electron does, since
    /// macOS can leave a second icon behind. Electron's <c>dock.hide()</c>.</summary>
    public void Hide()
    {
        if (!OperatingSystem.IsMacOS()) return;
        CefThread.RunOnUi(() =>
        {
            if (Environment.TickCount64 - _lastShow < 1000) return;
            // Electron's: windows of an app without a Dock icon cannot be hidden with it.
            foreach (var window in BaseWindow.GetAllWindows())
                if (window.GetNativeWindowHandle() is var handle and not 0) MacWindow.SetCanHide(handle, false);
            MacActivation.TransformProcess(foreground: false);
        });
    }

    /// <summary>Shows the Dock icon; completes once it is shown. Electron's <c>dock.show()</c>.</summary>
    public Task ShowAsync()
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            _lastShow = Environment.TickCount64;
            if (!MacActivation.IsRunningActive())
            {
                MacActivation.TransformProcess(foreground: true);
                done.SetResult();
                return;
            }
            // Electron's workaround: an active app transforms reliably only after the Dock is activated, a second apart.
            MacActivation.ActivateDockApp();
            CefThread.PostUiDelayed(() =>
            {
                MacActivation.TransformProcess(foreground: true);
                CefThread.PostUiDelayed(() =>
                {
                    MacActivation.ActivateSelf();
                    done.SetResult();
                }, TimeSpan.FromSeconds(1));
            }, TimeSpan.FromSeconds(1));
        });
        return done.Task;
    }

    /// <summary>Whether the Dock icon shows; it may lag <see cref="ShowAsync"/> briefly. Electron's <c>dock.isVisible()</c>.</summary>
    public bool IsVisible() => OperatingSystem.IsMacOS() && CefThread.InvokeOnUi(MacActivation.DockVisible);

    /// <summary>The menu the Dock icon shows. Electron's <c>dock.setMenu()</c>.</summary>
    public void SetMenu(Menu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (!OperatingSystem.IsMacOS()) return;
        CefThread.RunOnUi(() =>
        {
            _menu = menu;
            MacApplicationMenu.SetDockMenu(menu);
        });
    }

    /// <summary>The menu <see cref="SetMenu"/> set, or null. Electron's <c>dock.getMenu()</c>.</summary>
    public Menu? GetMenu() => CefThread.InvokeOnUi(() => _menu);

    /// <summary>The Dock icon's image; an empty image restores the app's own. Electron's <c>dock.setIcon()</c>.</summary>
    public void SetIcon(NativeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!OperatingSystem.IsMacOS()) return;
        var representations = image.GetScaleFactors().Select(scale => (image.ToPng(scale), scale)).ToList();
        var template = image.IsTemplateImage();
        CefThread.RunOnUi(() => MacActivation.SetIcon(representations, template));
    }

    /// <summary>Electron's <c>dock.setIcon()</c> with a path.</summary>
    public void SetIcon(string path) => SetIcon(NativeImage.CreateFromPath(path));

    /// <summary>Bounces the Dock icon; returns the request's id, or 0 when the app is already active. Electron's
    /// <c>dock.bounce()</c>.</summary>
    public int Bounce(DockBounceType type = DockBounceType.Informational)
        => OperatingSystem.IsMacOS() ? (int)CefThread.InvokeOnUi(() => MacActivation.Bounce(type == DockBounceType.Critical)) : 0;

    /// <summary>Stops the bounce <paramref name="id"/>. Electron's <c>dock.cancelBounce()</c>.</summary>
    public void CancelBounce(int id)
    {
        if (OperatingSystem.IsMacOS()) CefThread.RunOnUi(() => MacActivation.CancelBounce(id));
    }

    /// <summary>Bounces the Downloads stack if <paramref name="filePath"/> is in it. Electron's <c>dock.downloadFinished()</c>.</summary>
    public void DownloadFinished(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (OperatingSystem.IsMacOS()) CefThread.RunOnUi(() => MacActivation.DownloadFinished(filePath));
    }

    /// <summary>Shows <paramref name="text"/> on the Dock icon's badge; empty removes it. Electron's <c>dock.setBadge()</c>.</summary>
    public void SetBadge(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (OperatingSystem.IsMacOS()) CefThread.RunOnUi(() => MacActivation.SetBadge(text));
    }

    /// <summary>The Dock icon's badge, or empty. Electron's <c>dock.getBadge()</c>.</summary>
    public string GetBadge() => OperatingSystem.IsMacOS() ? CefThread.InvokeOnUi(MacActivation.Badge) : "";
}

public sealed partial class RiftwayApp
{
    private int _badgeCount;

    /// <summary>The Dock tile (macOS). Electron's <c>app.dock</c>.</summary>
    public AppDock Dock { get; } = new();

    /// <summary>Hides every window of the app (macOS); does nothing elsewhere, where Electron has no <c>app.hide()</c>.</summary>
    public void Hide()
    {
        if (OperatingSystem.IsMacOS()) CefThread.RunOnUi(MacActivation.Hide);
    }

    /// <summary>Shows the windows <see cref="Hide"/> hid (macOS), without focusing them. Electron's <c>app.show()</c>.</summary>
    public void Show()
    {
        if (OperatingSystem.IsMacOS()) CefThread.RunOnUi(MacActivation.Unhide);
    }

    /// <summary>Whether the app is hidden (macOS); false elsewhere. Electron's <c>app.isHidden()</c>.</summary>
    public bool IsHidden() => OperatingSystem.IsMacOS() && CefThread.InvokeOnUi(MacActivation.IsHidden);

    /// <summary>Makes the app active (macOS), or focuses its first visible window (Windows, Linux). Electron's
    /// <c>app.focus()</c>.</summary>
    public void Focus(AppFocusOptions? options = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            var steal = options?.Steal ?? false;
            CefThread.RunOnUi(() => MacActivation.Activate(steal));
            return;
        }
        BaseWindow.GetAllWindows().FirstOrDefault(w => w.IsVisible)?.Focus();
    }

    /// <summary>Shows <paramref name="count"/> on the app's badge: the Dock tile on macOS, an overlay drawn on the taskbar
    /// button of every open window on Windows; "99+" past 99 and a dot when null; 0 removes it. Returns whether it is
    /// shown, false where the OS has no badge. Electron's <c>app.setBadgeCount()</c>.</summary>
    /// <remarks>Linux: a Unity launcher entry signal on the session bus, matched to the app by
    /// <see cref="SetDesktopName"/>, which only some desktops show; false for null or without a desktop name.</remarks>
    public bool SetBadgeCount(int? count = null)
    {
        if (OperatingSystem.IsLinux())
        {
            if (count is null || !LinuxLauncherEntry.SetBadgeCount(count.Value)) return false;
            _badgeCount = count.Value;
            return true;
        }
        if (OperatingSystem.IsWindows()) SetWindowsBadge(count);
        else if (OperatingSystem.IsMacOS()) Dock.SetBadge(count == 0 ? "" : BadgeText(count));
        else return false;
        _badgeCount = count ?? 0;
        return true;
    }

    /// <summary>The count <see cref="SetBadgeCount"/> last set. Electron's <c>app.getBadgeCount()</c>.</summary>
    public int GetBadgeCount() => _badgeCount;

    /// <summary>The name of the app's .desktop file, which Linux desktops match its launcher entry, badge and progress
    /// to; set by default from <see cref="Name"/>. Does nothing elsewhere. Electron's <c>app.setDesktopName()</c>.</summary>
    public void SetDesktopName(string desktopName)
    {
        ArgumentNullException.ThrowIfNull(desktopName);
        if (OperatingSystem.IsLinux()) LinuxLauncherEntry.SetDesktopName(desktopName);
    }

    // Electron's defaultDesktopName (lib/browser/desktop-name.ts): an XDG-valid, lowercase, hyphenated slug.
    internal static string DefaultDesktopName(string? name)
    {
        var slug = string.IsNullOrEmpty(name) ? "" : Regex.Replace(
            Regex.Replace(name.Normalize(NormalizationForm.FormKD), @"\p{M}", "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug + ".desktop" : Path.GetFileName(Environment.ProcessPath ?? "") + ".desktop";
    }

    // Chromium's badging::BadgeManager::GetBadgeString.
    internal static string BadgeText(int? count) => count switch
    {
        null => "\u2022",
        > 99 => "99+",
        _ => FormatBadgeNumber(count.Value),
    };
}
