using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The Windows clipboard as bytes per format id: the thin Win32 layer under Oriel's <c>clipboard</c>.</summary>
/// <remarks>Every call opens the clipboard for its own duration and closes it again. Which bytes each format holds is
/// the caller's business.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsClipboard
{
    /// <summary>ANSI text; <c>CF_TEXT</c>.</summary>
    public const uint CfText = 1;

    /// <summary>A <c>BITMAPINFO</c> and its pixels; <c>CF_DIB</c>.</summary>
    public const uint CfDib = 8;

    /// <summary>NUL-terminated UTF-16 text; <c>CF_UNICODETEXT</c>.</summary>
    public const uint CfUnicodeText = 13;

    /// <summary>A <c>BITMAPV5HEADER</c> and its pixels; <c>CF_DIBV5</c>.</summary>
    public const uint CfDibV5 = 17;

    private const uint GmemMoveable = 0x0002;
    private const int OpenAttempts = 10;
    private const int RetryDelayMs = 15;

    /// <summary>Replaces the clipboard's contents with <paramref name="items"/>, all in one open; none just empties it.</summary>
    /// <exception cref="InvalidOperationException">Another process holds the clipboard, or the system refused a block.</exception>
    public static void Write(ReadOnlySpan<(uint Format, byte[] Data)> items)
    {
        Open();
        try
        {
            if (EmptyClipboard() == 0) throw new InvalidOperationException("The clipboard could not be emptied.");
            foreach (var (format, data) in items) SetData(format, data);
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Empties the clipboard.</summary>
    public static void Clear() => Write([]);

    /// <summary>The bytes of <paramref name="format"/>, or null if the clipboard has none. Text formats include their
    /// terminating NUL, and the block may be larger than the data.</summary>
    public static byte[]? Read(uint format)
    {
        if (IsClipboardFormatAvailable(format) == 0) return null;
        Open();
        try
        {
            var handle = GetClipboardData(format);
            if (handle == 0) return null;
            var size = (int)GlobalSize(handle);
            var source = GlobalLock(handle);
            if (source is null) return null;
            try { return new ReadOnlySpan<byte>(source, size).ToArray(); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>The ids of the formats on the clipboard, in the order the owner set them. Windows adds the formats it
    /// can synthesize, such as <c>CF_UNICODETEXT</c> beside <c>CF_TEXT</c>.</summary>
    public static uint[] Formats()
    {
        Open();
        try
        {
            var formats = new List<uint>();
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format)) formats.Add(format);
            return [.. formats];
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Whether the clipboard holds <paramref name="format"/>.</summary>
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

    private static void Open()
    {
        for (var attempt = 1; ; attempt++)
        {
            if (OpenClipboard(0) != 0) return;
            if (attempt == OpenAttempts) throw new InvalidOperationException("The clipboard is in use by another process.");
            Thread.Sleep(RetryDelayMs);
        }
    }

    private static void SetData(uint format, byte[] data)
    {
        // A zero-length GMEM_MOVEABLE block is discarded; one byte keeps it valid.
        var handle = GlobalAlloc(GmemMoveable, (nuint)Math.Max(data.Length, 1));
        if (handle == 0) throw new InvalidOperationException("Memory for the clipboard could not be allocated.");
        var target = GlobalLock(handle);
        if (target is null)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("Memory for the clipboard could not be locked.");
        }
        data.CopyTo(new Span<byte>(target, data.Length));
        GlobalUnlock(handle);
        // After a successful SetClipboardData the clipboard owns the block.
        if (SetClipboardData(format, handle) != 0) return;
        GlobalFree(handle);
        throw new InvalidOperationException("The clipboard refused the data.");
    }

    [DllImport("user32.dll")] private static extern int OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern int CloseClipboard();
    [DllImport("user32.dll")] private static extern int EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern int IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint RegisterClipboardFormatW(char* name);
    [DllImport("user32.dll")] private static extern int GetClipboardFormatNameW(uint format, char* buffer, int capacity);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern void* GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern int GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
}
