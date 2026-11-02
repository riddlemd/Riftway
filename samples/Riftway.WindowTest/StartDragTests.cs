using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway;
using Riftway.Cef.Platform;

/// <summary>webContents.startDrag on Windows: a page's mousedown asks the main process to drag real files, the real
/// mouse carries them onto a window registered as an OLE drop target, and that target reads the drop back.</summary>
static class StartDragTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    private static bool? s_uiTaskRanInDrag;

    internal static async Task RunAsync(Action<string, bool, object?> check)
    {
        if (!OperatingSystem.IsWindows())
        {
            var thrown = Catch(() => BrowserWindow.GetAllWindows()[0].WebContents.StartDrag(new StartDragItem
            {
                File = "/tmp/x",
                Icon = NativeImage.CreateFromBitmap(new byte[16], new NativeImageBitmapOptions { Width = 2, Height = 2 }),
            }));
            if (OperatingSystem.IsMacOS())
            {
                check("startDrag starts a drag session on macOS without throwing", thrown is null, thrown?.ToString());
                // The NSDraggingSession runs AppKit's drag loop until a mouse-up, holding the main thread (and the quit) meanwhile;
                // WindowServer drops a posted mouse-up without its mouse-down, so the release goes onto NSApp's own queue.
                await Task.Delay(500);
                MacInterop.PostAppMouseUp();
                await Task.Delay(500);
            }
            else check("startDrag throws PlatformNotSupportedException on Linux (drift C40)", thrown is PlatformNotSupportedException, thrown?.GetType().Name);
            return;
        }

        var dir = Directory.CreateTempSubdirectory("riftway-drag-").FullName;
        var first = Path.Combine(dir, "first file.txt");
        var second = Path.Combine(dir, "second-ü.txt");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var icon = NativeImage.CreateFromBitmap(pixels, new NativeImageBitmapOptions { Width = 32, Height = 32 });
        var iconPath = Path.Combine(dir, "icon.png");
        File.WriteAllBytes(iconPath, icon.ToPng());

        var preload = Path.Combine(dir, "preload.js");
        File.WriteAllText(preload, "const { ipcRenderer } = riftway;\naddEventListener('mousedown', (e) => ipcRenderer.send('riftway-drag-test', e.button));\n");
        var page = Path.Combine(dir, "drag.html");
        File.WriteAllText(page, "<!doctype html><body style='margin:0;height:100vh;background:#ddd;user-select:none'>drag from here");
        var w = new BrowserWindow(new BrowserWindowOptions { X = 80, Y = 80, Width = 360, Height = 260, AlwaysOnTop = true, WebPreferences = new WebPreferences { Preload = preload } });
        await w.WebContents.LoadFile(page);
        await Task.Delay(500);
        var hwnd = w.GetNativeWindowHandle();
        _ = GetWindowRect(hwnd, out var rect);
        var scale = WindowsDrag.ScaleFor(hwnd);
        var size = (int)(200 * scale);
        var targetX = rect.Right + 40 + size <= GetSystemMetrics(0) ? rect.Right + 40 : Math.Max(0, rect.Left - 40 - size);
        DropTarget.Start(targetX, rect.Top, size, size);
        var target = (X: targetX + size / 2, Y: rect.Top + size / 2);
        var source = (X: (rect.Left + rect.Right) / 2, Y: (rect.Top + rect.Bottom) / 2);

        StartDragItem? next = null;
        Exception? dragError = null;
        var inDrag = false;
        using var entered = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        Action<IpcMainEvent> onDrag = e =>
        {
            if (Interlocked.Exchange(ref next, null) is not { } item) return;
            entered.Set();
            inDrag = true;
            try { e.Sender.StartDrag(item); }
            catch (Exception ex) { dragError = ex; }
            inDrag = false;
            returned.Set();
        };
        IpcMain.On("riftway-drag-test", onDrag);
        try
        {
            // Electron's StartDrag: files first, then the icon (missing throws, unloadable throws, empty returns),
            // and only then no files throws.
            static string? Message(Exception? e) => e?.Message;
            check("startDrag without icon throws Electron's \"'icon' parameter is required\"",
                Message(Catch(() => w.WebContents.StartDrag(new StartDragItem { File = first }))) == "'icon' parameter is required",
                Message(Catch(() => w.WebContents.StartDrag(new StartDragItem { File = first }))));
            check("startDrag with neither files nor icon reports the icon first",
                Message(Catch(() => w.WebContents.StartDrag(new StartDragItem()))) == "'icon' parameter is required", null);
            var badPath = Path.Combine(dir, "missing.png");
            check("startDrag with an icon path that does not load throws Electron's message",
                Message(Catch(() => w.WebContents.StartDrag(new StartDragItem { File = first, IconPath = badPath }))) == $"Failed to load image from path '{badPath}'",
                Message(Catch(() => w.WebContents.StartDrag(new StartDragItem { File = first, IconPath = badPath }))));
            check("startDrag without file or files throws Electron's \"Must specify either 'file' or 'files' option\"",
                Message(Catch(() => w.WebContents.StartDrag(new StartDragItem { Icon = icon }))) == "Must specify either 'file' or 'files' option", null);
            check("startDrag with empty files ignores file and throws, as Electron's",
                Message(Catch(() => w.WebContents.StartDrag(new StartDragItem { Files = [], File = first, Icon = icon }))) == "Must specify either 'file' or 'files' option", null);
            check("startDrag with an empty icon and no files returns without throwing, as Electron's",
                Catch(() => w.WebContents.StartDrag(new StartDragItem { Icon = NativeImage.CreateEmpty() })) is null, null);

            // The real flow: press on the page, its mousedown asks for the drag, the mouse carries it to the target.
            bool? cancelled = null;
            async Task<bool> DragWithMouse(StartDragItem item, bool escape = false)
            {
                DropTarget.Reset();
                entered.Reset();
                returned.Reset();
                dragError = null;
                Volatile.Write(ref next, item);
                _ = SetCursorPos(source.X, source.Y);
                await Task.Delay(300);
                mouse_event(0x0002, 0, 0, 0, 0); // LEFTDOWN
                var started = await Task.Run(() => entered.Wait(Step));
                await Task.Delay(300);
                for (var i = 1; i <= 10; i++)
                {
                    _ = SetCursorPos(source.X + (target.X - source.X) * i / 10, source.Y + (target.Y - source.Y) * i / 10);
                    await Task.Delay(50);
                }
                await Task.Delay(300);
                s_uiTaskRanInDrag = null;
                if (started) Riftway.Cef.CefThread.PostUi(() => s_uiTaskRanInDrag ??= inDrag);
                await Task.Delay(300);
                if (escape)
                {
                    keybd_event(0x1B, 0, 0, 0);
                    keybd_event(0x1B, 0, 2, 0); // KEYUP
                    cancelled = await Task.Run(() => returned.Wait(Step));
                }
                mouse_event(0x0004, 0, 0, 0, 0); // LEFTUP
                var ended = await Task.Run(() => returned.Wait(Step * 2));
                if (started) EndOrKill(ended);
                Volatile.Write(ref next, null);
                return started && ended;
            }

            var mouseDrag = await DragWithMouse(new StartDragItem { Files = [first, second], File = second, Icon = icon });
            check("startDrag from the page's mousedown drags until the button is released, then returns", mouseDrag && dragError is null,
                $"ended={mouseDrag} error={dragError?.Message}");
            check("the drop reads files in order from CF_HDROP", DropTarget.Drops == 1 && DropTarget.Files.SequenceEqual([first, second]),
                $"drops={DropTarget.Drops} files={string.Join("|", DropTarget.Files)}");
            check("CF_HDROP is a DROPFILES with wide names right after its 20-byte header", DropTarget.Header == (20u, 1u), DropTarget.Header);
            check("the drag allows copy and link, as Electron's DRAG_COPY | DRAG_LINK", DropTarget.AllowedEffects == 5u, DropTarget.AllowedEffects);
            var formats = string.Join(",", DropTarget.Formats);
            check("the data object offers CF_HDROP and no shell ID list or file descriptor, as Chromium's SetFilenames",
                DropTarget.Formats.Contains("15") && !DropTarget.Formats.Any(f => f is "Shell IDList Array" or "FileGroupDescriptorW" or "FileNameW"), formats);
            var image = DropTarget.Image;
            check("the drag image is the icon and title button: 150 DIPs wide, icon height plus insets, cursor at its DIP centre",
                image is { } im && im.Cx == (int)Math.Ceiling(150 * scale) && im.Cy == (int)Math.Ceiling(42 * scale) && im.Ox == 75 && im.Oy == 21,
                image is null ? $"none; formats={formats}" : $"{image} scale={scale}");
            check("UI tasks wait while the drag runs (drift C40)", s_uiTaskRanInDrag == false, s_uiTaskRanInDrag);

            var emptyDrag = await DragWithMouse(new StartDragItem { Files = [first], Icon = NativeImage.CreateEmpty() });
            check("startDrag with an empty icon starts no drag and returns at once", emptyDrag && dragError is null && DropTarget.Enters == 0,
                $"ended={emptyDrag} error={dragError?.Message} enters={DropTarget.Enters}");

            var escaped = await DragWithMouse(new StartDragItem { File = first, Icon = icon }, escape: true);
            check("Escape ends the drag without a drop, as Chromium's DragSourceWin cancels", escaped && cancelled == true && DropTarget.Drops == 0 && dragError is null,
                $"ended={escaped} beforeRelease={cancelled} drops={DropTarget.Drops} error={dragError?.Message}");

            var single = await DragWithMouse(new StartDragItem { File = second, IconPath = iconPath });
            check("startDrag with file and an icon path drags that one file", single && dragError is null && DropTarget.Files.SequenceEqual([second]),
                $"ended={single} error={dragError?.Message} files={string.Join("|", DropTarget.Files)}");
        }
        finally
        {
            IpcMain.RemoveListener("riftway-drag-test", onDrag);
            mouse_event(0x0004, 0, 0, 0, 0);
            DropTarget.Stop();
            w.Destroy();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // A drag that never ends would hang the run: report it and end the process.
    private static void EndOrKill(bool ended)
    {
        if (ended) return;
        Console.WriteLine("FAIL  the drag loop did not end; killing the run");
        Console.Out.Flush();
        Process.GetCurrentProcess().Kill();
    }

    private static Exception? Catch(Action act)
    {
        try { act(); return null; }
        catch (Exception e) { return e; }
    }

    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}

/// <summary>A window on its own OLE thread, registered with RegisterDragDrop, whose IDropTarget records what arrives.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static unsafe class DropTarget
{
    internal static volatile int Enters;
    internal static volatile int Drops;
    internal static uint AllowedEffects;
    internal static List<string> Files = [];
    internal static List<string> Formats = [];
    internal static (int Cx, int Cy, int Ox, int Oy)? Image;
    internal static (uint PFiles, uint Wide)? Header;

    private static Thread? s_thread;
    private static uint s_threadId;
    private static void* s_vtbl;
    private static void* s_object;

    internal static void Reset()
    {
        Enters = Drops = 0;
        AllowedEffects = 0;
        Files = [];
        Formats = [];
        Image = null;
        Header = null;
    }

    internal static void Start(int x, int y, int width, int height)
    {
        using var ready = new ManualResetEventSlim();
        s_thread = new Thread(() =>
        {
            _ = OleInitialize(null);
            s_threadId = GetCurrentThreadId();
            var vtbl = (void**)NativeMemory.Alloc((nuint)(7 * sizeof(void*)));
            vtbl[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
            vtbl[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
            vtbl[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
            vtbl[3] = (delegate* unmanaged[Stdcall]<void*, void*, uint, long, uint*, int>)&DragEnter;
            vtbl[4] = (delegate* unmanaged[Stdcall]<void*, uint, long, uint*, int>)&DragOver;
            vtbl[5] = (delegate* unmanaged[Stdcall]<void*, int>)&DragLeave;
            vtbl[6] = (delegate* unmanaged[Stdcall]<void*, void*, uint, long, uint*, int>)&Drop;
            s_vtbl = vtbl;
            s_object = NativeMemory.Alloc((nuint)sizeof(void*));
            *(void**)s_object = vtbl;
            nint hwnd;
            fixed (char* cls = "STATIC") fixed (char* title = "Riftway drop target")
                hwnd = CreateWindowExW(0x00000008 | 0x00000080 /* TOPMOST | TOOLWINDOW */, cls, title, 0x90800000 /* POPUP | VISIBLE | BORDER */,
                    x, y, width, height, 0, 0, 0, 0);
            var registered = RegisterDragDrop(hwnd, s_object);
            if (registered < 0) Console.Error.WriteLine($"WindowTest: RegisterDragDrop failed 0x{registered:X8}");
            ready.Set();
            Msg msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0) DispatchMessageW(&msg);
            _ = RevokeDragDrop(hwnd);
            _ = DestroyWindow(hwnd);
            OleUninitialize();
        }) { IsBackground = true, Name = "drop target" };
        s_thread.SetApartmentState(ApartmentState.STA);
        s_thread.Start();
        ready.Wait();
    }

    internal static void Stop()
    {
        if (s_thread is null) return;
        _ = PostThreadMessageW(s_threadId, 0x0012, 0, 0);
        s_thread.Join(TimeSpan.FromSeconds(5));
        s_thread = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (*iid == new Guid("00000000-0000-0000-C000-000000000046") || *iid == new Guid("00000122-0000-0000-C000-000000000046"))
        {
            *result = self;
            return 0;
        }
        *result = null;
        return unchecked((int)0x80004002);
    }

    // Static and never freed while the thread runs, so counting is unnecessary.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragEnter(void* self, void* data, uint keyState, long point, uint* effect)
    {
        Enters++;
        AllowedEffects = *effect;
        Formats = ReadFormats(data);
        Image = ReadImage(data);
        *effect &= 1; // DROPEFFECT_COPY
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragOver(void* self, uint keyState, long point, uint* effect)
    {
        *effect &= 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragLeave(void* self) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Drop(void* self, void* data, uint keyState, long point, uint* effect)
    {
        var format = new FormatEtc { Format = 15, Aspect = 1, Index = -1, Tymed = 1 };
        StgMedium medium;
        var files = new List<string>();
        if (Call<FormatEtc, StgMedium>(data, 3, &format, &medium) >= 0)
        {
            var header = (uint*)GlobalLock(medium.Handle);
            Header = (header[0], header[4]);
            _ = GlobalUnlock(medium.Handle);
            var count = DragQueryFileW(medium.Handle, 0xFFFFFFFF, null, 0);
            for (var i = 0u; i < count; i++)
            {
                var length = DragQueryFileW(medium.Handle, i, null, 0);
                var buffer = new char[length + 1];
                fixed (char* b = buffer) _ = DragQueryFileW(medium.Handle, i, b, length + 1);
                files.Add(new string(buffer, 0, (int)length));
            }
            ReleaseStgMedium(&medium);
        }
        Files = files;
        Drops++;
        *effect &= 1;
        return 0;
    }

    private static List<string> ReadFormats(void* data)
    {
        var names = new List<string>();
        void* enumerator;
        if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)(*(void***)data)[8])(data, 1, &enumerator) < 0) return names;
        FormatEtc format;
        uint fetched;
        var name = stackalloc char[256];
        while (((delegate* unmanaged[Stdcall]<void*, uint, FormatEtc*, uint*, int>)(*(void***)enumerator)[3])(enumerator, 1, &format, &fetched) == 0 && fetched == 1)
        {
            var length = format.Format >= 0xC000 ? GetClipboardFormatNameW(format.Format, name, 256) : 0;
            names.Add(length > 0 ? new string(name, 0, length) : format.Format.ToString());
        }
        ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)enumerator)[2])(enumerator);
        return names;
    }

    // The shell's private "DragImageBits" format starts with the SHDRAGIMAGE size and cursor offset.
    private static (int, int, int, int)? ReadImage(void* data)
    {
        ushort id;
        fixed (char* name = "DragImageBits") id = (ushort)RegisterClipboardFormatW(name);
        var format = new FormatEtc { Format = id, Aspect = 1, Index = -1, Tymed = 1 };
        StgMedium medium;
        if (Call<FormatEtc, StgMedium>(data, 3, &format, &medium) < 0) return null;
        var bits = (int*)GlobalLock(medium.Handle);
        var result = (bits[0], bits[1], bits[2], bits[3]);
        _ = GlobalUnlock(medium.Handle);
        ReleaseStgMedium(&medium);
        return result;
    }

    private static int Call<TA, TB>(void* com, int slot, TA* a, TB* b) where TA : unmanaged where TB : unmanaged
        => ((delegate* unmanaged[Stdcall]<void*, TA*, TB*, int>)(*(void***)com)[slot])(com, a, b);

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
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint hwnd, void* target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint hwnd);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(StgMedium* medium);
    [DllImport("shell32.dll")] private static extern uint DragQueryFileW(nint drop, uint index, char* file, uint length);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern void* GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern int GlobalUnlock(nint memory);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern int DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(Msg* msg);
    [DllImport("user32.dll")] private static extern int PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint RegisterClipboardFormatW(char* name);
    [DllImport("user32.dll")] private static extern int GetClipboardFormatNameW(uint format, char* name, int length);
}
