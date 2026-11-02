using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The Windows clipboard as bytes per format id: the thin Win32 layer under Riftway's <c>clipboard</c>, with
/// Chromium's <c>ClipboardWin</c> rules for opening it and for how much it reads.</summary>
/// <remarks>Every call opens the clipboard for its own duration and closes it again. As in Chromium, a clipboard that
/// another process holds reads as empty and drops a write, rather than throwing.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsClipboard
{
    /// <summary>ANSI text; <c>CF_TEXT</c>.</summary>
    public const uint CfText = 1;

    /// <summary>A <c>BITMAPINFO</c> and its pixels; <c>CF_DIB</c>.</summary>
    public const uint CfDib = 8;

    /// <summary>NUL-terminated UTF-16 text; <c>CF_UNICODETEXT</c>.</summary>
    public const uint CfUnicodeText = 13;

    /// <summary>A <c>DROPFILES</c> file list; <c>CF_HDROP</c>.</summary>
    public const uint CfHDrop = 15;

    /// <summary>A <c>BITMAPV5HEADER</c> and its pixels; <c>CF_DIBV5</c>.</summary>
    public const uint CfDibV5 = 17;

    // Chromium's kMaxClipboardSize: larger blocks read as missing (crbug.com/1164680).
    private const long MaxBytes = 256L * 1024 * 1024;
    private const uint GmemMoveable = 0x0002;
    private const int OpenAttempts = 5;
    private const int RetryDelayMs = 5;

    /// <summary>Empties the clipboard and puts <paramref name="items"/> on it, in order, all in one open, with
    /// <paramref name="owner"/> as the clipboard owner (0 for none). Nothing happens if the clipboard cannot be opened; a
    /// block the system refuses is skipped.</summary>
    public static void Write(ReadOnlySpan<(uint Format, byte[] Data)> items, nint owner = 0)
    {
        if (!Open(owner)) return;
        try
        {
            EmptyClipboard();
            foreach (var (format, data) in items) SetData(format, data);
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Empties the clipboard, opening it as <paramref name="owner"/>; nothing happens if it cannot be opened.</summary>
    public static void Clear(nint owner = 0) => Write([], owner);

    /// <summary>A message-only window to own the clipboard, as Chromium's <c>ClipboardWin</c> keeps one. Create it on a
    /// thread that pumps messages: other apps emptying the clipboard send it <c>WM_DESTROYCLIPBOARD</c> and wait.</summary>
    /// <exception cref="InvalidOperationException">Windows refused the window.</exception>
    public static nint CreateOwnerWindow()
    {
        const string ClassName = "RiftwayClipboardOwner";
        fixed (char* name = ClassName)
        {
            var cls = new WndClass
            {
                Size = (uint)sizeof(WndClass),
                // Chromium's owner answers every clipboard message with 0, which is DefWindowProc's answer too.
                WndProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW"),
                Instance = GetModuleHandleW(null),
                ClassName = name,
            };
            RegisterClassExW(&cls);
            var hwnd = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, cls.Instance, 0);
            return hwnd != 0 ? hwnd : throw new InvalidOperationException("The clipboard owner window could not be created.");
        }
    }

    /// <summary>The whole block of <paramref name="format"/>, or null if the clipboard has none, cannot be opened, or
    /// the block is over 256 MiB. Text formats include their terminating NUL, and the block may be larger than the
    /// data.</summary>
    public static byte[]? Read(uint format)
    {
        if (!Open(0)) return null;
        try { return Copy(DataWithLimit(format)); }
        finally { CloseClipboard(); }
    }

    /// <summary>The ids of the formats on the clipboard, in the order the owner set them, or none if it cannot be
    /// opened. Windows adds the formats it can synthesize, such as <c>CF_UNICODETEXT</c> beside <c>CF_TEXT</c>.</summary>
    public static uint[] Formats()
    {
        if (!Open(0)) return [];
        try
        {
            var formats = new List<uint>();
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format)) formats.Add(format);
            return [.. formats];
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Whether the clipboard holds <paramref name="format"/>, synthesized ones included. Needs no open.</summary>
    public static bool IsAvailable(uint format) => IsClipboardFormatAvailable(format) != 0;

    /// <summary>The id Windows gives the custom format <paramref name="name"/>, registering it on first use.</summary>
    /// <exception cref="InvalidOperationException">The system refused the name.</exception>
    public static uint RegisterFormat(string name)
    {
        fixed (char* text = name)
        {
            var id = RegisterClipboardFormatW(text);
            return id != 0 ? id : throw new InvalidOperationException($"The clipboard format '{name}' could not be registered.");
        }
    }

    /// <summary>The name of a registered format, or null for the predefined <c>CF_</c> ids, which have none.</summary>
    public static string? FormatName(uint format)
    {
        const int Capacity = 256;
        var buffer = stackalloc char[Capacity];
        var length = GetClipboardFormatNameW(format, buffer, Capacity);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>The paths in the <c>CF_HDROP</c> block, as <c>DragQueryFileW</c> gives them; null if there is none.</summary>
    public static string[]? ReadDropFiles()
    {
        if (!Open(0)) return null;
        try
        {
            var drop = GetClipboardData(CfHDrop);
            if (drop == 0 || (long)GlobalSize(drop) > MaxBytes) return null;
            var names = new List<string>();
            var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            for (var i = 0u; i < count; i++)
            {
                var length = DragQueryFileW(drop, i, null, 0);
                if (length == 0) continue;
                var buffer = new char[length + 1];
                fixed (char* b = buffer)
                    if (DragQueryFileW(drop, i, b, length + 1) == 0) continue;
                names.Add(new string(buffer, 0, (int)length));
            }
            return [.. names];
        }
        finally { CloseClipboard(); }
    }

    /// <summary>The <c>CF_DIB</c> block drawn into a top-down 32-bit bitmap with <c>SetDIBitsToDevice</c>, as Chromium's
    /// <c>ReadBitmapInternal</c> does, so GDI handles every bit depth and palette; null if there is none or GDI cannot
    /// draw it. <paramref name="bitCount"/> is the block's own depth.</summary>
    public static byte[]? ReadDibPixels(out int width, out int height, out int bitCount)
    {
        width = height = bitCount = 0;
        if (!Open(0)) return null;
        try
        {
            var handle = GetClipboardData(CfDib);
            if (handle == 0) return null;
            var info = (BitmapInfoHeader*)GlobalLock(handle);
            if (info is null) return null;
            try { return Draw(info, out width, out height, out bitCount); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    private static byte[]? Draw(BitmapInfoHeader* info, out int width, out int height, out int bitCount)
    {
        width = info->Width;
        height = 0;
        bitCount = info->BitCount;
        if (info->Height == int.MinValue) return null;
        height = Math.Abs(info->Height);
        var bytesPerPixel = Math.Max(bitCount / 8, 1);
        if (width < 0 || (long)width * height * bytesPerPixel > MaxBytes) return null;
        int colorTable;
        switch (bitCount)
        {
            case 1 or 4 or 8: colorTable = info->ClrUsed != 0 ? (int)info->ClrUsed : 1 << bitCount; break;
            case 16 or 32: colorTable = info->Compression == 3 /* BI_BITFIELDS */ ? 3 : 0; break;
            case 24: colorTable = 0; break;
            default: return null;
        }
        var bits = (byte*)info + info->Size + colorTable * 4;

        // skia::CreateHBitmapXRGB8888: a top-down 32-bit DIB section.
        var target = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        void* pixels;
        var bitmap = CreateDIBSection(0, &target, 0, &pixels, 0, 0);
        if (bitmap == 0 || pixels is null) return null;
        try
        {
            var dc = CreateCompatibleDC(0);
            var old = SelectObject(dc, bitmap);
            SetDIBitsToDevice(dc, 0, 0, (uint)width, (uint)height, 0, 0, 0, (uint)height, bits, info, 0 /* DIB_RGB_COLORS */);
            SelectObject(dc, old);
            DeleteDC(dc);
            return new ReadOnlySpan<byte>(pixels, width * height * 4).ToArray();
        }
        finally { DeleteObject(bitmap); }
    }

    private static nint DataWithLimit(uint format)
    {
        var handle = GetClipboardData(format);
        return handle != 0 && (long)GlobalSize(handle) <= MaxBytes ? handle : 0;
    }

    private static byte[]? Copy(nint handle)
    {
        if (handle == 0) return null;
        var size = (int)GlobalSize(handle);
        var source = GlobalLock(handle);
        // A zero-length block is discarded memory: Chromium reads it as empty.
        if (source is null) return [];
        try { return new ReadOnlySpan<byte>(source, size).ToArray(); }
        finally { GlobalUnlock(handle); }
    }

    // Reads open with no owner, as Chromium's worker-thread reads do; writes pass the owner window.
    private static bool Open(nint owner)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(owner) != 0) return true;
            Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    private static void SetData(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        if (handle == 0) return;
        if (data.Length > 0)
        {
            var target = GlobalLock(handle);
            if (target is null)
            {
                GlobalFree(handle);
                return;
            }
            data.CopyTo(new Span<byte>(target, data.Length));
            GlobalUnlock(handle);
        }
        // After a successful SetClipboardData the clipboard owns the block.
        if (SetClipboardData(format, handle) == 0) GlobalFree(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClass
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint IconSmall;
    }

    [DllImport("user32.dll")] private static extern ushort RegisterClassExW(WndClass* cls);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
    [DllImport("user32.dll")] private static extern int OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern int CloseClipboard();
    [DllImport("user32.dll")] private static extern int EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern int IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint RegisterClipboardFormatW(char* name);
    [DllImport("user32.dll")] private static extern int GetClipboardFormatNameW(uint format, char* buffer, int capacity);
    [DllImport("shell32.dll")] private static extern uint DragQueryFileW(nint drop, uint index, char* file, uint capacity);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int SetDIBitsToDevice(nint dc, int x, int y, uint width, uint height, int srcX, int srcY, uint startScan, uint lines, void* bits, BitmapInfoHeader* info, uint usage);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern void* GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern int GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
}
