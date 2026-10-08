using System.Reflection;

namespace Oriel;

/// <summary>Electron's defaults for each <see cref="MenuItemRole"/>: label, accelerator, submenu and action.</summary>
internal static class MenuRoles
{
    private static bool Mac => OperatingSystem.IsMacOS();

    private static string AppName => OrielApp.CurrentOrNull?.Name ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "Oriel";

    internal static string? Label(MenuItemRole role) => role switch
    {
        MenuItemRole.Undo => "Undo",
        MenuItemRole.Redo => "Redo",
        MenuItemRole.Cut => "Cut",
        MenuItemRole.Copy => "Copy",
        MenuItemRole.Paste => "Paste",
        MenuItemRole.PasteAndMatchStyle => "Paste and Match Style",
        MenuItemRole.Delete => "Delete",
        MenuItemRole.SelectAll => "Select All",
        MenuItemRole.Reload => "Reload",
        MenuItemRole.ForceReload => "Force Reload",
        MenuItemRole.ToggleDevTools => "Toggle Developer Tools",
        MenuItemRole.ResetZoom => "Actual Size",
        MenuItemRole.ZoomIn => "Zoom In",
        MenuItemRole.ZoomOut => "Zoom Out",
        MenuItemRole.ToggleFullScreen => "Toggle Full Screen",
        MenuItemRole.Minimize => "Minimize",
        MenuItemRole.Close => Mac ? "Close Window" : "Close",
        MenuItemRole.Zoom => "Zoom",
        MenuItemRole.Quit => Mac ? $"Quit {AppName}" : OperatingSystem.IsWindows() ? "Exit" : "Quit",
        MenuItemRole.About => OperatingSystem.IsLinux() ? "About" : $"About {AppName}",
        MenuItemRole.Hide => $"Hide {AppName}",
        MenuItemRole.HideOthers => "Hide Others",
        MenuItemRole.Unhide => "Show All",
        MenuItemRole.Front => "Bring All to Front",
        MenuItemRole.Services => "Services",
        MenuItemRole.Window or MenuItemRole.WindowMenu => "Window",
        MenuItemRole.Help => "Help",
        MenuItemRole.AppMenu => AppName,
        MenuItemRole.FileMenu => "File",
        MenuItemRole.EditMenu => "Edit",
        MenuItemRole.ViewMenu => "View",
        MenuItemRole.ToggleSpellChecker => "Check Spelling While Typing",
        MenuItemRole.StartSpeaking => "Start Speaking",
        MenuItemRole.StopSpeaking => "Stop Speaking",
        MenuItemRole.ShowSubstitutions => "Show Substitutions",
        MenuItemRole.ToggleSmartQuotes => "Smart Quotes",
        MenuItemRole.ToggleSmartDashes => "Smart Dashes",
        MenuItemRole.ToggleTextReplacement => "Text Replacement",
        MenuItemRole.ClearRecentDocuments => "Clear Menu",
        _ => null,
    };

    internal static string? Accelerator(MenuItemRole role) => role switch
    {
        MenuItemRole.Undo => "CmdOrCtrl+Z",
        MenuItemRole.Redo => OperatingSystem.IsWindows() ? "Control+Y" : "Shift+CmdOrCtrl+Z",
        MenuItemRole.Cut => "CmdOrCtrl+X",
        MenuItemRole.Copy => "CmdOrCtrl+C",
        MenuItemRole.Paste => "CmdOrCtrl+V",
        MenuItemRole.PasteAndMatchStyle => Mac ? "Shift+Option+CmdOrCtrl+V" : "Shift+CmdOrCtrl+V",
        MenuItemRole.SelectAll => "CmdOrCtrl+A",
        MenuItemRole.Reload => "CmdOrCtrl+R",
        MenuItemRole.ForceReload => "Shift+CmdOrCtrl+R",
        MenuItemRole.ToggleDevTools => Mac ? "Alt+Command+I" : "Ctrl+Shift+I",
        MenuItemRole.ResetZoom => "CmdOrCtrl+0",
        MenuItemRole.ZoomIn => "CmdOrCtrl+Plus",
        MenuItemRole.ZoomOut => "CmdOrCtrl+-",
        MenuItemRole.ToggleFullScreen => Mac ? "Control+Command+F" : "F11",
        MenuItemRole.Minimize => "CmdOrCtrl+M",
        MenuItemRole.Close => "CmdOrCtrl+W",
        MenuItemRole.Quit => Mac ? "Command+Q" : OperatingSystem.IsWindows() ? null : "Ctrl+Q",
        MenuItemRole.Hide => "Command+H",
        MenuItemRole.HideOthers => "Command+Alt+H",
        _ => null,
    };

