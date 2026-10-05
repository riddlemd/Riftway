using Oriel.Cef;
using Oriel.Cef.Platform;

// Each AppKit call runs behind an OperatingSystem.IsMacOS() check the analyzer cannot follow into lambdas.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>Options for <see cref="OrielApp.Focus"/>; Electron's <c>app.focus()</c> options.</summary>
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
    internal AppDock() { }

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

public sealed partial class OrielApp
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
        BrowserWindow.GetAllWindows().FirstOrDefault(w => w.IsVisible)?.Focus();
    }

    /// <summary>Shows <paramref name="count"/> on the app's badge: the Dock tile on macOS, "99+" past 99 and a dot when
    /// null; 0 removes it. Returns whether it is shown, false where the OS has no badge. Electron's
    /// <c>app.setBadgeCount()</c>.</summary>
    public bool SetBadgeCount(int? count = null)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        Dock.SetBadge(count == 0 ? "" : BadgeText(count));
        _badgeCount = count ?? 0;
        return true;
    }

    /// <summary>The count <see cref="SetBadgeCount"/> last set. Electron's <c>app.getBadgeCount()</c>.</summary>
    public int GetBadgeCount() => _badgeCount;

    // Chromium's badging::BadgeManager::GetBadgeString.
    internal static string BadgeText(int? count) => count switch
    {
        null => "\u2022",
        > 99 => "99+",
        _ => count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
