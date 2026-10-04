using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>What a taskbar button's progress shows; the values are Win32's <c>TBPF_</c> flags.</summary>
public enum TaskbarProgressState
{
    /// <summary>No progress; <c>TBPF_NOPROGRESS</c>.</summary>
    None = 0,

    /// <summary>Cycling, without a value; <c>TBPF_INDETERMINATE</c>.</summary>
    Indeterminate = 1,

    /// <summary>Green, with a value; <c>TBPF_NORMAL</c>.</summary>
    Normal = 2,

    /// <summary>Red, with a value; <c>TBPF_ERROR</c>.</summary>
    Error = 4,

    /// <summary>Yellow, with a value; <c>TBPF_PAUSED</c>.</summary>
    Paused = 8,
}

/// <summary>A window's taskbar button on Windows: progress, overlay icon, flashing, and whether it has one.</summary>
/// <remarks>UI thread only. Chromium's UI thread is in the MTA, so it reaches the apartment-threaded <c>ITaskbarList3</c>
/// through a proxy.
/// The shell ignores calls made before the window's button exists, so call these once the window is shown.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsTaskbar
{
    private static readonly Guid s_clsidTaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid s_iidTaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    // ITaskbarList3 vtable slots, after IUnknown's three.
    private const int HrInit = 3;
    private const int AddTab = 4;
    private const int DeleteTab = 5;
    private const int SetProgressValue = 9;
    private const int SetProgressState = 10;
    private const int ThumbBarAddButtonsSlot = 15;
    private const int ThumbBarUpdateButtonsSlot = 16;
    private const int SetOverlayIconSlot = 18;
    private const int SetThumbnailTooltipSlot = 19;
    private const int SetThumbnailClipSlot = 20;

    private static void* s_taskbar;
    private static bool s_unavailable;

    /// <summary>Sets the button's progress: <paramref name="percent"/> of 100, or none for states without a value.
    /// False if the shell refused.</summary>
    public static bool SetProgress(nint hwnd, TaskbarProgressState state, int? percent)
    {
        var taskbar = Taskbar();
        if (taskbar is null) return false;
        if (((delegate* unmanaged<void*, nint, int, int>)Slot(taskbar, SetProgressState))(taskbar, hwnd, (int)state) < 0) return false;
        // SetProgressValue after SetProgressState: Normal is implied, Error and Paused are kept.
        return percent is not { } value
            || ((delegate* unmanaged<void*, nint, ulong, ulong, int>)Slot(taskbar, SetProgressValue))(taskbar, hwnd, (ulong)value, 100) >= 0;
    }

    /// <summary>Removes the window's button from the taskbar, or puts it back. False if the shell refused.</summary>
    /// <remarks>The shell creates the button when the window is first shown, so call this after that.</remarks>
    public static bool SetSkipTaskbar(nint hwnd, bool skip)
    {
        var taskbar = Taskbar();
        return taskbar is not null && ((delegate* unmanaged<void*, nint, int>)Slot(taskbar, skip ? DeleteTab : AddTab))(taskbar, hwnd) >= 0;
    }

    /// <summary>Shows <paramref name="bgra"/> (straight alpha, top row first) over the button's icon, with
    /// <paramref name="description"/> for screen readers. False if the icon could not be made or the shell refused.</summary>
    public static bool SetOverlayIcon(nint hwnd, ReadOnlySpan<byte> bgra, int width, int height, string? description)
    {
        var taskbar = Taskbar();
        if (taskbar is null) return false;
        var icon = CreateIcon(bgra, width, height);
        if (icon == 0) return false;
        try
        {
            fixed (char* text = description)
                return ((delegate* unmanaged<void*, nint, nint, char*, int>)Slot(taskbar, SetOverlayIconSlot))(taskbar, hwnd, icon, text) >= 0;
        }
        // The taskbar keeps its own copy.
        finally { DestroyIcon(icon); }
    }

    /// <summary>Adds <paramref name="buttons"/> to the window's thumbnail toolbar (<paramref name="add"/>, once per
    /// window) or updates them. False if the shell refused.</summary>
    /// <remarks>The shell keeps using the icons, so destroy them only after they are replaced.</remarks>
    public static bool SetThumbButtons(nint hwnd, ReadOnlySpan<ThumbButton> buttons, bool add)
    {
        var taskbar = Taskbar();
        if (taskbar is null) return false;
        var native = stackalloc ThumbButtonNative[buttons.Length];
        for (var i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            native[i] = new ThumbButtonNative { Mask = ThbIcon | ThbTooltip | ThbFlags, Id = b.Id, Icon = b.Icon, Flags = b.Flags };
            var tip = (b.Tooltip ?? "").AsSpan();
            tip[..Math.Min(tip.Length, 259)].CopyTo(new Span<char>(native[i].Tip, 260));
        }
        return ((delegate* unmanaged<void*, nint, uint, ThumbButtonNative*, int>)Slot(taskbar, add ? ThumbBarAddButtonsSlot : ThumbBarUpdateButtonsSlot))(taskbar, hwnd, (uint)buttons.Length, native) >= 0;
    }

    /// <summary>An icon from <paramref name="bgra"/> (straight alpha, top row first), or 0; free it with
    /// <see cref="DestroyIconHandle"/>.</summary>
    public static nint CreateIconHandle(ReadOnlySpan<byte> bgra, int width, int height) => CreateIcon(bgra, width, height);

    /// <summary>Frees an icon from <see cref="CreateIconHandle"/>; 0 is ignored.</summary>
    public static void DestroyIconHandle(nint icon)
    {
        if (icon != 0) DestroyIcon(icon);
    }

    /// <summary>Removes the overlay icon. False if the shell refused.</summary>
    public static bool ClearOverlayIcon(nint hwnd)
    {
        var taskbar = Taskbar();
        return taskbar is not null && ((delegate* unmanaged<void*, nint, nint, char*, int>)Slot(taskbar, SetOverlayIconSlot))(taskbar, hwnd, 0, null) >= 0;
    }

    /// <summary>Sets the tooltip on the window's taskbar thumbnail; empty restores the window title.
    /// False if the shell refused.</summary>
    public static bool SetThumbnailToolTip(nint hwnd, string toolTip)
    {
        var taskbar = Taskbar();
        if (taskbar is null) return false;
        fixed (char* text = toolTip)
            return ((delegate* unmanaged<void*, nint, char*, int>)Slot(taskbar, SetThumbnailTooltipSlot))(taskbar, hwnd, text) >= 0;
    }

    /// <summary>Shows only <paramref name="clip"/> (client-area pixels) in the taskbar thumbnail, or the whole window
    /// when null; <paramref name="done"/> gets false if the shell refused. Asynchronous, in call order.</summary>
    public static void SetThumbnailClip(nint hwnd, (int Left, int Top, int Right, int Bottom)? clip, Action<bool> done)
    {
        lock (s_clipQueue)
        {
            s_clipThread ??= StartClipThread();
            s_clipQueue.Add(() =>
            {
                var taskbar = s_clipTaskbar;
                var rect = clip is { } c ? new Rect { Left = c.Left, Top = c.Top, Right = c.Right, Bottom = c.Bottom } : default;
                done(taskbar is not null && ((delegate* unmanaged<void*, nint, Rect*, int>)Slot(taskbar, SetThumbnailClipSlot))(taskbar, hwnd, clip is null ? null : &rect) >= 0);
            });
        }
    }

    // A NULL clip, which clears it, fails with RPC_X_NULL_REF_POINTER through the proxy the MTA UI thread gets,
    // so clips go to an STA thread that owns its own instance and calls it directly.
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> s_clipQueue = [];
    private static Thread? s_clipThread;
    private static void* s_clipTaskbar;

    private static Thread StartClipThread()
    {
        var thread = new Thread(() =>
        {
            s_clipTaskbar = Create();
            // Blocking waits on an STA thread pump COM messages.
            foreach (var work in s_clipQueue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "Oriel taskbar clip" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    /// <summary>Flashes the title bar and taskbar button a few times, leaving the button highlighted until the
    /// window is activated; or stops. Chromium's <c>FlashFrame</c>, which Electron uses.</summary>
    public static void FlashFrame(nint hwnd, bool flash)
    {
        var info = new FlashInfo
        {
            Size = (uint)sizeof(FlashInfo),
            Hwnd = hwnd,
            Flags = flash ? FlashAll : FlashStop,
            Count = flash ? 4u : 0u,
        };
        FlashWindowEx(&info);
    }

    private static void* Taskbar()
    {
        if (s_taskbar is not null || s_unavailable) return s_taskbar;
        s_taskbar = Create();
        // No shell (Server Core, some kiosks): every taskbar call becomes a no-op.
        s_unavailable = s_taskbar is null;
        return s_taskbar;
    }

    private static void* Create()
    {
        void* taskbar;
        var clsid = s_clsidTaskbarList;
        var iid = s_iidTaskbarList3;
        if (CoCreateInstance(&clsid, null, InprocServer, &iid, &taskbar) < 0) return null;
        return ((delegate* unmanaged<void*, int>)Slot(taskbar, HrInit))(taskbar) < 0 ? null : taskbar;
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    // A 32-bit top-down DIB carries the alpha; the monochrome mask is unused but required.
    private static nint CreateIcon(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return 0;
        var header = new BitmapV5Header
        {
            Size = (uint)sizeof(BitmapV5Header),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = BitFields,
            RedMask = 0x00FF0000,
            GreenMask = 0x0000FF00,
            BlueMask = 0x000000FF,
            AlphaMask = 0xFF000000,
        };
        void* bits;
        var color = CreateDIBSection(0, &header, 0, &bits, 0, 0);
        if (color == 0) return 0;
        var mask = CreateBitmap(width, height, 1, 1, null);
        try
        {
            if (mask == 0) return 0;
            bgra[..(width * height * 4)].CopyTo(new Span<byte>(bits, width * height * 4));
            var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
            return CreateIconIndirect(&info);
        }
        finally
        {
            DeleteObject(color);
            if (mask != 0) DeleteObject(mask);
        }
    }

    private const uint InprocServer = 1;
    private const uint FlashStop = 0;
    private const uint FlashAll = 3;
    private const uint BitFields = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private const uint ThbIcon = 2;
    private const uint ThbTooltip = 4;
    private const uint ThbFlags = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButtonNative
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public nint Icon;
        public fixed char Tip[260];
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapV5Header
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public uint AlphaMask;
        public uint CSType;
        public fixed byte Endpoints[36];
        public uint GammaRed;
        public uint GammaGreen;
        public uint GammaBlue;
        public uint Intent;
        public uint ProfileData;
        public uint ProfileSize;
        public uint Reserved;
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
    [DllImport("user32.dll")] private static extern int FlashWindowEx(FlashInfo* info);
    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(IconInfo* info);
    [DllImport("user32.dll")] private static extern int DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, BitmapV5Header* header, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
}
