using System.Globalization;
using Riftway.Cef;
using Riftway.Cef.Platform;

namespace Riftway;

public sealed partial class RiftwayApp
{
    // Electron's Browser::SetBadgeCount on Windows: every open window gets the overlay, and windows opened later none.
    private static void SetWindowsBadge(int? count)
    {
        if (!OperatingSystem.IsWindows()) return;
        var badge = count == 0 ? null : WindowsBadge.Draw(BadgeText(count));
        var description = BadgeDescription(count);
        foreach (var window in BaseWindow.GetAllWindows()) window.SetBadgeOverlay(badge, description);
    }

    // Chromium's IDS_BADGE_UNREAD_NOTIFICATIONS* (ui_strings.grd), which Electron gives the overlay for screen readers.
    internal static string BadgeDescription(int? count) => count switch
    {
        null => "Unread Notifications",
        > 99 => "More than 99 unread notifications",
        1 => "1 Unread Notification",
        _ => FormatBadgeNumber(count.Value) + " Unread Notifications",
    };

    // base::FormatNumber groups thousands, which only negative counts reach.
    private static string FormatBadgeNumber(int value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}

public unsafe partial class BaseWindow
{
    /// <summary>Shows <paramref name="bgra"/> (a straight-alpha <see cref="WindowsBadge.Size"/>-pixel badge) over the
    /// taskbar icon, or removes the overlay when null, with <paramref name="description"/> for screen readers.</summary>
    internal void SetBadgeOverlay(byte[]? bgra, string description) => Command(w =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = Hwnd(w);
        if (bgra is null) WindowsTaskbar.ClearOverlayIcon(hwnd, description);
        else WindowsTaskbar.SetOverlayIcon(hwnd, bgra, WindowsBadge.Size, WindowsBadge.Size, description);
    });
}
