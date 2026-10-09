using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>Notification area icons on Windows (<c>Shell_NotifyIconW</c>, legacy version 0 as Electron leaves it), with
/// their callbacks and balloons. UI thread only: the icons' callbacks arrive on a hidden window owned by that thread.</summary>
/// <remarks>The hidden window is top-level, not message-only: <c>TaskbarCreated</c>, sent when Explorer restarts, is
/// broadcast to top-level windows only, and every icon must then be added again.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsTray
{
    /// <summary>The callback message the icons send their host.</summary>
    public const uint CallbackMessage = 0x8001; // WM_APP + 1

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetFocus = 3;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifGuid = 0x20;
    private const uint WmTimer = 0x0113;
    private const nuint HoverTimerId = 1;

    private static readonly ConcurrentDictionary<uint, Icon> s_icons = new();
    private static nint s_host;
    private static uint s_taskbarCreated;
    // Electron's kBaseIconId: the first icon is 2, clear of plugins that hard-code id 1.
    private static uint s_nextId = 1;
    private static Action? s_timerTick;

    private sealed class Icon
    {
        public required uint Id;
        public Guid? Guid;
        public nint Handle;
        public required Action<uint> Callback;
    }

    /// <summary>The hidden window the icons call back; created on first use.</summary>
    public static nint Host => s_host != 0 ? s_host : s_host = CreateHost();

    /// <summary>Adds an icon, with no image or tooltip yet, and returns its id; <paramref name="callback"/> gets the mouse
    /// or <c>NIN_</c> message of each callback.</summary>
    /// <remarks>As Electron's <c>NotifyIcon</c>: a refusal by the shell (Explorer not running; a GUID held by another
    /// icon, or by this executable at another path) is only logged, and the id stays usable.</remarks>
    public static uint Add(Guid? guid, Action<uint> callback)
    {
        if (guid is { } g && s_icons.Values.Any(i => i.Guid == g))
            Console.Error.WriteLine("[Oriel] Guid already in use. Existing tray entry will be replaced.");
        var entry = new Icon { Id = Interlocked.Increment(ref s_nextId), Guid = guid, Callback = callback };
        s_icons[entry.Id] = entry;
        var data = Data(entry, NifMessage);
        data.CallbackMessage = CallbackMessage;
        if (Shell_NotifyIconW(NimAdd, &data) == 0) Console.Error.WriteLine("[Oriel] Unable to create status tray icon.");
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
        var data = Data(entry, NifTip);
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

    /// <summary>Makes the host the foreground window, so that a menu opened for an icon closes when the user clicks
    /// elsewhere. False if Windows refused.</summary>
    public static bool SetForeground() => SetForegroundWindow(Host) != 0;

    /// <summary>Calls <paramref name="tick"/> every <paramref name="milliseconds"/> on the host's thread until
    /// <see cref="StopTimer"/>; replaces any running timer.</summary>
    public static void StartTimer(uint milliseconds, Action tick)
    {
        s_timerTick = tick;
        SetTimer(Host, HoverTimerId, milliseconds, null);
    }

    /// <summary>Stops the timer <see cref="StartTimer"/> started.</summary>
    public static void StopTimer()
    {
        s_timerTick = null;
        if (s_host != 0) KillTimer(s_host, HoverTimerId);
    }

    // Electron's NotifyIcon::ResetIcon: the image comes back, the tooltip does not.
    private static void Reset(Icon entry)
    {
        var stale = Data(entry, 0);
        Shell_NotifyIconW(NimDelete, &stale);
        var data = Data(entry, NifMessage | (entry.Handle != 0 ? NifIcon : 0));
        data.CallbackMessage = CallbackMessage;
        data.Icon = entry.Handle;
        if (Shell_NotifyIconW(NimAdd, &data) == 0) Console.Error.WriteLine("[Oriel] Unable to re-create status tray icon.");
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
            // Version 0: wParam is the icon's id and lParam the mouse or NIN_ message itself.
            if (message == CallbackMessage)
            {
                if (s_icons.TryGetValue((uint)wParam, out var entry)) entry.Callback((uint)lParam);
                return 1;
            }
            if (message == WmTimer && (nuint)wParam == HoverTimerId)
            {
                s_timerTick?.Invoke();
                return 0;
            }
            if (message == s_taskbarCreated && s_taskbarCreated != 0)
            {
                foreach (var icon in s_icons.Values) Reset(icon);
                return 1;
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
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint elapse, void* timerProc);
    [DllImport("user32.dll")] private static extern int KillTimer(nint hwnd, nuint id);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
}
