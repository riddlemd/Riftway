using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The icon and title for the drag image of <see cref="WindowsDrag.Run"/>.</summary>
/// <param name="IconWidth">The icon's width in pixels, at <paramref name="Scale"/>.</param>
/// <param name="IconHeight">The icon's height in pixels, at <paramref name="Scale"/>.</param>
/// <param name="IconBgra">The icon as premultiplied BGRA, top row first.</param>
/// <param name="Title">The text beside the icon: Electron passes the first file's path.</param>
/// <param name="Scale">The device scale factor to paint at.</param>
public sealed record WindowsDragImage(int IconWidth, int IconHeight, byte[] IconBgra, string Title, double Scale);

/// <summary>Where Chromium's <c>button_drag_utils::SetDragImage</c> button puts the icon and the title, in DIPs.</summary>
/// <param name="Width">The image's width.</param>
/// <param name="Height">The image's height.</param>
/// <param name="IconX">The icon's left edge.</param>
/// <param name="IconY">The icon's top edge.</param>
/// <param name="TextX">The title's left edge.</param>
/// <param name="TextWidth">The title's width; it is elided at the end to fit.</param>
/// <param name="OffsetX">The cursor's distance from the image's left edge.</param>
/// <param name="OffsetY">The cursor's distance from the image's top edge.</param>
public readonly record struct WindowsDragLayout(int Width, int Height, int IconX, int IconY, int TextX, int TextWidth, int OffsetX, int OffsetY);

/// <summary>Drags files out of the app with OLE, the way Electron's <c>DragFileItems</c> does through Chromium's
/// <c>DesktopDragDropClientWin</c>: a data object holding only <c>CF_HDROP</c>, a drag image set through
/// <c>IDragSourceHelper</c>, and <c>DoDragDrop</c> allowing copy and link.</summary>
/// <remarks><c>DoDragDrop</c> needs an OLE (STA) thread and CEF's UI thread is in the MTA, so the drag runs on an STA
/// thread of its own that shares the UI thread's input state, while the UI thread waits dispatching its messages.
/// <para>The layout and <c>CF_HDROP</c> helpers are pure and run anywhere; the drag itself is Windows only.</para></remarks>
public static unsafe class WindowsDrag
{
    /// <summary><c>DROPEFFECT_COPY | DROPEFFECT_LINK</c>: Electron passes <c>DRAG_COPY | DRAG_LINK</c>.</summary>
    public const uint AllowedEffects = 1 | 4;

    // button_drag_utils and views::LayoutProvider: INSETS_LABEL_BUTTON, DISTANCE_RELATED_LABEL_HORIZONTAL, kLinkDragImageMaxWidth.
    private const int InsetX = 6;
    private const int InsetY = 5;
    private const int ImageLabelSpacing = 12;
    private const int MaxWidth = 150;
    // kColorTextfieldForeground and kColorTextfieldBackground in the light theme.
    private const uint TextColor = 0x202124;
    private const uint HaloColor = 0xFFFFFF;

    private const ushort CfHdrop = 15;
    private const uint TymedNull = 0, TymedHglobal = 1, TymedFile = 2, TymedIstream = 4, TymedIstorage = 8, TymedGdi = 16, TymedMfpict = 32, TymedEnhmf = 64;
    private const int SOk = 0;
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EPointer = unchecked((int)0x80004003);
    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    private const int DragDropSDrop = 0x00040100;
    private const int DragDropSCancel = 0x00040101;
    private const int DragDropSUseDefaultCursors = 0x00040102;

    private static readonly Guid s_iidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid s_iidDataObject = new("0000010e-0000-0000-C000-000000000046");
    private static readonly Guid s_iidDropSource = new("00000121-0000-0000-C000-000000000046");
    private static readonly Guid s_clsidDragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    private static readonly Guid s_iidDragSourceHelper = new("DE5BF786-477A-11D2-839D-00C04FD918D0");

