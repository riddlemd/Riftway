using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

public sealed partial class NativeImage
{
    // gfx's supported resource scales on macOS, each of which ImageSkiaFromNSImage draws.
    private static readonly double[] s_macScales = [1, 2];

    /// <summary>A thumbnail of the file at <paramref name="path"/>: Quick Look's preview on macOS, the shell's thumbnail
    /// cache on Windows (at <paramref name="size"/>'s width on the longer side). Electron's
    /// <c>nativeImage.createThumbnailFromPath()</c>.</summary>
    /// <remarks>On macOS the image is <paramref name="size"/> in DIPs, with 1x and 2x representations; the task fails with
    /// Electron's messages, as <see cref="InvalidOperationException"/>, for an empty size, a missing file or a Quick Look
    /// error.</remarks>
    /// <exception cref="PlatformNotSupportedException">On Linux, where Electron has no such method.</exception>
    public static Task<NativeImage> CreateThumbnailFromPathAsync(string path, (int Width, int Height) size)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsMacOS()) return MacThumbnailAsync(path, size);
        if (OperatingSystem.IsWindows()) return WindowsThumbnailAsync(path, size);
        throw new PlatformNotSupportedException(NativeImageRules.ThumbnailUnsupported);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static Task<NativeImage> MacThumbnailAsync(string path, (int Width, int Height) size)
    {
        if (NativeImageRules.IsEmptySize(size)) return Rejected(NativeImageRules.EmptySize);
        // Quick Look answers with a stock icon for a missing file, so Electron checks first.
        if (path.Length == 0 || !MacThumbnail.Exists(Path.GetFullPath(path))) return Rejected(NativeImageRules.MacNoPreview);
        var scale = OrielApp.CurrentOrNull is { IsReady: true } ? CefThread.InvokeOnUi(MacThumbnail.FirstScreenScale) : MacThumbnail.FirstScreenScale();
        var done = new TaskCompletionSource<NativeImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        MacThumbnail.Generate(Path.GetFullPath(path), size.Width, size.Height, scale, s_macScales, (bitmaps, error) =>
        {
            if (bitmaps is null)
            {
                done.SetException(new InvalidOperationException(NativeImageRules.MacFailure(error)));
                return;
            }
            var image = new NativeImage();
            foreach (var b in bitmaps) image.Add((float)b.Scale, new ImageBitmap(b.Width, b.Height, b.Bgra));
            done.SetResult(image);
        });
        return done.Task;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<NativeImage> WindowsThumbnailAsync(string path, (int Width, int Height) size)
    {
        if (NativeImageRules.IsEmptySize(size)) throw new InvalidOperationException(NativeImageRules.EmptySize);
        var (bitmap, failure) = await WindowsSta.Run(() => WindowsThumbnail.Create(path, size.Width)).ConfigureAwait(false);
        if (failure is { } step) throw new InvalidOperationException(NativeImageRules.WindowsFailure(step));
        var image = new NativeImage();
        if (bitmap is not null) image.Add(1f, new ImageBitmap(bitmap.Width, bitmap.Height, bitmap.Bgra));
        return image;
    }

    private static Task<NativeImage> Rejected(string message) => Task.FromException<NativeImage>(new InvalidOperationException(message));
}

/// <summary>The checks and messages of Electron's <c>NativeImage</c> that are not pixels.</summary>
internal static class NativeImageRules
{
    internal const string EmptySize = "size must not be empty";
    internal const string MacNoPreview = "unable to retrieve thumbnail preview image for the given path";
    internal const string ThumbnailUnsupported = "nativeImage.createThumbnailFromPath is not available on Linux, as in Electron.";

    /// <summary><c>gfx::Size::IsEmpty</c> after its constructor clamps negative sides to 0.</summary>
    internal static bool IsEmptySize((int Width, int Height) size) => Math.Max(size.Width, 0) == 0 || Math.Max(size.Height, 0) == 0;

    /// <summary><c>ReceivedThumbnailResult</c>'s rejection; a missing description reads as empty, as
    /// <c>SysNSStringToUTF8(nil)</c>.</summary>
    internal static string MacFailure(string? localizedDescription) => MacNoPreview + ": " + (localizedDescription ?? "");

    /// <summary>The Windows rejection for each failing step.</summary>
    internal static string WindowsFailure(WindowsThumbnailFailure step) => step switch
    {
        WindowsThumbnailFailure.CreateItem => "Failed to create IShellItem from the given path",
        WindowsThumbnailFailure.ThumbnailCache => "Failed to acquire local thumbnail cache reference",
        WindowsThumbnailFailure.GetThumbnail => "Failed to get thumbnail from local thumbnail cache reference",
        WindowsThumbnailFailure.SharedBitmap => "Failed to extract bitmap from thumbnail",
        _ => "Could not convert HBITMAP to BITMAP",
    };

    /// <summary>Electron's <c>IsTemplateFilename</c>: <c>base::MatchPattern</c> of the whole path against
    /// <c>*Template.*</c> or <c>*Template@*x.*</c>, case-sensitive.</summary>
    internal static bool IsTemplateFilename(string path)
    {
        if (path.Contains("Template.", StringComparison.Ordinal)) return true;
        var at = path.IndexOf("Template@", StringComparison.Ordinal);
        return at >= 0 && path.IndexOf("x.", at + "Template@".Length, StringComparison.Ordinal) >= 0;
    }
}
