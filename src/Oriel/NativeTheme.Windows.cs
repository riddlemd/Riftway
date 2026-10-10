using System.Runtime.Versioning;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

public static partial class NativeTheme
{
    private static bool s_windowsWatching;
    private static WindowsThemeInputs s_windowsInputs;
    private static WindowsThemeVariables s_windowsVariables;

    // Once watching, the getters answer from the state the notifications last left, as Chromium's NativeTheme does.
    private static WindowsThemeVariables WindowsVariables()
    {
        lock (s_gate)
        {
            if (s_windowsWatching) return s_windowsVariables;
        }
        return WindowsTheme.Variables(ReadWindowsInputs());
    }

    [SupportedOSPlatform("windows")]
    private static void StartWindows()
    {
        var inputs = ReadWindowsInputs();
        lock (s_gate)
        {
            s_windowsInputs = inputs;
            s_windowsVariables = WindowsTheme.Variables(inputs);
            s_windowsWatching = true;
        }
        WindowsThemeSettings.Start(OnWindowsChange);
    }

    /// <summary>For tests: reads the theme keys below <c>HKCU\<paramref name="prefix"/></c> instead of the user's own
    /// (empty for the user's), adopting what is there without raising <see cref="Updated"/>. UI thread.</summary>
    [SupportedOSPlatform("windows")]
    internal static void SetWindowsKeyPrefix(string prefix)
    {
        WindowsThemeSettings.KeyPrefix = prefix;
        var inputs = ReadWindowsInputs();
        lock (s_gate)
        {
            s_windowsInputs = inputs;
            s_windowsVariables = WindowsTheme.Variables(inputs);
        }
    }

    private static WindowsThemeInputs ReadWindowsInputs()
    {
        if (!OperatingSystem.IsWindows()) return default;
        var (appsUseLight, transparency) = WindowsThemeSettings.ReadPersonalize();
        var (window, windowText) = WindowsThemeSettings.ReadWindowColors();
        return new(appsUseLight, transparency, WindowsThemeSettings.ReadHighContrast(), window, windowText,
            WindowsThemeSettings.ReadInvertFilter(), WindowsThemeSettings.ReadAccentColor(), WindowsThemeSettings.ReadCaretBlinkTime());
    }

    // UI thread. Each notification re-reads only what its source covers, as OsSettingsProviderWin's handlers do.
    [SupportedOSPlatform("windows")]
    private static void OnWindowsChange(WindowsThemeChange change)
    {
        Checks++;
        WindowsThemeInputs before;
        WindowsThemeVariables current;
        lock (s_gate) (before, current) = (s_windowsInputs, s_windowsVariables);
        var after = change switch
        {
            WindowsThemeChange.Personalize => WithPersonalize(before),
            WindowsThemeChange.ColorFiltering => before with { InvertFilter = WindowsThemeSettings.ReadInvertFilter() },
            WindowsThemeChange.Dwm => before with { AccentColor = WindowsThemeSettings.ReadAccentColor() },
            WindowsThemeChange.HighContrast => before with { ForcedColorsActive = WindowsThemeSettings.ReadHighContrast() },
            _ => WithColors(before),
        } with { CaretBlinkTime = WindowsThemeSettings.ReadCaretBlinkTime() };
        var next = WindowsTheme.Next(change, before, after, current);
        lock (s_gate)
        {
            s_windowsInputs = after;
            if (next is { } variables) s_windowsVariables = variables;
        }
        // Electron emits from a task it posts on each OnNativeThemeUpdated.
        if (next is not null) CefThread.PostUi(Raise);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsThemeInputs WithPersonalize(WindowsThemeInputs inputs)
    {
        var (appsUseLight, transparency) = WindowsThemeSettings.ReadPersonalize();
        return inputs with { AppsUseLightTheme = appsUseLight, EnableTransparency = transparency };
    }

    [SupportedOSPlatform("windows")]
    private static WindowsThemeInputs WithColors(WindowsThemeInputs inputs)
    {
        var (window, windowText) = WindowsThemeSettings.ReadWindowColors();
        return inputs with { WindowColor = window, WindowTextColor = windowText };
    }
}
