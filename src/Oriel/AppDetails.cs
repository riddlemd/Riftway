using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A window's taskbar identity on Windows; Electron's <c>options</c> for <c>win.setAppDetails()</c>.</summary>
public sealed record AppDetailsOptions
{
    /// <summary>The window's App User Model ID; windows with different IDs get separate taskbar buttons.
    /// Electron's <c>appId</c>. Without it Windows ignores the other options.</summary>
    public string? AppId { get; init; }

    /// <summary>The relaunch icon: an .ico, .exe or .dll path. Electron's <c>appIconPath</c>.</summary>
    public string? AppIconPath { get; init; }

    /// <summary>Which icon in <see cref="AppIconPath"/>. Electron's <c>appIconIndex</c>.</summary>
    public int AppIconIndex { get; init; }

    /// <summary>The command line a pinned button runs. Electron's <c>relaunchCommand</c>. Windows uses it only
    /// together with <see cref="RelaunchDisplayName"/>.</summary>
    public string? RelaunchCommand { get; init; }

    /// <summary>The pinned button's name. Electron's <c>relaunchDisplayName</c>.</summary>
    public string? RelaunchDisplayName { get; init; }
}

internal static class AppDetails
{
    /// <summary>Chromium's <c>SetAppDetailsForWindow</c>: every non-empty option is written, the icon as
    /// <c>path,index</c>; empty options leave the window's current value.</summary>
    internal static List<(AppUserModelProperty Property, string Value)> Plan(AppDetailsOptions options)
    {
        var values = new List<(AppUserModelProperty, string)>();
        if (!string.IsNullOrEmpty(options.AppId)) values.Add((AppUserModelProperty.Id, options.AppId));
        if (!string.IsNullOrEmpty(options.AppIconPath))
            values.Add((AppUserModelProperty.RelaunchIconResource, $"{options.AppIconPath},{options.AppIconIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        if (!string.IsNullOrEmpty(options.RelaunchCommand)) values.Add((AppUserModelProperty.RelaunchCommand, options.RelaunchCommand));
        if (!string.IsNullOrEmpty(options.RelaunchDisplayName)) values.Add((AppUserModelProperty.RelaunchDisplayNameResource, options.RelaunchDisplayName));
        return values;
    }

    /// <summary>A thumbnail clip in DIPs to client pixels, Chromium's <c>ScaleToEnclosingRect</c>: the near edges
    /// round down and the far edges up. An empty region means the whole window.</summary>
    internal static (int Left, int Top, int Right, int Bottom)? ClipToPixels(Rectangle region, double scale)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        return ((int)Math.Floor(region.X * scale), (int)Math.Floor(region.Y * scale),
            (int)Math.Ceiling((region.X + region.Width) * scale), (int)Math.Ceiling((region.Y + region.Height) * scale));
    }
}

/// <summary>What Windows 11 draws behind a window. Electron's <c>backgroundMaterial</c>.</summary>
public enum BackgroundMaterial
{
    /// <summary>The system default. Electron's <c>auto</c>.</summary>
    Auto,

    /// <summary>No material. Electron's <c>none</c>.</summary>
    None,

    /// <summary>Mica, the long-lived window material. Electron's <c>mica</c>.</summary>
    Mica,

    /// <summary>Acrylic, for transient windows. Electron's <c>acrylic</c>.</summary>
    Acrylic,

    /// <summary>Mica Alt, for tabbed windows. Electron's <c>tabbed</c>.</summary>
    Tabbed,
}

internal static class BackgroundMaterials
{
    internal static WindowsBackdrop ToBackdrop(BackgroundMaterial material) => material switch
    {
        BackgroundMaterial.None => WindowsBackdrop.None,
        BackgroundMaterial.Mica => WindowsBackdrop.MainWindow,
        BackgroundMaterial.Acrylic => WindowsBackdrop.TransientWindow,
        BackgroundMaterial.Tabbed => WindowsBackdrop.TabbedWindow,
        _ => WindowsBackdrop.Auto,
    };
}
