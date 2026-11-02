using Riftway.Cef.Platform;

namespace Riftway;

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

    /// <summary>A thumbnail clip in DIPs to pixels, Chromium's <c>ScreenWin::DIPToScreenRect</c> as Electron calls it:
    /// the origin is <paramref name="origin"/>, the region's origin converted as a screen point on the window's display,
    /// and the size is that of <c>ScaleToEnclosingRect</c>. An empty region means the whole window.</summary>
    /// <remarks>Electron treats the client-relative region as screen DIPs, so off the primary display's origin the clip
    /// shifts by the display's offset, as here.</remarks>
    internal static (int Left, int Top, int Right, int Bottom)? ClipToPixels(Rectangle region, float scale, (int X, int Y) origin)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        var width = (int)MathF.Ceiling((region.X + region.Width) * scale) - (int)MathF.Floor(region.X * scale);
        var height = (int)MathF.Ceiling((region.Y + region.Height) * scale) - (int)MathF.Floor(region.Y * scale);
        return (origin.X, origin.Y, origin.X + width, origin.Y + height);
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
