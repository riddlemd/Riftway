using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What <see cref="WebContents.StartDrag"/> drags; Electron's <c>startDrag</c> item.</summary>
public sealed record StartDragItem
{
    /// <summary>The path of the file to drag. Electron's <c>file</c>.</summary>
    public string? File { get; init; }

    /// <summary>The paths of the files to drag; when set, even empty, <see cref="File"/> is ignored. Electron's
    /// <c>files</c>.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>The image shown under the cursor while dragging. Electron's <c>icon</c> as a <c>NativeImage</c>.</summary>
    public NativeImage? Icon { get; init; }

    /// <summary>A path to load the icon from, used when <see cref="Icon"/> is null. Electron's <c>icon</c> as a
    /// string.</summary>
    public string? IconPath { get; init; }
}

public sealed unsafe partial class WebContents
{
    /// <summary>Drags files out of the page: call it from the page's <c>mousedown</c> (through IPC) while the button
    /// is still down. Returns once the user drops or cancels; an empty icon starts no drag. Electron's
    /// <c>webContents.startDrag(item)</c>.</summary>
    /// <remarks>Windows only. While the drag runs, the UI thread answers window messages but runs no queued UI tasks
    /// (drift C40).</remarks>
    /// <exception cref="ArgumentException">No icon (<c>'icon' parameter is required</c>), an icon path that does not
    /// load, or no files (<c>Must specify either 'file' or 'files' option</c>), with Electron's messages.</exception>
    /// <exception cref="PlatformNotSupportedException">On macOS and Linux (drift C40).</exception>
    public void StartDrag(StartDragItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ResolveDragItem(item, NativeImage.CreateFromPath) is not var (files, icon)) return;
        if (OperatingSystem.IsMacOS())
        {
            DragOnMac(files, icon);
            return;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("startDrag is supported on Windows and macOS only.");
        DragOnWindows(files, icon);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void DragOnMac(IReadOnlyList<string> files, NativeImage icon)
    {
        var representations = icon.GetScaleFactors().Select(scale => (icon.ToPng(scale), scale)).ToList();
        CefThread.InvokeOnUi(() =>
        {
            if (_browser is null) return 0;
            var host = _browser->get_host(_browser);
            nint view;
            try { view = (nint)host->get_window_handle(host); }
            finally { NativeObject.Release(host); }
            var image = MacImages.FromRepresentations(representations, icon.IsTemplateImage());
            try { MacDrag.DragFiles(view, files, image); }
            finally { MacImages.Release(image); }
            return 0;
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void DragOnWindows(IReadOnlyList<string> files, NativeImage icon)
    {
        CefThread.InvokeOnUi(() =>
        {
            // Electron's DragFileItems returns when the view has no root window.
            if (_browser is null) return 0u;
            var host = _browser->get_host(_browser);
            nint hwnd;
            try { hwnd = (nint)host->get_window_handle(host); }
            finally { NativeObject.Release(host); }
            return WindowsDrag.Run(files, DragImage(icon, files[0], hwnd));
        });
    }

    /// <summary>Electron's <c>StartDrag</c> checks, in its order: the files (<c>files</c> wins over <c>file</c>), then a
    /// missing icon throws, an icon that will not load throws, an empty icon ends silently (null), and only then do no
    /// files throw.</summary>
    internal static (IReadOnlyList<string> Files, NativeImage Icon)? ResolveDragItem(StartDragItem item, Func<string, NativeImage> load)
    {
        IReadOnlyList<string> files = item.Files ?? (item.File is { } file ? [file] : []);
        NativeImage icon;
        if (item.Icon is { } given) icon = given;
        else if (item.IconPath is { } path)
        {
            icon = load(path);
            if (icon.IsEmpty()) throw new ArgumentException($"Failed to load image from path '{path}'");
        }
        else throw new ArgumentException("'icon' parameter is required");
        if (icon.IsEmpty()) return null;
        if (files.Count == 0) throw new ArgumentException("Must specify either 'file' or 'files' option");
        return (files, icon);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WindowsDragImage DragImage(NativeImage icon, string title, nint hwnd)
    {
        var scale = WindowsDrag.ScaleFor(hwnd);
        var (dipWidth, dipHeight) = icon.GetSize(scale);
        int width = (int)Math.Round(dipWidth * scale), height = (int)Math.Round(dipHeight * scale);
        var bitmap = icon.BitmapAt(scale);
        if (bitmap.Width != width || bitmap.Height != height) bitmap = ImagePixels.Resize(bitmap, width, height);
        return new WindowsDragImage(bitmap.Width, bitmap.Height, bitmap.Bgra, title, scale);
    }

}