    private static readonly void** s_dataObjectVtbl = DataObjectVtbl();
    // Keys reach the UI thread's focused window, not OLE's loop on the drag thread, so the UI pump relays Escape.
    private static volatile bool s_escapePressed;
    private static uint s_dragThread;
    private static readonly void** s_dropSourceVtbl = DropSourceVtbl();

    /// <summary>The <c>CF_HDROP</c> bytes for <paramref name="paths"/>: a <c>DROPFILES</c> header with wide names,
    /// then each path and its NUL, then one more NUL. Chromium's <c>clipboard_util::CreateStorageForFileNames</c>.</summary>
    public static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        const int header = 20; // sizeof(DROPFILES)
        var size = header + sizeof(char);
        foreach (var path in paths) size += (path.Length + 1) * sizeof(char);
        var bytes = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, header);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1);
        var offset = header;
        foreach (var path in paths)
        {
            MemoryMarshal.AsBytes(path.AsSpan()).CopyTo(bytes.AsSpan(offset));
            offset += (path.Length + 1) * sizeof(char);
        }
        return bytes;
    }

    /// <summary>Lays out the drag image as Chromium's <c>LabelButton</c> does for <c>SetDragImage</c>: label-button
    /// insets, the icon left and vertically centred, the title after it, 150 DIPs wide at most, cursor at the centre.</summary>
    /// <param name="iconWidth">The icon's width in DIPs.</param>
    /// <param name="iconHeight">The icon's height in DIPs.</param>
    /// <param name="textWidth">The title's unelided width in DIPs.</param>
    /// <param name="textHeight">The title's line height in DIPs.</param>
    public static WindowsDragLayout Layout(int iconWidth, int iconHeight, int textWidth, int textHeight)
    {
        var hasIcon = iconWidth > 0 && iconHeight > 0;
        var width = InsetX * 2 + iconWidth + (hasIcon && textWidth > 0 ? ImageLabelSpacing : 0) + textWidth;
        width = Math.Min(MaxWidth, width);
        var height = Math.Max(iconHeight, textHeight) + InsetY * 2;
        var iconShown = Math.Min(iconWidth, width - InsetX * 2);
        var textX = InsetX + (hasIcon ? iconShown + ImageLabelSpacing : 0);
        var textShown = Math.Max(0, Math.Min(textWidth, width - InsetX - textX));
        return new WindowsDragLayout(width, height, InsetX, (height - iconHeight) / 2, textX, textShown, width / 2, height / 2);
    }

    /// <summary>Drags <paramref name="files"/> until the user drops or cancels, and returns the drop effect
    /// (<c>DROPEFFECT_NONE</c> unless dropped). Call it from the UI thread: it dispatches that thread's window
    /// messages meanwhile, as Chromium's nested drag loop does.</summary>
    /// <param name="files">The paths, in order; at least one.</param>
    /// <param name="image">The drag image, or null for the shell's default.</param>
    [SupportedOSPlatform("windows")]
    public static uint Run(IReadOnlyList<string> files, WindowsDragImage? image)
    {
        ArgumentNullException.ThrowIfNull(files);
        var uiThread = GetCurrentThreadId();
        var effect = 0u;
        Exception? error = null;
        using var done = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            try { effect = DragOnThisThread(files, image, uiThread); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        }) { IsBackground = true, Name = "Oriel drag" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        WaitPumping(done);
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        return effect;
    }

    /// <summary>The device scale factor of <paramref name="hwnd"/>'s monitor; 1 for no window.</summary>
    [SupportedOSPlatform("windows")]
    public static double ScaleFor(nint hwnd) => hwnd != 0 && GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    private static uint DragOnThisThread(IReadOnlyList<string> files, WindowsDragImage? image, uint uiThread)
    {
        var ole = OleInitialize(null);
        if (ole < 0) throw new COMException("OleInitialize failed.", ole);
        // Shared input state lets OLE's capture window take the mouse the UI thread's window holds while pressed.
        var attached = AttachThreadInput(GetCurrentThreadId(), uiThread, 1) != 0;
        var data = CreateDataObject();
        var source = CreateObject(s_dropSourceVtbl, null);
        try
        {
            var bytes = DropFiles(files);
            var medium = new StgMedium { Tymed = TymedHglobal, Handle = ToHglobal(bytes) };
            var format = new FormatEtc { Format = CfHdrop, Aspect = 1, Index = -1, Tymed = TymedHglobal };
            Store(data).Contents.Add((format, medium));
            if (image is not null) SetDragImage(image, data);
            s_escapePressed = false;
            s_dragThread = GetCurrentThreadId();
            uint effect;
            var result = DoDragDrop(data, source, AllowedEffects, &effect);
            return result == DragDropSDrop ? effect : 0;
        }
        finally
        {
            // A target may still hold the data object; cut its proxies so the last reference is ours.
            CoDisconnectObject(data, 0);
            Release(data);
            Release(source);
            if (attached) AttachThreadInput(GetCurrentThreadId(), uiThread, 0);
            OleUninitialize();
        }
    }

    // Blocks until signalled, dispatching this thread's messages; a WM_QUIT seen meanwhile is posted again afterwards.
    private static void WaitPumping(ManualResetEvent done)
    {
        var handle = done.SafeWaitHandle.DangerousGetHandle();
        int? quit = null;
        Msg msg;
        while (!done.WaitOne(0))
        {
            if (MsgWaitForMultipleObjectsEx(1, &handle, 0xFFFFFFFF, 0x04FF, 4) == 0) break;
            while (PeekMessageW(&msg, 0, 0, 0, 1) != 0)
            {
                if (msg.Message == 0x0012) { quit = (int)msg.WParam; continue; }
                // OLE's loop on the UI thread would take Escape (WM_KEYDOWN, VK_ESCAPE) itself; the WM_NULL wakes it to ask.
                if (msg.Message == 0x0100 && msg.WParam == 0x1B)
                {
                    s_escapePressed = true;
                    PostThreadMessageW(s_dragThread, 0, 0, 0);
                    continue;
                }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        if (quit is { } code) PostQuitMessage(code);
    }

    private static nint ToHglobal(byte[] bytes)
    {
        var global = GlobalAlloc(0x0042, (nuint)bytes.Length); // GHND
        if (global == 0) throw new OutOfMemoryException();
        var target = GlobalLock(global);
        bytes.CopyTo(new Span<byte>(target, bytes.Length));
        GlobalUnlock(global);
        return global;
    }

    // ---- Drag image ----

    private static void SetDragImage(WindowsDragImage image, void* data)
    {
        var bitmap = PaintDragImage(image, out var layout, out var pixelWidth, out var pixelHeight);
        if (bitmap == 0) return;
        void* helper;
        var clsid = s_clsidDragDropHelper;
        var iid = s_iidDragSourceHelper;
        if (CoCreateInstance(&clsid, null, 1, &iid, &helper) < 0)
        {
            DeleteObject(bitmap);
            return;
        }
        // Chromium passes the DIP cursor offset unscaled, against a bitmap in pixels.
        var sdi = new ShDragImage { Width = pixelWidth, Height = pixelHeight, OffsetX = layout.OffsetX, OffsetY = layout.OffsetY, Bitmap = bitmap, ColorKey = 0xFFFFFFFF };
        // InitializeFromBitmap takes the bitmap, even when it fails.
        ((delegate* unmanaged[Stdcall]<void*, ShDragImage*, void*, int>)(*(void***)helper)[3])(helper, &sdi, data);
        Release(helper);
    }

    // Returns an HBITMAP of straight-alpha BGRA, as InitializeFromBitmap expects; 0 on failure.
    private static nint PaintDragImage(WindowsDragImage image, out WindowsDragLayout layout, out int width, out int height)
    {
        var scale = image.Scale > 0 ? image.Scale : 1.0;
        var dc = CreateCompatibleDC(0);
        var font = CreateFontW(-(int)Math.Round(12 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
        var oldFont = SelectObject(dc, font);
        try
        {
            Size extent = default;
            TextMetric metric = default;
            fixed (char* title = image.Title) GetTextExtentPoint32W(dc, title, image.Title.Length, &extent);
            GetTextMetricsW(dc, &metric);
            int Dip(int pixels) => (int)Math.Ceiling(pixels / scale);
            layout = Layout(Dip(image.IconWidth), Dip(image.IconHeight), image.Title.Length > 0 ? Dip(extent.Cx) : 0, Dip(metric.Height));
            width = (int)Math.Ceiling(layout.Width * scale);
            height = (int)Math.Ceiling(layout.Height * scale);
            var canvas = new byte[width * height * 4];
            Blend(canvas, width, height, image.IconBgra, image.IconWidth, image.IconHeight,
                (int)Math.Round(layout.IconX * scale), (height - image.IconHeight) / 2);

            var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            void* bits;
            var textBitmap = CreateDIBSection(dc, &header, 0, &bits, 0, 0);
            if (textBitmap == 0) { width = height = 0; return 0; }
            var oldBitmap = SelectObject(dc, textBitmap);
            new Span<byte>(bits, width * height * 4).Clear();
            SetTextColor(dc, 0xFFFFFF);
            SetBkMode(dc, 1); // TRANSPARENT
            var rect = new Rect { Left = (int)Math.Round(layout.TextX * scale), Top = 0, Bottom = height };
            rect.Right = rect.Left + (int)Math.Round(layout.TextWidth * scale);
            // DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS
            if (layout.TextWidth > 0) fixed (char* title = image.Title) DrawTextW(dc, title, image.Title.Length, &rect, 0x20 | 0x4 | 0x800 | 0x8000);
            GdiFlush();
            var coverage = new float[width * height];
            var text = new ReadOnlySpan<byte>(bits, width * height * 4);
            for (var i = 0; i < coverage.Length; i++) coverage[i] = text[i * 4 + 1] / 255f;
            SelectObject(dc, oldBitmap);
            DeleteObject(textBitmap);

            // views::Label text shadows: ten ShadowValues of blur 2 in the background colour, then the text.
            var halo = Blur(coverage, width, height, scale);
            for (var i = 0; i < halo.Length; i++) halo[i] = 1 - MathF.Pow(1 - halo[i], 10);
            Fill(canvas, halo, HaloColor);
            Fill(canvas, coverage, TextColor);
            return ToStraightBitmap(dc, canvas, width, height);
        }
        finally
        {
            SelectObject(dc, oldFont);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    private static void Blend(byte[] canvas, int width, int height, byte[] icon, int iconWidth, int iconHeight, int left, int top)
    {
        for (var y = 0; y < iconHeight; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= height) continue;
            for (var x = 0; x < iconWidth; x++)
            {
                var cx = left + x;
                if (cx < 0 || cx >= width) continue;
                int s = (y * iconWidth + x) * 4, d = (cy * width + cx) * 4;
                var inverse = 255 - icon[s + 3];
                for (var c = 0; c < 4; c++) canvas[d + c] = (byte)(icon[s + c] + (canvas[d + c] * inverse + 127) / 255);
            }
        }
    }

    // Source-over of a solid colour with per-pixel coverage, on premultiplied BGRA.
    private static void Fill(byte[] canvas, float[] coverage, uint rgb)
    {
        float r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        for (var i = 0; i < coverage.Length; i++)
        {
            var a = Math.Clamp(coverage[i], 0f, 1f);
            if (a <= 0) continue;
            var d = i * 4;
            canvas[d] = (byte)(b * a + canvas[d] * (1 - a) + 0.5f);
            canvas[d + 1] = (byte)(g * a + canvas[d + 1] * (1 - a) + 0.5f);
            canvas[d + 2] = (byte)(r * a + canvas[d + 2] * (1 - a) + 0.5f);
            canvas[d + 3] = (byte)(255 * a + canvas[d + 3] * (1 - a) + 0.5f);
        }
    }

    // Skia's blur sigma for a ShadowValue blur of 2 is 1, in DIPs.
    private static float[] Blur(float[] source, int width, int height, double scale)
    {
        var sigma = (float)scale;
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++) sum += kernel[i + radius] = MathF.Exp(-i * i / (2 * sigma * sigma));
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        var pass = new float[source.Length];
        var result = new float[source.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var acc = 0f;
                for (var k = -radius; k <= radius; k++) if (x + k >= 0 && x + k < width) acc += source[y * width + x + k] * kernel[k + radius];
                pass[y * width + x] = acc;
            }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var acc = 0f;
                for (var k = -radius; k <= radius; k++) if (y + k >= 0 && y + k < height) acc += pass[(y + k) * width + x] * kernel[k + radius];
                result[y * width + x] = acc;
            }
        return result;
    }

    private static nint ToStraightBitmap(nint dc, byte[] canvas, int width, int height)
    {
        var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        void* bits;
        var bitmap = CreateDIBSection(dc, &header, 0, &bits, 0, 0);
        if (bitmap == 0) return 0;
        var target = new Span<byte>(bits, canvas.Length);
        for (var i = 0; i < canvas.Length; i += 4)
        {
            var a = canvas[i + 3];
            for (var c = 0; c < 3; c++) target[i + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, (canvas[i + c] * 255 + a / 2) / a);
            target[i + 3] = a;
        }
        GdiFlush();
        return bitmap;
    }

    // ---- COM objects ----

    private struct ComObject
    {
        public void** Vtbl;
        public int Refs;
        public nint Handle;
    }

    private sealed class DataStore
    {
        public readonly List<(FormatEtc Format, StgMedium Medium)> Contents = [];
    }

    private static void* CreateDataObject() => CreateObject(s_dataObjectVtbl, new DataStore());

    private static void* CreateObject(void** vtbl, object? state)
    {
        var com = (ComObject*)NativeMemory.AllocZeroed((nuint)sizeof(ComObject));
        com->Vtbl = vtbl;
        com->Refs = 1;
        com->Handle = state is null ? 0 : GCHandle.ToIntPtr(GCHandle.Alloc(state));
        return com;
    }

    private static DataStore Store(void* self) => (DataStore)GCHandle.FromIntPtr(((ComObject*)self)->Handle).Target!;

    private static void Release(void* com) => ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)com)[2])(com);

    private static void** DataObjectVtbl()
    {
        var vtbl = (void**)NativeMemory.Alloc((nuint)(12 * sizeof(void*)));
        vtbl[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&DataObjectQueryInterface;
        vtbl[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        vtbl[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&DataObjectRelease;
        vtbl[3] = (delegate* unmanaged[Stdcall]<void*, FormatEtc*, StgMedium*, int>)&GetData;
        vtbl[4] = (delegate* unmanaged[Stdcall]<void*, FormatEtc*, StgMedium*, int>)&GetDataHere;
        vtbl[5] = (delegate* unmanaged[Stdcall]<void*, FormatEtc*, int>)&QueryGetData;
        vtbl[6] = (delegate* unmanaged[Stdcall]<void*, FormatEtc*, FormatEtc*, int>)&GetCanonicalFormatEtc;
        vtbl[7] = (delegate* unmanaged[Stdcall]<void*, FormatEtc*, StgMedium*, int, int>)&SetData;
        vtbl[8] = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)&EnumFormatEtc;
        vtbl[9] = (delegate* unmanaged[Stdcall]<void*, FormatEtc*, uint, void*, uint*, int>)&DAdvise;
        vtbl[10] = (delegate* unmanaged[Stdcall]<void*, uint, int>)&DUnadvise;
        vtbl[11] = (delegate* unmanaged[Stdcall]<void*, void**, int>)&EnumDAdvise;
        return vtbl;
    }

    private static void** DropSourceVtbl()
    {
        var vtbl = (void**)NativeMemory.Alloc((nuint)(5 * sizeof(void*)));
        vtbl[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&DropSourceQueryInterface;
        vtbl[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        vtbl[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&DropSourceRelease;
        vtbl[3] = (delegate* unmanaged[Stdcall]<void*, int, uint, int>)&QueryContinueDrag;
        vtbl[4] = (delegate* unmanaged[Stdcall]<void*, uint, int>)&GiveFeedback;
        return vtbl;
    }

    private static int QueryInterface(void* self, Guid* iid, void** result, in Guid own)
    {
        if (result is null) return EPointer;
        if (*iid == s_iidUnknown || *iid == own)
        {
            *result = self;
            Interlocked.Increment(ref ((ComObject*)self)->Refs);
            return SOk;
        }
        *result = null;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DataObjectQueryInterface(void* self, Guid* iid, void** result) => QueryInterface(self, iid, result, s_iidDataObject);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DropSourceQueryInterface(void* self, Guid* iid, void** result) => QueryInterface(self, iid, result, s_iidDropSource);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => (uint)Interlocked.Increment(ref ((ComObject*)self)->Refs);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint DataObjectRelease(void* self)
    {
        var com = (ComObject*)self;
        var refs = Interlocked.Decrement(ref com->Refs);
        if (refs > 0) return (uint)refs;
        var handle = GCHandle.FromIntPtr(com->Handle);
        foreach (var (_, medium) in ((DataStore)handle.Target!).Contents)
        {
            var copy = medium;
            ReleaseStgMedium(&copy);
        }
        handle.Free();
        NativeMemory.Free(com);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint DropSourceRelease(void* self)
    {
        var refs = Interlocked.Decrement(ref ((ComObject*)self)->Refs);
        if (refs == 0) NativeMemory.Free(self);
        return (uint)refs;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetData(void* self, FormatEtc* format, StgMedium* medium)
    {
        if (format is null || medium is null) return EPointer;
        foreach (var (stored, held) in Store(self).Contents)
        {
            if (stored.Format != format->Format || stored.Index != format->Index || (stored.Tymed & format->Tymed) == 0) continue;
            if (held.Tymed == TymedNull) return DvEFormatEtc;
            *medium = Duplicate(stored.Format, held);
            return SOk;
        }
        return DvEFormatEtc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetDataHere(void* self, FormatEtc* format, StgMedium* medium) => DvEFormatEtc;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryGetData(void* self, FormatEtc* format)
    {
        if (format is null) return EPointer;
        foreach (var (stored, _) in Store(self).Contents)
            if (stored.Format == format->Format) return SOk;
        return DvEFormatEtc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetCanonicalFormatEtc(void* self, FormatEtc* format, FormatEtc* result)
    {
        if (result is not null) result->Device = null;
        return ENotImpl;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetData(void* self, FormatEtc* format, StgMedium* medium, int release)
    {
        if (format is null || medium is null) return EPointer;
        var contents = Store(self).Contents;
        if (format->Device is null)
        {
            for (var i = 0; i < contents.Count; i++)
            {
                var c = contents[i].Format;
                if (c.Format != format->Format || c.Aspect != format->Aspect || c.Index != format->Index || c.Tymed != format->Tymed) continue;
                var old = contents[i].Medium;
                ReleaseStgMedium(&old);
                contents.RemoveAt(i);
                break;
            }
        }
        var kept = release != 0 ? *medium : Duplicate(format->Format, *medium);
        // Newest first, as Chromium's DataObjectImpl keeps them.
        contents.Insert(0, (*format with { Device = null }, kept));
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumFormatEtc(void* self, uint direction, void** enumerator)
    {
        if (enumerator is null) return EPointer;
        if (direction != 1) return ENotImpl; // DATADIR_GET
        var contents = Store(self).Contents;
        var formats = stackalloc FormatEtc[Math.Max(1, contents.Count)];
        for (var i = 0; i < contents.Count; i++) formats[i] = contents[i].Format;
        return SHCreateStdEnumFmtEtc((uint)contents.Count, formats, enumerator);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DAdvise(void* self, FormatEtc* format, uint advf, void* sink, uint* connection) => OleEAdviseNotSupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DUnadvise(void* self, uint connection) => OleEAdviseNotSupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumDAdvise(void* self, void** enumerator) => OleEAdviseNotSupported;

    // Chromium's DragSourceWin.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryContinueDrag(void* self, int escapePressed, uint keyState)
    {
        if (escapePressed != 0 || s_escapePressed) return DragDropSCancel;
        if ((keyState & (1 | 2)) == 0) return DragDropSDrop; // MK_LBUTTON, MK_RBUTTON
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GiveFeedback(void* self, uint effect) => DragDropSUseDefaultCursors;

    // Chromium's DuplicateMedium.
    private static StgMedium Duplicate(ushort format, StgMedium medium)
    {
        var copy = new StgMedium { Tymed = medium.Tymed, Release = medium.Release };
        switch (medium.Tymed)
        {
            case TymedHglobal or TymedMfpict or TymedGdi or TymedEnhmf or TymedFile:
                copy.Handle = OleDuplicateData(medium.Handle, format, 0);
                break;
            case TymedIstream:
                void* clone;
                var stream = (void*)medium.Handle;
                if (((delegate* unmanaged[Stdcall]<void*, void**, int>)(*(void***)stream)[13])(stream, &clone) >= 0 && clone is not null)
                    copy.Handle = (nint)clone;
                else
                {
                    copy.Handle = medium.Handle;
                    ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)stream)[1])(stream);
                }
                long position;
                ((delegate* unmanaged[Stdcall]<void*, long, uint, long*, int>)(*(void***)copy.Handle)[5])((void*)copy.Handle, 0, 0, &position);
                break;
            case TymedIstorage:
                copy.Handle = medium.Handle;
                ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)medium.Handle)[1])((void*)medium.Handle);
                break;
        }
        if (copy.Release is not null) ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)copy.Release)[1])(copy.Release);
        return copy;
    }

    // ---- Interop ----

    private struct FormatEtc
    {
        public ushort Format;
        public void* Device;
        public uint Aspect;
        public int Index;
        public uint Tymed;
    }

    private struct StgMedium
    {
        public uint Tymed;
        public nint Handle;
        public void* Release;
    }

    private struct ShDragImage
    {
        public int Width;
        public int Height;
        public int OffsetX;
        public int OffsetY;
        public nint Bitmap;
        public uint ColorKey;
    }

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

    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    private struct TextMetric
    {
        public int Height;
        public int Ascent;
        public int Descent;
        public int InternalLeading;
        public int ExternalLeading;
        public int AveCharWidth;
        public int MaxCharWidth;
        public int Weight;
        public int Overhang;
        public int DigitizedAspectX;
        public int DigitizedAspectY;
        public char FirstChar;
        public char LastChar;
        public char DefaultChar;
        public char BreakChar;
        public byte Italic;
        public byte Underlined;
        public byte StruckOut;
        public byte PitchAndFamily;
        public byte CharSet;
    }

    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(void* reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int DoDragDrop(void* data, void* source, uint okEffects, uint* effect);
    [DllImport("ole32.dll")] private static extern nint OleDuplicateData(nint source, ushort format, uint flags);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(StgMedium* medium);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
    [DllImport("ole32.dll")] private static extern int CoDisconnectObject(void* unknown, uint reserved);
    [DllImport("shell32.dll")] private static extern int SHCreateStdEnumFmtEtc(uint count, FormatEtc* formats, void** enumerator);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern void* GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern int GlobalUnlock(nint memory);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int AttachThreadInput(uint attach, uint attachTo, int doAttach);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll")] private static extern int PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int TranslateMessage(Msg* msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(Msg* msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern int PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int DrawTextW(nint dc, char* text, int length, Rect* rect, uint format);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, BitmapInfoHeader* header, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern int GetTextExtentPoint32W(nint dc, char* text, int length, Size* size);
    [DllImport("gdi32.dll")] private static extern int GetTextMetricsW(nint dc, TextMetric* metric);
    [DllImport("gdi32.dll")] private static extern int GdiFlush();
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW")] private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, char* face);

    private static nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face)
    {
        fixed (char* name = face) return CreateFontW(height, width, escapement, orientation, weight, italic, underline, strikeOut, charSet, outPrecision, clipPrecision, quality, pitchAndFamily, name);
    }
}
