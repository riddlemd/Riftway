using Oriel.Cef.Platform;

// Each AppKit call runs inside a lambda behind an OperatingSystem.IsMacOS() check the analyzer cannot follow.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>Options for <see cref="BaseWindow.SetVisibleOnAllWorkspaces"/>; Electron's options.</summary>
public sealed record VisibleOnAllWorkspacesOptions
{
    /// <summary>Also shows the window over full-screen apps (macOS).</summary>
    public bool VisibleOnFullScreen { get; init; }

    /// <summary>Leaves the Dock icon alone; otherwise it is hidden when <see cref="VisibleOnFullScreen"/> is set and
    /// shown when not, as Electron does (macOS).</summary>
    public bool SkipTransformProcessType { get; init; }
}

/// <summary>What Electron's <c>NativeWindowMac</c> does for a window <c>type</c>.</summary>
internal readonly record struct MacWindowType(bool Desktop, bool Floating, bool Textured)
{
    // NSWindowCollectionBehaviorCanJoinAllSpaces | Stationary | IgnoresCycle.
    internal const nuint DesktopBehavior = (1 << 0) | (1 << 4) | (1 << 6);
    // NSWindowStyleMaskTexturedBackground.
    internal const nuint TexturedStyle = 1 << 8;

    /// <summary><c>desktop</c> sits below the desktop icons, never key, on every Space and outside Exposé and window
    /// cycling; <c>panel</c> floats; <c>textured</c> (deprecated) applies only to a transparent or frameless window.</summary>
    internal static MacWindowType For(string? type, bool transparentOrFrameless) => type switch
    {
        "desktop" => new(true, false, false),
        "panel" => new(false, true, false),
        "textured" when transparentOrFrameless => new(false, false, true),
        _ => default,
    };
}

public unsafe partial class BaseWindow
{
    // NativeWindowMac's constructor for options.type; Chromium makes the window, so a panel is not an NSPanel.
    private void ApplyMacWindowType(nint view)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var type = MacWindowType.For(_options.Type, _macTransparent || IsFrameless);
        if (type.Textured)
        {
            Console.Error.WriteLine("[Oriel] DeprecationWarning: The 'textured' window type is deprecated and will be removed");
            MacWindow.AddStyleMask(view, MacWindowType.TexturedStyle);
        }
        if (type.Desktop)
        {
            MacWindow.SetLevel(view, MacWindow.BelowDesktopLevel());
            MacWindow.SetFocusable(view, false);
            MacWindow.SetCollectionBehaviorValue(view, MacWindowType.DesktopBehavior);
        }
        if (type.Floating) MacWindow.SetLevel(view, 3);
    }

    /// <summary>The file the title bar's proxy icon stands for (macOS); does nothing elsewhere. Electron's
    /// <c>win.setRepresentedFilename()</c>.</summary>
    public void SetRepresentedFilename(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        if (OperatingSystem.IsMacOS()) Command(w =>
        {
            MacWindow.SetRepresentedFilename((nint)w->get_window_handle(w), filename);
            RedrawMacButtons();
        });
    }

    /// <summary>Electron's <c>win.getRepresentedFilename()</c>; empty off macOS.</summary>
    public string GetRepresentedFilename()
        => OperatingSystem.IsMacOS() ? Query(w => MacWindow.GetRepresentedFilename((nint)w->get_window_handle(w)), "") : "";

    /// <summary>Marks the window's document as having unsaved changes (macOS); does nothing elsewhere. Electron's
    /// <c>win.setDocumentEdited()</c>.</summary>
    public void SetDocumentEdited(bool edited)
    {
        if (OperatingSystem.IsMacOS()) Command(w =>
        {
            MacWindow.SetDocumentEdited((nint)w->get_window_handle(w), edited);
            RedrawMacButtons();
        });
    }

    /// <summary>Electron's <c>win.isDocumentEdited()</c>; false off macOS.</summary>
    public bool IsDocumentEdited()
        => OperatingSystem.IsMacOS() && Query(w => MacWindow.IsDocumentEdited((nint)w->get_window_handle(w)), false);

    /// <summary>Leaves the window out of Mission Control (macOS). Electron's <c>win.setHiddenInMissionControl()</c>.</summary>
    public void SetHiddenInMissionControl(bool hidden)
    {
        if (OperatingSystem.IsMacOS()) Command(w => MacWindow.SetCollectionBehavior((nint)w->get_window_handle(w), MacWindow.Transient, hidden));
    }

    /// <summary>Electron's <c>win.isHiddenInMissionControl()</c>; false off macOS.</summary>
    public bool IsHiddenInMissionControl()
        => OperatingSystem.IsMacOS() && Query(w => MacWindow.HasCollectionBehavior((nint)w->get_window_handle(w), MacWindow.Transient), false);

    /// <summary>Shows the window on every workspace (macOS Spaces). Electron's <c>win.setVisibleOnAllWorkspaces()</c>.</summary>
    /// <remarks>On Linux the window manager's workspaces; Windows has none, as in Electron.</remarks>
    public void SetVisibleOnAllWorkspaces(bool visible, VisibleOnAllWorkspacesOptions? options = null)
    {
        if (OperatingSystem.IsLinux())
        {
            Command(w => X11Window.SetVisibleOnAllWorkspaces((nint)w->get_window_handle(w), visible));
            return;
        }
        if (!OperatingSystem.IsMacOS()) return;
        options ??= new VisibleOnAllWorkspacesOptions();
        Command(w =>
        {
            // Since 10.14 a window floats over full-screen apps only for an app without a Dock icon.
            if (!options.SkipTransformProcessType) MacWindow.SetDockIconHidden(options.VisibleOnFullScreen);
            var view = (nint)w->get_window_handle(w);
            MacWindow.SetCollectionBehavior(view, MacWindow.CanJoinAllSpaces, visible);
            MacWindow.SetCollectionBehavior(view, MacWindow.FullScreenAuxiliary, options.VisibleOnFullScreen);
        });
    }

    /// <summary>Electron's <c>win.isVisibleOnAllWorkspaces()</c>; false on Windows.</summary>
    public bool IsVisibleOnAllWorkspaces()
        => OperatingSystem.IsLinux() ? Query(w => X11Window.IsVisibleOnAllWorkspaces((nint)w->get_window_handle(w)), false)
            : OperatingSystem.IsMacOS() && Query(w => MacWindow.HasCollectionBehavior((nint)w->get_window_handle(w), MacWindow.CanJoinAllSpaces), false);
}