    /// <summary>A new copy of the role's submenu; null for roles without one.</summary>
    internal static Menu? Submenu(MenuItemRole role) => role switch
    {
        MenuItemRole.AppMenu =>
        [
            Item(MenuItemRole.About),
            Separator(),
            Item(MenuItemRole.Services),
            Separator(),
            Item(MenuItemRole.Hide),
            Item(MenuItemRole.HideOthers),
            Item(MenuItemRole.Unhide),
            Separator(),
            Item(MenuItemRole.Quit),
        ],
        MenuItemRole.FileMenu => [Item(Mac ? MenuItemRole.Close : MenuItemRole.Quit)],
        MenuItemRole.EditMenu => Mac
            ?
            [
                Item(MenuItemRole.Undo), Item(MenuItemRole.Redo), Separator(),
                Item(MenuItemRole.Cut), Item(MenuItemRole.Copy), Item(MenuItemRole.Paste),
                Item(MenuItemRole.PasteAndMatchStyle), Item(MenuItemRole.Delete), Item(MenuItemRole.SelectAll), Separator(),
                new MenuItem
                {
                    Label = "Substitutions",
                    Submenu =
                    [
                        Item(MenuItemRole.ShowSubstitutions), Separator(), Item(MenuItemRole.ToggleSmartQuotes),
                        Item(MenuItemRole.ToggleSmartDashes), Item(MenuItemRole.ToggleTextReplacement),
                    ],
                },
                new MenuItem { Label = "Speech", Submenu = [Item(MenuItemRole.StartSpeaking), Item(MenuItemRole.StopSpeaking)] },
            ]
            :
            [
                Item(MenuItemRole.Undo), Item(MenuItemRole.Redo), Separator(),
                Item(MenuItemRole.Cut), Item(MenuItemRole.Copy), Item(MenuItemRole.Paste),
                Item(MenuItemRole.Delete), Separator(), Item(MenuItemRole.SelectAll),
            ],
        MenuItemRole.ViewMenu =>
        [
            Item(MenuItemRole.Reload), Item(MenuItemRole.ForceReload), Item(MenuItemRole.ToggleDevTools), Separator(),
            Item(MenuItemRole.ResetZoom), Item(MenuItemRole.ZoomIn), Item(MenuItemRole.ZoomOut), Separator(),
            Item(MenuItemRole.ToggleFullScreen),
        ],
        MenuItemRole.WindowMenu or MenuItemRole.Window => Mac
            ? [Item(MenuItemRole.Minimize), Item(MenuItemRole.Zoom), Separator(), Item(MenuItemRole.Front)]
            : [Item(MenuItemRole.Minimize), Item(MenuItemRole.Zoom), Item(MenuItemRole.Close)],
        MenuItemRole.Services or MenuItemRole.Help => [],
        _ => null,
    };

    /// <summary>The default application menu on every OS; Electron's (lib/browser/default-menu.ts).</summary>
    internal static Menu DefaultApplicationMenu() =>
    [
        .. OperatingSystem.IsMacOS() ? [Item(MenuItemRole.AppMenu)] : Array.Empty<MenuItem>(),
        Item(MenuItemRole.FileMenu),
        Item(MenuItemRole.EditMenu),
        Item(MenuItemRole.ViewMenu),
        Item(MenuItemRole.WindowMenu),
    ];

