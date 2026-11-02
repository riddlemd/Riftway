using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The step of Electron's Windows <c>CreateThumbnailFromPath</c> that failed; each has its own rejection.</summary>
public enum WindowsThumbnailFailure
{
    /// <summary><c>SHCreateItemFromParsingName</c>.</summary>
    CreateItem,

    /// <summary><c>CoCreateInstance(CLSID_LocalThumbnailCache)</c>.</summary>
    ThumbnailCache,

    /// <summary><c>IThumbnailCache::GetThumbnail</c>.</summary>
    GetThumbnail,

    /// <summary><c>ISharedBitmap::GetSharedBitmap</c>.</summary>
    SharedBitmap,

    /// <summary><c>GetObject</c> on the <c>HBITMAP</c>.</summary>
    BitmapInfo,
}

/// <summary>Shell thumbnails as Electron's <c>NativeImage::CreateThumbnailFromPath</c> makes them on Windows
/// (shell/common/api/electron_api_native_image_win.cc): the local thumbnail cache, then <c>IconUtil</c>'s conversion.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsThumbnail
{
    // WTS_SCALETOREQUESTEDSIZE | WTS_SCALEUP.
    private const uint Flags = 0x40 | 0x10000;
    private static readonly Guid s_localThumbnailCache = new("50ef4544-ac9f-4a8e-b21b-8a26180db13f");
    private static readonly Guid s_thumbnailCache = new("f676c15d-596a-4ce2-8234-33996f445db1");
    private static readonly Guid s_shellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>The thumbnail of <paramref name="path"/> at <paramref name="size"/> pixels on its longer side, as one 1x
    /// bitmap, or the step that failed. Call on an STA thread, as Electron's <c>ScopedCOMInitializer</c> makes one.
    /// A null bitmap with no failure is <c>IconUtil</c> returning an empty bitmap, which Electron resolves as empty.</summary>
    public static (ThumbnailBitmap? Bitmap, WindowsThumbnailFailure? Failure) Create(string path, int size)
    {
        nint item = 0, cache = 0, shared = 0;
        try
        {
            var iidItem = s_shellItem;
            int hr;
            fixed (char* p = path) hr = SHCreateItemFromParsingName(p, 0, &iidItem, &item);
            if (hr < 0) return (null, WindowsThumbnailFailure.CreateItem);
            var clsid = s_localThumbnailCache;
            var iidCache = s_thumbnailCache;
            // CLSCTX_INPROC: CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER.
            if (CoCreateInstance(&clsid, 0, 0x1 | 0x2, &iidCache, &cache) < 0) return (null, WindowsThumbnailFailure.ThumbnailCache);
            // IThumbnailCache::GetThumbnail is slot 3.
            var getThumbnail = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint*, void*, void*, int>)(*(void***)cache)[3];
            if (getThumbnail(cache, item, (uint)size, Flags, &shared, null, null) < 0) return (null, WindowsThumbnailFailure.GetThumbnail);
            // ISharedBitmap::GetSharedBitmap is slot 3; the bitmap stays the shared bitmap's.
            var getSharedBitmap = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(void***)shared)[3];
            nint bitmap;
            if (getSharedBitmap(shared, &bitmap) < 0) return (null, WindowsThumbnailFailure.SharedBitmap);
            BitmapInfo info;
            if (GetObjectW(bitmap, sizeof(BitmapInfo), &info) == 0) return (null, WindowsThumbnailFailure.BitmapInfo);
            var iconInfo = new IconInfo { IsIcon = 1, Mask = bitmap, Color = bitmap };
            var icon = CreateIconIndirect(&iconInfo);
            try
            {
                // IconUtil::CreateSkBitmapFromHICON sizes the bitmap by the icon's mask.
                if (icon == 0 || WindowsIcon.FromIcon(icon) is not { } pixels) return (null, null);
                return (new ThumbnailBitmap(1, pixels.Width, pixels.Height, pixels.Bgra), null);
            }
            finally { if (icon != 0) _ = DestroyIcon(icon); }
        }
        finally
        {
            Release(shared);
            Release(cache);
            Release(item);
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)unknown)[2])(unknown);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [DllImport("shell32.dll")] private static extern int SHCreateItemFromParsingName(char* path, nint bindContext, Guid* iid, nint* item);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, void* buffer);
    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(IconInfo* info);
    [DllImport("user32.dll")] private static extern int DestroyIcon(nint icon);
}
