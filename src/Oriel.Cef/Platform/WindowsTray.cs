using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A native popup menu entry: a separator, an item with a command id, or a submenu.</summary>
public sealed record WindowsMenuEntry(int CommandId, string Text, bool Separator, bool Enabled, bool Checked, bool Radio, IReadOnlyList<WindowsMenuEntry>? Submenu);

/// <summary>Notification area icons on Windows (<c>Shell_NotifyIconW</c>, version 4), with their callbacks, balloons and
/// popup menus. UI thread only: the icons' callbacks arrive on a hidden window owned by that thread.</summary>
/// <remarks>The hidden window is top-level, not message-only: <c>TaskbarCreated</c>, sent when Explorer restarts, is
/// broadcast to top-level windows only, and every icon must then be added again.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsTray
{
    /// <summary>The callback message the icons send their host.</summary>
    public const uint CallbackMessage = 0x8001; // WM_APP + 1

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetFocus = 3, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifGuid = 0x20, NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;

    private static readonly ConcurrentDictionary<uint, Icon> s_icons = new();
    private static nint s_host;
    private static uint s_taskbarCreated;
    private static uint s_nextId;

    private sealed class Icon
    {
        public required uint Id;
        public Guid? Guid;
        public nint Handle;
        public string ToolTip = "";
        public required Action<uint, int, int> Callback;
    }

    /// <summary>The hidden window the icons call back; created on first use.</summary>
    public static nint Host => s_host != 0 ? s_host : s_host = CreateHost();

    /// <summary>Adds an icon showing <paramref name="icon"/> (owned by the caller, copied by the shell) and returns its id;
    /// <paramref name="callback"/> gets the version 4 event, then the anchor point in screen pixels. 0 if the shell refused.</summary>
    /// <remarks>With a <paramref name="guid"/>, Windows ties the icon to this executable's path: the same GUID from
    /// another path is refused.</remarks>
    public static uint Add(nint icon, string toolTip, Guid? guid, Action<uint, int, int> callback)
    {
        var entry = new Icon { Id = Interlocked.Increment(ref s_nextId), Guid = guid, Handle = icon, ToolTip = toolTip, Callback = callback };
        if (!Register(entry)) return 0;
        s_icons[entry.Id] = entry;
        return entry.Id;
    }

    /// <summary>Shows another icon (owned by the caller). False if the shell refused.</summary>
    public static bool SetIcon(uint id, nint icon)
    {
        if (!s_icons.TryGetValue(id, out var entry)) return false;
        entry.Handle = icon;
        var data = Data(entry, NifIcon);
        data.Icon = icon;
        return Shell_NotifyIconW(NimModify, &data) != 0;
    }

    /// <summary>Sets the tooltip, at most 127 characters. False if the shell refused.</summary>
    public static bool SetToolTip(uint id, string toolTip)
    {
        if (!s_icons.TryGetValue(id, out var entry)) return false;
        entry.ToolTip = toolTip;
        var data = Data(entry, NifTip | NifShowTip);
        Copy(toolTip, data.Tip, 128);
        return Shell_NotifyIconW(NimModify, &data) != 0;
    }

    /// <summary>Shows a balloon (a toast on Windows 10 and 11); <paramref name="flags"/> are <c>NIIF_</c> flags and
    /// <paramref name="balloonIcon"/> the icon for <c>NIIF_USER</c>, or 0. False if the shell refused.</summary>
    public static bool ShowBalloon(uint id, string title, string text, uint flags, nint balloonIcon)
    {
        if (!s_icons.TryGetValue(id, out var entry)) return false;
        var data = Data(entry, NifInfo);
        Copy(title, data.InfoTitle, 64);
        Copy(text, data.Info, 256);
        data.InfoFlags = flags;
        data.BalloonIcon = balloonIcon;
        return Shell_NotifyIconW(NimModify, &data) != 0;
    }

    /// <summary>Takes a balloon away; empty text removes it. False if the shell refused.</summary>
    public static bool RemoveBalloon(uint id) => s_icons.TryGetValue(id, out var entry) && Modify(Data(entry, NifInfo));

    /// <summary>Gives the icon keyboard focus in the notification area. False if the shell refused.</summary>
    public static bool Focus(uint id)
    {
        if (!s_icons.TryGetValue(id, out var entry)) return false;
        var data = Data(entry, 0);
        return Shell_NotifyIconW(NimSetFocus, &data) != 0;
    }

    /// <summary>Removes the icon. False if there was none or the shell refused.</summary>
    public static bool Delete(uint id)
    {
        if (!s_icons.TryRemove(id, out var entry)) return false;
        var data = Data(entry, 0);
        return Shell_NotifyIconW(NimDelete, &data) != 0;
    }

    /// <summary>Removes every icon; for process exit, when the host window may already be gone.</summary>
    public static void DeleteAll()
    {
        foreach (var id in s_icons.Keys) Delete(id);
    }

    /// <summary>The icon's bounds in screen pixels, or null when the shell cannot place it (gone, or hidden in the overflow).</summary>
    public static (int X, int Y, int Width, int Height)? GetRect(uint id)
    {
        if (!s_icons.TryGetValue(id, out var entry)) return null;
        var identifier = new Identifier { Size = (uint)sizeof(Identifier), Hwnd = Host, Id = entry.Id, Guid = entry.Guid ?? default };
        Rect r;
        return Shell_NotifyIconGetRect(&identifier, &r) >= 0 ? (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
    }

    /// <summary>Shows <paramref name="entries"/> as a popup menu at a screen point and returns the chosen command id, or
    /// null if none was chosen. Blocks in the menu's loop.</summary>
    /// <remarks>Windows' rule for notification icon menus: the owner must be the foreground window, or the menu does not
    /// close when the user clicks elsewhere; and a message posted after it lets the next one open.</remarks>
    public static int? TrackMenu(IReadOnlyList<WindowsMenuEntry> entries, int x, int y)
    {
        var menu = Build(entries);
        try
        {
            SetForegroundWindow(Host);
            var chosen = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */ | 0x0080 /* TPM_NONOTIFY */, x, y, Host, null);
            PostMessageW(Host, 0 /* WM_NULL */, 0, 0);
            return chosen == 0 ? null : chosen;
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>Closes the popup menu the icons' host is showing, if any; any thread.</summary>
    /// <remarks>The UI thread is inside the menu's loop, where CEF runs no posted tasks; a window message still gets
    /// dispatched there, and the host ends the menu when it arrives.</remarks>
    public static void CloseMenu()
    {
        if (s_host != 0) PostMessageW(s_host, WmCancelMode, 0, 0);
    }

    private const uint WmCancelMode = 0x001F;

    private static nint Build(IReadOnlyList<WindowsMenuEntry> entries)
    {
        var menu = CreatePopupMenu();
        foreach (var e in entries)
        {
            fixed (char* text = e.Text)
            {
                if (e.Separator) AppendMenuW(menu, 0x0800 /* MF_SEPARATOR */, 0, null);
                else if (e.Submenu is { } sub) AppendMenuW(menu, 0x0010 /* MF_POPUP */ | (e.Enabled ? 0u : 0x0003u /* MF_GRAYED|MF_DISABLED */), (nuint)Build(sub), text);
                else
                {
                    var flags = (e.Enabled ? 0u : 0x0003u) | (e.Checked ? 0x0008u /* MF_CHECKED */ : 0u);
                    AppendMenuW(menu, flags, (nuint)e.CommandId, text);
                    // A checked radio item shows a bullet, as Windows draws radio groups.
                    if (e.Radio && e.Checked)
                    {
                        var info = new MenuItemInfo { Size = (uint)sizeof(MenuItemInfo), Mask = 0x0100 /* MIIM_FTYPE */, Type = 0x0200 /* MFT_RADIOCHECK */ };
                        SetMenuItemInfoW(menu, (uint)e.CommandId, 0, &info);
                    }
                }
            }
        }
        return menu;
    }

    private static bool Register(Icon entry)
    {
        var data = Data(entry, NifMessage | NifIcon | NifTip | NifShowTip);
        data.CallbackMessage = CallbackMessage;
        data.Icon = entry.Handle;
        Copy(entry.ToolTip, data.Tip, 128);
        if (Shell_NotifyIconW(NimAdd, &data) == 0)
        {
            // A previous run of this process may have left its GUID icon behind.
            if (entry.Guid is null) return false;
            var stale = Data(entry, 0);
            Shell_NotifyIconW(NimDelete, &stale);
            if (Shell_NotifyIconW(NimAdd, &data) == 0) return false;
        }
        data.Version = NotifyIconVersion4;
        return Shell_NotifyIconW(NimSetVersion, &data) != 0;
    }

    private static NotifyIconData Data(Icon entry, uint flags)
    {
        // The id goes along with a GUID too, so callbacks still name the icon.
        var data = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Hwnd = Host, Id = entry.Id, Flags = flags };
        if (entry.Guid is { } guid)
        {
            data.Flags |= NifGuid;
            data.Guid = guid;
        }
        return data;
    }

    private static bool Modify(NotifyIconData data) => Shell_NotifyIconW(NimModify, &data) != 0;

    private static void Copy(string text, char* buffer, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(buffer, capacity));
        buffer[length] = '\0';
    }

    private static nint CreateHost()
    {
        fixed (char* name = "OrielTrayHost", message = "TaskbarCreated")
        {
            var cls = new WndClass { WndProc = &WndProc, Instance = GetModuleHandleW(null), ClassName = name };
            RegisterClassW(&cls);
            s_taskbarCreated = RegisterWindowMessageW(message);
            return CreateWindowExW(0, name, name, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == CallbackMessage)
            {
                var id = (uint)((lParam >> 16) & 0xFFFF);
                if (s_icons.TryGetValue(id, out var entry)) entry.Callback((uint)(lParam & 0xFFFF), (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                return 0;
            }
            if (message == WmCancelMode)
            {
                EndMenu();
                return 0;
            }
            if (message == s_taskbarCreated && s_taskbarCreated != 0)
            {
                foreach (var icon in s_icons.Values) Register(icon);
                return 0;
            }
        }
        // An exception must not cross into the window procedure.
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A tray callback threw: {e}"); }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Identifier
    {
        public uint Size;
        public nint Hwnd;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public nint Submenu;
        public nint CheckedBitmap;
        public nint UncheckedBitmap;
        public nint ItemData;
        public char* TypeData;
        public uint Cch;
        public nint ItemBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClass
    {
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconW(uint message, NotifyIconData* data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(Identifier* identifier, Rect* rect);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(WndClass* cls);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern uint RegisterWindowMessageW(char* name);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern int AppendMenuW(nint menu, uint flags, nuint id, char* text);
    [DllImport("user32.dll")] private static extern int SetMenuItemInfoW(nint menu, uint item, int byPosition, MenuItemInfo* info);
    [DllImport("user32.dll")] private static extern int DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, void* tpm);
    [DllImport("user32.dll")] private static extern int EndMenu();
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
}