    /// <summary>The AppKit action for roles AppKit implements itself (macOS only), sent along the responder chain.</summary>
    internal static string? MacSelector(MenuItemRole role) => role switch
    {
        MenuItemRole.Undo => "undo:",
        MenuItemRole.Redo => "redo:",
        MenuItemRole.Cut => "cut:",
        MenuItemRole.Copy => "copy:",
        MenuItemRole.Paste => "paste:",
        MenuItemRole.PasteAndMatchStyle => "pasteAndMatchStyle:",
        MenuItemRole.Delete => "delete:",
        MenuItemRole.SelectAll => "selectAll:",
        MenuItemRole.About => "orderFrontStandardAboutPanel:",
        MenuItemRole.Hide => "hide:",
        MenuItemRole.HideOthers => "hideOtherApplications:",
        MenuItemRole.Unhide => "unhideAllApplications:",
        MenuItemRole.Front => "arrangeInFront:",
        MenuItemRole.StartSpeaking => "startSpeaking:",
        MenuItemRole.StopSpeaking => "stopSpeaking:",
        MenuItemRole.ShowSubstitutions => "orderFrontSubstitutionsPanel:",
        MenuItemRole.ToggleSmartQuotes => "toggleAutomaticQuoteSubstitution:",
        MenuItemRole.ToggleSmartDashes => "toggleAutomaticDashSubstitution:",
        MenuItemRole.ToggleTextReplacement => "toggleAutomaticTextReplacement:",
        MenuItemRole.ToggleTabBar => "toggleTabBar:",
        MenuItemRole.SelectNextTab => "selectNextTab:",
        MenuItemRole.SelectPreviousTab => "selectPreviousTab:",
        MenuItemRole.ShowAllTabs => "toggleTabOverview:",
        MenuItemRole.MergeAllWindows => "mergeAllWindows:",
        MenuItemRole.MoveTabToNewWindow => "moveTabToNewWindow:",
        MenuItemRole.ClearRecentDocuments => "clearRecentDocuments:",
        _ => null,
    };

    /// <summary>Runs <paramref name="role"/>; false when it has no action here, so the item's click runs instead.</summary>
    internal static bool Execute(MenuItemRole role, BrowserWindow? window)
    {
        var page = window?.WebContents;
        switch (role)
        {
            case MenuItemRole.Undo: page?.Undo(); return true;
            case MenuItemRole.Redo: page?.Redo(); return true;
            case MenuItemRole.Cut: page?.Cut(); return true;
            case MenuItemRole.Copy: page?.Copy(); return true;
            case MenuItemRole.Paste: page?.Paste(); return true;
            case MenuItemRole.PasteAndMatchStyle: page?.PasteAndMatchStyle(); return true;
            case MenuItemRole.Delete: page?.Delete(); return true;
            case MenuItemRole.SelectAll: page?.SelectAll(); return true;
            case MenuItemRole.Reload: page?.Reload(); return true;
            case MenuItemRole.ForceReload: page?.ReloadIgnoringCache(); return true;
            case MenuItemRole.ToggleDevTools: page?.ToggleDevTools(); return true;
            case MenuItemRole.ResetZoom: page?.SetZoomLevel(0); return true;
            case MenuItemRole.ZoomIn: page?.SetZoomLevel(page.GetZoomLevel() + 0.5); return true;
            case MenuItemRole.ZoomOut: page?.SetZoomLevel(page.GetZoomLevel() - 0.5); return true;
            case MenuItemRole.ToggleFullScreen: window?.SetFullScreen(!window.IsFullScreen); return true;
            case MenuItemRole.Minimize: window?.Minimize(); return true;
            case MenuItemRole.Close: window?.Close(); return true;
            case MenuItemRole.Zoom:
                if (window is null) return true;
                if (window.IsMaximized) window.Unmaximize();
                else window.Maximize();
                return true;
            case MenuItemRole.Quit: OrielApp.CurrentOrNull?.Quit(); return true;
            case MenuItemRole.ToggleSpellChecker:
                // The focused page's session, else the default session, as Electron's.
                var session = page?.Session ?? Session.DefaultSession;
                session.SetSpellCheckerEnabled(!session.IsSpellCheckerEnabled());
                return true;
            default: return false;
        }
    }

    private static MenuItem Item(MenuItemRole role) => new() { Role = role };

    private static MenuItem Separator() => new() { Type = MenuItemType.Separator };
}

/// <summary>Menu label text.</summary>
internal static class MenuLabels
{
    /// <summary>The label without mnemonic markers: <c>&amp;File</c> → <c>File</c>, <c>&amp;&amp;</c> → <c>&amp;</c>.</summary>
    internal static string WithoutMnemonics(string label)
    {
        if (!label.Contains('&')) return label;
        var text = new System.Text.StringBuilder(label.Length);
        for (var i = 0; i < label.Length; i++)
        {
            if (label[i] != '&') text.Append(label[i]);
            else if (i + 1 < label.Length && label[i + 1] == '&') text.Append(label[++i]);
        }
        return text.ToString();
    }
}
